using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SiteLift.Core;

public enum SyncErrorCode
{
    None,
    WindowsGitMissing,
    Config,
    WindowsRepo,
    RepositoryBusy,
    WslDirectory,
    SourceMissing,
    WslIndex,
    Build,
    PreviewExpired,
    CleanupConfirmation,
    Push
}

public sealed class SyncException : InvalidOperationException
{
    public SyncErrorCode Code { get; }

    public SyncException(SyncErrorCode code, string message) : base(message)
    {
        Code = code;
    }
}

public enum RepositoryInitMode { Empty, Existing, Reset }

public sealed record RepositoryInitResult(string? BackupPath, string[] Changes, bool StatusUnavailable);

public sealed record SyncChangeItem(string Status, string Path);

public sealed record SyncPreviewResult(
    string PlanToken,
    string SnapshotCommit,
    SyncChangeItem[] Changes,
    string[] CleanupCandidates,
    string[] UntrackedDeletions);

public sealed record SyncApplyResult(string BaseCommit, string SnapshotCommit);

public sealed record SyncPublishResult(bool Pushed, string? CommitHash);

public static class SyncEngine
{
    private static readonly Regex RemoteUrlRegex = new(
        @"^(?:(?:https|ssh)://[^\s/]+/[^\s]+|(?:[^\s@/:]+@)?[^\s@/:]+:(?!:)[^\s]+)$",
        RegexOptions.Compiled);

    private static readonly Regex GithubUserRegex = new(
        @"^[a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommitHashRegex = new(
        @"\A[0-9a-f]{40}\z",
        RegexOptions.Compiled);

    private static readonly string[] InProgressGitStates =
        { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" };

    public static async Task<string> CheckWindowsGitAsync(CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        var result = await RunGitAsync(git, null, new[] { "--version" }, null, cancellation: cancellation);
        if (result.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsGitMissing, "无法运行 Windows Git，请重新安装 Git for Windows。");
        return result.Stdout.Trim();
    }

    public static async Task<string[]> ListRemotesAsync(ProjectConfig config, CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        string repo = await ResolveValidatedWindowsRepoAsync(git, config.WindowsRepo, testConnection: false, cancellation);
        var result = await RunGitAsync(git, repo, new[] { "remote" }, null, cancellation: cancellation);
        if (result.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, "无法读取本地 Git 远端名称。");
        return SplitLines(result.Stdout);
    }

    public static async Task<string[]> TestConnectionAsync(
        ProjectConfig config, Action<string>? log = null, CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        string? repo = string.IsNullOrWhiteSpace(config.RemoteUrl)
            ? await ResolveValidatedWindowsRepoAsync(git, config.WindowsRepo, testConnection: true, cancellation)
            : null;
        var remote = await PrepareRemoteAsync(git, config, cancellation);
        var args = new List<string>(remote.GitOptions) { "ls-remote", "--heads", "--", remote.Target };
        var result = await RunGitAsync(git, repo ?? Path.GetTempPath(), args, remote.Environment,
            onStderr: log, cancellation: cancellation);
        if (result.ExitCode != 0)
        {
            string detail = result.Stderr.Trim();
            throw new SyncException(SyncErrorCode.Config,
                detail.Length > 0 ? detail : "无法读取远端分支，请检查远端地址和仓库访问权限。");
        }

        var branches = new List<string>();
        foreach (string line in SplitLines(result.Stdout))
        {
            var match = Regex.Match(line, @"^[0-9a-f]+\trefs/heads/(.+)$");
            if (match.Success) branches.Add(match.Groups[1].Value);
        }
        return branches.ToArray();
    }

    public static async Task<string> ReadRemoteUrlAsync(
        string repository, string remoteName, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        string safeRemote = SyncPath.ValidateRemoteName(remoteName);
        var result = await RunGitAsync(git, repository, new[] { "remote", "get-url", "--", safeRemote },
            null, timeout: timeout ?? TimeSpan.FromSeconds(10), cancellation: cancellation);
        return !result.TimedOut && result.ExitCode == 0 ? result.Stdout.Trim() : "";
    }

    public static async Task<string[]> ReadWorkingTreeStatusAsync(
        string repository, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        var args = new[]
        {
            "--no-optional-locks", "-c", "core.quotepath=false",
            "status", "--short", "--untracked-files=all", "--no-renames"
        };
        var result = await RunGitAsync(git, repository, args, null,
            timeout: timeout ?? TimeSpan.FromSeconds(30), cancellation: cancellation);
        if (result.TimedOut)
            throw new SyncException(SyncErrorCode.WindowsRepo, "读取本地工作区状态超时。");
        if (result.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo,
                string.IsNullOrWhiteSpace(result.Stderr) ? "无法读取本地工作区状态。" : result.Stderr.Trim());
        return SplitLines(result.Stdout);
    }

    public static async Task<RepositoryInitResult> InitializeRepositoryAsync(
        ProjectConfig config,
        RepositoryInitMode mode,
        Action<string>? log = null,
        CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        if (string.IsNullOrWhiteSpace(config.RemoteUrl) ||
            string.IsNullOrWhiteSpace(config.Branch) ||
            !Directory.Exists(config.WindowsRepo))
        {
            throw new SyncException(SyncErrorCode.Config, "请选择现有的 Windows 目录，并填写远端仓库地址与目标分支。");
        }

        var directory = new DirectoryInfo(config.WindowsRepo);
        string repo = directory.FullName;
        if (!Regex.IsMatch(repo, @"^[A-Za-z]:\\") || directory.Parent is null ||
            directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SyncException(SyncErrorCode.Config, "请选择 Windows 磁盘上的项目目录。");
        }

        string branch = ValidateBranchOrThrow(config.Branch);
        await EnsureValidGitRefFormatAsync(git, branch, cancellation);
        string remoteName = string.IsNullOrWhiteSpace(config.Remote)
            ? "origin"
            : ValidateRemoteNameOrThrow(config.Remote);
        var remote = await PrepareRemoteAsync(git, config, cancellation);

        // Lock externally so Windows never blocks Directory.Move(".git", backup) during reset.
        using var repoLock = AcquireRepositoryLock(repo);

        var revParse = await RunGitAsync(git, repo, new[] { "rev-parse", "--git-dir" }, null, cancellation: cancellation);
        bool existingRepo = revParse.ExitCode == 0;
        string metadata = Path.Combine(repo, ".git");
        bool gitExists = Directory.Exists(metadata) || File.Exists(metadata);

        if (mode == RepositoryInitMode.Reset)
        {
            if (!Directory.Exists(metadata))
                throw new SyncException(SyncErrorCode.WindowsRepo, "重置仅支持含普通 .git 目录的独立仓库。请检查工作树、子模块或 Git 元数据链接。");
            var gitDirInfo = new DirectoryInfo(metadata);
            if (gitDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                Directory.Exists(Path.Combine(metadata, "worktrees")) ||
                Directory.Exists(Path.Combine(metadata, "modules")) ||
                File.Exists(Path.Combine(metadata, "commondir")))
            {
                throw new SyncException(SyncErrorCode.WindowsRepo, "重置仅支持含普通 .git 目录的独立仓库。请检查工作树、子模块或 Git 元数据链接。");
            }
        }
        else if (existingRepo || gitExists)
        {
            throw new SyncException(SyncErrorCode.WindowsRepo, "此目录已有 Git 元数据，或位于其他 Git 仓库内。请选择仓库根目录或修复现有仓库。");
        }

        bool hasFiles = mode == RepositoryInitMode.Reset ||
            Directory.EnumerateFileSystemEntries(repo).Any();
        if (mode != RepositoryInitMode.Reset && hasFiles != (mode == RepositoryInitMode.Existing))
            throw new SyncException(SyncErrorCode.WindowsRepo, "目录内容已变化，请重新确认初始化方式。");

        var lsRemoteArgs = new List<string>(remote.GitOptions)
            { "ls-remote", "--exit-code", "--heads", "--", remote.Target, $"refs/heads/{branch}" };
        var lsRemote = await RunGitAsync(git, Path.GetTempPath(), lsRemoteArgs, remote.Environment,
            onStdout: log, onStderr: log, cancellation: cancellation);
        if (lsRemote.ExitCode != 0)
            throw new SyncException(SyncErrorCode.Config, "无法读取指定远端分支。请检查地址、分支和访问权限。");

        string destination = repo;
        DirectoryInfo? staging = null;
        string? backup = null;
        try
        {
            var cloneArgs = new List<string>(remote.GitOptions)
                { "clone", "--branch", branch, "--origin", remoteName };
            if (hasFiles)
            {
                string stagingPath = Path.Combine(directory.Parent.FullName, ".sitelift-init-" + Guid.NewGuid().ToString("N"));
                staging = Directory.CreateDirectory(stagingPath);
                destination = staging.FullName;
                cloneArgs.Add("--no-checkout");
            }
            cloneArgs.Add("--");
            cloneArgs.Add(remote.Target);
            cloneArgs.Add(destination);

            var clone = await RunGitAsync(git, Path.GetTempPath(), cloneArgs, remote.Environment,
                onStdout: log, onStderr: log, cancellation: cancellation);
            if (clone.ExitCode != 0)
                throw new SyncException(SyncErrorCode.Config, "无法克隆远端分支。请检查远端地址、分支和访问权限。");

            if (!string.Equals(remote.Target, remote.CleanUrl, StringComparison.Ordinal) && remote.CleanUrl is not null)
            {
                var setUrl = await RunGitAsync(git, destination,
                    new[] { "remote", "set-url", "--", remoteName, remote.CleanUrl },
                    null, onStdout: log, onStderr: log, cancellation: cancellation);
                if (setUrl.ExitCode != 0)
                    throw new SyncException(SyncErrorCode.WindowsRepo, "无法保存远端地址。");
            }

            if (hasFiles)
            {
                var readTree = await RunGitAsync(git, destination, new[] { "read-tree", "HEAD" },
                    null, onStdout: log, onStderr: log, cancellation: cancellation);
                if (readTree.ExitCode != 0)
                    throw new SyncException(SyncErrorCode.WindowsRepo, "无法建立远端分支的暂存区基线。");

                if (mode == RepositoryInitMode.Reset)
                {
                    backup = Path.Combine(directory.Parent.FullName,
                        ".sitelift-git-backup-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(metadata, backup);
                }

                try
                {
                    Directory.Move(Path.Combine(destination, ".git"), metadata);
                }
                catch
                {
                    if (backup is not null)
                    {
                        try { Directory.Move(backup, metadata); }
                        catch
                        {
                            throw new SyncException(SyncErrorCode.WindowsRepo,
                                $"重置失败，无法自动恢复。旧 Git 数据保存在：{backup}");
                        }
                    }
                    throw;
                }
            }
        }
        finally
        {
            if (staging is not null && staging.Parent?.FullName == directory.Parent.FullName)
            {
                TryDeleteDirectoryForce(staging.FullName);
            }
        }

        if (!hasFiles)
            return new RepositoryInitResult(backup, Array.Empty<string>(), StatusUnavailable: false);

        var status = await RunGitAsync(git, repo,
            new[] { "--no-optional-locks", "-c", "core.quotepath=false", "status", "--short", "--untracked-files=normal" },
            null, cancellation: cancellation);
        return new RepositoryInitResult(
            backup,
            status.ExitCode == 0 ? SplitLines(status.Stdout) : Array.Empty<string>(),
            StatusUnavailable: status.ExitCode != 0);
    }

    public static async Task<SyncPreviewResult> BuildPreviewAsync(
        ProjectConfig config,
        Action<string>? log = null,
        CancellationToken cancellation = default)
    {
        var result = await RunBuildOrApplyAsync(config, preview: true, expectedPlan: null,
            allowCleanup: false, log, cancellation);
        return result.Preview!;
    }

    public static async Task<SyncApplyResult> ApplySyncAsync(
        ProjectConfig config,
        string expectedPlan,
        bool allowCleanup,
        Action<string>? log = null,
        CancellationToken cancellation = default)
    {
        var result = await RunBuildOrApplyAsync(config, preview: false, expectedPlan: expectedPlan,
            allowCleanup: allowCleanup, log, cancellation);
        return result.Applied!;
    }

    public static async Task<SyncPublishResult> PublishAsync(
        ProjectConfig config,
        string expectedSyncedOrPending,
        string? commitSummary,
        Action<string>? log = null,
        Action<PendingPushRecord>? onPendingCommit = null,
        CancellationToken cancellation = default)
    {
        string git = RequireGitExecutable();
        string repo = await ResolveValidatedWindowsRepoAsync(git, config.WindowsRepo, testConnection: false, cancellation);
        string branch = ValidateBranchOrThrow(config.Branch);
        var remote = await PrepareRemoteAsync(git, config, cancellation);

        var tokenMatch = Regex.Match(expectedSyncedOrPending.Trim(),
            @"\A([0-9a-f]{40}):([0-9a-f]{40})(?::([0-9a-f]{40}))?\z");
        if (!tokenMatch.Success)
            throw new SyncException(SyncErrorCode.PreviewExpired, "请先应用已校验的同步。");

        string baseCommit = tokenMatch.Groups[1].Value;
        string verifiedSnapshot = tokenMatch.Groups[2].Value;
        string? pendingCommit = tokenMatch.Groups[3].Success ? tokenMatch.Groups[3].Value : null;

        string lockKey = await ResolveLockKeyAsync(git, repo, cancellation);
        using var repoLock = AcquireRepositoryLock(lockKey);

        var fetchArgs = new List<string>(remote.GitOptions) { "fetch", "--no-tags", "--", remote.Target, $"refs/heads/{branch}" };
        var fetch = await RunGitAsync(git, repo, fetchArgs, remote.Environment, onStdout: log, onStderr: log, cancellation: cancellation);
        if (fetch.ExitCode != 0)
            throw new SyncException(SyncErrorCode.Push, "拉取远端分支失败，请检查网络连接与仓库权限。");

        string remoteCommit = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "--verify", "FETCH_HEAD^{commit}" }, "无法解析远端分支提交。", cancellation);
        string head = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "HEAD" }, "无法读取本地分支提交。", cancellation);
        string currentBranch = await ReadRequiredGitOutputAsync(git, repo, new[] { "branch", "--show-current" }, "请先检出配置的 Windows 分支。", cancellation);
        if (!string.Equals(currentBranch, branch, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.WindowsRepo, "请先检出配置的 Windows 分支。");

        await AssertReadyIndexAsync(git, repo, allowStaged: true, cancellation);

        if (pendingCommit is not null &&
            string.Equals(head, pendingCommit, StringComparison.Ordinal) &&
            string.Equals(remoteCommit, pendingCommit, StringComparison.Ordinal))
        {
            log?.Invoke($"已推送 {pendingCommit} 到分支 {branch}");
            return new SyncPublishResult(Pushed: true, pendingCommit);
        }

        if (!string.Equals(remoteCommit, baseCommit, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.Push, "远端分支已发生变化，请先核对远端更新和本地待推送提交。");

        if (pendingCommit is not null)
        {
            string parents = await ReadRequiredGitOutputAsync(git, repo,
                new[] { "rev-list", "--parents", "-n", "1", head },
                "本地待推送提交与当前分支不一致。", cancellation);
            if (!string.Equals(head, pendingCommit, StringComparison.Ordinal) ||
                !string.Equals(parents, $"{pendingCommit} {baseCommit}", StringComparison.Ordinal))
            {
                throw new SyncException(SyncErrorCode.WindowsRepo, "本地待推送提交与当前分支不再匹配。");
            }
        }
        else
        {
            if (!string.Equals(head, baseCommit, StringComparison.Ordinal))
                throw new SyncException(SyncErrorCode.PreviewExpired, "本地分支已变化，请重新构建并应用同步。");

            var add = await RunGitAsync(git, repo, new[] { "add", "-A", "--", "." }, null, onStdout: log, onStderr: log, cancellation: cancellation);
            if (add.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "暂存 Windows 工作区改动失败，请检查 Git 索引。");

            var staged = await RunGitAsync(git, repo, new[] { "-c", "core.quotepath=false", "diff", "--cached", "--name-status" }, null, cancellation: cancellation);
            if (staged.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "无法读取已暂存的改动列表。");

            string[] changes = SplitLines(staged.Stdout);
            if (changes.Length == 0)
            {
                log?.Invoke("没有需要推送的改动。");
                return new SyncPublishResult(Pushed: false, CommitHash: null);
            }

            log?.Invoke("已暂存的 Windows 工作区改动：");
            foreach (string change in changes)
                log?.Invoke(change);

            string? summaryFile = null;
            try
            {
                List<string> commitArgs;
                if (!string.IsNullOrWhiteSpace(commitSummary))
                {
                    summaryFile = Path.Combine(Path.GetTempPath(), $"sitelift-msg-{Guid.NewGuid():N}.txt");
                    await File.WriteAllTextAsync(summaryFile, commitSummary, new UTF8Encoding(false), cancellation);
                    commitArgs = new List<string> { "-c", "i18n.commitEncoding=UTF-8", "commit", "-F", summaryFile };
                }
                else
                {
                    commitArgs = new List<string> { "commit", "-m", "Update project working tree" };
                }

                var commitResult = await RunGitAsync(git, repo, commitArgs, null, onStdout: log, onStderr: log, cancellation: cancellation);
                if (commitResult.ExitCode != 0)
                    throw new SyncException(SyncErrorCode.WindowsRepo, "Git 提交失败，请查看日志和暂存内容。");
            }
            finally
            {
                TryDeleteFile(summaryFile);
            }

            pendingCommit = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "HEAD" }, "无法读取新生成的本地提交。", cancellation);
        }

        onPendingCommit?.Invoke(new PendingPushRecord(baseCommit, verifiedSnapshot, pendingCommit, repo, branch));

        var pushArgs = new List<string>(remote.GitOptions)
            { "push", "--", remote.Target, $"{pendingCommit}:refs/heads/{branch}" };
        var push = await RunGitAsync(git, repo, pushArgs, remote.Environment, onStdout: log, onStderr: log, cancellation: cancellation);
        if (push.ExitCode != 0)
            throw new SyncException(SyncErrorCode.Push, "本地提交已生成。请查看推送错误，然后重试推送。");

        log?.Invoke($"已推送 {pendingCommit} 到分支 {branch}");
        return new SyncPublishResult(Pushed: true, pendingCommit);
    }

    private sealed record BuildOrApplyOutcome(SyncPreviewResult? Preview, SyncApplyResult? Applied);

    private static async Task<BuildOrApplyOutcome> RunBuildOrApplyAsync(
        ProjectConfig config,
        bool preview,
        string? expectedPlan,
        bool allowCleanup,
        Action<string>? log,
        CancellationToken cancellation)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(config.WindowsRepo)) missing.Add("windowsRepo");
        if (string.IsNullOrWhiteSpace(config.WslRepo)) missing.Add("wslRepo");
        if (string.IsNullOrWhiteSpace(config.Distro)) missing.Add("distro");
        if (string.IsNullOrWhiteSpace(config.WslUser)) missing.Add("wslUser");
        if (string.IsNullOrWhiteSpace(config.BuildCommand)) missing.Add("buildCommand");
        if (string.IsNullOrWhiteSpace(config.Branch)) missing.Add("branch");
        if (config.Include.Length == 0) missing.Add("include");
        if (missing.Count > 0)
            throw new SyncException(SyncErrorCode.Config, $"项目配置缺少必填项：{string.Join(", ", missing)}");

        string git = RequireGitExecutable();
        string repo = await ResolveValidatedWindowsRepoAsync(git, config.WindowsRepo, testConnection: false, cancellation);
        if (!Regex.IsMatch(repo, @"^[A-Za-z]:\\"))
            throw new SyncException(SyncErrorCode.Config, "Windows 仓库路径必须是本地磁盘绝对路径。");

        string distro = WrapValidation(() => SyncPath.ValidateWslDistribution(config.Distro));
        string wslUser = WrapValidation(() => SyncPath.ValidateWslUser(config.WslUser));
        string wslRepo = WrapValidation(() => SyncPath.ValidateWslRoot(config.WslRepo));
        string nodeBin = WrapValidation(() => SyncPath.ValidateNodeBin(config.NodeBin));
        string branch = ValidateBranchOrThrow(config.Branch);
        await EnsureValidGitRefFormatAsync(git, branch, cancellation);

        string[] sitePaths = WrapValidation(() => config.Include
            .Select(p => SyncPath.Normalize(p, "同步路径", repo, wslRepo, distro))
            .Distinct(StringComparer.Ordinal).ToArray());
        string[] excludePaths = WrapValidation(() => config.Exclude
            .Select(p => SyncPath.Normalize(p, "排除路径", repo, wslRepo, distro))
            .Distinct(StringComparer.Ordinal).ToArray());
        string[] pullPaths = WrapValidation(() => config.PullPaths
            .Select(p => SyncPath.Normalize(p, "拉取目录", repo, wslRepo, distro))
            .Distinct(StringComparer.Ordinal).ToArray());

        var suffixList = WrapValidation(() => config.ExcludeSuffixes
            .Select(SyncPath.ValidateSuffix)
            .Distinct(StringComparer.Ordinal).ToList());
        if (suffixList.Contains(":Zone.Identifier", StringComparer.Ordinal))
            suffixList.Add("\uF03AZone.Identifier");
        string[] suffixes = suffixList.ToArray();

        var syncPathspecs = new List<string>(sitePaths);
        foreach (string ex in excludePaths)
            syncPathspecs.Add($":(exclude,literal){ex}");
        foreach (string sfx in suffixes)
            syncPathspecs.Add($":(exclude,glob)**/*{sfx}");

        var remote = await PrepareRemoteAsync(git, config, cancellation);

        var crlfResult = await RunGitAsync(git, repo, new[] { "config", "--default", "false", "--get", "core.autocrlf" }, null, cancellation: cancellation);
        if (crlfResult.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, "无法读取目标仓库的换行符配置（core.autocrlf）。");
        string autoCrlf = crlfResult.Stdout.Trim();

        string verifiedSnapshot = "";
        string expectedHash = "";
        if (!preview)
        {
            var planMatch = Regex.Match(expectedPlan ?? "", @"\A([0-9a-f]{40}):([0-9A-F]{64})\z");
            if (!planMatch.Success)
                throw new SyncException(SyncErrorCode.PreviewExpired, "变更清单已过期，请重新构建。");
            verifiedSnapshot = planMatch.Groups[1].Value;
            expectedHash = planMatch.Groups[2].Value;
        }

        string lockKey = await ResolveLockKeyAsync(git, repo, cancellation);
        using var repoLock = AcquireRepositoryLock(lockKey);

        var fetchArgs = new List<string>(remote.GitOptions) { "fetch", "--no-tags", "--", remote.Target, $"refs/heads/{branch}" };
        var fetch = await RunGitAsync(git, repo, fetchArgs, remote.Environment, onStdout: log, onStderr: log, cancellation: cancellation);
        if (fetch.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, "Git fetch 失败，请检查网络连接与远端配置。");

        string remoteCommit = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "--verify", "FETCH_HEAD^{commit}" }, "无法解析远端分支提交。", cancellation);
        string head = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "HEAD" }, "无法读取本地分支提交。", cancellation);
        string currentBranch = await ReadRequiredGitOutputAsync(git, repo, new[] { "branch", "--show-current" }, "请先检出配置的 Windows 分支。", cancellation);
        if (!string.Equals(currentBranch, branch, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.WindowsRepo, "请先检出配置的 Windows 分支。");

        await AssertReadyIndexAsync(git, repo, allowStaged: false, cancellation);

        var ancestor = await RunGitAsync(git, repo, new[] { "merge-base", "--is-ancestor", head, remoteCommit }, null, cancellation: cancellation);
        if (ancestor.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, "本地分支无法快进到远端分支，请先检查本地提交。");
        if (!string.Equals(head, remoteCommit, StringComparison.Ordinal))
            log?.Invoke($"Windows 分支将快进：{head} -> {remoteCommit}");

        var wslResult = await RunWslSnapshotAsync(
            distro, wslUser, repo, remoteCommit, wslRepo,
            config.BuildCommand, nodeBin, autoCrlf, branch, verifiedSnapshot,
            pullPaths, syncPathspecs.ToArray(), log, cancellation);

        var protectArgs = new List<string>();
        foreach (string ex in excludePaths)
        {
            protectArgs.Add("-e");
            protectArgs.Add($"/{ex}");
            protectArgs.Add("-e");
            protectArgs.Add($"/{ex}/");
        }
        foreach (string sfx in suffixes)
        {
            protectArgs.Add("-e");
            protectArgs.Add($"*{sfx}");
        }

        string[] cleanupCandidates = Array.Empty<string>();
        if (config.CleanUntracked)
        {
            var cleanPreviewArgs = new List<string> { "-c", "core.quotepath=false", "clean", "-nd" };
            cleanPreviewArgs.AddRange(protectArgs);
            var cleanPreview = await RunGitAsync(git, repo, cleanPreviewArgs, null, cancellation: cancellation);
            if (cleanPreview.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "预览目标仓库清理项失败。");
            cleanupCandidates = SplitLines(cleanPreview.Stdout)
                .Select(line => Regex.Replace(line, @"^Would remove ", ""))
                .ToArray();
            if (!preview && !allowCleanup)
                throw new SyncException(SyncErrorCode.CleanupConfirmation, "目标仓库未跟踪文件清理需要明确确认。");
        }

        string planHash = ComputePlanHash(
            head, remoteCommit, wslResult.Snapshot, wslResult.Trees, repo, remote.Target, branch,
            distro, wslUser, wslRepo, config.BuildCommand, nodeBin, autoCrlf, config.CleanUntracked,
            pullPaths, syncPathspecs.ToArray(), cleanupCandidates, wslResult.UntrackedDeletions);

        if (preview)
        {
            return new BuildOrApplyOutcome(
                new SyncPreviewResult(
                    $"{wslResult.Snapshot}:{planHash}",
                    wslResult.Snapshot,
                    wslResult.Changes,
                    cleanupCandidates,
                    wslResult.UntrackedDeletions),
                Applied: null);
        }

        if (!string.Equals(planHash, expectedHash, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.PreviewExpired, "源端、目标端或远端已发生变化，请重新构建。");

        string verifyBranch = await ReadRequiredGitOutputAsync(git, repo, new[] { "branch", "--show-current" }, "当前分支已变化，请重新构建。", cancellation);
        if (!string.Equals(verifyBranch, branch, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.PreviewExpired, "当前检出分支已变化，请重新构建。");

        string verifyHead = await ReadRequiredGitOutputAsync(git, repo, new[] { "rev-parse", "HEAD" }, "本地分支已变化，请重新构建。", cancellation);
        if (!string.Equals(verifyHead, head, StringComparison.Ordinal))
            throw new SyncException(SyncErrorCode.PreviewExpired, "本地分支已变化，请重新构建。");

        await AssertReadyIndexAsync(git, repo, allowStaged: false, cancellation);

        if (config.CleanUntracked)
        {
            var recheckArgs = new List<string> { "-c", "core.quotepath=false", "clean", "-nd" };
            recheckArgs.AddRange(protectArgs);
            var recheck = await RunGitAsync(git, repo, recheckArgs, null, cancellation: cancellation);
            string[] currentCleanup = recheck.ExitCode == 0
                ? SplitLines(recheck.Stdout).Select(line => Regex.Replace(line, @"^Would remove ", "")).ToArray()
                : Array.Empty<string>();
            if (recheck.ExitCode != 0 || !currentCleanup.SequenceEqual(cleanupCandidates, StringComparer.Ordinal))
                throw new SyncException(SyncErrorCode.PreviewExpired, "待清理文件已变化，请重新构建后再同步。");
        }

        if (!string.Equals(head, remoteCommit, StringComparison.Ordinal))
        {
            var merge = await RunGitAsync(git, repo, new[] { "merge", "--ff-only", remoteCommit }, null, onStdout: log, onStderr: log, cancellation: cancellation);
            if (merge.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "快进合并失败，请先检查 Windows 工作区。");
        }

        if (config.CleanUntracked)
        {
            var cleanArgs = new List<string> { "clean", "-fd" };
            cleanArgs.AddRange(protectArgs);
            var clean = await RunGitAsync(git, repo, cleanArgs, null, onStdout: log, onStderr: log, cancellation: cancellation);
            if (clean.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "清理 Windows 未跟踪文件失败。");
        }

        var restoreArgs = new List<string> { "restore", $"--source={wslResult.Snapshot}", "--worktree", "--" };
        restoreArgs.AddRange(syncPathspecs);
        var restore = await RunGitAsync(git, repo, restoreArgs, null, onStdout: log, onStderr: log, cancellation: cancellation);
        if (restore.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, "同步文件到 Windows 工作区失败。");

        // Remove any untracked (non-ignored) files inside sync scope that were reported as deleted in the preview.
        RemoveVerifiedUntrackedDeletions(repo, wslResult.UntrackedDeletions);

        log?.Invoke($"已同步经校验的构建源码（远端基线 {remoteCommit}）");
        return new BuildOrApplyOutcome(Preview: null, new SyncApplyResult(remoteCommit, wslResult.Snapshot));
    }

    private static void RemoveVerifiedUntrackedDeletions(string repoRoot, string[] relativePaths)
    {
        if (relativePaths.Length == 0) return;
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        string rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        foreach (string relative in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relative) ||
                Regex.IsMatch(relative, @"(^/|\\|(^|/)(\.{1,2}|(?i:\.git))(/|$))"))
                continue;
            string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(fullPath)) continue;
            var attrs = File.GetAttributes(fullPath);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
            File.Delete(fullPath);
            PruneEmptyParents(Path.GetDirectoryName(fullPath), fullRoot);
        }
    }

    private static void PruneEmptyParents(string? directory, string stopAtRoot)
    {
        while (directory is not null &&
               !string.Equals(directory, stopAtRoot, StringComparison.OrdinalIgnoreCase) &&
               directory.StartsWith(stopAtRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                break;
            try { Directory.Delete(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            directory = Path.GetDirectoryName(directory);
        }
    }

    private sealed record WslSnapshotOutcome(
        string Snapshot,
        string Trees,
        SyncChangeItem[] Changes,
        string[] UntrackedDeletions);

    private static async Task<WslSnapshotOutcome> RunWslSnapshotAsync(
        string distro,
        string wslUser,
        string windowsRepo,
        string remoteCommit,
        string wslRepo,
        string buildCommand,
        string nodeBin,
        string targetAutoCrlf,
        string sourceBranch,
        string verifiedSnapshot,
        string[] pullPaths,
        string[] syncPathspecs,
        Action<string>? log,
        CancellationToken cancellation)
    {
        string argsFile = Path.Combine(Path.GetTempPath(), $"sitelift-args-{Guid.NewGuid():N}.bin");
        string resultFile = Path.Combine(Path.GetTempPath(), $"sitelift-res-{Guid.NewGuid():N}.bin");
        string? leaseFile = cancellation.CanBeCanceled
            ? Path.Combine(Path.GetTempPath(), $"sitelift-lease-{Guid.NewGuid():N}.tmp")
            : null;
        try
        {
            await WriteNulPayloadAsync(argsFile, BuildWslPayloadFields(
                windowsRepo, remoteCommit, wslRepo, buildCommand, nodeBin,
                targetAutoCrlf, sourceBranch, verifiedSnapshot, pullPaths, syncPathspecs));
            await File.WriteAllBytesAsync(resultFile, Array.Empty<byte>(), cancellation);
            if (leaseFile is not null)
                await File.WriteAllBytesAsync(leaseFile, Array.Empty<byte>(), cancellation);

            var wslArgs = new List<string>
            {
                "-d", distro, "-u", wslUser, "--exec", "bash", "-s", "--",
                argsFile, resultFile, leaseFile ?? ""
            };

            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in wslArgs)
                start.ArgumentList.Add(arg);

            cancellation.ThrowIfCancellationRequested();
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("无法启动 WSL 进程。");
            using var logCts = new CancellationTokenSource();

            static bool IsCrlfWarning(string line) =>
                Regex.IsMatch(line,
                    @"^warning: in the working copy of '.+', (?:LF will be replaced by CRLF|CRLF will be replaced by LF) the next time Git touches it$");

            Task ReadLog(StreamReader reader) => Task.Run(async () =>
            {
                try
                {
                    while (await reader.ReadLineAsync(logCts.Token) is { } line)
                    {
                        string cleaned = line.TrimEnd('\r');
                        if (cleaned.Length > 0 && !IsCrlfWarning(cleaned))
                            log?.Invoke(cleaned);
                    }
                }
                catch (OperationCanceledException) when (logCts.IsCancellationRequested) { }
            });

            Task stdoutTask = ReadLog(process.StandardOutput);
            Task stderrTask = ReadLog(process.StandardError);
            try
            {
                await process.StandardInput.WriteAsync(WslBuildHelperScript.ReplaceLineEndings("\n"));
                await process.StandardInput.FlushAsync();
            }
            catch (IOException) { }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }

            Task execution = Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());
            try
            {
                await execution.WaitAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                TryDeleteFile(leaseFile);
                if (await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(4))) != execution)
                {
                    ProcessRunner.TryKillTree(process);
                    logCts.Cancel();
                }
                try { await execution; }
                catch (OperationCanceledException) when (logCts.IsCancellationRequested) { }
                throw;
            }

            byte[] rawResult = File.Exists(resultFile) ? await File.ReadAllBytesAsync(resultFile) : Array.Empty<byte>();
            var parsed = ParseWslResultPayload(rawResult);
            if (parsed.ErrorCode != SyncErrorCode.None)
                throw new SyncException(parsed.ErrorCode, MapErrorCodeMessage(parsed.ErrorCode));

            if (process.ExitCode != 0 || parsed.Snapshot is null || parsed.Trees is null)
                throw new SyncException(SyncErrorCode.Build, "WSL 构建或源码快照失败，已停止同步。请查看日志。");

            return new WslSnapshotOutcome(parsed.Snapshot, parsed.Trees, parsed.Changes, parsed.UntrackedDeletions);
        }
        finally
        {
            TryDeleteFile(argsFile);
            TryDeleteFile(resultFile);
            TryDeleteFile(leaseFile);
        }
    }

    internal static IReadOnlyList<string> BuildWslPayloadFields(
        string windowsRepo,
        string remoteCommit,
        string wslRepo,
        string buildCommand,
        string nodeBin,
        string targetAutoCrlf,
        string sourceBranch,
        string verifiedSnapshot,
        string[] pullPaths,
        string[] syncPathspecs)
    {
        var fields = new List<string>
        {
            windowsRepo,
            remoteCommit,
            wslRepo,
            buildCommand.ReplaceLineEndings("\n"),
            nodeBin,
            targetAutoCrlf,
            sourceBranch,
            verifiedSnapshot,
            pullPaths.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        fields.AddRange(pullPaths);
        fields.Add(syncPathspecs.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.AddRange(syncPathspecs);
        return fields;
    }

    private static async Task WriteNulPayloadAsync(string path, IReadOnlyList<string> fields)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var utf8 = new UTF8Encoding(false);
        foreach (string field in fields)
        {
            byte[] bytes = utf8.GetBytes(field);
            await stream.WriteAsync(bytes);
            stream.WriteByte(0);
        }
    }

    internal sealed record ParsedWslResult(
        SyncErrorCode ErrorCode,
        string? Snapshot,
        string? Trees,
        SyncChangeItem[] Changes,
        string[] UntrackedDeletions);

    internal static ParsedWslResult ParseWslResultPayload(byte[] raw)
    {
        var tokens = new List<string>();
        int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0) continue;
            tokens.Add(Encoding.UTF8.GetString(raw, start, i - start));
            start = i + 1;
        }

        SyncErrorCode errorCode = SyncErrorCode.None;
        string? snapshot = null;
        string? trees = null;
        var changes = new List<SyncChangeItem>();
        var untrackedDeletes = new List<string>();

        int idx = 0;
        while (idx < tokens.Count)
        {
            string tag = tokens[idx++];
            switch (tag)
            {
                case "ERROR" when idx < tokens.Count:
                    errorCode = ParseErrorCode(tokens[idx++]);
                    break;
                case "SNAPSHOT" when idx < tokens.Count:
                    string snap = tokens[idx++];
                    if (CommitHashRegex.IsMatch(snap)) snapshot = snap;
                    break;
                case "TREES" when idx < tokens.Count:
                    string t = tokens[idx++];
                    if (Regex.IsMatch(t, @"\A[0-9a-f]{40}:[0-9a-f]{40}\z")) trees = t;
                    break;
                case "CHANGE" when idx + 1 < tokens.Count:
                    string st = tokens[idx++];
                    string path = tokens[idx++];
                    if (st is "A" or "M" or "D" or "T" && path.Length > 0)
                        changes.Add(new SyncChangeItem(st, path));
                    break;
                case "UNTRACKED_DELETE" when idx < tokens.Count:
                    string del = tokens[idx++];
                    if (del.Length > 0) untrackedDeletes.Add(del);
                    break;
            }
        }

        return new ParsedWslResult(errorCode, snapshot, trees, changes.ToArray(),
            untrackedDeletes.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    private static SyncErrorCode ParseErrorCode(string code) => code switch
    {
        "WSL_DIRECTORY" => SyncErrorCode.WslDirectory,
        "SOURCE_MISSING" => SyncErrorCode.SourceMissing,
        "WSL_INDEX" => SyncErrorCode.WslIndex,
        "BUILD" => SyncErrorCode.Build,
        "PREVIEW_EXPIRED" => SyncErrorCode.PreviewExpired,
        _ => SyncErrorCode.Build
    };

    public static string MapErrorCodeMessage(SyncErrorCode code) => code switch
    {
        SyncErrorCode.WindowsGitMissing => "找不到 Windows Git。请安装 Git for Windows，确认 git 可从 Windows 命令行运行，然后重启 SiteLift 并重试。",
        SyncErrorCode.RepositoryBusy => "此 Windows 仓库正在执行另一项 SiteLift 操作。",
        SyncErrorCode.Config => "项目配置缺少必填项或格式无效，请检查项目设置。",
        SyncErrorCode.WindowsRepo => "Windows Git 仓库不可用。请在「Git 本地仓库」选择正确目录，并检查仓库操作。",
        SyncErrorCode.WslDirectory => "无法进入 WSL 源目录。请在项目设置中重新检测环境。",
        SyncErrorCode.SourceMissing => "WSL 缺少配置中指定的同步文件。请核对日志里的路径。",
        SyncErrorCode.WslIndex => "WSL 暂存区与当前文件不一致，或 Git 索引不可读。请查看日志并核对暂存内容。",
        SyncErrorCode.Build => "构建命令执行失败。请查看上方的构建输出。",
        SyncErrorCode.PreviewExpired => "变更清单已过期，请重新构建。",
        SyncErrorCode.CleanupConfirmation => "目标仓库未跟踪文件清理需要明确确认。",
        SyncErrorCode.Push => "本地提交已生成。请查看推送错误，然后重试推送。",
        _ => "操作失败，详情见日志。"
    };

    private sealed record PreparedRemote(string Target, string? CleanUrl, List<string> GitOptions, Dictionary<string, string> Environment);

    private static async Task<PreparedRemote> PrepareRemoteAsync(
        string git, ProjectConfig config, CancellationToken cancellation)
    {
        string remoteUrl = config.RemoteUrl.Trim();
        string target;
        string? cleanUrl = null;
        if (remoteUrl.Length > 0)
        {
            if (!RemoteUrlRegex.IsMatch(remoteUrl))
                throw new SyncException(SyncErrorCode.Config, "请输入有效的 SSH 或 HTTPS 仓库地址。");
            target = remoteUrl;
            cleanUrl = remoteUrl;
        }
        else
        {
            target = ValidateRemoteNameOrThrow(config.Remote);
        }

        string githubUser = config.GithubUsername.Trim();
        if (githubUser.Length > 0)
        {
            if (!GithubUserRegex.IsMatch(githubUser))
                throw new SyncException(SyncErrorCode.Config, "请输入有效的 GitHub 用户名。");
            if (!Regex.IsMatch(remoteUrl, @"^https://github\.com/", RegexOptions.IgnoreCase))
                throw new SyncException(SyncErrorCode.Config, "填写 GitHub 用户名时必须使用 https://github.com 仓库地址。");
        }

        var options = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_TERMINAL_PROMPT"] = "0"
        };

        if (Regex.IsMatch(remoteUrl, @"^https://", RegexOptions.IgnoreCase))
        {
            if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new SyncException(SyncErrorCode.Config, "仓库地址不能包含内嵌凭据、查询参数或锚点。");
            }
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var gcm = await RunGitAsync(git, Path.GetTempPath(), new[] { "credential-manager", "--version" }, null, cancellation: cancellation);
                if (gcm.ExitCode != 0)
                    throw new SyncException(SyncErrorCode.Config, "使用 GitHub HTTPS 认证需要安装 Git Credential Manager。");
                target = remoteUrl.TrimEnd('/');
                if (githubUser.Length > 0)
                {
                    var builder = new UriBuilder(target) { UserName = githubUser };
                    target = builder.Uri.AbsoluteUri;
                    env["GCM_NAMESPACE"] = $"sitelift-github-{githubUser.ToLowerInvariant()}";
                    env["GCM_CREDENTIAL_STORE"] = "wincredman";
                }
                options.AddRange(new[] { "-c", "credential.helper=", "-c", "credential.helper=manager", "-c", "credential.interactive=true" });
            }
        }
        else if (!string.IsNullOrWhiteSpace(config.GitSshCommand))
        {
            env["GIT_SSH_COMMAND"] = config.GitSshCommand.Trim();
        }

        return new PreparedRemote(target, cleanUrl, options, env);
    }

    private static async Task<string> ResolveValidatedWindowsRepoAsync(
        string git, string windowsRepo, bool testConnection, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(windowsRepo) || !Directory.Exists(windowsRepo))
        {
            throw testConnection
                ? new SyncException(SyncErrorCode.Config, "没有本地 Git 仓库时，请先填写远端仓库地址再测试连接。")
                : new SyncException(SyncErrorCode.WindowsRepo, "Windows 项目目录不存在或不可访问，请在「Git 本地仓库」选择目录。");
        }
        string repo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsRepo));
        var prefix = await RunGitAsync(git, repo, new[] { "rev-parse", "--show-prefix" }, null, cancellation: cancellation);
        if (prefix.ExitCode != 0 || prefix.Stdout.Trim().Length > 0)
        {
            throw testConnection
                ? new SyncException(SyncErrorCode.Config, "没有本地 Git 仓库时，请先填写远端仓库地址再测试连接。")
                : new SyncException(SyncErrorCode.WindowsRepo, "请在「Git 本地仓库」选择仓库根目录，并检查仓库操作。");
        }
        return repo;
    }

    private static async Task<string> ResolveLockKeyAsync(string git, string repo, CancellationToken cancellation)
    {
        var common = await RunGitAsync(git, repo, new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, null, cancellation: cancellation);
        if (common.ExitCode != 0 || string.IsNullOrWhiteSpace(common.Stdout))
            throw new SyncException(SyncErrorCode.WindowsRepo, "无法定位 Windows Git 元数据目录。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(common.Stdout.Trim()));
    }

    private static FileStream AcquireRepositoryLock(string keyPath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(keyPath)).ToUpperInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        string lockDir = Path.Combine(Path.GetTempPath(), "SiteLift-Locks");
        Directory.CreateDirectory(lockDir);
        string lockPath = Path.Combine(lockDir, $"{hash}.lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new SyncException(SyncErrorCode.RepositoryBusy, "此 Windows 仓库正在执行另一项 SiteLift 操作。");
        }
    }

    private static async Task AssertReadyIndexAsync(string git, string repo, bool allowStaged, CancellationToken cancellation)
    {
        if (!allowStaged)
        {
            var cached = await RunGitAsync(git, repo, new[] { "diff", "--cached", "--quiet" }, null, cancellation: cancellation);
            if (cached.ExitCode != 0)
                throw new SyncException(SyncErrorCode.WindowsRepo, "暂存区存在未提交改动，请先核对后再继续。");
        }

        foreach (string state in InProgressGitStates)
        {
            var statePath = await RunGitAsync(git, repo, new[] { "rev-parse", "--path-format=absolute", "--git-path", state }, null, cancellation: cancellation);
            string resolved = statePath.Stdout.Trim();
            if (statePath.ExitCode != 0 || (resolved.Length > 0 && (File.Exists(resolved) || Directory.Exists(resolved))))
                throw new SyncException(SyncErrorCode.WindowsRepo, "仓库正处于未完成的合并/变基/拣选操作中，请先完成该操作。");
        }
    }

    private static async Task EnsureValidGitRefFormatAsync(string git, string branch, CancellationToken cancellation)
    {
        var check = await RunGitAsync(git, Path.GetTempPath(), new[] { "check-ref-format", $"refs/heads/{branch}" }, null, cancellation: cancellation);
        if (check.ExitCode != 0)
            throw new SyncException(SyncErrorCode.Config, $"目标分支名称无效：{branch}");
    }

    private static string ValidateBranchOrThrow(string branch) =>
        WrapValidation(() => SyncPath.ValidateBranchName(branch));

    private static string ValidateRemoteNameOrThrow(string remote) =>
        WrapValidation(() => SyncPath.ValidateRemoteName(remote));

    private static T WrapValidation<T>(Func<T> validate)
    {
        try { return validate(); }
        catch (ArgumentException ex) { throw new SyncException(SyncErrorCode.Config, ex.Message); }
    }

    private static string RequireGitExecutable() =>
        ProcessRunner.FindGitExecutable()
        ?? throw new SyncException(SyncErrorCode.WindowsGitMissing, MapErrorCodeMessage(SyncErrorCode.WindowsGitMissing));

    private static async Task<string> ReadRequiredGitOutputAsync(
        string git, string repo, string[] args, string failureMessage, CancellationToken cancellation)
    {
        var result = await RunGitAsync(git, repo, args, null, cancellation: cancellation);
        if (result.ExitCode != 0)
            throw new SyncException(SyncErrorCode.WindowsRepo, failureMessage);
        return result.Stdout.Trim();
    }

    private static Task<ProcessResult> RunGitAsync(
        string gitExecutable,
        string? repoDirectory,
        IEnumerable<string> arguments,
        Dictionary<string, string>? environment,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        TimeSpan? timeout = null,
        CancellationToken cancellation = default)
    {
        var start = new System.Diagnostics.ProcessStartInfo(gitExecutable)
        {
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (repoDirectory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(repoDirectory);
        }
        foreach (string arg in arguments)
            start.ArgumentList.Add(arg);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        }
        return ProcessRunner.RunAsync(start, timeout, onStdout, onStderr, closeStdin: true, cancellation: cancellation);
    }

    private static string ComputePlanHash(params object?[] items)
    {
        string text = JsonSerializer.Serialize(items);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string[] SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

    private static void TryDeleteFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectoryForce(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(entry);
                    if (attrs.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                }
                catch { }
            }
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal const string WslBuildHelperScript = """
set -euo pipefail
args_file=$(wslpath -u "$1")
result_file=$(wslpath -u "$2")
build_lease=""
if [[ -n ${3:-} ]]; then
    build_lease=$(wslpath -u "$3")
fi

write_record() {
    printf '%s\0' "$@" >> "$result_file"
}

fail_with() {
    write_record ERROR "$1"
    if [[ -n ${2:-} ]]; then
        printf '%s\n' "$2" >&2
    fi
    exit 1
}

check_lease() {
    [[ -z $build_lease || -f $build_lease ]] || exit 130
}

exec 8<"$args_file"
read_field() {
    IFS= read -r -d '' "$1" <&8 || fail_with BUILD 'Failed to read SiteLift arguments.'
}

read_field windows_repo_win
read_field remote_commit
read_field wsl_repo
read_field build_command
read_field node_bin
read_field target_autocrlf
read_field source_branch
read_field verified_snapshot
read_field pull_count
pull_paths=()
for ((i=0; i<pull_count; i++)); do
    read_field item
    pull_paths+=("$item")
done
read_field site_count
site_paths=()
for ((i=0; i<site_count; i++)); do
    read_field item
    site_paths+=("$item")
done
exec 8<&-

windows_repo=$(wslpath -u "$windows_repo_win")
cd -- "$wsl_repo" || fail_with WSL_DIRECTORY "Cannot enter WSL directory: $wsl_repo"
check_lease

require_source() {
    if [[ ! -e $1 ]]; then
        fail_with SOURCE_MISSING "Missing required source: $2"
    fi
    printf 'Source present: %s\n' "$2"
}

for path in "${site_paths[@]}"; do
    [[ $path == ':('* ]] && continue
    if [[ -z $verified_snapshot ]] && (( pull_count > 0 )); then
        supplied_by_pull=false
        for pull in "${pull_paths[@]}"; do
            if [[ $path == "$pull" || $path == "$pull/"* || $pull == "$path/"* ]]; then
                supplied_by_pull=true
                break
            fi
        done
        if $supplied_by_pull; then continue; fi
    fi
    require_source "$path" "$path"
done

if [[ ! -e .git && -n $verified_snapshot ]]; then
    fail_with PREVIEW_EXPIRED
fi
if [[ ! -e .git ]]; then
    git init --initial-branch="$source_branch" .
fi
git_dir=$(git rev-parse --absolute-git-dir)
exec 9>"$git_dir/sitelift-build.lock"
flock -n 9 || { echo 'A SiteLift build is already running in this WSL repository.' >&2; exit 1; }
if [[ -n $node_bin ]]; then export PATH="$node_bin:$PATH"; fi
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT

mirror_pull_directories() (
    roots=()
    for path in "${pull_paths[@]}"; do
        covered=false
        for candidate in "${pull_paths[@]}"; do
            if [[ $path == "$candidate/"* ]]; then covered=true; break; fi
        done
        if $covered; then continue; fi
        parent=$path
        while [[ $parent == */* ]]; do
            parent=${parent%/*}
            if [[ -L $parent || ( -e $parent && ! -d $parent ) ]]; then
                printf 'Unsafe WSL mirror parent: %s\n' "$parent" >&2
                exit 1
            fi
        done
        roots+=("$path")
    done

    stage=$(mktemp -d ./.sitelift-mirror.XXXXXXXX)
    moved=()
    committed=false
    finish_mirror() {
        local result=$? i path recovered=true
        if ! $committed; then
            for ((i=${#moved[@]}-1; i>=0; i--)); do
                path=${moved[i]}
                if [[ ! -e $stage/next/$path ]]; then
                    mv -T -- "$path" "$stage/next/$path" || { recovered=false; continue; }
                fi
                if [[ -e $stage/previous/$i || -L $stage/previous/$i ]]; then
                    mv -T -- "$stage/previous/$i" "$path" || recovered=false
                fi
            done
        fi
        if $recovered; then
            rm -rf --one-file-system -- "$stage"
        else
            printf 'WSL mirror rollback failed. Backups were kept in %s/%s\n' "$PWD" "$stage" >&2
        fi
        return "$result"
    }
    trap finish_mirror EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    mkdir -- "$stage/next" "$stage/previous"
    (
        export GIT_INDEX_FILE="$tmp/mirror-index"
        git read-tree "$remote_commit"
        git --literal-pathspecs ls-files --stage -- "${roots[@]}" > "$tmp/mirror-entries"
        if grep '^160000 ' "$tmp/mirror-entries" >/dev/null; then
            echo 'WSL mirror directories cannot contain Git submodules.' >&2
            exit 1
        fi
        git --literal-pathspecs ls-files -z -- "${roots[@]}" |
            git -c core.symlinks=true checkout-index --prefix="$stage/next/" --stdin -z
    )
    for path in "${roots[@]}"; do
        mkdir -p -- "$stage/next/$path"
    done
    for path in "${site_paths[@]}"; do
        [[ $path == ':('* ]] && continue
        for pull in "${roots[@]}"; do
            if [[ $path == "$pull" || $path == "$pull/"* ]]; then
                require_source "$stage/next/$path" "$path"
                break
            fi
            if [[ $pull == "$path/"* && ! -e $path ]]; then
                require_source "$stage/next/$path" "$path"
                break
            fi
        done
    done

    for path in "${roots[@]}"; do
        check_lease
        mkdir -p -- "$(dirname -- "$path")"
        i=${#moved[@]}
        moved+=("$path")
        if [[ -e $path || -L $path ]]; then
            mv -T -- "$path" "$stage/previous/$i"
        fi
        mv -T -- "$stage/next/$path" "$path"
    done
    committed=true
)

if [[ -z $verified_snapshot ]]; then
    git -c safe.directory="$windows_repo" fetch --no-tags "$windows_repo" "$remote_commit"
    if (( pull_count > 0 )); then
        for path in "${pull_paths[@]}"; do
            if [[ $(git cat-file -t "$remote_commit:$path" 2>/dev/null) != tree ]]; then
                printf 'Missing remote directory: %s\n' "$path" >&2
                exit 1
            fi
        done
    fi
    if (( pull_count > 0 )) && [[ . -ef $windows_repo ]]; then
        echo 'WSL mirror requires a repository separate from the Windows repository.' >&2
        exit 1
    fi
    if ! git rev-parse --verify -q 'HEAD^{commit}' >/dev/null; then
        if ! git symbolic-ref -q HEAD >/dev/null || ! staged_tree=$(git write-tree); then
            fail_with WSL_INDEX
        fi
        if ! (
            export GIT_INDEX_FILE="$tmp/initial-index"
            git read-tree "$staged_tree" &&
            git add -u &&
            git -c core.quotepath=false diff --cached --name-status "$staged_tree" &&
            git diff --cached --quiet "$staged_tree"
        ); then
            fail_with WSL_INDEX
        fi
        git reset --mixed --quiet "$remote_commit"
        echo 'Initialized WSL Git history from the configured remote branch.'
    fi
    if (( pull_count > 0 )); then
        mirror_pull_directories
    fi

    if [[ -z $build_lease ]]; then
        CI=1 NO_COLOR=1 bash -e -o pipefail -c "$build_command" 9>&- || fail_with BUILD
    else
        check_lease
        CI=1 NO_COLOR=1 setsid bash -e -o pipefail -c "$build_command" </dev/null 9>&- &
        build_pid=$!
        while kill -0 "$build_pid" 2>/dev/null; do
            if [[ ! -f $build_lease ]]; then
                kill -TERM -- "-$build_pid" 2>/dev/null || kill -TERM "$build_pid" 2>/dev/null || true
                sleep 1
                kill -KILL -- "-$build_pid" 2>/dev/null || true
                wait "$build_pid" || true
                exit 130
            fi
            sleep 0.2
        done
        wait "$build_pid" || fail_with BUILD
        check_lease
    fi
fi

source_repo=$PWD
source_tree=$(
    export GIT_INDEX_FILE="$tmp/index"
    git read-tree HEAD
    git ls-files -z --cached --others --exclude-standard -- "${site_paths[@]}" > "$tmp/paths"
    if [[ -s "$tmp/paths" ]]; then
        LC_ALL=C git -c core.autocrlf="$target_autocrlf" --literal-pathspecs add -f -A --pathspec-from-file="$tmp/paths" --pathspec-file-nul
    fi
    git write-tree
)

if [[ -z $verified_snapshot ]]; then
    snapshot=$(git -c user.name='SiteLift' -c user.email='sitelift@localhost' commit-tree "$source_tree" -p HEAD -m 'Verified SiteLift source snapshot')
    git -c safe.directory="$windows_repo" -C "$windows_repo" fetch --no-tags --no-write-fetch-head "$source_repo" "$snapshot"
else
    snapshot=$verified_snapshot
fi
write_record SNAPSHOT "$snapshot"

(
    export GIT_INDEX_FILE="$tmp/target-index"
    git_target=(git -c core.filemode=false -c core.autocrlf="$target_autocrlf" -c safe.directory="$windows_repo" -C "$windows_repo")
    "${git_target[@]}" read-tree HEAD
    "${git_target[@]}" ls-files -z --cached --others --exclude-standard -- "${site_paths[@]}" > "$tmp/target-paths"
    if [[ -s "$tmp/target-paths" ]]; then
        LC_ALL=C "${git_target[@]}" --literal-pathspecs add -f -A --pathspec-from-file="$tmp/target-paths" --pathspec-file-nul
    fi
    target_tree=$("${git_target[@]}" write-tree)
    if [[ $("${git_target[@]}" rev-parse --verify "$snapshot^{tree}") != "$source_tree" ]]; then
        fail_with PREVIEW_EXPIRED
    fi

    "${git_target[@]}" ls-files -z --others --exclude-standard -- "${site_paths[@]}" > "$tmp/untracked-paths"

    while IFS= read -r -d '' status && IFS= read -r -d '' changed_path; do
        if [[ -z $verified_snapshot ]]; then
            write_record CHANGE "$status" "$changed_path"
        fi
        if [[ $status == D && -s "$tmp/untracked-paths" ]] && grep -Fzqx -- "$changed_path" "$tmp/untracked-paths"; then
            write_record UNTRACKED_DELETE "$changed_path"
        fi
    done < <("${git_target[@]}" diff -z --name-status --no-renames "$target_tree" "$source_tree" -- "${site_paths[@]}")

    write_record TREES "$source_tree:$target_tree"
)
""";
}

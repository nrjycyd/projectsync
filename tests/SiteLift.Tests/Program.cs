using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SiteLift.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject<T>(Action operation) where T : Exception
{
    try { operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task RunGit(string workingDir, params string[] args)
{
    string git = ProcessRunner.FindGitExecutable() ?? throw new InvalidOperationException("git.exe not found");
    var start = new ProcessStartInfo(git) { WorkingDirectory = workingDir };
    foreach (string arg in args) start.ArgumentList.Add(arg);
    var res = await ProcessRunner.RunAsync(start);
    if (res.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {res.CombinedOutput}");
}

// 1. SyncPath tests
static string Normalize(string path) =>
    SyncPath.Normalize(path, "sync", @"D:\项目 空格", "/home/user/项目 空格", "Ubuntu");

foreach (string path in new[]
{
    @"文档\a.txt", "\"d:/项目 空格/文档/a.txt\"",
    "/home/user/项目 空格/文档/a.txt", @"\\wsl.localhost\Ubuntu\home\user\项目 空格\文档\a.txt"
})
{
    Check(Normalize(path) == "文档/a.txt", $"Unexpected normalization: {path}");
}

foreach (string path in new[]
{
    "", ".", "../a", "dir/.GIT/config", "src//a", "src/*.txt",
    @"\\wsl$\Other\home\user\项目 空格\文档", @"D:\项目 空格2\a", "/home/user/outside/a"
})
{
    Reject<ArgumentException>(() => Normalize(path));
}

Check(SyncPath.ValidateSuffix(" .map ") == ".map", "Suffix should trim.");
Check(SyncPath.ValidateSuffix(":Zone.Identifier") == ":Zone.Identifier", "Zone.Identifier suffix should be allowed.");
Reject<ArgumentException>(() => SyncPath.ValidateSuffix("*.map"));
Reject<ArgumentException>(() => SyncPath.ValidateSuffix("dir/file"));

Check(SyncPath.ValidateNodeBin("/home/u/.nvm/versions/node/v22.0.0/bin") == "/home/u/.nvm/versions/node/v22.0.0/bin", "Valid nodeBin.");
Check(SyncPath.ValidateNodeBin("") == "", "Empty nodeBin allowed.");
Reject<ArgumentException>(() => SyncPath.ValidateNodeBin("./evil"));
Reject<ArgumentException>(() => SyncPath.ValidateNodeBin("/tmp/a:/tmp/b"));
Reject<ArgumentException>(() => SyncPath.ValidateNodeBin("/home/u/../evil"));

Check(SyncPath.ValidateBranchName("main") == "main", "Valid branch.");
Check(SyncPath.ValidateBranchName("release/v0.2.7") == "release/v0.2.7", "Slash branch.");
Reject<ArgumentException>(() => SyncPath.ValidateBranchName("-leading-dash"));
Reject<ArgumentException>(() => SyncPath.ValidateBranchName("a..b"));
Reject<ArgumentException>(() => SyncPath.ValidateBranchName("bad branch"));

Check(SyncPath.ValidateRemoteName("origin") == "origin", "Valid remote.");
Reject<ArgumentException>(() => SyncPath.ValidateRemoteName("-origin"));
Reject<ArgumentException>(() => SyncPath.ValidateRemoteName("https://github.com/a/b"));
Reject<ArgumentException>(() => SyncPath.ValidateRemoteName("git@github.com:a/b.git"));

// 2. ProjectConfig & SecurityReview tests
{
    string json = """
    {
      "name": "Demo",
      "windowsRepo": "D:\\repo",
      "remote": "origin",
      "branch": "main",
      "distro": "Ubuntu",
      "wslUser": "dev",
      "wslRepo": "/home/dev/repo",
      "nodeBin": "/home/dev/.nvm/bin",
      "buildCommand": "echo \"hello world\"",
      "pullPath": "docs/notices",
      "include": ["docs"],
      "cleanUntracked": true
    }
    """;
    var cfg = ProjectConfig.Parse(json);
    Check(cfg.PullPaths.SequenceEqual(new[] { "docs/notices" }), "Legacy pullPath should map to PullPaths.");
    Check(cfg.BuildCommand == "echo \"hello world\"", "Quoted buildCommand preserved.");

    var review = ProjectConfig.ExtractSecurityReviewItems(json);
    Check(review.Any(i => i.Key == "buildCommand"), "Review must include buildCommand.");
    Check(review.Any(i => i.Key == "nodeBin"), "Review must include nodeBin (PATH hijack protection).");
    Check(review.Any(i => i.Key == "pullPaths"), "Review must include pullPaths.");
    Check(review.Any(i => i.Key == "cleanUntracked"), "Review must include cleanUntracked.");

    var prev = ProjectConfig.ParseRawObject(json);
    Check(ProjectConfig.ExtractSecurityReviewItems(json, prev).Count == 0, "Unchanged config should have 0 review items.");

    string renamed = json.Replace("\"Demo\"", "\"Demo2\"");
    Check(ProjectConfig.ComputeBuildFingerprint(json) == ProjectConfig.ComputeBuildFingerprint(renamed),
        "Renaming project must not invalidate build fingerprint.");

    string withMaintenance = json.Replace("\"cleanUntracked\": true",
        "\"reinstallCommand\": \"pnpm install\", \"themeUpgradeCommand\": \"pnpm up\", \"cleanUntracked\": true");
    Check(ProjectConfig.ComputeBuildFingerprint(json) == ProjectConfig.ComputeBuildFingerprint(withMaintenance),
        "Changing maintenance commands must not invalidate build fingerprint.");

    Check(SyncPath.IsValidWslUser("dev_01"), "Valid WSL user.");
    Check(!SyncPath.IsValidWslUser("-bad"), "Leading dash WSL user must be rejected.");
    Check(!SyncPath.IsValidWslUser("bad user"), "Whitespace WSL user must be rejected.");

    Reject<JsonException>(() => ProjectConfig.Parse("""{"git":{"branch":"main"}}"""));
    Check(ProjectConfig.TryReadString("{invalid", "branch") == "", "Safe reader should handle malformed JSON.");
}

// 3. FileCopy tests (including read-only overwrite, Zone.Identifier, ResolvePath)
{
    string root = Directory.CreateTempSubdirectory("sitelift-tests-").FullName;
    try
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "来源 空格")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(root, "目标 空格")).FullName;
        string input = Path.Combine(source, "文件.txt");
        File.WriteAllText(input, "original");
        File.WriteAllText(Path.Combine(source, "文件.txt.Zone.Identifier"), "[ZoneTransfer]");
        string output = Path.Combine(target, Path.GetFileName(source), Path.GetFileName(input));

        var first = FileCopy.Preview(source, target, replace: false, skipDownloadMark: true);
        Check(first.Any(entry => entry.Action == CopyAction.Copy), "Preview should include the new file.");
        Check(first.Any(entry => entry.Action == CopyAction.SkipDownloadMark), "Preview should skip Zone.Identifier.");
        FileCopy.Apply(first, skipDownloadMark: true, _ => { });
        Check(File.ReadAllText(output) == "original", "Apply should copy file contents.");
        Check(!File.Exists(Path.Combine(target, Path.GetFileName(source), "文件.txt.Zone.Identifier")), "Zone.Identifier must not be copied.");

        // Read-only destination overwrite test
        File.SetAttributes(output, FileAttributes.ReadOnly);
        File.WriteAllText(input, "updated");
        var replaceReadOnly = FileCopy.Preview(source, target, replace: true, skipDownloadMark: true);
        FileCopy.Apply(replaceReadOnly, skipDownloadMark: true, _ => { });
        Check(File.ReadAllText(output) == "updated", "Apply should replace read-only destination files.");

        var repeat = FileCopy.Preview(source, target, replace: false, skipDownloadMark: true);
        Check(repeat.Any(entry => entry.Action == CopyAction.SkipExisting), "Repeated copy should skip existing files.");
        FileCopy.Apply(repeat, skipDownloadMark: true, _ => { });

        var staleSource = FileCopy.Preview(source, target, replace: true, skipDownloadMark: true);
        File.WriteAllText(input, "source changed");
        Reject<IOException>(() => FileCopy.Apply(staleSource, true, _ => { }));
        Check(File.ReadAllText(output) == "updated", "A changed source must preserve the target.");

        var staleTarget = FileCopy.Preview(source, target, replace: true, skipDownloadMark: true);
        File.WriteAllText(output, "target changed");
        Reject<IOException>(() => FileCopy.Apply(staleTarget, true, _ => { }));
        Check(File.ReadAllText(output) == "target changed", "A changed target must be preserved.");

        Reject<IOException>(() => FileCopy.Preview(source, source, true, false));
        Reject<FileNotFoundException>(() => FileCopy.Preview(Path.Combine(root, "missing.txt"), target, true, false));
        Reject<OperationCanceledException>(() => FileCopy.Preview(source, target, true, false, new CancellationToken(true)));

        Check(FileCopy.ResolvePath("/home/user/a", "Ubuntu") == @"\\wsl.localhost\Ubuntu\home\user\a", "WSL ResolvePath.");
        Reject<ArgumentException>(() => FileCopy.ResolvePath("/home/../a", "Ubuntu"));
        Reject<ArgumentException>(() => FileCopy.ResolvePath("relative/path", null));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 4. WslOutputParser tests (UTF-16LE Chinese '上' U+4E0A = 0A 4E + mixed UTF-8 + streaming chunks)
{
    var mixed = new List<byte>();
    // UTF-16LE BOM + line with '上' (0A 4E) in middle
    mixed.AddRange(new byte[] { 0xFF, 0xFE });
    mixed.AddRange(Encoding.Unicode.GetBytes("调整磁盘上限\r\n"));
    // UTF-8 e2fsck output
    mixed.AddRange(Encoding.UTF8.GetBytes("e2fsck 1.47.0 (5-Feb-2023)\nPass 1: Checking inodes\n"));
    // UTF-16LE starting directly with '上' (0A 4E) without BOM!
    mixed.AddRange(Encoding.Unicode.GetBytes("上限已成功更新。\r\n"));
    // UTF-8 trailing line without newline
    mixed.AddRange(Encoding.UTF8.GetBytes("Resizing filesystem done."));

    byte[] payload = mixed.ToArray();
    string decodedFull = WslOutputParser.DecodeResizeOutput(payload);
    string[] expectedLines =
    {
        "调整磁盘上限",
        "e2fsck 1.47.0 (5-Feb-2023)",
        "Pass 1: Checking inodes",
        "上限已成功更新。",
        "Resizing filesystem done."
    };
    Check(decodedFull == string.Join("\n", expectedLines), $"Mixed resize decode mismatch:\n{decodedFull}");

    // Streaming chunk-by-chunk (3 bytes at a time)
    var decoder = new WslOutputParser.IncrementalResizeDecoder();
    var streamed = new List<string>();
    for (int i = 0; i < payload.Length; i += 3)
        decoder.Append(payload.AsSpan(i, Math.Min(3, payload.Length - i)), streamed.Add);
    decoder.Flush(streamed.Add);
    Check(streamed.SequenceEqual(expectedLines), $"Streamed resize decode mismatch:\n{string.Join("\n", streamed)}");

    // Multi-locale distribution list parsing
    string listText = """
      NAME            STATE           VERSION
    * Ubuntu          Running         2
      Debian          已停止          2
      Arch            執行中          2
      Alpine          実行中          1
    """;
    var rows = WslOutputParser.ParseDistributionList(listText);
    Check(rows.Count == 4, $"Expected 4 distros, got {rows.Count}.");
    Check(rows[0].IsDefault && rows[0].IsRunning && rows[0].StateLabel == "运行中", "Ubuntu row.");
    Check(!rows[1].IsRunning && rows[1].StateLabel == "已停止", "Debian row.");
    Check(rows[2].IsRunning && rows[2].StateLabel == "运行中", "Traditional Chinese running row.");
    Check(rows[3].IsRunning && rows[3].Version == 1, "Japanese running WSL 1 row.");
    Check(WslOutputParser.IsSystemUtilityDistribution("docker-desktop-data"), "Docker data utility check.");
}

// 5. AppDataStore tests (legacy migration from v0.3 & v0.2 + pending push persistence + preferences + log pruning)
foreach (string legacyDirName in AppDataStore.LegacyDataDirectoryNames)
{
    string fakeLocalAppData = Directory.CreateTempSubdirectory("sitelift-appdata-").FullName;
    try
    {
        string legacy = Path.Combine(fakeLocalAppData, legacyDirName);
        Directory.CreateDirectory(legacy);
        Guid pid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(legacy, "projects.json"), "[]");
        string oldToken = $"{new string('a', 40)}:{new string('b', 40)}:{new string('c', 40)}";
        File.WriteAllText(Path.Combine(legacy, $"pending-{pid:N}-DEADBEAF.txt"), oldToken);

        Check(AppDataStore.MigrateLegacyDirectoryIfNeeded(fakeLocalAppData), $"Legacy directory '{legacyDirName}' should migrate.");
        var store = new AppDataStore(fakeLocalAppData);
        Check(File.Exists(store.ProjectsFilePath), $"projects.json from '{legacyDirName}' should exist in new SiteLift directory.");

        var loadedPending = store.LoadPendingPush(pid);
        Check(loadedPending is not null && loadedPending.ToToken() == oldToken, "Legacy pending file should migrate and load.");
        Check(File.Exists(store.PendingPushPath(pid)), "New pending-{id}.json should be created.");

        store.SavePreferences(new AppPreferences("Dark"));
        Check(store.LoadPreferences().Theme == "Dark", "Theme preference should persist.");

        Directory.CreateDirectory(store.DiagnosticsDirectory);
        string ancientLog = Path.Combine(store.DiagnosticsDirectory, "sitelift-2020-01-01.log");
        string ancientLegacyLog = Path.Combine(store.DiagnosticsDirectory, "projectsync-2020-01-01.log");
        File.WriteAllText(ancientLog, "old");
        File.WriteAllText(ancientLegacyLog, "old legacy");
        store.AppendDiagnostic("hello");
        Check(!File.Exists(ancientLog), "SiteLift logs older than retention window should be pruned.");
        Check(!File.Exists(ancientLegacyLog), "Legacy ProjectSync logs older than retention window should be pruned.");

        store.DeleteAllProjectState(pid);
        Check(store.LoadPendingPush(pid) is null, "Pending state should be deleted.");
    }
    finally
    {
        Directory.Delete(fakeLocalAppData, recursive: true);
    }
}

// 6. SyncEngine payload, parser, and real temporary Git repository integration tests
{
    var fields = SyncEngine.BuildWslPayloadFields(
        @"D:\repo 空格", new string('a', 40), "/home/u/repo",
        "echo \"hello world\" && python3 -c \"print('ok')\"",
        "/home/u/.nvm/bin", "false", "main", "",
        new[] { "docs/notices" }, new[] { "docs", ":(exclude,literal)docs/cache" });
    Check(fields[3] == "echo \"hello world\" && python3 -c \"print('ok')\"", "Quoted buildCommand must survive intact.");

    using var ms = new MemoryStream();
    void WriteTok(string s) { ms.Write(Encoding.UTF8.GetBytes(s)); ms.WriteByte(0); }
    WriteTok("SNAPSHOT"); WriteTok(new string('1', 40));
    WriteTok("CHANGE"); WriteTok("A"); WriteTok("docs/new file.md");
    WriteTok("CHANGE"); WriteTok("D"); WriteTok("docs/old.md");
    WriteTok("UNTRACKED_DELETE"); WriteTok("docs/old.md");
    WriteTok("TREES"); WriteTok($"{new string('2', 40)}:{new string('3', 40)}");
    var parsed = SyncEngine.ParseWslResultPayload(ms.ToArray());
    Check(parsed.Snapshot == new string('1', 40), "Parsed snapshot.");
    Check(parsed.Changes.Length == 2 && parsed.Changes[0].Path == "docs/new file.md", "Parsed changes.");
    Check(parsed.UntrackedDeletions.SequenceEqual(new[] { "docs/old.md" }), "Parsed untracked deletions.");

    // Real local Git tests
    string version = await SyncEngine.CheckWindowsGitAsync();
    Check(version.Contains("git version", StringComparison.OrdinalIgnoreCase), $"Unexpected git version: {version}");

    string gitRoot = Directory.CreateTempSubdirectory("sitelift-git-").FullName;
    try
    {
        string bare = Directory.CreateDirectory(Path.Combine(gitRoot, "remote.git")).FullName;
        string work = Directory.CreateDirectory(Path.Combine(gitRoot, "work")).FullName;
        await RunGit(bare, "init", "--bare", "-b", "main");
        await RunGit(work, "init", "-b", "main");
        await RunGit(work, "config", "user.name", "Test User");
        await RunGit(work, "config", "user.email", "test@example.invalid");
        await RunGit(work, "remote", "add", "origin", bare);
        File.WriteAllText(Path.Combine(work, "README.md"), "v1\n");
        await RunGit(work, "add", ".");
        await RunGit(work, "commit", "-m", "init");
        await RunGit(work, "push", "-u", "origin", "main");

        string baseCommit = (await ProcessRunner.RunAsync(new ProcessStartInfo(ProcessRunner.FindGitExecutable()!)
        {
            WorkingDirectory = work,
            ArgumentList = { "rev-parse", "HEAD" }
        })).Stdout.Trim();

        var cfg = new ProjectConfig(
            Name: "Test", WindowsRepo: work, Remote: "origin", RemoteUrl: "", Branch: "main",
            GithubUsername: "", GitSshCommand: "", Distro: "Ubuntu", WslUser: "u", WslRepo: "/home/u/repo",
            NodeBin: "", BuildCommand: "true", PreviewCommand: "", PreviewAfterBuild: false,
            ReinstallCommand: "", ThemeUpgradeCommand: "", PullPaths: Array.Empty<string>(),
            Include: new[] { "README.md" }, Exclude: Array.Empty<string>(), ExcludeSuffixes: Array.Empty<string>(),
            CleanUntracked: false);

        var remotes = await SyncEngine.ListRemotesAsync(cfg);
        Check(remotes.SequenceEqual(new[] { "origin" }), "ListRemotesAsync should find origin.");

        var inspectedRepo = await SyncEngine.InspectLocalRepositoryAsync(work);
        Check(inspectedRepo.IsGitRepository && !inspectedRepo.IsInsideAnotherRepository,
            "InspectLocalRepositoryAsync should identify valid Git repository root.");
        Check(inspectedRepo.CurrentBranch == "main",
            $"InspectLocalRepositoryAsync expected branch 'main', got '{inspectedRepo.CurrentBranch}'.");
        Check(inspectedRepo.Remotes.Length == 1 && inspectedRepo.Remotes[0].Name == "origin" &&
            inspectedRepo.Remotes[0].Url == bare,
            "InspectLocalRepositoryAsync should return origin name and URL.");

        string subDir = Directory.CreateDirectory(Path.Combine(work, "nested")).FullName;
        var inspectedSub = await SyncEngine.InspectLocalRepositoryAsync(subDir);
        Check(!inspectedSub.IsGitRepository && inspectedSub.IsInsideAnotherRepository,
            "InspectLocalRepositoryAsync should detect subdirectory inside another Git repository.");

        string plainDir = Directory.CreateDirectory(Path.Combine(gitRoot, "plain")).FullName;
        var inspectedPlain = await SyncEngine.InspectLocalRepositoryAsync(plainDir);
        Check(!inspectedPlain.IsGitRepository && !inspectedPlain.IsInsideAnotherRepository && inspectedPlain.Remotes.Length == 0,
            "InspectLocalRepositoryAsync should identify non-Git directory.");

        var branches = await SyncEngine.TestConnectionAsync(cfg);
        Check(branches.SequenceEqual(new[] { "main" }), "TestConnectionAsync should list main.");

        // Reject invalid branch & invalid remote name smuggling
        var badBranchCfg = cfg with { Branch = "-evil" };
        await RejectAsync<SyncException>(() => SyncEngine.PublishAsync(badBranchCfg, $"{baseCommit}:{baseCommit}", "msg"));
        var badRemoteCfg = cfg with { Remote = "https://github.com/a/b" };
        await RejectAsync<SyncException>(() => SyncEngine.TestConnectionAsync(badRemoteCfg));

        // Publish with no changes
        var noChangePub = await SyncEngine.PublishAsync(cfg, $"{baseCommit}:{baseCommit}", "empty");
        Check(!noChangePub.Pushed, "Publish with no changes should report Pushed=false.");

        // Publish with real modification and verify pending callback + push
        File.WriteAllText(Path.Combine(work, "README.md"), "v2\n");
        var status = await SyncEngine.ReadWorkingTreeStatusAsync(work);
        Check(status.Length == 1 && status[0].Contains("README.md"), "Working tree status should show README.md.");

        PendingPushRecord? savedPending = null;
        var pub = await SyncEngine.PublishAsync(cfg, $"{baseCommit}:{baseCommit}", "Update README",
            onPendingCommit: rec => savedPending = rec);
        Check(pub.Pushed && pub.CommitHash is not null && savedPending is not null, "Publish should commit and push.");

        // Idempotent retry push on already pushed commit
        var retry = await SyncEngine.PublishAsync(cfg, savedPending!.ToToken(), null);
        Check(retry.Pushed && retry.CommitHash == pub.CommitHash, "Retry push on already pushed commit should succeed.");
    }
    finally
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(gitRoot, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(entry);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(gitRoot, recursive: true);
    }
}

// 7. SiteFrameworkDetector tests
{
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"devDependencies":{"vuepress":"2.0.0","vuepress-theme-plume":"1.0.0"}}""") == SiteFramework.VuePress, "VuePress package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"devDependencies":{"vitepress":"1.6.0"}}""") == SiteFramework.VitePress, "VitePress package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"dependencies":{"@docusaurus/core":"3.0.0"}}""") == SiteFramework.Docusaurus, "Docusaurus package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"dependencies":{"astro":"5.0.0"}}""") == SiteFramework.Astro, "Astro package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"dependencies":{"hexo":"7.0.0"}}""") == SiteFramework.Hexo, "Hexo package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"devDependencies":{"rspress":"1.0.0"}}""") == SiteFramework.Rspress, "Rspress package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"dependencies":{"nuxt":"3.0.0"}}""") == SiteFramework.Nuxt, "Nuxt package.json detection.");
    Check(SiteFrameworkDetector.DetectFromPackageJsonContent("""{"dependencies":{"next":"15.0.0"}}""") == SiteFramework.NextJs, "Next.js package.json detection.");

    Check(SiteFrameworkDetector.DetectFromConfiguration("""{"exclude":["docs/.vuepress/.temp"],"themeUpgradeCommand":"pnpm dlx vp-update"}""") == SiteFramework.VuePress, "VuePress config hint detection.");
    Check(SiteFrameworkDetector.DetectFromConfiguration("""{"exclude":["docs/.vitepress/cache"],"buildCommand":"pnpm docs:build"}""") == SiteFramework.VitePress, "VitePress config hint detection.");
    Check(SiteFrameworkDetector.DetectFromConfiguration("""{"buildCommand":"hugo --minify"}""") == SiteFramework.Hugo, "Hugo config hint detection.");
    Check(SiteFrameworkDetector.DetectFromConfiguration("""{"buildCommand":"mkdocs build"}""") == SiteFramework.MkDocs, "MkDocs config hint detection.");
    Check(SiteFrameworkDetector.DetectFromConfiguration("""{"buildCommand":"echo custom"}""") == SiteFramework.Generic, "Unknown project falls back to Generic.");

    string fwRoot = Directory.CreateTempSubdirectory("sitelift-fw-").FullName;
    try
    {
        string vpDir = Directory.CreateDirectory(Path.Combine(fwRoot, "vp")).FullName;
        Directory.CreateDirectory(Path.Combine(vpDir, "docs", ".vitepress"));
        Check(SiteFrameworkDetector.DetectFromDirectory(vpDir) == SiteFramework.VitePress, "VitePress directory detection.");

        string hugoDir = Directory.CreateDirectory(Path.Combine(fwRoot, "hugo")).FullName;
        File.WriteAllText(Path.Combine(hugoDir, "hugo.toml"), "baseURL = 'https://example.org/'\n");
        Check(SiteFrameworkDetector.DetectFromDirectory(hugoDir) == SiteFramework.Hugo, "Hugo directory detection.");

        string mkDir = Directory.CreateDirectory(Path.Combine(fwRoot, "mk")).FullName;
        File.WriteAllText(Path.Combine(mkDir, "mkdocs.yml"), "site_name: Demo\n");
        Check(SiteFrameworkDetector.DetectFromDirectory(mkDir) == SiteFramework.MkDocs, "MkDocs directory detection.");
    }
    finally
    {
        Directory.Delete(fwRoot, recursive: true);
    }
}

// 8. AppUpdater tests
{
    string releaseJson = """
    {
      "tag_name": "v0.6.0",
      "html_url": "https://github.com/nrjycyd/sitelift/releases/tag/v0.6.0",
      "assets": [
        {
          "name": "SiteLift-v0.6.0-win-x64.zip",
          "size": 3145728,
          "browser_download_url": "https://github.com/nrjycyd/sitelift/releases/download/v0.6.0/SiteLift-v0.6.0-win-x64.zip"
        },
        {
          "name": "SHA256SUMS",
          "size": 96,
          "browser_download_url": "https://github.com/nrjycyd/sitelift/releases/download/v0.6.0/SHA256SUMS"
        }
      ]
    }
    """;
    var parsedRelease = AppUpdater.ParseLatestRelease(releaseJson);
    Check(parsedRelease is not null && parsedRelease.Version == new Version(0, 6, 0), "ParseLatestRelease should parse version.");
    Check(parsedRelease!.CanInPlaceUpdate, "Release with zip and SHA256SUMS should support in-place update.");
    Check(parsedRelease.PackageAsset!.SizeBytes == 3145728, "Package asset size should be parsed.");
    Check(parsedRelease.ReleaseUrl == "https://github.com/nrjycyd/sitelift/releases/tag/v0.6.0", "Release html_url should be preserved.");

    string untrustedJson = """
    {
      "tag_name": "v0.6.0",
      "html_url": "https://evil.example/releases",
      "assets": [
        {
          "name": "SiteLift-v0.6.0-win-x64.zip",
          "size": 100,
          "browser_download_url": "https://evil.example/SiteLift-v0.6.0-win-x64.zip"
        },
        {
          "name": "SHA256SUMS",
          "size": 96,
          "browser_download_url": "http://github.com/nrjycyd/sitelift/releases/download/v0.6.0/SHA256SUMS"
        }
      ]
    }
    """;
    var untrustedRelease = AppUpdater.ParseLatestRelease(untrustedJson);
    Check(untrustedRelease is not null && !untrustedRelease.CanInPlaceUpdate, "Untrusted or HTTP download URLs must be rejected.");
    Check(untrustedRelease!.ReleaseUrl == AppUpdater.ReleasesUrl, "Untrusted html_url must fall back to default ReleasesUrl.");

    Check(AppUpdater.ParseLatestRelease("""{"tag_name":"v1.2.3.4"}""") is null, "Four-part version tag should be rejected.");
    Check(AppUpdater.ParseLatestRelease("""{"tag_name":"invalid"}""") is null, "Invalid tag should be rejected.");

    string updRoot = Directory.CreateTempSubdirectory("sitelift-upd-").FullName;
    try
    {
        // SHA-256 verification
        string sampleFile = Path.Combine(updRoot, "sample.zip");
        File.WriteAllText(sampleFile, "hello sitelift update", Encoding.UTF8);
        string actualHash = AppUpdater.ComputeFileSha256(sampleFile);
        string sums = $"{actualHash}  SiteLift-v0.6.0-win-x64.zip\n";
        Check(AppUpdater.ParseExpectedSha256(sums, "SiteLift-v0.6.0-win-x64.zip") == actualHash, "ParseExpectedSha256 should match exact asset name.");
        Check(AppUpdater.ParseExpectedSha256(sums, "other.zip") is null, "ParseExpectedSha256 should return null for missing file.");
        Check(AppUpdater.VerifyFileSha256(sampleFile, actualHash), "VerifyFileSha256 should succeed for matching hash.");
        Check(!AppUpdater.VerifyFileSha256(sampleFile, new string('0', 64)), "VerifyFileSha256 should fail for wrong hash.");

        // Synthetic package extraction & validation
        static string CreateTestZip(string zipPath, IEnumerable<(string EntryName, string Content)> entries)
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            return zipPath;
        }

        string validZip = CreateTestZip(Path.Combine(updRoot, "valid.zip"), new[]
        {
            ("SiteLift/SiteLift.App.exe", "new-exe"),
            ("SiteLift/SiteLift.App.dll", "new-dll"),
            ("SiteLift/SiteLift.Core.dll", "new-core"),
            ("SiteLift/SiteLift.App.deps.json", "{}"),
            ("SiteLift/SiteLift.App.runtimeconfig.json", "{}"),
            ("SiteLift/licenses/WPF-UI-LICENSE.md", "MIT")
        });
        string validStage = Directory.CreateDirectory(Path.Combine(updRoot, "stage-valid")).FullName;
        var staged = AppUpdater.ExtractAndVerifyPackage(
            validZip, validStage, Path.Combine(validStage, "extracted"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0));
        Check(staged.RelativeFiles.Contains("licenses/WPF-UI-LICENSE.md"), "Valid package extraction should list nested files.");

        Reject<UpdateException>(() => AppUpdater.ExtractAndVerifyPackage(
            validZip, validStage, Path.Combine(updRoot, "extract-mismatch"),
            new Version(0, 6, 0), _ => new Version(0, 5, 3, 0)));

        string slipZip = CreateTestZip(Path.Combine(updRoot, "slip.zip"), new[]
        {
            ("SiteLift/../evil.dll", "pwned")
        });
        Reject<UpdateException>(() => AppUpdater.ExtractAndVerifyPackage(
            slipZip, validStage, Path.Combine(updRoot, "extract-slip"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0)));

        string badRootZip = CreateTestZip(Path.Combine(updRoot, "badroot.zip"), new[]
        {
            ("WrongRoot/SiteLift.App.exe", "exe")
        });
        Reject<UpdateException>(() => AppUpdater.ExtractAndVerifyPackage(
            badRootZip, validStage, Path.Combine(updRoot, "extract-badroot"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0)));

        string missingReqZip = CreateTestZip(Path.Combine(updRoot, "missing.zip"), new[]
        {
            ("SiteLift/SiteLift.App.exe", "exe")
        });
        Reject<UpdateException>(() => AppUpdater.ExtractAndVerifyPackage(
            missingReqZip, validStage, Path.Combine(updRoot, "extract-missing"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0)));

        // Install directory inspection
        string portableDir = Directory.CreateDirectory(Path.Combine(updRoot, "更新测试 空格")).FullName;
        File.WriteAllText(Path.Combine(portableDir, "SiteLift.App.exe"), "old-exe");
        var portableStatus = AppUpdater.InspectInstallDirectory(portableDir);
        Check(portableStatus.CanInPlaceUpdate && !portableStatus.RequiresElevation, "Writable portable dir should allow non-elevated update.");

        string devDir = Directory.CreateDirectory(Path.Combine(updRoot, "dev-dir")).FullName;
        File.WriteAllText(Path.Combine(devDir, "SiteLift.App.exe"), "dev-exe");
        File.WriteAllText(Path.Combine(devDir, "SiteLift.App.pdb"), "pdb");
        var devStatus = AppUpdater.InspectInstallDirectory(devDir);
        Check(!devStatus.CanInPlaceUpdate, "Dev directory with PDB should block in-place update.");

        string emptyDir = Directory.CreateDirectory(Path.Combine(updRoot, "empty-dir")).FullName;
        Check(!AppUpdater.InspectInstallDirectory(emptyDir).CanInPlaceUpdate, "Directory without exe should block in-place update.");

        // End-to-end updater script: apply & preserve user files
        string updatesStore = Directory.CreateDirectory(Path.Combine(updRoot, "updates-store")).FullName;
        string logsStore = Directory.CreateDirectory(Path.Combine(updRoot, "logs-store")).FullName;
        string targetDir = Directory.CreateDirectory(Path.Combine(updRoot, "安装目录 测试")).FullName;
        File.WriteAllText(Path.Combine(targetDir, "SiteLift.App.exe"), "old-exe");
        File.WriteAllText(Path.Combine(targetDir, "SiteLift.App.dll"), "old-dll");
        File.WriteAllText(Path.Combine(targetDir, "SiteLift.Core.dll"), "old-core");
        File.WriteAllText(Path.Combine(targetDir, "SiteLift.App.deps.json"), "old-deps");
        File.WriteAllText(Path.Combine(targetDir, "SiteLift.App.runtimeconfig.json"), "old-rt");
        File.WriteAllText(Path.Combine(targetDir, "user-custom.txt"), "keep-me");

        string runStage = Directory.CreateDirectory(Path.Combine(updatesStore, "v0.6.0-run1")).FullName;
        var stagedForRun = AppUpdater.ExtractAndVerifyPackage(
            validZip, runStage, Path.Combine(runStage, "extracted"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0));

        var (scriptPath, planPath) = AppUpdater.PrepareApplyScript(
            stagedForRun, targetDir, updatesStore, logsStore,
            currentProcessId: 0, new Version(0, 5, 3), elevated: false, relaunch: false);

        var applyResult = await ProcessRunner.RunAsync(AppUpdater.BuildUpdaterStartInfo(scriptPath, planPath, elevated: false));
        Check(applyResult.ExitCode == 0, $"Apply script should succeed, got exit {applyResult.ExitCode}: {applyResult.CombinedOutput}");
        Check(File.ReadAllText(Path.Combine(targetDir, "SiteLift.App.exe")) == "new-exe", "Target exe should be updated.");
        Check(File.ReadAllText(Path.Combine(targetDir, "licenses", "WPF-UI-LICENSE.md")) == "MIT", "New nested file should be copied.");
        Check(File.ReadAllText(Path.Combine(targetDir, "user-custom.txt")) == "keep-me", "Unrelated user files should be preserved.");

        var successOutcome = AppUpdater.ConsumeUpdateOutcomeAndCleanup(updatesStore);
        Check(successOutcome is { Succeeded: true, TargetVersion: "0.6.0", PreviousVersion: "0.5.3" }, "Outcome should record successful update.");
        Check(!Directory.Exists(runStage), "Staging directory should be cleaned up on startup.");

        // End-to-end updater script: rollback when a target file is locked
        string rollbackTarget = Directory.CreateDirectory(Path.Combine(updRoot, "rollback-target")).FullName;
        File.WriteAllText(Path.Combine(rollbackTarget, "SiteLift.App.deps.json"), "old-deps");
        File.WriteAllText(Path.Combine(rollbackTarget, "SiteLift.App.dll"), "old-dll");
        File.WriteAllText(Path.Combine(rollbackTarget, "SiteLift.App.exe"), "old-exe");
        File.WriteAllText(Path.Combine(rollbackTarget, "SiteLift.App.runtimeconfig.json"), "old-rt");
        File.WriteAllText(Path.Combine(rollbackTarget, "SiteLift.Core.dll"), "old-core");

        string rollbackStage = Directory.CreateDirectory(Path.Combine(updatesStore, "v0.6.0-rollback")).FullName;
        var stagedForRollback = AppUpdater.ExtractAndVerifyPackage(
            validZip, rollbackStage, Path.Combine(rollbackStage, "extracted"),
            new Version(0, 6, 0), _ => new Version(0, 6, 0, 0));

        var (rbScript, rbPlan) = AppUpdater.PrepareApplyScript(
            stagedForRollback, rollbackTarget, updatesStore, logsStore,
            currentProcessId: 0, new Version(0, 5, 3), elevated: false, relaunch: false);

        using (var lockStream = new FileStream(Path.Combine(rollbackTarget, "SiteLift.Core.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rbResult = await ProcessRunner.RunAsync(AppUpdater.BuildUpdaterStartInfo(rbScript, rbPlan, elevated: false));
            Check(rbResult.ExitCode != 0, "Apply script should fail when a target file is exclusively locked.");
        }

        Check(File.ReadAllText(Path.Combine(rollbackTarget, "SiteLift.App.exe")) == "old-exe", "Previously overwritten file must be rolled back.");
        Check(File.ReadAllText(Path.Combine(rollbackTarget, "SiteLift.App.dll")) == "old-dll", "Previously overwritten dll must be rolled back.");
        Check(!File.Exists(Path.Combine(rollbackTarget, "licenses", "WPF-UI-LICENSE.md")), "Newly created file must be removed on rollback.");

        var failedOutcome = AppUpdater.ConsumeUpdateOutcomeAndCleanup(updatesStore);
        Check(failedOutcome is { Succeeded: false, TargetVersion: "0.6.0" }, "Failed outcome should be recorded.");
    }
    finally
    {
        AppUpdater.TryDeleteDirectory(updRoot);
    }
}

// 9. WslDevelopmentEnvironment tests
{
    string probeSample = """
        nvm	1
        dltool	1
        git	git version 2.43.0
        node	v22.14.0
        pnpm	10.6.3
        python3	Python 3.12.3
        pypip	0
        pyvenv	0
        pipx	0
        hugo	hugo v0.145.0-666444f0a52132f9fec9f71cf25b441cc6a4f355+extended linux/amd64 BuildDate=2025-02-26T15:41:25Z
        go	go version go1.24.1 linux/amd64
        ruby	ruby 3.2.3 (2024-01-18 revision 52bb2ac0a6) [x86_64-linux-gnu]
        bundler	0
        mdbook	mdbook v0.4.48
        rustc	rustc 1.85.0 (4d91de4e4 2025-02-17)
        """;
    var probe = WslDevelopmentEnvironment.ParseProbeOutput(probeSample);
    Check(probe.GitVersion == "2.43.0", "Probe should parse Git version.");
    Check(probe.HasNvm && probe.NodeVersion == "22.14.0" && probe.PnpmVersion == "10.6.3", "Probe should parse Node/pnpm/nvm.");
    Check(probe.PythonVersion == "3.12.3" && !probe.PythonReadyForMkDocs, "Python without pip/venv should not be ready for MkDocs.");
    Check(probe.HugoVersion == "0.145.0" && probe.HugoIsExtended && probe.HugoReady, "Probe should parse Hugo Extended.");
    Check(probe.GoVersion == "1.24.1", "Probe should parse Go version.");
    Check(probe.RubyVersion == "3.2.3" && !probe.RubyReadyForJekyll, "Ruby without bundler should not be ready for Jekyll.");
    Check(probe.MdBookVersion == "0.4.48" && probe.RustVersion == "1.85.0", "Probe should parse mdBook and Rust.");

    // Preset activation glow states
    var linked = new[] { SiteFramework.VitePress, SiteFramework.Hugo };
    Check(WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Node, probe, linked) == PresetActivation.Active, "Node preset should be Active.");
    Check(WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Hugo, probe, linked) == PresetActivation.Active, "Hugo preset should be Active.");
    Check(WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.MkDocs, probe, linked) == PresetActivation.Partial, "MkDocs preset should be Partial when pip/venv missing.");
    Check(WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.Jekyll, probe, linked) == PresetActivation.Partial, "Jekyll preset should be Partial when bundler missing.");
    Check(WslDevelopmentEnvironment.GetPresetActivation(WslEnvironmentPreset.MdBook, probe, linked) == PresetActivation.Active, "mdBook preset should be Active.");

    // Dynamic toolchain filtering per preset
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.Hugo, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.Hugo }), "Hugo preset must show only Git and Hugo.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.Node, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.Node }), "Node preset must show only Git and Node.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.MkDocs, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.Python }), "MkDocs preset must show only Git and Python.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.Jekyll, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.Ruby }), "Jekyll preset must show only Git and Ruby.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.MdBook, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.MdBook }), "mdBook preset must show only Git and mdBook.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.Recommended, probe, linked)
        .SequenceEqual(new[] { WslToolGroup.Git, WslToolGroup.Node, WslToolGroup.Hugo }), "Recommended preset should match linked frameworks.");
    Check(WslDevelopmentEnvironment.GetVisibleGroups(WslEnvironmentPreset.Custom, probe, linked).Count == 6, "Custom preset must show all 6 groups.");

    // Preset selection skips already-installed tools and fills missing dependencies
    var partialProbe = new WslToolProbeResult(GitVersion: "2.43.0", PythonVersion: "3.12.3", HasPythonPip: false, HasPythonVenv: false, HasDownloadTool: true);
    var mkSel = WslDevelopmentEnvironment.BuildPresetSelection(WslEnvironmentPreset.MkDocs, partialProbe, Array.Empty<SiteFramework>());
    Check(!mkSel.Git && mkSel.Python && mkSel.PythonPipVenv && mkSel.PythonPipx, "MkDocs preset should skip installed Git and select missing pip/venv/pipx.");

    var nodeOnlySel = new WslDevelopmentSelection(Node: true, Pnpm: true, Corepack: true);
    Check(!WslDevelopmentEnvironment.RequiresSudo(nodeOnlySel, partialProbe), "Node/pnpm/Corepack with curl present should not require sudo.");
    Check(WslDevelopmentEnvironment.RequiresSudo(new WslDevelopmentSelection(Hugo: true), partialProbe), "Installing Hugo to /usr/local/bin should require sudo.");

    // Version validation rejects injection
    Check(!WslDevelopmentEnvironment.IsValidSelection(new WslDevelopmentSelection(Hugo: true, HugoVersion: "v0.145.0; rm -rf /")), "Hugo version must reject shell injection.");
    Check(!WslDevelopmentEnvironment.IsValidSelection(new WslDevelopmentSelection(Go: true, GoVersion: "$(whoami)")), "Go version must reject shell injection.");
    Check(!WslDevelopmentEnvironment.IsValidSelection(new WslDevelopmentSelection(MdBook: true, MdBookVersion: "../evil")), "mdBook version must reject invalid tags.");

    // Setup script generation covers all toolchains
    var fullSel = new WslDevelopmentSelection(
        Git: true, Node: true, Pnpm: true, Corepack: true,
        Python: true, PythonPipVenv: true, PythonPipx: true, PythonAlias: true,
        Hugo: true, HugoVersion: "v0.145.0", Go: true, GoVersion: "1.24.1",
        Ruby: true, MdBook: true, MdBookVersion: "v0.4.48", Rust: true);
    var (setupScript, setupArgs) = WslDevelopmentEnvironment.BuildSetupCommand(fullSel);
    Check(setupArgs.Contains("python3-pip") && setupArgs.Contains("python3-venv") && setupArgs.Contains("pipx"), "Script args should include Python MkDocs packages.");
    Check(setupArgs.Contains("ruby-full") && setupArgs.Contains("build-essential") && setupArgs.Contains("zlib1g-dev"), "Script args should include Jekyll native deps.");
    Check(setupScript.Contains("hugo_extended_"), "Script should install Hugo Extended.");
    Check(setupScript.Contains("/usr/local/bin/go"), "Script should symlink Go into /usr/local/bin.");
    Check(setupScript.Contains("GEM_HOME"), "Script should configure user-level GEM_HOME for Bundler.");
    Check(setupScript.Contains("/usr/local/bin/mdbook"), "Script should install mdBook binary into /usr/local/bin.");
    Check(setupScript.Contains("corepack enable"), "Script should enable Corepack.");
    Check(!WslDevelopmentEnvironment.ProbeScript.Contains('\r'), "ProbeScript must use LF-only line endings for WSL bash.");
    Check(!setupScript.Contains('\r'), "BuildSetupCommand script must use LF-only line endings for WSL bash.");
    Check(setupScript.Contains("sh.rustup.rs"), "Script should install Rust via rustup.");
}

Console.WriteLine("All SiteLift.Core unit and Git integration checks passed.");

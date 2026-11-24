using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private int localInspectSequence;
    private CancellationTokenSource? localInspectCts;
    private LocalRepositoryInspection? lastLocalInspection;
    private string lastInspectedPath = "";
    private readonly Dictionary<string, string> localRemoteUrls = new(StringComparer.Ordinal);

    private static bool HasGitMetadata(string path)
    {
        string metadata = Path.Combine(path, ".git");
        return Directory.Exists(metadata) || File.Exists(metadata);
    }

    private void CancelLocalInspection()
    {
        localInspectSequence++;
        localInspectCts?.Cancel();
        localInspectCts?.Dispose();
        localInspectCts = null;
        if (LocalInspectRing is not null)
            LocalInspectRing.Visibility = Visibility.Collapsed;
    }

    private void ResetLocalInspectionState()
    {
        CancelLocalInspection();
        lastLocalInspection = null;
        lastInspectedPath = "";
        localRemoteUrls.Clear();
    }

    private bool HasFreshLocalInspection(string path) =>
        lastLocalInspection is not null &&
        string.Equals(lastInspectedPath, path, StringComparison.OrdinalIgnoreCase);

    private bool IsLocalDirectoryGitRepository(string path)
    {
        if (HasFreshLocalInspection(path))
            return lastLocalInspection!.IsGitRepository;
        return Path.IsPathFullyQualified(path) && Directory.Exists(path) && HasGitMetadata(path);
    }

    private void UpdateRepositoryStateUi()
    {
        string path = windowsRepo.Text.Trim();
        bool hasPath = path.Length > 0;
        bool hasValidDir = hasPath && Path.IsPathFullyQualified(path) && Directory.Exists(path);
        bool freshInspection = hasValidDir && HasFreshLocalInspection(path);
        bool isGitRepo = freshInspection
            ? lastLocalInspection!.IsGitRepository
            : hasValidDir && HasGitMetadata(path);
        bool isInsideAnotherRepo = freshInspection && lastLocalInspection!.IsInsideAnotherRepository;

        bool hasFiles = false;
        bool accessError = false;
        if (hasValidDir && !isGitRepo && !isInsideAnotherRepo)
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                hasFiles = entries.MoveNext();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                accessError = true;
            }
        }

        // 1. Local directory status badge & summary
        if (!hasPath)
        {
            LocalRepoBadge.Content = "未设置";
            LocalRepoBadge.Appearance = ControlAppearance.Secondary;
            LocalRepoSummary.Text = "尚未选择本地目录，请通过「浏览…」选择 Windows 项目目录。";
        }
        else if (!hasValidDir)
        {
            LocalRepoBadge.Content = "目录不存在";
            LocalRepoBadge.Appearance = ControlAppearance.Caution;
            LocalRepoSummary.Text = "目录不存在或路径不完整，请通过「浏览…」选择现有的 Windows 目录。";
        }
        else if (accessError)
        {
            LocalRepoBadge.Content = "无法访问";
            LocalRepoBadge.Appearance = ControlAppearance.Danger;
            LocalRepoSummary.Text = "无法读取该目录内容，请检查目录权限。";
        }
        else if (isInsideAnotherRepo)
        {
            LocalRepoBadge.Content = "非仓库根目录";
            LocalRepoBadge.Appearance = ControlAppearance.Caution;
            LocalRepoSummary.Text = "此目录位于其他 Git 仓库内部，请选择仓库根目录或独立的空目录。";
        }
        else if (isGitRepo)
        {
            LocalRepoBadge.Content = "已连接 Git";
            LocalRepoBadge.Appearance = ControlAppearance.Success;
            if (freshInspection)
            {
                string currentBranch = string.IsNullOrWhiteSpace(lastLocalInspection!.CurrentBranch)
                    ? "未检出分支"
                    : lastLocalInspection.CurrentBranch;
                string remoteSummary = lastLocalInspection.Remotes.Length == 0
                    ? "未配置远端"
                    : string.Join("、", lastLocalInspection.Remotes.Select(r => r.Name));
                LocalRepoSummary.Text = $"当前分支：{currentBranch} · 本地远端：{remoteSummary}";
            }
            else
            {
                LocalRepoSummary.Text = "检测到 .git 目录，正在读取本地分支与远端信息…";
            }
        }
        else if (hasFiles)
        {
            LocalRepoBadge.Content = "未连接 Git";
            LocalRepoBadge.Appearance = ControlAppearance.Caution;
            LocalRepoSummary.Text = "此目录包含现有文件但尚未连接 Git；填写下方远端仓库地址与目标分支后，可保留现有文件并接入远端历史。";
        }
        else
        {
            LocalRepoBadge.Content = "空目录";
            LocalRepoBadge.Appearance = ControlAppearance.Info;
            LocalRepoSummary.Text = "此目录为空；填写下方远端仓库地址与目标分支后，可直接克隆远端分支到此目录。";
        }

        InspectLocalRepoButton.IsEnabled = hasValidDir && !accessError && !running;

        // 2. Remote URL / remote name hints (coherent with local repo inspection)
        string typedUrl = remoteUrl.Text.Trim();
        string effectiveRemoteName = string.IsNullOrWhiteSpace(remote.Text) ? "origin" : remote.Text.Trim();
        string? detectedUrl = isGitRepo && localRemoteUrls.TryGetValue(effectiveRemoteName, out string? foundUrl) &&
            !string.IsNullOrWhiteSpace(foundUrl)
            ? foundUrl
            : null;

        if (typedUrl.Length == 0)
        {
            if (detectedUrl is not null)
            {
                RemoteUrlHint.Text = $"当前留空，将沿用本地远端「{effectiveRemoteName}」：{detectedUrl}";
                ApplyDetectedUrlButton.Tag = detectedUrl;
                ApplyDetectedUrlButton.Visibility = Visibility.Visible;
            }
            else if (isGitRepo)
            {
                RemoteUrlHint.Text = "留空时将按下方「远端名称」读取本地配置；若本地尚未配置该远端，请在此填写地址。";
                ApplyDetectedUrlButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                RemoteUrlHint.Text = "必填：填写 SSH 或 HTTPS 仓库地址，用于测试连接以及克隆或接入本地目录。";
                ApplyDetectedUrlButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ApplyDetectedUrlButton.Visibility = Visibility.Collapsed;
            RemoteUrlHint.Text = detectedUrl is not null && !string.Equals(typedUrl, detectedUrl, StringComparison.Ordinal)
                ? $"将优先使用上方地址（覆盖本地远端「{effectiveRemoteName}」当前的 {detectedUrl}）。"
                : "将优先使用上方地址进行连接测试、拉取、推送与仓库初始化。";
        }

        RemoteNameHint.Text = isGitRepo && localRemoteUrls.Count > 0
            ? $"已从本地读取 {localRemoteUrls.Count} 个远端，可直接下拉选择"
            : "留空默认使用 origin";

        // 3. Local repository initialization vs maintenance card
        string targetBranch = branch.Text.Trim();
        bool hasBranch = targetBranch.Length > 0;
        string effectiveAddress = typedUrl.Length > 0 ? typedUrl : detectedUrl ?? "";

        if (isGitRepo)
        {
            LocalInitCard.Visibility = Visibility.Collapsed;
            LocalMaintenanceCard.Visibility = Visibility.Visible;
            LocalGitResetAction.IsEnabled = hasValidDir && !running;
            LocalResetDescription.Text = hasBranch
                ? $"按「{targetBranch}」分支重建 .git，保留工作目录文件"
                : "按目标分支重建 .git，保留工作目录文件";
        }
        else
        {
            LocalInitCard.Visibility = Visibility.Visible;
            LocalMaintenanceCard.Visibility = Visibility.Collapsed;
            LocalMaintenanceCard.IsExpanded = false;

            bool canAttempt = hasValidDir && !isInsideAnotherRepo && !accessError && !running;
            bool ready = canAttempt && typedUrl.Length > 0 && hasBranch;
            LocalGitAction.IsEnabled = canAttempt;
            LocalGitAction.Appearance = ready ? ControlAppearance.Primary : ControlAppearance.Secondary;

            if (!hasValidDir || isInsideAnotherRepo || accessError)
            {
                LocalInitTitle.Text = "初始化本地 Git 仓库";
                LocalGitAction.Content = "连接 Git 仓库";
                LocalInitDescription.Text = isInsideAnotherRepo
                    ? "当前目录位于其他 Git 仓库内，请先改选仓库根目录或独立目录。"
                    : accessError
                    ? "当前目录不可访问，请重新选择有效的 Windows 目录。"
                    : "请先在上方选择现有的 Windows 本地目录。";
            }
            else if (!hasFiles)
            {
                LocalInitTitle.Text = "克隆远端分支到本地空目录";
                LocalGitAction.Content = "克隆到本地";
                LocalInitDescription.Text = ready
                    ? $"将从 {typedUrl} 克隆分支「{targetBranch}」（远端名 {effectiveRemoteName}）到此空目录。"
                    : typedUrl.Length == 0
                    ? "请先在上方填写「远端仓库地址」，再克隆到此空目录。"
                    : "请先在上方填写或点击「测试连接并获取分支」选择「目标分支」。";
            }
            else
            {
                LocalInitTitle.Text = "接入远端仓库（保留现有文件）";
                LocalGitAction.Content = "接入 Git 仓库";
                LocalInitDescription.Text = ready
                    ? $"将接入 {typedUrl} 的「{targetBranch}」分支历史，保留当前目录中的现有文件并展示本地差异。"
                    : typedUrl.Length == 0
                    ? "请先在上方填写「远端仓库地址」，再接入此目录。"
                    : "请先在上方填写或点击「测试连接并获取分支」选择「目标分支」。";
            }
        }
    }

    private void ApplyDetectedRemoteUrl(object sender, RoutedEventArgs e)
    {
        if (ApplyDetectedUrlButton.Tag is string url && url.Length > 0)
        {
            remoteUrl.Text = url;
            remoteUrl.Focus();
            remoteUrl.CaretIndex = url.Length;
        }
    }

    private async Task ScheduleLocalRepositoryInspectionAsync(
        bool autoFillBlanks, int debounceMs = 0, bool showFeedback = false)
    {
        CancelLocalInspection();
        int sequence = localInspectSequence;
        var cts = new CancellationTokenSource();
        localInspectCts = cts;

        string path = windowsRepo.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            lastLocalInspection = null;
            lastInspectedPath = "";
            localRemoteUrls.Clear();
            UpdateRepositoryStateUi();
            if (showFeedback)
                LocalInspectStatus.ShowFeedback("请先选择现有的 Windows 本地目录。", InfoBarSeverity.Warning);
            return;
        }

        if (debounceMs > 0)
        {
            try { await Task.Delay(debounceMs, cts.Token); }
            catch (OperationCanceledException) { return; }
        }

        if (sequence != localInspectSequence)
            return;

        if (showFeedback)
            LocalInspectStatus.ShowFeedback("正在读取本地仓库信息…");
        LocalInspectRing.Visibility = Visibility.Visible;

        try
        {
            var inspection = await SyncEngine.InspectLocalRepositoryAsync(path, cancellation: cts.Token);
            if (sequence != localInspectSequence)
                return;

            lastLocalInspection = inspection;
            lastInspectedPath = path;
            localRemoteUrls.Clear();
            foreach (var entry in inspection.Remotes)
                localRemoteUrls[entry.Name] = entry.Url;

            if (inspection.IsGitRepository)
            {
                string[] remoteNames = inspection.Remotes.Select(r => r.Name).ToArray();
                string selectedRemote = remote.Text;
                bool formMutated = false;

                if (autoFillBlanks && string.IsNullOrWhiteSpace(selectedRemote))
                {
                    string picked = remoteNames.Contains("origin")
                        ? "origin"
                        : remoteNames.Length == 1 ? remoteNames[0] : "";
                    if (picked.Length > 0)
                    {
                        selectedRemote = picked;
                        formMutated = true;
                    }
                }

                loading = true;
                RestoreChoices(remote, selectedRemote, remoteNames);
                if (autoFillBlanks && string.IsNullOrWhiteSpace(branch.Text) &&
                    !string.IsNullOrWhiteSpace(inspection.CurrentBranch))
                {
                    branch.Text = inspection.CurrentBranch;
                    formMutated = true;
                }
                loading = false;

                string? cacheError = SaveChoices(RemoteChoiceKey(), remoteNames);
                if (formMutated)
                {
                    ClearRemoteBranches();
                    FormChanged();
                }

                if (showFeedback)
                {
                    string branchText = string.IsNullOrWhiteSpace(inspection.CurrentBranch)
                        ? "未检出分支"
                        : $"当前分支「{inspection.CurrentBranch}」";
                    string message = cacheError ?? (remoteNames.Length == 0
                        ? $"已识别本地 Git 仓库（{branchText}），但尚未配置远端，请直接在下方填写远端仓库地址。"
                        : $"已读取本地 Git 仓库（{branchText}），发现 {remoteNames.Length} 个远端：{string.Join("、", remoteNames)}。");
                    LocalInspectStatus.ShowFeedback(message,
                        cacheError is not null || remoteNames.Length == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
                }
                else
                {
                    LocalInspectStatus.IsOpen = false;
                }
            }
            else if (inspection.IsInsideAnotherRepository)
            {
                LocalInspectStatus.ShowFeedback("此目录位于其他 Git 仓库内部，请选择仓库根目录或独立的空目录。", InfoBarSeverity.Warning);
            }
            else
            {
                if (showFeedback)
                    LocalInspectStatus.ShowFeedback("当前目录还不是 Git 仓库，可在下方配置远端后直接克隆或接入。", InfoBarSeverity.Informational);
                else
                    LocalInspectStatus.IsOpen = false;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            if (sequence != localInspectSequence) return;
            WindowsGitSummary.Text = "未检测到 Git for Windows，请先安装后再继续";
            if (showFeedback)
                ShowWindowsGitMissing(LocalInspectStatus, error.Message);
        }
        catch (Exception error)
        {
            if (sequence != localInspectSequence) return;
            if (showFeedback)
                LocalInspectStatus.ShowFeedback(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (sequence == localInspectSequence)
            {
                LocalInspectRing.Visibility = Visibility.Collapsed;
                UpdateRepositoryStateUi();
            }
        }
    }

    private async void InspectLocalRepository(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        if (jsonDraft)
        {
            ShowPendingJson("请先应用到表单，再读取本地仓库信息。");
            return;
        }
        await ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: true, debounceMs: 0, showFeedback: true);
    }

    private async void CheckWindowsGit(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        WindowsGitStatus.ShowFeedback("正在检测 Windows Git…");
        try
        {
            string version = await SyncEngine.CheckWindowsGitAsync();
            WindowsGitSummary.Text = $"已检测到 {version}";
            WindowsGitStatus.ShowFeedback($"已检测到 Git（{version}），可以继续。", InfoBarSeverity.Success);
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            WindowsGitSummary.Text = "未检测到 Git for Windows，请先安装后再继续";
            ShowWindowsGitMissing(WindowsGitStatus, error.Message);
        }
        catch (Exception error) { WindowsGitStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
            UpdateRepositoryStateUi();
        }
    }

    private async void ConfigureLocalRepository(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl)
            return;
        if (jsonDraft)
        {
            ShowPendingJson("请先应用到表单，再操作本地 Git 仓库。");
            return;
        }

        string path = windowsRepo.Text.Trim();
        if (!Directory.Exists(path) || !Path.IsPathFullyQualified(path))
        {
            LocalGitStatus.ShowFeedback("请先在上方选择现有的 Windows 项目目录。", InfoBarSeverity.Warning);
            windowsRepo.Focus();
            return;
        }

        bool reset;
        bool hasFiles;
        try
        {
            reset = HasGitMetadata(path);
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            hasFiles = entries.MoveNext();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LocalGitStatus.ShowFeedback($"无法读取本地目录：{error.Message}", InfoBarSeverity.Error);
            return;
        }

        string effectiveRemoteName = string.IsNullOrWhiteSpace(remote.Text) ? "origin" : remote.Text.Trim();
        string address = remoteUrl.Text.Trim();
        if (reset && address.Length == 0)
        {
            if (localRemoteUrls.TryGetValue(effectiveRemoteName, out string? cachedUrl) &&
                !string.IsNullOrWhiteSpace(cachedUrl))
            {
                address = cachedUrl;
            }
            else
            {
                try { address = await SyncEngine.ReadRemoteUrlAsync(path, effectiveRemoteName); }
                catch (Exception error) when (error is SyncException or ArgumentException) { address = ""; }
            }
        }

        if (address.Length == 0)
        {
            LocalGitStatus.ShowFeedback("请先在上方填写「远端仓库地址」，再继续操作。", InfoBarSeverity.Warning);
            remoteUrl.Focus();
            return;
        }

        string targetBranch = branch.Text.Trim();
        if (targetBranch.Length == 0)
        {
            LocalGitStatus.ShowFeedback("请先在上方填写或点击「测试连接并获取分支」选择「目标分支」，再继续操作。", InfoBarSeverity.Warning);
            branch.Focus();
            return;
        }

        bool backupGit = BackupGitOnReset.IsChecked == true;
        string operation = reset ? "重置 Git 数据" : hasFiles ? "接入 Git 仓库" : "克隆到本地";
        string confirmation = reset
            ? (backupGit
                ? "按所选远端分支重建 Git 元数据，保留工作目录中的文件。原 .git 目录会备份到上级目录。"
                : "按所选远端分支重建 Git 元数据，保留工作目录中的文件。原有本地提交、分支、暂存区和仓库设置将被直接删除且不会备份。")
            : hasFiles
            ? "接入所选远端分支的历史，保留现有文件。完成后将显示本地差异。"
            : "将所选远端分支克隆到此空目录。";
        if (FluentDialog.Ask(this, operation, $"{confirmation}\n\n本地目录：{path}\n远端名称：{effectiveRemoteName}\n远端地址：{address}\n目标分支：{targetBranch}",
            reset ? "重置" : hasFiles ? "接入" : "克隆", "取消", destructive: reset) != MessageBoxResult.Yes)
            return;

        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        Status.IsOpen = false;
        LocalGitStatus.ShowFeedback(reset ? "正在重置 Git 数据…" : hasFiles ? "正在接入 Git 仓库…" : "正在克隆远端分支…");
        var messages = new List<string>();
        bool succeeded = false;
        try
        {
            var config = ProjectConfig.Parse(SerializeConfig()) with
            {
                Remote = effectiveRemoteName,
                RemoteUrl = address
            };
            var mode = reset ? RepositoryInitMode.Reset
                : hasFiles ? RepositoryInitMode.Existing
                : RepositoryInitMode.Empty;
            var outcome = await SyncEngine.InitializeRepositoryAsync(config, mode,
                line => messages.Add(line), backupGitMetadata: backupGit);

            succeeded = true;
            InvalidateLocalRepositoryPlans(path);
            string result = reset
                ? (outcome.BackupPath is not null
                    ? $"Git 数据已重置，工作文件已保留。原 .git 备份：{outcome.BackupPath}"
                    : "Git 数据已重置，工作文件已保留。")
                : hasFiles ? "Git 仓库已接入，现有文件已保留。" : "克隆完成，可以开始构建与同步。";
            if (outcome.StatusUnavailable)
                result += "\n无法读取差异。请在本地仓库查看 Git 状态。";
            LocalGitStatus.ShowFeedback(result, outcome.StatusUnavailable ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            if (hasFiles && !outcome.StatusUnavailable)
                ShowInitializedChanges(outcome.Changes);
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(LocalGitStatus, error.Message);
        }
        catch (Exception error)
        {
            string details = error is SyncException || messages.Count == 0
                ? error.Message : string.Join(Environment.NewLine, messages);
            LocalGitStatus.ShowFeedback($"{operation}未完成。{Environment.NewLine}{details}", InfoBarSeverity.Error);
        }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
            UpdateRepositoryStateUi();
        }

        if (succeeded)
            await ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: false, debounceMs: 0, showFeedback: false);
    }

    private void InvalidateLocalRepositoryPlans(string path)
    {
        string repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var project in savedProjects)
        {
            if (string.IsNullOrWhiteSpace(project.Repository) || !Path.IsPathFullyQualified(project.Repository))
                continue;

            string savedPath;
            try { savedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Repository)); }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }
            if (!string.Equals(repository, savedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            projectRuns.Remove(project.Id);
            DeletePreview(project);
            SiteLift.App.App.Store.DeletePendingPush(project.Id);
        }
        ShowSavedChanges();
        RefreshProjects();
    }

    private void ShowInitializedChanges(IReadOnlyList<string> changes)
    {
        var close = new Wpf.Ui.Controls.Button
        {
            Content = "关闭", IsCancel = true, MinWidth = 84,
            Padding = new Thickness(14, 5.5, 14, 6.5), HorizontalAlignment = HorizontalAlignment.Right
        };
        close.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var footer = new Border
        {
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = close
        };
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        var diffBox = new Wpf.Ui.Controls.TextBox
        {
            IsReadOnly = true,
            Padding = new Thickness(12, 10, 12, 10),
            Text = changes.Count == 0 ? "当前文件与远端基线一致。" : string.Join(Environment.NewLine, changes),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        diffBox.SetResourceReference(Control.StyleProperty, "WorkspaceLog");
        var panel = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(diffBox);
        var dialog = FluentDialog.Create(this, "Git 仓库 · 本地差异", panel, 680, 380);
        dialog.ShowDialog();
    }
}

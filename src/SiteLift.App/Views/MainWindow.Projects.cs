using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using SiteLift.Core;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private const int MaxHomeLogLength = 200_000;
    private readonly StringBuilder homeLogBuffer = new();
    private readonly string projectFile = SiteLift.App.App.Store.ProjectsFilePath;
    private readonly List<ProjectEntry> savedProjects = new();
    private ProjectEntry? activeProject;
    private int settingsOpenSequence;
    private JsonObject? originalConfig;

    private bool HasConfigChanges => !JsonNode.DeepEquals(current, originalConfig);

    private void UpdateSaveProjectAction()
    {
        bool hasChanges = HasConfigChanges;
        bool canSave = !jsonDraft && hasChanges;
        SaveProjectButton.Appearance = canSave ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SaveProjectButton.IsEnabled = canSave;
        ApplyJsonButton.Appearance = jsonDraft ? ControlAppearance.Primary : ControlAppearance.Secondary;
        ApplyJsonButton.IsEnabled = jsonDraft;
        UpdateJsonSyncBadge();

        string displayName = ProjectName.Text.Trim();
        if (displayName.Length == 0)
            displayName = activeProject?.Name ?? "新建项目";
        ActiveProjectHeaderTitle.Text = displayName;
        UpdateActiveProjectFrameworkVisual();
        UnsavedChangesBadge.Visibility = !jsonDraft && hasChanges ? Visibility.Visible : Visibility.Collapsed;
        JsonPendingBadge.Visibility = jsonDraft ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly HashSet<string> probedWslFrameworks = new(StringComparer.Ordinal);

    private static bool IsDarkThemeActive() =>
        Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;

    private void UpdateActiveProjectFrameworkVisual()
    {
        if (ActiveProjectIconChip is null) return;
        string cfg = SerializeConfig();
        var framework = SiteFrameworkDetector.Detect(cfg, windowsRepo.Text.Trim());
        if (framework == SiteFramework.Generic && activeProject is { Framework: not SiteFramework.Generic } currentEntry)
            framework = currentEntry.Framework;
        var visual = SiteFrameworkVisuals.Resolve(framework, IsDarkThemeActive());
        ActiveProjectIconChip.ToolTip = visual.DisplayName;
        var fallbackVis = visual.IsGeneric ? Visibility.Visible : Visibility.Collapsed;
        var customVis = visual.IsGeneric ? Visibility.Collapsed : Visibility.Visible;
        ActiveProjectFallbackIcon.Visibility = fallbackVis;
        ActiveProjectFrameworkImage.Source = visual.Image;
        ActiveProjectFrameworkImage.Visibility = customVis;
    }

    private void ApplyProjectFramework(ProjectEntry project, SiteFramework framework)
    {
        project.Framework = framework;
        var visual = SiteFrameworkVisuals.Resolve(framework, IsDarkThemeActive());
        project.FrameworkLabel = visual.DisplayName;
        project.FrameworkImage = visual.Image;
        project.CustomFrameworkVisibility = visual.IsGeneric ? Visibility.Collapsed : Visibility.Visible;
        project.FallbackFolderVisibility = visual.IsGeneric ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DetectProjectFramework(ProjectEntry project)
    {
        var detected = SiteFrameworkDetector.Detect(project.Configuration, project.Repository);
        ApplyProjectFramework(project, detected);
        if (detected != SiteFramework.Generic) return;

        string distroName = ProjectConfig.TryReadString(project.Configuration, "distro");
        string linuxRepo = ProjectConfig.TryReadString(project.Configuration, "wslRepo");
        if (string.IsNullOrWhiteSpace(distroName) || string.IsNullOrWhiteSpace(linuxRepo))
            return;

        string probeKey = $"{project.Id:N}|{distroName}|{linuxRepo}";
        if (!probedWslFrameworks.Add(probeKey))
            return;

        _ = Task.Run(() =>
        {
            string wslUnc;
            try { wslUnc = FileCopy.ResolvePath(linuxRepo, distroName); }
            catch (ArgumentException) { return SiteFramework.Generic; }
            return SiteFrameworkDetector.DetectFromDirectory(wslUnc);
        }).ContinueWith(task =>
        {
            if (task.IsFaulted || task.Result == SiteFramework.Generic) return;
            var found = task.Result;
            _ = Dispatcher.BeginInvoke(() =>
            {
                var target = savedProjects.Find(p => p.Id == project.Id);
                if (target is null || target.Framework != SiteFramework.Generic) return;
                ApplyProjectFramework(target, found);
                RefreshProjects();
                if (activeProject?.Id == target.Id)
                    UpdateActiveProjectFrameworkVisual();
            });
        }, TaskScheduler.Default);
    }

    private void RefreshFrameworkVisuals()
    {
        if (savedProjects is null || Projects is null) return;
        foreach (var project in savedProjects)
            ApplyProjectFramework(project, project.Framework);
        if (savedProjects.Count > 0)
            RefreshProjects();
        if (ProjectPage?.Visibility == Visibility.Visible)
            UpdateActiveProjectFrameworkVisual();
    }

    private sealed record ProjectEntry(Guid Id, string Name, string Repository, string Branch, string Configuration)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Checked { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string RemoteRepository => ProjectConfig.GetRemoteDisplaySummary(Configuration, Branch);

        [System.Text.Json.Serialization.JsonIgnore]
        public string Progress { get; set; } = "待构建";

        [System.Text.Json.Serialization.JsonIgnore]
        public SiteFramework Framework { get; set; } = SiteFramework.Generic;

        [System.Text.Json.Serialization.JsonIgnore]
        public string FrameworkLabel { get; set; } = "通用项目";

        [System.Text.Json.Serialization.JsonIgnore]
        public System.Windows.Media.ImageSource? FrameworkImage { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public Visibility CustomFrameworkVisibility { get; set; } = Visibility.Collapsed;

        [System.Text.Json.Serialization.JsonIgnore]
        public Visibility FallbackFolderVisibility { get; set; } = Visibility.Visible;
    }

    private void InitializeProjects()
    {
        try
        {
            if (File.Exists(projectFile))
                savedProjects.AddRange(JsonSerializer.Deserialize<List<ProjectEntry>>(File.ReadAllText(projectFile))
                    ?? throw new JsonException("项目列表必须是 JSON 数组。"));
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法加载项目列表：{error.Message}", InfoBarSeverity.Error);
        }
        foreach (var project in savedProjects)
        {
            DetectProjectFramework(project);
            RestoreBuildReceipt(project);
            try
            {
                if (SiteLift.App.App.Store.LoadPendingPush(project.Id) is { } pending)
                {
                    projectRuns[project.Id] = new RunState(null, pending.ToToken(), "已提交，待推送");
                }
                string previewPath = SiteLift.App.App.Store.PreviewStatePath(project.Id);
                if (!projectRuns.ContainsKey(project.Id) && File.Exists(previewPath))
                {
                    var preview = JsonSerializer.Deserialize<SavedPreview>(File.ReadAllText(previewPath));
                    if (preview is not null &&
                        preview.ConfigurationHash == ProjectConfig.ComputeBuildFingerprint(project.Configuration) &&
                        System.Text.RegularExpressions.Regex.IsMatch(preview.Plan, @"\A[0-9a-f]{40}:[0-9A-F]{64}\z"))
                    {
                        projectRuns[project.Id] = new RunState(preview.Plan, null, "可同步", preview.Changes);
                        if (!HasSuccessfulBuild(project) && BuildFingerprint(project) is { } hash)
                            successfulBuilds[project.Id] = hash;
                    }
                }
            }
            catch (Exception error) { HomeStatus.ShowFeedback($"无法读取「{project.Name}」的待推送记录：{error.Message}", InfoBarSeverity.Error); }
        }
        HomeChanges.Items.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(BatchChange.Group)));
        ShowSavedChanges();
        RefreshProjects();
    }

    private void SavePreview(ProjectEntry project, string plan, BatchChange[] changes)
    {
        string path = SiteLift.App.App.Store.PreviewStatePath(project.Id);
        string hash = ProjectConfig.ComputeBuildFingerprint(project.Configuration)
            ?? ProjectConfig.ComputeConfigurationHash(project.Configuration);
        AppDataStore.WriteTextAtomically(path, JsonSerializer.Serialize(new SavedPreview(hash, plan, changes)));
    }

    private void DeletePreview(ProjectEntry project) =>
        SiteLift.App.App.Store.DeletePreviewState(project.Id);

    private void ShowSavedChanges()
    {
        HomeChanges.Items.Clear();
        int added = 0;
        int modified = 0;
        int deleted = 0;
        foreach (var project in savedProjects)
            if (projectRuns.TryGetValue(project.Id, out var state) && state.Changes is { } changes)
                foreach (var change in changes)
                {
                    HomeChanges.Items.Add(change);
                    switch (change.Action)
                    {
                        case "新增": added++; break;
                        case "修改":
                        case "类型变更": modified++; break;
                        case "源端删除":
                        case "目标清理": deleted++; break;
                    }
                }
        int count = HomeChanges.Items.Count;
        bool hasChanges = count > 0;
        bool hasCompletedPlan = projectRuns.Values.Any(state => state.Plan is not null);
        HomeChanges.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        EmptyChangesPanel.Visibility = hasChanges ? Visibility.Collapsed : Visibility.Visible;
        HomeChangesBadge.Text = count.ToString();
        HomeChangesBadge.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;

        HomeChangesAddedBadge.Content = $"+{added} 新增";
        HomeChangesAddedBadge.Visibility = added > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeChangesModifiedBadge.Content = $"~{modified} 修改";
        HomeChangesModifiedBadge.Visibility = modified > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeChangesDeletedBadge.Content = $"-{deleted} 删除";
        HomeChangesDeletedBadge.Visibility = deleted > 0 ? Visibility.Visible : Visibility.Collapsed;
        HomeChangesSummary.Visibility = hasChanges ? Visibility.Collapsed : Visibility.Visible;

        if (!hasChanges)
        {
            HomeChangesSummary.Text = hasCompletedPlan ? "无变更" : "待同步 0";
            if (hasCompletedPlan)
            {
                EmptyChangesTitle.Text = "无文件变更";
                EmptyChangesSubtitle.Text = "与目标仓库一致";
            }
            else
            {
                EmptyChangesTitle.Text = "暂无待同步变更";
                EmptyChangesSubtitle.Text = "构建后显示待同步文件";
            }
        }
    }

    private ProjectEntry CreateEntry() => new(activeProject?.Id ?? Guid.NewGuid(), ProjectName.Text.Trim(),
        windowsRepo.Text.Trim(), $"{remote.Text.Trim()}/{branch.Text.Trim()}", SerializeConfig());

    private void PersistProjects() =>
        AppDataStore.WriteTextAtomically(projectFile,
            JsonSerializer.Serialize(savedProjects, new JsonSerializerOptions { WriteIndented = true }));

    private void RefreshProjects()
    {
        var selected = (Projects.SelectedItem as ProjectEntry)?.Id;
        foreach (var project in savedProjects)
            if (projectRuns.TryGetValue(project.Id, out var state)) project.Progress = state.Progress;
            else project.Progress = "待构建";
        Projects.ItemsSource = null;
        Projects.ItemsSource = savedProjects;
        Projects.SelectedItem = savedProjects.Find(project => project.Id == selected);
        bool hasProjects = savedProjects.Count > 0;
        var projectVisibility = hasProjects ? Visibility.Visible : Visibility.Collapsed;
        ProjectTableFrame.Visibility = projectVisibility;
        BatchActions.Visibility = projectVisibility;
        EmptyProjects.Visibility = hasProjects ? Visibility.Collapsed : Visibility.Visible;
        UpdateHomeActions();
    }

    private Task ShowProjectAsync(string configuration, ProjectEntry? entry, bool isBlankProject = false)
    {
        ++settingsOpenSequence;
        restoreDetectedOptions = !isBlankProject;
        LoadConfig(configuration);
        LocalGitStatus.IsOpen = false;
        Status.IsOpen = false;
        activeProject = entry;
        originalConfig = entry is not null || isBlankProject ? (JsonObject)current.DeepClone() : null;
        UpdateSaveProjectAction();
        SettingsTabs.SelectedItem = RepositorySettingsTab;
        HomePage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Visible;
        _ = ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: false, debounceMs: 0, showFeedback: false);
        return Task.CompletedTask;
    }

    private async void NewProject(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton &&
            sender is Wpf.Ui.Controls.SplitButton { Flyout: System.Windows.Controls.ContextMenu menu } button)
        {
            e.Handled = true;
            FlyoutKeyboard.Open(menu, button);
            return;
        }

        await ShowProjectAsync("{}", null, isBlankProject: true);
    }

    private async void ImportProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入项目配置", Filter = "项目配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            string configuration = File.ReadAllText(dialog.FileName);
            if (!ConfirmExternalConfiguration(configuration)) return;
            await ShowProjectAsync(configuration, null);
            if (string.IsNullOrWhiteSpace(ProjectName.Text))
                ProjectName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            Status.ShowFeedback("已导入配置。点击「保存项目」加入列表。");
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法导入项目：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OpenProject(object sender, RoutedEventArgs e)
    {
        if (Projects.SelectedItem is not ProjectEntry entry)
            return;
        try { await ShowProjectAsync(entry.Configuration, entry); }
        catch (Exception error) { HomeStatus.ShowFeedback($"无法打开项目：{error.Message}", InfoBarSeverity.Error); }
    }

    private void SaveProject(object sender, RoutedEventArgs e) => TrySaveProject();

    private bool TrySaveProject()
    {
        if (jsonDraft)
        {
            ShowPendingJson("请先应用到表单，再保存项目。");
            return false;
        }
        if (!HasConfigChanges) return true;

        if (string.IsNullOrWhiteSpace(ProjectName.Text))
        {
            SettingsTabs.SelectedItem = RepositorySettingsTab;
            ProjectName.Focus();
            Status.ShowFeedback("请填写项目名称。", InfoBarSeverity.Warning);
            return false;
        }

        if (!TryNormalizeSyncPaths(include, exclude, pullPath)) return false;
        if (!HasConfigChanges) return true;

        ProjectEntry entry = CreateEntry();
        DetectProjectFramework(entry);
        int index = savedProjects.FindIndex(item => item.Id == entry.Id);
        ProjectEntry? previous = index < 0 ? null : savedProjects[index];
        entry.Checked = previous?.Checked ?? false;
        if (index < 0)
            savedProjects.Add(entry);
        else
            savedProjects[index] = entry;
        try { PersistProjects(); }
        catch (Exception error)
        {
            if (previous is null) savedProjects.Remove(entry);
            else savedProjects[index] = previous;
            Status.ShowFeedback($"保存失败：{error.Message}", InfoBarSeverity.Error);
            return false;
        }

        activeProject = entry;
        if (previous is not null &&
            ProjectConfig.ComputeBuildFingerprint(previous.Configuration) != ProjectConfig.ComputeBuildFingerprint(entry.Configuration))
        {
            bool isPendingPush = projectRuns.TryGetValue(entry.Id, out var existingState) &&
                existingState.Progress == "已提交，待推送";
            if (!isPendingPush)
            {
                projectRuns.Remove(entry.Id);
                DeletePreview(entry);
                ShowSavedChanges();
            }
            else if (!string.Equals(previous.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(ProjectConfig.TryReadString(previous.Configuration, "branch"),
                                    ProjectConfig.TryReadString(entry.Configuration, "branch"), StringComparison.Ordinal))
            {
                // Repository or branch changed: old pending push no longer applies.
                projectRuns.Remove(entry.Id);
                SiteLift.App.App.Store.DeletePendingPush(entry.Id);
            }
            // If only remoteUrl/SSH/credentials changed while pending push, keep pending state so Retry Push works!
        }
        originalConfig = (JsonObject)current.DeepClone();
        UpdateSaveProjectAction();
        Status.IsOpen = false;
        JsonStatus.IsOpen = false;
        return true;
    }

    private bool CanLeaveProject()
    {
        if (ProjectPage.Visibility != Visibility.Visible || (!jsonDraft && !HasConfigChanges))
            return true;

        var choice = ConfirmProjectAction("未保存的项目", "保存当前项目的配置改动？", "保存", "放弃", canCancel: true);
        return choice == MessageBoxResult.No || (choice == MessageBoxResult.Yes && TrySaveProject());
    }

    private void BackToHome(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveProject())
            return;
        CancelLocalInspection();
        ResetPreview();
        ProjectPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
        HomeStatus.IsOpen = false;
        RefreshProjects();
        Projects.SelectedItem = activeProject;
    }

    private void ProjectRowMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (running) e.Handled = true;
    }

    private async void EditProjectFromMenu(object sender, RoutedEventArgs e)
    {
        if (running || sender is not System.Windows.Controls.MenuItem { DataContext: ProjectEntry entry })
            return;
        try
        {
            Projects.SelectedItem = entry;
            await ShowProjectAsync(entry.Configuration, entry);
        }
        catch (Exception error) { HomeStatus.ShowFeedback($"无法打开项目：{error.Message}", InfoBarSeverity.Error); }
    }

    private void ToggleSelectAllProjects(object sender, RoutedEventArgs e)
    {
        if (running || savedProjects.Count == 0)
            return;
        bool checkAll = savedProjects.Any(project => !project.Checked);
        foreach (var project in savedProjects)
            project.Checked = checkAll;
        RefreshProjects();
    }

    private void CopyHomeLog(object sender, RoutedEventArgs e)
    {
        if (homeLogBuffer.Length == 0)
        {
            HomeStatus.ShowFeedback("当前运行日志为空。", InfoBarSeverity.Informational);
            return;
        }
        try
        {
            Clipboard.SetText(homeLogBuffer.ToString());
            HomeStatus.ShowFeedback("已复制运行日志到剪贴板。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法写入剪贴板：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private void ClearHomeLog(object sender, RoutedEventArgs e) => ResetHomeLog();

    private void ResetHomeLog()
    {
        homeLogBuffer.Clear();
        HomeLog.Document.Blocks.Clear();
        HomeLog.Document.PageWidth = double.NaN;
        EmptyHomeLogPanel.Visibility = Visibility.Visible;
    }

    private void RemoveProject(object sender, RoutedEventArgs e)
    {
        if (running || sender is not System.Windows.Controls.MenuItem { DataContext: ProjectEntry row })
            return;

        int index = savedProjects.FindIndex(project => project.Id == row.Id);
        if (index < 0) return;
        var entry = savedProjects[index];
        if (ConfirmProjectAction("移除项目", $"从列表移除「{entry.Name}」？仓库文件会保留。", "移除", "取消", canCancel: false, destructive: true) != MessageBoxResult.Yes)
            return;

        savedProjects.RemoveAt(index);
        try { PersistProjects(); }
        catch (Exception error)
        {
            savedProjects.Insert(index, entry);
            HomeStatus.ShowFeedback($"移除失败：{error.Message}", InfoBarSeverity.Error);
            return;
        }

        RefreshProjects();
        projectRuns.Remove(entry.Id);
        successfulBuilds.Remove(entry.Id);
        SiteLift.App.App.Store.DeleteAllProjectState(entry.Id);
        ShowSavedChanges();
        HomeStatus.ShowFeedback($"已移除「{entry.Name}」。", InfoBarSeverity.Success);
    }

    private sealed record BatchChange(string Project, string Action, string Path)
    {
        public string Group => Action == "目标清理" ? "目标清理（永久删除）" : "同步变更";
        public string DirectoryPrefix
        {
            get
            {
                int separator = Path.LastIndexOfAny(new[] { '/', '\\' });
                return separator >= 0 ? Path[..(separator + 1)] : string.Empty;
            }
        }
        public string FileName
        {
            get
            {
                int separator = Path.LastIndexOfAny(new[] { '/', '\\' });
                return separator >= 0 ? Path[(separator + 1)..] : Path;
            }
        }
    }
    private enum Operation { Build, BrowserPreview, Apply, Publish, RetryPush }
    private sealed record RunState(string? Plan, string? Synced, string Progress, BatchChange[]? Changes = null);
    private sealed record SavedPreview(string ConfigurationHash, string Plan, BatchChange[] Changes);
    private readonly Dictionary<Guid, RunState> projectRuns = new();

    private void ProjectSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateHomeActions();

    public static readonly DependencyProperty IsRowSelectionEnabledProperty =
        DependencyProperty.Register(nameof(IsRowSelectionEnabled), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public bool IsRowSelectionEnabled
    {
        get => (bool)GetValue(IsRowSelectionEnabledProperty);
        private set => SetValue(IsRowSelectionEnabledProperty, value);
    }

    private void ProjectCheckChanged(object sender, RoutedEventArgs e)
    {
        if (running) return;
        if (sender is System.Windows.Controls.CheckBox { DataContext: ProjectEntry project } checkBox)
            project.Checked = checkBox.IsChecked == true;
        UpdateHomeActions();
    }

    private async void OpenRepositoryShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ProjectEntry project, Tag: string shortcut })
            return;

        try
        {
            string Field(string key) => ProjectConfig.TryReadString(project.Configuration, key);
            string destination;
            switch (shortcut)
            {
                case "Local":
                    destination = project.Repository;
                    break;
                case "Wsl":
                case "WslTerminal":
                case "Reinstall":
                case "UpgradeTheme":
                    string path = UnquotePath(Field("wslRepo"));
                    string distribution = Field("distro");
                    if (TryParseWslNetworkPath(path, out var linuxPath, out var namedDistribution))
                    {
                        path = linuxPath;
                        distribution = namedDistribution;
                    }
                    if (!path.StartsWith('/') || string.IsNullOrWhiteSpace(distribution) ||
                        distribution.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                        throw new InvalidOperationException("请先在项目设置中检测并保存本地构建环境。");
                    if (shortcut is "Reinstall" or "UpgradeTheme")
                    {
                        bool upgradeTheme = shortcut == "UpgradeTheme";
                        await RunMaintenanceCommandAsync(project, distribution, Field("wslUser"), path,
                            Field("nodeBin"), Field(upgradeTheme ? "themeUpgradeCommand" : "reinstallCommand"),
                            upgradeTheme ? "升级主题" : "重装依赖", upgradeTheme ? "执行升级" : "执行重装");
                        return;
                    }
                    if (shortcut == "WslTerminal")
                    {
                        string user = Field("wslUser");
                        if (user.Length == 0)
                            throw new InvalidOperationException("请先在项目设置中选择并保存 WSL 用户。");

                        await QueryWslAsync(System.Text.Encoding.UTF8,
                            "-d", distribution, "-u", user, "--", "test", "-d", path);
                        var terminal = new System.Diagnostics.ProcessStartInfo(ProcessRunner.WslExecutablePath) { UseShellExecute = true };
                        foreach (string arg in new[] { "-d", distribution, "-u", user, "--cd", path })
                            terminal.ArgumentList.Add(arg);
                        using var session = System.Diagnostics.Process.Start(terminal);
                        return;
                    }
                    destination = @"\\wsl.localhost\" + distribution + path.Replace('/', '\\');
                    break;
                case "Remote":
                    destination = Field("remoteUrl");
                    if (destination.Length == 0)
                        destination = await ReadShortcutRemoteAsync(project.Repository, Field("remote"));
                    destination = RepositoryBrowserUrl(destination);
                    break;
                default:
                    return;
            }

            if (shortcut != "Remote" && !await Task.Run(() => Directory.Exists(destination)))
                throw new DirectoryNotFoundException($"目录不可访问：{destination}");

            using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(destination)
            {
                UseShellExecute = true
            });
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(HomeStatus, error.Message);
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法打开「{project.Name}」的快捷访问目标：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private static Task<string> ReadShortcutRemoteAsync(string repository, string remoteName) =>
        SyncEngine.ReadRemoteUrlAsync(repository, remoteName);

    private static string RepositoryBrowserUrl(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Host.Length > 0)
            return uri.AbsoluteUri;

        var github = System.Text.RegularExpressions.Regex.Match(address,
            @"^(?:git@github\.com:|ssh://git@github\.com/)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (github.Success)
            return "https://github.com/" + github.Groups[1].Value;
        throw new InvalidOperationException("请在项目设置中填写远端仓库的 HTTP(S) 地址。");
    }

    private void OpenProjectRow(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (running || e.Handled || sender is not System.Windows.Controls.DataGridRow { Item: ProjectEntry project })
            return;
        for (var element = e.OriginalSource as System.Windows.Media.Visual; element is not null;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element) as System.Windows.Media.Visual)
            if (element is System.Windows.Controls.Primitives.ButtonBase)
                return;
        Projects.SelectedItem = project;
        OpenProject(sender, e);
    }

    private void MenuKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        FlyoutKeyboard.Handle(sender, e);

    private bool CanRunBatchOperation(ProjectEntry project, Operation operation)
    {
        projectRuns.TryGetValue(project.Id, out var state);
        bool pending = state?.Progress == "已提交，待推送";
        return operation switch
        {
            Operation.Build => !pending,
            Operation.BrowserPreview => HasSuccessfulBuild(project) && HasPreviewCommand(project),
            Operation.Apply => !pending && state?.Plan is not null,
            Operation.Publish => !pending && state?.Synced is not null,
            Operation.RetryPush => pending && state?.Synced is not null,
            _ => false
        };
    }

    private void UpdateHomeActions()
    {
        if (Projects is null || BuildBatchButton is null || BrowserPreviewButton is null)
            return;

        var selected = savedProjects.Where(project => project.Checked).ToArray();
        int canBuild = selected.Count(project => CanRunBatchOperation(project, Operation.Build));
        int canPreview = selected.Count(project => CanRunBatchOperation(project, Operation.BrowserPreview));
        int canApply = selected.Count(project => CanRunBatchOperation(project, Operation.Apply));
        int canPublish = selected.Count(project => CanRunBatchOperation(project, Operation.Publish));
        int canRetry = selected.Count(project => CanRunBatchOperation(project, Operation.RetryPush));
        UpdateBatchButton(BuildBatchButton, "构建", "开始构建", canBuild, selected.Length, "请勾选可构建的项目");
        UpdateBatchButton(BrowserPreviewButton, "预览", "打开浏览器预览", canPreview, selected.Length,
            selected.Any(HasSuccessfulBuild) ? "请填写并保存预览命令" : "请勾选已成功构建的项目");
        UpdateBatchButton(ApplyBatchButton, "同步", "应用同步", canApply, selected.Length, "请勾选已构建的项目");
        UpdateBatchButton(PublishBatchButton, "推送", "提交并推送", canPublish, selected.Length, "请勾选已同步的项目");
        UpdateBatchButton(RetryPushButton, "重试", "重试推送", canRetry, selected.Length, "请勾选待推送的项目");
        RetryPushButton.Visibility = canRetry > 0 ? Visibility.Visible : Visibility.Collapsed;

        var batchButtons = new[] { RetryPushButton, PublishBatchButton, ApplyBatchButton, BuildBatchButton };
        var primaryAction = Array.Find(batchButtons, button => button.IsEnabled);
        foreach (var button in batchButtons)
            button.Appearance = button == primaryAction ? ControlAppearance.Primary : ControlAppearance.Transparent;
        BrowserPreviewButton.Appearance = ControlAppearance.Transparent;

        if (SelectAllProjectsCheckBox is not null)
        {
            SelectAllProjectsCheckBox.IsEnabled = !running && savedProjects.Count > 0;
            SelectAllProjectsCheckBox.IsChecked = savedProjects.Count == 0 || selected.Length == 0
                ? false
                : selected.Length == savedProjects.Count ? true : null;
        }
        if (HomeFooterSummary is not null)
        {
            HomeFooterSummary.Text = savedProjects.Count == 0
                ? "点击右上角「新建项目」或「导入 JSON」开始使用。"
                : selected.Length > 0
                ? $"已勾选 {selected.Length} / {savedProjects.Count} 个项目 · 双击行可打开项目配置"
                : $"共 {savedProjects.Count} 个项目 · 双击行或右键可打开项目配置";
        }
        NewProjectButton.Appearance = primaryAction is null ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void UpdateBatchButton(Wpf.Ui.Controls.Button button, string label, string action, int eligible, int selected, string selectionHint)
    {
        bool partial = eligible > 0 && eligible < selected;
        button.Content = partial ? $"{label} · {eligible}" : label;
        button.IsEnabled = !running && eligible > 0;
        string hint = partial ? $"{action} · {eligible}/{selected} 项" :
            eligible > 0 ? $"{action} · {eligible} 项" : selectionHint;
        button.ToolTip = hint;
        AutomationProperties.SetHelpText(button, hint);
    }

    private async void BuildSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Build);
    private async void OpenPreviewSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.BrowserPreview);
    private async void ApplySelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Apply);
    private async void PublishSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Publish);
    private async void RetryPushSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.RetryPush);

    private string? RequestCommitSummary(ProjectEntry project, string[] changes)
    {
        var body = new System.Windows.Controls.DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        var dialog = FluentDialog.Create(this, "提交并推送", body, 480);
        string branch = ProjectConfig.TryReadString(project.Configuration, "branch");
        var scope = new System.Windows.Controls.TextBlock
        {
            Text = "将暂存并提交整个 Windows 工作区的新增、修改和删除。Git 已忽略的未跟踪文件会跳过。",
            Margin = new Thickness(0, 0, 0, 10)
        };
        scope.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        System.Windows.Controls.DockPanel.SetDock(scope, System.Windows.Controls.Dock.Top);
        body.Children.Add(scope);

        var targetTitle = new System.Windows.Controls.TextBlock
        {
            Text = $"{project.Name} → {branch}（{changes.Length} 项变更）",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2)
        };
        targetTitle.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        Typography.SetNumeralAlignment(targetTitle, FontNumeralAlignment.Tabular);
        System.Windows.Controls.DockPanel.SetDock(targetTitle, System.Windows.Controls.Dock.Top);
        body.Children.Add(targetTitle);

        var repoPath = new System.Windows.Controls.TextBlock
        {
            Text = project.Repository, Margin = new Thickness(0, 0, 0, 8)
        };
        repoPath.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        System.Windows.Controls.DockPanel.SetDock(repoPath, System.Windows.Controls.Dock.Top);
        body.Children.Add(repoPath);

        var changesBox = new Wpf.Ui.Controls.TextBox
        {
            Text = string.Join(Environment.NewLine, changes),
            IsReadOnly = true, AcceptsReturn = true, MaxHeight = 200,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12)
        };
        changesBox.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceLog");
        System.Windows.Controls.DockPanel.SetDock(changesBox, System.Windows.Controls.Dock.Top);
        body.Children.Add(changesBox);

        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var footer = new System.Windows.Controls.Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = actions
        };
        footer.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(footer);
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, Appearance = ControlAppearance.Secondary, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var accept = new Wpf.Ui.Controls.Button
        {
            Content = "提交并推送", IsDefault = true, IsEnabled = false,
            Appearance = ControlAppearance.Primary, MinWidth = 104
        };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        actions.Children.Add(cancel);
        actions.Children.Add(accept);

        var row = new System.Windows.Controls.DockPanel();
        var input = new Wpf.Ui.Controls.TextBox { MinHeight = 34 };
        var label = new System.Windows.Controls.Label
        {
            Content = "提交摘要", Target = input,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        label.SetResourceReference(FrameworkElement.StyleProperty, "SettingsFieldLabel");
        row.Children.Add(label);
        row.Children.Add(input);
        body.Children.Add(row);

        input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private CancellationTokenSource? batchCancellation;
    private bool closeAfterBatch;

    private void ShowBatchProgress(Operation operation, string detail)
    {
        var (activity, stopLabel) = operation switch
        {
            Operation.Build => ("构建", "停止构建"),
            Operation.BrowserPreview => ("启动预览", "取消打开预览"),
            Operation.Apply => ("应用同步", "停止批量操作"),
            Operation.RetryPush => ("重试推送", "停止批量操作"),
            _ => ("提交并推送", "停止批量操作")
        };
        StopBatchButton.Content = stopLabel;
        SetActivityText(BatchProgressText, $"正在{activity}{detail}");
    }

    private void StopBatch(object sender, RoutedEventArgs e)
    {
        batchCancellation?.Cancel();
        StopBatchButton.IsEnabled = false;
        SetActivityText(BatchProgressText, $"正在{StopBatchButton.Content}…");
    }

    private async Task RunSelectedAsync(Operation operation)
    {
        if (running) return;
        bool publishing = operation is Operation.Publish or Operation.RetryPush;
        var selected = savedProjects.Where(project => project.Checked && CanRunBatchOperation(project, operation)).ToArray();
        if (selected.Length == 0)
        {
            HomeStatus.ShowFeedback("当前勾选的项目中没有可执行此操作的项目。", InfoBarSeverity.Warning);
            return;
        }

        var cleanupProjects = operation == Operation.Apply
            ? selected.Where(project => projectRuns.TryGetValue(project.Id, out var state) && state.Plan is not null
                && ProjectConfig.TryReadBool(project.Configuration, "cleanUntracked")).ToArray()
            : Array.Empty<ProjectEntry>();
        if (cleanupProjects.Length > 0 && ConfirmProjectAction("确认永久清理",
            "以下项目将永久删除预览列出的目标清理项。清理覆盖整个 Windows 工作区，文件不会进入回收站。\n\n" +
            string.Join("\n", cleanupProjects.Select(project => project.Name + " · " + project.Repository)),
            "确认清理并同步", "取消", canCancel: false, destructive: true) != MessageBoxResult.Yes)
            return;

        using var cancellation = new CancellationTokenSource();
        batchCancellation = cancellation;
        StopBatchButton.IsEnabled = true;
        ShowBatchProgress(operation, $" · {selected.Length} 个项目");
        BatchProgressPanel.Visibility = Visibility.Visible;
        HomeStatus.IsOpen = false;
        running = true;
        IsRowSelectionEnabled = false;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        int completed = 0;
        int failed = 0;
        bool cancelledBeforeStart = false;
        bool autoPreviewFailed = false;
        bool windowsGitMissing = false;
        string? windowsGitMissingMessage = null;
        try
        {
            var summaries = new Dictionary<Guid, string>();
            if (operation == Operation.Publish)
            {
                foreach (var project in selected)
                {
                    if (cancellation.IsCancellationRequested) return;
                    if (!projectRuns.TryGetValue(project.Id, out var state) || state.Synced is not { } synced ||
                        synced.Count(ch => ch == ':') != 1) continue;
                    try
                    {
                        string[] changes = await SyncEngine.ReadWorkingTreeStatusAsync(project.Repository, cancellation: cancellation.Token);
                        if (cancellation.IsCancellationRequested) return;
                        if (changes.Length == 0)
                            continue;
                        if (RequestCommitSummary(project, changes) is not { } summary)
                        {
                            cancelledBeforeStart = true;
                            return;
                        }
                        summaries.Add(project.Id, summary);
                    }
                    catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
                    {
                        failed++;
                        windowsGitMissing = true;
                        windowsGitMissingMessage = error.Message;
                        HomeResults.SelectedItem = HomeLogTab;
                        SetResultsCollapsed(false);
                        WriteHomeLog($"{project.Name}：{error.Message}");
                        return;
                    }
                    catch (Exception error)
                    {
                        failed++;
                        HomeResults.SelectedItem = HomeLogTab;
                        SetResultsCollapsed(false);
                        WriteHomeLog($"{project.Name}：无法读取待提交文件：{error.Message}");
                        return;
                    }
                }
            }

            if (operation != Operation.BrowserPreview) ResetHomeLog();
            HomeResults.SelectedItem = HomeLogTab;
            SetResultsCollapsed(false);
            if (operation == Operation.Build)
            {
                HomeChanges.Items.Clear();
                HomeChangesBadge.Visibility = Visibility.Collapsed;
                HomeChangesAddedBadge.Visibility = Visibility.Collapsed;
                HomeChangesModifiedBadge.Visibility = Visibility.Collapsed;
                HomeChangesDeletedBadge.Visibility = Visibility.Collapsed;
                HomeChangesSummary.Text = "正在生成…";
                HomeChangesSummary.Visibility = Visibility.Visible;
            }

            int position = 0;
            foreach (var project in selected)
            {
                if (cancellation.IsCancellationRequested) break;
                string progress = $"「{project.Name}」 · {++position}/{selected.Length}";
                ShowBatchProgress(operation, progress);
                WriteHomeLog($"[{project.Name}] {operation switch { Operation.Build => "构建", Operation.BrowserPreview => "打开预览", Operation.Apply => "应用同步", Operation.RetryPush => "重试推送", _ => "提交并推送" }}");
                if (operation == Operation.BrowserPreview)
                {
                    try
                    {
                        if (await TryStartSitePreviewAsync(project, cancellation.Token)) completed++;
                        else failed++;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    continue;
                }

                projectRuns.TryGetValue(project.Id, out var previousState);
                string? expected = publishing ? previousState?.Synced : previousState?.Plan;
                string? pendingToken = publishing && expected?.Count(ch => ch == ':') == 2 ? expected : null;

                if (operation == Operation.Build)
                {
                    projectRuns.Remove(project.Id);
                    DeletePreview(project);
                    InvalidateSuccessfulBuild(project);
                }
                else if (operation == Operation.Apply && HasSuccessfulBuild(project))
                {
                    RememberSuccessfulBuild(project);
                }

                Action<string> logLine = line => Dispatcher.BeginInvoke(() => WriteHomeLog(line));

                try
                {
                    var config = ProjectConfig.Parse(project.Configuration);
                    if (operation == Operation.Build)
                    {
                        var previewResult = await SyncEngine.BuildPreviewAsync(config, logLine, cancellation.Token);
                        var batchChanges = BuildBatchChanges(project.Name, previewResult);
                        projectRuns[project.Id] = new RunState(previewResult.PlanToken, null, "可同步", batchChanges);
                        try { SavePreview(project, previewResult.PlanToken, batchChanges); }
                        catch (IOException error) { WriteHomeLog($"无法保存预览记录：{error.Message}"); }
                        catch (UnauthorizedAccessException error) { WriteHomeLog($"无法保存预览记录：{error.Message}"); }

                        completed++;
                        WriteHomeLog("操作完成。");
                        RememberSuccessfulBuild(project);
                        if (ShouldAutoOpenPreview(project) && !cancellation.IsCancellationRequested)
                        {
                            ShowBatchProgress(Operation.BrowserPreview, progress);
                            autoPreviewFailed |= !await TryStartSitePreviewAsync(project, cancellation.Token);
                        }
                    }
                    else if (operation == Operation.Apply)
                    {
                        var applied = await SyncEngine.ApplySyncAsync(config, expected!, cleanupProjects.Contains(project), logLine);
                        DeletePreview(project);
                        projectRuns[project.Id] = new RunState(null, $"{applied.BaseCommit}:{applied.SnapshotCommit}", "可推送");
                        completed++;
                        WriteHomeLog("操作完成。");
                    }
                    else
                    {
                        var publishResult = await SyncEngine.PublishAsync(config, expected!,
                            summaries.GetValueOrDefault(project.Id), logLine,
                            onPendingCommit: rec =>
                            {
                                pendingToken = rec.ToToken();
                                try { SiteLift.App.App.Store.SavePendingPush(project.Id, rec); }
                                catch (Exception error) { logLine($"无法保存待推送记录：{error.Message}"); }
                            });
                        SiteLift.App.App.Store.DeletePendingPush(project.Id);
                        projectRuns[project.Id] = new RunState(null, null, publishResult.Pushed ? "已推送" : "无需推送");
                        completed++;
                        WriteHomeLog("操作完成。");
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    WriteHomeLog("批量操作已停止。");
                    break;
                }
                catch (Exception error)
                {
                    failed++;
                    if (error is SyncException syncEx && syncEx.Code == SyncErrorCode.WindowsGitMissing)
                    {
                        windowsGitMissing = true;
                        windowsGitMissingMessage = syncEx.Message;
                    }

                    if (pendingToken is not null)
                    {
                        projectRuns[project.Id] = new RunState(null, pendingToken, "已提交，待推送");
                    }
                    else if (operation == Operation.Apply &&
                             error is SyncException applyEx &&
                             applyEx.Code == SyncErrorCode.RepositoryBusy &&
                             previousState is not null)
                    {
                        // Preserve valid preview state on transient repository-busy failure.
                        projectRuns[project.Id] = previousState;
                    }
                    else if (operation == Operation.Publish &&
                             error is SyncException pubEx &&
                             pubEx.Code is SyncErrorCode.RepositoryBusy or SyncErrorCode.WindowsGitMissing or SyncErrorCode.Push &&
                             previousState is not null)
                    {
                        // Preserve valid synced state when no commit was created yet.
                        projectRuns[project.Id] = previousState;
                    }
                    else
                    {
                        projectRuns[project.Id] = new RunState(null, null, "操作失败");
                        if (operation == Operation.Apply)
                            DeletePreview(project);
                    }
                    WriteHomeLog($"{project.Name}：操作失败。{error.Message}");
                }
            }
        }
        finally
        {
            running = false;
            IsRowSelectionEnabled = true;
            batchCancellation = null;
            BatchProgressPanel.Visibility = Visibility.Collapsed;
            HomeActions.IsEnabled = true;
            RefreshProjects();
            ShowSavedChanges();
            if (!cancelledBeforeStart)
            {
                if (operation == Operation.Build && completed == selected.Length && !autoPreviewFailed)
                {
                    HomeResults.SelectedItem = HomeChangesTab;
                    SetResultsCollapsed(false);
                }
                string result = (cancellation.IsCancellationRequested ? "已停止 · " : "") +
                    $"完成 {completed} 个 · 失败 {failed} 个";
                if (windowsGitMissing && windowsGitMissingMessage is not null)
                    ShowWindowsGitMissing(HomeStatus, windowsGitMissingMessage);
                else if (failed > 0)
                    HomeStatus.ShowFeedback(result + "。详情见运行日志。", InfoBarSeverity.Warning);
                else if (autoPreviewFailed)
                    HomeStatus.ShowFeedback(result + "。部分浏览器预览未打开，详情见运行日志。", InfoBarSeverity.Warning);
                else
                    HomeStatus.ShowFeedback(result, cancellation.IsCancellationRequested ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
            }
            if (closeAfterBatch)
            {
                closeAfterBatch = false;
                Close();
            }
        }
    }

    private static BatchChange[] BuildBatchChanges(string projectName, SyncPreviewResult preview)
    {
        var items = new List<BatchChange>(preview.Changes.Length + preview.CleanupCandidates.Length);
        foreach (var change in preview.Changes)
        {
            string? action = change.Status switch
            {
                "A" => "新增",
                "M" => "修改",
                "D" => "源端删除",
                "T" => "类型变更",
                _ => null
            };
            if (action is not null)
                items.Add(new BatchChange(projectName, action, change.Path));
        }
        foreach (string cleanup in preview.CleanupCandidates)
            items.Add(new BatchChange(projectName, "目标清理", cleanup));
        return items.ToArray();
    }

    private void WriteHomeLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (homeLogBuffer.Length > MaxHomeLogLength)
        {
            int removeChars = homeLogBuffer.Length - (MaxHomeLogLength / 2);
            int newline = homeLogBuffer.ToString().IndexOf('\n', removeChars);
            int trimCount = newline >= 0 ? newline + 1 : removeChars;
            homeLogBuffer.Remove(0, trimCount);
            int dropBlocks = HomeLog.Document.Blocks.Count / 2;
            for (int i = 0; i < dropBlocks && HomeLog.Document.Blocks.FirstBlock is not null; i++)
                HomeLog.Document.Blocks.Remove(HomeLog.Document.Blocks.FirstBlock);
        }
        homeLogBuffer.AppendLine(line);
        var run = new Run(line);
        run.SetResourceReference(TextElement.ForegroundProperty, ResolveLogBrushKey(line));
        var paragraph = new Paragraph(run)
        {
            Margin = new Thickness(0),
            LineHeight = 19,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        double viewport = HomeLog.ActualWidth - HomeLog.Padding.Left - HomeLog.Padding.Right - 24;
        double requiredWidth = line.Length * 8.4 + 48;
        if (viewport > 100 && requiredWidth > viewport)
        {
            double currentWidth = double.IsNaN(HomeLog.Document.PageWidth) ? viewport : HomeLog.Document.PageWidth;
            if (requiredWidth > currentWidth)
                HomeLog.Document.PageWidth = requiredWidth;
        }
        HomeLog.Document.Blocks.Add(paragraph);
        EmptyHomeLogPanel.Visibility = Visibility.Collapsed;
        HomeLog.ScrollToEnd();
        _ = Task.Run(() => SiteLift.App.App.AppendDiagnostic(line));
    }

    private static string ResolveLogBrushKey(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith('[') && trimmed.IndexOf(']') > 1)
            return "AccentTextFillColorPrimaryBrush";
        if (trimmed.Contains("失败", StringComparison.Ordinal) ||
            trimmed.Contains("无法", StringComparison.Ordinal) ||
            trimmed.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ERR!", StringComparison.Ordinal) ||
            trimmed.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Exception", StringComparison.Ordinal))
            return "SystemFillColorCriticalBrush";
        if (trimmed.Contains("已停止", StringComparison.Ordinal) ||
            trimmed.Contains("警告", StringComparison.Ordinal) ||
            trimmed.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("WARN", StringComparison.Ordinal))
            return "SystemFillColorCautionBrush";
        if (trimmed.Contains("操作完成", StringComparison.Ordinal) ||
            trimmed.Contains("已推送", StringComparison.Ordinal) ||
            trimmed.Contains("built in", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('✓'))
            return "SystemFillColorSuccessBrush";
        return "TextFillColorSecondaryBrush";
    }

    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断（运行日志可能包含路径、命令或其他敏感信息）",
            Filter = "诊断压缩包 (*.zip)|*.zip",
            FileName = $"SiteLift-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var output = File.Open(dialog.FileName, FileMode.Create);
            using var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create);
            using (var summary = new StreamWriter(archive.CreateEntry("summary.txt").Open()))
            {
                summary.WriteLine($"SiteLift {typeof(MainWindow).Assembly.GetName().Version}");
                summary.WriteLine($"OS: {Environment.OSVersion}");
                summary.WriteLine($"Saved projects: {savedProjects.Count}");
                summary.WriteLine($"Projects with previews: {projectRuns.Values.Count(state => state.Plan is not null)}");
                summary.WriteLine($"Projects pending push: {projectRuns.Values.Count(state => state.Progress == "已提交，待推送")}");
            }
            if (Directory.Exists(SiteLift.App.App.DiagnosticsDirectory))
            {
                var logs = Directory.EnumerateFiles(SiteLift.App.App.DiagnosticsDirectory, "sitelift-*.log")
                    .Concat(Directory.EnumerateFiles(SiteLift.App.App.DiagnosticsDirectory, "projectsync-*.log"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Take(7);
                foreach (string log in logs)
                {
                    using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var entry = archive.CreateEntry(Path.GetFileName(log)).Open();
                    input.CopyTo(entry);
                }
            }
            HomeStatus.ShowFeedback("已导出诊断。分享前请检查日志是否包含敏感信息。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法导出诊断：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private MessageBoxResult ConfirmProjectAction(string title, string message, string accept, string decline,
        bool canCancel, bool destructive = false) =>
        FluentDialog.Ask(this, title, message, accept, decline, canCancel, destructive);
}

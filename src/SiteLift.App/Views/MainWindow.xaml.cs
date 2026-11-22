using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool running;
    private string? displayedBranchKey;
    private string selectedThemePreference = "Light";
    private bool resultsCollapsed;
    private GridLength savedProjectsRowHeight = new(40, GridUnitType.Star);
    private GridLength savedResultsRowHeight = new(60, GridUnitType.Star);

    private readonly Dictionary<string, TextBox> fields;
    private JsonObject current = new();
    private bool loading;
    private bool suppressPathAutoInspect;
    private bool jsonDraft;
    private bool restoreDetectedOptions = true;

    private void DashboardTableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var table = (DataGrid)sender;
        var frame = FindAncestorBorder(table);
        if (frame is null) return;
        double radius = Math.Max(0, frame.CornerRadius.TopLeft - frame.BorderThickness.Left);
        table.Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    private static Border? FindAncestorBorder(DependencyObject element)
    {
        for (var current = LogicalTreeHelper.GetParent(element); current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is Border border) return border;
        }
        return null;
    }

    private void HomeResultsTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HomeResults)) return;
        UpdateHomeResultsHeaderTools();
        if (resultsCollapsed)
            SetResultsCollapsed(false);
    }

    private void HomeResultsTabHeaderMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (resultsCollapsed)
            SetResultsCollapsed(false);
    }

    private void ToggleResultsCollapse(object sender, RoutedEventArgs e) =>
        SetResultsCollapsed(!resultsCollapsed);

    private void SetResultsCollapsed(bool collapsed)
    {
        if (resultsCollapsed == collapsed || HomeProjectsRow is null || HomeResultsRow is null || HomeSplitterRow is null)
            return;

        resultsCollapsed = collapsed;
        if (collapsed)
        {
            if (HomeProjectsRow.ActualHeight > 0 && HomeResultsRow.ActualHeight > 0)
            {
                savedProjectsRowHeight = new GridLength(HomeProjectsRow.ActualHeight, GridUnitType.Star);
                savedResultsRowHeight = new GridLength(HomeResultsRow.ActualHeight, GridUnitType.Star);
            }
            HomeSplitterRow.Height = new GridLength(12);
            HomeWorkspaceSplitter.Visibility = Visibility.Collapsed;
            HomeResults.Tag = "Collapsed";
            HomeProjectsRow.Height = new GridLength(1, GridUnitType.Star);
            HomeResultsRow.MinHeight = 40;
            HomeResultsRow.Height = new GridLength(40);
            ResultsCollapseChevron.Data = System.Windows.Media.Geometry.Parse("M 2.5,7.8 L 6,4.2 L 9.5,7.8");
            ToggleResultsCollapseButton.ToolTip = "展开结果区";
        }
        else
        {
            HomeResults.Tag = null;
            HomeWorkspaceSplitter.Visibility = Visibility.Visible;
            HomeSplitterRow.Height = new GridLength(16);
            HomeResultsRow.MinHeight = 140;
            HomeProjectsRow.Height = savedProjectsRowHeight;
            HomeResultsRow.Height = savedResultsRowHeight;
            ResultsCollapseChevron.Data = System.Windows.Media.Geometry.Parse("M 2.5,4.2 L 6,7.8 L 9.5,4.2");
            ToggleResultsCollapseButton.ToolTip = "收起结果区";
        }
    }

    private void UpdateHomeResultsHeaderTools()
    {
        if (HomeChangesHeaderTools is null || HomeLogHeaderTools is null || HomeResults is null)
            return;
        bool isLog = ReferenceEquals(HomeResults.SelectedItem, HomeLogTab);
        HomeChangesHeaderTools.Visibility = isLog ? Visibility.Collapsed : Visibility.Visible;
        HomeLogHeaderTools.Visibility = isLog ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool followingSystemTheme;

    private void CycleTheme(object sender, RoutedEventArgs e)
    {
        string nextTheme = selectedThemePreference switch
        {
            "Light" => "Dark",
            "Dark" => "System",
            _ => "Light"
        };
        ApplyTheme(nextTheme, persist: true);
    }

    private void UpdateThemeMenuVisuals()
    {
        if (ThemeSunIcon is null || ThemeMoonIcon is null || ThemeSystemIcon is null || ThemeMenuButton is null)
            return;

        ThemeSunIcon.Visibility = selectedThemePreference == "Light" ? Visibility.Visible : Visibility.Collapsed;
        ThemeMoonIcon.Visibility = selectedThemePreference == "Dark" ? Visibility.Visible : Visibility.Collapsed;
        ThemeSystemIcon.Visibility = selectedThemePreference == "System" ? Visibility.Visible : Visibility.Collapsed;

        (string current, string next) = selectedThemePreference switch
        {
            "Dark" => ("深色", "跟随系统"),
            "System" => ("跟随系统", "浅色"),
            _ => ("浅色", "深色")
        };
        string hint = $"主题：{current}（点击切换为{next}）";
        ThemeMenuButton.ToolTip = hint;
        System.Windows.Automation.AutomationProperties.SetName(ThemeMenuButton, hint);
    }

    private void ApplyTheme(string theme, bool persist)
    {
        selectedThemePreference = theme is "System" or "Dark" or "Light" ? theme : "Light";
        if (followingSystemTheme)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            followingSystemTheme = false;
        }
        if (selectedThemePreference == "System")
        {
            if (IsLoaded)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica);
                followingSystemTheme = true;
            }
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        }
        else
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                selectedThemePreference == "Dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica);
        }

        if (persist)
            SiteLift.App.App.Store.SavePreferences(new AppPreferences(selectedThemePreference));
        UpdateThemeMenuVisuals();
        RefreshFrameworkVisuals();
    }

    private void OnApplicationThemeChanged(Wpf.Ui.Appearance.ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        UpdateThemeMenuVisuals();
        RefreshFrameworkVisuals();
    }

    public MainWindow()
    {
        InitializeComponent();
        var savedTheme = SiteLift.App.App.Store.LoadPreferences().Theme;
        ApplyTheme(savedTheme, persist: false);
        Loaded += (_, _) =>
        {
            if (selectedThemePreference == "System" && !followingSystemTheme)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica);
                followingSystemTheme = true;
            }
            UpdateThemeMenuVisuals();
        };
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        Closed += (_, _) =>
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
            if (followingSystemTheme) Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
        };
        AppVersionText.Text = $"v{CurrentVersion.ToString(3)}";
        UpdateHomeResultsHeaderTools();
        fields = new()
        {
            ["windowsRepo"] = windowsRepo,
            ["remoteUrl"] = remoteUrl,
            ["githubUsername"] = githubUsername,
            ["gitSshCommand"] = gitSshCommand,
            ["distro"] = distro,
            ["wslUser"] = wslUser,
            ["wslRepo"] = wslRepo,
            ["pullPaths"] = pullPath,
            ["buildCommand"] = buildCommand,
            ["previewCommand"] = previewCommand,
            ["reinstallCommand"] = reinstallCommand,
            ["themeUpgradeCommand"] = themeUpgradeCommand,
            ["nodeBin"] = nodeBin,
            ["include"] = include,
            ["exclude"] = exclude,
            ["excludeSuffixes"] = excludeSuffixes,
        };
        foreach (var input in fields.Values)
            input.TextChanged += (_, _) => FormChanged();
        branch.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => FormChanged()));
        branch.SelectionChanged += (_, _) => FormChanged();
        foreach (var input in new[] { windowsRepo, remoteUrl, githubUsername, gitSshCommand })
            input.TextChanged += (_, _) =>
            {
                if (!loading) ClearRemoteBranches();
            };
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox))
            .AddValueChanged(remote, (_, _) => RemoteChanged());
        windowsRepo.TextChanged += (_, _) =>
        {
            LocalGitStatus.IsOpen = false;
            LocalInspectStatus.IsOpen = false;
            if (loading)
            {
                UpdateRepositoryStateUi();
                return;
            }
            ResetLocalInspectionState();
            string text = remote.Text;
            loading = true;
            remote.ItemsSource = null;
            remote.Text = text;
            loading = false;
            UpdateRepositoryStateUi();
            if (!suppressPathAutoInspect)
                _ = ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: false, debounceMs: 250, showFeedback: false);
        };
        wslRepo.TextChanged += (_, _) =>
        {
            if (loading || detectingWsl) return;
            NodeVersion.ItemsSource = null;
            NodeHint.Text = "";
            WslDistribution.ItemsSource = null;
            WslUserChoice.ItemsSource = null;
            distro.Clear();
            wslUser.Clear();
            EnvironmentFeedback.ShowFeedback("仓库路径已修改，请检测环境。");
        };
        PreviewAfterBuild.Checked += (_, _) => FormChanged();
        PreviewAfterBuild.Unchecked += (_, _) => FormChanged();
        CleanUntracked.Checked += (_, _) => FormChanged();
        CleanUntracked.Unchecked += (_, _) => FormChanged();
        ProjectName.TextChanged += (_, _) => FormChanged();
        Config.TextChanged += (_, _) =>
        {
            if (loading) return;
            jsonDraft = !JsonMatchesForm();
            ResetPreview();
            UpdateSaveProjectAction();
            JsonStatus.IsOpen = false;
        };
        InitializeProjects();
    }

    private static bool IsPathList(string key) => key is "include" or "exclude" or "excludeSuffixes" or "pullPaths";

    private bool ConfirmExternalConfiguration(string configuration, bool onlyChanged = false)
    {
        var reviewItems = ProjectConfig.ExtractSecurityReviewItems(configuration, onlyChanged ? current : null);
        if (reviewItems.Count == 0)
            return true;

        string details = string.Join("\n\n", reviewItems.Select(item => $"{item.Label}\n{item.Value}"));
        return ConfirmProjectAction("检查配置中的命令与敏感选项",
            "项目操作可能以你的权限运行以下命令行、修改 PATH 或覆写/清理目录。请逐条核对，并仅在信任来源时继续。\n\n" + details,
            "接受配置", "取消", canCancel: false) == MessageBoxResult.Yes;
    }

    private void LoadConfig(string text)
    {
        var parsed = ProjectConfig.ParseRawObject(text);
        if (!parsed.ContainsKey("pullPaths") && parsed["pullPath"] is JsonNode legacy)
            parsed["pullPaths"] = new JsonArray(legacy.DeepClone());
        parsed.Remove("pullPath");
        string configuredRemote = parsed["remote"]?.GetValue<string>() ?? "";
        string configuredBranch = parsed["branch"]?.GetValue<string>() ?? "";
        var values = new Dictionary<string, string>();
        foreach (string key in fields.Keys)
        {
            var node = parsed[key];
            values[key] = node is null ? "" : IsPathList(key)
                ? string.Join(Environment.NewLine, node.AsArray().Select(item =>
                    item?.GetValue<string>() ?? throw new JsonException($"{key} 包含空项。")))
                : node.GetValue<string>();
        }
        string name = parsed["name"]?.GetValue<string>() ?? "";
        bool clean = parsed["cleanUntracked"]?.GetValue<bool>() ?? false;
        loading = true;
        ResetLocalInspectionState();
        current = parsed;
        remote.Text = configuredRemote;
        branch.Text = configuredBranch;
        foreach (var field in fields)
            field.Value.Text = values[field.Key];
        LocalInspectStatus.IsOpen = false;
        RemoteTestStatus.IsOpen = false;
        LocalGitStatus.IsOpen = false;
        WindowsGitStatus.IsOpen = false;
        EnvironmentFeedback.IsOpen = false;
        JsonStatus.IsOpen = false;
        RemoteConnectionOptions.IsExpanded = !string.IsNullOrWhiteSpace(githubUsername.Text) ||
            !string.IsNullOrWhiteSpace(gitSshCommand.Text);
        ExtraPathOptions.IsExpanded = false;
        MaintenanceCommandOptions.IsExpanded = !string.IsNullOrWhiteSpace(reinstallCommand.Text) ||
            !string.IsNullOrWhiteSpace(themeUpgradeCommand.Text);
        RestoreChoices(remote, configuredRemote, ReadChoices<string[]>(RemoteChoiceKey()));
        displayedBranchKey = BranchChoiceKey();
        RestoreChoices(branch, configuredBranch, ReadChoices<string[]>(displayedBranchKey));
        RestoreChoices(WslDistribution, distro.Text, ReadChoices<string[]>(DistributionChoiceKey()));
        RestoreChoices(WslUserChoice, wslUser.Text, ReadChoices<string[]>(UserChoiceKey(distro.Text)));
        if (restoreDetectedOptions || nodeBin.Text.Length > 0)
            RestoreSavedNodeOption();
        else
        {
            NodeVersion.ItemsSource = null;
            NodeHint.Text = "";
        }
        CleanUntracked.IsChecked = clean;
        PreviewAfterBuild.IsChecked = parsed["previewAfterBuild"]?.GetValue<bool>() ?? false;
        BackupGitOnReset.IsChecked = false;
        ProjectName.Text = name;
        loading = false;
        jsonDraft = false;
        FormChanged();
    }

    private void FormChanged()
    {
        if (loading) return;
        foreach (var field in fields)
            current[field.Key] = IsPathList(field.Key)
                ? JsonSerializer.SerializeToNode(field.Value.Text.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                : JsonValue.Create(field.Value.Text.Trim());
        current["remote"] = remote.Text.Trim();
        current["branch"] = branch.Text.Trim();
        current["name"] = ProjectName.Text.Trim();
        current["previewAfterBuild"] = PreviewAfterBuild.IsChecked == true;
        current["cleanUntracked"] = CleanUntracked.IsChecked == true;
        if (jsonDraft && JsonMatchesForm())
        {
            jsonDraft = false;
            JsonStatus.IsOpen = false;
        }
        if (!jsonDraft)
        {
            loading = true;
            Config.Text = SerializeConfig();
            loading = false;
        }
        ResetPreview();
        UpdateRepositoryStateUi();
        UpdateWslEnvironmentUi();
        UpdateSyncSettingsUi();
        UpdateSaveProjectAction();
    }

    private void UpdateSyncSettingsUi()
    {
        int includeCount = Lines(include.Text).Length;
        SyncIncludeBadge.Content = includeCount > 0 ? $"已配 {includeCount} 项" : "未配置";
        SyncIncludeBadge.Appearance = includeCount > 0 ? Wpf.Ui.Controls.ControlAppearance.Success : Wpf.Ui.Controls.ControlAppearance.Secondary;

        int excludeCount = Lines(exclude.Text).Length;
        int suffixCount = Lines(excludeSuffixes.Text).Length;
        if (excludeCount == 0 && suffixCount == 0)
        {
            SyncExcludeBadge.Content = "无排除规则";
            SyncExcludeBadge.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
        }
        else
        {
            SyncExcludeBadge.Content = (excludeCount, suffixCount) switch
            {
                ( > 0, > 0) => $"{excludeCount} 路径 · {suffixCount} 后缀",
                ( > 0, 0) => $"{excludeCount} 条排除路径",
                _ => $"{suffixCount} 个排除后缀"
            };
            SyncExcludeBadge.Appearance = Wpf.Ui.Controls.ControlAppearance.Info;
        }

        CleanUntrackedWarning.IsOpen = CleanUntracked.IsChecked == true;
    }

    private bool JsonMatchesForm()
    {
        try { return JsonNode.DeepEquals(current, JsonNode.Parse(Config.Text)); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    private void NormalizeSyncPathField(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (!loading && sender is TextBox field)
            TryNormalizeSyncPaths(field);
    }

    private bool TryNormalizeSyncPaths(params TextBox[] boxes)
    {
        try
        {
            foreach (string suffix in Lines(excludeSuffixes.Text))
                SyncPath.ValidateSuffix(suffix);
            SyncPath.ValidateNodeBin(nodeBin.Text);
            var updates = boxes.Select(box => (Box: box, Text: string.Join(Environment.NewLine,
                Lines(box.Text).Select(path => NormalizeSyncPath(path,
                    box == include ? "同步路径" : box == pullPath ? "拉取目录" : "排除路径"))))).ToArray();
            foreach (var update in updates)
                if (update.Box.Text != update.Text)
                    update.Box.Text = update.Text;
            return true;
        }
        catch (ArgumentException error)
        {
            Status.ShowFeedback(error.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private string NormalizeSyncPath(string input, string label) =>
        SyncPath.Normalize(input, label, windowsRepo.Text, wslRepo.Text, distro.Text.Trim());

    private static string UnquotePath(string value) => SyncPath.UnquotePath(value);

    private string SerializeConfig() => current.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Windows 本地项目目录" };
        if (Directory.Exists(windowsRepo.Text))
            dialog.InitialDirectory = windowsRepo.Text;
        if (dialog.ShowDialog(this) != true)
            return;

        suppressPathAutoInspect = true;
        try
        {
            windowsRepo.Text = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(ProjectName.Text))
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.FolderName));
                if (!string.IsNullOrWhiteSpace(folderName))
                    ProjectName.Text = folderName;
            }
        }
        finally
        {
            suppressPathAutoInspect = false;
        }

        _ = ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: true, debounceMs: 0, showFeedback: false);
    }

    private void ClearConfiguration(object sender, RoutedEventArgs e)
    {
        string message = activeProject is null
            ? "将当前表单和 JSON 恢复到新建项目的初始状态？"
            : $"清空「{activeProject.Name}」的配置？将保留项目名称，点击「保存项目」后生效。";
        if (ConfirmProjectAction("清空配置",
            message,
            "清空配置", "取消", canCancel: false, destructive: true) != MessageBoxResult.Yes)
            return;

        restoreDetectedOptions = false;
        var initialConfig = new JsonObject();
        if (activeProject is not null)
            initialConfig["name"] = activeProject.Name;
        LoadConfig(initialConfig.ToJsonString());
        if (activeProject is null)
        {
            originalConfig = (JsonObject)current.DeepClone();
            UpdateSaveProjectAction();
        }
        LocalGitStatus.IsOpen = false;
    }

    private void UpdateJsonSyncBadge()
    {
        if (!jsonDraft)
        {
            JsonSyncBadge.Content = "已与表单同步";
            JsonSyncBadge.Appearance = Wpf.Ui.Controls.ControlAppearance.Success;
            return;
        }

        bool validObject;
        try { validObject = ProjectConfig.ParseRawObject(Config.Text) is not null; }
        catch (Exception error) when (error is JsonException or ArgumentException) { validObject = false; }

        JsonSyncBadge.Content = validObject ? "待应用到表单" : "JSON 格式错误";
        JsonSyncBadge.Appearance = validObject
            ? Wpf.Ui.Controls.ControlAppearance.Caution
            : Wpf.Ui.Controls.ControlAppearance.Danger;
    }

    private void CopyJson(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Config.Text);
            JsonStatus.ShowFeedback("已复制当前 JSON 配置到剪贴板。", InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            JsonStatus.ShowFeedback($"无法写入剪贴板：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private void ApplyJson(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ConfirmExternalConfiguration(Config.Text, onlyChanged: true)) return;
            LoadConfig(Config.Text);
            JsonStatus.ShowFeedback("已将 JSON 配置应用到表单。", InfoBarSeverity.Success);
            _ = ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: false, debounceMs: 0, showFeedback: false);
        }
        catch (Exception error) { JsonStatus.ShowFeedback($"配置无效：{error.Message}", InfoBarSeverity.Error); }
    }

    private void ShowPendingJson(string message)
    {
        SettingsTabs.SelectedItem = JsonSettingsTab;
        JsonStatus.ShowFeedback(message, InfoBarSeverity.Warning);
        Config.Focus();
    }

    private void ResetPreview()
    {
        Status.IsOpen = false;
    }

    private void Open(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入项目配置", Filter = "项目配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            string configuration = File.ReadAllText(dialog.FileName);
            if (!ConfirmExternalConfiguration(configuration)) return;
            LoadConfig(configuration);
            _ = ScheduleLocalRepositoryInspectionAsync(autoFillBlanks: false, debounceMs: 0, showFeedback: false);
        }
        catch (IOException error) { JsonStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        catch (UnauthorizedAccessException error) { JsonStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        catch (JsonException error) { JsonStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        catch (InvalidOperationException error) { JsonStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (jsonDraft)
        {
            ShowPendingJson("请先应用到表单，再导出 JSON。");
            return;
        }

        if (!TryNormalizeSyncPaths(include, exclude, pullPath)) return;

        string name = string.Join("_", ProjectName.Text.Trim().Split(Path.GetInvalidFileNameChars())).TrimEnd('.', ' ');
        var dialog = new SaveFileDialog
        {
            Filter = "项目配置 (*.json)|*.json",
            Title = "导出 JSON",
            FileName = string.IsNullOrWhiteSpace(name) ? "build-sync.json" : $"{name}.json"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, SerializeConfig());
            Status.ShowFeedback($"已导出 JSON 到 {dialog.FileName}", InfoBarSeverity.Success);
        }
        catch (Exception error) { Status.ShowFeedback(error.Message, InfoBarSeverity.Error); }
    }

    private void RemoteChanged()
    {
        if (loading) return;
        ClearRemoteBranches();
        FormChanged();
    }

    private void ClearRemoteBranches()
    {
        string key = BranchChoiceKey();
        if (key == displayedBranchKey) return;
        displayedBranchKey = key;

        loading = true;
        RestoreChoices(branch, branch.Text, ReadChoices<string[]>(key));
        loading = false;
    }

    private async void TestRemote(object sender, RoutedEventArgs e)
    {
        if (running || jsonDraft)
        {
            if (jsonDraft) ShowPendingJson("请先应用到表单，再测试远端连接。");
            return;
        }

        string urlText = remoteUrl.Text.Trim();
        string localPath = windowsRepo.Text.Trim();
        if (urlText.Length == 0)
        {
            if (!IsLocalDirectoryGitRepository(localPath))
            {
                RemoteTestStatus.ShowFeedback("本地目录尚未连接 Git，请先填写「远端仓库地址」再测试连接。", InfoBarSeverity.Warning);
                remoteUrl.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(remote.Text))
            {
                RemoteTestStatus.ShowFeedback("请填写或选择「远端名称」（例如 origin），或直接填写「远端仓库地址」。", InfoBarSeverity.Warning);
                remote.Focus();
                return;
            }
        }

        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        ClearRemoteBranches();
        RemoteTestStatus.ShowFeedback("正在连接远端并读取分支列表… 如弹出浏览器，请完成 GitHub 登录。");
        try
        {
            var config = ProjectConfig.Parse(SerializeConfig());
            string[] branches = await SyncEngine.TestConnectionAsync(config);
            string text = branch.Text.Trim();
            bool autoSelected = false;
            if (text.Length == 0 && branches.Length > 0)
            {
                text = branches.Contains("main") ? "main"
                    : branches.Contains("master") ? "master"
                    : branches.Length == 1 ? branches[0] : "";
                autoSelected = text.Length > 0;
            }

            loading = true;
            RestoreChoices(branch, text, branches);
            loading = false;
            if (autoSelected)
                FormChanged();

            string? cacheError = SaveChoices(BranchChoiceKey(), branches);
            if (cacheError is not null)
            {
                RemoteTestStatus.ShowFeedback(cacheError, InfoBarSeverity.Warning);
            }
            else if (branches.Length == 0)
            {
                RemoteTestStatus.ShowFeedback("连接成功，但远端暂无分支。", InfoBarSeverity.Informational);
            }
            else if (autoSelected)
            {
                RemoteTestStatus.ShowFeedback($"连接成功，已读取 {branches.Length} 个远端分支，并自动选中「{text}」。", InfoBarSeverity.Success);
            }
            else if (text.Length == 0)
            {
                RemoteTestStatus.ShowFeedback($"连接成功，已读取 {branches.Length} 个远端分支，请从「目标分支」下拉列表选择。", InfoBarSeverity.Success);
            }
            else if (branches.Contains(text))
            {
                RemoteTestStatus.ShowFeedback($"连接成功，已读取 {branches.Length} 个远端分支，目标分支「{text}」可用。", InfoBarSeverity.Success);
            }
            else
            {
                RemoteTestStatus.ShowFeedback($"连接成功，已读取 {branches.Length} 个远端分支，但远端未找到「{text}」，请从下拉列表重新选择。", InfoBarSeverity.Warning);
            }
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(RemoteTestStatus, error.Message);
        }
        catch (Exception error)
        {
            RemoteTestStatus.ShowFeedback(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
            UpdateRepositoryStateUi();
        }
    }

    private static void ShowWindowsGitMissing(Wpf.Ui.Controls.InfoBar status, string message)
    {
        const string downloadUrl = "https://git-scm.com/install/windows";
        var action = new Wpf.Ui.Controls.Button { Content = "下载 Git for Windows" };
        action.Click += (_, _) =>
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception failed)
            {
                SiteLift.App.App.AppendDiagnostic(failed.Message);
                status.ShowFeedback($"{message} 下载地址：{downloadUrl}", InfoBarSeverity.Warning);
            }
        };
        status.ShowFeedback(message, InfoBarSeverity.Warning, action);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (running || detectingWsl || !CanLeaveProject())
        {
            e.Cancel = true;
            if (batchCancellation is not null)
            {
                closeAfterBatch = true;
                StopBatch(this, new RoutedEventArgs());
            }
            else if (running || detectingWsl)
            {
                var targetBar = ProjectPage.Visibility == Visibility.Visible ? Status : HomeStatus;
                targetBar.ShowFeedback("当前操作正在执行，请等待完成后再关闭窗口。", InfoBarSeverity.Informational);
            }
        }
        if (!e.Cancel)
        {
            CancelLocalInspection();
            ForceStopAllPreviews();
        }
    }
}

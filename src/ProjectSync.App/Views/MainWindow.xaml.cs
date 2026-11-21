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
using ProjectSync.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool running;
    private string? displayedBranchKey;
    private bool initializingTheme = true;

    private readonly Dictionary<string, TextBox> fields;
    private JsonObject current = new();
    private bool loading;
    private bool jsonDraft;
    private bool restoreDetectedOptions = true;

    private void DashboardTableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var table = (DataGrid)sender;
        var frame = (Border)table.Parent;
        var radius = frame.CornerRadius.TopLeft - frame.BorderThickness.Left;
        table.Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    private bool followingSystemTheme;

    private void ChooseTheme(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ComboBoxItem item } || item.Tag is not string theme) return;
        ApplyTheme(theme, persist: !initializingTheme);
    }

    private void ApplyTheme(string theme, bool persist)
    {
        if (followingSystemTheme)
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
            followingSystemTheme = false;
        }
        if (theme == "System")
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
                theme == "Dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica);
        }

        if (persist)
            ProjectSync.App.App.Store.SavePreferences(new AppPreferences(theme));
    }

    public MainWindow()
    {
        InitializeComponent();
        var savedTheme = ProjectSync.App.App.Store.LoadPreferences().Theme;
        foreach (ComboBoxItem item in ThemeChoice.Items)
        {
            if (item.Tag as string == savedTheme)
            {
                ThemeChoice.SelectedItem = item;
                break;
            }
        }
        initializingTheme = false;
        Loaded += (_, _) =>
        {
            if (ThemeChoice.SelectedItem is ComboBoxItem { Tag: "System" } && !followingSystemTheme)
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica);
                followingSystemTheme = true;
            }
        };
        Closed += (_, _) =>
        {
            if (followingSystemTheme) Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(this);
        };
        AppVersionText.Text = $"v{CurrentVersion.ToString(3)}";
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
            UpdateLocalGitAction();
            if (loading) return;
            string text = remote.Text;
            loading = true;
            remote.ItemsSource = null;
            remote.Text = text;
            loading = false;
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
        current = parsed;
        remote.Text = configuredRemote;
        branch.Text = configuredBranch;
        foreach (var field in fields)
            field.Value.Text = values[field.Key];
        RemoteDetectionStatus.IsOpen = false;
        RemoteTestStatus.IsOpen = false;
        EnvironmentFeedback.IsOpen = false;
        JsonStatus.IsOpen = false;
        RemoteConnectionOptions.IsExpanded = !string.IsNullOrWhiteSpace(githubUsername.Text) ||
            !string.IsNullOrWhiteSpace(gitSshCommand.Text);
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
        UpdateSaveProjectAction();
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
        var dialog = new OpenFolderDialog { Title = "选择 Windows Git 仓库" };
        if (Directory.Exists(windowsRepo.Text))
            dialog.InitialDirectory = windowsRepo.Text;
        if (dialog.ShowDialog(this) == true)
            windowsRepo.Text = dialog.FolderName;
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

    private void ApplyJson(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ConfirmExternalConfiguration(Config.Text, onlyChanged: true)) return;
            LoadConfig(Config.Text);
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

    private async void DetectRemotes(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        if (string.IsNullOrWhiteSpace(windowsRepo.Text))
        {
            RemoteDetectionStatus.ShowFeedback("请先在「Git 本地仓库」填写仓库路径。", InfoBarSeverity.Warning);
            return;
        }

        RemoteDetectionStatus.ShowFeedback("正在读取本地仓库的远端名称…");
        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        try
        {
            var config = ProjectConfig.Parse(SerializeConfig());
            string[] output = await SyncEngine.ListRemotesAsync(config);

            string text = remote.Text;
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(remoteUrl.Text))
                text = output.Contains("origin") ? "origin" : output.Length == 1 ? output[0] : text;
            loading = true;
            RestoreChoices(remote, text, output);
            loading = false;
            ClearRemoteBranches();
            FormChanged();
            string? cacheError = SaveChoices(RemoteChoiceKey(), output);
            RemoteDetectionStatus.ShowFeedback(cacheError ?? (output.Length == 0 ? "本地仓库没有配置远端名称，可以直接填写远端仓库地址。" :
                $"已读取 {output.Length} 个远端名称。可从下拉列表选择。"), cacheError is not null || output.Length == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (SyncException error) when (error.Code == SyncErrorCode.WindowsGitMissing)
        {
            ShowWindowsGitMissing(RemoteDetectionStatus, error.Message);
        }
        catch (Exception error) { RemoteDetectionStatus.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
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
        running = true;
        SettingsTabs.IsEnabled = Actions.IsEnabled = false;
        ClearRemoteBranches();
        RemoteTestStatus.ShowFeedback("正在读取远端分支。如弹出浏览器，请完成 GitHub 登录。");
        try
        {
            var config = ProjectConfig.Parse(SerializeConfig());
            string[] branches = await SyncEngine.TestConnectionAsync(config);
            string text = branch.Text;
            loading = true;
            RestoreChoices(branch, text, branches);
            loading = false;
            string? cacheError = SaveChoices(BranchChoiceKey(), branches);
            RemoteTestStatus.ShowFeedback(cacheError ?? (branches.Length == 0 ? "连接成功，远端暂无分支。" :
                branches.Contains(text) ? $"连接成功，已读取 {branches.Length} 个分支。" :
                $"连接成功，已读取 {branches.Length} 个分支。请从下拉列表选择目标分支。"), cacheError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
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
                ProjectSync.App.App.AppendDiagnostic(failed.Message);
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
            ForceStopAllPreviews();
    }
}

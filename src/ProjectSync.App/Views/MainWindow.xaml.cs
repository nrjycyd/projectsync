using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private bool running;
    private string? displayedBranchKey;


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
            return;
        }

        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            theme == "Dark" ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.Mica);
    }

    public MainWindow()
    {
        InitializeComponent();
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
        var parsed = JsonNode.Parse(configuration) as JsonObject
            ?? throw new JsonException("配置必须是 JSON 对象。");
        if (new[] { "git", "source", "build", "sync" }.Any(key => parsed[key] is JsonObject))
            throw new JsonException("不支持嵌套格式，请使用扁平项目配置。");

        var commands = new[] { "buildCommand", "previewCommand", "reinstallCommand",
            "themeUpgradeCommand", "gitSshCommand" }
            .Select(key => (Key: key, Value: parsed[key]?.GetValue<string>()))
            .Where(command => !string.IsNullOrWhiteSpace(command.Value))
            .Where(command => !onlyChanged || parsed[command.Key]?.ToJsonString() != current[command.Key]?.ToJsonString())
            .Select(command => $"{command.Key}\n{command.Value}");
        string details = string.Join("\n\n", commands);
        return details.Length == 0 || ConfirmProjectAction("检查配置中的命令",
            "项目操作可能以你的权限运行以下命令。请逐条核对，并仅在信任来源时继续。\n\n" + details,
            "接受配置", "取消", canCancel: false) == MessageBoxResult.Yes;
    }

    private void LoadConfig(string text)
    {
        var parsed = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("配置必须是 JSON 对象。");
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
                if (suffix != ":Zone.Identifier" && System.Text.RegularExpressions.Regex.IsMatch(suffix, @"[\\/:*?""<>|\[\]\x00-\x1f]"))
                    throw new ArgumentException($"排除后缀应填写文件名末尾的文字：{suffix}");
            var updates = boxes.Select(box => (Box: box, Text: string.Join(Environment.NewLine,
                Lines(box.Text).Select(path => NormalizeSyncPath(path, box == include ? "同步路径" : "排除路径"))))).ToArray();
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
        ProjectSync.Core.SyncPath.Normalize(input, label, windowsRepo.Text, wslRepo.Text, distro.Text.Trim());

    private static string UnquotePath(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

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

        if (!TryNormalizeSyncPaths(include, exclude)) return;

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
            var output = new List<string>();
            var errors = new StringBuilder();
            await RunProjectAsync(SerializeConfig(), false, false, null, line =>
            {
                const string prefix = "LOCAL_REMOTE=";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    output.Add(line[prefix.Length..]);
                else
                    errors.AppendLine(line);
            }, listRemotes: true, failureMessage: () => errors.ToString().Trim());

            string text = remote.Text;
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(remoteUrl.Text))
                text = output.Contains("origin") ? "origin" : output.Count == 1 ? output[0] : text;
            loading = true;
            RestoreChoices(remote, text, output.ToArray());
            loading = false;
            ClearRemoteBranches();
            FormChanged();
            string? cacheError = SaveChoices(RemoteChoiceKey(), output);
            RemoteDetectionStatus.ShowFeedback(cacheError ?? (output.Count == 0 ? "本地仓库没有配置远端名称，可以直接填写远端仓库地址。" :
                $"已读取 {output.Count} 个远端名称。可从下拉列表选择。"), cacheError is not null || output.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
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
        var branches = new List<string>();
        var output = new StringBuilder();
        try
        {
            await RunProjectAsync(SerializeConfig(), false, false, null,
                line =>
                {
                    const string prefix = "REMOTE_BRANCH=";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        branches.Add(line[prefix.Length..]);
                    else
                        output.AppendLine(line);
                }, testConnection: true);
            string text = branch.Text;
            loading = true;
            RestoreChoices(branch, text, branches.ToArray());
            loading = false;
            string? cacheError = SaveChoices(BranchChoiceKey(), branches);
            RemoteTestStatus.ShowFeedback(cacheError ?? (branches.Count == 0 ? "连接成功，远端暂无分支。" :
                branches.Contains(text) ? $"连接成功，已读取 {branches.Count} 个分支。" :
                $"连接成功，已读取 {branches.Count} 个分支。请从下拉列表选择目标分支。"), cacheError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception error)
        {
            RemoteTestStatus.ShowFeedback(output.Length == 0 ? error.Message : output.ToString().Trim(), InfoBarSeverity.Error);
        }
        finally
        {
            running = false;
            SettingsTabs.IsEnabled = Actions.IsEnabled = true;
        }
    }

    private static async Task<(string? Plan, string? Synced, bool Pushed)> RunProjectAsync(
        string configuration, bool preview, bool publish, string? expected, Action<string> writeLog, bool testConnection = false, bool listRemotes = false, Func<string>? failureMessage = null, Action<string>? savePending = null, string? commitSummary = null, string? initializeRepository = null, bool allowCleanup = false, CancellationToken cancellation = default)
    {
        string? temporary = null;
        string? summaryFile = null;
        string? buildLease = null;
        string? resultFile = null;
        try
        {
            temporary = Path.GetTempFileName();
            await File.WriteAllTextAsync(temporary, configuration);
            resultFile = Path.GetTempFileName();
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "-NoProfile", "-File",
                Path.Combine(AppContext.BaseDirectory, "scripts", "BuildSync.ps1"), "-Config", temporary,
                "-ResultFile", resultFile })
                start.ArgumentList.Add(argument);
            if (listRemotes)
                start.ArgumentList.Add("-ListRemotes");
            else if (testConnection)
                start.ArgumentList.Add("-TestConnection");
            else if (initializeRepository is not null)
            {
                start.ArgumentList.Add("-InitializeRepository");
                start.ArgumentList.Add(initializeRepository);
            }
            else
            {
                if (publish)
                {
                    start.ArgumentList.Add("-Publish");
                    if (commitSummary is not null)
                    {
                        summaryFile = Path.GetTempFileName();
                        await File.WriteAllTextAsync(summaryFile, commitSummary);
                        start.ArgumentList.Add("-CommitSummaryFile");
                        start.ArgumentList.Add(summaryFile);
                    }
                }
                start.ArgumentList.Add(preview ? "-Preview" : "-ExpectedPlan");
                if (!preview)
                    start.ArgumentList.Add(expected!);
                if (allowCleanup)
                    start.ArgumentList.Add("-AllowCleanup");
            }

            if (cancellation.CanBeCanceled)
            {
                buildLease = Path.GetTempFileName();
                start.ArgumentList.Add("-BuildLease");
                start.ArgumentList.Add(buildLease);
            }

            cancellation.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动同步进程。");
            using var logCancellation = new CancellationTokenSource();
            Task execution = Task.WhenAll(ReadLog(process.StandardOutput, line =>
            {
                if (publish && System.Text.RegularExpressions.Regex.IsMatch(line, @"\APENDING=[0-9a-f]{40}(?::[0-9a-f]{40}){2}\z"))
                    savePending?.Invoke(line.Substring(8));
                else
                    writeLog(line);
            }, logCancellation.Token), ReadLog(process.StandardError, writeLog, logCancellation.Token),
                process.WaitForExitAsync());
            try
            {
                await execution.WaitAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                File.Delete(buildLease!);
                if (await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(4))) != execution)
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    logCancellation.Cancel();
                }
                try { await execution; }
                catch (OperationCanceledException) when (logCancellation.IsCancellationRequested) { }
                throw;
            }
            string payload = await File.ReadAllTextAsync(resultFile);
            var response = payload.Length == 0 ? null : JsonNode.Parse(payload);
            string? kind = response?["kind"]?.GetValue<string>();
            string? value = response?["value"]?.GetValue<string>();
            string? result = kind == "PLAN" ? value : null;
            string? verified = kind == "SYNCED" ? value : null;
            string? errorCode = kind == "PROJECTSYNC_ERROR" ? value : null;
            if (process.ExitCode != 0 || (preview && result is null) ||
                (publish && (kind != "PUBLISHED" || value is not ("pushed" or "no_changes"))) ||
                (!listRemotes && !testConnection && !preview && !publish && initializeRepository is null && verified is null))
                throw new InvalidOperationException(failureMessage?.Invoke() is { Length: > 0 } message ? message : errorCode switch
                {
                    "REPOSITORY_BUSY" => "此 Windows 仓库正在执行另一项 ProjectSync 操作。",
                    "CONFIG" => "项目配置缺少必填项。请打开项目设置，按日志列出的字段补齐。",
                    "WINDOWS_REPO" => "Windows Git 仓库不可用。请在「Git 本地仓库」选择正确目录，或点击「初始化 Git…」。",
                    "WSL_DIRECTORY" => "无法进入 WSL 源目录。请在项目设置中重新检测环境。",
                    "SOURCE_MISSING" => "WSL 缺少配置中指定的同步文件。请核对日志里的路径。",
                    "WSL_INDEX" => "WSL 暂存区与当前文件不一致，或 Git 索引不可读。请查看日志并核对暂存内容。",
                    "BUILD" => "构建命令执行失败。请查看上方的构建输出。",
                    "PREVIEW_EXPIRED" => "变更清单已过期，请重新构建。",
                    "PUSH" => "本地提交已生成。请查看推送错误，然后重试推送。",
                    _ => "操作失败，详情见日志。"
                });
            return (result, verified, value == "pushed");
        }
        finally
        {
            foreach (string? file in new[] { temporary, summaryFile, buildLease, resultFile })
            {
                if (file is null) continue;
                try { File.Delete(file); }
                catch (IOException error) { writeLog(error.Message); }
                catch (UnauthorizedAccessException error) { writeLog(error.Message); }
            }
        }
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
        }
        if (!e.Cancel)
            foreach (var server in previewServers.Values) server.StandardInput.Close();
    }

    private static async Task ReadLog(StreamReader reader, Action<string> handle, CancellationToken cancellation = default)
    {
        while (await reader.ReadLineAsync(cancellation) is { } line)
            handle(line);
    }

}

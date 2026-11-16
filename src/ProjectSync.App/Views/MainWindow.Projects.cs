using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private readonly string projectFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProjectSync.App.App.DataDirectoryName, "projects.json");
    private readonly List<ProjectEntry> savedProjects = new();
    private ProjectEntry? activeProject;
    private int settingsOpenSequence;
    private JsonObject? originalConfig;

    private bool HasConfigChanges => !JsonNode.DeepEquals(current, originalConfig);

    private void UpdateSaveProjectAction()
    {
        bool canSave = !jsonDraft && HasConfigChanges;
        SaveProjectButton.Appearance = canSave ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SaveProjectButton.IsEnabled = canSave;
        ApplyJsonButton.Appearance = jsonDraft ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private sealed record ProjectEntry(Guid Id, string Name, string Repository, string Branch, string Configuration)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Checked { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string RemoteRepository
        {
            get
            {
                try
                {
                    using var config = JsonDocument.Parse(Configuration);
                    if (config.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (string field in new[] { "remoteUrl", "remote" })
                        {
                            if (config.RootElement.TryGetProperty(field, out var value) &&
                                value.ValueKind == JsonValueKind.String &&
                                value.GetString()?.Trim() is { Length: > 0 } address)
                                return address;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall back to the saved remote summary for malformed configuration.
                }
                return Branch;
            }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public string Progress { get; set; } = "待构建";
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
            try
            {
                string path = PendingPushPath(project);
                if (File.Exists(path))
                {
                    string pending = File.ReadAllText(path).Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(pending, @"\A[0-9a-f]{40}(?::[0-9a-f]{40}){2}\z"))
                        projectRuns[project.Id] = new RunState(null, pending, "已提交，待推送");
                }
                if (!projectRuns.ContainsKey(project.Id) && File.Exists(PreviewStatePath(project)))
                {
                    var preview = JsonSerializer.Deserialize<SavedPreview>(File.ReadAllText(PreviewStatePath(project)));
                    if (preview is not null && preview.ConfigurationHash == ConfigurationHash(project) &&
                        System.Text.RegularExpressions.Regex.IsMatch(preview.Plan, @"\A[0-9a-f]{40}:[0-9A-F]{64}\z"))
                    {
                        projectRuns[project.Id] = new RunState(preview.Plan, null, "可同步", preview.Changes);
                    }
                }
            }
            catch (Exception error) { HomeStatus.ShowFeedback($"无法读取「{project.Name}」的待推送记录：{error.Message}", InfoBarSeverity.Error); }
        }
        HomeChanges.Items.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(BatchChange.Group)));
        ShowSavedChanges();
        RefreshProjects();
    }

    private static string ConfigurationHash(ProjectEntry project) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(project.Configuration)));

    private string PendingPushPath(ProjectEntry project) =>
        Path.Combine(Path.GetDirectoryName(projectFile)!, $"pending-{project.Id:N}-{ConfigurationHash(project)}.txt");

    private string PreviewStatePath(ProjectEntry project) =>
        Path.Combine(Path.GetDirectoryName(projectFile)!, $"preview-{project.Id:N}.json");

    private void SavePreview(ProjectEntry project, string plan, BatchChange[] changes)
    {
        string path = PreviewStatePath(project);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new SavedPreview(ConfigurationHash(project), plan, changes)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void DeletePreview(ProjectEntry project)
    {
        try { File.Delete(PreviewStatePath(project)); }
        catch (IOException error) { WriteHomeLog($"无法删除预览记录：{error.Message}"); }
        catch (UnauthorizedAccessException error) { WriteHomeLog($"无法删除预览记录：{error.Message}"); }
    }

    private void ShowSavedChanges()
    {
        HomeChanges.Items.Clear();
        foreach (var project in savedProjects)
            if (projectRuns.TryGetValue(project.Id, out var state) && state.Changes is { } changes)
                foreach (var change in changes)
                    HomeChanges.Items.Add(change);
        if (HomeChanges.Items.Count > 0)
            HomeChangesSummary.Text = $"预览列出 {HomeChanges.Items.Count} 项变更";
        else
            HomeChangesSummary.Text = projectRuns.Values.Any(state => state.Plan is not null)
                ? "预览未列出文件变更。" : "构建并预览后显示待同步变更。";
    }

    private ProjectEntry CreateEntry() => new(activeProject?.Id ?? Guid.NewGuid(), ProjectName.Text.Trim(),
        windowsRepo.Text.Trim(), $"{remote.Text.Trim()}/{branch.Text.Trim()}", SerializeConfig());

    private void PersistProjects()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        string temporary = projectFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(savedProjects, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, projectFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

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

    private async Task ShowProjectAsync(string configuration, ProjectEntry? entry, bool isBlankProject = false)
    {
        int openSequence = ++settingsOpenSequence;
        restoreDetectedOptions = !isBlankProject;
        LoadConfig(configuration);
        LocalGitStatus.IsOpen = false;
        Status.IsOpen = false;
        activeProject = entry;
        originalConfig = entry is not null || isBlankProject ? (JsonObject)current.DeepClone() : null;
        UpdateSaveProjectAction();
        var selectedTab = RemoteSettingsTab;
        if (!string.IsNullOrWhiteSpace(branch.Text))
        {
            if (!string.IsNullOrWhiteSpace(remoteUrl.Text))
            {
                selectedTab = LocalSettingsTab;
            }
            else
            {
                string localPath = windowsRepo.Text.Trim();
                string remoteName = remote.Text.Trim();
                if (remoteName.Length > 0 && Directory.Exists(localPath))
                {
                    try
                    {
                        if ((Directory.Exists(Path.Combine(localPath, ".git")) || File.Exists(Path.Combine(localPath, ".git"))) &&
                            (await ReadShortcutRemoteAsync(localPath, remoteName)).Length > 0)
                            selectedTab = LocalSettingsTab;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Git is unavailable. Keep the remote settings visible.
                    }
                }
            }
        }
        if (openSequence != settingsOpenSequence)
            return;

        SettingsTabs.SelectedItem = selectedTab;
        HomePage.Visibility = Visibility.Collapsed;
        ProjectPage.Visibility = Visibility.Visible;
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
            SettingsTabs.SelectedItem = LocalSettingsTab;
            ProjectName.Focus();
            Status.ShowFeedback("请填写项目名称。", InfoBarSeverity.Warning);
            return false;
        }

        if (!TryNormalizeSyncPaths(include, exclude)) return false;
        if (!HasConfigChanges) return true;

        ProjectEntry entry = CreateEntry();
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
        if (previous is not null && previous.Configuration != entry.Configuration)
        {
            projectRuns.Remove(entry.Id);
            DeletePreview(entry);
            ShowSavedChanges();
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
        DeletePreview(entry);
        ShowSavedChanges();
        HomeStatus.ShowFeedback($"已移除「{entry.Name}」。", InfoBarSeverity.Success);
    }

    private sealed record BatchChange(string Project, string Action, string Path)
    {
        public string Group => Action == "目标清理" ? "目标清理（永久删除）" : "同步变更";
    }
    private enum Operation { Preview, Apply, Publish, RetryPush }
    private sealed record RunState(string? Plan, string? Synced, string Progress, BatchChange[]? Changes = null);
    private sealed record SavedPreview(string ConfigurationHash, string Plan, BatchChange[] Changes);
    private readonly Dictionary<Guid, RunState> projectRuns = new();

    private void ProjectSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateHomeActions();

    private void ProjectCheckChanged(object sender, RoutedEventArgs e)
    {
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
            var config = JsonNode.Parse(project.Configuration);
            string Field(string key) => config?[key]?.GetValue<string>()?.Trim() ?? "";
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
                        var terminal = new System.Diagnostics.ProcessStartInfo("wsl.exe") { UseShellExecute = true };
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
        catch (Exception error)
        {
            HomeStatus.ShowFeedback($"无法打开「{project.Name}」的快捷访问目标：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private static async Task<string> ReadShortcutRemoteAsync(string repository, string remoteName)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (string arg in new[] { "remote", "get-url", "--", remoteName })
            start.ArgumentList.Add(arg);
        using var git = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 Git。");
        string address = (await git.StandardOutput.ReadToEndAsync()).Trim();
        await git.WaitForExitAsync();
        return git.ExitCode == 0 ? address : "";
    }

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
            if (element is System.Windows.Controls.Button)
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
            Operation.Preview => !pending,
            Operation.Apply => !pending && state?.Plan is not null,
            Operation.Publish => !pending && state?.Synced is not null,
            Operation.RetryPush => pending && state?.Synced is not null,
            _ => false
        };
    }

    private void UpdateHomeActions()
    {
        if (Projects is null || PreviewBatchButton is null)
            return;

        var selected = savedProjects.Where(project => project.Checked).ToArray();
        int canPreview = selected.Count(project => CanRunBatchOperation(project, Operation.Preview));
        int canApply = selected.Count(project => CanRunBatchOperation(project, Operation.Apply));
        int canPublish = selected.Count(project => CanRunBatchOperation(project, Operation.Publish));
        int canRetry = selected.Count(project => CanRunBatchOperation(project, Operation.RetryPush));
        UpdateBatchButton(PreviewBatchButton, "构建并预览", canPreview, selected.Length, "请勾选可预览的项目");
        UpdateBatchButton(ApplyBatchButton, "应用同步", canApply, selected.Length, "请勾选已预览的项目");
        UpdateBatchButton(PublishBatchButton, "提交并推送", canPublish, selected.Length, "请勾选已同步的项目");
        UpdateBatchButton(RetryPushButton, "重试推送", canRetry, selected.Length, "请勾选待推送的项目");
        RetryPushButton.Visibility = canRetry > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Prefer finishing work that has already reached a later stage.
        var batchButtons = new[] { RetryPushButton, PublishBatchButton, ApplyBatchButton, PreviewBatchButton };
        var primaryAction = Array.Find(batchButtons, button => button.IsEnabled);
        foreach (var button in batchButtons)
            button.Appearance = button == primaryAction ? ControlAppearance.Primary : ControlAppearance.Secondary;
        NewProjectButton.Appearance = primaryAction is null ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    private void UpdateBatchButton(Wpf.Ui.Controls.Button button, string label, int eligible, int selected, string selectionHint)
    {
        bool partial = eligible > 0 && eligible < selected;
        button.Content = partial ? $"{label} · {eligible}" : label;
        button.IsEnabled = !running && eligible > 0;
        string hint = partial ? $"处理 {eligible}/{selected} 项" :
            eligible > 0 ? $"处理 {eligible} 项" : selectionHint;
        button.ToolTip = hint;
        AutomationProperties.SetHelpText(button, hint);
    }

    private async void PreviewSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Preview);
    private async void ApplySelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Apply);
    private async void PublishSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Publish);

    private async void RetryPushSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.RetryPush);

    private static async Task<string[]> ReadWorkingTreeAsync(ProjectEntry project)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (string argument in new[] { "--no-optional-locks", "-C", project.Repository,
            "-c", "core.quotepath=false", "status", "--short", "--untracked-files=all", "--no-renames" })
            start.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new IOException("无法启动 Git。");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, errors, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new IOException(errors.Result.Trim());
        return output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private string? RequestCommitSummary(ProjectEntry project, string[] changes)
    {
        var body = new System.Windows.Controls.DockPanel { Margin = new Thickness(20) };
        var dialog = FluentDialog.Create(this, "提交并推送", body, 480);
        var branch = JsonNode.Parse(project.Configuration)?["branch"]?.GetValue<string>();
        string target = $"{project.Name} → {branch}\n{project.Repository}\n\n当前工作区有 {changes.Length} 项变更。";
        var scope = new System.Windows.Controls.TextBlock
        {
            Text = "将暂存并提交整个 Windows 工作区的新增、修改和删除。Git 已忽略的未跟踪文件会跳过。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.DockPanel.SetDock(scope, System.Windows.Controls.Dock.Top);
        body.Children.Add(scope);
        var targetsView = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 245, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = target + "\n" + string.Join("\n", changes),
                TextWrapping = TextWrapping.Wrap
            },
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.DockPanel.SetDock(targetsView, System.Windows.Controls.Dock.Top);
        body.Children.Add(targetsView);

        var actions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0)
        };
        System.Windows.Controls.DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom);
        body.Children.Add(actions);
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Wpf.Ui.Controls.Button
        {
            Content = "提交并推送", IsDefault = true, IsEnabled = false,
            Appearance = ControlAppearance.Primary
        };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);

        var row = new System.Windows.Controls.DockPanel();
        var input = new Wpf.Ui.Controls.TextBox();
        var label = new System.Windows.Controls.Label
        {
            Content = "提交摘要", Target = input,
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 10, 0)
        };
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

    private void StopBatch(object sender, RoutedEventArgs e)
    {
        batchCancellation?.Cancel();
        StopBatchButton.IsEnabled = false;
        BatchProgressText.Text = "正在停止批量操作…";
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
                && JsonNode.Parse(project.Configuration)?["cleanUntracked"]?.GetValue<bool>() == true).ToArray()
            : Array.Empty<ProjectEntry>();
        if (cleanupProjects.Length > 0 && ConfirmProjectAction("确认永久清理",
            "以下项目将永久删除预览列出的目标清理项。清理覆盖整个 Windows 工作区，文件不会进入回收站。\n\n" +
            string.Join("\n", cleanupProjects.Select(project => project.Name + " · " + project.Repository)),
            "确认清理并同步", "取消", canCancel: false, destructive: true) != MessageBoxResult.Yes)
            return;

        using var cancellation = new CancellationTokenSource();
        batchCancellation = cancellation;
        StopBatchButton.IsEnabled = true;
        BatchProgressText.Text = $"正在准备处理 {selected.Length} 个项目…";
        BatchProgressPanel.Visibility = Visibility.Visible;
        HomeStatus.IsOpen = false;
        running = true;
        HomePage.Tag = true;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        HomeLog.Clear();
        HomeResults.SelectedItem = HomeLogTab;
        if (operation == Operation.Preview)
        {
            HomeChanges.Items.Clear();
            HomeChangesSummary.Text = "正在生成变更清单…";
        }
        int completed = 0;
        int failed = 0;
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
                        string[] changes = await ReadWorkingTreeAsync(project);
                        if (cancellation.IsCancellationRequested) return;
                        if (RequestCommitSummary(project, changes) is not { } summary)
                        {
                            cancellation.Cancel();
                            return;
                        }
                        summaries.Add(project.Id, summary);
                    }
                    catch (Exception error)
                    {
                        failed++;
                        WriteHomeLog($"{project.Name}：无法读取待提交文件：{error.Message}");
                        return;
                    }
                }
            }

            int position = 0;
            foreach (var project in selected)
            {
                if (cancellation.IsCancellationRequested) break;
                BatchProgressText.Text = $"正在处理「{project.Name}」 · {++position}/{selected.Length}";
                WriteHomeLog($"[{project.Name}] {operation switch { Operation.Preview => "构建并预览", Operation.Apply => "应用同步", Operation.RetryPush => "重试推送", _ => "提交并推送" }}");
                projectRuns.TryGetValue(project.Id, out var state);
                string? expected = publishing ? state?.Synced : state?.Plan;
                string? pending = publishing && expected?.Count(ch => ch == ':') == 2 ? expected : null;
                projectRuns.Remove(project.Id);
                DeletePreview(project);
                var changes = new List<BatchChange>();
                try
                {
                    var result = await RunProjectAsync(project.Configuration, operation == Operation.Preview,
                        publishing, expected, line =>
                        {
                            WriteHomeLog(line);
                            if (operation == Operation.Preview && ParseHomeChange(project.Name, line) is { } change)
                                changes.Add(change);
                        }, commitSummary: summaries.GetValueOrDefault(project.Id), allowCleanup: cleanupProjects.Contains(project),
                        cancellation: operation == Operation.Preview ? cancellation.Token : default,
                        savePending: value =>
                        {
                            pending = value;
                            try
                            {
                                string path = PendingPushPath(project);
                                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                                try
                                {
                                    File.WriteAllText(temporary, value);
                                    File.Move(temporary, path, overwrite: true);
                                }
                                finally { File.Delete(temporary); }
                            }
                            catch (Exception error) { WriteHomeLog($"无法保存待推送记录：{error.Message}"); }
                        });
                    if (publishing)
                    {
                        try { File.Delete(PendingPushPath(project)); }
                        catch (Exception error) { WriteHomeLog($"无法清理待推送记录：{error.Message}"); }
                    }
                    var previewChanges = operation == Operation.Preview ? changes.ToArray() : null;
                    projectRuns[project.Id] = new RunState(result.Plan, result.Synced, operation switch
                    { Operation.Preview => "可同步", Operation.Apply => "可推送", _ => result.Pushed ? "已推送" : "无需推送" }, previewChanges);
                    if (result.Plan is not null && previewChanges is not null)
                    {
                        try { SavePreview(project, result.Plan, previewChanges); }
                        catch (IOException error) { WriteHomeLog($"无法保存预览记录：{error.Message}"); }
                        catch (UnauthorizedAccessException error) { WriteHomeLog($"无法保存预览记录：{error.Message}"); }
                    }
                    completed++;
                    WriteHomeLog("操作完成。");
                    if (operation == Operation.Preview && !cancellation.IsCancellationRequested)
                        await TryStartSitePreviewAsync(project);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    WriteHomeLog("构建和预览已停止。");
                    break;
                }
                catch (Exception error)
                {
                    failed++;
                    projectRuns[project.Id] = new RunState(null, pending, pending is null ? "操作失败" : "已提交，待推送");
                    WriteHomeLog($"{project.Name}：操作失败。{error.Message}");
                }
            }
        }
        finally
        {
            running = false;
            batchCancellation = null;
            BatchProgressPanel.Visibility = Visibility.Collapsed;
            HomePage.Tag = false;
            HomeActions.IsEnabled = true;
            RefreshProjects();
            ShowSavedChanges();
            if (operation == Operation.Preview)
            {
                if (completed == selected.Length) HomeResults.SelectedItem = HomeChangesTab;
            }
            string result = (cancellation.IsCancellationRequested ? "已停止 · " : "") +
                $"完成 {completed} 个 · 失败 {failed} 个";
            if (failed > 0)
                HomeStatus.ShowFeedback(result + "。详情见运行日志。", InfoBarSeverity.Warning);
            else
                HomeStatus.ShowFeedback(result, cancellation.IsCancellationRequested ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
            if (closeAfterBatch)
            {
                closeAfterBatch = false;
                Close();
            }
        }
    }

    private static BatchChange? ParseHomeChange(string project, string line)
    {
        if (line.StartsWith("CLEANUP=", StringComparison.Ordinal))
            return new BatchChange(project, "目标清理", line.Substring(8));
        var parts = line.Split('\t', 2);
        if (parts.Length == 2)
        {
            string? action = parts[0] switch
            {
                "A" => "新增", "M" => "修改", "D" => "源端删除", "T" => "类型变更", _ => null
            };
            if (action is not null) return new BatchChange(project, action, parts[1]);
        }
        return null;
    }

    private void WriteHomeLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        HomeLog.AppendText(line + Environment.NewLine);
        ProjectSync.App.App.AppendDiagnostic(line);
        HomeLog.ScrollToEnd();
    }

    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断（运行日志可能包含路径、命令或其他敏感信息）",
            Filter = "诊断压缩包 (*.zip)|*.zip",
            FileName = $"ProjectSync-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var output = File.Open(dialog.FileName, FileMode.Create);
            using var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create);
            using (var summary = new StreamWriter(archive.CreateEntry("summary.txt").Open()))
            {
                summary.WriteLine($"ProjectSync {typeof(MainWindow).Assembly.GetName().Version}");
                summary.WriteLine($"OS: {Environment.OSVersion}");
                summary.WriteLine($"Saved projects: {savedProjects.Count}");
                summary.WriteLine($"Projects with previews: {projectRuns.Values.Count(state => state.Plan is not null)}");
                summary.WriteLine($"Projects pending push: {projectRuns.Values.Count(state => state.Progress == "已提交，待推送")}");
            }
            if (Directory.Exists(ProjectSync.App.App.DiagnosticsDirectory))
            {
                foreach (string log in Directory.EnumerateFiles(ProjectSync.App.App.DiagnosticsDirectory, "projectsync-*.log")
                    .OrderByDescending(path => path, StringComparer.Ordinal).Take(7))
                {
                    using var input = File.OpenRead(log);
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

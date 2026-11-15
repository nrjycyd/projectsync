using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private readonly string projectFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectSync", "projects.json");
    private readonly List<ProjectEntry> savedProjects = new();
    private ProjectEntry? activeProject;
    private int settingsOpenSequence;
    private string? originalConfig;

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
            else
            {
                LoadConfig(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "edusprout.build-sync.json")));
                ProjectName.Text = "EduSprout";
                savedProjects.Add(CreateEntry());
                PersistProjects();
            }
        }
        catch (Exception error)
        {
            HomeStatus.Text = $"无法加载项目列表：{error.Message}";
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
            }
            catch (Exception error) { HomeStatus.Text = $"无法读取「{project.Name}」的待推送记录：{error.Message}"; }
        }
        RefreshProjects();
    }

    private string PendingPushPath(ProjectEntry project)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(project.Configuration)));
        return Path.Combine(Path.GetDirectoryName(projectFile)!, $"pending-{project.Id:N}-{hash}.txt");
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
        UpdateHomeActions();
    }

    private async Task ShowProjectAsync(string configuration, ProjectEntry? entry)
    {
        int openSequence = ++settingsOpenSequence;
        LoadConfig(configuration);
        activeProject = entry;
        originalConfig = entry is null ? null : SerializeConfig();
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
        await ShowProjectAsync("""{"name":"","remote":"origin","branch":"main","cleanUntracked":false}""", null);
    }

    private async void ImportProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入项目配置", Filter = "项目配置 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            await ShowProjectAsync(File.ReadAllText(dialog.FileName), null);
            if (string.IsNullOrWhiteSpace(ProjectName.Text))
                ProjectName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            Status.Text = "已导入配置。点击「保存项目」加入列表。";
        }
        catch (Exception error)
        {
            HomeStatus.Text = $"无法导入项目：{error.Message}";
        }
    }

    private async void OpenProject(object sender, RoutedEventArgs e)
    {
        if (Projects.SelectedItem is not ProjectEntry entry)
            return;
        try { await ShowProjectAsync(entry.Configuration, entry); }
        catch (Exception error) { HomeStatus.Text = $"无法打开项目：{error.Message}"; }
    }

    private void SaveProject(object sender, RoutedEventArgs e) => TrySaveProject();

    private bool TrySaveProject()
    {
        if (jsonDraft || string.IsNullOrWhiteSpace(ProjectName.Text))
        {
            Status.Text = jsonDraft ? "请先应用待处理的 JSON 改动。" : "请填写项目名称。";
            return false;
        }

        if (!TryNormalizeSyncPaths(include, exclude)) return false;

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
            Status.Text = $"保存失败：{error.Message}";
            return false;
        }

        activeProject = entry;
        if (previous?.Configuration != entry.Configuration) projectRuns.Remove(entry.Id);
        originalConfig = entry.Configuration;
        Status.Text = $"已保存项目「{entry.Name}」。";
        return true;
    }

    private bool CanLeaveProject()
    {
        if (ProjectPage.Visibility != Visibility.Visible || (!jsonDraft && SerializeConfig() == originalConfig))
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
        HomeStatus.Text = "";
        RefreshProjects();
        Projects.SelectedItem = activeProject;
    }

    private void RemoveProject(object sender, RoutedEventArgs e)
    {
        if (Projects.SelectedItem is not ProjectEntry entry ||
            ConfirmProjectAction("移除项目", $"从列表移除「{entry.Name}」？仓库文件会保留。", "移除", "取消", canCancel: false) != MessageBoxResult.Yes)
            return;

        int index = savedProjects.IndexOf(entry);
        savedProjects.RemoveAt(index);
        try { PersistProjects(); }
        catch (Exception error)
        {
            savedProjects.Insert(index, entry);
            HomeStatus.Text = $"移除失败：{error.Message}";
            return;
        }

        RefreshProjects();
        projectRuns.Remove(entry.Id);
        HomeStatus.Text = $"已移除「{entry.Name}」。";
    }

    private sealed record BatchChange(string Project, string Action, string Path);
    private enum Operation { Preview, Apply, Publish }
    private sealed record RunState(string? Plan, string? Synced, string Progress);
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
            HomeStatus.Text = $"无法打开「{project.Name}」的快捷访问目标：{error.Message}";
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

    private void OpenHomeMenu(object sender, RoutedEventArgs e)
    {
        if (running || sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
            return;

        RemoveProjectItem.IsEnabled = Projects.SelectedItem is ProjectEntry;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HomeToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeActions is null || BatchActions is null) return;
        bool wrap = e.NewSize.Width < BatchActions.DesiredSize.Width + HomeActions.DesiredSize.Width + 16;
        System.Windows.Controls.Grid.SetRow(HomeActions, wrap ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(HomeActions, wrap ? 0 : 2);
        System.Windows.Controls.Grid.SetColumnSpan(HomeActions, wrap ? 3 : 1);
        HomeActions.Margin = new Thickness(0, wrap ? 6 : 0, 0, 0);
    }

    private void UpdateHomeActions()
    {
        if (Projects is null || PreviewBatchButton is null || SelectionSummary is null)
            return;

        var selected = savedProjects.Where(project => project.Checked).ToArray();
        int canApply = selected.Count(project => projectRuns.TryGetValue(project.Id, out var state) && state.Plan is not null);
        int canPublish = selected.Count(project => projectRuns.TryGetValue(project.Id, out var state) && state.Synced is not null);
        PreviewBatchButton.IsEnabled = !running && selected.Length > 0;
        ApplyBatchButton.IsEnabled = !running && canApply > 0;
        PublishBatchButton.IsEnabled = !running && canPublish > 0;
        PublishBatchButton.Content = canPublish > 0 && selected.Any(project =>
            projectRuns.TryGetValue(project.Id, out var state) && state.Progress == "已提交，待推送")
            ? "提交／重试推送" : "提交并推送";

        SelectionSummary.Text = selected.Length == 0 ? "勾选项目后开始" :
            $"已勾选 {selected.Length} 个";
        SelectionSummary.ToolTip = selected.Length == 0 ? null : $"可同步 {canApply} 个 · 可推送 {canPublish} 个";
    }

    private async void PreviewSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Preview);
    private async void ApplySelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Apply);
    private async void PublishSelected(object sender, RoutedEventArgs e) => await RunSelectedAsync(Operation.Publish);

    private string? RequestCommitSummary(ProjectEntry[] projects)
    {
        var body = new System.Windows.Controls.DockPanel { Margin = new Thickness(20) };
        var dialog = new Window
        {
            Title = "提交并推送", Owner = this, Icon = Icon, FontFamily = FontFamily, FontSize = 14,
            Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.White, Content = body
        };
        string[] targets = projects.Select(project =>
        {
            var branch = JsonNode.Parse(project.Configuration)?["branch"]?.GetValue<string>();
            return $"{project.Name} → {branch}\n{project.Repository}";
        }).ToArray();
        var scope = new System.Windows.Controls.TextBlock
        {
            Text = "将暂存并提交整个 Windows 工作区的新增、修改和删除。Git 已忽略的未跟踪文件会跳过。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.DockPanel.SetDock(scope, System.Windows.Controls.Dock.Top);
        body.Children.Add(scope);
        var targetsView = new System.Windows.Controls.ScrollViewer
        {
            MaxHeight = 110, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Content = new System.Windows.Controls.TextBlock { Text = string.Join(Environment.NewLine, targets), TextWrapping = TextWrapping.Wrap },
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
        var cancel = new System.Windows.Controls.Button { Content = "取消", IsCancel = true, Style = (Style)FindResource("DashboardButton") };
        var accept = new System.Windows.Controls.Button
        {
            Content = "提交并推送", IsDefault = true, IsEnabled = false,
            Style = (Style)FindResource("DashboardPrimaryButton")
        };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);

        var row = new System.Windows.Controls.DockPanel();
        var input = new System.Windows.Controls.TextBox { Style = (Style)FindResource(typeof(System.Windows.Controls.TextBox)) };
        var label = new System.Windows.Controls.Label
        {
            Content = projects.Length > 1 ? "统一提交摘要" : "提交摘要", Target = input,
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

    private async Task RunSelectedAsync(Operation operation)
    {
        if (running) return;
        var selected = savedProjects.Where(project => project.Checked).ToArray();
        if (selected.Length == 0)
        {
            HomeStatus.Text = "请先勾选要操作的项目。";
            return;
        }

        string? summary = null;
        if (operation == Operation.Publish)
        {
            var newCommits = selected.Where(project =>
                projectRuns.TryGetValue(project.Id, out var state) && state.Synced is { } synced &&
                synced.Count(ch => ch == ':') == 1).ToArray();
            if (newCommits.Length > 0)
            {
                summary = RequestCommitSummary(newCommits);
                if (summary is null) return;
            }
        }

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
        int skipped = 0;
        string? lastFailure = null;
        try
        {
            foreach (var project in selected)
            {
                HomeStatus.Text = $"正在处理「{project.Name}」…";
                WriteHomeLog($"[{project.Name}] {operation switch { Operation.Preview => "构建并预览", Operation.Apply => "应用同步", _ => "提交并推送" }}");
                projectRuns.TryGetValue(project.Id, out var state);
                if (operation != Operation.Publish && state?.Progress == "已提交，待推送")
                {
                    WriteHomeLog("已跳过。此项目已有待推送提交，请先重试推送。");
                    skipped++;
                    continue;
                }
                string? expected = operation == Operation.Publish ? state?.Synced : state?.Plan;
                if (operation != Operation.Preview && expected is null)
                {
                    WriteHomeLog(operation == Operation.Publish ? "已跳过，请先应用同步。" : "已跳过，请先构建并预览。");
                    skipped++;
                    continue;
                }
                string? pending = operation == Operation.Publish && expected?.Count(ch => ch == ':') == 2 ? expected : null;
                projectRuns.Remove(project.Id);
                try
                {
                    var result = await RunProjectAsync(project.Configuration, operation == Operation.Preview,
                        operation == Operation.Publish, expected, line =>
                        {
                            WriteHomeLog(line);
                            if (operation == Operation.Preview) CaptureHomeChange(project.Name, line);
                        }, commitSummary: summary, savePending: value =>
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
                    if (operation == Operation.Publish)
                    {
                        try { File.Delete(PendingPushPath(project)); }
                        catch (Exception error) { WriteHomeLog($"无法清理待推送记录：{error.Message}"); }
                    }
                    projectRuns[project.Id] = new RunState(result.Plan, result.Synced, operation switch
                    { Operation.Preview => "可同步", Operation.Apply => "可推送", _ => "推送步骤完成" });
                    completed++;
                    WriteHomeLog("操作完成。");
                    if (operation == Operation.Preview)
                        await TryStartSitePreviewAsync(project);
                }
                catch (Exception error)
                {
                    projectRuns[project.Id] = new RunState(null, pending, pending is null ? "操作失败" : "已提交，待推送");
                    lastFailure = $"{project.Name}：{error.Message}";
                    WriteHomeLog($"失败：{error.Message}");
                }
            }
        }
        finally
        {
            running = false;
            HomePage.Tag = false;
            HomeActions.IsEnabled = true;
            RefreshProjects();
            if (operation == Operation.Preview)
            {
                HomeChangesSummary.Text = $"预览列出 {HomeChanges.Items.Count} 项变更";
                if (completed == selected.Length) HomeResults.SelectedItem = HomeChangesTab;
            }
            HomeStatus.Text = $"完成 {completed} 个 · 失败 {selected.Length - completed - skipped} 个 · 跳过 {skipped} 个" +
                (lastFailure is null ? "" : $" · {lastFailure}");
        }
    }

    private void CaptureHomeChange(string project, string line)
    {
        var parts = line.Split('\t', 2);
        if (parts.Length == 2)
        {
            string? action = parts[0] switch
            {
                "A" => "新增", "M" => "修改", "D" => "源端删除", "T" => "类型变更", _ => null
            };
            if (action is not null) HomeChanges.Items.Add(new BatchChange(project, action, parts[1]));
        }
        else if (line.StartsWith("Would remove ", StringComparison.Ordinal))
            HomeChanges.Items.Add(new BatchChange(project, "目标清理", line.Substring(13)));
    }

    private void WriteHomeLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        HomeLog.AppendText(line + Environment.NewLine);
        HomeLog.ScrollToEnd();
    }

    private MessageBoxResult ConfirmProjectAction(string title, string message, string accept, string decline, bool canCancel)
    {
        var dialog = new Window
        {
            Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Background = System.Windows.Media.Brushes.White, FontFamily = FontFamily,
            Icon = Icon, FontSize = 14, UseLayoutRounding = true
        };
        var body = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        var prompt = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var icon = new System.Windows.Controls.Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = System.Windows.Media.Brushes.RoyalBlue, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "?", FontSize = 20, Foreground = System.Windows.Media.Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Controls.DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        prompt.Children.Add(icon);
        prompt.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(prompt);
        var buttons = new System.Windows.Controls.WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(buttons);
        dialog.Content = body;
        var result = MessageBoxResult.Cancel;

        void AddButton(string text, MessageBoxResult choice, bool primary, bool cancel)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = text, MinWidth = 84, IsCancel = cancel, IsDefault = canCancel && primary,
                Style = (Style)FindResource(primary ? "PrimaryButton" : typeof(System.Windows.Controls.Button))
            };
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            buttons.Children.Add(button);
            if ((!canCancel && cancel) || (canCancel && primary))
                dialog.Loaded += (_, _) => button.Focus();
        }

        AddButton(accept, MessageBoxResult.Yes, canCancel, false);
        AddButton(decline, MessageBoxResult.No, false, !canCancel);
        if (canCancel) AddButton("取消", MessageBoxResult.Cancel, false, true);
        dialog.ShowDialog();
        return result;
    }

}

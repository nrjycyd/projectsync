using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class WslManagerWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record Instance(string Name, bool IsDefault, string State, bool IsRunning, string Version, string Location, DiskUsage Disk, string? DiskFile, DevelopmentTool[] Tools, string ToolStatus, string Distribution, ImageSource? DistributionIcon)
    {
        public string DistributionHint => $"{Distribution} · {State}";

        public override string ToString() => $"{Name} · {DistributionHint}";
    }
    private sealed record DiskUsage(string Label, string Hint, double Percent = 0, bool HasMeasurement = false)
    {
        public bool IsLow => HasMeasurement && Percent <= 15;
    }

    private static async Task<DiskUsage?> ReadDiskUsageAsync(string instance)
    {
        try
        {
            var result = await InvokeAsync(new[] { "-d", instance, "--exec", "env", "LC_ALL=C",
                "df", "-B1", "--output=size,avail", "/" }, encoding: Encoding.UTF8, timeout: TimeSpan.FromSeconds(5));
            if (result.Code != 0) return null;

            var match = Regex.Match(result.Output, @"(?m)^[ \t]*([0-9]+)[ \t]+([0-9]+)[ \t\r]*$");
            if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out ulong total) ||
                !ulong.TryParse(match.Groups[2].Value, out ulong available) || total == 0 || available > total)
                return null;

            string totalLabel = VirtualDiskCapacity.Format(total);
            string availableLabel = VirtualDiskCapacity.Format(available);
            int separator = availableLabel.LastIndexOf(' ');
            string compact = totalLabel.EndsWith(availableLabel[separator..], StringComparison.Ordinal)
                ? availableLabel[..separator] : availableLabel;
            return new DiskUsage($"{compact} / {totalLabel}",
                $"根文件系统剩余 {availableLabel} / 总容量 {totalLabel}", 100d * available / total, true);
        }
        catch (Exception error) when (error is TimeoutException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly Func<string, IEnumerable<string>> projectsUsing;
    private readonly Func<string[], Task<(int Code, string Output)>> queryHost;
    private const int QueryTimeout = -1;
    private bool busy;
    private bool hostReady;
    private string help = "";
    private string installHelp = "";

    internal WslManagerWindow(Func<string, IEnumerable<string>> projectsUsing,
        Func<string[], Task<(int Code, string Output)>>? queryHost = null)
    {
        this.projectsUsing = projectsUsing;
        this.queryHost = queryHost ?? (args => InvokeAsync(args,
            timeout: TimeSpan.FromSeconds(args is ["--status"] or ["--list", "--verbose"] ? 20 : 8)));
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private void InstanceGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = InstanceFrame.CornerRadius.TopLeft - InstanceFrame.BorderThickness.Left;
        Instances.Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (busy) e.Cancel = true;
    }

    private static async Task<(int Code, string Output)> InvokeAsync(string[] args, Action<string>? log = null,
        Encoding? encoding = null, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        bool resize = args.Length >= 3 && args[0] == "--manage" && args[2] == "--resize";
        ProcessResult result = resize
            ? await ProcessRunner.RunWslResizeAsync(args, log, timeout, cancellation)
            : await ProcessRunner.RunWslAsync(args, encoding ?? Encoding.Unicode, timeout, log, closeStdin: timeout.HasValue, cancellation: cancellation);

        if (result.TimedOut)
        {
            string combined = result.CombinedOutput;
            string prefix = combined.Length > 0 ? combined + Environment.NewLine : "";
            return (QueryTimeout, $"{prefix}WSL 查询超时（{timeout!.Value.TotalSeconds:0} 秒）。");
        }
        return (result.ExitCode, result.CombinedOutput);
    }

    private static bool HostNeedsSetup(string output) => WslOutputParser.HostNeedsSetup(output);
    private static bool IsEmptyDistributionList(string output) => WslOutputParser.IsEmptyDistributionList(output);

    private void SetHostReady(bool ready, bool showSetup = false)
    {
        hostReady = ready;
        InstallDistributionButton.IsEnabled = UpdateWslButton.IsEnabled = ready;
        InstallDistributionButton.Appearance = ready ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        UpdateWslButton.Visibility = showSetup ? Visibility.Collapsed : Visibility.Visible;
        WslSetupButton.Visibility = showSetup ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordHostQueryFailure(string command, string output)
    {
        Log.Text = $"wsl.exe {command}{Environment.NewLine}{output}";
        LogArea.IsExpanded = true;
        string details = output.ReplaceLineEndings(" | ");
        SiteLift.App.App.AppendDiagnostic($"WSL 管理查询失败：wsl.exe {command} · {details[..Math.Min(details.Length, 1600)]}");
    }

    private void ShowHostSetup(string output, string? command = null, bool timedOut = false)
    {
        SetHostReady(false, showSetup: true);
        VersionText.Text = "";
        Instances.ItemsSource = Array.Empty<Instance>();
        if (command is not null)
            RecordHostQueryFailure(command, output);
        else
        {
            Log.Text = output;
            LogArea.IsExpanded = true;
        }
        Status.ShowFeedback(timedOut
            ? "WSL 检测超时。可以刷新重试，或打开「WSL 安装与修复」排查。"
            : "Windows 的 WSL 组件不可用。请先安装或修复 WSL，再刷新检测。", InfoBarSeverity.Warning);
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        Toolbar.IsEnabled = Instances.IsEnabled = false;
        try { await work(); }
        catch (Exception error) { Status.ShowFeedback(error.Message, InfoBarSeverity.Error); }
        finally
        {
            busy = false;
            Toolbar.IsEnabled = Instances.IsEnabled = true;
        }
    }

    private async Task<(int Code, string Output)?> CheckHostAsync(params string[] args)
    {
        string command = string.Join(" ", args);
        (int Code, string Output) result;
        try { result = await queryHost(args); }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            ShowHostSetup(error.Message, command);
            return null;
        }
        if (HostNeedsSetup(result.Output))
        {
            ShowHostSetup(result.Output, command);
            return null;
        }
        if (result.Code == QueryTimeout && !IsEmptyDistributionList(result.Output))
        {
            ShowHostSetup(result.Output, command, timedOut: true);
            return null;
        }
        return result;
    }

    private async Task RefreshAsync()
    {
        SetHostReady(false);
        Status.ShowFeedback("正在检测 Windows WSL 组件…");
        if (await CheckHostAsync("--status") is not { } host) return;
        if (host.Code != 0 && !IsEmptyDistributionList(host.Output))
            RecordHostQueryFailure("--status", host.Output);

        Status.ShowFeedback("正在读取已安装的发行版…");
        if (await CheckHostAsync("--list", "--verbose") is not { } list) return;
        if (list.Code != 0 && !IsEmptyDistributionList(list.Output))
        {
            VersionText.Text = "";
            Instances.ItemsSource = Array.Empty<Instance>();
            RecordHostQueryFailure("--list --verbose", list.Output);
            Status.ShowFeedback("无法读取 WSL 发行版列表，请查看日志后刷新检测。", InfoBarSeverity.Error);
            return;
        }
        var results = await Task.WhenAll(queryHost(new[] { "--version" }), queryHost(new[] { "--help" }));
        int setupErrorIndex = Array.FindIndex(results, result => HostNeedsSetup(result.Output));
        if (setupErrorIndex >= 0)
        {
            ShowHostSetup(results[setupErrorIndex].Output, setupErrorIndex == 0 ? "--version" : "--help");
            return;
        }
        string version = WslOutputParser.ExtractWslVersion(results[0].Output);
        VersionText.Text = version.Length > 0 ? $"WSL {version}" : "";
        help = results[1].Output;
        installHelp = WslOutputParser.ExtractInstallHelp(help);
        var locations = new Dictionary<string, (string Location, string DiskFile, string? Flavor, string? OsVersion)>(StringComparer.OrdinalIgnoreCase);
        using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
        {
            if (lxss is not null)
                foreach (string key in lxss.GetSubKeyNames())
                {
                    using var record = lxss.OpenSubKey(key);
                    if (record?.GetValue("DistributionName") is string name && record.GetValue("BasePath") is string place)
                        locations[name] = (NormalizeInstallLocation(place),
                            record.GetValue("VhdFileName") as string ?? "ext4.vhdx",
                            record.GetValue("Flavor") as string, record.GetValue("OsVersion") as string);
                }
        }

        var rows = new List<Instance>();
        foreach (var parsed in WslOutputParser.ParseDistributionList(list.Output))
        {
            var (location, diskFile, flavor, osVersion) = locations.GetValueOrDefault(parsed.Name);
            bool wsl2 = parsed.Version == 2;
            var (capacity, icon) = await Task.Run(() =>
                (wsl2 && location is not null ? VirtualDiskCapacity.Read(location, diskFile) : null,
                 ReadDistributionIcon(location, parsed.IsRunning)));
            string? limit = capacity is ulong bytes ? VirtualDiskCapacity.Format(bytes) : null;
            string unavailable = parsed.IsRunning ? "未能读取根文件系统剩余空间" : "实例未运行，剩余空间待检测";
            var disk = wsl2
                ? new DiskUsage(limit is null ? "—" : $"— / {limit}",
                    limit is null ? unavailable : $"{unavailable}。虚拟磁盘上限 {limit}。")
                : new DiskUsage("不适用", "WSL 1 没有独立的虚拟磁盘。");
            string distribution = DistributionLabel(flavor, osVersion);
            rows.Add(new Instance(parsed.Name, parsed.IsDefault, parsed.StateLabel, parsed.IsRunning,
                $"WSL {parsed.Version}", location ?? "—", disk, diskFile, Array.Empty<DevelopmentTool>(),
                parsed.IsRunning ? "检测中…" : "待检测", distribution, icon));
        }
        Instances.ItemsSource = rows;
        var checkedRows = await Task.WhenAll(rows.Select(async row =>
        {
            if (!row.IsRunning) return row;
            var diskTask = row.Version == "WSL 2" ? ReadDiskUsageAsync(row.Name) : Task.FromResult<DiskUsage?>(null);
            var (tools, status) = await ReadInstalledToolsAsync(row.Name);
            return row with { Tools = tools, ToolStatus = status, Disk = await diskTask ?? row.Disk };
        }));
        var selected = (Instances.SelectedItem as Instance)?.Name;
        Instances.ItemsSource = checkedRows;
        Instances.SelectedItem = checkedRows.FirstOrDefault(row => row.Name == selected);
        SetHostReady(true);
        Status.ShowFeedback(rows.Count == 0 ? "WSL 已就绪，尚未安装发行版。" : $"检测到 {rows.Count} 个发行版实例。");
    }

    private static string NormalizeInstallLocation(string location)
    {
        string path = location.StartsWith(@"\\?\", StringComparison.Ordinal) ? location[4..] : location;
        if (!Regex.IsMatch(path, @"^[A-Za-z]:\\")) return location;
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return location;
        }
    }

    private async Task ExecuteAsync(params string[] args)
    {
        Log.Clear();
        LogArea.IsExpanded = true;
        Status.ShowFeedback("正在执行 WSL 操作…");
        var result = await InvokeAsync(args, line => Dispatcher.BeginInvoke(() =>
        {
            Log.AppendText(line + Environment.NewLine);
            Log.ScrollToEnd();
        }));
        if (result.Code != 0)
        {
            if (HostNeedsSetup(result.Output))
            {
                ShowHostSetup(result.Output);
                return;
            }
            if (result.Output.Contains("smaller than minimum", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标容量小于 Linux 文件系统允许的最小容量，请查看日志。");
            throw new InvalidOperationException($"WSL 操作失败（退出代码 {result.Code}），请查看日志。");
        }
        await RefreshAsync();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RunAsync(RefreshAsync);
    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (hostReady) await RunAsync(() => ExecuteAsync("--update"));
    }

    private void MenuKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        FlyoutKeyboard.Handle(sender, e);

    private static Instance? Row(object sender) => (sender as FrameworkElement)?.DataContext as Instance;

    private void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } instance) return;
        try
        {
            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath) { UseShellExecute = true };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(instance.Name);
            start.ArgumentList.Add("--cd");
            start.ArgumentList.Add("~");
            using var terminal = Process.Start(start);
        }
        catch (Exception error) { Status.ShowFeedback(error.Message, InfoBarSeverity.Error); }
    }

    private async void DefaultClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } instance) await RunAsync(() => ExecuteAsync("--set-default", instance.Name));
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } instance) return;
        var file = new SaveFileDialog { Title = "导出 WSL 备份", Filter = "TAR 备份 (*.tar)|*.tar",
            FileName = instance.Name + ".tar", DefaultExt = ".tar" };
        if (file.ShowDialog(this) == true) await RunAsync(() => ExecuteAsync("--export", instance.Name, file.FileName));
    }

    private async void ResizeClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } instance) return;
        if (instance.Version != "WSL 2" || !help.Contains("--resize", StringComparison.Ordinal))
        {
            Status.ShowFeedback("调整磁盘上限需要支持 --resize 的 WSL 2。", InfoBarSeverity.Warning);
            return;
        }
        await RunAsync(async () =>
        {
            if (instance.DiskFile is null)
            {
                Status.ShowFeedback("无法确认当前磁盘上限，请先刷新检测。", InfoBarSeverity.Error);
                return;
            }
            ulong? capacity = await Task.Run(() => VirtualDiskCapacity.Read(instance.Location, instance.DiskFile));
            if (capacity is not ulong current || current == 0)
            {
                Status.ShowFeedback("无法确认当前磁盘上限，请先刷新检测。", InfoBarSeverity.Error);
                return;
            }
            const ulong gib = 1UL << 30;
            var (dialog, form, footer) = CreateDialog("调整虚拟磁盘上限");
            form.Children.Add(new TextBlock
            {
                Text = $"实例：{instance.Name}\n当前上限：{VirtualDiskCapacity.Format(current)}\n应用前会停止所有 WSL 实例及其中运行的程序。",
                TextWrapping = TextWrapping.Wrap
            });
            var size = AddInput(form, "新上限（GiB）");
            ulong requested = 0;
            bool ValidCapacity()
            {
                if (!PositiveSize(size.Text) || !uint.TryParse(size.Text.Trim(), out uint amount)) return false;
                requested = amount * gib;
                return requested != current && requested <= (64UL << 40);
            }
            if (!Confirm(dialog, footer, "停止 WSL 并调整", ValidCapacity,
                "请输入与当前上限不同的正整数 GiB 值，最大 64 TiB。")) return;
            if (requested < current && FluentDialog.Ask(this, "确认缩小磁盘上限",
                $"将「{instance.Name}」从 {VirtualDiskCapacity.Format(current)} 缩小到 {VirtualDiskCapacity.Format(requested)}。WSL 会检查文件系统能否缩到目标容量。继续吗？",
                "缩小", "取消", destructive: true) != MessageBoxResult.Yes)
                return;

            var stopped = await InvokeAsync(new[] { "--shutdown" });
            if (stopped.Code != 0) throw new InvalidOperationException(stopped.Output);
            await ExecuteAsync("--manage", instance.Name, "--resize",
                requested.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B");
        });
    }

    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } instance) return;
        await RunAsync(async () =>
        {
            var (dialog, form, footer) = CreateDialog($"注销并删除 {instance.Name}");
            string[] projects = projectsUsing(instance.Name).Distinct().ToArray();
            form.Children.Add(new TextBlock
            {
                Text = "此操作会永久删除该实例的 Linux 文件、设置和应用。" +
                    (projects.Length == 0 ? "" : "\n关联项目：" + string.Join("、", projects)),
                TextWrapping = TextWrapping.Wrap
            });
            var input = AddInput(form, "输入实例名称确认");
            if (Confirm(dialog, footer, "注销并删除", () => input.Text == instance.Name, "请输入完整的实例名称。", destructive: true))
                await ExecuteAsync("--unregister", instance.Name);
        });
    }

    private async void InstallClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (!hostReady) return;
        Status.ShowFeedback("正在获取在线发行版列表…");
        var list = await queryHost(new[] { "--list", "--online" });
        if (HostNeedsSetup(list.Output))
        {
            ShowHostSetup(list.Output, "--list --online");
            return;
        }
        if (list.Code != 0)
        {
            RecordHostQueryFailure("--list --online", list.Output);
            Status.ShowFeedback("无法获取在线目录，可手动输入发行版名称。", InfoBarSeverity.Warning);
        }
        else
            Status.IsOpen = false;
        var names = list.Code == 0 ? WslOutputParser.ParseOnlineDistributions(list.Output) : Array.Empty<string>();
        var (dialog, form, footer) = CreateDialog("安装发行版");
        var distribution = new ComboBox { IsEditable = names.Length == 0, ItemsSource = names };
        AddField(form, "发行版", distribution);
        if (names.Length == 0) form.Children.Add(new TextBlock { Text = "可手动填写发行版名称，例如 Ubuntu。", TextWrapping = TextWrapping.Wrap });
        var name = AddInput(form, "实例名称（可选）");
        name.IsEnabled = SupportsInstall("--name");
        var place = new DockPanel();
        var browse = new Wpf.Ui.Controls.Button { Content = "浏览…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var location = new Wpf.Ui.Controls.TextBox { MinHeight = 34, Padding = new Thickness(9, 4, 9, 4) };
        place.Children.Add(browse);
        place.Children.Add(location);
        AddField(form, "安装位置（可选）", place);
        place.IsEnabled = SupportsInstall("--location");
        browse.Click += (_, _) =>
        {
            var folder = new OpenFolderDialog { Title = "选择安装位置" };
            if (folder.ShowDialog(dialog) == true) location.Text = folder.FolderName;
        };
        var size = AddInput(form, "虚拟磁盘上限（GB，可选，最大 65536 GB，按使用量增长）");
        size.IsEnabled = SupportsInstall("--vhd-size");
        var web = new Wpf.Ui.Controls.ToggleSwitch { OnContent = "从网络下载", OffContent = "从网络下载", IsChecked = SupportsInstall("--web-download"),
            IsEnabled = SupportsInstall("--web-download"), Margin = new Thickness(0, 12, 0, 0) };
        form.Children.Add(web);
        string SelectedDistribution() => (distribution.SelectedItem as string ?? distribution.Text).Trim();
        bool Valid() =>
            ValidName(SelectedDistribution()) &&
            (name.Text.Trim().Length == 0 || ValidName(name.Text.Trim()) &&
                !((IEnumerable<Instance>?)Instances.ItemsSource ?? Array.Empty<Instance>()).Any(row =>
                    string.Equals(row.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase))) &&
            (location.Text.Trim().Length == 0 || ValidInstallFolder(location.Text.Trim())) &&
            (size.Text.Trim().Length == 0 || (PositiveSize(size.Text) && uint.TryParse(size.Text.Trim(), out uint gb) && gb <= 65536));
        if (!Confirm(dialog, footer, "开始安装", Valid, "请选择发行版，并检查实例名称、安装目录和虚拟磁盘容量（1~65536 GB）。"))
        {
            Status.ShowFeedback("已取消安装。");
            return;
        }
        var args = new List<string> { "--install", "-d", SelectedDistribution() };
        if (web.IsChecked == true) args.Add("--web-download");
        if (SupportsInstall("--no-launch")) args.Add("--no-launch");
        if (name.IsEnabled && name.Text.Trim().Length > 0) args.AddRange(new[] { "--name", name.Text.Trim() });
        if (place.IsEnabled && location.Text.Trim().Length > 0) args.AddRange(new[] { "--location", Path.GetFullPath(location.Text.Trim()) });
        if (size.IsEnabled && size.Text.Trim().Length > 0) args.AddRange(new[] { "--vhd-size", size.Text.Trim() + "GB" });
        await ExecuteAsync(args.ToArray());
    });

    private bool SupportsInstall(string option) => Regex.IsMatch(installHelp, @"(?m)^\s+" + Regex.Escape(option) + @"(?:[\s,]|$)");
    private static bool ValidName(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z");
    private static bool PositiveSize(string value) => Regex.IsMatch(value.Trim(), @"\A[0-9]+\z") &&
        uint.TryParse(value.Trim(), out uint size) && size > 0;

    private static bool ValidInstallFolder(string path)
    {
        if (!Regex.IsMatch(path, @"^[A-Za-z]:\\") || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return full.Length > 3 && !File.Exists(full) &&
                (!Directory.Exists(full) || !Directory.EnumerateFileSystemEntries(full).Any());
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    private (Window Dialog, StackPanel Form, StackPanel Footer) CreateDialog(string title)
    {
        var form = new StackPanel();
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var body = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        var dialog = FluentDialog.Create(this, title, body, 510);
        FluentDialog.LimitToWorkArea(dialog, this);
        return (dialog, form, footer);
    }

    private static TextBox AddInput(StackPanel form, string label)
    {
        var input = new Wpf.Ui.Controls.TextBox { MinHeight = 34, Padding = new Thickness(9, 4, 9, 4) };
        AddField(form, label, input);
        return input;
    }

    private static void AddField(StackPanel form, string label, FrameworkElement field)
    {
        form.Children.Add(new Label { Content = label, Target = field, Padding = new Thickness(0, 8, 0, 4) });
        form.Children.Add(field);
    }

    private bool Confirm(Window dialog, StackPanel footer, string action, Func<bool> validate,
        string invalidMessage, bool destructive = false)
    {
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        message.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        footer.Children.Add(message);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, MinWidth = 84, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Wpf.Ui.Controls.Button { Content = action, MinWidth = 100, Padding = new Thickness(14, 5, 14, 5) };
        confirm.Appearance = destructive ? Wpf.Ui.Controls.ControlAppearance.Danger : Wpf.Ui.Controls.ControlAppearance.Primary;
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        confirm.Click += (_, _) =>
        {
            if (validate()) dialog.DialogResult = true;
            else message.Text = invalidMessage;
        };
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
}

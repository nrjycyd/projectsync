using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ProjectSync.Core;

namespace ProjectSync.App.Views;

public partial class FileCopyWindow : Wpf.Ui.Controls.FluentWindow
{
    private sealed record Location(string Label, string? Distribution)
    {
        public override string ToString() => Label;
    }

    private sealed record PreviewRow(string Action, string Path);
    private IReadOnlyList<CopyEntry>? preview;
    private CancellationTokenSource? operation;

    internal FileCopyWindow(Func<Task<string[]>> getDistributions)
    {
        InitializeComponent();
        var pc = new Location("本机 Windows", null);
        SourceLocation.Items.Add(pc);
        TargetLocation.Items.Add(pc);
        SourceLocation.SelectedIndex = TargetLocation.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            try
            {
                foreach (var name in (await getDistributions()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var location = new Location($"WSL · {name}", name);
                    SourceLocation.Items.Add(location);
                    TargetLocation.Items.Add(location);
                }
            }
            catch (Exception ex)
            {
                SourceLocation.ToolTip = TargetLocation.ToolTip = $"无法读取 WSL 实例：{ex.Message}";
            }
        };
    }

    private static string? Distribution(ComboBox selector) => (selector.SelectedItem as Location)?.Distribution;

    private void EndpointChanged(object sender, RoutedEventArgs e)
    {
        if (SourcePath is null || TargetPath is null) return;
        SourcePath.ToolTip = PathHint(SourceLocation);
        TargetPath.ToolTip = PathHint(TargetLocation);
        OptionsChanged(sender, e);
    }

    private static string PathHint(ComboBox selector) => Distribution(selector) is null
        ? @"Windows 绝对路径，例如 D:\资料" : "WSL 绝对路径，例如 /home/blog/资料";

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (Changes is null) return;
        preview = null;
        Changes.ItemsSource = null;
        CopyStatus.Text = "请选择来源和目标，或直接输入绝对路径。";
    }

    private void SwapClick(object sender, RoutedEventArgs e)
    {
        (SourceLocation.SelectedItem, TargetLocation.SelectedItem) =
            (TargetLocation.SelectedItem, SourceLocation.SelectedItem);
        (SourcePath.Text, TargetPath.Text) = (TargetPath.Text, SourcePath.Text);
    }

    private void BrowseSourceFile(object sender, RoutedEventArgs e) => Browse(SourceLocation, SourcePath, true);
    private void BrowseSourceFolder(object sender, RoutedEventArgs e) => Browse(SourceLocation, SourcePath, false);
    private void BrowseTargetFolder(object sender, RoutedEventArgs e) => Browse(TargetLocation, TargetPath, false);

    private void Browse(ComboBox selector, TextBox field, bool file)
    {
        string? distro = Distribution(selector);
        try
        {
            string initial = distro is null ? "" : FileCopy.ResolvePath("/", distro);
            string selected;
            if (file)
            {
                var dialog = new OpenFileDialog { InitialDirectory = initial, CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                selected = dialog.FileName;
            }
            else
            {
                var dialog = new OpenFolderDialog { InitialDirectory = initial };
                if (dialog.ShowDialog(this) != true) return;
                selected = dialog.FolderName;
            }

            if (distro is not null)
            {
                string? share = new[] { $@"\\wsl.localhost\{distro}", $@"\\wsl$\{distro}" }
                    .FirstOrDefault(root => selected.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        selected.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
                if (share is null) throw new ArgumentException($"请选择 {distro} 中的路径。");
                selected = "/" + selected[share.Length..].TrimStart('\\').Replace('\\', '/');
            }
            FileCopy.ResolvePath(selected, distro);
            field.Text = selected;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            CopyStatus.Text = ex.Message;
        }
    }

    private async void PreviewClick(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void CopyClick(object sender, RoutedEventArgs e) => await RunAsync(true);
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (operation is null) return;
        e.Cancel = true;
        operation.Cancel();
    }

    private async Task RunAsync(bool apply)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        CopyOptions.IsEnabled = PreviewCopy.IsEnabled = StartCopy.IsEnabled = false;
        CancelCopy.Visibility = Visibility.Visible;
        CopyStatus.Text = "正在检查来源和目标…";
        int filesCopied = 0, directoriesCreated = 0;
        try
        {
            string source = FileCopy.ResolvePath(SourcePath.Text, Distribution(SourceLocation));
            string target = FileCopy.ResolvePath(TargetPath.Text, Distribution(TargetLocation));
            bool replace = Conflict.SelectedIndex == 1;
            bool skipMarks = SkipMarks.IsChecked == true;
            var plan = await Task.Run(() => FileCopy.Preview(source, target, replace, skipMarks, cancellation.Token));
            bool changed = preview is not null && !preview.SequenceEqual(plan);
            preview = plan;
            Changes.ItemsSource = plan.Select(entry => new PreviewRow(entry.Action switch
            {
                CopyAction.CreateDirectory => "创建目录",
                CopyAction.Copy => "复制",
                CopyAction.Replace => "覆盖",
                CopyAction.SkipExisting => "跳过同名",
                _ => "跳过下载标记"
            }, entry.RelativePath)).ToList();
            int copies = plan.Count(entry => entry.Action == CopyAction.Copy);
            int replacements = plan.Count(entry => entry.Action == CopyAction.Replace);
            int skips = plan.Count(entry => entry.Action is CopyAction.SkipExisting or CopyAction.SkipDownloadMark);
            int folders = plan.Count(entry => entry.Action == CopyAction.CreateDirectory);
            CopyStatus.Text = $"复制 {copies} 个 · 覆盖 {replacements} 个 · 跳过 {skips} 个 · 创建目录 {folders} 个";
            if (!apply) return;
            if (changed)
            {
                CopyStatus.Text = "来源或目标已变化，已刷新清单。请核对后再次点击开始复制。";
                return;
            }
            if (copies + replacements + folders == 0)
            {
                CopyStatus.Text = $"没有需要复制的项目，跳过 {skips} 个。";
                return;
            }
            if (replacements > 0 && FluentDialog.Ask(this, "确认覆盖", $"将覆盖目标中的 {replacements} 个同名文件。继续复制？",
                "覆盖", "取消", destructive: true) != MessageBoxResult.Yes)
                return;

            CopyStatus.Text = "正在复制…";
            await Task.Run(() => FileCopy.Apply(plan, skipMarks, entry =>
            {
                if (entry.Action == CopyAction.CreateDirectory) directoriesCreated++;
                else filesCopied++;
            }, cancellation.Token));
            preview = null;
            CopyStatus.Text = $"复制完成，处理文件 {filesCopied} 个 · 创建目录 {directoriesCreated} 个 · 跳过 {skips} 个。";
        }
        catch (OperationCanceledException)
        {
            preview = null;
            CopyStatus.Text = $"已取消，已处理文件 {filesCopied} 个 · 已创建目录 {directoriesCreated} 个。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            preview = null;
            CopyStatus.Text = $"{ex.Message} 已处理文件 {filesCopied} 个 · 已创建目录 {directoriesCreated} 个。";
        }
        finally
        {
            operation = null;
            CopyOptions.IsEnabled = PreviewCopy.IsEnabled = StartCopy.IsEnabled = true;
            CancelCopy.Visibility = Visibility.Collapsed;
        }
    }
}

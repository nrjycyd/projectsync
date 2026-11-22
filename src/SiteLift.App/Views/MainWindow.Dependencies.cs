using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private async Task RunMaintenanceCommandAsync(ProjectEntry project, string distribution, string user,
        string directory, string nodeBin, string command, string operation, string confirmLabel)
    {
        if (running) return;
        if (string.IsNullOrWhiteSpace(command))
        {
            HomeStatus.ShowFeedback($"请先在「{project.Name}」的本地构建环境中填写{operation}命令。", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            distribution = SyncPath.ValidateWslDistribution(distribution);
            user = SyncPath.ValidateWslUser(user);
            directory = SyncPath.ValidateWslRoot(directory);
            nodeBin = SyncPath.ValidateNodeBin(nodeBin);
        }
        catch (ArgumentException error)
        {
            HomeStatus.ShowFeedback(error.Message, InfoBarSeverity.Warning);
            return;
        }

        command = command.ReplaceLineEndings("\n");
        if (!ConfirmMaintenanceCommand(project.Name, distribution, user, directory, command, operation, confirmLabel)) return;

        running = true;
        HomePage.Tag = true;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        ResetHomeLog();
        HomeResults.SelectedItem = HomeLogTab;
        HomeStatus.ShowFeedback($"正在为「{project.Name}」执行{operation}…");
        WriteHomeLog($"[{project.Name}] {operation}");
        // Maintenance may change the dependency lockfile, so refresh any cached preview.
        if (projectRuns.TryGetValue(project.Id, out var state) && state.Progress != "已提交，待推送")
        {
            projectRuns.Remove(project.Id);
            DeletePreview(project);
        }

        try
        {
            if (stoppingPreviews is { } stopping) await stopping;
            await StopPreviewServerAsync(project.Id);

            const string script = """if [[ -n $1 ]]; then export PATH="$1:$PATH"; fi; exec bash -e -o pipefail -s""";
            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath)
            {
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-d", distribution, "-u", user, "--cd", directory,
                "--exec", "bash", "-c", script, "sitelift-reinstall", nodeBin })
                start.ArgumentList.Add(arg);

            var result = await ProcessRunner.RunAsync(start,
                onStdout: line => Dispatcher.BeginInvoke(() => WriteHomeLog(line)),
                onStderr: line => Dispatcher.BeginInvoke(() => WriteHomeLog(line)),
                stdinContent: command + "\n");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"命令返回退出代码 {result.ExitCode}，请查看日志。");
            HomeStatus.ShowFeedback($"「{project.Name}」{operation}完成。", InfoBarSeverity.Success);
            WriteHomeLog($"{operation}完成。");
        }
        catch (Exception error)
        {
            string message = $"{operation}失败：{error.Message}";
            HomeStatus.ShowFeedback(message, InfoBarSeverity.Error);
            WriteHomeLog(message);
        }
        finally
        {
            running = false;
            HomePage.Tag = false;
            HomeActions.IsEnabled = true;
            RefreshProjects();
        }
    }

    private bool ConfirmMaintenanceCommand(string project, string distribution, string user, string directory, string command, string operation, string confirmLabel)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = FluentDialog.Create(this, operation, panel, 510);
        panel.Children.Add(new TextBlock
        {
            Text = $"{project}\n{distribution} · {user}\n{directory}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new Wpf.Ui.Controls.TextBox
        {
            Text = command, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 220,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        panel.Children.Add(new TextBlock
        {
            Text = "命令将直接修改该 WSL 项目目录，请确认以上操作。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        panel.Children.Add(actions);
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var accept = new Wpf.Ui.Controls.Button { Content = confirmLabel, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
}

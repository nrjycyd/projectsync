using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

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
        if (string.IsNullOrWhiteSpace(user) ||
            !directory.StartsWith("/", StringComparison.Ordinal) ||
            Array.Exists(directory.Split('/'), part => part is "." or "..") ||
            directory.Trim('/') == "")
        {
            HomeStatus.ShowFeedback("请先检测并保存有效的 WSL 用户和项目根目录。", InfoBarSeverity.Warning);
            return;
        }

        command = command.ReplaceLineEndings("\n");
        if (!ConfirmMaintenanceCommand(project.Name, distribution, user, directory, command, operation, confirmLabel)) return;

        running = true;
        HomePage.Tag = true;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        HomeLog.Clear();
        HomeResults.SelectedItem = HomeLogTab;
        HomeStatus.ShowFeedback($"正在为「{project.Name}」执行{operation}…");
        WriteHomeLog($"[{project.Name}] {operation}");
        // Maintenance may change the dependency lockfile, so refresh any cached preview.
        if (projectRuns.TryGetValue(project.Id, out var state) && state.Progress != "已提交，待推送")
            projectRuns.Remove(project.Id);

        try
        {
            if (previewServers.Remove(project.Id, out var preview))
            {
                using (preview)
                {
                    preview.StandardInput.Close();
                    await preview.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            const string script = """if [[ -n $1 ]]; then export PATH="$1:$PATH"; fi; exec bash -e -o pipefail -s""";
            var start = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-d", distribution, "-u", user, "--cd", directory,
                "--exec", "bash", "-c", script, "projectsync-reinstall", nodeBin })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WSL。");
            var output = ReadLog(process.StandardOutput, WriteHomeLog);
            var errors = ReadLog(process.StandardError, WriteHomeLog);
            try { await process.StandardInput.WriteAsync(command + "\n"); }
            finally { process.StandardInput.Close(); }
            await Task.WhenAll(output, errors, process.WaitForExitAsync());
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"命令返回退出代码 {process.ExitCode}，请查看日志。");
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

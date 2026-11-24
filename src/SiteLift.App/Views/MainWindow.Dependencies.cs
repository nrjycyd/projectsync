using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
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
        if (command.Contains('\0'))
        {
            HomeStatus.ShowFeedback($"{operation}命令不能包含空字符。", InfoBarSeverity.Warning);
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

        using var cancellation = new CancellationTokenSource();
        batchCancellation = cancellation;
        StopBatchButton.Content = $"停止{operation}";
        StopBatchButton.IsEnabled = true;
        SetActivityText(BatchProgressText, $"正在{operation}「{project.Name}」");
        BatchProgressPanel.Visibility = Visibility.Visible;
        HomeStatus.IsOpen = false;
        running = true;
        IsRowSelectionEnabled = false;
        HomeActions.IsEnabled = false;
        UpdateHomeActions();
        ResetHomeLog();
        HomeResults.SelectedItem = HomeLogTab;
        SetResultsCollapsed(false);
        WriteHomeLog($"[{project.Name}] {operation}");
        // Maintenance may change the dependency lockfile, so refresh any cached preview and build receipt.
        InvalidateSuccessfulBuild(project);
        if (projectRuns.TryGetValue(project.Id, out var state) && state.Progress != "已提交，待推送")
        {
            projectRuns.Remove(project.Id);
            DeletePreview(project);
        }

        try
        {
            if (stoppingPreviews is { } stopping)
            {
                try { await stopping.WaitAsync(cancellation.Token); }
                catch when (!cancellation.IsCancellationRequested) { }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            await StopPreviewServerAsync(project.Id);
            cancellation.Token.ThrowIfCancellationRequested();

            int exitCode = await RunCancellableWslMaintenanceAsync(
                distribution, user, directory, nodeBin, command, cancellation.Token);
            if (exitCode != 0)
                throw new InvalidOperationException($"命令返回退出代码 {exitCode}，请查看日志。");
            HomeStatus.ShowFeedback($"「{project.Name}」{operation}完成。", InfoBarSeverity.Success);
            WriteHomeLog($"{operation}完成。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            HomeStatus.ShowFeedback($"已停止「{project.Name}」的{operation}。", InfoBarSeverity.Informational);
            WriteHomeLog($"{operation}已停止。");
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
            IsRowSelectionEnabled = true;
            batchCancellation = null;
            BatchProgressPanel.Visibility = Visibility.Collapsed;
            HomeActions.IsEnabled = true;
            RefreshProjects();
            if (closeAfterBatch)
            {
                closeAfterBatch = false;
                Close();
            }
        }
    }

    private async Task<int> RunCancellableWslMaintenanceAsync(
        string distribution,
        string user,
        string directory,
        string nodeBin,
        string command,
        CancellationToken cancellation)
    {
        // Keep stdin open after sending the NUL-terminated command so closing stdin signals the WSL process group to stop.
        const string script = """
            exec 3<&0
            if [[ -n $1 ]]; then export PATH="$1:$PATH"; fi
            IFS= read -r -d '' command <&3 || exit 130
            setsid bash -e -o pipefail -c "$command" </dev/null 3<&- &
            job=$!
            (
                while IFS= read -r _; do :; done
                if kill -TERM -- "-$job" 2>/dev/null || kill -TERM "$job" 2>/dev/null; then
                    for _ in 1 2 3 4 5 6 7 8 9 10; do
                        kill -0 -- "-$job" 2>/dev/null || kill -0 "$job" 2>/dev/null || break
                        sleep 0.2
                    done
                    kill -KILL -- "-$job" 2>/dev/null || kill -KILL "$job" 2>/dev/null || true
                fi
            ) <&3 >/dev/null 2>&1 &
            watcher=$!
            cleanup() {
                kill "$watcher" 2>/dev/null || true
                if kill -TERM -- "-$job" 2>/dev/null || kill -TERM "$job" 2>/dev/null; then
                    sleep 1
                    kill -KILL -- "-$job" 2>/dev/null || kill -KILL "$job" 2>/dev/null || true
                fi
            }
            trap cleanup EXIT
            wait "$job"
            status=$?
            kill "$watcher" 2>/dev/null || true
            wait "$watcher" 2>/dev/null || true
            if kill -0 -- "-$job" 2>/dev/null; then
                kill -TERM -- "-$job" 2>/dev/null || true
                for _ in 1 2 3 4 5; do
                    kill -0 -- "-$job" 2>/dev/null || break
                    sleep 0.2
                done
                kill -KILL -- "-$job" 2>/dev/null || true
            fi
            trap - EXIT
            exit "$status"
            """;

        var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-d", distribution, "-u", user, "--cd", directory,
            "--exec", "bash", "-c", script.ReplaceLineEndings("\n"), "sitelift-reinstall", nodeBin })
            start.ArgumentList.Add(arg);

        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 WSL 进程。");
        using var logCts = new CancellationTokenSource();

        Task ReadLog(StreamReader reader) => Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync(logCts.Token) is { } raw)
                {
                    string line = raw.Trim('\0', '\uFEFF', '\r');
                    if (line.Length > 0)
                        _ = Dispatcher.BeginInvoke(() => WriteHomeLog(line));
                }
            }
            catch (OperationCanceledException) when (logCts.IsCancellationRequested) { }
        });

        Task stdoutTask = ReadLog(process.StandardOutput);
        Task stderrTask = ReadLog(process.StandardError);
        Task execution = Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync());
        try
        {
            try
            {
                await process.StandardInput.WriteAsync((command + "\0").AsMemory(), cancellation);
                await process.StandardInput.FlushAsync(cancellation);
            }
            catch (IOException) { }

            await execution.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            if (await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(4))) != execution)
            {
                ProcessRunner.TryKillTree(process);
                logCts.Cancel();
            }
            try { await execution; }
            catch (OperationCanceledException) when (logCts.IsCancellationRequested) { }
            throw;
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private bool ConfirmMaintenanceCommand(string project, string distribution, string user, string directory, string command, string operation, string confirmLabel)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        var dialog = FluentDialog.Create(this, operation, panel, 510);
        var projectTitle = new TextBlock { Text = project, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        projectTitle.SetResourceReference(FrameworkElement.StyleProperty, "SettingsGroupHeader");
        panel.Children.Add(projectTitle);
        var environmentMeta = new TextBlock
        {
            Text = $"{distribution} · {user}\n{directory}",
            Margin = new Thickness(0, 0, 0, 12)
        };
        environmentMeta.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        panel.Children.Add(environmentMeta);
        var commandBox = new Wpf.Ui.Controls.TextBox
        {
            Text = command, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        commandBox.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceMonoInput");
        panel.Children.Add(commandBox);
        var warning = new TextBlock
        {
            Text = "命令将直接修改该 WSL 项目目录，请确认以上操作。",
            Margin = new Thickness(0, 10, 0, 0)
        };
        warning.SetResourceReference(FrameworkElement.StyleProperty, "SettingsCardSubtitle");
        panel.Children.Add(warning);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var footer = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Child = actions
        };
        panel.Children.Add(footer);
        var cancel = new Wpf.Ui.Controls.Button { Content = "取消", IsCancel = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        var accept = new Wpf.Ui.Controls.Button { Content = confirmLabel, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 96 };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        accept.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }
}

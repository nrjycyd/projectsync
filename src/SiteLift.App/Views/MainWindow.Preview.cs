using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private readonly Dictionary<Guid, Process> previewServers = new();
    private readonly Dictionary<Guid, string> successfulBuilds = new();
    private Task? stoppingPreviews;

    private static void SetActivityText(TextBlock label, string text)
    {
        if (label.Text == text) return;
        label.Text = text;
        UIElementAutomationPeer.FromElement(label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void UpdatePreviewStatus()
    {
        int count = previewServers.Count;
        bool stopping = stoppingPreviews is not null;
        PreviewStatusPanel.Visibility = count > 0 || stopping ? Visibility.Visible : Visibility.Collapsed;
        StopPreviewButton.IsEnabled = !stopping;
        if (!stopping)
            StopPreviewButton.Content = count > 1 ? "停止全部预览" : "停止预览";
        string? name = count == 1
            ? savedProjects.Find(project => project.Id == previewServers.Keys.First())?.Name
            : null;
        SetActivityText(PreviewStatusText, stopping ? "正在停止预览…" :
            name is not null ? $"正在预览「{name}」" : $"正在预览 · {count} 个项目");
    }

    private async void StopPreviews(object sender, RoutedEventArgs e)
    {
        if (stoppingPreviews is not null || previewServers.Count == 0) return;
        stoppingPreviews = Task.WhenAll(previewServers.Keys.ToArray().Select(StopPreviewServerAsync));
        UpdatePreviewStatus();
        try { await stoppingPreviews; }
        catch (Exception error)
        {
            WriteHomeLog($"无法停止预览：{error.Message}");
            HomeStatus.ShowFeedback("部分预览未停止，详情见运行日志。", InfoBarSeverity.Warning);
        }
        finally
        {
            stoppingPreviews = null;
            UpdatePreviewStatus();
        }
    }

    private async Task StopPreviewServerAsync(Guid projectId)
    {
        if (!previewServers.Remove(projectId, out var server)) return;
        try
        {
            try { server.StandardInput.Close(); } catch (IOException) { }
            try
            {
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                ProcessRunner.TryKillTree(server);
                try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
            }
        }
        finally
        {
            server.Dispose();
            UpdatePreviewStatus();
        }
    }

    private void ForceStopAllPreviews()
    {
        foreach (var server in previewServers.Values.ToArray())
        {
            try { server.StandardInput.Close(); } catch (Exception) { }
            ProcessRunner.TryKillTree(server);
            server.Dispose();
        }
        previewServers.Clear();
    }

    private async Task WatchPreviewExitAsync(Guid projectId, Process server, Task exit)
    {
        await exit;
        if (!previewServers.TryGetValue(projectId, out var currentServer) || currentServer != server) return;
        previewServers.Remove(projectId);
        server.Dispose();
        UpdatePreviewStatus();
    }

    private static string? BuildFingerprint(ProjectEntry project) =>
        ProjectConfig.ComputeBuildFingerprint(project.Configuration);

    private bool HasSuccessfulBuild(ProjectEntry project) =>
        successfulBuilds.TryGetValue(project.Id, out var hash) && hash == BuildFingerprint(project);

    private void RestoreBuildReceipt(ProjectEntry project)
    {
        try
        {
            if (SiteLift.App.App.Store.LoadBuildReceipt(project.Id) is { Length: > 0 } hash)
                successfulBuilds[project.Id] = hash;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteHomeLog($"无法读取构建记录：{error.Message}");
        }
    }

    private void RememberSuccessfulBuild(ProjectEntry project)
    {
        if (BuildFingerprint(project) is not { } hash) return;
        successfulBuilds[project.Id] = hash;
        try
        {
            SiteLift.App.App.Store.SaveBuildReceipt(project.Id, hash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteHomeLog($"无法保存构建记录：{error.Message}");
        }
    }

    private void InvalidateSuccessfulBuild(ProjectEntry project)
    {
        SiteLift.App.App.Store.DeleteBuildReceipt(project.Id);
        successfulBuilds.Remove(project.Id);
    }

    private static bool HasPreviewCommand(ProjectEntry project) =>
        ProjectConfig.TryReadString(project.Configuration, "previewCommand").Length > 0;

    private static bool ShouldAutoOpenPreview(ProjectEntry project) =>
        ProjectConfig.TryReadBool(project.Configuration, "previewAfterBuild");

    private static Uri? TryExtractPreviewUrl(string line)
    {
        var match = Regex.Match(line,
            @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):[0-9]+(?:/[^\s\x1b""'<>()]*)?",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        string raw = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Host == "0.0.0.0")
            return new UriBuilder(uri) { Host = "localhost" }.Uri;
        return uri;
    }

    private static async Task<bool> TryConnectLoopbackAsync(Uri target, CancellationToken token)
    {
        string[] hosts = target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new[] { "127.0.0.1", "::1" }
            : new[] { target.Host.Trim('[', ']') };
        foreach (string host in hosts)
        {
            using var connection = new TcpClient();
            try
            {
                await connection.ConnectAsync(host, target.Port, token);
                return true;
            }
            catch (SocketException) { }
        }
        return false;
    }

    private async Task<bool> TryStartSitePreviewAsync(ProjectEntry project, CancellationToken cancellation)
    {
        Process? server = null;
        try
        {
            var config = ProjectConfig.Parse(project.Configuration);
            if (string.IsNullOrWhiteSpace(config.PreviewCommand))
                throw new InvalidOperationException("请在本地构建环境中填写预览命令。");
            string distro = SyncPath.ValidateWslDistribution(config.Distro);
            string user = SyncPath.ValidateWslUser(config.WslUser);
            string wslRepo = SyncPath.ValidateWslRoot(config.WslRepo);
            string nodeBin = SyncPath.ValidateNodeBin(config.NodeBin);

            if (stoppingPreviews is { } stopping)
            {
                try { await stopping.WaitAsync(cancellation); }
                catch when (!cancellation.IsCancellationRequested) { }
            }
            await StopPreviewServerAsync(project.Id);
            cancellation.ThrowIfCancellationRequested();

            // Closing stdin signals the WSL process group owned by this preview.
            const string script = """
                if [[ -n $1 ]]; then export PATH="$1:$PATH"; fi
                setsid bash -c "$2" </dev/null &
                server=$!
                (
                    while IFS= read -r line; do :; done
                    kill -TERM -- "-$server" 2>/dev/null || kill -TERM "$server" 2>/dev/null || true
                ) <&3 &
                watcher=$!
                cleanup() {
                    kill "$watcher" 2>/dev/null || true
                    if kill -TERM -- "-$server" 2>/dev/null; then
                        sleep 1
                        kill -KILL -- "-$server" 2>/dev/null || true
                    fi
                }
                trap cleanup EXIT
                wait "$server"
                """;
            var start = new ProcessStartInfo(ProcessRunner.WslExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            string wrapper = "exec 3<&0; " + script.ReplaceLineEndings("\n");
            foreach (string arg in new[] { "-d", distro, "-u", user,
                "--cd", wslRepo, "--exec", "bash", "-c", wrapper, "sitelift-preview", nodeBin, config.PreviewCommand.ReplaceLineEndings("\n") })
                start.ArgumentList.Add(arg);

            server = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WSL 预览。");
            var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Output(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is not { } line) return;
                Dispatcher.BeginInvoke(new Action(() => WriteHomeLog($"[{project.Name} 预览] {line}")));
                if (TryExtractPreviewUrl(line) is { } uri)
                    address.TrySetResult(uri);
            }

            server.OutputDataReceived += Output;
            server.ErrorDataReceived += Output;
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            var exit = server.WaitForExitAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var timeout = Task.Delay(-1, deadline.Token);
            if (await Task.WhenAny(address.Task, exit, timeout) != address.Task)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new InvalidOperationException("预览命令未输出可访问的本地地址。请查看预览日志。");
            }

            Uri target = await address.Task;
            while (true)
            {
                if (server.HasExited) throw new InvalidOperationException("预览服务已退出，请查看日志。");
                if (await TryConnectLoopbackAsync(target, deadline.Token))
                    break;
                await Task.Delay(200, deadline.Token);
            }

            cancellation.ThrowIfCancellationRequested();
            using var browser = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            previewServers[project.Id] = server;
            _ = WatchPreviewExitAsync(project.Id, server, exit);
            UpdatePreviewStatus();
            server = null;
            return true;
        }
        catch (Exception error)
        {
            cancellation.ThrowIfCancellationRequested();
            string reason = error is OperationCanceledException
                ? "等待预览服务响应超时。" : error.Message;
            WriteHomeLog($"[{project.Name}] 无法打开浏览器预览：{reason}");
            HomeResults.SelectedItem = HomeLogTab;
            return false;
        }
        finally
        {
            if (server is not null)
            {
                try { server.StandardInput.Close(); } catch (IOException) { }
                ProcessRunner.TryKillTree(server);
                server.Dispose();
            }
        }
    }
}

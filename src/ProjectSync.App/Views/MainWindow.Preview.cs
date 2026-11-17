using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private readonly Dictionary<Guid, Process> previewServers = new();
    private readonly Dictionary<Guid, string> successfulBuilds = new();

    private static string? BuildFingerprint(ProjectEntry project)
    {
        try
        {
            if (JsonNode.Parse(project.Configuration) is not JsonObject config) return null;
            config.Remove("name");
            config.Remove("previewAfterBuild");
            config.Remove("previewCommand");
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(config.ToJsonString())));
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private bool HasSuccessfulBuild(ProjectEntry project) =>
        successfulBuilds.TryGetValue(project.Id, out var hash) && hash == BuildFingerprint(project);

    private string BuildReceiptPath(ProjectEntry project) =>
        Path.Combine(Path.GetDirectoryName(projectFile)!, $"built-{project.Id:N}.txt");

    private void RestoreBuildReceipt(ProjectEntry project)
    {
        try
        {
            string path = BuildReceiptPath(project);
            if (File.Exists(path))
                successfulBuilds[project.Id] = File.ReadAllText(path).Trim();
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
            string path = BuildReceiptPath(project);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, hash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WriteHomeLog($"无法保存构建记录：{error.Message}");
        }
    }

    private void InvalidateSuccessfulBuild(ProjectEntry project)
    {
        File.Delete(BuildReceiptPath(project));
        successfulBuilds.Remove(project.Id);
    }

    private static bool HasPreviewCommand(ProjectEntry project) =>
        ReadPreviewOption(project, "previewCommand") is JsonValue value &&
        value.TryGetValue<string>(out var command) && !string.IsNullOrWhiteSpace(command);

    private static bool ShouldAutoOpenPreview(ProjectEntry project) =>
        ReadPreviewOption(project, "previewAfterBuild") is JsonValue value &&
        value.TryGetValue<bool>(out var enabled) && enabled;

    private static JsonNode? ReadPreviewOption(ProjectEntry project, string name)
    {
        try { return (JsonNode.Parse(project.Configuration) as JsonObject)?[name]; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private async Task<bool> TryStartSitePreviewAsync(ProjectEntry project, CancellationToken cancellation)
    {
        Process? server = null;
        try
        {
            var config = JsonNode.Parse(project.Configuration) as JsonObject
                ?? throw new InvalidOperationException("项目配置无效。");
            string Read(string key) => config[key]?.GetValue<string>() ?? "";
            string command = Read("previewCommand");
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("请在本地构建环境中填写预览命令。");

            if (previewServers.Remove(project.Id, out var previous))
            {
                using var previousServer = previous;
                previous.StandardInput.Close();
                await previous.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Close stdin to stop the WSL process group when the window closes or a preview is replaced.
            const string script = """
                if [[ -n $1 ]]; then export PATH="$1:$PATH"; fi
                setsid bash -c "$2" </dev/null &
                server=$!
                ( while IFS= read -r line; do :; done; kill -TERM -- "-$server" 2>/dev/null ) <&3 &
                watcher=$!
                trap 'kill "$watcher" 2>/dev/null || true' EXIT
                wait "$server"
                """;
            var start = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            // Windows source checkouts can give raw string literals CRLF line endings.
            string wrapper = "exec 3<&0; " + script.ReplaceLineEndings("\n");
            foreach (string arg in new[] { "-d", Read("distro"), "-u", Read("wslUser"),
                "--cd", Read("wslRepo"), "--exec", "bash", "-c", wrapper, "projectsync-preview", Read("nodeBin"), command.ReplaceLineEndings("\n") })
                start.ArgumentList.Add(arg);

            server = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WSL 预览。");
            var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Output(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is not { } line) return;
                Dispatcher.BeginInvoke(new Action(() => WriteHomeLog($"[{project.Name} 预览] {line}")));
                var match = Regex.Match(line, @"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):[0-9]+(?:/[^\s\x1b]*)?",
                    RegexOptions.IgnoreCase);
                if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
                    address.TrySetResult(new UriBuilder(uri) { Host = "127.0.0.1" }.Uri);
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
                using var connection = new TcpClient();
                try
                {
                    await connection.ConnectAsync(target.Host, target.Port, deadline.Token);
                    break;
                }
                catch (SocketException) { await Task.Delay(200, deadline.Token); }
            }

            cancellation.ThrowIfCancellationRequested();
            using var browser = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            previewServers[project.Id] = server;
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
                server.StandardInput.Close();
                server.Dispose();
            }
        }
    }
}

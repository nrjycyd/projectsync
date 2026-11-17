using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private readonly Dictionary<Guid, Process> previewServers = new();

    private async Task TryStartSitePreviewAsync(ProjectEntry project)
    {
        Process? server = null;
        try
        {
            var config = JsonNode.Parse(project.Configuration);
            if (config?["previewAfterBuild"]?.GetValue<bool>() != true) return;
            string Read(string key) => config[key]?.GetValue<string>() ?? "";
            string command = Read("previewCommand");
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("请在本地构建环境中填写预览命令。");

            if (previewServers.Remove(project.Id, out var previous))
            {
                using var previousServer = previous;
                previous.StandardInput.Close();
                await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
                "--cd", Read("wslRepo"), "--exec", "bash", "-c", wrapper, "projectsync-preview", Read("nodeBin"), command })
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
            using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            var timeout = Task.Delay(-1, deadline.Token);
            if (await Task.WhenAny(address.Task, exit, timeout) != address.Task)
                throw new InvalidOperationException("预览命令未输出可访问的本地地址。请查看预览日志。");

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

            using var browser = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            previewServers[project.Id] = server;
            server = null;
        }
        catch (Exception error)
        {
            WriteHomeLog($"[{project.Name}] 构建已通过，浏览器预览未启动：{error.Message}");
            HomeResults.SelectedItem = HomeLogTab;
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

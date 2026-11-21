using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectSync.Core;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public string CombinedOutput =>
        string.IsNullOrEmpty(Stderr) ? Stdout
        : string.IsNullOrEmpty(Stdout) ? Stderr
        : Stdout + Environment.NewLine + Stderr;
}

public static class ProcessRunner
{
    public static string WslExecutablePath =>
        Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public static string? FindGitExecutable(string? pathOverride = null)
    {
        string pathEnv = pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir, "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo start,
        TimeSpan? timeout = null,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        string? stdinContent = null,
        bool closeStdin = true,
        CancellationToken cancellation = default)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        if (stdinContent is not null || closeStdin)
            start.RedirectStandardInput = true;

        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"无法启动进程：{start.FileName}");

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        Task stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutBuilder, onStdout, linked.Token);
        Task stderrTask = ReadStreamAsync(process.StandardError, stderrBuilder, onStderr, linked.Token);

        if (stdinContent is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinContent.AsMemory(), linked.Token);
                await process.StandardInput.FlushAsync(linked.Token);
            }
            catch (IOException) { }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { }
            }
        }
        else if (closeStdin)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
        }

        Task waitExit = process.WaitForExitAsync(linked.Token);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, waitExit);
            return new ProcessResult(
                process.ExitCode,
                stdoutBuilder.ToString().TrimEnd('\r', '\n'),
                stderrBuilder.ToString().TrimEnd('\r', '\n'),
                TimedOut: false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            TryKillTree(process);
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }

            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);

            return new ProcessResult(
                ExitCode: -1,
                stdoutBuilder.ToString().TrimEnd('\r', '\n'),
                stderrBuilder.ToString().TrimEnd('\r', '\n'),
                TimedOut: true);
        }
    }

    public static Task<ProcessResult> RunWslAsync(
        IEnumerable<string> arguments,
        Encoding? encoding = null,
        TimeSpan? timeout = null,
        Action<string>? onLine = null,
        string? stdinContent = null,
        bool closeStdin = true,
        CancellationToken cancellation = default)
    {
        var enc = encoding ?? Encoding.Unicode;
        var start = new ProcessStartInfo(WslExecutablePath)
        {
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc
        };
        if (stdinContent is not null)
            start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string arg in arguments)
            start.ArgumentList.Add(arg);
        return RunAsync(start, timeout, onLine, onLine, stdinContent, closeStdin, cancellation);
    }

    public static async Task<ProcessResult> RunWslResizeAsync(
        IEnumerable<string> arguments,
        Action<string>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken cancellation = default)
    {
        var start = new ProcessStartInfo(WslExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in arguments)
            start.ArgumentList.Add(arg);

        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 WSL。");
        try { process.StandardInput.Close(); } catch (IOException) { }

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        var stdoutText = new StringBuilder();
        var stderrText = new StringBuilder();

        Task ReadRaw(Stream stream, StringBuilder sink) => Task.Run(async () =>
        {
            var decoder = new WslOutputParser.IncrementalResizeDecoder();
            var buf = new byte[4096];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buf.AsMemory(), linked.Token)) > 0)
                {
                    decoder.Append(buf.AsSpan(0, read), line =>
                    {
                        sink.AppendLine(line);
                        onLine?.Invoke(line);
                    });
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                decoder.Flush(line =>
                {
                    sink.AppendLine(line);
                    onLine?.Invoke(line);
                });
            }
        });

        Task outTask = ReadRaw(process.StandardOutput.BaseStream, stdoutText);
        Task errTask = ReadRaw(process.StandardError.BaseStream, stderrText);
        Task exitTask = process.WaitForExitAsync(linked.Token);
        try
        {
            await Task.WhenAll(outTask, errTask, exitTask);
            return new ProcessResult(
                process.ExitCode,
                stdoutText.ToString().TrimEnd('\r', '\n'),
                stderrText.ToString().TrimEnd('\r', '\n'),
                TimedOut: false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            TryKillTree(process);
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }

            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);

            return new ProcessResult(
                ExitCode: -1,
                stdoutText.ToString().TrimEnd('\r', '\n'),
                stderrText.ToString().TrimEnd('\r', '\n'),
                TimedOut: true);
        }
    }

    public static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        StringBuilder sink,
        Action<string>? callback,
        CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is { } raw)
            {
                string line = raw.Trim('\0', '\uFEFF', '\r');
                if (line.Length == 0) continue;
                sink.AppendLine(line);
                callback?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProjectSync.App.Views;

public partial class WslManagerWindow
{
    private const string WslReleaseApi = "https://api.github.com/repos/microsoft/WSL/releases/latest";
    private const long MaximumInstallerSize = 1L << 30;
    private static readonly HttpClient WslInstallerClient = new();
    private sealed record WslInstallerAsset(Uri Url, string Sha256, long Size);

    private async void SetupWslClick(object sender, RoutedEventArgs e)
    {
        if (busy || hostReady) return;
        if (FluentDialog.Ask(this, "WSL 安装与修复",
            "将从微软下载并安装或修复 WSL 组件。下载可能达到数百 MB，Windows 会请求管理员授权。现有发行版和文件会保留。组件就绪后可选择发行版，若需要重启，软件会提示。",
            "继续", "取消") != MessageBoxResult.Yes)
            return;

        await RunAsync(SetupWslAsync);
    }

    private async Task SetupWslAsync()
    {
        DirectoryInfo? staging = null;
        string? installer = null;
        try
        {
            Status.ShowFeedback("正在查找微软官方 WSL 安装包…");
            using var download = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var asset = await GetWslInstallerAsync(download.Token);
            staging = Directory.CreateTempSubdirectory("ProjectSync-Wsl-");
            installer = Path.Combine(staging.FullName, "wsl.msi");
            Status.ShowFeedback("正在下载微软官方 WSL 安装包…");
            await DownloadWslInstallerAsync(asset, installer,
                progress => Status.ShowFeedback($"正在下载微软官方 WSL 安装包… {progress}%"), download.Token);

            Status.ShowFeedback("正在验证 WSL 安装包…");
            await using var verifiedFile = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(verifiedFile, download.Token));
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WSL 安装包的 SHA-256 校验失败。");
            await VerifyMicrosoftSignatureAsync(installer);

            bool repair = IsMsiProductInstalled(installer);
            Status.ShowFeedback(repair ? "正在修复 WSL，请确认 Windows 的管理员授权…" : "正在安装 WSL，请确认 Windows 的管理员授权…");
            string[] installArgs = repair
                ? new[] { "/i", installer, "REINSTALL=ALL", "REINSTALLMODE=vomus", "/passive", "/norestart" }
                : new[] { "/i", installer, "/passive", "/norestart" };
            int installCode = await RunElevatedAsync("msiexec.exe", installArgs);
            bool restart = RequiresRestart(installCode, "WSL 安装或修复");
            Status.ShowFeedback("正在启用虚拟机平台…");
            int featureCode = await RunElevatedAsync("dism.exe", "/Online", "/Enable-Feature",
                "/FeatureName:VirtualMachinePlatform", "/All", "/NoRestart");
            restart |= RequiresRestart(featureCode, "启用虚拟机平台");
            if (restart)
            {
                ShowWslRestartRequired();
                return;
            }

            await RefreshAsync();
            if (hostReady)
                Status.ShowFeedback("WSL 已就绪，可以安装或管理发行版。", InfoBarSeverity.Success);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Status.ShowFeedback("已取消管理员授权。可重新尝试安装或刷新检测。");
        }
        catch (OperationCanceledException)
        {
            ReportSetupFailure("下载 WSL 安装包超时，请检查网络后重试。");
        }
        catch (Exception error)
        {
            ReportSetupFailure(error.Message);
        }
        finally
        {
            try
            {
                if (installer is not null) File.Delete(installer);
                staging?.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ProjectSync.App.App.AppendDiagnostic($"无法清理 WSL 安装临时文件：{error.Message}");
            }
        }
    }

    private void ShowWslRestartRequired() =>
        Status.ShowFeedback("请重启 Windows，然后回来点击「刷新检测」。", InfoBarSeverity.Warning);

    private void ReportSetupFailure(string detail)
    {
        ProjectSync.App.App.AppendDiagnostic($"WSL 安装或修复失败：{detail.ReplaceLineEndings(" | ")}");
        Log.Text = detail;
        LogArea.IsExpanded = true;
        Status.ShowFeedback($"WSL 安装或修复未完成。{detail}", InfoBarSeverity.Error);
    }

    private static async Task<WslInstallerAsset> GetWslInstallerAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, WslReleaseApi);
        request.Headers.UserAgent.ParseAdd("ProjectSync");
        using var response = await WslInstallerClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return SelectWslInstaller(json.RootElement, RuntimeInformation.OSArchitecture);
    }

    private static WslInstallerAsset SelectWslInstaller(JsonElement release, Architecture architecture)
    {
        string suffix = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException("此 Windows 架构没有受支持的 WSL 安装包。")
        };
        string? tag = release.GetProperty("tag_name").GetString();
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            tag is null || !Regex.IsMatch(tag, @"\Av?[0-9]+(?:\.[0-9]+){1,3}\z"))
            throw new InvalidOperationException("未找到微软发布的稳定版 WSL 安装包。");

        var assets = release.GetProperty("assets").EnumerateArray().Where(asset =>
            asset.GetProperty("name").GetString() is { } name &&
            Regex.IsMatch(name, @"\Awsl\.[0-9]+(?:\.[0-9]+){1,3}\." + suffix + @"\.msi\z")).ToArray();
        if (assets.Length != 1)
            throw new InvalidOperationException("未找到匹配当前 Windows 架构的 WSL 安装包。");
        var asset = assets[0];
        string name = asset.GetProperty("name").GetString()!;
        string expectedUrl = $"https://github.com/microsoft/WSL/releases/download/{tag}/{name}";
        string? digest = asset.GetProperty("digest").GetString();
        long size = asset.GetProperty("size").GetInt64();
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl ||
            digest is null || !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") ||
            size <= 0 || size > MaximumInstallerSize)
            throw new InvalidOperationException("微软 WSL 发布包的下载信息或校验值无效。");
        return new WslInstallerAsset(new Uri(expectedUrl), digest["sha256:".Length..], size);
    }

    private static async Task DownloadWslInstallerAsync(WslInstallerAsset asset, string path, Action<int> reportProgress,
        CancellationToken token)
    {
        using var response = await WslInstallerClient.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int lastPercent = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            bytes += count;
            if (bytes > asset.Size) throw new InvalidOperationException("WSL 安装包的下载大小超出预期。");
            await target.WriteAsync(buffer.AsMemory(0, count), token);
            int percent = (int)(bytes * 100 / asset.Size);
            if (percent >= lastPercent + 5 || percent == 100)
            {
                lastPercent = percent;
                reportProgress(percent);
            }
        }
        if (bytes != asset.Size) throw new InvalidOperationException("WSL 安装包的下载大小与发布信息不符。");
    }

    private static Task VerifyMicrosoftSignatureAsync(string msi) => Task.Run(() =>
    {
        IntPtr path = Marshal.StringToCoTaskMemUni(msi);
        IntPtr fileInfo = IntPtr.Zero;
        try
        {
            var file = new WinTrustFile { Size = (uint)Marshal.SizeOf<WinTrustFile>(), Path = path };
            fileInfo = Marshal.AllocCoTaskMem((int)file.Size);
            Marshal.StructureToPtr(file, fileInfo, false);
            var trust = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfo,
                ProviderFlags = 0x80 // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref trust);
            if (result != 0)
                throw new InvalidOperationException($"Windows 无法验证 WSL 安装包的数字签名（0x{result:X8}）。");

            using var embedded = X509Certificate.CreateFromSignedFile(msi);
            using var signer = new X509Certificate2(embedded);
            if (!string.Equals(signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                "Microsoft Corporation", StringComparison.Ordinal))
                throw new InvalidOperationException("WSL 安装包的签名发布者不是 Microsoft Corporation。");
        }
        finally
        {
            if (fileInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfo);
            Marshal.FreeCoTaskMem(path);
        }
    });

    private static bool IsMsiProductInstalled(string path)
    {
        const int installed = 5;
        uint result = MsiOpenDatabase(path, IntPtr.Zero, out uint database); // MSIDBOPEN_READONLY
        if (result != 0)
            throw new InvalidOperationException($"Windows Installer 无法读取安装包（代码 {result}）。");
        uint view = 0;
        uint record = 0;
        try
        {
            if (MsiDatabaseOpenView(database, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'", out view) != 0 ||
                MsiViewExecute(view, 0) != 0 || MsiViewFetch(view, out record) != 0)
                throw new InvalidOperationException("无法查询 WSL 安装包的产品标识。");
            var productCode = new StringBuilder(39);
            uint length = (uint)productCode.Capacity;
            result = MsiRecordGetString(record, 1, productCode, ref length);
            if (result != 0 || !Guid.TryParse(productCode.ToString(), out _))
                throw new InvalidOperationException("无法读取 WSL 安装包的产品标识。");
            return MsiQueryProductState(productCode.ToString()) == installed;
        }
        finally
        {
            if (record != 0) MsiCloseHandle(record);
            if (view != 0) MsiCloseHandle(view);
            MsiCloseHandle(database);
        }
    }

    private static async Task<int> RunElevatedAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, executable))
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {executable}。");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool RequiresRestart(int exitCode, string operation) => exitCode switch
    {
        0 => false,
        1641 or 3010 => true,
        1602 => throw new Win32Exception(1223),
        _ => throw new InvalidOperationException($"{operation}失败（Windows 退出代码 {exitCode}）。")
    };

    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);

    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint view, uint record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);

    [DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string productCode);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFile
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}

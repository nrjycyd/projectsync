using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SiteLift.Core;

public sealed record ReleaseAssetInfo(string Name, Uri DownloadUrl, long SizeBytes);

public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    string ReleaseUrl,
    ReleaseAssetInfo? PackageAsset,
    ReleaseAssetInfo? ChecksumAsset)
{
    public bool CanInPlaceUpdate => PackageAsset is not null && ChecksumAsset is not null;
}

public sealed record InstallDirectoryStatus(
    string DirectoryPath,
    bool CanInPlaceUpdate,
    bool RequiresElevation,
    string? BlockedReason);

public enum UpdateStage
{
    DownloadingChecksum,
    DownloadingPackage,
    VerifyingChecksum,
    ExtractingPackage,
    PreparingApply
}

public sealed record UpdateProgress(UpdateStage Stage, long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived * 100.0 / TotalBytes.Value, 0.0, 100.0)
        : null;
}

public sealed record StagedUpdate(
    Version Version,
    string StagingRoot,
    string ExtractedAppDirectory,
    string PackagePath,
    IReadOnlyList<string> RelativeFiles);

public sealed record UpdateOutcome(
    bool Succeeded,
    string TargetVersion,
    string PreviousVersion,
    string? Message);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed record UpdateApplyPlan(
    int OldPid,
    string TargetDir,
    string SourceDir,
    string BackupDir,
    string OutcomePath,
    string LogPath,
    string ExecutableName,
    string TargetVersion,
    string PreviousVersion,
    bool Relaunch,
    bool Elevated,
    IReadOnlyList<string> RelativeFiles);

public static class AppUpdater
{
    public const string ReleasesUrl = "https://github.com/nrjycyd/sitelift/releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/nrjycyd/sitelift/releases/latest";
    public const string ChecksumAssetName = "SHA256SUMS";
    public const string ExecutableName = "SiteLift.App.exe";
    public const string MainAssemblyName = "SiteLift.App.dll";

    public const long MaxPackageBytes = 100L * 1024 * 1024;
    public const long MaxExtractedBytes = 200L * 1024 * 1024;
    public const long MaxChecksumBytes = 64 * 1024;

    private static readonly string[] RequiredPackageFiles =
    {
        "SiteLift.App.exe",
        "SiteLift.App.dll",
        "SiteLift.Core.dll",
        "SiteLift.App.deps.json",
        "SiteLift.App.runtimeconfig.json"
    };

    private static readonly TimeSpan ChunkReadTimeout = TimeSpan.FromSeconds(30);

    public static string PackageAssetName(Version version) =>
        $"SiteLift-v{NormalizeThreePartVersion(version)}-win-x64.zip";

    public static Version NormalizeThreePartVersion(Version version)
    {
        if (version.Major < 0 || version.Minor < 0 || version.Build < 0)
            throw new ArgumentException("版本号必须包含主、次、修订三段。", nameof(version));
        return new Version(version.Major, version.Minor, version.Build);
    }

    public static bool IsTrustedDownloadUrl(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        string host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static ReleaseInfo? ParseLatestRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            return null;

        string tagName = tagElement.GetString()?.Trim() ?? "";
        if (!TryParseReleaseTag(tagName, out var version))
            return null;

        string releaseUrl = ReleasesUrl;
        if (root.TryGetProperty("html_url", out var htmlUrlElement) &&
            htmlUrlElement.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(htmlUrlElement.GetString(), UriKind.Absolute, out var parsedHtmlUrl) &&
            string.Equals(parsedHtmlUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parsedHtmlUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            parsedHtmlUrl.AbsolutePath.StartsWith("/nrjycyd/sitelift/releases", StringComparison.OrdinalIgnoreCase))
        {
            releaseUrl = parsedHtmlUrl.AbsoluteUri;
        }

        string expectedPackage = PackageAssetName(version);
        ReleaseAssetInfo? packageAsset = null;
        ReleaseAssetInfo? checksumAsset = null;

        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                if (!asset.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    continue;
                if (!asset.TryGetProperty("browser_download_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                    continue;

                string name = nameEl.GetString() ?? "";
                if (!Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var downloadUri) || !IsTrustedDownloadUrl(downloadUri))
                    continue;

                long size = 0;
                if (asset.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number && sizeEl.TryGetInt64(out long parsedSize) && parsedSize >= 0)
                    size = parsedSize;

                if (string.Equals(name, expectedPackage, StringComparison.Ordinal))
                    packageAsset = new ReleaseAssetInfo(name, downloadUri, size);
                else if (string.Equals(name, ChecksumAssetName, StringComparison.Ordinal))
                    checksumAsset = new ReleaseAssetInfo(name, downloadUri, size);
            }
        }

        return new ReleaseInfo(version, tagName, releaseUrl, packageAsset, checksumAsset);
    }

    public static bool TryParseReleaseTag(string tagName, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
            return false;

        string trimmed = tagName.Trim();
        if (trimmed.StartsWith('v'))
            trimmed = trimmed[1..];
        if (!Regex.IsMatch(trimmed, @"\A\d+\.\d+\.\d+\z"))
            return false;

        if (!Version.TryParse(trimmed, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0)
            return false;

        version = parsed;
        return true;
    }

    public static string? ParseExpectedSha256(string checksumContent, string assetFileName)
    {
        if (string.IsNullOrWhiteSpace(checksumContent) || string.IsNullOrWhiteSpace(assetFileName))
            return null;

        using var reader = new StringReader(checksumContent);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var match = Regex.Match(trimmed, @"\A([0-9a-fA-F]{64})\s+\*?(.+)\z");
            if (!match.Success)
                continue;

            string fileName = match.Groups[2].Value.Trim();
            if (string.Equals(fileName, assetFileName, StringComparison.Ordinal))
                return match.Groups[1].Value.ToLowerInvariant();
        }

        return null;
    }

    public static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyFileSha256(string filePath, string expectedHex)
    {
        if (!Regex.IsMatch(expectedHex, @"\A[0-9a-fA-F]{64}\z"))
            return false;

        string actualHex = ComputeFileSha256(filePath);
        byte[] actualBytes = Encoding.ASCII.GetBytes(actualHex);
        byte[] expectedBytes = Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    public static InstallDirectoryStatus InspectInstallDirectory(string baseDirectory)
    {
        string normalized = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(normalized) || !File.Exists(Path.Combine(normalized, ExecutableName)))
        {
            return new InstallDirectoryStatus(
                normalized,
                CanInPlaceUpdate: false,
                RequiresElevation: false,
                BlockedReason: $"当前运行目录缺少 {ExecutableName}，无法直接原位更新。");
        }

        if (IsDevelopmentBuildDirectory(normalized))
        {
            return new InstallDirectoryStatus(
                normalized,
                CanInPlaceUpdate: false,
                RequiresElevation: false,
                BlockedReason: "当前运行在本地开发构建目录中，不支持原地覆盖更新。");
        }

        string probeFile = Path.Combine(normalized, $".sitelift-update-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probeFile)) { }
            File.Delete(probeFile);
            return new InstallDirectoryStatus(normalized, CanInPlaceUpdate: true, RequiresElevation: false, BlockedReason: null);
        }
        catch (UnauthorizedAccessException)
        {
            return new InstallDirectoryStatus(normalized, CanInPlaceUpdate: true, RequiresElevation: true, BlockedReason: null);
        }
        catch (IOException error)
        {
            return new InstallDirectoryStatus(normalized, CanInPlaceUpdate: false, RequiresElevation: false, BlockedReason: $"无法写入当前程序目录：{error.Message}");
        }
        finally
        {
            TryDeleteFile(probeFile);
        }
    }

    private static bool IsDevelopmentBuildDirectory(string directory)
    {
        if (File.Exists(Path.Combine(directory, "SiteLift.App.pdb")))
            return true;

        string normalized = directory.Replace('/', '\\');
        bool looksLikeBinOutput = normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\bin\Debug", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(@"\bin\Release", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeBinOutput)
            return false;

        var cursor = new DirectoryInfo(directory);
        for (int depth = 0; depth < 6 && cursor is not null; depth++, cursor = cursor.Parent)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "SiteLift.App.csproj")) ||
                File.Exists(Path.Combine(cursor.FullName, "SiteLift.sln")))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<StagedUpdate> DownloadAndStageAsync(
        HttpClient httpClient,
        ReleaseInfo release,
        string updatesRootDirectory,
        Version currentVersion,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.PackageAsset is null || release.ChecksumAsset is null)
            throw new UpdateException("该版本缺少可用于自动更新的压缩包或校验文件。");

        if (release.PackageAsset.SizeBytes > MaxPackageBytes)
            throw new UpdateException("更新包体积超出安全上限，已取消下载。");

        Directory.CreateDirectory(updatesRootDirectory);
        string stagingRoot = Path.Combine(updatesRootDirectory, $"v{release.Version.ToString(3)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        bool succeeded = false;
        try
        {
            string userAgent = $"SiteLift/{NormalizeThreePartVersion(currentVersion)}";

            progress?.Report(new UpdateProgress(UpdateStage.DownloadingChecksum, 0, release.ChecksumAsset.SizeBytes > 0 ? release.ChecksumAsset.SizeBytes : null));
            string checksumContent = await DownloadSmallTextAsync(httpClient, release.ChecksumAsset.DownloadUrl, userAgent, MaxChecksumBytes, cancellationToken);
            string? expectedSha256 = ParseExpectedSha256(checksumContent, release.PackageAsset.Name);
            if (expectedSha256 is null)
                throw new UpdateException($"校验文件 {ChecksumAssetName} 中未找到 {release.PackageAsset.Name} 的有效 SHA-256 记录。");

            string packagePath = Path.Combine(stagingRoot, release.PackageAsset.Name);
            await DownloadFileAsync(httpClient, release.PackageAsset.DownloadUrl, userAgent, packagePath, release.PackageAsset.SizeBytes, progress, cancellationToken);

            progress?.Report(new UpdateProgress(UpdateStage.VerifyingChecksum, 0, null));
            bool hashOk = await Task.Run(() => VerifyFileSha256(packagePath, expectedSha256), cancellationToken);
            if (!hashOk)
                throw new UpdateException("更新包 SHA-256 校验失败，文件可能已损坏或下载不完整。");

            progress?.Report(new UpdateProgress(UpdateStage.ExtractingPackage, 0, null));
            string extractRoot = Path.Combine(stagingRoot, "extracted");
            var staged = await Task.Run(() => ExtractAndVerifyPackage(packagePath, stagingRoot, extractRoot, release.Version), cancellationToken);

            succeeded = true;
            return staged;
        }
        finally
        {
            if (!succeeded)
                TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task<string> DownloadSmallTextAsync(
        HttpClient httpClient,
        Uri url,
        string userAgent,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await SendWithInactivityTimeoutAsync(httpClient, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new UpdateException("校验文件体积异常，已中止更新。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            int read = await ReadChunkWithTimeoutAsync(stream, buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new UpdateException("校验文件体积异常，已中止更新。");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task DownloadFileAsync(
        HttpClient httpClient,
        Uri url,
        string userAgent,
        string destinationPath,
        long fallbackTotalBytes,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent);
        using var response = await SendWithInactivityTimeoutAsync(httpClient, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength
            ?? (fallbackTotalBytes > 0 ? fallbackTotalBytes : null);
        if (totalBytes is > MaxPackageBytes)
            throw new UpdateException("更新包体积超出安全上限，已中止下载。");

        progress?.Report(new UpdateProgress(UpdateStage.DownloadingPackage, 0, totalBytes));

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        byte[] buffer = new byte[65536];
        long received = 0;
        var reportTimer = Stopwatch.StartNew();

        while (true)
        {
            int read = await ReadChunkWithTimeoutAsync(source, buffer, cancellationToken);
            if (read == 0)
                break;

            received += read;
            if (received > MaxPackageBytes)
                throw new UpdateException("更新包体积超出安全上限，已中止下载。");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (reportTimer.ElapsedMilliseconds >= 80 || (totalBytes is > 0 && received >= totalBytes.Value))
            {
                progress?.Report(new UpdateProgress(UpdateStage.DownloadingPackage, received, totalBytes));
                reportTimer.Restart();
            }
        }

        progress?.Report(new UpdateProgress(UpdateStage.DownloadingPackage, received, totalBytes ?? received));
    }

    private static async Task<HttpResponseMessage> SendWithInactivityTimeoutAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ChunkReadTimeout);
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接下载服务器超时，请检查网络或改用浏览器下载。");
        }
    }

    private static async Task<int> ReadChunkWithTimeoutAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ChunkReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("下载连接长时间无响应，请重试或改用浏览器下载。");
        }
    }

    internal static StagedUpdate ExtractAndVerifyPackage(
        string zipPath,
        string stagingRoot,
        string extractRoot,
        Version expectedVersion,
        Func<string, Version?>? readAssemblyVersion = null)
    {
        Directory.CreateDirectory(extractRoot);
        string fullExtractRoot = Path.GetFullPath(extractRoot);
        string appDir = Path.Combine(fullExtractRoot, "SiteLift");
        string appDirPrefix = appDir + Path.DirectorySeparatorChar;

        var relativeFiles = new List<string>();
        long totalExtracted = 0;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            if (archive.Entries.Count == 0)
                throw new UpdateException("更新压缩包为空。");

            foreach (var entry in archive.Entries)
            {
                if (IsUnixSymbolicLink(entry))
                    throw new UpdateException("更新压缩包包含不受支持的符号链接条目。");

                string rawName = entry.FullName;
                if (string.IsNullOrEmpty(rawName))
                    continue;

                bool isDirectoryEntry = rawName.EndsWith('/') || rawName.EndsWith('\\');
                string[] segments = rawName.TrimEnd('/', '\\').Split(new[] { '/', '\\' }, StringSplitOptions.None);
                if (segments.Length == 0 || !string.Equals(segments[0], "SiteLift", StringComparison.Ordinal))
                    throw new UpdateException("更新压缩包目录结构异常（缺少 SiteLift 根目录）。");

                foreach (string segment in segments)
                {
                    if (segment.Length == 0 || segment == "." || segment == ".." ||
                        segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        throw new UpdateException($"更新压缩包包含非法路径：{rawName}");
                    }
                }

                if (isDirectoryEntry)
                {
                    if (segments.Length > 1)
                    {
                        string dirPath = Path.GetFullPath(Path.Combine(new[] { fullExtractRoot }.Concat(segments).ToArray()));
                        if (!dirPath.StartsWith(appDirPrefix, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(dirPath, appDir, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new UpdateException($"更新压缩包包含越界目录：{rawName}");
                        }
                        Directory.CreateDirectory(dirPath);
                    }
                    continue;
                }

                if (segments.Length < 2)
                    throw new UpdateException($"更新压缩包包含非法文件条目：{rawName}");

                string targetPath = Path.GetFullPath(Path.Combine(new[] { fullExtractRoot }.Concat(segments).ToArray()));
                if (!targetPath.StartsWith(appDirPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException($"更新压缩包包含越界路径：{rawName}");

                string? parentDir = Path.GetDirectoryName(targetPath);
                if (parentDir is not null)
                    Directory.CreateDirectory(parentDir);

                using (var entryStream = entry.Open())
                using (var outputStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        totalExtracted += read;
                        if (totalExtracted > MaxExtractedBytes)
                            throw new UpdateException("更新包解压后体积超出安全上限。");
                        outputStream.Write(buffer, 0, read);
                    }
                }

                string relativePath = string.Join('/', segments.Skip(1));
                relativeFiles.Add(relativePath);
            }
        }

        if (!Directory.Exists(appDir))
            throw new UpdateException("更新压缩包中缺少 SiteLift 程序目录。");

        var fileSet = new HashSet<string>(relativeFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string required in RequiredPackageFiles)
        {
            if (!fileSet.Contains(required))
                throw new UpdateException($"更新压缩包缺少必要文件：{required}");
        }

        string dllPath = Path.Combine(appDir, MainAssemblyName);
        Version? assemblyVersion = readAssemblyVersion is not null
            ? readAssemblyVersion(dllPath)
            : ReadAssemblyVersion(dllPath);

        if (assemblyVersion is null || assemblyVersion.Build < 0 ||
            !string.Equals(NormalizeThreePartVersion(assemblyVersion).ToString(3), NormalizeThreePartVersion(expectedVersion).ToString(3), StringComparison.Ordinal))
        {
            throw new UpdateException($"更新包程序集版本与目标版本 v{NormalizeThreePartVersion(expectedVersion)} 不一致。");
        }

        relativeFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return new StagedUpdate(NormalizeThreePartVersion(expectedVersion), stagingRoot, appDir, zipPath, relativeFiles);
    }

    private static Version? ReadAssemblyVersion(string assemblyPath)
    {
        try
        {
            return AssemblyName.GetAssemblyName(assemblyPath).Version;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsUnixSymbolicLink(ZipArchiveEntry entry)
    {
        int unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    public static (string ScriptPath, string PlanPath) PrepareApplyScript(
        StagedUpdate staged,
        string targetDirectory,
        string updatesRootDirectory,
        string diagnosticsDirectory,
        int currentProcessId,
        Version currentVersion,
        bool elevated,
        bool relaunch = true)
    {
        Directory.CreateDirectory(updatesRootDirectory);
        Directory.CreateDirectory(diagnosticsDirectory);

        var plan = new UpdateApplyPlan(
            OldPid: currentProcessId,
            TargetDir: Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            SourceDir: Path.GetFullPath(staged.ExtractedAppDirectory),
            BackupDir: Path.Combine(staged.StagingRoot, "backup"),
            OutcomePath: Path.Combine(updatesRootDirectory, "outcome.json"),
            LogPath: Path.Combine(diagnosticsDirectory, "update-apply.log"),
            ExecutableName: ExecutableName,
            TargetVersion: staged.Version.ToString(3),
            PreviousVersion: NormalizeThreePartVersion(currentVersion).ToString(3),
            Relaunch: relaunch,
            Elevated: elevated,
            RelativeFiles: staged.RelativeFiles);

        string planPath = Path.Combine(staged.StagingRoot, "plan.json");
        string scriptPath = Path.Combine(staged.StagingRoot, "apply-update.ps1");

        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.WriteAllText(scriptPath, ApplyScriptTemplate, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return (scriptPath, planPath);
    }

    public static ProcessStartInfo BuildUpdaterStartInfo(string scriptPath, string planPath, bool elevated)
    {
        string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string powershell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            powershell = "powershell.exe";

        if (elevated)
        {
            return new ProcessStartInfo(powershell)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File {QuoteWindowsArgument(scriptPath)} -PlanPath {QuoteWindowsArgument(planPath)}"
            };
        }

        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-PlanPath");
        startInfo.ArgumentList.Add(planPath);
        return startInfo;
    }

    public static void LaunchUpdater(string scriptPath, string planPath, bool elevated)
    {
        var startInfo = BuildUpdaterStartInfo(scriptPath, planPath, elevated);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new UpdateException("无法启动更新进程。");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员权限授权，未应用更新。", error);
        }
        catch (Win32Exception error)
        {
            throw new UpdateException($"无法启动更新进程：{error.Message}", error);
        }
    }

    public static UpdateOutcome? ConsumeUpdateOutcomeAndCleanup(string updatesRootDirectory)
    {
        if (!Directory.Exists(updatesRootDirectory))
            return null;

        UpdateOutcome? outcome = null;
        string outcomePath = Path.Combine(updatesRootDirectory, "outcome.json");
        if (File.Exists(outcomePath))
        {
            try
            {
                string json = File.ReadAllText(outcomePath, Encoding.UTF8);
                outcome = JsonSerializer.Deserialize<UpdateOutcome>(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
            TryDeleteFile(outcomePath);
        }

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(updatesRootDirectory, "v*"))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return outcome;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024L * 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
                backslashes = 0;
            }
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const string ApplyScriptTemplate = """
param(
    [Parameter(Mandatory)]
    [string]$PlanPath
)

$ErrorActionPreference = 'Stop'

function Write-UpdateLog {
    param([string]$LogPath, [string]$Message)
    try {
        $dir = Split-Path -Parent $LogPath
        if ($dir -and !(Test-Path -LiteralPath $dir)) {
            [IO.Directory]::CreateDirectory($dir) | Out-Null
        }
        $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
        [IO.File]::AppendAllText($LogPath, "[$stamp] $Message`r`n", [Text.UTF8Encoding]::new($false))
    } catch { }
}

function Write-UpdateOutcome {
    param([string]$OutcomePath, [bool]$Succeeded, [string]$TargetVersion, [string]$PreviousVersion, [string]$Message)
    try {
        $dir = Split-Path -Parent $OutcomePath
        if ($dir -and !(Test-Path -LiteralPath $dir)) {
            [IO.Directory]::CreateDirectory($dir) | Out-Null
        }
        $payload = @{
            Succeeded = $Succeeded
            TargetVersion = $TargetVersion
            PreviousVersion = $PreviousVersion
            Message = $Message
        } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($OutcomePath, $payload, [Text.UTF8Encoding]::new($false))
    } catch { }
}

function Copy-WithRetry {
    param([string]$Source, [string]$Destination, [int]$Attempts = 12, [int]$DelayMs = 250)
    $parent = Split-Path -Parent $Destination
    if ($parent -and !(Test-Path -LiteralPath $parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            [IO.File]::Copy($Source, $Destination, $true)
            return
        } catch {
            if ($i -eq $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
}

function Start-SiteLiftApp {
    param([string]$TargetDir, [string]$ExecutableName, [bool]$Elevated)
    $exePath = Join-Path $TargetDir $ExecutableName
    if (!(Test-Path -LiteralPath $exePath)) { return }
    try {
        if ($Elevated) {
            Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $exePath + '"') | Out-Null
        } else {
            Start-Process -FilePath $exePath -WorkingDirectory $TargetDir | Out-Null
        }
    } catch { }
}

$plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
$logPath = [string]$plan.LogPath
$outcomePath = [string]$plan.OutcomePath
$targetDir = [string]$plan.TargetDir
$sourceDir = [string]$plan.SourceDir
$backupDir = [string]$plan.BackupDir
$targetVersion = [string]$plan.TargetVersion
$previousVersion = [string]$plan.PreviousVersion
$oldPid = [int]$plan.OldPid
$relaunch = [bool]$plan.Relaunch
$elevated = [bool]$plan.Elevated

Write-UpdateLog $logPath "Start applying update v$previousVersion -> v$targetVersion (OldPid=$oldPid, Elevated=$elevated)"

$exited = ($oldPid -le 0)
if (-not $exited) {
    for ($i = 0; $i -lt 60; $i++) {
        $proc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
        if ($null -eq $proc) {
            $exited = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
}

if (-not $exited) {
    $msg = "旧版本进程 (PID $oldPid) 未在规定时间内退出，已中止更新。"
    Write-UpdateLog $logPath $msg
    Write-UpdateOutcome $outcomePath $false $targetVersion $previousVersion $msg
    exit 1
}

if ($oldPid -gt 0) {
    Start-Sleep -Milliseconds 350
}

$backedUp = [System.Collections.Generic.List[string]]::new()
$createdNew = [System.Collections.Generic.List[string]]::new()

try {
    if (!(Test-Path -LiteralPath $sourceDir)) { throw "找不到解压后的更新源目录。" }
    if (!(Test-Path -LiteralPath $targetDir)) { throw "找不到目标安装目录。" }

    foreach ($rel in @($plan.RelativeFiles)) {
        $normalizedRel = ([string]$rel).Replace('/', '\')
        $src = Join-Path $sourceDir $normalizedRel
        $dst = Join-Path $targetDir $normalizedRel
        $bak = Join-Path $backupDir $normalizedRel

        if (Test-Path -LiteralPath $dst) {
            Copy-WithRetry -Source $dst -Destination $bak
            $backedUp.Add($normalizedRel)
        } else {
            $createdNew.Add($normalizedRel)
        }

        Copy-WithRetry -Source $src -Destination $dst
    }

    Write-UpdateOutcome $outcomePath $true $targetVersion $previousVersion $null
    Write-UpdateLog $logPath "Update applied successfully."

    try {
        if (Test-Path -LiteralPath $backupDir) {
            Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } catch { }

    if ($relaunch) {
        Start-SiteLiftApp -TargetDir $targetDir -ExecutableName ([string]$plan.ExecutableName) -Elevated $elevated
    }
    exit 0
} catch {
    $errMsg = $_.Exception.Message
    Write-UpdateLog $logPath "Update failed: $errMsg. Rolling back..."

    foreach ($rel in $backedUp) {
        try {
            $bak = Join-Path $backupDir $rel
            $dst = Join-Path $targetDir $rel
            if (Test-Path -LiteralPath $bak) {
                Copy-WithRetry -Source $bak -Destination $dst -Attempts 6 -DelayMs 200
            }
        } catch {
            Write-UpdateLog $logPath "Rollback failed for $rel : $($_.Exception.Message)"
        }
    }

    foreach ($rel in $createdNew) {
        try {
            $dst = Join-Path $targetDir $rel
            if (Test-Path -LiteralPath $dst) {
                Remove-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }

    Write-UpdateOutcome $outcomePath $false $targetVersion $previousVersion $errMsg
    if ($relaunch) {
        Start-SiteLiftApp -TargetDir $targetDir -ExecutableName ([string]$plan.ExecutableName) -Elevated $elevated
    }
    exit 1
}
""";
}

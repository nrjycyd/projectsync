using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectSync.Core;

public sealed record PendingPushRecord(
    string BaseCommit,
    string VerifiedSnapshot,
    string PendingCommit,
    string Repository = "",
    string Branch = "")
{
    public string ToToken() => $"{BaseCommit}:{VerifiedSnapshot}:{PendingCommit}";

    public static PendingPushRecord? TryParseToken(string token, string repository = "", string branch = "")
    {
        var match = Regex.Match(token.Trim(), @"\A([0-9a-f]{40}):([0-9a-f]{40}):([0-9a-f]{40})\z");
        if (!match.Success) return null;
        return new PendingPushRecord(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            repository,
            branch);
    }
}

public sealed record AppPreferences(string Theme = "Light");

public sealed class AppDataStore
{
    public const string DataDirectoryName = "ProjectSync";
    public const string LegacyDataDirectoryName = "ProjectSync-WpfUiTrial";

    private readonly object logLock = new();
    private DateOnly lastPrunedDate;

    public string RootDirectory { get; }
    public string DiagnosticsDirectory => Path.Combine(RootDirectory, "logs");
    public string ProjectsFilePath => Path.Combine(RootDirectory, "projects.json");
    public string ChoicesFilePath => Path.Combine(RootDirectory, "detected-options.json");
    public string PreferencesFilePath => Path.Combine(RootDirectory, "settings.json");

    public AppDataStore(string? localAppDataRoot = null)
    {
        string baseRoot = localAppDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RootDirectory = Path.Combine(baseRoot, DataDirectoryName);
    }

    public static bool MigrateLegacyDirectoryIfNeeded(string? localAppDataRoot = null)
    {
        string baseRoot = localAppDataRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string legacyDir = Path.Combine(baseRoot, LegacyDataDirectoryName);
        string targetDir = Path.Combine(baseRoot, DataDirectoryName);

        if (!Directory.Exists(legacyDir))
            return false;

        if (!Directory.Exists(targetDir))
        {
            try
            {
                Directory.Move(legacyDir, targetDir);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall back to copying files if a lock prevents moving the directory atomically.
                Directory.CreateDirectory(targetDir);
                CopyMissingTree(legacyDir, targetDir);
                return true;
            }
        }

        // If the new directory already exists without projects.json (e.g. only logs created),
        // copy missing data files without overwriting newer state.
        string newProjects = Path.Combine(targetDir, "projects.json");
        string oldProjects = Path.Combine(legacyDir, "projects.json");
        if (!File.Exists(newProjects) && File.Exists(oldProjects))
        {
            CopyMissingTree(legacyDir, targetDir);
            return true;
        }

        return false;
    }

    private static void CopyMissingTree(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string destination = Path.Combine(targetDir, Path.GetFileName(file));
            if (File.Exists(destination)) continue;
            try { File.Copy(file, destination, overwrite: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (string dir in Directory.EnumerateDirectories(sourceDir))
        {
            string destination = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyMissingTree(dir, destination);
        }
    }

    public static void WriteTextAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("无效的文件路径。", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public string PendingPushPath(Guid projectId) =>
        Path.Combine(RootDirectory, $"pending-{projectId:N}.json");

    public string PreviewStatePath(Guid projectId) =>
        Path.Combine(RootDirectory, $"preview-{projectId:N}.json");

    public string BuildReceiptPath(Guid projectId) =>
        Path.Combine(RootDirectory, $"built-{projectId:N}.txt");

    public void SavePendingPush(Guid projectId, PendingPushRecord record)
    {
        WriteTextAtomically(PendingPushPath(projectId), JsonSerializer.Serialize(record));
        DeleteLegacyPendingFiles(projectId);
    }

    public PendingPushRecord? LoadPendingPush(Guid projectId)
    {
        string path = PendingPushPath(projectId);
        if (File.Exists(path))
        {
            var record = JsonSerializer.Deserialize<PendingPushRecord>(File.ReadAllText(path));
            if (record is not null && PendingPushRecord.TryParseToken(record.ToToken()) is not null)
                return record;
        }

        if (!Directory.Exists(RootDirectory))
            return null;

        // Migrate any legacy pending-{projectId}-{configHash}.txt from v0.2.x.
        string prefix = $"pending-{projectId:N}-";
        string? legacy = Directory.EnumerateFiles(RootDirectory, $"{prefix}*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (legacy is null)
            return null;

        string token = File.ReadAllText(legacy).Trim();
        var migrated = PendingPushRecord.TryParseToken(token);
        if (migrated is not null)
        {
            try { SavePendingPush(projectId, migrated); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return migrated;
    }

    public void DeletePendingPush(Guid projectId)
    {
        TryDeleteFile(PendingPushPath(projectId));
        DeleteLegacyPendingFiles(projectId);
    }

    private void DeleteLegacyPendingFiles(Guid projectId)
    {
        if (!Directory.Exists(RootDirectory)) return;
        foreach (string file in Directory.EnumerateFiles(RootDirectory, $"pending-{projectId:N}-*.txt"))
            TryDeleteFile(file);
    }

    public void SaveBuildReceipt(Guid projectId, string fingerprint) =>
        WriteTextAtomically(BuildReceiptPath(projectId), fingerprint);

    public string? LoadBuildReceipt(Guid projectId)
    {
        string path = BuildReceiptPath(projectId);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public void DeleteBuildReceipt(Guid projectId) =>
        TryDeleteFile(BuildReceiptPath(projectId));

    public void DeletePreviewState(Guid projectId) =>
        TryDeleteFile(PreviewStatePath(projectId));

    public void DeleteAllProjectState(Guid projectId)
    {
        DeletePreviewState(projectId);
        DeleteBuildReceipt(projectId);
        DeletePendingPush(projectId);
    }

    public AppPreferences LoadPreferences()
    {
        try
        {
            if (File.Exists(PreferencesFilePath) &&
                JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PreferencesFilePath)) is { } prefs &&
                prefs.Theme is "System" or "Light" or "Dark")
                return prefs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new AppPreferences();
    }

    public void SavePreferences(AppPreferences preferences)
    {
        try { WriteTextAtomically(PreferencesFilePath, JsonSerializer.Serialize(preferences)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void AppendDiagnostic(string message, int retentionDays = 14)
    {
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(DiagnosticsDirectory);
                var now = DateTimeOffset.Now;
                var today = DateOnly.FromDateTime(now.Date);
                if (lastPrunedDate != today)
                {
                    lastPrunedDate = today;
                    PruneOldLogs(today.AddDays(-retentionDays));
                }
                File.AppendAllText(
                    Path.Combine(DiagnosticsDirectory, $"projectsync-{now:yyyy-MM-dd}.log"),
                    $"{now:HH:mm:ss.fff zzz} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void PruneOldLogs(DateOnly cutoff)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(DiagnosticsDirectory, "projectsync-*.log"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length == "projectsync-yyyy-MM-dd".Length &&
                    DateOnly.TryParseExact(stem["projectsync-".Length..], "yyyy-MM-dd", out var fileDate) &&
                    fileDate < cutoff)
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ProjectSync.Core;

public enum CopyAction { CreateDirectory, Copy, Replace, SkipExisting, SkipDownloadMark }

public sealed record CopyEntry(string Source, string Destination, string RelativePath, CopyAction Action,
    long Length = 0, DateTime LastWrite = default, long? DestinationLength = null, DateTime? DestinationLastWrite = null);

public static class FileCopy
{
    public static string ResolvePath(string text, string? distribution)
    {
        string path = text.Trim().Trim('"');
        if (distribution is null)
        {
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
                path[2] is not ('\\' or '/') || path[2..].Contains(':'))
                throw new ArgumentException("请输入 Windows 磁盘上的绝对路径，例如 D:\\资料。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        if (distribution.Length == 0 || distribution.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("请选择有效的 WSL 发行版。");
        if (!path.StartsWith('/'))
            throw new ArgumentException("请输入 WSL 内的绝对路径，例如 /home/blog/edusprout。");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"此路径片段无法通过 Windows 访问：{part}");
        }
        var share = $@"\\wsl.localhost\{distribution}";
        return parts.Length == 0 ? share : Path.Combine(share, Path.Combine(parts));
    }

    public static IReadOnlyList<CopyEntry> Preview(string source, string target, bool replace, bool skipDownloadMark,
        CancellationToken cancellation = default)
    {
        CheckAncestors(source);
        CheckAncestors(target);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException("目标目录不存在或不可访问，请先选择一个现有目录。");
        FileAttributes sourceAttributes = File.GetAttributes(source);
        string name = Path.GetFileName(source);
        if (name.Length == 0)
            throw new IOException("请选择根目录下的文件或子目录。");
        string destination = Path.Combine(target, name);
        if (SameOrInside(destination, source))
            throw new IOException("目标与来源相同，或位于来源目录内。");

        var entries = new List<CopyEntry>();
        var pending = new Stack<(string Source, string Destination, string Relative, FileAttributes Attributes)>();
        pending.Push((source, destination, name, sourceAttributes));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            EnsureOrdinary(item.Source, item.Attributes);
            if (skipDownloadMark && IsDownloadMark(Path.GetFileName(item.Source)) &&
                !item.Attributes.HasFlag(FileAttributes.Directory))
            {
                entries.Add(new(item.Source, item.Destination, item.Relative, CopyAction.SkipDownloadMark));
                continue;
            }

            var existing = ReadAttributes(item.Destination);
            if (existing.HasValue) EnsureOrdinary(item.Destination, existing.Value);
            if (item.Attributes.HasFlag(FileAttributes.Directory))
            {
                if (existing.HasValue && !existing.Value.HasFlag(FileAttributes.Directory))
                    throw new IOException($"目标已有同名文件：{item.Destination}");
                if (!existing.HasValue)
                    entries.Add(new(item.Source, item.Destination, item.Relative, CopyAction.CreateDirectory));

                var children = Directory.GetFileSystemEntries(item.Source);
                Array.Sort(children, StringComparer.Ordinal);
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    string child = children[i];
                    string childName = Path.GetFileName(child);
                    pending.Push((child, Path.Combine(item.Destination, childName),
                        Path.Combine(item.Relative, childName), File.GetAttributes(child)));
                }
                continue;
            }

            if (existing?.HasFlag(FileAttributes.Directory) == true)
                throw new IOException($"目标已有同名目录：{item.Destination}");
            var info = new FileInfo(item.Source);
            var destinationInfo = existing.HasValue ? new FileInfo(item.Destination) : null;
            var action = !existing.HasValue ? CopyAction.Copy : replace ? CopyAction.Replace : CopyAction.SkipExisting;
            entries.Add(new(item.Source, item.Destination, item.Relative, action, info.Length, info.LastWriteTimeUtc,
                destinationInfo?.Length, destinationInfo?.LastWriteTimeUtc));
        }
        return entries;
    }

    public static void Apply(IReadOnlyList<CopyEntry> entries, bool skipDownloadMark, Action<CopyEntry> completed,
        CancellationToken cancellation = default)
    {
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Action is CopyAction.SkipExisting or CopyAction.SkipDownloadMark)
                continue;
            CheckAncestors(entry.Source);
            CheckAncestors(entry.Destination);
            if (entry.Action == CopyAction.CreateDirectory)
            {
                Directory.CreateDirectory(entry.Destination);
                completed(entry);
                continue;
            }
            if (!Matches(entry.Source, entry.Length, entry.LastWrite) ||
                !Matches(entry.Destination, entry.DestinationLength, entry.DestinationLastWrite))
                throw new IOException($"来源或目标已变化，请重新预览：{entry.RelativePath}");

            var temporary = Path.Combine(Path.GetDirectoryName(entry.Destination)!, $".projectsync-copy-{Guid.NewGuid():N}.tmp");
            bool ownsTemporary = false;
            try
            {
                if (skipDownloadMark)
                {
                    using var input = File.OpenRead(entry.Source);
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    ownsTemporary = true;
                    input.CopyToAsync(output, cancellation).GetAwaiter().GetResult();
                }
                else
                {
                    using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    ownsTemporary = true;
                    File.Copy(entry.Source, temporary, overwrite: true);
                }
                File.SetLastWriteTimeUtc(temporary, entry.LastWrite);
                cancellation.ThrowIfCancellationRequested();
                if (!Matches(entry.Source, entry.Length, entry.LastWrite) ||
                    !Matches(entry.Destination, entry.DestinationLength, entry.DestinationLastWrite))
                    throw new IOException($"复制期间文件已变化，请重新预览：{entry.RelativePath}");
                File.Move(temporary, entry.Destination, overwrite: entry.Action == CopyAction.Replace);
                ownsTemporary = false;
                completed(entry);
            }
            finally
            {
                if (ownsTemporary) File.Delete(temporary);
            }
        }
    }

    private static bool Matches(string path, long? length, DateTime? lastWrite)
    {
        var attributes = ReadAttributes(path);
        if (!length.HasValue) return !attributes.HasValue;
        if (!attributes.HasValue || attributes.Value.HasFlag(FileAttributes.Directory)) return false;
        EnsureOrdinary(path, attributes.Value);
        var info = new FileInfo(path);
        return info.Length == length && info.LastWriteTimeUtc == lastWrite;
    }

    private static bool IsDownloadMark(string name) =>
        name.EndsWith(":Zone.Identifier", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".Zone.Identifier", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("\uF03AZone.Identifier", StringComparison.OrdinalIgnoreCase);

    private static bool SameOrInside(string path, string ancestor) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(ancestor.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static void CheckAncestors(string path)
    {
        string? current = path;
        while (current is not null)
        {
            if (ReadAttributes(current) is FileAttributes attributes)
                EnsureOrdinary(current, attributes);
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureOrdinary(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"无法复制或穿过符号链接、连接点：{path}");
    }
}

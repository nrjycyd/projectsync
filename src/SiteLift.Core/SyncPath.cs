using System;
using System.Text.RegularExpressions;

namespace SiteLift.Core;

public static class SyncPath
{
    public static string Normalize(string input, string label, string windowsRoot, string wslRoot, string distribution)
    {
        string path = UnquotePath(input).Replace('\\', '/').TrimEnd('/');
        string? root = null;
        var comparison = StringComparison.Ordinal;
        if (TryParseWslNetworkPath(path, out var linuxPath, out var pathDistro))
        {
            string configuredDistro = TryParseWslNetworkPath(UnquotePath(wslRoot), out _, out var rootDistro)
                ? rootDistro : distribution.Trim();
            if (!string.Equals(pathDistro, configuredDistro, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{label}的 WSL 发行版与当前配置不一致：{input}");
            path = linuxPath;
            root = wslRoot;
        }
        else if (Regex.IsMatch(path, @"^[A-Za-z]:/"))
        {
            root = windowsRoot;
            comparison = StringComparison.OrdinalIgnoreCase;
        }
        else if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
        {
            root = wslRoot;
        }

        if (root is not null)
        {
            root = UnquotePath(root).Replace('\\', '/');
            if (TryParseWslNetworkPath(root, out var rootPath, out _)) root = rootPath;
            string prefix = root.TrimEnd('/') + "/";
            if (root.Length == 0 || !path.StartsWith(prefix, comparison))
                throw new ArgumentException($"{label}必须位于当前项目目录内：{input}");
            path = path[prefix.Length..];
        }

        if (path.Length == 0 || Regex.IsMatch(path,
            @"(^/|//|[""<>|:*?\[\]\x00-\x1f]|(^|/)(\.{1,2}|(?i:\.git))(/|$))"))
            throw new ArgumentException($"{label}包含无效路径：{input}");
        return path;
    }

    public static string ValidateSuffix(string input)
    {
        string suffix = input.Trim();
        if (suffix.Length == 0)
            throw new ArgumentException("排除后缀不能为空。");
        if (suffix != ":Zone.Identifier" &&
            Regex.IsMatch(suffix, @"[\\/:*?""<>|\[\]\x00-\x1f]"))
            throw new ArgumentException($"排除后缀应填写文件名末尾的文字：{suffix}");
        return suffix;
    }

    public static string ValidateNodeBin(string? input)
    {
        string value = (input ?? "").Trim();
        if (value.Length == 0) return "";
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            value.IndexOfAny(new[] { ':', '\r', '\n', '\0', '"', '\'' }) >= 0 ||
            Regex.IsMatch(value, @"[\x00-\x1f]|(^|/)\.{1,2}(/|$)"))
            throw new ArgumentException($"额外 PATH 目录必须是有效的 WSL 绝对路径：{value}");
        return value;
    }

    public static string ValidateWslDistribution(string? input)
    {
        string value = (input ?? "").Trim();
        if (value.Length == 0 || value.StartsWith('-') ||
            value.IndexOfAny(new[] { '/', '\\', ':', '\r', '\n', '\0' }) >= 0 ||
            Regex.IsMatch(value, @"[\x00-\x1f]"))
            throw new ArgumentException($"WSL 发行版名称无效：{input}");
        return value;
    }

    public static string ValidateWslUser(string? input)
    {
        string value = (input ?? "").Trim();
        if (value.Length == 0 || value.StartsWith('-') ||
            !Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_.-]*[$]?\z"))
            throw new ArgumentException($"WSL 用户名无效：{input}");
        return value;
    }

    public static string ValidateWslRoot(string? input)
    {
        string value = UnquotePath(input ?? "");
        if (TryParseWslNetworkPath(value, out var linuxPath, out _))
            value = linuxPath;
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal) ||
            value.Trim('/') == "" || Regex.IsMatch(value, @"[\x00-\x1f]|(^|/)\.{1,2}(/|$)"))
            throw new ArgumentException($"WSL 仓库路径必须是有效的 Linux 绝对路径：{input}");
        return value.TrimEnd('/');
    }

    public static string ValidateBranchName(string? input)
    {
        string value = (input ?? "").Trim();
        if (value.Length == 0 || value.StartsWith('-') || value.StartsWith('/') || value.EndsWith('/') ||
            value.EndsWith(".lock", StringComparison.Ordinal) || value.EndsWith('.') ||
            value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            Regex.IsMatch(value, @"[\x00-\x20\x7f~^:?*\[\\""]"))
            throw new ArgumentException($"目标分支名称无效：{input}");
        return value;
    }

    public static string ValidateRemoteName(string? input)
    {
        string value = (input ?? "").Trim();
        if (value.Length == 0 || value.StartsWith('-') ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.IndexOfAny(new[] { ':', '/', '\\', '@', ' ', '\t', '\r', '\n', '"', '\'' }) >= 0 ||
            Regex.IsMatch(value, @"[\x00-\x1f]"))
            throw new ArgumentException($"远端名称无效：{input}");
        return value;
    }

    public static bool TryParseWslNetworkPath(string value, out string path, out string distribution)
    {
        var match = Regex.Match(value.Replace('\\', '/'),
            @"^/{2,}(?:wsl\.localhost|wsl\$)/([^/]+)(/.+)$",
            RegexOptions.IgnoreCase);
        distribution = match.Groups[1].Value;
        path = match.Groups[2].Value;
        return match.Success;
    }

    public static string UnquotePath(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }
}

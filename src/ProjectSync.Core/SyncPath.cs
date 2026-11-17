#nullable enable
using System;

namespace ProjectSync.Core;

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
        else if (System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:/"))
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

        if (path.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(path,
            @"(^/|//|[""<>|:*?\[\]\x00-\x1f]|(^|/)(\.{1,2}|(?i:\.git))(/|$))"))
            throw new ArgumentException($"{label}包含无效路径：{input}");
        return path;
    }

    public static bool TryParseWslNetworkPath(string value, out string path, out string distribution)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value.Replace('\\', '/'),
            @"^/{2,}(?:wsl\.localhost|wsl\$)/([^/]+)(/.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        distribution = match.Groups[1].Value;
        path = match.Groups[2].Value;
        return match.Success;
    }

    private static string UnquotePath(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SiteLift.Core;

public sealed record SecurityReviewItem(string Key, string Label, string Value);

public sealed record ProjectConfig(
    string Name,
    string WindowsRepo,
    string Remote,
    string RemoteUrl,
    string Branch,
    string GithubUsername,
    string GitSshCommand,
    string Distro,
    string WslUser,
    string WslRepo,
    string NodeBin,
    string BuildCommand,
    string PreviewCommand,
    bool PreviewAfterBuild,
    string ReinstallCommand,
    string ThemeUpgradeCommand,
    string[] PullPaths,
    string[] Include,
    string[] Exclude,
    string[] ExcludeSuffixes,
    bool CleanUntracked)
{
    private static readonly string[] NestedSectionKeys = { "git", "source", "build", "sync" };

    private static readonly (string Key, string Label)[] CommandKeys =
    {
        ("buildCommand", "buildCommand（构建命令）"),
        ("previewCommand", "previewCommand（预览命令）"),
        ("reinstallCommand", "reinstallCommand（重装依赖命令）"),
        ("themeUpgradeCommand", "themeUpgradeCommand（升级主题命令）"),
        ("gitSshCommand", "gitSshCommand（SSH 命令与参数）"),
        ("nodeBin", "nodeBin（额外 PATH 目录）")
    };

    public static JsonObject ParseRawObject(string configuration)
    {
        var parsed = JsonNode.Parse(configuration) as JsonObject
            ?? throw new JsonException("配置必须是 JSON 对象。");
        if (NestedSectionKeys.Any(key => parsed[key] is JsonObject))
            throw new JsonException("不支持嵌套格式，请使用扁平项目配置。");
        return parsed;
    }

    public static ProjectConfig Parse(string configuration)
    {
        var parsed = ParseRawObject(configuration);
        string ReadString(string key)
        {
            var node = parsed[key];
            if (node is null) return "";
            return node.GetValue<string>().Trim();
        }

        string ReadRawString(string key)
        {
            var node = parsed[key];
            if (node is null) return "";
            return node.GetValue<string>();
        }

        bool ReadBool(string key) => parsed[key]?.GetValue<bool>() ?? false;

        string[] ReadList(string key)
        {
            var node = parsed[key];
            if (node is null) return Array.Empty<string>();
            if (node is JsonArray array)
            {
                return array
                    .Select(item => item?.GetValue<string>() ?? throw new JsonException($"{key} 包含空项。"))
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            throw new JsonException($"{key} 必须是字符串数组。");
        }

        string[] pullPaths;
        if (parsed.ContainsKey("pullPaths"))
        {
            pullPaths = ReadList("pullPaths");
        }
        else if (parsed["pullPath"] is JsonNode legacy)
        {
            string value = legacy.GetValue<string>().Trim();
            pullPaths = value.Length > 0 ? new[] { value } : Array.Empty<string>();
        }
        else
        {
            pullPaths = Array.Empty<string>();
        }

        return new ProjectConfig(
            Name: ReadString("name"),
            WindowsRepo: ReadString("windowsRepo"),
            Remote: ReadString("remote"),
            RemoteUrl: ReadString("remoteUrl"),
            Branch: ReadString("branch"),
            GithubUsername: ReadString("githubUsername"),
            GitSshCommand: ReadString("gitSshCommand"),
            Distro: ReadString("distro"),
            WslUser: ReadString("wslUser"),
            WslRepo: ReadString("wslRepo"),
            NodeBin: ReadString("nodeBin"),
            BuildCommand: ReadRawString("buildCommand").Trim(),
            PreviewCommand: ReadRawString("previewCommand").Trim(),
            PreviewAfterBuild: ReadBool("previewAfterBuild"),
            ReinstallCommand: ReadRawString("reinstallCommand").Trim(),
            ThemeUpgradeCommand: ReadRawString("themeUpgradeCommand").Trim(),
            PullPaths: pullPaths,
            Include: ReadList("include"),
            Exclude: ReadList("exclude"),
            ExcludeSuffixes: ReadList("excludeSuffixes"),
            CleanUntracked: ReadBool("cleanUntracked"));
    }

    public static IReadOnlyList<SecurityReviewItem> ExtractSecurityReviewItems(
        string configuration, JsonObject? previous = null)
    {
        var parsed = ParseRawObject(configuration);
        var items = new List<SecurityReviewItem>();

        bool Changed(string key) =>
            previous is null || !JsonNode.DeepEquals(parsed[key], previous[key]);

        foreach (var (key, label) in CommandKeys)
        {
            var value = parsed[key]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value) && Changed(key))
                items.Add(new SecurityReviewItem(key, label, value.Trim()));
        }

        JsonNode? pullNode = parsed["pullPaths"] ?? parsed["pullPath"];
        JsonNode? prevPullNode = previous?["pullPaths"] ?? previous?["pullPath"];
        if (pullNode is not null && (previous is null || !JsonNode.DeepEquals(pullNode, prevPullNode)))
        {
            string[] pullValues = pullNode is JsonArray arr
                ? arr.Select(item => item?.GetValue<string>()?.Trim() ?? "")
                    .Where(v => v.Length > 0).ToArray()
                : new[] { pullNode.GetValue<string>().Trim() }.Where(v => v.Length > 0).ToArray();
            if (pullValues.Length > 0)
            {
                items.Add(new SecurityReviewItem("pullPaths",
                    "pullPaths（构建前以远端覆盖这些 WSL 目录）",
                    string.Join(Environment.NewLine, pullValues)));
            }
        }

        if (parsed["cleanUntracked"]?.GetValue<bool>() == true && Changed("cleanUntracked"))
        {
            items.Add(new SecurityReviewItem("cleanUntracked",
                "cleanUntracked（同步前永久清理未跟踪文件）",
                "已开启：应用同步前将永久删除 Windows 工作区中未跟踪且未忽略的文件。"));
        }

        return items;
    }

    public static string ComputeConfigurationHash(string configuration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));

    public static string? ComputeBuildFingerprint(string configuration)
    {
        try
        {
            if (JsonNode.Parse(configuration) is not JsonObject config) return null;
            config.Remove("name");
            config.Remove("previewAfterBuild");
            config.Remove("previewCommand");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToJsonString())));
        }
        catch (JsonException) { return null; }
    }

    public static string TryReadString(string configuration, string property)
    {
        try
        {
            if ((JsonNode.Parse(configuration) as JsonObject)?[property] is JsonValue value &&
                value.TryGetValue<string>(out var text))
                return text.Trim();
        }
        catch (JsonException) { }
        return "";
    }

    public static bool TryReadBool(string configuration, string property)
    {
        try
        {
            if ((JsonNode.Parse(configuration) as JsonObject)?[property] is JsonValue value &&
                value.TryGetValue<bool>(out var flag))
                return flag;
        }
        catch (JsonException) { }
        return false;
    }

    public static string GetRemoteDisplaySummary(string configuration, string fallback)
    {
        try
        {
            using var config = JsonDocument.Parse(configuration);
            if (config.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string field in new[] { "remoteUrl", "remote" })
                {
                    if (config.RootElement.TryGetProperty(field, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        value.GetString()?.Trim() is { Length: > 0 } address)
                        return address;
                }
            }
        }
        catch (JsonException) { }
        return fallback;
    }
}

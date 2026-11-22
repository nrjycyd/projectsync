using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SiteLift.Core;

public enum SiteFramework
{
    Generic,
    VuePress,
    VitePress,
    Hexo,
    Hugo,
    Astro,
    Docusaurus,
    MkDocs,
    Quartz,
    Rspress,
    Nuxt,
    NextJs,
    Jekyll,
    MdBook,
    Docsify
}

public static class SiteFrameworkDetector
{
    public static string GetDisplayName(SiteFramework framework) => framework switch
    {
        SiteFramework.VuePress => "VuePress",
        SiteFramework.VitePress => "VitePress",
        SiteFramework.Hexo => "Hexo",
        SiteFramework.Hugo => "Hugo",
        SiteFramework.Astro => "Astro",
        SiteFramework.Docusaurus => "Docusaurus",
        SiteFramework.MkDocs => "MkDocs",
        SiteFramework.Quartz => "Quartz",
        SiteFramework.Rspress => "Rspress",
        SiteFramework.Nuxt => "Nuxt",
        SiteFramework.NextJs => "Next.js",
        SiteFramework.Jekyll => "Jekyll",
        SiteFramework.MdBook => "mdBook",
        SiteFramework.Docsify => "Docsify",
        _ => "通用项目"
    };

    public static SiteFramework Detect(string? configurationJson, string? windowsRepo = null)
    {
        string? repo = string.IsNullOrWhiteSpace(windowsRepo) ? null : windowsRepo.Trim();
        if (repo is null && !string.IsNullOrWhiteSpace(configurationJson))
        {
            string fromConfig = ProjectConfig.TryReadString(configurationJson, "windowsRepo");
            if (fromConfig.Length > 0) repo = fromConfig;
        }

        if (repo is not null)
        {
            var fromDir = DetectFromDirectory(repo);
            if (fromDir != SiteFramework.Generic)
                return fromDir;
        }

        if (!string.IsNullOrWhiteSpace(configurationJson))
        {
            var fromConfig = DetectFromConfiguration(configurationJson);
            if (fromConfig != SiteFramework.Generic)
                return fromConfig;
        }

        return SiteFramework.Generic;
    }

    public static SiteFramework DetectFromDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return SiteFramework.Generic;

        try
        {
            if (!Directory.Exists(directory))
                return SiteFramework.Generic;

            string packageJsonPath = Path.Combine(directory, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var fromPkg = DetectFromPackageJson(packageJsonPath);
                if (fromPkg != SiteFramework.Generic)
                    return fromPkg;
            }

            if (HasAnyDirectory(directory, ".vitepress", "docs/.vitepress", "src/.vitepress"))
                return SiteFramework.VitePress;
            if (HasAnyDirectory(directory, ".vuepress", "docs/.vuepress", "src/.vuepress"))
                return SiteFramework.VuePress;
            if (HasAnyFile(directory, "astro.config.mjs", "astro.config.ts", "astro.config.js", "astro.config.cjs"))
                return SiteFramework.Astro;
            if (HasAnyFile(directory, "docusaurus.config.ts", "docusaurus.config.js", "docusaurus.config.mjs", "docusaurus.config.cjs"))
                return SiteFramework.Docusaurus;
            if (HasAnyFile(directory, "quartz.config.ts", "quartz.config.js"))
                return SiteFramework.Quartz;
            if (HasAnyFile(directory, "rspress.config.ts", "rspress.config.js", "rspress.config.mjs"))
                return SiteFramework.Rspress;
            if (HasAnyFile(directory, "mkdocs.yml", "mkdocs.yaml"))
                return SiteFramework.MkDocs;
            if (HasAnyFile(directory, "hugo.toml", "hugo.yaml", "hugo.yml", "hugo.json") ||
                (HasAnyFile(directory, "config.toml") && HasAnyDirectory(directory, "content", "archetypes")))
                return SiteFramework.Hugo;
            if (HasAnyFile(directory, "book.toml"))
                return SiteFramework.MdBook;
            if (HasAnyFile(directory, "nuxt.config.ts", "nuxt.config.js", "nuxt.config.mjs"))
                return SiteFramework.Nuxt;
            if (HasAnyFile(directory, "next.config.mjs", "next.config.ts", "next.config.js", "next.config.cjs"))
                return SiteFramework.NextJs;

            if (HasAnyFile(directory, "_config.yml", "_config.yaml"))
            {
                if (HasAnyDirectory(directory, "source/_posts", "scaffolds"))
                    return SiteFramework.Hexo;
                if (HasAnyDirectory(directory, "_posts") || HasAnyFile(directory, "Gemfile"))
                    return SiteFramework.Jekyll;
            }

            string indexHtml = Path.Combine(directory, "index.html");
            if (File.Exists(indexHtml))
            {
                var info = new FileInfo(indexHtml);
                if (info.Length <= 65_536)
                {
                    string html = File.ReadAllText(indexHtml);
                    if (html.Contains("docsify", StringComparison.OrdinalIgnoreCase))
                        return SiteFramework.Docsify;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SiteFramework.Generic;
        }

        return SiteFramework.Generic;
    }

    public static SiteFramework DetectFromPackageJsonContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SiteFramework.Generic;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return SiteFramework.Generic;

            static bool HasDep(JsonElement root, Func<string, bool> match)
            {
                foreach (string section in new[] { "dependencies", "devDependencies", "peerDependencies" })
                {
                    if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var prop in deps.EnumerateObject())
                    {
                        if (match(prop.Name))
                            return true;
                    }
                }
                return false;
            }

            static string ReadScripts(JsonElement root)
            {
                if (!root.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
                    return "";
                var parts = new System.Collections.Generic.List<string>();
                foreach (var prop in scripts.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        parts.Add(prop.Value.GetString() ?? "");
                }
                return string.Join('\n', parts);
            }

            var root = doc.RootElement;
            if (HasDep(root, n => n.Equals("vitepress", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("vitepress-", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.VitePress;
            if (HasDep(root, n => n.Equals("vuepress", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("@vuepress/", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("vuepress-", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.VuePress;
            if (HasDep(root, n => n.Equals("@docusaurus/core", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("@docusaurus/", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Docusaurus;
            if (HasDep(root, n => n.Equals("astro", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("@astrojs/", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Astro;
            if (HasDep(root, n => n.Equals("hexo", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("hexo-", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Hexo;
            if (HasDep(root, n => n.Equals("rspress", StringComparison.OrdinalIgnoreCase) ||
                                  n.StartsWith("@rspress/", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Rspress;
            if (HasDep(root, n => n.Equals("nuxt", StringComparison.OrdinalIgnoreCase) ||
                                  n.Equals("@nuxt/content", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Nuxt;
            if (HasDep(root, n => n.Equals("next", StringComparison.OrdinalIgnoreCase) ||
                                  n.Equals("nextra", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.NextJs;
            if (HasDep(root, n => n.Equals("docsify", StringComparison.OrdinalIgnoreCase) ||
                                  n.Equals("docsify-cli", StringComparison.OrdinalIgnoreCase)))
                return SiteFramework.Docsify;

            if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String &&
                (nameProp.GetString() ?? "").Contains("quartz", StringComparison.OrdinalIgnoreCase))
                return SiteFramework.Quartz;

            string scriptsText = ReadScripts(root);
            if (scriptsText.Length > 0)
            {
                var fromScripts = DetectFromTextHints(scriptsText);
                if (fromScripts != SiteFramework.Generic)
                    return fromScripts;
            }
        }
        catch (JsonException) { }

        return SiteFramework.Generic;
    }

    public static SiteFramework DetectFromConfiguration(string configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
            return SiteFramework.Generic;

        try
        {
            if (JsonNode.Parse(configurationJson) is not JsonObject obj)
                return SiteFramework.Generic;

            var hints = new System.Text.StringBuilder();
            void AppendField(string key)
            {
                if (obj[key] is JsonValue val && val.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    hints.AppendLine(text);
                else if (obj[key] is JsonArray arr)
                {
                    foreach (var item in arr)
                        if (item is JsonValue itemVal && itemVal.TryGetValue<string>(out var itemText))
                            hints.AppendLine(itemText);
                }
            }

            AppendField("exclude");
            AppendField("include");
            AppendField("pullPaths");
            AppendField("buildCommand");
            AppendField("previewCommand");
            AppendField("themeUpgradeCommand");
            AppendField("reinstallCommand");

            return DetectFromTextHints(hints.ToString());
        }
        catch (JsonException)
        {
            return SiteFramework.Generic;
        }
    }

    private static SiteFramework DetectFromPackageJson(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 512_000)
            return SiteFramework.Generic;
        return DetectFromPackageJsonContent(File.ReadAllText(path));
    }

    private static SiteFramework DetectFromTextHints(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return SiteFramework.Generic;

        if (text.Contains(".vitepress", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\bvitepress\b", RegexOptions.IgnoreCase))
            return SiteFramework.VitePress;
        if (text.Contains(".vuepress", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\b(?:vuepress|vp-update)\b", RegexOptions.IgnoreCase))
            return SiteFramework.VuePress;
        if (Regex.IsMatch(text, @"\bdocusaurus\b", RegexOptions.IgnoreCase))
            return SiteFramework.Docusaurus;
        if (Regex.IsMatch(text, @"\bastro\b", RegexOptions.IgnoreCase))
            return SiteFramework.Astro;
        if (Regex.IsMatch(text, @"\bhexo\b", RegexOptions.IgnoreCase))
            return SiteFramework.Hexo;
        if (Regex.IsMatch(text, @"\bhugo\b", RegexOptions.IgnoreCase))
            return SiteFramework.Hugo;
        if (Regex.IsMatch(text, @"\bmkdocs\b", RegexOptions.IgnoreCase))
            return SiteFramework.MkDocs;
        if (Regex.IsMatch(text, @"\bquartz\b", RegexOptions.IgnoreCase))
            return SiteFramework.Quartz;
        if (Regex.IsMatch(text, @"\brspress\b", RegexOptions.IgnoreCase))
            return SiteFramework.Rspress;
        if (Regex.IsMatch(text, @"\bnuxt\b", RegexOptions.IgnoreCase))
            return SiteFramework.Nuxt;
        if (Regex.IsMatch(text, @"\bnext\s+(?:build|dev|start|export)\b", RegexOptions.IgnoreCase))
            return SiteFramework.NextJs;
        if (Regex.IsMatch(text, @"\bjekyll\b", RegexOptions.IgnoreCase))
            return SiteFramework.Jekyll;
        if (Regex.IsMatch(text, @"\bmdbook\b", RegexOptions.IgnoreCase))
            return SiteFramework.MdBook;
        if (Regex.IsMatch(text, @"\bdocsify\b", RegexOptions.IgnoreCase))
            return SiteFramework.Docsify;

        return SiteFramework.Generic;
    }

    private static bool HasAnyDirectory(string root, params string[] relativePaths)
    {
        foreach (string rel in relativePaths)
            if (Directory.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                return true;
        return false;
    }

    private static bool HasAnyFile(string root, params string[] relativePaths)
    {
        foreach (string rel in relativePaths)
            if (File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))))
                return true;
        return false;
    }
}

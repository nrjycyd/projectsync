using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace SiteLift.Core;

public sealed record SiteProfileScanOptions(
    string? ConfigurationJson = null,
    string[]? PullPaths = null,
    string[]? ExcludePaths = null,
    string? ProjectName = null,
    bool EnsureGitExclude = true);

public sealed record SiteProfileFrontmatter(
    string Format,
    string[] CommonKeys,
    string SampleTemplate,
    string? ScaffoldPath);

public sealed record SiteProfileAssets(
    string Mode,
    string PreferredDir,
    string[] DetectedAssetDirs,
    bool PostAssetFolder);

public sealed record SiteProfileNavigation(
    string Mode,
    string[] IndexFiles,
    string? Note);

public sealed record SiteProfileDirectory(
    string Path,
    string Title,
    int ArticleCount,
    string[] SampleTitles,
    string[] SampleFiles,
    string[] CommonFrontmatterKeys,
    bool OverwrittenOnBuild);

public sealed record SiteProfileTaxonomy(
    string[][] Categories,
    string[] Tags);

public sealed record SiteProfileAutoScanned(
    string[] ContentRoots,
    string[] SupportedExtensions,
    string FileNaming,
    SiteProfileFrontmatter Frontmatter,
    SiteProfileAssets Assets,
    SiteProfileNavigation Navigation,
    SiteProfileDirectory[] Directories,
    SiteProfileTaxonomy Taxonomy,
    Dictionary<string, object?> FrameworkHints);

public sealed record SiteProfileDocument(
    int SchemaVersion,
    string GeneratedAt,
    string StructureFingerprint,
    string ProjectName,
    string Framework,
    string Theme,
    SiteProfileAutoScanned AutoScanned,
    JsonObject UserRules);

public sealed record SiteProfileScanResult(
    string ProfilePath,
    string RelativePath,
    SiteProfileDocument Document,
    bool GitExcludeUpdated,
    bool GitDirectoryFound,
    int ScannedDirectories,
    int SampledArticles,
    int CategoryCount,
    int TagCount,
    bool PreservedUserRules);

public sealed record SiteProfileFreshness(
    bool IsStale,
    string CurrentFingerprint,
    string Reason);

public static class SiteProfileScanner
{
    public const string ProfileDirectoryName = ".sitelift";
    public const string ProfileFileName = "profile.json";
    public const string ProfileRelativePath = ".sitelift/profile.json";
    public const string GitExcludeEntry = ".sitelift/";
    public const int CurrentSchemaVersion = 1;

    private const int MaxDirectories = 200;
    private const int MaxSamplesPerDirectory = 5;
    private const int MaxTotalFrontmatterReads = 140;
    private const int MaxHeaderReadBytes = 8192;
    private const int MaxConfigReadBytes = 262_144;
    private const int MaxTaxonomyCategories = 80;
    private const int MaxTaxonomyTags = 120;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> GlobalIgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".sitelift", ".github", ".vscode", ".idea",
        "node_modules", ".vitepress", ".vuepress", ".docusaurus", ".astro",
        ".next", ".nuxt", ".cache", ".temp", ".tmp", "_site",
        "dist", "build", "out", "target", "vendor", ".venv", "venv",
        "__pycache__", "themes", "scaffolds", "archetypes",
        "_layouts", "_includes", "_sass", "_data", "_tabs", "_drafts"
    };

    private static readonly HashSet<string> GlobalStaticAssetDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "static", "assets", "images", "img", "media", "attachments", "files"
    };

    private static readonly Regex DateSlugRegex = new(@"^\d{4}-\d{2}-\d{2}-.+", RegexOptions.Compiled);
    private static readonly Regex NumberedSlugRegex = new(@"^\d+[._-].+", RegexOptions.Compiled);
    private static readonly Regex KebabSlugRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex CjkRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex ObsidianEmbedRegex = new(@"!\[\[([^\]]+\.(?:png|jpe?g|gif|webp|svg|avif))(?:\|[^\]]*)?\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlImgRegex = new(@"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Heading1Regex = new(@"^\s*#\s+(.+?)\s*#*\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private sealed record FrameworkSpec(
        SiteFramework Framework,
        string[] SupportedExtensions,
        string[] DefaultContentRoots,
        string[] ConfigCandidates,
        string[] ExtraIgnoredDirNames,
        string DefaultFileNaming,
        string DefaultFrontmatterFormat,
        string[] DefaultAssetCandidates);

    private static readonly Dictionary<SiteFramework, FrameworkSpec> Specs = new()
    {
        [SiteFramework.VitePress] = new(
            SiteFramework.VitePress,
            new[] { ".md" },
            new[] { "docs", "." },
            new[] { "docs/.vitepress/config.mts", "docs/.vitepress/config.ts", "docs/.vitepress/config.mjs", "docs/.vitepress/config.js", ".vitepress/config.mts", ".vitepress/config.ts", ".vitepress/config.mjs", ".vitepress/config.js", "src/.vitepress/config.mts", "src/.vitepress/config.ts" },
            Array.Empty<string>(),
            "kebab-case.md",
            "yaml",
            new[] { "docs/public", "public", "docs/assets" }),

        [SiteFramework.VuePress] = new(
            SiteFramework.VuePress,
            new[] { ".md" },
            new[] { "docs", "src", "." },
            new[] { "docs/.vuepress/config.ts", "docs/.vuepress/config.mts", "docs/.vuepress/config.js", "docs/.vuepress/config.mjs", ".vuepress/config.ts", ".vuepress/config.js", "src/.vuepress/config.ts", "src/.vuepress/config.js" },
            Array.Empty<string>(),
            "kebab-case.md",
            "yaml",
            new[] { "docs/.vuepress/public", ".vuepress/public", "docs/public", "docs/assets" }),

        [SiteFramework.Hugo] = new(
            SiteFramework.Hugo,
            new[] { ".md" },
            new[] { "content" },
            new[] { "hugo.toml", "hugo.yaml", "hugo.yml", "hugo.json", "config.toml", "config.yaml", "config.yml", "config/_default/hugo.toml", "config/_default/config.toml", "config/_default/hugo.yaml" },
            new[] { "resources", "layouts", "i18n", "data" },
            "kebab-case.md",
            "yaml",
            new[] { "static/images", "static/img", "assets/images", "assets/img" }),

        [SiteFramework.Hexo] = new(
            SiteFramework.Hexo,
            new[] { ".md" },
            new[] { "source/_posts", "source" },
            new[] { "_config.yml", "_config.yaml" },
            Array.Empty<string>(),
            "kebab-case.md",
            "yaml",
            new[] { "source/images", "source/img", "source/assets" }),

        [SiteFramework.Jekyll] = new(
            SiteFramework.Jekyll,
            new[] { ".md", ".markdown" },
            new[] { "_posts", "." },
            new[] { "_config.yml", "_config.yaml", "Gemfile" },
            Array.Empty<string>(),
            "YYYY-MM-DD-slug.md",
            "yaml",
            new[] { "assets/img/posts", "assets/img", "assets/images", "images" }),

        [SiteFramework.Astro] = new(
            SiteFramework.Astro,
            new[] { ".md", ".mdx" },
            new[] { "src/content", "src/pages" },
            new[] { "astro.config.mjs", "astro.config.ts", "astro.config.js", "astro.config.cjs", "src/content.config.ts", "src/content/config.ts" },
            new[] { "src/components", "src/layouts", "src/styles" },
            "kebab-case.md",
            "yaml",
            new[] { "public/images", "public", "src/assets" }),

        [SiteFramework.Docusaurus] = new(
            SiteFramework.Docusaurus,
            new[] { ".md", ".mdx" },
            new[] { "docs", "blog" },
            new[] { "docusaurus.config.ts", "docusaurus.config.js", "docusaurus.config.mjs", "sidebars.ts", "sidebars.js" },
            new[] { "src", "i18n", "versioned_docs" },
            "kebab-case.md",
            "yaml",
            new[] { "static/img", "static/images" }),

        [SiteFramework.MkDocs] = new(
            SiteFramework.MkDocs,
            new[] { ".md" },
            new[] { "docs" },
            new[] { "mkdocs.yml", "mkdocs.yaml" },
            new[] { "site", "overrides" },
            "kebab-case.md",
            "yaml",
            new[] { "docs/assets", "docs/images", "docs/img" }),

        [SiteFramework.Quartz] = new(
            SiteFramework.Quartz,
            new[] { ".md" },
            new[] { "content" },
            new[] { "quartz.config.ts", "quartz.config.js", "quartz.layout.ts" },
            new[] { "quartz" },
            "mixed",
            "yaml",
            new[] { "content/attachments", "content/assets", "content/images" }),

        [SiteFramework.Rspress] = new(
            SiteFramework.Rspress,
            new[] { ".md", ".mdx" },
            new[] { "docs" },
            new[] { "rspress.config.ts", "rspress.config.js", "rspress.config.mjs" },
            new[] { "doc_build", "theme" },
            "kebab-case.md",
            "yaml",
            new[] { "docs/public", "public" }),

        [SiteFramework.Nuxt] = new(
            SiteFramework.Nuxt,
            new[] { ".md", ".mdx" },
            new[] { "content" },
            new[] { "nuxt.config.ts", "nuxt.config.js", "nuxt.config.mjs", "content.config.ts" },
            new[] { "components", "composables", "layouts", "pages", "server", "plugins" },
            "numbered-slug.md",
            "yaml",
            new[] { "public/images", "public", "assets" }),

        [SiteFramework.NextJs] = new(
            SiteFramework.NextJs,
            new[] { ".md", ".mdx" },
            new[] { "content", "pages", "src/pages", "app" },
            new[] { "next.config.mjs", "next.config.ts", "next.config.js", "next.config.cjs", "theme.config.tsx", "theme.config.jsx" },
            new[] { "components", "lib", "styles" },
            "kebab-case.mdx",
            "yaml",
            new[] { "public/images", "public" }),

        [SiteFramework.MdBook] = new(
            SiteFramework.MdBook,
            new[] { ".md" },
            new[] { "src" },
            new[] { "book.toml" },
            new[] { "book", "theme" },
            "kebab-case.md",
            "none",
            new[] { "src/images", "src/assets" }),

        [SiteFramework.Docsify] = new(
            SiteFramework.Docsify,
            new[] { ".md" },
            new[] { "docs", "." },
            new[] { "docs/index.html", "index.html", "docs/_sidebar.md", "_sidebar.md" },
            Array.Empty<string>(),
            "kebab-case.md",
            "none",
            new[] { "docs/images", "docs/assets", "images", "assets" }),

        [SiteFramework.Generic] = new(
            SiteFramework.Generic,
            new[] { ".md", ".mdx" },
            new[] { "docs", "content", "posts", "blog", "src", "." },
            Array.Empty<string>(),
            Array.Empty<string>(),
            "mixed",
            "yaml",
            new[] { "assets", "images", "public", "static" })
    };

    public static string GetProfilePath(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("站点根目录不能为空。", nameof(rootDirectory));
        return Path.Combine(rootDirectory, ProfileDirectoryName, ProfileFileName);
    }

    public static SiteProfileScanResult ScanAndSave(
        string rootDirectory,
        SiteProfileScanOptions? options = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"站点目录不存在或不可访问：{rootDirectory}");

        options ??= new SiteProfileScanOptions();
        string profilePath = GetProfilePath(rootDirectory);

        var (existingUserRules, preservedUserRules) = TryLoadExistingUserRules(profilePath);
        var (doc, stats) = ScanInternal(rootDirectory, options, existingUserRules, cancellation);

        string profileDir = Path.GetDirectoryName(profilePath)!;
        Directory.CreateDirectory(profileDir);

        string json = JsonSerializer.Serialize(doc, SerializerOptions).Replace("\r\n", "\n") + "\n";
        string tempPath = Path.Combine(profileDir, $".{ProfileFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        bool gitFound = false;
        bool gitExcludeUpdated = false;
        if (options.EnsureGitExclude)
            (gitFound, gitExcludeUpdated) = EnsureGitInfoExclude(rootDirectory, GitExcludeEntry);

        return new SiteProfileScanResult(
            ProfilePath: profilePath,
            RelativePath: ProfileRelativePath,
            Document: doc,
            GitExcludeUpdated: gitExcludeUpdated,
            GitDirectoryFound: gitFound,
            ScannedDirectories: stats.ScannedDirectories,
            SampledArticles: stats.SampledArticles,
            CategoryCount: doc.AutoScanned.Taxonomy.Categories.Length,
            TagCount: doc.AutoScanned.Taxonomy.Tags.Length,
            PreservedUserRules: preservedUserRules);
    }

    public static SiteProfileDocument Scan(
        string rootDirectory,
        SiteProfileScanOptions? options = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"站点目录不存在或不可访问：{rootDirectory}");

        options ??= new SiteProfileScanOptions();
        var (existingUserRules, _) = TryLoadExistingUserRules(GetProfilePath(rootDirectory));
        return ScanInternal(rootDirectory, options, existingUserRules, cancellation).Document;
    }

    public static SiteProfileFreshness CheckStructureFreshness(
        string rootDirectory,
        SiteProfileDocument profile,
        SiteProfileScanOptions? options = null,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            return new SiteProfileFreshness(false, profile.StructureFingerprint, "");

        options ??= new SiteProfileScanOptions();
        cancellation.ThrowIfCancellationRequested();

        var framework = SiteFrameworkDetector.DetectFromDirectory(rootDirectory);
        if (framework == SiteFramework.Generic && !string.IsNullOrWhiteSpace(options.ConfigurationJson))
            framework = SiteFrameworkDetector.DetectFromConfiguration(options.ConfigurationJson);

        var spec = Specs[framework];
        using var packageJson = TryReadPackageJson(rootDirectory);
        string theme = DetectTheme(rootDirectory, framework, packageJson);

        var ignoredRelPaths = new HashSet<string>(NormalizeRelativePaths(options.ExcludePaths), StringComparer.OrdinalIgnoreCase);
        foreach (string userIgnored in ReadUserIgnoredDirectories(profile.UserRules))
            ignoredRelPaths.Add(userIgnored);

        var ignoredDirNames = new HashSet<string>(GlobalIgnoredDirNames, StringComparer.OrdinalIgnoreCase);
        foreach (string extra in spec.ExtraIgnoredDirNames)
            ignoredDirNames.Add(extra);

        var extensions = new HashSet<string>(spec.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        string[] contentRoots = ResolveContentRoots(rootDirectory, framework, spec, extensions, ignoredDirNames, ignoredRelPaths);
        var navigation = DetectNavigation(rootDirectory, framework, contentRoots, packageJson, new Dictionary<string, object?>());

        var fingerprintInputs = new List<string>();
        foreach (string relConfig in spec.ConfigCandidates)
        {
            string fullConfig = CombineRelative(rootDirectory, relConfig);
            if (File.Exists(fullConfig))
                fingerprintInputs.Add($"cfg:{relConfig}:{SafeGetLastWriteUtc(fullConfig).Ticks}");
        }
        foreach (string idxFile in navigation.IndexFiles)
        {
            string fullIdx = CombineRelative(rootDirectory, idxFile);
            if (File.Exists(fullIdx))
                fingerprintInputs.Add($"nav:{idxFile}:{SafeGetLastWriteUtc(fullIdx).Ticks}");
        }

        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (string root in contentRoots)
        {
            string normalized = NormalizeRelativePath(root);
            if (visitedDirs.Add(normalized))
                queue.Enqueue(normalized);
        }

        int walked = 0;
        while (queue.Count > 0 && walked < MaxDirectories)
        {
            cancellation.ThrowIfCancellationRequested();
            string relDir = queue.Dequeue();
            string fullDir = relDir == "." ? rootDirectory : CombineRelative(rootDirectory, relDir);
            if (!Directory.Exists(fullDir))
                continue;

            fingerprintInputs.Add($"dir:{relDir}");
            walked++;

            string[] childDirs;
            try { childDirs = Directory.GetDirectories(fullDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(childDirs, StringComparer.OrdinalIgnoreCase);

            foreach (string childFull in childDirs)
            {
                string dirName = Path.GetFileName(childFull);
                string childRel = relDir == "." ? dirName : $"{relDir}/{dirName}";
                if (ShouldIgnoreDirectory(dirName, childRel, framework, ignoredDirNames, ignoredRelPaths))
                    continue;
                if (IsLeafPageBundle(childFull, framework, extensions, out _))
                {
                    fingerprintInputs.Add($"bundle:{childRel}");
                    continue;
                }
                if (visitedDirs.Add(childRel))
                    queue.Enqueue(childRel);
            }
        }

        fingerprintInputs.Sort(StringComparer.Ordinal);
        string payload = $"{SiteFrameworkDetector.GetDisplayName(framework)}|{theme}|{string.Join("\n", fingerprintInputs)}";
        string currentFingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        bool stale = !string.Equals(currentFingerprint, profile.StructureFingerprint, StringComparison.OrdinalIgnoreCase);
        return new SiteProfileFreshness(
            IsStale: stale,
            CurrentFingerprint: currentFingerprint,
            Reason: stale ? "检测到站点目录结构或框架配置已变化，建议点击「刷新画像」以获得最新推荐。" : "");
    }

    public static bool AppendUserRoutingHint(string rootDirectory, string hint)
    {
        string trimmed = (hint ?? "").Trim();
        if (trimmed.Length == 0)
            return false;

        string profilePath = GetProfilePath(rootDirectory);
        if (!File.Exists(profilePath))
            return false;

        if (JsonNode.Parse(File.ReadAllText(profilePath, Encoding.UTF8)) is not JsonObject doc)
            return false;

        if (doc["userRules"] is not JsonObject rules)
        {
            rules = CreateDefaultUserRules();
            doc["userRules"] = rules;
        }
        if (rules["routingHints"] is not JsonArray hints)
        {
            hints = new JsonArray();
            rules["routingHints"] = hints;
        }

        foreach (var item in hints)
        {
            if (item is JsonValue val && val.TryGetValue<string>(out var existing) &&
                string.Equals(existing.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        hints.Add(trimmed);
        string json = doc.ToJsonString(SerializerOptions).Replace("\r\n", "\n") + "\n";
        string profileDir = Path.GetDirectoryName(profilePath)!;
        string tempPath = Path.Combine(profileDir, $".{ProfileFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        return true;
    }

    public static (bool GitDirectoryFound, bool Updated) EnsureGitInfoExclude(
        string rootDirectory,
        string entry = GitExcludeEntry)
    {
        string? gitDir = ResolveGitDirectory(rootDirectory);
        if (gitDir is null)
            return (false, false);

        string infoDir = Path.Combine(gitDir, "info");
        Directory.CreateDirectory(infoDir);
        string excludePath = Path.Combine(infoDir, "exclude");

        string normalizedEntry = entry.Trim();
        string bareEntry = normalizedEntry.Trim('/');

        string existing = File.Exists(excludePath) ? File.ReadAllText(excludePath, Encoding.UTF8) : "";
        foreach (string rawLine in existing.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string cleaned = line.Trim('/');
            if (string.Equals(line, normalizedEntry, StringComparison.Ordinal) ||
                string.Equals(cleaned, bareEntry, StringComparison.Ordinal))
            {
                return (true, false);
            }
        }

        var updated = new StringBuilder(existing);
        if (updated.Length > 0 && updated[^1] != '\n')
            updated.Append('\n');
        if (updated.Length > 0 && !existing.EndsWith("\n\n", StringComparison.Ordinal) &&
            !existing.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            updated.Append('\n');
        updated.Append("# SiteLift local site profile\n");
        updated.Append(normalizedEntry).Append('\n');

        File.WriteAllText(excludePath, updated.ToString(), Utf8NoBom);
        return (true, true);
    }

    private static string? ResolveGitDirectory(string rootDirectory)
    {
        string dotGitPath = Path.Combine(rootDirectory, ".git");
        if (Directory.Exists(dotGitPath))
            return dotGitPath;
        if (!File.Exists(dotGitPath))
            return null;

        try
        {
            string firstLine = File.ReadLines(dotGitPath, Encoding.UTF8).FirstOrDefault()?.Trim() ?? "";
            const string prefix = "gitdir:";
            if (!firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            string target = firstLine[prefix.Length..].Trim();
            if (target.Length == 0)
                return null;
            string resolved = Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(rootDirectory, target));
            return Directory.Exists(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static (JsonObject UserRules, bool Preserved) TryLoadExistingUserRules(string profilePath)
    {
        if (File.Exists(profilePath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(profilePath, Encoding.UTF8)) as JsonObject;
                if (node?["userRules"] is JsonObject existingRules)
                    return ((JsonObject)existingRules.DeepClone(), true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return (CreateDefaultUserRules(), false);
    }

    private static JsonObject CreateDefaultUserRules() => new()
    {
        ["routingHints"] = new JsonArray(),
        ["ignoredDirectories"] = new JsonArray(),
        ["preferredNaming"] = "",
        ["preferredTagsStyle"] = "",
        ["notes"] = ""
    };

    private static (SiteProfileDocument Document, ScanStats Stats) ScanInternal(
        string rootDirectory,
        SiteProfileScanOptions options,
        JsonObject userRules,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        var framework = SiteFrameworkDetector.DetectFromDirectory(rootDirectory);
        if (framework == SiteFramework.Generic && !string.IsNullOrWhiteSpace(options.ConfigurationJson))
            framework = SiteFrameworkDetector.DetectFromConfiguration(options.ConfigurationJson);

        var spec = Specs[framework];
        using var packageJson = TryReadPackageJson(rootDirectory);
        string theme = DetectTheme(rootDirectory, framework, packageJson);

        string[] normalizedPullPaths = NormalizeRelativePaths(options.PullPaths);
        var ignoredRelPaths = new HashSet<string>(NormalizeRelativePaths(options.ExcludePaths), StringComparer.OrdinalIgnoreCase);
        foreach (string userIgnored in ReadUserIgnoredDirectories(userRules))
            ignoredRelPaths.Add(userIgnored);

        var ignoredDirNames = new HashSet<string>(GlobalIgnoredDirNames, StringComparer.OrdinalIgnoreCase);
        foreach (string extra in spec.ExtraIgnoredDirNames)
            ignoredDirNames.Add(extra);

        var extensions = new HashSet<string>(spec.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        string[] contentRoots = ResolveContentRoots(rootDirectory, framework, spec, extensions, ignoredDirNames, ignoredRelPaths);

        var hints = new Dictionary<string, object?>(StringComparer.Ordinal);
        var navigation = DetectNavigation(rootDirectory, framework, contentRoots, packageJson, hints);
        string? scaffoldPath = DetectScaffoldPath(rootDirectory, framework);
        bool postAssetFolder = framework == SiteFramework.Hexo && DetectHexoPostAssetFolder(rootDirectory);
        PopulateFrameworkSpecificHints(rootDirectory, framework, theme, contentRoots, packageJson, postAssetFolder, hints);

        var stats = new ScanStats();
        var directories = new List<SiteProfileDirectory>();
        var allFrontmatterKeyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allFormatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allNamingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var assetRefModes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var categoryMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fingerprintInputs = new List<string>();
        bool detectedMediaSubpath = false;
        bool detectedAbbrlink = false;
        string? detectedDateSample = null;
        string? detectedSampleTemplate = null;

        foreach (string relConfig in spec.ConfigCandidates)
        {
            string fullConfig = CombineRelative(rootDirectory, relConfig);
            if (File.Exists(fullConfig))
            {
                DateTime mtime = SafeGetLastWriteUtc(fullConfig);
                fingerprintInputs.Add($"cfg:{relConfig}:{mtime.Ticks}");
            }
        }
        foreach (string idxFile in navigation.IndexFiles)
        {
            string fullIdx = CombineRelative(rootDirectory, idxFile);
            if (File.Exists(fullIdx))
            {
                DateTime mtime = SafeGetLastWriteUtc(fullIdx);
                fingerprintInputs.Add($"nav:{idxFile}:{mtime.Ticks}");
            }
        }

        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (string root in contentRoots)
        {
            string normalized = NormalizeRelativePath(root);
            if (visitedDirs.Add(normalized))
                queue.Enqueue(normalized);
        }

        while (queue.Count > 0 && directories.Count < MaxDirectories)
        {
            cancellation.ThrowIfCancellationRequested();
            string relDir = queue.Dequeue();
            string fullDir = relDir == "." ? rootDirectory : CombineRelative(rootDirectory, relDir);
            if (!Directory.Exists(fullDir))
                continue;

            fingerprintInputs.Add($"dir:{relDir}");

            string[] childDirs;
            string[] childFiles;
            try
            {
                childDirs = Directory.GetDirectories(fullDir);
                childFiles = Directory.GetFiles(fullDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(childDirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(childFiles, StringComparer.OrdinalIgnoreCase);

            // Detect Hugo-style leaf page bundles (childDir/index.md with no nested article subdirs)
            int pageBundlesInDir = 0;
            var bundleSampleFiles = new List<(string DisplayName, string FullPath)>();

            foreach (string childFull in childDirs)
            {
                string dirName = Path.GetFileName(childFull);
                string childRel = relDir == "." ? dirName : $"{relDir}/{dirName}";
                if (ShouldIgnoreDirectory(dirName, childRel, framework, ignoredDirNames, ignoredRelPaths))
                    continue;

                if (IsLeafPageBundle(childFull, framework, extensions, out string? bundleIndexFile))
                {
                    pageBundlesInDir++;
                    if (bundleSampleFiles.Count < MaxSamplesPerDirectory)
                        bundleSampleFiles.Add(($"{dirName}/{Path.GetFileName(bundleIndexFile!)}", bundleIndexFile!));
                    fingerprintInputs.Add($"bundle:{childRel}");
                    continue;
                }

                if (visitedDirs.Add(childRel))
                    queue.Enqueue(childRel);
            }

            var articleFiles = new List<string>();
            foreach (string file in childFiles)
            {
                string fileName = Path.GetFileName(file);
                string ext = Path.GetExtension(fileName);
                if (!extensions.Contains(ext))
                    continue;
                if (IsSpecialIndexOrMetaMarkdown(fileName, framework, relDir))
                    continue;
                articleFiles.Add(file);
            }

            int totalArticlesHere = articleFiles.Count + pageBundlesInDir;
            bool isContentRoot = contentRoots.Any(r => string.Equals(NormalizeRelativePath(r), relDir, StringComparison.OrdinalIgnoreCase));
            if (totalArticlesHere == 0 && !isContentRoot)
                continue;

            string dirTitle = DetectDirectoryTitle(fullDir, relDir, framework);
            var sampleTitles = new List<string>();
            var sampleFiles = new List<string>();
            var dirKeyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Order most recently modified first so samples reflect current conventions
            var prioritizedFiles = articleFiles
                .Select(f => (FullPath: f, DisplayName: Path.GetFileName(f), IsBundle: false, LastWrite: SafeGetLastWriteUtc(f)))
                .Concat(bundleSampleFiles.Select(b => (b.FullPath, b.DisplayName, IsBundle: true, LastWrite: SafeGetLastWriteUtc(b.FullPath))))
                .OrderByDescending(x => x.LastWrite)
                .ToList();

            bool isOverwrittenByPull = IsOverwrittenByPullPaths(relDir, normalizedPullPaths);

            int sampledHere = 0;
            foreach (var item in prioritizedFiles)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!isOverwrittenByPull)
                {
                    if (item.IsBundle)
                        Increment(allNamingCounts, "page-bundle (slug/index.md)");
                    else
                        Increment(allNamingCounts, ClassifyFileName(item.DisplayName));
                }

                if (sampleFiles.Count < MaxSamplesPerDirectory)
                    sampleFiles.Add(item.DisplayName);

                // Read frontmatter/title up to budget (sample more when directory has many posts, e.g. _posts)
                int dirReadLimit = isContentRoot && totalArticlesHere > 15 ? 40 : MaxSamplesPerDirectory;
                if (sampledHere >= dirReadLimit || stats.SampledArticles >= MaxTotalFrontmatterReads)
                    continue;

                var parsed = ParseMarkdownHeader(item.FullPath);
                sampledHere++;
                stats.SampledArticles++;

                if (!string.IsNullOrWhiteSpace(parsed.Title) && sampleTitles.Count < MaxSamplesPerDirectory)
                    sampleTitles.Add(parsed.Title!);

                if (parsed.FrontmatterFormat != "none")
                {
                    if (!isOverwrittenByPull)
                        Increment(allFormatCounts, parsed.FrontmatterFormat);
                    if (detectedSampleTemplate is null && parsed.Keys.Count > 0)
                        detectedSampleTemplate = BuildTemplateFromKeys(parsed.FrontmatterFormat, parsed.Keys, parsed.DateSample);
                }

                if (detectedDateSample is null && !string.IsNullOrWhiteSpace(parsed.DateSample))
                    detectedDateSample = parsed.DateSample;
                if (parsed.HasMediaSubpath)
                    detectedMediaSubpath = true;
                if (parsed.HasAbbrlink)
                    detectedAbbrlink = true;

                foreach (string key in parsed.Keys)
                {
                    Increment(dirKeyCounts, key);
                    Increment(allFrontmatterKeyCounts, key);
                }

                foreach (var catPath in parsed.Categories)
                {
                    string catKey = string.Join(" / ", catPath);
                    if (catKey.Length > 0 && categoryMap.Count < MaxTaxonomyCategories && !categoryMap.ContainsKey(catKey))
                        categoryMap[catKey] = catPath;
                }

                foreach (string tag in parsed.Tags)
                    Increment(tagCounts, tag);

                foreach (string imgRef in parsed.ImageRefs)
                    Increment(assetRefModes, ClassifyImageReference(imgRef, item.IsBundle, postAssetFolder));
            }

            directories.Add(new SiteProfileDirectory(
                Path: relDir,
                Title: dirTitle,
                ArticleCount: totalArticlesHere,
                SampleTitles: sampleTitles.ToArray(),
                SampleFiles: sampleFiles.ToArray(),
                CommonFrontmatterKeys: TopKeys(dirKeyCounts, 8),
                OverwrittenOnBuild: IsOverwrittenByPullPaths(relDir, normalizedPullPaths)));
            stats.ScannedDirectories++;
        }

        string fileNaming = SelectDominant(allNamingCounts, spec.DefaultFileNaming);
        string fmFormat = SelectDominant(allFormatCounts, spec.DefaultFrontmatterFormat);
        string[] commonFmKeys = TopKeys(allFrontmatterKeyCounts, 10);
        string sampleTemplate = BuildFinalTemplate(rootDirectory, scaffoldPath, fmFormat, commonFmKeys, detectedSampleTemplate, detectedDateSample, framework, theme);

        var assets = BuildAssetsProfile(rootDirectory, spec, assetRefModes, postAssetFolder, framework, detectedMediaSubpath);

        if (framework == SiteFramework.Jekyll && string.Equals(theme, "Chirpy", StringComparison.OrdinalIgnoreCase))
        {
            hints["isChirpy"] = true;
            hints["maxCategoryDepth"] = 2;
            hints["tagsLowercaseRecommended"] = true;
            hints["fileNamingRule"] = "YYYY-MM-DD-title-slug.md";
            hints["mediaSubpathDetected"] = detectedMediaSubpath;
            if (!string.IsNullOrWhiteSpace(detectedDateSample))
                hints["dateSample"] = detectedDateSample;
        }
        else if (framework == SiteFramework.Hexo && detectedAbbrlink)
        {
            hints["abbrlinkDetected"] = true;
        }
        else if (framework == SiteFramework.VuePress && theme.Contains("plume", StringComparison.OrdinalIgnoreCase))
        {
            hints["usesCreateTime"] = allFrontmatterKeyCounts.ContainsKey("createTime");
            hints["usesPermalink"] = allFrontmatterKeyCounts.ContainsKey("permalink");
        }

        string[][] sortedCategories = categoryMap.Values
            .OrderBy(c => string.Join("/", c), StringComparer.OrdinalIgnoreCase)
            .Take(MaxTaxonomyCategories)
            .ToArray();

        string[] sortedTags = tagCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .Take(MaxTaxonomyTags)
            .ToArray();

        fingerprintInputs.Sort(StringComparer.Ordinal);
        string fingerprintPayload = $"{SiteFrameworkDetector.GetDisplayName(framework)}|{theme}|{string.Join("\n", fingerprintInputs)}";
        string fingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload))).ToLowerInvariant();

        string projectName = !string.IsNullOrWhiteSpace(options.ProjectName)
            ? options.ProjectName.Trim()
            : (!string.IsNullOrWhiteSpace(options.ConfigurationJson)
                ? ProjectConfig.TryReadString(options.ConfigurationJson, "name")
                : "");
        if (projectName.Length == 0)
            projectName = Path.GetFileName(rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var autoScanned = new SiteProfileAutoScanned(
            ContentRoots: contentRoots,
            SupportedExtensions: spec.SupportedExtensions,
            FileNaming: fileNaming,
            Frontmatter: new SiteProfileFrontmatter(fmFormat, commonFmKeys, sampleTemplate, scaffoldPath),
            Assets: assets,
            Navigation: navigation,
            Directories: directories.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Taxonomy: new SiteProfileTaxonomy(sortedCategories, sortedTags),
            FrameworkHints: hints);

        var document = new SiteProfileDocument(
            SchemaVersion: CurrentSchemaVersion,
            GeneratedAt: DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            StructureFingerprint: fingerprint,
            ProjectName: projectName,
            Framework: SiteFrameworkDetector.GetDisplayName(framework),
            Theme: theme,
            AutoScanned: autoScanned,
            UserRules: userRules);

        return (document, stats);
    }

    private sealed class ScanStats
    {
        public int ScannedDirectories { get; set; }
        public int SampledArticles { get; set; }
    }

    private static string[] ResolveContentRoots(
        string rootDirectory,
        SiteFramework framework,
        FrameworkSpec spec,
        HashSet<string> extensions,
        HashSet<string> ignoredDirNames,
        HashSet<string> ignoredRelPaths)
    {
        switch (framework)
        {
            case SiteFramework.VitePress:
            {
                string? configText = ReadFirstExistingText(rootDirectory, spec.ConfigCandidates, out string? configRel);
                string? srcDir = ExtractJsOrTsStringOption(configText, "srcDir");
                if (!string.IsNullOrWhiteSpace(srcDir))
                {
                    string baseDir = configRel is not null && configRel.Contains("/.vitepress/", StringComparison.OrdinalIgnoreCase)
                        ? configRel[..configRel.IndexOf("/.vitepress/", StringComparison.OrdinalIgnoreCase)]
                        : ".";
                    string resolved = NormalizeRelativePath(baseDir == "." ? srcDir! : $"{baseDir}/{srcDir}");
                    if (DirectoryExistsAt(rootDirectory, resolved))
                        return new[] { resolved };
                }
                if (Directory.Exists(CombineRelative(rootDirectory, "docs/.vitepress")))
                    return new[] { "docs" };
                if (Directory.Exists(CombineRelative(rootDirectory, "src/.vitepress")))
                    return new[] { "src" };
                if (Directory.Exists(CombineRelative(rootDirectory, "docs")))
                    return new[] { "docs" };
                return new[] { "." };
            }

            case SiteFramework.VuePress:
            {
                if (Directory.Exists(CombineRelative(rootDirectory, "docs/.vuepress")))
                    return new[] { "docs" };
                if (Directory.Exists(CombineRelative(rootDirectory, "src/.vuepress")))
                    return new[] { "src" };
                if (Directory.Exists(CombineRelative(rootDirectory, "docs")))
                    return new[] { "docs" };
                if (Directory.Exists(CombineRelative(rootDirectory, "src")))
                    return new[] { "src" };
                return new[] { "." };
            }

            case SiteFramework.Hugo:
            {
                string? configText = ReadFirstExistingText(rootDirectory, spec.ConfigCandidates, out _);
                string? contentDir = ExtractSimpleKeyValue(configText, "contentDir");
                if (!string.IsNullOrWhiteSpace(contentDir))
                {
                    string normalized = NormalizeRelativePath(contentDir!);
                    if (DirectoryExistsAt(rootDirectory, normalized))
                        return new[] { normalized };
                }
                return Directory.Exists(CombineRelative(rootDirectory, "content")) ? new[] { "content" } : new[] { "." };
            }

            case SiteFramework.Hexo:
            {
                string? configText = ReadFirstExistingText(rootDirectory, spec.ConfigCandidates, out _);
                string sourceDir = NormalizeRelativePath(ExtractSimpleKeyValue(configText, "source_dir") ?? "source");
                string postsDir = $"{sourceDir}/_posts";
                if (DirectoryExistsAt(rootDirectory, postsDir))
                    return new[] { postsDir };
                if (DirectoryExistsAt(rootDirectory, sourceDir))
                    return new[] { sourceDir };
                return new[] { "." };
            }

            case SiteFramework.Jekyll:
                return Directory.Exists(CombineRelative(rootDirectory, "_posts")) ? new[] { "_posts" } : new[] { "." };

            case SiteFramework.MkDocs:
            {
                string? configText = ReadFirstExistingText(rootDirectory, spec.ConfigCandidates, out _);
                string docsDir = NormalizeRelativePath(ExtractSimpleKeyValue(configText, "docs_dir") ?? "docs");
                return DirectoryExistsAt(rootDirectory, docsDir) ? new[] { docsDir } : new[] { "." };
            }

            case SiteFramework.MdBook:
            {
                string? configText = ReadFirstExistingText(rootDirectory, spec.ConfigCandidates, out _);
                string srcDir = NormalizeRelativePath(ExtractSimpleKeyValue(configText, "src") ?? "src");
                return DirectoryExistsAt(rootDirectory, srcDir) ? new[] { srcDir } : new[] { "." };
            }

            case SiteFramework.Docusaurus:
            {
                var roots = new List<string>();
                if (Directory.Exists(CombineRelative(rootDirectory, "docs")))
                    roots.Add("docs");
                if (Directory.Exists(CombineRelative(rootDirectory, "blog")))
                    roots.Add("blog");
                return roots.Count > 0 ? roots.ToArray() : new[] { "." };
            }

            case SiteFramework.Astro:
            {
                if (Directory.Exists(CombineRelative(rootDirectory, "src/content")))
                    return new[] { "src/content" };
                if (Directory.Exists(CombineRelative(rootDirectory, "src/pages")))
                    return new[] { "src/pages" };
                return new[] { "." };
            }

            case SiteFramework.Docsify:
            {
                if (File.Exists(CombineRelative(rootDirectory, "docs/index.html")) ||
                    File.Exists(CombineRelative(rootDirectory, "docs/_sidebar.md")))
                    return new[] { "docs" };
                return new[] { "." };
            }

            case SiteFramework.NextJs:
            {
                var found = spec.DefaultContentRoots
                    .Where(r => DirectoryExistsAt(rootDirectory, r) && ContainsMarkdownShallow(CombineRelative(rootDirectory, r), extensions))
                    .ToArray();
                if (found.Length > 0)
                    return found;
                break;
            }
        }

        var defaults = spec.DefaultContentRoots
            .Where(r => r != "." && DirectoryExistsAt(rootDirectory, r) && !ignoredRelPaths.Contains(r))
            .ToArray();
        if (defaults.Length > 0)
        {
            if (framework == SiteFramework.Generic)
            {
                var withMd = defaults.Where(r => ContainsMarkdownShallow(CombineRelative(rootDirectory, r), extensions)).ToArray();
                if (withMd.Length > 0)
                    return withMd;
            }
            return new[] { defaults[0] };
        }

        // Generic fallback: keep root
        _ = ignoredDirNames;
        return new[] { "." };
    }

    private static string DetectTheme(string rootDirectory, SiteFramework framework, JsonDocument? packageJson)
    {
        switch (framework)
        {
            case SiteFramework.Jekyll:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, new[] { "_config.yml", "_config.yaml" }, out _);
                string? gemfile = ReadFirstExistingText(rootDirectory, new[] { "Gemfile" }, out _);
                string? themeVal = ExtractSimpleKeyValue(cfg, "theme") ?? ExtractSimpleKeyValue(cfg, "remote_theme");
                if ((themeVal?.Contains("chirpy", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (gemfile?.Contains("jekyll-theme-chirpy", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    Directory.Exists(CombineRelative(rootDirectory, "_tabs")))
                    return "Chirpy";
                return !string.IsNullOrWhiteSpace(themeVal) ? themeVal! : "default";
            }

            case SiteFramework.Hexo:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, new[] { "_config.yml", "_config.yaml" }, out _);
                string? themeVal = ExtractSimpleKeyValue(cfg, "theme");
                return !string.IsNullOrWhiteSpace(themeVal) ? themeVal! : "default";
            }

            case SiteFramework.Hugo:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, Specs[SiteFramework.Hugo].ConfigCandidates, out _);
                string? themeVal = ExtractSimpleKeyValue(cfg, "theme");
                if (!string.IsNullOrWhiteSpace(themeVal))
                    return themeVal!;
                string themesDir = CombineRelative(rootDirectory, "themes");
                if (Directory.Exists(themesDir))
                {
                    try
                    {
                        var dirs = Directory.GetDirectories(themesDir);
                        if (dirs.Length == 1)
                            return Path.GetFileName(dirs[0]);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return "default";
            }

            case SiteFramework.VuePress:
            {
                string? dep = FindPackageDep(packageJson, "vuepress-theme-hope", "vuepress-theme-plume", "vuepress-theme-reco", "@vuepress/theme-default")
                              ?? FindPackageDepByPrefix(packageJson, "vuepress-theme-");
                return dep ?? "default";
            }

            case SiteFramework.VitePress:
            {
                string? dep = FindPackageDep(packageJson, "@sugarat/theme", "vitepress-theme-teek", "vitepress-theme-demoblock")
                              ?? FindPackageDepByPrefix(packageJson, "vitepress-theme-");
                return dep ?? "default";
            }

            case SiteFramework.MkDocs:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, new[] { "mkdocs.yml", "mkdocs.yaml" }, out _);
                if (!string.IsNullOrWhiteSpace(cfg))
                {
                    var m = Regex.Match(cfg, @"(?m)^theme:\s*(?:\r?\n\s+name:\s*['""]?([^\s'""#]+)|['""]?([^\s'""#]+))");
                    if (m.Success)
                        return !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                }
                return "default";
            }

            case SiteFramework.Astro:
            {
                string? dep = FindPackageDep(packageJson, "@astrojs/starlight");
                return dep ?? "default";
            }

            case SiteFramework.NextJs:
            {
                string? dep = FindPackageDep(packageJson, "nextra-theme-docs", "nextra-theme-blog", "nextra");
                return dep ?? "default";
            }

            default:
                return "default";
        }
    }

    private static SiteProfileNavigation DetectNavigation(
        string rootDirectory,
        SiteFramework framework,
        string[] contentRoots,
        JsonDocument? packageJson,
        Dictionary<string, object?> hints)
    {
        var indexFiles = new List<string>();
        string mode = "auto";
        string? note = null;

        switch (framework)
        {
            case SiteFramework.MdBook:
            {
                string summary = NormalizeRelativePath($"{contentRoots[0]}/SUMMARY.md");
                if (File.Exists(CombineRelative(rootDirectory, summary)))
                    indexFiles.Add(summary);
                mode = "manual";
                note = "新增章节需要在 SUMMARY.md 中登记链接才会出现在目录中。";
                break;
            }

            case SiteFramework.Docsify:
            {
                string sidebar = NormalizeRelativePath($"{contentRoots[0]}/_sidebar.md");
                if (File.Exists(CombineRelative(rootDirectory, sidebar)))
                {
                    indexFiles.Add(sidebar);
                    mode = "manual";
                    note = "新增页面通常需要在 _sidebar.md 中添加导航项。";
                }
                break;
            }

            case SiteFramework.MkDocs:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, new[] { "mkdocs.yml", "mkdocs.yaml" }, out string? cfgRel);
                if (cfgRel is not null)
                    indexFiles.Add(cfgRel);
                if (!string.IsNullOrWhiteSpace(cfg) && Regex.IsMatch(cfg, @"(?m)^nav\s*:"))
                {
                    mode = "manual";
                    note = "mkdocs.yml 中定义了显式 nav 导航树，新增文档需同步补充条目。";
                }
                break;
            }

            case SiteFramework.VitePress:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, Specs[SiteFramework.VitePress].ConfigCandidates, out string? cfgRel);
                if (cfgRel is not null)
                    indexFiles.Add(cfgRel);
                bool hasAutoPlugin = FindPackageDep(packageJson, "vitepress-sidebar", "vitepress-plugin-auto-sidebar", "@sugarat/theme") is not null ||
                                     (cfg?.Contains("generateSidebar", StringComparison.OrdinalIgnoreCase) ?? false);
                bool hasManualSidebar = cfg is not null && Regex.IsMatch(cfg, @"\bsidebar\s*:");
                mode = hasAutoPlugin ? (hasManualSidebar ? "mixed" : "auto") : (hasManualSidebar ? "manual" : "auto");
                if (mode == "manual")
                    note = "VitePress 配置包含手动 sidebar，新增页面可能需同步更新侧边栏。";
                break;
            }

            case SiteFramework.VuePress:
            {
                string? cfg = ReadFirstExistingText(rootDirectory, Specs[SiteFramework.VuePress].ConfigCandidates, out string? cfgRel);
                if (cfgRel is not null)
                    indexFiles.Add(cfgRel);
                bool isHope = FindPackageDep(packageJson, "vuepress-theme-hope") is not null;
                bool hasStructureSidebar = cfg is not null && cfg.Contains("\"structure\"", StringComparison.OrdinalIgnoreCase);
                bool hasManualSidebar = cfg is not null && Regex.IsMatch(cfg, @"\bsidebar\s*:");
                if (isHope || hasStructureSidebar)
                    mode = hasManualSidebar && !hasStructureSidebar ? "mixed" : "auto";
                else if (hasManualSidebar)
                    mode = "manual";
                if (mode == "manual")
                    note = "VuePress 配置包含手动 sidebar，新增页面可能需同步更新侧边栏。";
                break;
            }

            case SiteFramework.Docusaurus:
            {
                string? sb = ReadFirstExistingText(rootDirectory, new[] { "sidebars.ts", "sidebars.js", "sidebars.cjs" }, out string? sbRel);
                if (sbRel is not null)
                    indexFiles.Add(sbRel);
                if (sb is not null)
                {
                    bool auto = sb.Contains("autogenerated", StringComparison.OrdinalIgnoreCase);
                    mode = auto ? "auto" : "manual";
                }
                break;
            }

            case SiteFramework.Rspress:
            case SiteFramework.NextJs:
            {
                foreach (string root in contentRoots)
                {
                    foreach (string meta in new[] { "_meta.json", "_meta.ts", "_meta.js" })
                    {
                        string rel = NormalizeRelativePath($"{root}/{meta}");
                        if (File.Exists(CombineRelative(rootDirectory, rel)))
                            indexFiles.Add(rel);
                    }
                }
                mode = indexFiles.Count > 0 ? "mixed" : "auto";
                break;
            }

            case SiteFramework.Hugo:
            case SiteFramework.Hexo:
            case SiteFramework.Jekyll:
            case SiteFramework.Quartz:
            case SiteFramework.Astro:
            case SiteFramework.Nuxt:
                mode = "auto";
                break;
        }

        _ = hints;
        return new SiteProfileNavigation(mode, indexFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), note);
    }

    private static void PopulateFrameworkSpecificHints(
        string rootDirectory,
        SiteFramework framework,
        string theme,
        string[] contentRoots,
        JsonDocument? packageJson,
        bool postAssetFolder,
        Dictionary<string, object?> hints)
    {
        switch (framework)
        {
            case SiteFramework.Hexo:
                hints["postAssetFolder"] = postAssetFolder;
                break;

            case SiteFramework.Quartz:
                hints["wikilinks"] = true;
                hints["obsidianEmbeds"] = true;
                break;

            case SiteFramework.Astro:
            {
                string contentDir = CombineRelative(rootDirectory, "src/content");
                if (Directory.Exists(contentDir))
                {
                    try
                    {
                        string[] collections = Directory.GetDirectories(contentDir)
                            .Select(Path.GetFileName)
                            .Where(n => !string.IsNullOrWhiteSpace(n) && !n!.StartsWith('.'))
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToArray()!;
                        if (collections.Length > 0)
                            hints["collections"] = collections;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                foreach (string schemaFile in new[] { "src/content.config.ts", "src/content/config.ts" })
                {
                    if (File.Exists(CombineRelative(rootDirectory, schemaFile)))
                    {
                        hints["contentSchemaFile"] = schemaFile;
                        break;
                    }
                }
                break;
            }

            case SiteFramework.Jekyll:
            {
                if (Directory.Exists(CombineRelative(rootDirectory, "_tabs")))
                    hints["excludedSpecialDirs"] = new[] { "_tabs" };
                string? cfg = ReadFirstExistingText(rootDirectory, new[] { "_config.yml", "_config.yaml" }, out _);
                string? tz = ExtractSimpleKeyValue(cfg, "timezone");
                if (!string.IsNullOrWhiteSpace(tz))
                    hints["timezone"] = tz;
                break;
            }

            case SiteFramework.VuePress:
            {
                if (theme.Contains("hope", StringComparison.OrdinalIgnoreCase))
                    hints["supportsDirMeta"] = true;
                if (theme.Contains("plume", StringComparison.OrdinalIgnoreCase))
                    hints["isPlume"] = true;
                break;
            }
        }

        _ = contentRoots;
        _ = packageJson;
    }

    private static string? DetectScaffoldPath(string rootDirectory, SiteFramework framework)
    {
        string[] candidates = framework switch
        {
            SiteFramework.Hexo => new[] { "scaffolds/post.md" },
            SiteFramework.Hugo => new[] { "archetypes/posts.md", "archetypes/default.md" },
            _ => Array.Empty<string>()
        };
        foreach (string rel in candidates)
        {
            if (File.Exists(CombineRelative(rootDirectory, rel)))
                return rel;
        }
        return null;
    }

    private static bool DetectHexoPostAssetFolder(string rootDirectory)
    {
        string? cfg = ReadFirstExistingText(rootDirectory, new[] { "_config.yml", "_config.yaml" }, out _);
        string? val = ExtractSimpleKeyValue(cfg, "post_asset_folder");
        return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static SiteProfileAssets BuildAssetsProfile(
        string rootDirectory,
        FrameworkSpec spec,
        Dictionary<string, int> refModes,
        bool postAssetFolder,
        SiteFramework framework,
        bool detectedMediaSubpath)
    {
        var detectedDirs = new List<string>();
        foreach (string rel in spec.DefaultAssetCandidates)
        {
            if (DirectoryExistsAt(rootDirectory, rel))
                detectedDirs.Add(rel);
        }

        string mode;
        if (postAssetFolder)
            mode = "post-asset-folder";
        else if (detectedMediaSubpath)
            mode = "shared-dir";
        else if (refModes.Count > 0)
            mode = SelectDominant(refModes, detectedDirs.Count > 0 ? "shared-dir" : "relative-assets");
        else
            mode = detectedDirs.Count > 0 ? "shared-dir" : "relative-assets";

        string preferredDir = detectedDirs.Count > 0
            ? detectedDirs[0]
            : (framework == SiteFramework.Jekyll ? "assets/img/posts" : "assets");

        return new SiteProfileAssets(mode, preferredDir, detectedDirs.ToArray(), postAssetFolder);
    }

    private static bool ShouldIgnoreDirectory(
        string dirName,
        string relPath,
        SiteFramework framework,
        HashSet<string> ignoredDirNames,
        HashSet<string> ignoredRelPaths)
    {
        if (dirName.StartsWith('.') || ignoredDirNames.Contains(dirName))
            return true;
        if (ignoredRelPaths.Contains(relPath))
            return true;

        // Ignore static asset subdirectories inside content trees (e.g., docs/public, docs/assets, content/images)
        if (GlobalStaticAssetDirNames.Contains(dirName))
            return true;

        if (framework == SiteFramework.Jekyll && dirName.StartsWith('_'))
            return true;

        return false;
    }

    private static bool IsLeafPageBundle(
        string directoryPath,
        SiteFramework framework,
        HashSet<string> extensions,
        out string? indexFilePath)
    {
        indexFilePath = null;
        if (framework is not (SiteFramework.Hugo or SiteFramework.Hexo or SiteFramework.Astro))
            return false;

        try
        {
            string indexMd = Path.Combine(directoryPath, "index.md");
            if (!File.Exists(indexMd))
                return false;

            // A leaf bundle has index.md and no child subdirectories containing articles
            var subDirs = Directory.GetDirectories(directoryPath);
            if (subDirs.Length > 0)
                return false;

            int mdCount = Directory.GetFiles(directoryPath)
                .Count(f => extensions.Contains(Path.GetExtension(f)));
            if (mdCount == 1)
            {
                indexFilePath = indexMd;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool IsSpecialIndexOrMetaMarkdown(string fileName, SiteFramework framework, string relDir)
    {
        if (fileName.StartsWith('_'))
        {
            // Hugo _index.md, Docsify _sidebar.md / _navbar.md / _coverpage.md
            return true;
        }
        if (relDir == "." && (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
                              fileName.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase) ||
                              fileName.Equals("LICENSE.md", StringComparison.OrdinalIgnoreCase) ||
                              fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
                              fileName.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)))
        {
            return framework is not (SiteFramework.Docsify or SiteFramework.VitePress or SiteFramework.VuePress);
        }
        if (framework == SiteFramework.MdBook && fileName.Equals("SUMMARY.md", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string DetectDirectoryTitle(string fullDir, string relDir, SiteFramework framework)
    {
        if (framework == SiteFramework.Docusaurus)
        {
            string catJson = Path.Combine(fullDir, "_category_.json");
            if (File.Exists(catJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(catJson, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                    {
                        string? text = label.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            return text!;
                    }
                }
                catch { }
            }
        }

        string[] indexCandidates = framework == SiteFramework.Hugo
            ? new[] { "_index.md", "index.md", "README.md" }
            : new[] { "README.md", "index.md", "_index.md" };

        foreach (string name in indexCandidates)
        {
            string p = Path.Combine(fullDir, name);
            if (!File.Exists(p))
                continue;
            var header = ParseMarkdownHeader(p);
            if (!string.IsNullOrWhiteSpace(header.Title))
                return header.Title!;
        }

        return relDir == "." ? "根目录" : Path.GetFileName(fullDir);
    }

    private sealed record ParsedMarkdownHeader(
        string? Title,
        string FrontmatterFormat,
        List<string> Keys,
        List<string[]> Categories,
        List<string> Tags,
        List<string> ImageRefs,
        string? DateSample,
        bool HasMediaSubpath,
        bool HasAbbrlink);

    private static ParsedMarkdownHeader ParseMarkdownHeader(string filePath)
    {
        var keys = new List<string>();
        var categories = new List<string[]>();
        var tags = new List<string>();
        var images = new List<string>();
        string? title = null;
        string? dateSample = null;
        string format = "none";
        bool hasMediaSubpath = false;
        bool hasAbbrlink = false;

        string content = ReadHeadText(filePath, MaxHeaderReadBytes);
        if (content.Length == 0)
            return new ParsedMarkdownHeader(null, format, keys, categories, tags, images, null, false, false);

        content = content.Replace("\r\n", "\n");
        string body = content;

        if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                format = "yaml";
                string fm = content[4..end];
                int afterDelim = end + 4;
                body = afterDelim < content.Length ? content[afterDelim..] : "";
                ParseYamlFrontmatter(fm, keys, categories, tags, ref title, ref dateSample, ref hasMediaSubpath, ref hasAbbrlink);
            }
        }
        else if (content.StartsWith("+++\n", StringComparison.Ordinal))
        {
            int end = content.IndexOf("\n+++", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                format = "toml";
                string fm = content[4..end];
                int afterDelim = end + 4;
                body = afterDelim < content.Length ? content[afterDelim..] : "";
                ParseTomlFrontmatter(fm, keys, categories, tags, ref title, ref dateSample);
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            var h1 = Heading1Regex.Match(body);
            if (h1.Success)
                title = h1.Groups[1].Value.Trim();
        }

        foreach (Match m in MarkdownImageRegex.Matches(body))
        {
            string target = m.Groups[1].Value.Trim();
            if (target.Length > 0 && !IsExternalUrl(target))
                images.Add(target);
        }
        foreach (Match m in ObsidianEmbedRegex.Matches(body))
            images.Add(m.Groups[1].Value.Trim());
        foreach (Match m in HtmlImgRegex.Matches(body))
        {
            string target = m.Groups[1].Value.Trim();
            if (target.Length > 0 && !IsExternalUrl(target))
                images.Add(target);
        }

        return new ParsedMarkdownHeader(title, format, keys, categories, tags, images, dateSample, hasMediaSubpath, hasAbbrlink);
    }

    private static void ParseYamlFrontmatter(
        string yaml,
        List<string> keys,
        List<string[]> categories,
        List<string> tags,
        ref string? title,
        ref string? dateSample,
        ref bool hasMediaSubpath,
        ref bool hasAbbrlink)
    {
        string[] lines = yaml.Split('\n');
        string? currentListKey = null;
        var currentListItems = new List<string>();

        void FlushList()
        {
            if (currentListKey is null || currentListItems.Count == 0)
                return;
            if (currentListKey is "categories" or "category")
                categories.Add(currentListItems.ToArray());
            else if (currentListKey is "tags" or "tag")
                tags.AddRange(currentListItems);
            currentListKey = null;
            currentListItems.Clear();
        }

        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            bool isIndented = rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t');
            if (currentListKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                string item = UnquoteScalar(trimmed[2..].Trim());
                if (item.Length > 0)
                    currentListItems.Add(item);
                continue;
            }

            if (isIndented)
                continue;

            FlushList();

            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = rawLine[..colon].Trim();
            string value = rawLine[(colon + 1)..].Trim();
            if (key.Length == 0 || key.Contains(' '))
                continue;

            keys.Add(key);
            string lowerKey = key.ToLowerInvariant();
            if (lowerKey is "media_subpath" or "img_path")
                hasMediaSubpath = true;
            else if (lowerKey == "abbrlink")
                hasAbbrlink = true;

            if (lowerKey == "title" && title is null && value.Length > 0)
            {
                title = UnquoteScalar(value);
            }
            else if (lowerKey == "date" && dateSample is null && value.Length > 0)
            {
                dateSample = UnquoteScalar(value);
            }
            else if (lowerKey is "categories" or "category")
            {
                if (value.Length == 0)
                {
                    currentListKey = lowerKey;
                }
                else
                {
                    string[] items = ParseInlineArrayOrScalar(value);
                    if (items.Length > 0)
                        categories.Add(items);
                }
            }
            else if (lowerKey is "tags" or "tag")
            {
                if (value.Length == 0)
                {
                    currentListKey = lowerKey;
                }
                else
                {
                    tags.AddRange(ParseInlineArrayOrScalar(value));
                }
            }
        }

        FlushList();
    }

    private static void ParseTomlFrontmatter(
        string toml,
        List<string> keys,
        List<string[]> categories,
        List<string> tags,
        ref string? title,
        ref string? dateSample)
    {
        foreach (string rawLine in toml.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0 || key.Contains(' '))
                continue;

            keys.Add(key);
            string lowerKey = key.ToLowerInvariant();
            if (lowerKey == "title" && title is null)
                title = UnquoteScalar(value);
            else if (lowerKey == "date" && dateSample is null)
                dateSample = UnquoteScalar(value);
            else if (lowerKey is "categories" or "category")
            {
                string[] items = ParseInlineArrayOrScalar(value);
                if (items.Length > 0)
                    categories.Add(items);
            }
            else if (lowerKey is "tags" or "tag")
            {
                tags.AddRange(ParseInlineArrayOrScalar(value));
            }
        }
    }

    private static string[] ParseInlineArrayOrScalar(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            string inner = trimmed[1..^1];
            return inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => UnquoteScalar(s.Trim()))
                .Where(s => s.Length > 0)
                .ToArray();
        }
        string scalar = UnquoteScalar(trimmed);
        return scalar.Length > 0 ? new[] { scalar } : Array.Empty<string>();
    }

    private static string UnquoteScalar(string text)
    {
        string s = text.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1].Trim();
        return s;
    }

    private static string ClassifyFileName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (DateSlugRegex.IsMatch(stem))
            return $"YYYY-MM-DD-slug{ext}";
        if (NumberedSlugRegex.IsMatch(stem))
            return $"numbered-slug{ext}";
        if (CjkRegex.IsMatch(stem))
            return $"chinese-or-unicode{ext}";
        if (KebabSlugRegex.IsMatch(stem))
            return $"kebab-case{ext}";
        return "mixed";
    }

    private static string ClassifyImageReference(string imgRef, bool isPageBundle, bool postAssetFolder)
    {
        if (isPageBundle && !imgRef.StartsWith('/') && !imgRef.StartsWith("../", StringComparison.Ordinal))
            return "page-bundle";
        if (postAssetFolder && !imgRef.StartsWith('/'))
            return "post-asset-folder";
        if (imgRef.StartsWith('/'))
            return "shared-dir";
        return "relative-assets";
    }

    private static string BuildTemplateFromKeys(string format, List<string> keys, string? dateSample)
    {
        var sb = new StringBuilder();
        string delim = format == "toml" ? "+++" : "---";
        sb.Append(delim).Append('\n');
        foreach (string key in keys.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            string lower = key.ToLowerInvariant();
            if (format == "toml")
            {
                string val = lower switch
                {
                    "date" => $"\"{dateSample ?? "2026-01-01T00:00:00+08:00"}\"",
                    "categories" or "tags" => "[]",
                    "draft" => "false",
                    _ => "\"\""
                };
                sb.Append(key).Append(" = ").Append(val).Append('\n');
            }
            else
            {
                string val = lower switch
                {
                    "date" => dateSample ?? "2026-01-01 00:00:00 +0800",
                    "categories" or "tags" => "[]",
                    "draft" => "false",
                    _ => "\"\""
                };
                sb.Append(key).Append(": ").Append(val).Append('\n');
            }
        }
        sb.Append(delim);
        return sb.ToString();
    }

    private static string BuildFinalTemplate(
        string rootDirectory,
        string? scaffoldPath,
        string fmFormat,
        string[] commonKeys,
        string? detectedSampleTemplate,
        string? dateSample,
        SiteFramework framework,
        string theme)
    {
        if (scaffoldPath is not null)
        {
            string scaffoldFull = CombineRelative(rootDirectory, scaffoldPath);
            string text = ReadHeadText(scaffoldFull, 2048).Replace("\r\n", "\n").Trim();
            if (text.Length > 0)
                return text;
        }
        if (!string.IsNullOrWhiteSpace(detectedSampleTemplate))
            return detectedSampleTemplate!;
        if (commonKeys.Length > 0 && fmFormat != "none")
            return BuildTemplateFromKeys(fmFormat, commonKeys.ToList(), dateSample);

        if (framework == SiteFramework.Jekyll && string.Equals(theme, "Chirpy", StringComparison.OrdinalIgnoreCase))
        {
            return "---\ntitle: \"\"\ndate: 2026-01-01 00:00:00 +0800\ncategories: []\ntags: []\n---";
        }
        if (fmFormat == "none")
            return "";
        return "---\ntitle: \"\"\n---";
    }

    private static bool IsOverwrittenByPullPaths(string relDir, string[] normalizedPullPaths)
    {
        if (normalizedPullPaths.Length == 0 || relDir == ".")
            return false;
        foreach (string pull in normalizedPullPaths)
        {
            if (string.Equals(relDir, pull, StringComparison.OrdinalIgnoreCase) ||
                relDir.StartsWith(pull + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string[] NormalizeRelativePaths(string[]? paths)
    {
        if (paths is null || paths.Length == 0)
            return Array.Empty<string>();
        return paths
            .Select(NormalizeRelativePath)
            .Where(p => p.Length > 0 && p != ".")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ReadUserIgnoredDirectories(JsonObject userRules)
    {
        if (userRules["ignoredDirectories"] is not JsonArray arr)
            yield break;
        foreach (var item in arr)
        {
            if (item is JsonValue val && val.TryGetValue<string>(out var text))
            {
                string norm = NormalizeRelativePath(text);
                if (norm.Length > 0 && norm != ".")
                    yield return norm;
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        string s = path.Trim().Replace('\\', '/').Trim('/');
        if (s.StartsWith("./", StringComparison.Ordinal))
            s = s[2..];
        return s.Length == 0 ? "." : s;
    }

    private static string CombineRelative(string rootDirectory, string relativePath)
    {
        string norm = NormalizeRelativePath(relativePath);
        return norm == "." ? rootDirectory : Path.Combine(rootDirectory, norm.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool DirectoryExistsAt(string rootDirectory, string relativePath) =>
        Directory.Exists(CombineRelative(rootDirectory, relativePath));

    private static bool ContainsMarkdownShallow(string directory, HashSet<string> extensions)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                    return true;
            }
            foreach (string sub in Directory.EnumerateDirectories(directory).Take(12))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || GlobalIgnoredDirNames.Contains(name))
                    continue;
                foreach (string file in Directory.EnumerateFiles(sub))
                {
                    if (extensions.Contains(Path.GetExtension(file)))
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static JsonDocument? TryReadPackageJson(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, "package.json");
        if (!File.Exists(path))
            return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxConfigReadBytes)
                return null;
            return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch { return null; }
    }

    private static string? FindPackageDep(JsonDocument? packageJson, params string[] names)
    {
        if (packageJson is null || packageJson.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        foreach (string section in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (!packageJson.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                continue;
            foreach (string name in names)
            {
                if (deps.TryGetProperty(name, out _))
                    return name;
            }
        }
        return null;
    }

    private static string? FindPackageDepByPrefix(JsonDocument? packageJson, string prefix)
    {
        if (packageJson is null || packageJson.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        foreach (string section in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (!packageJson.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var prop in deps.EnumerateObject())
            {
                if (prop.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return prop.Name;
            }
        }
        return null;
    }

    private static string? ReadFirstExistingText(string rootDirectory, string[] candidates, out string? foundRelPath)
    {
        foreach (string rel in candidates)
        {
            string full = CombineRelative(rootDirectory, rel);
            if (!File.Exists(full))
                continue;
            try
            {
                var info = new FileInfo(full);
                if (info.Length <= MaxConfigReadBytes)
                {
                    foundRelPath = rel;
                    return File.ReadAllText(full, Encoding.UTF8);
                }
            }
            catch { }
        }
        foundRelPath = null;
        return null;
    }

    private static string? ExtractSimpleKeyValue(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = Regex.Match(text, $@"(?m)^\s*{Regex.Escape(key)}\s*[:=]\s*['""]?([^'""#\r\n]+?)['""]?\s*(?:#.*)?$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractJsOrTsStringOption(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = Regex.Match(text, $@"\b{Regex.Escape(key)}\s*:\s*['""]([^'""]+)['""]");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string ReadHeadText(string filePath, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int toRead = (int)Math.Min(stream.Length, maxBytes);
            if (toRead <= 0)
                return "";
            byte[] buffer = new byte[toRead];
            int total = 0;
            while (total < toRead)
            {
                int read = stream.Read(buffer, total, toRead - total);
                if (read == 0) break;
                total += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, total).TrimStart('\uFEFF');
        }
        catch { return ""; }
    }

    private static DateTime SafeGetLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static bool IsExternalUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
    }

    private static string SelectDominant(Dictionary<string, int> counts, string fallback)
    {
        if (counts.Count == 0)
            return fallback;
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
    }

    private static string[] TopKeys(Dictionary<string, int> counts, int maxCount) =>
        counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .Take(maxCount)
            .ToArray();
}

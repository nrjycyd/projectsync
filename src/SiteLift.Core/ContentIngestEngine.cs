using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace SiteLift.Core;

public sealed record DroppedLocalAsset(
    string OriginalRef,
    string Kind, // "markdown" | "obsidian" | "html"
    string ResolvedSourcePath,
    string FileName,
    bool Exists,
    long FileSize);

public sealed record DroppedDocumentInfo(
    string SourceFilePath,
    string OriginalFileName,
    string FullRawText,
    bool HasFrontmatter,
    string ExistingFrontmatterFormat,
    string ExistingFrontmatterRaw,
    string BodyText,
    string DetectedTitle,
    string[] Headings,
    string BodyExcerpt,
    string[] ExistingCategories,
    string[] ExistingTags,
    DroppedLocalAsset[] LocalAssets);

public sealed record PlannedAssetTransfer(
    string OriginalRef,
    string Kind,
    string SourcePath,
    string DestinationRelativePath,
    string DestinationFullPath,
    string RewrittenMarkdownRef,
    bool AlreadyExists);

public sealed record NavigationPatchPlan(
    string IndexFileRelativePath,
    string IndexFileFullPath,
    string LineToAppend,
    string DiffPreview,
    bool AlreadyPresent,
    bool CanAutoApply);

public sealed record ContentIngestPlan(
    string SourceMode, // "ai" | "heuristic"
    string TargetDirectory,
    string[] CandidateDirectories,
    string[] AllowedDirectories,
    string Reason,
    string SuggestedFileName,
    string TargetRelativePath,
    bool WillOverwriteExisting,
    bool ShouldInjectFrontmatter,
    string SuggestedFrontmatter,
    string[] SuggestedCategories,
    string[] SuggestedTags,
    PlannedAssetTransfer[] AssetTransfers,
    string[] MissingAssetRefs,
    string NavigationAdvice,
    string? WarningNote,
    NavigationPatchPlan? NavigationPatch = null);

public sealed record ContentIngestRequest(
    string TargetDirectory,
    string TargetFileName,
    bool ApplyFrontmatter,
    string FrontmatterText,
    bool CopyLocalAssets,
    bool AllowOverwrite,
    PlannedAssetTransfer[] AssetTransfers,
    bool ApplyNavigationPatch = false,
    NavigationPatchPlan? NavigationPatch = null);

public sealed record ContentIngestResult(
    string TargetFullPath,
    string TargetRelativePath,
    int CopiedAssetsCount,
    int SkippedAssetsCount,
    bool OverwroteExisting,
    bool NavigationIndexUpdated = false,
    string? UpdatedNavigationFile = null);

public static class ContentIngestEngine
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SupportedDropExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".mdx", ".markdown"
    };

    private static readonly Regex HeadingRegex = new(@"^\s{0,3}(#{1,3})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownImageRegex = new(@"!\[(?<alt>[^\]]*)\]\((?<target><[^>]+>|[^)\s]+)(?<title>\s+""[^""]*"")?\)", RegexOptions.Compiled);
    private static readonly Regex ObsidianEmbedRegex = new(@"!\[\[(?<target>[^\]|]+\.(?:png|jpe?g|gif|webp|svg|avif|bmp))(?:\|(?<alias>[^\]]*))?\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlImgRegex = new(@"(?<prefix><img\b[^>]*\bsrc\s*=\s*)(?<quote>[""'])(?<target>[^""']+)\k<quote>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DatePrefixRegex = new(@"^\d{4}-\d{2}-\d{2}-", RegexOptions.Compiled);
    private static readonly Regex TokenSplitRegex = new(@"[\s/\\_\-.,;:!?()\[\]{}""'""''《》【】（）·、，。！？]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string[] DiscoverMarkdownInputs(IEnumerable<string> droppedPaths)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            string path = raw.Trim();
            try
            {
                if (File.Exists(path))
                {
                    if (SupportedDropExtensions.Contains(Path.GetExtension(path)) && seen.Add(path))
                        results.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    var topMd = Directory.GetFiles(path)
                        .Where(f => SupportedDropExtensions.Contains(Path.GetExtension(f)))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (topMd.Length > 0)
                    {
                        foreach (string f in topMd)
                            if (seen.Add(f)) results.Add(f);
                    }
                    else
                    {
                        foreach (string sub in Directory.GetDirectories(path).Take(16))
                        {
                            string name = Path.GetFileName(sub);
                            if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (string f in Directory.GetFiles(sub)
                                         .Where(f => SupportedDropExtensions.Contains(Path.GetExtension(f)))
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                            {
                                if (seen.Add(f)) results.Add(f);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        return results.ToArray();
    }

    public static DroppedDocumentInfo InspectDroppedDocument(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            throw new FileNotFoundException($"待归档文档不存在：{sourceFilePath}", sourceFilePath);

        string raw = File.ReadAllText(sourceFilePath, Encoding.UTF8).TrimStart('\uFEFF').Replace("\r\n", "\n");
        string originalFileName = Path.GetFileName(sourceFilePath);
        string sourceDir = Path.GetDirectoryName(sourceFilePath) ?? "";

        bool hasFrontmatter = false;
        string fmFormat = "none";
        string fmRaw = "";
        string body = raw;
        string? fmTitle = null;
        var existingCats = new List<string>();
        var existingTags = new List<string>();

        if (raw.StartsWith("---\n", StringComparison.Ordinal))
        {
            int end = raw.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                int afterEnd = end + 4;
                if (afterEnd < raw.Length && raw[afterEnd] == '\n') afterEnd++;
                hasFrontmatter = true;
                fmFormat = "yaml";
                fmRaw = raw[..afterEnd].TrimEnd('\n');
                body = afterEnd < raw.Length ? raw[afterEnd..] : "";
                ExtractFrontmatterMetadata(raw[4..end], isToml: false, ref fmTitle, existingCats, existingTags);
            }
        }
        else if (raw.StartsWith("+++\n", StringComparison.Ordinal))
        {
            int end = raw.IndexOf("\n+++", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                int afterEnd = end + 4;
                if (afterEnd < raw.Length && raw[afterEnd] == '\n') afterEnd++;
                hasFrontmatter = true;
                fmFormat = "toml";
                fmRaw = raw[..afterEnd].TrimEnd('\n');
                body = afterEnd < raw.Length ? raw[afterEnd..] : "";
                ExtractFrontmatterMetadata(raw[4..end], isToml: true, ref fmTitle, existingCats, existingTags);
            }
        }

        var headings = new List<string>();
        string? firstH1 = null;
        foreach (Match m in HeadingRegex.Matches(body))
        {
            string text = m.Groups[2].Value.Trim();
            if (text.Length == 0) continue;
            if (m.Groups[1].Value.Length == 1 && firstH1 is null)
                firstH1 = text;
            if (headings.Count < 16)
                headings.Add(text);
        }

        string detectedTitle = !string.IsNullOrWhiteSpace(fmTitle)
            ? fmTitle!
            : (!string.IsNullOrWhiteSpace(firstH1)
                ? firstH1!
                : DatePrefixRegex.Replace(Path.GetFileNameWithoutExtension(originalFileName), "").Replace('-', ' ').Trim());
        if (detectedTitle.Length == 0)
            detectedTitle = Path.GetFileNameWithoutExtension(originalFileName);

        string excerpt = body.Trim();
        if (excerpt.Length > 1000)
            excerpt = excerpt[..1000];

        var assets = DiscoverLocalAssets(body, sourceDir);

        return new DroppedDocumentInfo(
            SourceFilePath: sourceFilePath,
            OriginalFileName: originalFileName,
            FullRawText: raw,
            HasFrontmatter: hasFrontmatter,
            ExistingFrontmatterFormat: fmFormat,
            ExistingFrontmatterRaw: fmRaw,
            BodyText: body,
            DetectedTitle: detectedTitle,
            Headings: headings.ToArray(),
            BodyExcerpt: excerpt,
            ExistingCategories: existingCats.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ExistingTags: existingTags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            LocalAssets: assets);
    }

    public static (SiteProfileDocument Profile, bool AutoScannedNow) LoadOrScanProfile(
        string rootDirectory,
        SiteProfileScanOptions? scanOptions = null,
        CancellationToken cancellation = default)
    {
        string profilePath = SiteProfileScanner.GetProfilePath(rootDirectory);
        if (File.Exists(profilePath))
        {
            try
            {
                string json = File.ReadAllText(profilePath, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<SiteProfileDocument>(json, ProfileJsonOptions);
                if (doc is not null && doc.AutoScanned is not null)
                    return (doc, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }

        var scanned = SiteProfileScanner.ScanAndSave(rootDirectory, scanOptions, cancellation);
        return (scanned.Document, true);
    }

    public static ContentIngestPlan BuildHeuristicPlan(
        SiteProfileDocument profile,
        string rootDirectory,
        DroppedDocumentInfo document)
    {
        string[] allowedDirs = GetAllowedDirectories(profile);
        var docTokens = ExtractTokens($"{document.DetectedTitle} {string.Join(' ', document.Headings)} {string.Join(' ', document.ExistingCategories)} {string.Join(' ', document.ExistingTags)} {document.BodyExcerpt[..Math.Min(350, document.BodyExcerpt.Length)]}");
        var routingHints = ReadRoutingHints(profile.UserRules);

        var scored = new List<(string Path, int Score, string Reason)>();
        foreach (string dirPath in allowedDirs)
        {
            var dirInfo = profile.AutoScanned.Directories
                .FirstOrDefault(d => string.Equals(d.Path, dirPath, StringComparison.OrdinalIgnoreCase));

            int score = 0;
            string? matchedHint = null;
            var matchedWords = new List<string>();

            // 1. User routing hints carry highest weight
            foreach (string hint in routingHints)
            {
                if (hint.Contains(dirPath, StringComparison.OrdinalIgnoreCase) ||
                    (dirInfo is not null && dirInfo.Title.Length > 1 && hint.Contains(dirInfo.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    var hintTokens = ExtractTokens(hint);
                    int overlap = hintTokens.Count(t => docTokens.Contains(t));
                    if (overlap > 0)
                    {
                        score += 50 + overlap * 15;
                        matchedHint = hint;
                    }
                }
            }

            // 2. Directory path & title match
            var dirTokens = ExtractTokens($"{dirPath} {dirInfo?.Title ?? ""}");
            foreach (string tok in dirTokens)
            {
                if (docTokens.Contains(tok))
                {
                    score += 18;
                    if (matchedWords.Count < 3) matchedWords.Add(tok);
                }
            }

            // 3. Sample titles match
            if (dirInfo is not null)
            {
                var sampleTokens = ExtractTokens(string.Join(' ', dirInfo.SampleTitles));
                int sampleHits = sampleTokens.Count(t => docTokens.Contains(t));
                score += Math.Min(sampleHits * 6, 24);
                if (dirInfo.ArticleCount > 0)
                    score += 4;
            }

            // 4. Primary posts dir bonus for flat blog frameworks
            if (dirPath.Equals("_posts", StringComparison.OrdinalIgnoreCase) ||
                dirPath.Equals("source/_posts", StringComparison.OrdinalIgnoreCase) ||
                dirPath.Equals("content/posts", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            string reason = matchedHint is not null
                ? $"命中自定义投递规则：{matchedHint}"
                : matchedWords.Count > 0
                    ? $"标题/正文与目录「{dirInfo?.Title ?? dirPath}」关键词（{string.Join("、", matchedWords)}）匹配"
                    : allowedDirs.Length == 1
                        ? $"站点唯一主内容目录（{profile.Framework}）"
                        : $"按 {profile.Framework} 站点画像默认内容目录推荐";

            scored.Add((dirPath, score, reason));
        }

        var ordered = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string bestDir = ordered[0].Path;
        string bestReason = ordered[0].Reason;
        string[] topCandidates = ordered.Select(x => x.Path).Take(3).ToArray();

        string suggestedFileName = SuggestFileName(profile, bestDir, document.OriginalFileName, document.DetectedTitle);
        var (suggestedCats, suggestedTags) = MatchTaxonomyHeuristically(profile, document, docTokens);
        string suggestedFm = BuildSuggestedFrontmatter(profile, document, suggestedFileName, document.DetectedTitle, suggestedCats, suggestedTags, "");

        return FinalizePlan(
            sourceMode: "heuristic",
            profile: profile,
            rootDirectory: rootDirectory,
            document: document,
            targetDirectory: bestDir,
            candidateDirectories: topCandidates,
            allowedDirectories: allowedDirs,
            reason: bestReason,
            suggestedFileName: suggestedFileName,
            suggestedFrontmatter: suggestedFm,
            suggestedCategories: suggestedCats,
            suggestedTags: suggestedTags,
            navigationAdvice: BuildDefaultNavigationAdvice(profile, bestDir, suggestedFileName, document.DetectedTitle),
            warningNote: null);
    }

    public static ContentIngestPlan MergeAiSuggestion(
        ContentIngestPlan heuristicPlan,
        AiRoutingSuggestion ai,
        SiteProfileDocument profile,
        string rootDirectory,
        DroppedDocumentInfo document)
    {
        var allowedLookup = heuristicPlan.AllowedDirectories
            .ToDictionary(d => d, d => d, StringComparer.OrdinalIgnoreCase);

        string? warning = null;
        string targetDir = heuristicPlan.TargetDirectory;
        string normalizedAiDir = NormalizeRelativePath(ai.RecommendedDirectory);
        if (!string.IsNullOrWhiteSpace(ai.RecommendedDirectory) && allowedLookup.TryGetValue(normalizedAiDir, out string? safeDir))
        {
            targetDir = safeDir;
        }
        else if (!string.IsNullOrWhiteSpace(ai.RecommendedDirectory))
        {
            warning = $"AI 推荐的目录「{ai.RecommendedDirectory}」不在安全白名单内，已自动回退至「{targetDir}」。";
        }

        var candidates = new List<string> { targetDir };
        foreach (string alt in ai.AlternativeDirectories)
        {
            string normAlt = NormalizeRelativePath(alt);
            if (allowedLookup.TryGetValue(normAlt, out string? safeAlt) &&
                !candidates.Contains(safeAlt, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(safeAlt);
            }
        }
        foreach (string fallback in heuristicPlan.CandidateDirectories)
        {
            if (candidates.Count >= 3) break;
            if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fallback);
        }

        string fileName = SanitizeSuggestedFileName(ai.SuggestedFileName, profile, heuristicPlan.SuggestedFileName);
        string title = !string.IsNullOrWhiteSpace(ai.Title) ? ai.Title : document.DetectedTitle;

        bool isChirpy = IsChirpy(profile);
        string[] categories = ai.Categories.Length > 0 ? ai.Categories : heuristicPlan.SuggestedCategories;
        if (isChirpy && categories.Length > 2)
            categories = categories.Take(2).ToArray();

        string[] tags = ai.Tags.Length > 0 ? ai.Tags : heuristicPlan.SuggestedTags;
        if (isChirpy)
            tags = tags.Select(t => t.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        string frontmatter = BuildSuggestedFrontmatter(profile, document, fileName, title, categories, tags, ai.Description);
        string navAdvice = !string.IsNullOrWhiteSpace(ai.NavigationAdvice)
            ? ai.NavigationAdvice
            : BuildDefaultNavigationAdvice(profile, targetDir, fileName, title);

        string reason = !string.IsNullOrWhiteSpace(ai.Reason) ? ai.Reason : heuristicPlan.Reason;

        return FinalizePlan(
            sourceMode: "ai",
            profile: profile,
            rootDirectory: rootDirectory,
            document: document,
            targetDirectory: targetDir,
            candidateDirectories: candidates.ToArray(),
            allowedDirectories: heuristicPlan.AllowedDirectories,
            reason: reason,
            suggestedFileName: fileName,
            suggestedFrontmatter: frontmatter,
            suggestedCategories: categories,
            suggestedTags: tags,
            navigationAdvice: navAdvice,
            warningNote: warning);
    }

    public static ContentIngestPlan RebuildPlanForSelection(
        ContentIngestPlan currentPlan,
        SiteProfileDocument profile,
        string rootDirectory,
        DroppedDocumentInfo document,
        string targetDirectory,
        string targetFileName)
    {
        string normDir = NormalizeRelativePath(targetDirectory);
        string safeFile = SanitizeSuggestedFileName(targetFileName, profile, currentPlan.SuggestedFileName);
        string navAdvice = BuildDefaultNavigationAdvice(profile, normDir, safeFile, document.DetectedTitle);

        return FinalizePlan(
            sourceMode: currentPlan.SourceMode,
            profile: profile,
            rootDirectory: rootDirectory,
            document: document,
            targetDirectory: normDir,
            candidateDirectories: currentPlan.CandidateDirectories,
            allowedDirectories: currentPlan.AllowedDirectories,
            reason: currentPlan.Reason,
            suggestedFileName: safeFile,
            suggestedFrontmatter: currentPlan.SuggestedFrontmatter,
            suggestedCategories: currentPlan.SuggestedCategories,
            suggestedTags: currentPlan.SuggestedTags,
            navigationAdvice: navAdvice,
            warningNote: currentPlan.WarningNote);
    }

    public static ContentIngestResult ApplyIngest(
        string rootDirectory,
        DroppedDocumentInfo document,
        ContentIngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException($"站点根目录不存在：{rootDirectory}");

        string targetRel = CombineRelativePath(request.TargetDirectory, ValidateSafeTargetFileName(request.TargetFileName));
        string targetFull = ResolveSafePathInsideRoot(rootDirectory, targetRel);

        if (targetRel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            targetRel.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            targetRel.StartsWith(".sitelift/", StringComparison.OrdinalIgnoreCase) ||
            targetRel.Equals(".sitelift", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不允许写入系统保护目录：{targetRel}");
        }

        bool existed = File.Exists(targetFull);
        if (existed && !request.AllowOverwrite)
            throw new IOException($"目标文件已存在且未允许覆盖：{targetRel}");

        int copiedAssets = 0;
        int skippedAssets = 0;
        var rewrites = new Dictionary<string, (string Kind, string NewRef)>(StringComparer.Ordinal);

        if (request.CopyLocalAssets && request.AssetTransfers.Length > 0)
        {
            foreach (var transfer in request.AssetTransfers)
            {
                if (transfer.FileNameContainsZoneIdentifier())
                    continue;

                string destFull = ResolveSafePathInsideRoot(rootDirectory, transfer.DestinationRelativePath);
                if (!File.Exists(transfer.SourcePath))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                if (File.Exists(destFull) && FilesHaveSameLengthAndTime(transfer.SourcePath, destFull))
                {
                    skippedAssets++;
                }
                else
                {
                    File.Copy(transfer.SourcePath, destFull, overwrite: true);
                    copiedAssets++;
                }

                rewrites[transfer.OriginalRef] = (transfer.Kind, transfer.RewrittenMarkdownRef);
            }
        }

        string bodyToWrite = rewrites.Count > 0
            ? RewriteAssetReferences(document.BodyText, rewrites)
            : document.BodyText;

        string finalMarkdown;
        if (request.ApplyFrontmatter && !string.IsNullOrWhiteSpace(request.FrontmatterText))
        {
            string fm = request.FrontmatterText.Replace("\r\n", "\n").Trim();
            string cleanBody = bodyToWrite.TrimStart('\n');
            finalMarkdown = fm + "\n\n" + cleanBody;
        }
        else if (document.HasFrontmatter)
        {
            string cleanBody = bodyToWrite.TrimStart('\n');
            finalMarkdown = document.ExistingFrontmatterRaw + "\n\n" + cleanBody;
        }
        else
        {
            finalMarkdown = bodyToWrite;
        }

        finalMarkdown = finalMarkdown.Replace("\r\n", "\n");
        if (!finalMarkdown.EndsWith('\n'))
            finalMarkdown += "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
        string tempPath = targetFull + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, finalMarkdown, Utf8NoBom);
            File.Move(tempPath, targetFull, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        bool navUpdated = false;
        string? navFile = null;
        if (request.ApplyNavigationPatch && request.NavigationPatch is { CanAutoApply: true, AlreadyPresent: false } navPatch)
        {
            navUpdated = ApplyNavigationPatch(rootDirectory, navPatch);
            if (navUpdated) navFile = navPatch.IndexFileRelativePath;
        }

        return new ContentIngestResult(
            TargetFullPath: targetFull,
            TargetRelativePath: targetRel,
            CopiedAssetsCount: copiedAssets,
            SkippedAssetsCount: skippedAssets,
            OverwroteExisting: existed,
            NavigationIndexUpdated: navUpdated,
            UpdatedNavigationFile: navFile);
    }

    public static NavigationPatchPlan? BuildNavigationPatch(
        SiteProfileDocument profile,
        string rootDirectory,
        string targetDirectory,
        string targetFileName,
        string title)
    {
        var nav = profile.AutoScanned.Navigation;
        if (nav.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        string targetRel = CombineRelativePath(targetDirectory, targetFileName);
        string safeTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(targetFileName) : title.Trim();

        if (profile.Framework.Equals("mdBook", StringComparison.OrdinalIgnoreCase))
        {
            string summaryRel = nav.IndexFiles.FirstOrDefault(f => f.EndsWith("SUMMARY.md", StringComparison.OrdinalIgnoreCase))
                                ?? CombineRelativePath(profile.AutoScanned.ContentRoots.FirstOrDefault() ?? "src", "SUMMARY.md");
            string summaryFull = ResolveSafePathInsideRoot(rootDirectory, summaryRel);
            if (!File.Exists(summaryFull))
                return null;

            string summaryDir = Path.GetDirectoryName(summaryRel.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
            string relLink = summaryDir.Length > 0 && targetRel.StartsWith(summaryDir + "/", StringComparison.OrdinalIgnoreCase)
                ? targetRel[(summaryDir.Length + 1)..]
                : targetRel;
            string line = $"- [{safeTitle}](./{relLink})";
            string existing = File.ReadAllText(summaryFull, Encoding.UTF8);
            bool already = existing.Contains($"({relLink})", StringComparison.OrdinalIgnoreCase) ||
                           existing.Contains($"(./{relLink})", StringComparison.OrdinalIgnoreCase);
            return new NavigationPatchPlan(
                IndexFileRelativePath: summaryRel,
                IndexFileFullPath: summaryFull,
                LineToAppend: line,
                DiffPreview: $"--- {summaryRel}\n+++ {summaryRel}\n+ {line}",
                AlreadyPresent: already,
                CanAutoApply: true);
        }

        if (profile.Framework.Equals("Docsify", StringComparison.OrdinalIgnoreCase))
        {
            string sidebarRel = nav.IndexFiles.FirstOrDefault(f => f.EndsWith("_sidebar.md", StringComparison.OrdinalIgnoreCase))
                                ?? CombineRelativePath(profile.AutoScanned.ContentRoots.FirstOrDefault() ?? ".", "_sidebar.md");
            string sidebarFull = ResolveSafePathInsideRoot(rootDirectory, sidebarRel);
            if (!File.Exists(sidebarFull))
                return null;

            string sidebarDir = Path.GetDirectoryName(sidebarRel.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
            string relLink = sidebarDir.Length > 0 && targetRel.StartsWith(sidebarDir + "/", StringComparison.OrdinalIgnoreCase)
                ? targetRel[(sidebarDir.Length + 1)..]
                : targetRel;
            string line = $"* [{safeTitle}]({relLink})";
            string existing = File.ReadAllText(sidebarFull, Encoding.UTF8);
            bool already = existing.Contains($"({relLink})", StringComparison.OrdinalIgnoreCase) ||
                           existing.Contains($"(/{relLink})", StringComparison.OrdinalIgnoreCase);
            return new NavigationPatchPlan(
                IndexFileRelativePath: sidebarRel,
                IndexFileFullPath: sidebarFull,
                LineToAppend: line,
                DiffPreview: $"--- {sidebarRel}\n+++ {sidebarRel}\n+ {line}",
                AlreadyPresent: already,
                CanAutoApply: true);
        }

        if (profile.Framework.Equals("MkDocs", StringComparison.OrdinalIgnoreCase))
        {
            string cfgRel = nav.IndexFiles.FirstOrDefault() ?? "mkdocs.yml";
            string cfgFull = ResolveSafePathInsideRoot(rootDirectory, cfgRel);
            if (!File.Exists(cfgFull))
                return null;

            string docsRoot = profile.AutoScanned.ContentRoots.FirstOrDefault() ?? "docs";
            string relInDocs = docsRoot != "." && targetRel.StartsWith(docsRoot + "/", StringComparison.OrdinalIgnoreCase)
                ? targetRel[(docsRoot.Length + 1)..]
                : targetRel;
            string line = $"  - {safeTitle}: {relInDocs}";
            string existing = File.ReadAllText(cfgFull, Encoding.UTF8);
            bool already = existing.Contains(relInDocs, StringComparison.OrdinalIgnoreCase);
            return new NavigationPatchPlan(
                IndexFileRelativePath: cfgRel,
                IndexFileFullPath: cfgFull,
                LineToAppend: line,
                DiffPreview: $"# 在 {cfgRel} 的 nav: 节点下补充：\n+ {line}",
                AlreadyPresent: already,
                CanAutoApply: false);
        }

        return null;
    }

    public static bool ApplyNavigationPatch(string rootDirectory, NavigationPatchPlan patch)
    {
        if (!patch.CanAutoApply || patch.AlreadyPresent)
            return false;

        string fullPath = ResolveSafePathInsideRoot(rootDirectory, patch.IndexFileRelativePath);
        if (!File.Exists(fullPath))
            return false;

        string existing = File.ReadAllText(fullPath, Encoding.UTF8).Replace("\r\n", "\n");
        if (existing.Contains(patch.LineToAppend, StringComparison.OrdinalIgnoreCase))
            return false;

        var sb = new StringBuilder(existing);
        if (sb.Length > 0 && sb[^1] != '\n')
            sb.Append('\n');
        sb.Append(patch.LineToAppend).Append('\n');

        string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, sb.ToString(), Utf8NoBom);
            File.Move(tempPath, fullPath, overwrite: true);
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

    public static string BuildLocalCommitMessage(string projectName, string[] changes)
    {
        if (changes is null || changes.Length == 0)
            return $"更新 {projectName} 站点内容";

        int added = 0, modified = 0, deleted = 0;
        var docNames = new List<string>();
        foreach (string raw in changes)
        {
            string line = raw.Trim();
            if (line.StartsWith("新增", StringComparison.Ordinal) || line.StartsWith("A ", StringComparison.Ordinal) || line.StartsWith("?? ", StringComparison.Ordinal))
                added++;
            else if (line.StartsWith("删除", StringComparison.Ordinal) || line.StartsWith("D ", StringComparison.Ordinal) || line.StartsWith("清理", StringComparison.Ordinal))
                deleted++;
            else
                modified++;

            string pathPart = line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim() : line;
            if (pathPart.Contains(' '))
                pathPart = pathPart[(pathPart.IndexOf(' ') + 1)..].Trim();
            string ext = Path.GetExtension(pathPart);
            if (SupportedDropExtensions.Contains(ext) && docNames.Count < 2)
            {
                string stem = Path.GetFileNameWithoutExtension(pathPart);
                if (!string.IsNullOrWhiteSpace(stem) && !docNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
                    docNames.Add(stem);
            }
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"新增 {added}");
        if (modified > 0) parts.Add($"修改 {modified}");
        if (deleted > 0) parts.Add($"删除 {deleted}");
        string counts = parts.Count > 0 ? $"（{string.Join("、", parts)}）" : $"（{changes.Length} 项变更）";

        return docNames.Count > 0
            ? $"更新文档：{string.Join("、", docNames)} {counts}"
            : $"发布 {projectName} 更新{counts}";
    }

    private static ContentIngestPlan FinalizePlan(
        string sourceMode,
        SiteProfileDocument profile,
        string rootDirectory,
        DroppedDocumentInfo document,
        string targetDirectory,
        string[] candidateDirectories,
        string[] allowedDirectories,
        string reason,
        string suggestedFileName,
        string suggestedFrontmatter,
        string[] suggestedCategories,
        string[] suggestedTags,
        string navigationAdvice,
        string? warningNote)
    {
        string targetRel = CombineRelativePath(targetDirectory, suggestedFileName);
        string targetFull = ResolveSafePathInsideRoot(rootDirectory, targetRel);
        bool willOverwrite = File.Exists(targetFull);

        var (transfers, missing) = BuildAssetTransfers(profile, rootDirectory, document, targetDirectory, suggestedFileName, out string? chirpyMediaSubpath);
        string finalFrontmatter = document.HasFrontmatter
            ? document.ExistingFrontmatterRaw
            : InjectChirpyMediaSubpathIfNeeded(suggestedFrontmatter, chirpyMediaSubpath);
        bool shouldInjectFm = !document.HasFrontmatter && !string.IsNullOrWhiteSpace(finalFrontmatter);
        var navPatch = BuildNavigationPatch(profile, rootDirectory, targetDirectory, suggestedFileName, document.DetectedTitle);

        return new ContentIngestPlan(
            SourceMode: sourceMode,
            TargetDirectory: targetDirectory,
            CandidateDirectories: candidateDirectories,
            AllowedDirectories: allowedDirectories,
            Reason: reason,
            SuggestedFileName: suggestedFileName,
            TargetRelativePath: targetRel,
            WillOverwriteExisting: willOverwrite,
            ShouldInjectFrontmatter: shouldInjectFm,
            SuggestedFrontmatter: finalFrontmatter,
            SuggestedCategories: suggestedCategories,
            SuggestedTags: suggestedTags,
            AssetTransfers: transfers,
            MissingAssetRefs: missing,
            NavigationAdvice: navigationAdvice,
            WarningNote: warningNote,
            NavigationPatch: navPatch);
    }

    private static (PlannedAssetTransfer[] Transfers, string[] Missing) BuildAssetTransfers(
        SiteProfileDocument profile,
        string rootDirectory,
        DroppedDocumentInfo document,
        string targetDirectory,
        string targetFileName,
        out string? chirpyMediaSubpath)
    {
        chirpyMediaSubpath = null;
        var transfers = new List<PlannedAssetTransfer>();
        var missing = new List<string>();
        if (document.LocalAssets.Length == 0)
            return (Array.Empty<PlannedAssetTransfer>(), Array.Empty<string>());

        bool isPageBundle = targetFileName.Contains('/') &&
                            targetFileName.EndsWith("/index.md", StringComparison.OrdinalIgnoreCase);
        string slugFolder = isPageBundle
            ? targetFileName[..targetFileName.LastIndexOf('/')]
            : Path.GetFileNameWithoutExtension(targetFileName);
        slugFolder = SanitizeSlugSegment(slugFolder);
        if (slugFolder.Length == 0) slugFolder = "post";

        string assetMode = isPageBundle ? "page-bundle" : profile.AutoScanned.Assets.Mode;
        string preferredAssetDir = NormalizeRelativePath(profile.AutoScanned.Assets.PreferredDir);
        bool isChirpy = IsChirpy(profile);
        bool useChirpyMediaSubpath = isChirpy &&
            assetMode == "shared-dir" &&
            preferredAssetDir != "." &&
            !document.HasFrontmatter &&
            HasBooleanHint(profile, "mediaSubpathDetected");

        var usedDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in document.LocalAssets)
        {
            if (!asset.Exists)
            {
                missing.Add(asset.OriginalRef);
                continue;
            }

            string fileName = MakeUniqueFileName(asset.FileName, usedDestNames);
            string destRelDir;
            string mdRef;

            if (assetMode == "page-bundle")
            {
                destRelDir = CombineRelativePath(targetDirectory, slugFolder);
                mdRef = $"./{fileName}";
            }
            else if (assetMode == "post-asset-folder")
            {
                destRelDir = CombineRelativePath(targetDirectory, slugFolder);
                mdRef = fileName;
            }
            else if (assetMode == "shared-dir" && preferredAssetDir != ".")
            {
                destRelDir = CombineRelativePath(preferredAssetDir, slugFolder);
                if (useChirpyMediaSubpath)
                {
                    chirpyMediaSubpath = "/" + destRelDir.Trim('/') + "/";
                    mdRef = fileName;
                }
                else
                {
                    mdRef = BuildSharedAssetMarkdownRef(profile, destRelDir, fileName, isChirpy);
                }
            }
            else
            {
                destRelDir = CombineRelativePath(targetDirectory, "assets");
                mdRef = $"./assets/{fileName}";
            }

            string destRel = CombineRelativePath(destRelDir, fileName);
            string destFull = ResolveSafePathInsideRoot(rootDirectory, destRel);
            transfers.Add(new PlannedAssetTransfer(
                OriginalRef: asset.OriginalRef,
                Kind: asset.Kind,
                SourcePath: asset.ResolvedSourcePath,
                DestinationRelativePath: destRel,
                DestinationFullPath: destFull,
                RewrittenMarkdownRef: mdRef,
                AlreadyExists: File.Exists(destFull)));
        }

        return (transfers.ToArray(), missing.ToArray());
    }

    private static string BuildSharedAssetMarkdownRef(
        SiteProfileDocument profile,
        string destRelDir,
        string fileName,
        bool isChirpy)
    {
        string fullRel = CombineRelativePath(destRelDir, fileName);
        if (isChirpy || profile.Framework.Equals("Jekyll", StringComparison.OrdinalIgnoreCase))
            return "/" + fullRel.TrimStart('/');

        // Strip static/public prefix served at web root in VitePress/VuePress/Hugo/Docusaurus/Astro/Nuxt/Next.js/Hexo
        string[] webRootPrefixes =
        {
            "docs/.vuepress/public/",
            ".vuepress/public/",
            "docs/public/",
            "public/",
            "static/",
            "source/"
        };
        foreach (string prefix in webRootPrefixes)
        {
            if (fullRel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "/" + fullRel[prefix.Length..].TrimStart('/');
        }

        return "/" + fullRel.TrimStart('/');
    }

    private static string RewriteAssetReferences(
        string body,
        Dictionary<string, (string Kind, string NewRef)> rewrites)
    {
        string result = MarkdownImageRegex.Replace(body, match =>
        {
            string rawTarget = match.Groups["target"].Value;
            string unbracketed = rawTarget.Length >= 2 && rawTarget[0] == '<' && rawTarget[^1] == '>'
                ? rawTarget[1..^1]
                : rawTarget;
            if (!rewrites.TryGetValue(unbracketed, out var r))
                return match.Value;

            string alt = match.Groups["alt"].Value;
            string title = match.Groups["title"].Value;
            string encoded = r.NewRef.Contains(' ') ? $"<{r.NewRef}>" : r.NewRef;
            return $"![{alt}]({encoded}{title})";
        });

        result = ObsidianEmbedRegex.Replace(result, match =>
        {
            string rawTarget = match.Groups["target"].Value.Trim();
            if (!rewrites.TryGetValue(rawTarget, out var r))
                return match.Value;

            string alias = match.Groups["alias"].Value.Trim();
            string alt = alias.Length > 0 ? alias : Path.GetFileNameWithoutExtension(rawTarget);
            string encoded = r.NewRef.Contains(' ') ? $"<{r.NewRef}>" : r.NewRef;
            return $"![{alt}]({encoded})";
        });

        result = HtmlImgRegex.Replace(result, match =>
        {
            string rawTarget = match.Groups["target"].Value.Trim();
            if (!rewrites.TryGetValue(rawTarget, out var r))
                return match.Value;

            return $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{r.NewRef}{match.Groups["quote"].Value}";
        });

        return result;
    }

    private static DroppedLocalAsset[] DiscoverLocalAssets(string body, string sourceDir)
    {
        var list = new List<DroppedLocalAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAdd(string rawRef, string kind)
        {
            string cleaned = rawRef.Trim();
            if (cleaned.Length >= 2 && cleaned[0] == '<' && cleaned[^1] == '>')
                cleaned = cleaned[1..^1].Trim();
            if (cleaned.Length == 0 || !seen.Add(cleaned))
                return;
            if (IsExternalOrAnchorRef(cleaned))
                return;

            string withoutQuery = cleaned.Split(new[] { '?', '#' }, 2)[0];
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(withoutQuery); }
            catch { unescaped = withoutQuery; }

            string? resolved = ResolveSourceAssetPath(unescaped, sourceDir, kind == "obsidian");
            bool exists = resolved is not null && File.Exists(resolved);
            long size = 0;
            if (exists)
            {
                try { size = new FileInfo(resolved!).Length; } catch { }
            }

            string fileName = Path.GetFileName(unescaped.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "image.png";

            list.Add(new DroppedLocalAsset(
                OriginalRef: cleaned,
                Kind: kind,
                ResolvedSourcePath: resolved ?? unescaped,
                FileName: fileName,
                Exists: exists,
                FileSize: size));
        }

        foreach (Match m in MarkdownImageRegex.Matches(body))
            TryAdd(m.Groups["target"].Value, "markdown");
        foreach (Match m in ObsidianEmbedRegex.Matches(body))
            TryAdd(m.Groups["target"].Value, "obsidian");
        foreach (Match m in HtmlImgRegex.Matches(body))
            TryAdd(m.Groups["target"].Value, "html");

        return list.ToArray();
    }

    private static string? ResolveSourceAssetPath(string reference, string sourceDir, bool isObsidian)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        // Absolute Windows path
        if (reference.Length >= 3 && char.IsLetter(reference[0]) && reference[1] == ':')
        {
            try { return Path.GetFullPath(reference); } catch { return null; }
        }

        // Site-root absolute path like /assets/foo.png is probably already hosted in the site
        if (reference.StartsWith('/') || reference.StartsWith('\\'))
            return null;

        if (string.IsNullOrWhiteSpace(sourceDir))
            return null;

        string relative = reference.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            string direct = Path.GetFullPath(Path.Combine(sourceDir, relative));
            if (File.Exists(direct))
                return direct;

            if (isObsidian)
            {
                foreach (string sub in new[] { "assets", "images", "img", "attachments" })
                {
                    string candidate = Path.GetFullPath(Path.Combine(sourceDir, sub, relative));
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return direct;
        }
        catch { return null; }
    }

    private static string[] GetAllowedDirectories(SiteProfileDocument profile)
    {
        var dirs = profile.AutoScanned.Directories
            .Where(d => !d.OverwrittenOnBuild)
            .Select(d => NormalizeRelativePath(d.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dirs.Count == 0)
        {
            foreach (string root in profile.AutoScanned.ContentRoots)
            {
                string norm = NormalizeRelativePath(root);
                if (!dirs.Contains(norm, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(norm);
            }
        }

        if (dirs.Count == 0)
            dirs.Add(".");

        return dirs.ToArray();
    }

    private static string SuggestFileName(
        SiteProfileDocument profile,
        string targetDirectory,
        string originalFileName,
        string detectedTitle)
    {
        string ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext) || !profile.AutoScanned.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            ext = profile.AutoScanned.SupportedExtensions.FirstOrDefault() ?? ".md";

        string rawStem = Path.GetFileNameWithoutExtension(originalFileName).Trim();
        if (rawStem.Length == 0) rawStem = detectedTitle;
        string cleanStem = SanitizeSlugSegment(rawStem);
        if (cleanStem.Length == 0) cleanStem = "untitled";

        var dirInfo = profile.AutoScanned.Directories
            .FirstOrDefault(d => string.Equals(d.Path, targetDirectory, StringComparison.OrdinalIgnoreCase));
        bool dirUsesPageBundles = dirInfo is not null && dirInfo.SampleFiles.Length > 0 &&
            dirInfo.SampleFiles.Count(f => f.EndsWith("/index.md", StringComparison.OrdinalIgnoreCase)) * 2 >= dirInfo.SampleFiles.Length;

        string naming = profile.AutoScanned.FileNaming;
        if (dirUsesPageBundles || naming.StartsWith("page-bundle", StringComparison.OrdinalIgnoreCase))
            return $"{cleanStem}/index{ext}";

        if (naming.StartsWith("YYYY-MM-DD-", StringComparison.OrdinalIgnoreCase) ||
            profile.Framework.Equals("Jekyll", StringComparison.OrdinalIgnoreCase))
        {
            if (DatePrefixRegex.IsMatch(cleanStem))
                return cleanStem + ext;
            return $"{DateTime.Now:yyyy-MM-dd}-{cleanStem}{ext}";
        }

        return cleanStem + ext;
    }

    private static string SanitizeSuggestedFileName(string candidate, SiteProfileDocument profile, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;

        string normalized = candidate.Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':'))
            return fallback;

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > 2)
            return fallback;
        if (segments.Length == 2 && !segments[1].Equals("index.md", StringComparison.OrdinalIgnoreCase) &&
            !segments[1].Equals("index.mdx", StringComparison.OrdinalIgnoreCase))
        {
            segments = new[] { segments[^1] };
        }

        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = SanitizeFileNameSegment(segments[i]);
            if (segments[i].Length == 0)
                return fallback;
        }

        string joined = string.Join('/', segments);
        string ext = Path.GetExtension(joined);
        if (string.IsNullOrWhiteSpace(ext) || !SupportedDropExtensions.Contains(ext))
            joined += profile.AutoScanned.SupportedExtensions.FirstOrDefault() ?? ".md";

        return joined;
    }

    private static string ValidateSafeTargetFileName(string targetFileName)
    {
        string normalized = (targetFileName ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':'))
            throw new ArgumentException($"目标文件名无效：{targetFileName}");

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > 2)
            throw new ArgumentException($"目标文件名层级无效：{targetFileName}");

        foreach (string seg in segments)
        {
            if (seg is "." or ".." || seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"目标文件名包含非法字符：{targetFileName}");
        }

        return string.Join('/', segments);
    }

    private static (string[] Categories, string[] Tags) MatchTaxonomyHeuristically(
        SiteProfileDocument profile,
        DroppedDocumentInfo document,
        HashSet<string> docTokens)
    {
        bool isChirpy = IsChirpy(profile);
        var categories = new List<string>();
        var tags = new List<string>();

        if (document.ExistingCategories.Length > 0)
        {
            categories.AddRange(document.ExistingCategories);
        }
        else
        {
            string[]? bestCat = null;
            int bestScore = 0;
            foreach (string[] catPath in profile.AutoScanned.Taxonomy.Categories)
            {
                int score = catPath.Sum(seg => ExtractTokens(seg).Count(t => docTokens.Contains(t)));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCat = catPath;
                }
            }
            if (bestCat is not null)
                categories.AddRange(bestCat);
        }

        if (isChirpy && categories.Count > 2)
            categories = categories.Take(2).ToList();

        if (document.ExistingTags.Length > 0)
        {
            tags.AddRange(document.ExistingTags);
        }
        else
        {
            foreach (string existingTag in profile.AutoScanned.Taxonomy.Tags)
            {
                if (docTokens.Contains(existingTag.ToLowerInvariant()))
                {
                    tags.Add(existingTag);
                    if (tags.Count >= 5) break;
                }
            }
        }

        if (isChirpy)
            tags = tags.Select(t => t.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return (categories.ToArray(), tags.ToArray());
    }

    private static string BuildSuggestedFrontmatter(
        SiteProfileDocument profile,
        DroppedDocumentInfo document,
        string suggestedFileName,
        string title,
        string[] categories,
        string[] tags,
        string description)
    {
        string format = profile.AutoScanned.Frontmatter.Format;
        if (format.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "";

        string safeTitle = EscapeQuotedValue(string.IsNullOrWhiteSpace(title) ? document.DetectedTitle : title);
        bool isChirpy = IsChirpy(profile);
        var now = DateTimeOffset.Now;

        if (format.Equals("toml", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.Append("+++\n");
            sb.Append($"title = \"{safeTitle}\"\n");
            sb.Append($"date = \"{now:yyyy-MM-ddTHH:mm:sszzz}\"\n");
            sb.Append("draft = false\n");
            if (categories.Length > 0)
                sb.Append($"categories = [{string.Join(", ", categories.Select(c => $"\"{EscapeQuotedValue(c)}\""))}]\n");
            if (tags.Length > 0)
                sb.Append($"tags = [{string.Join(", ", tags.Select(t => $"\"{EscapeQuotedValue(t)}\""))}]\n");
            if (!string.IsNullOrWhiteSpace(description))
                sb.Append($"description = \"{EscapeQuotedValue(description)}\"\n");
            sb.Append("+++");
            return sb.ToString();
        }

        var y = new StringBuilder();
        y.Append("---\n");
        y.Append($"title: \"{safeTitle}\"\n");

        bool isPlume = IsPlume(profile);
        if (isPlume)
        {
            y.Append($"createTime: {now:yyyy/MM/dd HH:mm:ss}\n");
            if (HasBooleanHint(profile, "usesPermalink") ||
                profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("permalink", StringComparison.OrdinalIgnoreCase)))
            {
                string shortId = Guid.NewGuid().ToString("N")[..8];
                y.Append($"permalink: /article/{shortId}/\n");
            }
        }
        else
        {
            bool includeDate = isChirpy ||
                profile.Framework is "Jekyll" or "Hexo" or "Hugo" ||
                profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("date", StringComparison.OrdinalIgnoreCase));
            if (includeDate)
            {
                string offset = now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");
                string dateStr = isChirpy || profile.Framework == "Jekyll"
                    ? $"{now:yyyy-MM-dd HH:mm:ss} {offset}"
                    : profile.Framework == "Hexo"
                        ? $"{now:yyyy-MM-dd HH:mm:ss}"
                        : now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
                y.Append($"date: {dateStr}\n");
            }
        }

        if (categories.Length > 0)
        {
            string catKey = profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("category", StringComparison.OrdinalIgnoreCase)) &&
                            !profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("categories", StringComparison.OrdinalIgnoreCase))
                ? "category"
                : "categories";
            y.Append($"{catKey}: [{string.Join(", ", categories.Select(FormatYamlInlineItem))}]\n");
        }

        if (tags.Length > 0)
        {
            string tagKey = profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("tag", StringComparison.OrdinalIgnoreCase)) &&
                            !profile.AutoScanned.Frontmatter.CommonKeys.Any(k => k.Equals("tags", StringComparison.OrdinalIgnoreCase))
                ? "tag"
                : "tags";
            y.Append($"{tagKey}: [{string.Join(", ", tags.Select(FormatYamlInlineItem))}]\n");
        }

        if (!string.IsNullOrWhiteSpace(description))
            y.Append($"description: \"{EscapeQuotedValue(description)}\"\n");

        if (profile.Framework == "Hugo")
            y.Append("draft: false\n");

        _ = suggestedFileName;
        y.Append("---");
        return y.ToString();
    }

    private static string BuildDefaultNavigationAdvice(
        SiteProfileDocument profile,
        string targetDirectory,
        string targetFileName,
        string title)
    {
        var nav = profile.AutoScanned.Navigation;
        if (nav.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "";

        string targetRel = CombineRelativePath(targetDirectory, targetFileName);
        if (profile.Framework.Equals("mdBook", StringComparison.OrdinalIgnoreCase))
        {
            string relInSrc = targetRel.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ? targetRel[4..] : targetRel;
            return $"mdBook 需要手动登记目录，请在 SUMMARY.md 中添加：- [{title}](./{relInSrc})";
        }
        if (profile.Framework.Equals("Docsify", StringComparison.OrdinalIgnoreCase))
            return $"Docsify 使用手动侧边栏，请在 _sidebar.md 中添加：* [{title}]({targetRel})";
        if (!string.IsNullOrWhiteSpace(nav.Note))
            return nav.Note!;
        return nav.IndexFiles.Length > 0
            ? $"当前站点使用手动导航，归档后请按需更新：{string.Join("、", nav.IndexFiles)}"
            : "";
    }

    private static void ExtractFrontmatterMetadata(
        string fmText,
        bool isToml,
        ref string? title,
        List<string> categories,
        List<string> tags)
    {
        string? listKey = null;
        foreach (string rawLine in fmText.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (!isToml && listKey is not null && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                string val = Unquote(trimmed[2..]);
                if (val.Length > 0)
                {
                    if (listKey is "categories" or "category") categories.Add(val);
                    else tags.Add(val);
                }
                continue;
            }

            if (rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t'))
                continue;

            listKey = null;
            int sep = isToml ? rawLine.IndexOf('=') : rawLine.IndexOf(':');
            if (sep <= 0) continue;

            string key = rawLine[..sep].Trim().ToLowerInvariant();
            string value = rawLine[(sep + 1)..].Trim();
            if (key == "title" && title is null && value.Length > 0)
                title = Unquote(value);
            else if (key is "categories" or "category")
            {
                if (!isToml && value.Length == 0) listKey = key;
                else categories.AddRange(ParseInlineList(value));
            }
            else if (key is "tags" or "tag")
            {
                if (!isToml && value.Length == 0) listKey = key;
                else tags.AddRange(ParseInlineList(value));
            }
        }
    }

    private static string[] ParseInlineList(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return trimmed[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(Unquote)
                .Where(s => s.Length > 0)
                .ToArray();
        }
        string scalar = Unquote(trimmed);
        return scalar.Length > 0 ? new[] { scalar } : Array.Empty<string>();
    }

    private static string Unquote(string s)
    {
        string t = s.Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
            return t[1..^1].Trim();
        return t;
    }

    private static bool IsChirpy(SiteProfileDocument profile) =>
        profile.Framework.Equals("Jekyll", StringComparison.OrdinalIgnoreCase) &&
        profile.Theme.Equals("Chirpy", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlume(SiteProfileDocument profile) =>
        profile.Framework.Equals("VuePress", StringComparison.OrdinalIgnoreCase) &&
        profile.Theme.Contains("plume", StringComparison.OrdinalIgnoreCase);

    private static bool HasBooleanHint(SiteProfileDocument profile, string key)
    {
        if (profile.AutoScanned.FrameworkHints is null || !profile.AutoScanned.FrameworkHints.TryGetValue(key, out object? val) || val is null)
            return false;
        if (val is bool b) return b;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.True) return true;
        return string.Equals(val.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string InjectChirpyMediaSubpathIfNeeded(string frontmatter, string? mediaSubpath)
    {
        if (string.IsNullOrWhiteSpace(frontmatter) || string.IsNullOrWhiteSpace(mediaSubpath))
            return frontmatter;
        if (frontmatter.Contains("media_subpath:", StringComparison.OrdinalIgnoreCase))
            return frontmatter;
        int endIdx = frontmatter.LastIndexOf("\n---", StringComparison.Ordinal);
        if (endIdx < 0)
            return frontmatter;
        return frontmatter[..endIdx] + $"\nmedia_subpath: {mediaSubpath}" + frontmatter[endIdx..];
    }

    private static string[] ReadRoutingHints(JsonObject? userRules)
    {
        if (userRules?["routingHints"] is not JsonArray arr)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in arr)
        {
            if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }
        return list.ToArray();
    }

    private static HashSet<string> ExtractTokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return set;

        foreach (string part in TokenSplitRegex.Split(text.ToLowerInvariant()))
        {
            if (part.Length >= 2)
                set.Add(part);
        }
        return set;
    }

    private static bool IsExternalOrAnchorRef(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith('#');

    private static string SanitizeSlugSegment(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            else if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && c is not ('/' or '\\'))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim('-', '.');
    }

    private static string SanitizeFileNameSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        foreach (char c in segment.Trim())
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && c is not ('/' or '\\'))
                sb.Append(c);
        }
        return sb.ToString().Trim('.');
    }

    private static string MakeUniqueFileName(string fileName, HashSet<string> used)
    {
        string safe = SanitizeFileNameSegment(fileName);
        if (safe.Length == 0) safe = "asset.png";
        if (used.Add(safe))
            return safe;

        string stem = Path.GetFileNameWithoutExtension(safe);
        string ext = Path.GetExtension(safe);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{stem}-{i}{ext}";
            if (used.Add(candidate))
                return candidate;
        }
        return $"{stem}-{Guid.NewGuid():N}{ext}";
    }

    private static string NormalizeRelativePath(string path)
    {
        string s = (path ?? "").Trim().Replace('\\', '/').Trim('/');
        if (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s.Length == 0 ? "." : s;
    }

    private static string CombineRelativePath(string dir, string child)
    {
        string normDir = NormalizeRelativePath(dir);
        string normChild = NormalizeRelativePath(child);
        if (normDir == ".") return normChild;
        if (normChild == ".") return normDir;
        return $"{normDir}/{normChild}";
    }

    private static string ResolveSafePathInsideRoot(string rootDirectory, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string normRel = NormalizeRelativePath(relativePath);
        string fullTarget = normRel == "."
            ? fullRoot
            : Path.GetFullPath(Path.Combine(fullRoot, normRel.Replace('/', Path.DirectorySeparatorChar)));

        string rootWithSep = fullRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullTarget.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"目标路径超出站点根目录范围：{relativePath}");
        }
        return fullTarget;
    }

    private static bool FilesHaveSameLengthAndTime(string source, string destination)
    {
        try
        {
            var a = new FileInfo(source);
            var b = new FileInfo(destination);
            return a.Length == b.Length && Math.Abs((a.LastWriteTimeUtc - b.LastWriteTimeUtc).TotalSeconds) < 2;
        }
        catch { return false; }
    }

    private static bool FileNameContainsZoneIdentifier(this PlannedAssetTransfer transfer) =>
        transfer.DestinationRelativePath.Contains("Zone.Identifier", StringComparison.OrdinalIgnoreCase);

    private static string EscapeQuotedValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ").Trim();

    private static string FormatYamlInlineItem(string item)
    {
        string trimmed = item.Trim();
        if (trimmed.IndexOfAny(new[] { ':', '#', ',', '[', ']', '{', '}', '"', '\'', '&', '*', '!' }) >= 0)
            return $"\"{EscapeQuotedValue(trimmed)}\"";
        return trimmed;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SiteLift.Core;

public sealed record AiSettings(
    bool Enabled = false,
    string Endpoint = "https://api.deepseek.com/v1",
    string Model = "deepseek-chat",
    string ProtectedApiKey = "",
    int TimeoutSeconds = 30)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ProtectedApiKey);

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Model);

    public AiSettings Normalize() => new(
        Enabled: Enabled,
        Endpoint: (Endpoint ?? "").Trim(),
        Model: (Model ?? "").Trim(),
        ProtectedApiKey: (ProtectedApiKey ?? "").Trim(),
        TimeoutSeconds: Math.Clamp(TimeoutSeconds <= 0 ? 30 : TimeoutSeconds, 5, 120));
}

public sealed record AiRoutingSuggestion(
    string RecommendedDirectory,
    string[] AlternativeDirectories,
    string Reason,
    string SuggestedFileName,
    string Title,
    string[] Categories,
    string[] Tags,
    string Description,
    string NavigationAdvice);

public sealed record AiDiagnosisResult(
    string Summary,
    string RootCause,
    string[] FixSteps,
    string[] SuggestedCommands);

public static class DiagnosticsRedactor
{
    private const int MaxRedactedLogChars = 4500;
    private static readonly Regex WindowsUserPathRegex = new(@"(?<=[A-Za-z]:\\Users\\)[^\\/\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LinuxHomePathRegex = new(@"(?<=/home/)[^/\s""':]+", RegexOptions.Compiled);
    private static readonly Regex UrlCredsRegex = new(@"(?<=https?://)[^/\s@:]+:[^/\s@]+(?=@)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TokenRegex = new(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{16,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b|\bsk-[A-Za-z0-9_-]{16,}\b", RegexOptions.Compiled);
    private static readonly Regex BearerRegex = new(@"(?i)(Bearer\s+)[A-Za-z0-9._\-]{12,}", RegexOptions.Compiled);

    public static string Redact(string rawText, string? githubUsername = null, string? sshCommand = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return "";

        string text = rawText.Replace("\r\n", "\n");
        if (!string.IsNullOrWhiteSpace(sshCommand))
            text = text.Replace(sshCommand.Trim(), "<ssh-command-redacted>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(githubUsername) && githubUsername.Trim().Length >= 3)
            text = text.Replace(githubUsername.Trim(), "<github-user>", StringComparison.OrdinalIgnoreCase);

        text = WindowsUserPathRegex.Replace(text, "<user>");
        text = LinuxHomePathRegex.Replace(text, "<user>");
        text = UrlCredsRegex.Replace(text, "<redacted>");
        text = TokenRegex.Replace(text, "<token-redacted>");
        text = BearerRegex.Replace(text, "$1<token-redacted>");

        if (text.Length > MaxRedactedLogChars)
        {
            text = "...(前段日志已截断)...\n" + text[^MaxRedactedLogChars..];
        }
        return text.Trim();
    }
}

public static class DpapiSecretProtector
{
    private const string Prefix = "dpapi:";
    private const uint CryptProtectUiForbidden = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("仅在 Windows 上支持 DPAPI 密钥加密。");

        byte[] inputBytes = Encoding.UTF8.GetBytes(plaintext);
        var inBlob = new DataBlob
        {
            cbData = inputBytes.Length,
            pbData = Marshal.AllocHGlobal(inputBytes.Length)
        };
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(inputBytes, 0, inBlob.pbData, inputBytes.Length);
            if (!CryptProtectData(ref inBlob, "SiteLift.AiApiKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outBlob))
            {
                throw new InvalidOperationException($"无法使用 Windows DPAPI 加密密钥（错误码 {Marshal.GetLastWin32Error()}）。");
            }
            byte[] protectedBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, protectedBytes, 0, outBlob.cbData);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
            return "";
        string trimmed = protectedText.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            return "";
        if (!OperatingSystem.IsWindows())
            return "";

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(trimmed[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return "";
        }

        var inBlob = new DataBlob
        {
            cbData = protectedBytes.Length,
            pbData = Marshal.AllocHGlobal(protectedBytes.Length)
        };
        DataBlob outBlob = default;
        try
        {
            Marshal.Copy(protectedBytes, 0, inBlob.pbData, protectedBytes.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outBlob))
            {
                return "";
            }
            byte[] plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero)
                LocalFree(outBlob.pbData);
        }
    }
}

public static class AiAdvisorClient
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Uri BuildChatCompletionsUri(string endpoint)
    {
        string raw = (endpoint ?? "").Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("API 接口地址必须是有效的 http:// 或 https:// URL。");
        }

        string trimmed = raw.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(trimmed);
        return new Uri(trimmed + "/chat/completions");
    }

    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        AiSettings settings,
        string? plainApiKeyOverride = null,
        CancellationToken cancellation = default)
    {
        var normalized = settings.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.Model))
            return (false, "请先填写模型名称。");

        Uri uri;
        try { uri = BuildChatCompletionsUri(normalized.Endpoint); }
        catch (ArgumentException ex) { return (false, ex.Message); }

        string apiKey = plainApiKeyOverride is not null
            ? plainApiKeyOverride.Trim()
            : DpapiSecretProtector.Unprotect(normalized.ProtectedApiKey);

        var payload = new JsonObject
        {
            ["model"] = normalized.Model,
            ["temperature"] = 0,
            ["max_tokens"] = 16,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Reply with OK only."
                }
            }
        };

        try
        {
            string reply = await SendChatCompletionAsync(uri, apiKey, payload, normalized.TimeoutSeconds, cancellation);
            string snippet = reply.Trim();
            if (snippet.Length > 40) snippet = snippet[..40] + "...";
            return (true, $"连接成功（模型：{normalized.Model}，响应：{snippet}）。");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    public static async Task<AiRoutingSuggestion> RequestRoutingAsync(
        AiSettings settings,
        SiteProfileDocument profile,
        DroppedDocumentInfo document,
        string[] allowedDirectories,
        CancellationToken cancellation = default)
    {
        var normalized = settings.Normalize();
        if (!normalized.IsConfigured)
            throw new InvalidOperationException("AI 助手尚未启用或未配置完整。");

        Uri uri = BuildChatCompletionsUri(normalized.Endpoint);
        string apiKey = DpapiSecretProtector.Unprotect(normalized.ProtectedApiKey);

        string systemPrompt = """
            你是 SiteLift 的静态博客归档路由助手。你必须严格依据提供的站点画像（.sitelift/profile.json 摘要）与待归档 Markdown 摘要，决定文章最合适的目标目录、文件名、分类与标签。
            硬性规则：
            1. recommendedDirectory 与 alternativeDirectories 必须严格从 allowedDirectories 列表中原样选取，绝不可编造新目录或选择 overwrittenOnBuild 为 true 的目录。
            2. 文件名遵循站点的 fileNaming 与 supportedExtensions（例如 Jekyll/Chirpy 必须使用 YYYY-MM-DD-slug.md；Hugo page-bundle 使用 slug/index.md）。
            3. 优先复用站点 taxonomy 中已有的 categories 与 tags；若站点为 Chirpy，categories 最多 2 级、tags 建议小写。
            4. 严格只输出一个合法 JSON 对象，不要附加任何解释或 Markdown 代码块。
            输出 JSON 结构：
            {
              "recommendedDirectory": "string",
              "alternativeDirectories": ["string"],
              "reason": "不超过45字的中文推荐理由",
              "suggestedFileName": "string",
              "title": "string",
              "categories": ["string"],
              "tags": ["string"],
              "description": "string",
              "navigationAdvice": "仅当 navigation.mode 为 manual 或 mixed 时给出简短建议，否则为空字符串"
            }
            """;

        var compactProfile = new
        {
            framework = profile.Framework,
            theme = profile.Theme,
            fileNaming = profile.AutoScanned.FileNaming,
            supportedExtensions = profile.AutoScanned.SupportedExtensions,
            frontmatterFormat = profile.AutoScanned.Frontmatter.Format,
            frontmatterCommonKeys = profile.AutoScanned.Frontmatter.CommonKeys,
            navigation = profile.AutoScanned.Navigation,
            allowedDirectories,
            directories = profile.AutoScanned.Directories
                .Where(d => !d.OverwrittenOnBuild)
                .Take(40)
                .Select(d => new
                {
                    path = d.Path,
                    title = d.Title,
                    articleCount = d.ArticleCount,
                    sampleTitles = d.SampleTitles.Take(4).ToArray(),
                    sampleFiles = d.SampleFiles.Take(3).ToArray()
                }),
            taxonomy = new
            {
                categories = profile.AutoScanned.Taxonomy.Categories.Take(30).ToArray(),
                tags = profile.AutoScanned.Taxonomy.Tags.Take(50).ToArray()
            },
            frameworkHints = profile.AutoScanned.FrameworkHints,
            userRules = profile.UserRules
        };

        var compactDoc = new
        {
            today = DateTime.Now.ToString("yyyy-MM-dd"),
            originalFileName = document.OriginalFileName,
            detectedTitle = document.DetectedTitle,
            existingFrontmatter = document.ExistingFrontmatterRaw,
            headings = document.Headings.Take(10).ToArray(),
            excerpt = document.BodyExcerpt,
            localImageCount = document.LocalAssets.Length
        };

        string userPrompt = "站点画像：\n" +
            JsonSerializer.Serialize(compactProfile, PromptJsonOptions) +
            "\n\n待归档文档：\n" +
            JsonSerializer.Serialize(compactDoc, PromptJsonOptions);

        var payload = new JsonObject
        {
            ["model"] = normalized.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        string rawReply = await SendChatCompletionAsync(uri, apiKey, payload, normalized.TimeoutSeconds, cancellation);
        return ParseRoutingJson(rawReply);
    }

    public static AiRoutingSuggestion ParseRoutingJson(string rawResponseText)
    {
        if (string.IsNullOrWhiteSpace(rawResponseText))
            throw new JsonException("模型返回内容为空。");

        string candidate = rawResponseText.Trim();
        var fenced = Regex.Match(candidate, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
        {
            candidate = fenced.Groups[1].Value.Trim();
        }
        else
        {
            int firstBrace = candidate.IndexOf('{');
            int lastBrace = candidate.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                candidate = candidate[firstBrace..(lastBrace + 1)];
        }

        if (JsonNode.Parse(candidate) is not JsonObject obj)
            throw new JsonException("模型返回的不是 JSON 对象。");

        string ReadString(string key) =>
            obj[key] is JsonValue val && val.TryGetValue<string>(out var s) ? s.Trim() : "";

        string[] ReadStringArray(string key)
        {
            if (obj[key] is not JsonArray arr)
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in arr)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return new AiRoutingSuggestion(
            RecommendedDirectory: ReadString("recommendedDirectory"),
            AlternativeDirectories: ReadStringArray("alternativeDirectories"),
            Reason: ReadString("reason"),
            SuggestedFileName: ReadString("suggestedFileName"),
            Title: ReadString("title"),
            Categories: ReadStringArray("categories"),
            Tags: ReadStringArray("tags"),
            Description: ReadString("description"),
            NavigationAdvice: ReadString("navigationAdvice"));
    }

    public static string BuildDiagnosisCopyPrompt(string framework, string projectName, string redactedLogs)
    {
        return $"""
            我在使用 SiteLift（Windows + WSL 静态站点发布工具）构建或推送站点时遇到了报错，请帮我分析原因并给出修复步骤：

            - 站点名称：{projectName}
            - 静态站点架构：{framework}
            - 脱敏运行日志：
            ```text
            {redactedLogs}
            ```

            请输出：
            1. 一句话结论
            2. 根本原因
            3. 具体修复步骤（如有可执行的 WSL/Git 命令请单独列出）
            """;
    }

    public static async Task<AiDiagnosisResult> DiagnoseFailureAsync(
        AiSettings settings,
        string framework,
        string projectName,
        string redactedLogs,
        CancellationToken cancellation = default)
    {
        var normalized = settings.Normalize();
        if (!normalized.IsConfigured)
            throw new InvalidOperationException("AI 助手尚未启用或未配置完整。");

        Uri uri = BuildChatCompletionsUri(normalized.Endpoint);
        string apiKey = DpapiSecretProtector.Unprotect(normalized.ProtectedApiKey);

        string systemPrompt = """
            你是 SiteLift（Windows + WSL 静态站点发布工作台）的报错诊断专家。
            根据脱敏后的构建/同步/推送日志与站点架构，精准判断失败原因并给出可落地的修复方案。
            严格只输出一个合法 JSON 对象，不要附加解释或 Markdown 代码块：
            {
              "summary": "一句话核心结论（不超过30字）",
              "rootCause": "清晰的中文根因解释",
              "fixSteps": ["步骤1", "步骤2"],
              "suggestedCommands": ["可选：建议用户在终端执行的单行命令，没有则返回空数组"]
            }
            """;

        string userPrompt = $"站点名称：{projectName}\n站点架构：{framework}\n\n脱敏运行日志：\n{redactedLogs}";

        var payload = new JsonObject
        {
            ["model"] = normalized.Model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        string rawReply = await SendChatCompletionAsync(uri, apiKey, payload, normalized.TimeoutSeconds, cancellation);
        return ParseDiagnosisJson(rawReply);
    }

    public static AiDiagnosisResult ParseDiagnosisJson(string rawResponseText)
    {
        if (string.IsNullOrWhiteSpace(rawResponseText))
            throw new JsonException("模型返回内容为空。");

        string candidate = ExtractJsonObjectText(rawResponseText);
        if (JsonNode.Parse(candidate) is not JsonObject obj)
            throw new JsonException("模型返回的不是 JSON 对象。");

        string ReadString(string key) =>
            obj[key] is JsonValue val && val.TryGetValue<string>(out var s) ? s.Trim() : "";

        string[] ReadArray(string key)
        {
            if (obj[key] is not JsonArray arr) return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in arr)
            {
                if (item is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }
            return list.ToArray();
        }

        string summary = ReadString("summary");
        string rootCause = ReadString("rootCause");
        if (summary.Length == 0 && rootCause.Length == 0)
            throw new JsonException("诊断结果缺少 summary / rootCause 字段。");

        return new AiDiagnosisResult(
            Summary: summary.Length > 0 ? summary : "报错诊断完成",
            RootCause: rootCause,
            FixSteps: ReadArray("fixSteps"),
            SuggestedCommands: ReadArray("suggestedCommands"));
    }

    public static async Task<string> GenerateCommitMessageAsync(
        AiSettings settings,
        string projectName,
        string framework,
        string[] changes,
        CancellationToken cancellation = default)
    {
        var normalized = settings.Normalize();
        if (!normalized.IsConfigured)
            throw new InvalidOperationException("AI 助手尚未启用或未配置完整。");

        Uri uri = BuildChatCompletionsUri(normalized.Endpoint);
        string apiKey = DpapiSecretProtector.Unprotect(normalized.ProtectedApiKey);

        string systemPrompt = """
            你是静态博客的 Git 提交说明生成助手。
            根据站点变更清单，生成一句简洁、清晰的中文单行 Git Commit Message（不超过 50 个字）。
            规则：
            1. 只输出提交说明本身这一行纯文本，不要加引号、代码块、前缀说明或换行。
            2. 优先概括新增/更新的文章主题或功能改动，不要罗列构建产物哈希文件名。
            """;

        string sampleChanges = string.Join("\n", changes.Take(50));
        string userPrompt = $"站点：{projectName}（{framework}）\n总变更项：{changes.Length}\n变更清单（前50项）：\n{sampleChanges}";

        var payload = new JsonObject
        {
            ["model"] = normalized.Model,
            ["temperature"] = 0.2,
            ["max_tokens"] = 80,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        string rawReply = await SendChatCompletionAsync(uri, apiKey, payload, normalized.TimeoutSeconds, cancellation);
        return ParseCommitMessage(rawReply);
    }

    public static string ParseCommitMessage(string rawResponseText)
    {
        string text = (rawResponseText ?? "").Trim();
        text = Regex.Replace(text, @"^```[^\n]*\n?|\n?```$", "", RegexOptions.Singleline).Trim();
        int nl = text.IndexOf('\n');
        if (nl > 0) text = text[..nl].Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();
        if (text.Length > 100) text = text[..100].Trim();
        if (text.Length == 0)
            throw new JsonException("模型未返回有效的提交说明。");
        return text;
    }

    private static string ExtractJsonObjectText(string rawResponseText)
    {
        string candidate = rawResponseText.Trim();
        var fenced = Regex.Match(candidate, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
            return fenced.Groups[1].Value.Trim();

        int firstBrace = candidate.IndexOf('{');
        int lastBrace = candidate.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return candidate[firstBrace..(lastBrace + 1)];

        return candidate;
    }

    private static async Task<string> SendChatCompletionAsync(
        Uri uri,
        string apiKey,
        JsonObject payload,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        string body = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            string detail = ExtractErrorMessage(body);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{detail}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? "";
            }
        }

        throw new JsonException("模型响应缺少 choices[0].message.content 字段。");
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var msg) &&
                    msg.ValueKind == JsonValueKind.String)
                {
                    return msg.GetString() ?? body;
                }
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? body;
            }
        }
        catch { }

        string trimmed = body.Trim();
        return trimmed.Length > 240 ? trimmed[..240] + "..." : (trimmed.Length > 0 ? trimmed : "未知错误");
    }
}

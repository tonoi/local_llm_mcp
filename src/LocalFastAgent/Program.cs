using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var server = new LocalFastAgent();
await server.RunAsync();

sealed class LocalFastAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly AgentConfig _config = AgentConfig.FromEnvironment();
    private readonly OpenAiCompatibleClient _llm;
    private readonly SecretMasker _masker = new();
    private readonly Dictionary<string, string> _cache = new();

    public LocalFastAgent() => _llm = new OpenAiCompatibleClient(_config);

    public async Task RunAsync()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var id)) continue; // notifications do not receive responses
                var method = root.GetProperty("method").GetString() ?? "";
                var result = method switch
                {
                    "initialize" => InitializeResult(),
                    "tools/list" => ToolsListResult(),
                    "tools/call" => await CallToolAsync(root.GetProperty("params")),
                    _ => throw new McpException(-32601, $"Unknown method: {method}")
                };
                await WriteResponseAsync(id, result);
            }
            catch (McpException ex)
            {
                await WriteErrorAsync(line, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} status=error error={ex.GetType().Name}: {ex.Message}");
                await WriteErrorAsync(line, -32603, ex.Message);
            }
        }
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "LocalFastAgent", ["version"] = "0.1.0" }
    };

    private static JsonObject ToolsListResult() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool("local_summarize_logs", "Summarize build/test/runtime logs through a local OpenAI-compatible LLM.", new JsonObject
            {
                ["type"]="object", ["properties"] = new JsonObject
                {
                    ["log_path"] = new JsonObject { ["type"]="string" },
                    ["log_text"] = new JsonObject { ["type"]="string" },
                    ["focus"] = new JsonObject { ["type"]="string" }
                }
            }),
            Tool("local_code_map", "Find likely relevant files and symbols before broad repository exploration.", new JsonObject
            {
                ["type"]="object", ["properties"] = new JsonObject
                {
                    ["question"] = new JsonObject { ["type"]="string" },
                    ["include_globs"] = new JsonObject { ["type"]="array", ["items"] = new JsonObject { ["type"]="string" } },
                    ["exclude_globs"] = new JsonObject { ["type"]="array", ["items"] = new JsonObject { ["type"]="string" } }
                }, ["required"] = new JsonArray("question")
            }),
            Tool("local_review_diff", "Perform first-pass risk review of a diff through a local OpenAI-compatible LLM.", new JsonObject
            {
                ["type"]="object", ["properties"] = new JsonObject
                {
                    ["diff_path"] = new JsonObject { ["type"]="string" },
                    ["diff_text"] = new JsonObject { ["type"]="string" },
                    ["focus"] = new JsonObject { ["type"]="string" }
                }
            })
        }
    };

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    { ["name"] = name, ["description"] = description, ["inputSchema"] = inputSchema };

    private async Task<JsonObject> CallToolAsync(JsonElement parameters)
    {
        var name = parameters.GetProperty("name").GetString() ?? throw new McpException(-32602, "Tool name is required.");
        var args = parameters.TryGetProperty("arguments", out var a) ? a : default;
        var sw = Stopwatch.StartNew();
        var inputBytes = args.ValueKind == JsonValueKind.Undefined ? 0 : Encoding.UTF8.GetByteCount(args.GetRawText());
        try
        {
            var text = name switch
            {
                "local_summarize_logs" => await SummarizeLogsAsync(args),
                "local_code_map" => await CodeMapAsync(args),
                "local_review_diff" => await ReviewDiffAsync(args),
                _ => throw new McpException(-32602, $"Unknown tool: {name}")
            };
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} tool={name} duration_ms={sw.ElapsedMilliseconds} input_bytes={inputBytes} status=ok");
            return TextContent(text);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} tool={name} duration_ms={sw.ElapsedMilliseconds} input_bytes={inputBytes} status=error error={ex.Message}");
            return TextContent($"## LocalFastAgent エラー\n\n{WebUtility.HtmlEncode(ex.Message)}");
        }
    }

    private async Task<string> SummarizeLogsAsync(JsonElement args)
    {
        var focus = GetString(args, "focus") ?? "主要エラー、根本原因候補、次に確認すべきこと";
        var source = GetString(args, "log_text") ?? ReadWorkspaceFile(GetString(args, "log_path") ?? throw new McpException(-32602, "log_path or log_text is required."), _config.MaxLogBytes, tailPreferred: true);
        var compressed = CompressLog(_masker.Mask(source), _config.MaxPromptChars);
        return await CachedLlmAsync("local_summarize_logs", focus + compressed, "あなたはログ解析エージェントです。ログ全文を返さず、日本語Markdownで主要エラー、根本原因候補、関連ファイル、次にAIエージェントが確認すべきこと、無視してよいノイズを簡潔に返してください。", $"focus: {focus}\n\nログ抜粋:\n{compressed}");
    }

    private async Task<string> CodeMapAsync(JsonElement args)
    {
        var question = GetString(args, "question") ?? throw new McpException(-32602, "question is required.");
        var includes = GetStringArray(args, "include_globs", ["**/*.cs", "**/*.ts", "**/*.tsx", "**/*.js", "**/*.py", "**/*.rs", "**/*.go", "**/*.java", "**/*.md"]);
        var excludes = GetStringArray(args, "exclude_globs", ["**/.git/**", "**/bin/**", "**/obj/**", "**/node_modules/**", "**/dist/**", "**/build/**"]);
        var files = EnumerateWorkspaceFiles(includes, excludes).Take(_config.MaxCodeMapFiles).ToList();
        var terms = Regex.Matches(question.ToLowerInvariant(), "[a-z0-9_]{3,}|[一-龯ぁ-んァ-ン]{2,}").Select(m => m.Value).Distinct().ToArray();
        var snippets = files.Select(path => ScoreAndSnippet(path, terms)).Where(x => x.Score > 0 || snippetsCount(files) < 20).OrderByDescending(x => x.Score).Take(30).Select(x => x.Snippet);
        var body = string.Join("\n\n---\n", snippets).Limit(_config.MaxPromptChars);
        return await CachedLlmAsync("local_code_map", question + body, "あなたはコード調査エージェントです。ソース全文を返さず、日本語Markdownで関連度が高いファイル、主要シンボル、推定処理経路、変更候補、AIエージェントが次に読むべき箇所を返してください。推測と確認済みを分けてください。", $"質問: {question}\n\n候補抜粋:\n{_masker.Mask(body)}");
        static int snippetsCount<T>(IEnumerable<T> _) => 0;
    }

    private async Task<string> ReviewDiffAsync(JsonElement args)
    {
        var focus = GetString(args, "focus") ?? "bug risk, exception handling, security";
        var source = GetString(args, "diff_text") ?? ReadWorkspaceFile(GetString(args, "diff_path") ?? throw new McpException(-32602, "diff_path or diff_text is required."), _config.MaxDiffBytes, tailPreferred: false);
        var diff = FilterDiff(_masker.Mask(source)).Limit(_config.MaxPromptChars);
        return await CachedLlmAsync("local_review_diff", focus + diff, "あなたはdiff一次レビュー担当です。stylisticな指摘は避け、実害のある高/中リスク、テスト不足、AIエージェントが最終確認すべき点だけを日本語Markdownで返してください。diff全文は返さないでください。", $"focus: {focus}\n\ndiff抜粋:\n{diff}");
    }

    private async Task<string> CachedLlmAsync(string tool, string keyMaterial, string system, string user)
    {
        var key = tool + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
        if (_cache.TryGetValue(key, out var cached)) return cached + "\n\n_短命キャッシュから返却しました。_";
        var result = await _llm.ChatAsync(system, user);
        result = _masker.Mask(result);
        _cache[key] = result;
        return result;
    }

    private string ReadWorkspaceFile(string relativePath, int maxBytes, bool tailPreferred)
    {
        if (SecretMasker.IsSecretPath(relativePath)) throw new McpException(-32602, "秘密情報に該当する可能性があるファイルは読み取りません。");
        var full = Path.GetFullPath(Path.Combine(_config.WorkspaceRoot, relativePath));
        var root = Path.GetFullPath(_config.WorkspaceRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new McpException(-32602, "指定パスはworkspace外のため読み取りません。");
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > maxBytes) bytes = tailPreferred ? bytes[^maxBytes..] : bytes[..maxBytes];
        return Encoding.UTF8.GetString(bytes);
    }

    private IEnumerable<string> EnumerateWorkspaceFiles(string[] includes, string[] excludes)
    {
        var root = Path.GetFullPath(_config.WorkspaceRoot);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (SecretMasker.IsSecretPath(rel)) continue;
            if (excludes.Any(g => Glob.IsMatch(rel, g))) continue;
            if (!includes.Any(g => Glob.IsMatch(rel, g))) continue;
            var info = new FileInfo(file);
            if (info.Length <= _config.MaxFileBytes) yield return file;
        }
    }

    private (int Score, string Snippet) ScoreAndSnippet(string fullPath, string[] terms)
    {
        var rel = Path.GetRelativePath(_config.WorkspaceRoot, fullPath).Replace('\\', '/');
        var lines = File.ReadLines(fullPath).Take(400).ToArray();
        var text = string.Join('\n', lines);
        var score = terms.Count(t => rel.Contains(t, StringComparison.OrdinalIgnoreCase) || text.Contains(t, StringComparison.OrdinalIgnoreCase));
        var symbolLines = lines.Select((l, i) => (l, i)).Where(x => Regex.IsMatch(x.l, "\\b(class|interface|record|struct|enum|def|function|async|public|private|protected|static)\\b")).Take(30).Select(x => $"{x.i + 1}: {x.l.Trim()}");
        return (score, $"file: {rel}\nscore: {score}\n{string.Join("\n", symbolLines)}".Limit(4000));
    }

    private static string CompressLog(string text, int maxChars)
    {
        var keywords = new Regex("error|failed|exception|traceback|fatal|panic|assert|timeout", RegexOptions.IgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var important = lines.Where(l => keywords.IsMatch(l)).TakeLast(300);
        var tail = lines.TakeLast(500);
        return string.Join("\n", important.Concat(["", "--- tail ---"]).Concat(tail)).Limit(maxChars);
    }

    private static string FilterDiff(string diff)
    {
        var excluded = new Regex("(^diff --git .*?(package-lock\\.json|yarn\\.lock|pnpm-lock\\.yaml|\\.min\\.|generated|/dist/|/build/).*?)(?=^diff --git |\\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return excluded.Replace(diff, "");
    }

    private static JsonObject TextContent(string text) => new() { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } };
    private static string? GetString(JsonElement args, string name) => args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static string[] GetStringArray(JsonElement args, string name, string[] fallback) => args.ValueKind != JsonValueKind.Undefined && args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : fallback;

    private static async Task WriteResponseAsync(JsonElement id, JsonObject result) => await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }, JsonOptions));
    private static async Task WriteErrorAsync(string requestLine, int code, string message)
    {
        JsonNode? id = null;
        try { using var d = JsonDocument.Parse(requestLine); if (d.RootElement.TryGetProperty("id", out var e)) id = JsonNode.Parse(e.GetRawText()); } catch { }
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, JsonOptions));
    }
}

sealed record AgentConfig(string WorkspaceRoot, string BaseUrl, string Model, string? ApiKey, int MaxLogBytes, int MaxDiffBytes, int MaxFileBytes, int MaxPromptChars, int MaxCodeMapFiles, int RequestTimeoutSeconds)
{
    public static AgentConfig FromEnvironment() => new(
        Environment.GetEnvironmentVariable("WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory(),
        (Environment.GetEnvironmentVariable("LOCAL_OPENAI_BASE_URL") ?? "http://host.docker.internal:8080/v1").TrimEnd('/'),
        Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL") ?? "local-coder",
        Environment.GetEnvironmentVariable("LOCAL_OPENAI_API_KEY"),
        EnvInt("MAX_LOG_BYTES", 512_000), EnvInt("MAX_DIFF_BYTES", 512_000), EnvInt("MAX_FILE_BYTES", 256_000), EnvInt("MAX_PROMPT_CHARS", 60_000), EnvInt("MAX_CODE_MAP_FILES", 200), EnvInt("LOCAL_REQUEST_TIMEOUT_SECONDS", 120));
    private static int EnvInt(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}

sealed class OpenAiCompatibleClient(AgentConfig config)
{
    public async Task<string> ChatAsync(string system, string user)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds) };
        if (!string.IsNullOrWhiteSpace(config.ApiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        var payload = new { model = config.Model, messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } }, temperature = 0.1, max_tokens = 1600 };
        try
        {
            using var res = await http.PostAsync($"{config.BaseUrl}/chat/completions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return Diagnostic(res.StatusCode, body);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "(empty response)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"組み込み・ローカル・エッジ機器のOpenAI互換APIに接続できませんでした。\n\n確認事項:\n1. Windowsホストから `{config.BaseUrl}/models` に接続できるか\n2. Windowsコンテナ内から同じURLに接続できるか\n3. ローカル推論機器側FirewallがWindowsホストを許可しているか\n4. APIキーが一致しているか\n\n詳細: {ex.GetType().Name}: {ex.Message}";
        }
    }
    private static string Diagnostic(HttpStatusCode code, string body) => $"ローカル推論APIがHTTP {(int)code}を返しました。4xxは設定/APIキー/モデル名、5xxは推論サーバー状態を確認してください。\n\n応答抜粋:\n{body.Limit(2000)}";
}

sealed class SecretMasker
{
    private readonly Regex[] _patterns =
    [
        new("-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Singleline),
        new("Bearer\\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase),
        new("eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+"),
        new("(?i)(api[_-]?key|password|passwd|client[_-]?secret|connectionstring|token)\\s*[:=]\\s*['\"]?[^'\"\\s]+"),
    ];
    public string Mask(string input)
    {
        foreach (var pattern in _patterns) input = pattern.Replace(input, m => m.Groups.Count > 1 ? $"{m.Groups[1].Value}=[REDACTED_SECRET]" : "[REDACTED_SECRET]");
        return input;
    }
    public static bool IsSecretPath(string path) => Regex.IsMatch(path.Replace('\\', '/'), "(?i)(^|/)(\\.env|secrets\\.json|appsettings\\.Production\\.json|id_rsa|id_ed25519|credentials?|secrets?)(/|$)");
}

static class Glob
{
    public static bool IsMatch(string path, string glob)
    {
        var rx = "^" + Regex.Escape(glob.Replace('\\', '/')).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path, rx, RegexOptions.IgnoreCase);
    }
}

static class StringExtensions { public static string Limit(this string value, int max) => value.Length <= max ? value : value[..max] + "\n...[truncated by LocalFastAgent]"; }
sealed class McpException(int code, string message) : Exception(message) { public int Code { get; } = code; }

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigBlueFish.HermesBridge;

internal sealed class HermesException(string message) : Exception(message);

// Hermes keeps the history server-side: every turn sends the same X-Hermes-Session-Id and only the
// newest user text. A fresh id starts a new conversation (/new); an id is never reused.
internal sealed partial class HermesChat : IDisposable
{
    // Layered on top of Hermes' own persona for each API turn; it is not stored in the history.
    private const string Instructions = "最重要的規則：你這一輪最後的回答文字，會原封不動顯示在桌面寵物「藍色大肥魚（魚魚）」的對話氣泡裡，"
        + "所以回答只能是魚魚要對使用者說的話本身，不要描述你呼叫了哪些工具、做了哪些動作或顯示了什麼。"
        + "絕對不要呼叫 big-blue-fish 的 say 或 say_next 工具，想說的話直接寫在回答裡。"
        + "請用自然的繁體中文回答，不要使用 Markdown 標記、程式碼區塊或表格；盡量簡短（約 150 字內），內容很長時先講重點。"
        + "可以用 play_action 讓魚魚做動作，"
        + "但動作名稱必須和 list_actions 回傳的名稱（簡體字）一字不差；播放失敗就放棄，不要一直重試。"
        + "只有在使用者這則訊息明確要求你看螢幕時才可以截圖，而且優先使用 capture_foreground。";
    // Toolsets that let a typed message act on this computer or reach beyond the conversation.
    private static readonly string[] RiskyToolsets = ["terminal", "file", "code_execution", "computer_use", "browser", "cronjob", "delegation"];
    private static readonly TimeSpan RetryBudget = TimeSpan.FromMinutes(2);
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue auth;
    private readonly string statePath;

    internal HermesChat(Uri baseUrl, string apiKey, string statePath)
    {
        // Tool-using turns can run for minutes and non-streaming replies send nothing meanwhile.
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(30) };
        // Sent only with authenticated requests, never with the open /health probe.
        auth = new AuthenticationHeaderValue("Bearer", apiKey);
        this.statePath = statePath;
        Session = LoadSession() ?? NewSession();
    }

    internal string Session { get; private set; }

    internal string NewSession()
    {
        Session = "pet-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(new { session = Session }));
            File.Move(statePath + ".tmp", statePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write("無法儲存對話編號，下次啟動會開始新的對話：" + error.Message);
        }
        return Session;
    }

    private string? LoadSession()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var id = document.RootElement.GetProperty("session").GetString();
            return id is { Length: > 4 and <= 256 } && id.StartsWith("pet-", StringComparison.Ordinal) ? id : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    internal async Task<bool> IsUpAsync(CancellationToken token)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync("health", limit.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    // Names of enabled toolsets that can run commands, edit files or act outside the chat; null if unknown.
    internal async Task<string[]?> RiskyToolsetsAsync(CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/toolsets");
            request.Headers.Authorization = auth;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, limit.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            return document.RootElement.GetProperty("data").EnumerateArray()
                .Where(t => t.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True)
                .Select(t => t.GetProperty("name").GetString() ?? "")
                .Where(name => RiskyToolsets.Contains(name)).ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal async Task<string> AskAsync(string text, CancellationToken token)
    {
        // Every attempt for one message keeps this key, so a retry replays the same turn instead of
        // running the work twice.
        string idempotency = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        var backoff = TimeSpan.FromSeconds(2);
        bool queued = false;
        while (true)
        {
            HttpResponseMessage response;
            try { response = await SendAskAsync(text, idempotency, token); }
            // While Hermes restarts with a saved admission, keep replaying it until it is back.
            catch (HttpRequestException) when (queued && DateTime.UtcNow - started < RetryBudget)
            {
                await Task.Delay(backoff, token);
                continue;
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                using var document = Parse(body);
                string code = ErrorField(document, "code");
                // Busy or settling after a restart: 503 runtime_draining / admission_queued, 429.
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                {
                    queued |= code == "admission_queued";
                    if (DateTime.UtcNow - started >= RetryBudget)
                        throw new HermesException(queued ? "Hermes 正在重新啟動，訊息已經保存，回來後會自動處理，不用重傳。" : "Hermes 一直很忙，請稍後再試。");
                    var wait = response.Headers.RetryAfter?.Delta is { } after && after > backoff ? after : backoff;
                    Log.Write($"Hermes 暫時忙碌（HTTP {(int)response.StatusCode} {code}），{wait.TotalSeconds:0} 秒後重試。");
                    await Task.Delay(wait, token);
                    backoff = TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new HermesException("Hermes 不接受 API 金鑰，請確認 .env 裡的 API_SERVER_KEY。");
                if (!response.IsSuccessStatusCode)
                    throw new HermesException($"HTTP {(int)response.StatusCode} {ErrorField(document, "message")}".Trim());
                try
                {
                    var reply = document?.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                    // Hermes inlines generated images as data URLs, which a speech bubble cannot show.
                    return InlineImage().Replace(reply, "（圖片）").Trim();
                }
                catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
                {
                    throw new HermesException("Hermes 的回應格式看不懂。");
                }
            }
        }
    }

    private Task<HttpResponseMessage> SendAskAsync(string text, string idempotency, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "hermes-agent",
                stream = false,
                messages = new object[] { new { role = "system", content = Instructions }, new { role = "user", content = text } }
            })
        };
        request.Headers.Authorization = auth;
        request.Headers.Add("X-Hermes-Session-Id", Session);
        request.Headers.Add("Idempotency-Key", idempotency);
        return SendAndDisposeAsync(request, token);
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken token)
    {
        using (request) return await http.SendAsync(request, token);
    }

    [GeneratedRegex(@"!\[[^\]]*\]\(data:[^)]*\)")]
    private static partial Regex InlineImage();

    private static JsonDocument? Parse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static string ErrorField(JsonDocument? document, string field) =>
        document != null && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public void Dispose() => http.Dispose();
}

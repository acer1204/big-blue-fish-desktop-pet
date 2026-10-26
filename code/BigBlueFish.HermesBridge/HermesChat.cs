using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        + "只能用台灣繁體中文和台灣用語回答；使用者用英文問時可以用英文回答。絕對不要出現簡體字，引用簡體資料或提到動作名稱時也要寫成繁體。"
        + "不要使用 Markdown 標記、程式碼區塊或表格；盡量簡短（約 150 字內），內容很長時先講重點。"
        + "可以用 play_action 讓魚魚做動作；呼叫時動作名稱必須和 list_actions 回傳的名稱（簡體字）一字不差，"
        + "播放失敗就放棄，不要一直重試。"
        + "只有在使用者這則訊息明確要求你看螢幕時才可以截圖，而且優先使用 capture_foreground。"
        // Hermes hides MCP tools behind tool_search, so a model that does not look thinks they are missing.
        + "需要工具時，先用 tool_search 查找可用的工具（包括使用者自己安裝的 MCP 外掛工具），不要沒查就說沒有、沒有連線或做不到。"
        + "找到符合需求的外掛工具時優先使用它，不要改用終端機指令或自行安裝其他程式繞過；外掛工具失敗時，才改用其他方法並說明原因。"
        + "下載檔案、影片或串流時，優先使用 Divebird 下載管理員的工具；它回報需要確認時，請使用者到 Divebird 視窗確認。"
        + "下載前先確認對象（影片標題或網址）就是使用者要的那一個：從瀏覽器偵測到的影音可能是預覽、廣告或其他分頁的內容，對不上就不要下載；下載完成後回報標題、檔名與存放位置。"
        // A small model otherwise repeats one failing command until the turn budget runs out.
        + "同一個指令或工具失敗兩次就換別的方法，不要重複執行完全相同的失敗指令；連續失敗三次就停下來，告訴使用者卡在哪裡並詢問接下來要怎麼做。";
    // Toolsets that let a typed message act on this computer or reach beyond the conversation.
    private static readonly string[] RiskyToolsets = ["terminal", "file", "code_execution", "computer_use", "browser", "cronjob", "delegation"];
    private static readonly TimeSpan RetryBudget = TimeSpan.FromMinutes(2);
    // Hermes sends a keepalive comment while a tool runs, so a long silence means the stream is stuck.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TurnLimit = TimeSpan.FromMinutes(30);
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue auth;
    private readonly string statePath;
    private volatile string? runId;

    internal HermesChat(Uri baseUrl, string apiKey, string statePath)
    {
        // The timeout covers getting the response headers; the streamed body has its own limits.
        http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = baseUrl, Timeout = TimeSpan.FromMinutes(2) };
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

    // Streams the turn so `activity` hears about each tool Hermes starts, with how many times in a row
    // it started the same thing (a retry loop shows as a rising count); returns the final answer.
    internal async Task<string> AskAsync(string text, Action<string, int> activity, CancellationToken token)
    {
        // Retries of one message keep this key, which Hermes uses to admit a turn only once.
        string idempotency = Guid.NewGuid().ToString("N");
        var started = DateTime.UtcNow;
        var backoff = TimeSpan.FromSeconds(2);
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = JsonContent.Create(new
                {
                    model = "hermes-agent",
                    stream = true,
                    messages = new object[] { new { role = "system", content = Instructions }, new { role = "user", content = text } }
                })
            };
            request.Headers.Authorization = auth;
            request.Headers.Add("X-Hermes-Session-Id", Session);
            request.Headers.Add("Idempotency-Key", idempotency);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            // Busy or settling after a restart (429, 503 runtime_draining): refused before the turn
            // started, so asking again cannot run the work twice.
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                using var busy = Parse(await response.Content.ReadAsStringAsync(token));
                if (DateTime.UtcNow - started >= RetryBudget) throw new HermesException("Hermes 一直很忙，請稍後再試。");
                var wait = response.Headers.RetryAfter?.Delta is { } after && after > backoff ? after : backoff;
                Log.Write($"Hermes 暫時忙碌（HTTP {(int)response.StatusCode} {ErrorField(busy, "code")}），{wait.TotalSeconds:0} 秒後重試。");
                await Task.Delay(wait, token);
                backoff = TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new HermesException("Hermes 不接受 API 金鑰，請確認 .env 裡的 API_SERVER_KEY。");
            if (!response.IsSuccessStatusCode)
            {
                using var failure = Parse(await response.Content.ReadAsStringAsync(token));
                throw new HermesException($"HTTP {(int)response.StatusCode} {ErrorField(failure, "message")}".Trim());
            }
            return await ReadAnswerAsync(response, activity, token);
        }
    }

    // Server-sent events: answer text arrives as chat.completion.chunk deltas, tool starts as
    // hermes.tool.progress, provider waits as hermes.status. Text written before a tool call is the
    // agent narrating its plan, so only the text after the last tool call is the answer. The exception
    // mirrors Hermes itself: an answer sent together with bookkeeping calls (memory, todo list, skills)
    // and followed by nothing is still the answer, but Hermes does not stream it a second time.
    private async Task<string> ReadAnswerAsync(HttpResponseMessage response, Action<string, int> activity, CancellationToken token)
    {
        try { return await ReadEventsAsync(response, activity, token); }
        finally { runId = null; }
    }

    private async Task<string> ReadEventsAsync(HttpResponseMessage response, Action<string, int> activity, CancellationToken token)
    {
        string earlier = "";
        bool onlyBookkeeping = false;
        string? lastDoing = null;
        int repeat = 0;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(token);
        turn.CancelAfter(TurnLimit);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token), new UTF8Encoding(false));
        var answer = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null, error = null;
        bool done = false;
        while (!done)
        {
            idle.CancelAfter(IdleLimit);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new HermesException(turn.IsCancellationRequested ? "這次做太久了（超過 30 分鐘），先放棄了。" : "Hermes 好幾分鐘都沒有動靜，這次先放棄了。");
            }
            catch (IOException)
            {
                throw new HermesException("和 Hermes 的連線中途斷了，工作可能只做了一半。");
            }
            if (line == null) break;
            if (line.StartsWith(':')) continue; // keepalive comment
            if (line.StartsWith("event:", StringComparison.Ordinal)) { eventName = line[6..].Trim(); continue; }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
                continue;
            }
            if (line.Length > 0 || data.Length == 0) continue;
            var payload = data.ToString();
            data.Clear();
            string kind = eventName ?? "message";
            eventName = null;
            if (payload == "[DONE]") { done = true; continue; }
            using var document = Parse(payload);
            if (document == null || document.RootElement.ValueKind != JsonValueKind.Object) continue;
            var root = document.RootElement;
            switch (kind)
            {
                case "message":
                    // The completion id doubles as Hermes' run id, which /stop interrupts.
                    if (runId == null && Text(root, "id") is { } id && id.StartsWith("chatcmpl-", StringComparison.Ordinal)) runId = id;
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                        && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        answer.Append(content.GetString());
                    if (root.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object
                        && failure.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                        error = message.GetString();
                    break;
                case "hermes.tool.progress" when Text(root, "status") == "running":
                    string tool = Text(root, "tool") ?? "";
                    bool bookkeeping = tool is "memory" or "todo_list" or "skill_manage" or "session_search" or "setup_beat";
                    var said = answer.ToString().Trim();
                    if (said.Length > 0) { earlier = said; onlyBookkeeping = bookkeeping; }
                    else onlyBookkeeping &= bookkeeping;
                    if (ToolStatus.Short(said, 60) is { } plan) activity(plan, 1);
                    answer.Clear();
                    if (ToolStatus.Describe(tool, Text(root, "label")) is { } doing)
                    {
                        // Narration in between does not break the count: the loop is the same tool again.
                        repeat = doing == lastDoing ? repeat + 1 : 1;
                        lastDoing = doing;
                        activity(doing, repeat);
                    }
                    break;
                case "hermes.status":
                    if (ToolStatus.Short(Text(root, "text"), 60) is { } note) activity(note, 1);
                    break;
                case "approval.request":
                    activity("Hermes 正在等你核准一個操作", 1);
                    break;
            }
        }
        var reply = answer.ToString().Trim();
        if (reply.Length == 0 && onlyBookkeeping) reply = earlier;
        if (!done && reply.Length == 0) throw new HermesException("和 Hermes 的連線中途斷了，工作可能只做了一半。");
        if (reply.Length == 0 && error != null) throw new HermesException(error);
        // A speech bubble cannot show images: Hermes inlines them as data URLs or, in streamed text,
        // refers to them as MEDIA:<path> tags.
        return MediaImage().Replace(InlineImage().Replace(reply, "（圖片）"), "（圖片）").Trim();
    }

    // Asks Hermes to interrupt the turn now streaming (it also stops the background processes that
    // turn started); false when no turn is running or Hermes did not accept.
    internal async Task<bool> StopAsync(CancellationToken token)
    {
        if (runId is not { } run) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/runs/{Uri.EscapeDataString(run)}/stop");
            request.Headers.Authorization = auth;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, limit.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"!\[[^\]]*\]\(data:[^)]*\)")]
    private static partial Regex InlineImage();

    // Anchored on the extension so Chinese text glued to the path survives (x.png，很可愛).
    [GeneratedRegex(@"MEDIA:\s*(?:`[^`\n]+`|""[^""\n]+""|'[^'\n]+'|\S+?\.(?:png|jpe?g|gif|webp|bmp))(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex MediaImage();

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

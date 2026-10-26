using System.Text.Json;
using System.Threading.Channels;

namespace BigBlueFish.HermesBridge;

// Keeps a link to the pet (reconnecting when the pet or its MCP switch restarts) and answers typed
// messages one at a time, in order, so replies never overtake each other.
internal sealed class Bridge(BridgeOptions options)
{
    private const string ThinkingAction = "深度思考碎碎念";
    private const int SayLimit = 4000;
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromMinutes(2);
    // Mirrors CompanionPreferences.BubbleDurations in the pet (seconds a finished bubble stays; 0 = until closed).
    private static readonly int[] BubbleSeconds = [5, 10, 15, 30, 60, 120, 180, 0];
    private static readonly TimeSpan MinimumShow = TimeSpan.FromSeconds(1.5);
    private volatile string activity = "";
    private readonly Channel<string> inbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private volatile PetLink? pet;
    private bool reconnects;

    internal async Task<int> RunAsync(CancellationToken token)
    {
        using var hermes = new HermesChat(options.HermesUrl, options.ApiKey, options.StatePath);
        reconnects = true;
        Log.Write($"魚魚 MCP：{options.McpCommand}");
        Log.Write($"Hermes：{options.HermesUrl}（{(await hermes.IsUpAsync(token) ? "已啟動" : "目前連不上，收到訊息時會再試")}）");
        if (await hermes.RiskyToolsetsAsync(token) is { Length: > 0 } risky)
            Log.Write($"注意：Hermes 目前開著 {string.Join("、", risky)} 工具組。任何能在魚魚輸入框打字的人，都能讓 Hermes 以你的身分執行指令、修改檔案；訊息也會送到 Hermes 設定的模型服務。");
        var worker = Task.Run(() => AnswerAsync(hermes, token), CancellationToken.None);
        while (!token.IsCancellationRequested)
        {
            PetLink? link = null;
            try
            {
                link = await PetLink.ConnectAsync(options.McpCommand, inbox.Writer, token);
                pet = link;
                Log.Write("已連上魚魚。雙擊魚魚輸入訊息就會交給 Hermes；輸入 /new 開始新的對話。");
                await link.Closed.WaitAsync(token);
                Log.Write("和魚魚的連線中斷了（桌寵關閉，或 MCP 介面被關掉）。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                Log.Write("連不上魚魚：" + error.Message);
            }
            finally
            {
                // Unpublish first so the worker stops picking up a link that is being torn down.
                pet = null;
                if (link != null) await link.DisposeAsync();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { }
        }
        inbox.Writer.TryComplete();
        await worker;
        Log.Write("橋接程式結束。");
        return 0;
    }

    // Test path: one message through the same steps, without listening for typed input.
    internal async Task<int> AskOnceAsync(string text, CancellationToken token)
    {
        using var hermes = new HermesChat(options.HermesUrl, options.ApiKey, options.StatePath);
        var link = await PetLink.ConnectAsync(options.McpCommand, Channel.CreateUnbounded<string>().Writer, token);
        try
        {
            pet = link;
            Console.WriteLine(await HandleAsync(hermes, text, token));
            return 0;
        }
        finally
        {
            pet = null;
            await link.DisposeAsync();
        }
    }

    private async Task AnswerAsync(HermesChat hermes, CancellationToken token)
    {
        try
        {
            await foreach (var text in inbox.Reader.ReadAllAsync(token))
            {
                try { await HandleAsync(hermes, text, token); }
                // Only stopping the bridge ends the loop; anything else costs just this message.
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Log.Write("處理訊息時出錯：" + error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<string> HandleAsync(HermesChat hermes, string text, CancellationToken token)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        Log.Write("收到：" + text);
        if (string.Equals(text, "/new", StringComparison.OrdinalIgnoreCase))
        {
            hermes.NewSession();
            Log.Write("開始新的對話：" + hermes.Session);
            const string fresh = "好～我們重新開始聊吧！";
            await DeliverAsync(fresh, token);
            return fresh;
        }
        activity = "收到～讓魚魚想一想";
        using var thinking = CancellationTokenSource.CreateLinkedTokenSource(token);
        var animation = Task.WhenAll(ThinkAsync(thinking.Token), ShowProgressAsync(DateTime.UtcNow, thinking.Token));
        string reply;
        try
        {
            reply = await hermes.AskAsync(text, now =>
            {
                if (now == activity) return;
                activity = now;
                Log.Write("進度：" + now);
            }, token);
            if (reply.Length == 0) reply = "（Hermes 這次沒有回答內容）";
        }
        catch (HermesException error) { reply = "主人，Hermes 出了點狀況：" + error.Message; }
        catch (HttpRequestException) { reply = "主人，魚魚連不上 Hermes，請確認 Hermes 有在執行喔。"; }
        // HttpClient reports its own timeout as a cancellation the bridge did not ask for.
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { reply = "主人，Hermes 太久沒有回應，這次先放棄了。"; }
        finally
        {
            thinking.Cancel();
            try { await animation; }
            catch (Exception error) when (!token.IsCancellationRequested) { Log.Write("思考動作出錯：" + error.Message); }
        }
        Log.Write("回覆：" + reply);
        await DeliverAsync(Fit(reply), token);
        return reply;
    }

    // say accepts at most 4000 UTF-16 units; never cut an emoji or other surrogate pair in half.
    private static string Fit(string text)
    {
        if (text.Length <= SayLimit) return text;
        int cut = SayLimit - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "…";
    }

    // The thinking clip lasts about ten seconds. Replay it while the pet is merely idling. When Hermes
    // plays or queues an action of its own (interrupt_action would clear its queue), hold off until the
    // pet has been idle for a while, so a sequence of Hermes' actions is not cut in between.
    private async Task ThinkAsync(CancellationToken token)
    {
        try
        {
            await ActAsync(ThinkingAction, token);
            int quiet = -1;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (pet is not { } link) continue;
                string? json = await CallAsync(link, "get_settings", new { }, token);
                if (json == null) continue;
                using var document = JsonDocument.Parse(json);
                var state = document.RootElement.GetProperty("state");
                string action = state.GetProperty("action").GetString() ?? "";
                string phase = state.GetProperty("animationPhase").GetString() ?? "";
                if (state.GetProperty("queuedActions").GetArrayLength() > 0 || phase == "External" && action != ThinkingAction) { quiet = 0; continue; }
                // Leave drags, movement and the pet's own events alone; only an idle pet starts thinking again.
                if (phase is not ("Idle" or "PuppetIdle") || quiet >= 0 && ++quiet < 5) continue;
                quiet = -1;
                await ActAsync(ThinkingAction, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write("看不懂魚魚目前的狀態，這次先停止思考動作：" + error.Message);
        }
    }

    // Keeps a progress bubble up while Hermes works: what it is doing and for how long. A finished
    // bubble hides after the user's chosen duration, so the text is said again before that happens.
    // The first line answers the user at once; later ones give way while the pet shows something the
    // user asked it for (weather, a speech test), and a bubble the user closed stays closed until
    // Hermes moves on to something new.
    private async Task ShowProgressAsync(DateTime started, CancellationToken token)
    {
        try
        {
            var refresh = await RefreshIntervalAsync(token);
            string shown = "";
            DateTime said = DateTime.MinValue, quietUntil = DateTime.MinValue;
            while (true)
            {
                string now = activity;
                var since = DateTime.UtcNow - said;
                bool news = now != shown;
                if (DateTime.UtcNow >= quietUntil && (news && since >= MinimumShow || since >= refresh))
                {
                    var bubble = said == DateTime.MinValue ? Bubble.Free : await BubbleAsync(token);
                    if (bubble == Bubble.PetBusy || bubble == Bubble.Closed && !news)
                        quietUntil = DateTime.UtcNow.AddSeconds(2);
                    else
                    {
                        var elapsed = DateTime.UtcNow - started;
                        string text = Ellipsis(now) + (elapsed < TimeSpan.FromSeconds(5) ? "" : $"（{ToolStatus.Elapsed(elapsed)}）");
                        await SayAsync(text, token);
                        shown = now;
                        said = DateTime.UtcNow;
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private enum Bubble { Free, PetBusy, Closed }

    // From the pet's get_settings state; an older pet without these fields always reads as Free.
    private async Task<Bubble> BubbleAsync(CancellationToken token)
    {
        if (pet is not { } link || await CallAsync(link, "get_settings", new { }, token) is not { } json) return Bubble.Free;
        try
        {
            using var document = JsonDocument.Parse(json);
            var state = document.RootElement.GetProperty("state");
            bool? Flag(string name) => state.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            bool? visible = Flag("bubbleVisible"), external = Flag("externalBubble");
            if (Flag("speechPending") == true || visible == true && external == false) return Bubble.PetBusy;
            return visible == false && external == true ? Bubble.Closed : Bubble.Free;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return Bubble.Free; }
    }

    private async Task<TimeSpan> RefreshIntervalAsync(CancellationToken token)
    {
        int seconds = 5;
        if (pet is { } link && await CallAsync(link, "get_settings", new { }, token) is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                int index = document.RootElement.GetProperty("settings").GetProperty("BubbleDurationIndex").GetInt32();
                if (index >= 0 && index < BubbleSeconds.Length) seconds = BubbleSeconds[index];
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return TimeSpan.FromSeconds(seconds == 0 ? 15 : Math.Clamp(seconds - 2, 3, 15));
    }

    private static string Ellipsis(string text) =>
        text.Length > 0 && "。！？…～~!?.）)」』".Contains(text[^1]) ? text : text + "…";

    // The final answer is worth waiting for: if the pet is gone (restarting, MCP switched off and
    // on), hold it until the link comes back and try once more.
    private async Task DeliverAsync(string text, CancellationToken token)
    {
        var failed = pet;
        if (failed != null && await CallAsync(failed, "say", new { text }, token) != null) return;
        if (reconnects)
        {
            Log.Write("魚魚暫時沒有連線，最多等兩分鐘再把回覆送過去。");
            var deadline = DateTime.UtcNow + ReconnectWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                if (pet is { } link && link != failed)
                {
                    if (await CallAsync(link, "say", new { text }, token) != null) return;
                    break;
                }
            }
        }
        Log.Write("回覆沒能送到魚魚，內容請看上面的紀錄。");
    }

    private async Task SayAsync(string text, CancellationToken token)
    {
        if (pet is { } link) await CallAsync(link, "say", new { text }, token);
    }

    private async Task ActAsync(string action, CancellationToken token)
    {
        if (pet is { } link) await CallAsync(link, "interrupt_action", new { action }, token);
    }

    // Null when the call did not go through; a dropped link surfaces as one of these exceptions.
    private static async Task<string?> CallAsync(PetLink link, string tool, object arguments, CancellationToken token)
    {
        try { return await link.CallToolAsync(tool, arguments, token) ?? ""; }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Write($"{tool}：{error.Message}");
            return null;
        }
    }
}

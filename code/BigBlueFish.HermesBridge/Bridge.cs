using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace BigBlueFish.HermesBridge;

// Keeps a link to the pet (reconnecting when the pet or its MCP switch restarts) and answers typed
// messages one at a time, in order, so replies never overtake each other. /stop and /jobs are the
// exceptions: they are handled the moment they arrive, /stop interrupting the turn being answered.
// In between turns it tells what Hermes' cron jobs left behind, and when the user sits down for the first time in
// a day, or comes back after a long break, a briefing goes before them (Briefings). An event on the user's Google
// Calendar about to start goes before both (Calendar, EventReminders).
internal sealed class Bridge(BridgeOptions options)
{
    private const string ThinkingAction = "深度思考碎碎念";
    private const string ReportAction = "点击回应-元气挥手"; // a cron report comes in: the fish waves before telling it
    // The pet's four Disabled clips never play on their own, so they are its reactions to how a turn goes.
    private const string FailedToolAction = "工作状态-垂头叹气冒汗"; // a tool call failed: a red ✕ and a sigh, about 4 s
    private const string StuckAction = "余额-数金皱眉";              // the third failure in one turn: a worried frown
    private const string FailedTaskAction = "余额-分文不剩";         // the request itself failed: sits down, deflated
    private const string StoppedAction = "余额-袋空如洗";            // /stop: empties the bag and lets it go
    private static readonly string[] Reactions = [FailedToolAction, StuckAction, FailedTaskAction, StoppedAction];
    private const int SayLimit = 4000;
    // A turn whose tool calls failed this often, or that made the very same call this many times, is going round
    // in circles (scenario tests, 2026-10-28: for a file that was not there, 94 calls with 11 failures in seven
    // minutes, and another time the same ls 40 times): the bridge asks Hermes to stop it.
    private const int RunawayFailures = 6;
    private const int RunawayRepeats = 8;
    private static readonly TimeSpan ReconnectWait = TimeSpan.FromMinutes(2);
    // Mirrors CompanionPreferences.BubbleDurations in the pet (seconds a finished bubble stays; 0 = until closed).
    private static readonly int[] BubbleSeconds = [5, 10, 15, 30, 60, 120, 180, 0];
    private static readonly TimeSpan MinimumShow = TimeSpan.FromSeconds(1.5);
    // What Hermes is doing and how many times in a row it started it; replaced whole, never mutated.
    private sealed record Progress(string Text, int Repeat);
    private volatile Progress progress = new("", 1);
    // Typed messages first reach the dispatcher; everything but /stop and /jobs then waits in inbox for
    // the worker. Only the multi-reader channel can tell how many are waiting, which reports check.
    private readonly Channel<PetMessage> incoming = Channel.CreateUnbounded<PetMessage>();
    private readonly Channel<PetMessage> inbox = Channel.CreateUnbounded<PetMessage>(new UnboundedChannelOptions { SingleReader = true });
    private volatile CancellationTokenSource? turn; // the Hermes turn being answered
    private readonly HermesLog log = new(options.HermesLogPath);
    private int failures; // tool calls that failed in the turn being answered
    private volatile PetLink? pet;
    private long linkedAt; // UTC ticks when the current link came up: reports older than that were missed
    private bool reconnects;
    private readonly CronReports reports = new(options.HermesHome, options.StatePath);
    private int answering; // typed messages waiting for or being answered; reports never cut in on them
    // Recent turns, so a report Hermes saved during one (asked to run a job now) is not told twice.
    private sealed record Answered(DateTime Asked, DateTime Done, string Reply);
    private readonly List<Answered> answered = [];
    // When the bridge last put words in the bubble (a reply, a report, the job list) and how long they
    // stay before a report may replace them; null keeps them for the user's bubble duration.
    private sealed record Spoken(DateTime At, TimeSpan? Keep);
    private volatile Spoken spoken = new(DateTime.MinValue, null);
    private long progressHeld; // UTC ticks: progress bubbles wait until then while the job list is read
    private DateTime waved = DateTime.MinValue; // when the last round of reports opened with a wave
    private PetLink? counted; // the link the "while you were away" count was said on
    private int folded; // runs left out for a newer one of the same job, not yet mentioned in a count
    private bool round; // a round of reports is under way: its count, if any, was already said
    private static readonly TimeSpan ListReading = TimeSpan.FromSeconds(15);
    // The pet tells when the user comes back; a new link is heard here too, as "linked".
    private readonly Channel<PetEvent> events = Channel.CreateUnbounded<PetEvent>(new UnboundedChannelOptions { SingleReader = true });
    // A daily briefing or welcome back on its way: cron reports wait for it, and the report loop says it once
    // Hermes (or the fallback) has written it. Day is the briefing day a daily one counts for.
    // Asked is how many messages had been typed when it was made: a welcome back is dropped once the user is talking.
    private sealed class Briefing(Briefings.Kind kind, DateTime day, int asked)
    {
        internal Briefings.Kind Kind { get; } = kind;
        internal DateTime Day { get; } = day;
        internal int Asked { get; } = asked;
        internal DateTime Created { get; } = DateTime.UtcNow;
        private volatile string? text;
        internal string? Text => text;
        internal DateTime Ready { get; private set; }
        internal void Write(string words) { Ready = DateTime.UtcNow; text = words; }
    }
    private Briefing? briefing; // read with Volatile.Read, replaced with Interlocked
    private Briefing? Pending => Volatile.Read(ref briefing);
    private volatile bool deciding; // an event is being looked at: reports wait until it is known whether a briefing comes
    private volatile bool recheck; // the user was not at the computer when the link came up: look again for the daily briefing
    private long nudged; // UTC ticks: when the report loop last asked BriefAsync to look again
    private int typedCount; // messages typed so far
    private volatile string[] waiting = []; // jobs of the cron reports waiting to be told, for a briefing to mention
    private double lastIdle; // the idle seconds the report loop saw last
    private long returnHold; // UTC ticks: right after a break the reports give a briefing a moment to be decided
    private static readonly TimeSpan BriefingLimit = TimeSpan.FromSeconds(90);
    // Past these after it was written a briefing no longer fits the moment (the user went off again, a game).
    private static readonly TimeSpan DailyStale = TimeSpan.FromMinutes(15), WelcomeStale = TimeSpan.FromMinutes(10);
    // Reports (and briefings) wait while the keyboard and mouse have been still this long: nobody would see them.
    private static readonly TimeSpan AwayIdle = TimeSpan.FromSeconds(120);
    // The pet's own threshold for a break (CompanionReminders.BreakLength), after which it tells user_returned.
    private const double BreakSeconds = 300;
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
    private readonly Calendar calendar = new(options.HermesHome);
    // How old the calendar read for the reminders may get (five minutes unless a test asks otherwise); the pet's
    // EventReminderMinutes and whether the user is there are looked at 20 times as often.
    private readonly TimeSpan calendarRefresh = options.CalendarRefresh ?? TimeSpan.FromMinutes(5);
    // The coming events and how many minutes before its start each is told (0: not at all); replaced whole.
    private sealed record Upcoming(IReadOnlyList<CalendarEvent> Events, int Lead);
    private volatile Upcoming upcoming = new([], 0);
    private readonly HashSet<string> reminded = []; // event starts told (EventReminders.Key); persisted
    // Reminders due and not said yet, by EventReminders.Key, so one held until too late is let go with a line in the
    // log; only the report loop touches it.
    private readonly Dictionary<string, CalendarEvent> holding = [];
    // Before a reminder is said the calendar is looked at again unless read this recently.
    private static readonly TimeSpan ConfirmAge = TimeSpan.FromSeconds(30);
    private bool lapseTold; // the user was told the calendar's authorization lapsed (Calendar.Lapsed); persisted

    internal async Task<int> RunAsync(CancellationToken token)
    {
        using var hermes = new HermesChat(options.HermesUrl, options.ApiKey, options.StatePath);
        reconnects = true;
        Log.Write($"魚魚 MCP：{options.McpCommand}");
        bool up = await hermes.IsUpAsync(token);
        Log.Write($"Hermes：{options.HermesUrl}（{(up ? "已啟動" : "目前連不上，收到訊息時會再試")}）");
        if (await hermes.RiskyToolsetsAsync(token) is { Length: > 0 } risky)
            Log.Write($"注意：Hermes 目前開著 {string.Join("、", risky)} 工具組。任何能在魚魚輸入框打字的人，都能讓 Hermes 以你的身分執行指令、修改檔案；訊息也會送到 Hermes 設定的模型服務。");
        // Only the running Hermes gateway fires cron jobs; the bridge just tells what they leave behind.
        int active = CronReports.ActiveJobs(options.HermesHome);
        if (active > 0)
            Log.Write(up ? $"Hermes 有 {active} 個啟用中的排程，執行結果出來時魚魚會說出來（輸入 /jobs 查看）。"
                : $"注意：Hermes 有 {active} 個啟用中的排程，但 Hermes 現在連不上，排程到時間也不會執行；請先啟動 Hermes gateway。");
        var worker = Task.Run(() => AnswerAsync(hermes, token), CancellationToken.None);
        var dispatcher = Task.Run(() => DispatchAsync(hermes, token), CancellationToken.None);
        var watching = Task.Run(() => reports.WatchAsync(token), CancellationToken.None);
        var reporting = Task.Run(() => ReportAsync(token), CancellationToken.None);
        var briefer = Task.Run(() => BriefAsync(token), CancellationToken.None);
        var looking = Task.Run(() => WatchCalendarAsync(token), CancellationToken.None);
        while (!token.IsCancellationRequested)
        {
            PetLink? link = null;
            try
            {
                link = await PetLink.ConnectAsync(options.McpCommand, incoming.Writer, token, events.Writer);
                Interlocked.Exchange(ref linkedAt, DateTime.UtcNow.Ticks);
                pet = link;
                // The user may already be sitting there (the fish or the bridge just started): see whether a briefing is due.
                events.Writer.TryWrite(new PetEvent("linked", default));
                Log.Write("已連上魚魚。雙擊魚魚輸入訊息就會交給 Hermes；輸入 /new 開始新的對話，/stop 停止目前的工作，/jobs 查看排程。");
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
        incoming.Writer.TryComplete();
        await dispatcher; // completes inbox on the way out
        await worker;
        await watching;
        await reporting;
        await looking;
        events.Writer.TryComplete();
        await briefer;
        Log.Write("橋接程式結束。");
        return 0;
    }

    // /stop cannot wait behind the turn it is meant to stop, and /jobs needs no turn at all, so typed
    // text is sorted here first.
    private async Task DispatchAsync(HermesChat hermes, CancellationToken token)
    {
        try
        {
            await foreach (var typed in incoming.Reader.ReadAllAsync(token))
            {
                var text = typed.Text.Trim();
                string ignored = typed.Attachments.Count > 0 ? $"（一起送來的 {typed.Attachments.Count} 個附件不處理）" : "";
                if (text.Equals("/stop", StringComparison.OrdinalIgnoreCase) || text == "/停止")
                {
                    Log.Write("收到：" + text + ignored);
                    if (turn is not { } running) { await SayAsync("目前沒有在做的事喔。", token); continue; }
                    await SayAsync("好，正在請 Hermes 停下來…", token);
                    // Hermes' own stop also ends the background processes of the turn; dropping the
                    // stream afterwards makes sure the bridge itself moves on either way.
                    if (!await hermes.StopAsync(token)) Log.Write("Hermes 沒有接受停止要求，改為直接中斷連線。");
                    try { running.Cancel(); } catch (ObjectDisposedException) { }
                    continue;
                }
                if (CronReports.IsJobsCommand(text))
                {
                    Log.Write("收到：" + text + ignored);
                    // The progress bubble of a running turn gives the list time to be read first.
                    if (turn != null) Interlocked.Exchange(ref progressHeld, (DateTime.UtcNow + ListReading).Ticks);
                    string list = Fit(JobList());
                    if (pet is { } link && await CallAsync(link, "say", new { text = list }, token) != null) spoken = new(DateTime.UtcNow, null);
                    continue;
                }
                if ((text.Length > 0 || typed.Attachments.Count > 0) && turn != null)
                    await SayAsync("收到，等目前這件事做完就處理；輸入 /stop 可以先停下來。", token);
                Interlocked.Increment(ref answering); // AnswerAsync counts it down once it is answered
                Interlocked.Increment(ref typedCount);
                await inbox.Writer.WriteAsync(typed, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { inbox.Writer.TryComplete(); }
    }

    // Test path: one message through the same steps, without listening for typed input.
    internal async Task<int> AskOnceAsync(PetMessage message, CancellationToken token)
    {
        using var hermes = new HermesChat(options.HermesUrl, options.ApiKey, options.StatePath);
        var link = await PetLink.ConnectAsync(options.McpCommand, Channel.CreateUnbounded<PetMessage>().Writer, token);
        try
        {
            pet = link;
            Console.WriteLine(await HandleAsync(hermes, message, token));
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
            await foreach (var message in inbox.Reader.ReadAllAsync(token))
            {
                try { await HandleAsync(hermes, message, token); }
                // Only stopping the bridge ends the loop; anything else costs just this message.
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Log.Write("處理訊息時出錯：" + error.Message);
                }
                finally { Interlocked.Decrement(ref answering); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<string> HandleAsync(HermesChat hermes, PetMessage message, CancellationToken token)
    {
        string text = message.Text.Trim();
        var files = message.Attachments;
        // Files sent without a word are a request of their own.
        if (text.Length == 0 && files.Count == 0) return "";
        Log.Write("收到：" + text + (files.Count > 0 ? "（附件：" + string.Join("、", files.Select(file => file.Name)) + "）" : ""));
        if (string.Equals(text, "/new", StringComparison.OrdinalIgnoreCase))
        {
            hermes.NewSession();
            Log.Write("開始新的對話：" + hermes.Session);
            const string fresh = "好～我們重新開始聊吧！";
            if (files.Count == 0)
            {
                await DeliverAsync(fresh, token);
                return fresh;
            }
            text = ""; // the files open the new conversation
        }
        // The listening bridge answers /jobs on arrival; this is the --ask path.
        if (CronReports.IsJobsCommand(text))
        {
            string list = JobList();
            await DeliverAsync(Fit(list), token);
            return list;
        }
        progress = new Progress(files.Count > 0 ? $"收到～（附 {files.Count} 個檔案）讓魚魚看看" : "收到～讓魚魚想一想", 1);
        Volatile.Write(ref failures, 0);
        using var thinking = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reacted = DateTime.MinValue;
        string? reaction = null;
        string? runaway = null; // why the bridge stopped a turn going round in circles
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task StopRunawayAsync(string why)
        {
            if (runaway != null || stop.IsCancellationRequested) return;
            runaway = why;
            Log.Write(why + "，請 Hermes 先停下來。");
            if (!await hermes.StopAsync(CancellationToken.None)) Log.Write("Hermes 沒有接受停止要求，改為直接中斷連線。");
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        // Each failed tool call gets a short reaction, never restarted before its clip ends, so a fast
        // retry loop does not replay it over and over; from the third failure on the reaction turns into
        // a worried frown, which waits for the clip on screen to finish.
        var watching = log.WatchAsync(hermes.Session, async tool =>
        {
            int count = Interlocked.Increment(ref failures);
            Log.Write($"工具失敗：{tool}（這次已失敗 {count} 次）");
            if (count == RunawayFailures) { await StopRunawayAsync($"這次已經失敗 {count} 次"); return; }
            string wanted = count >= 3 ? StuckAction : FailedToolAction;
            var since = DateTime.UtcNow - reacted;
            // Found only after the turn ended: the ending below shows it instead.
            if (thinking.IsCancellationRequested || wanted == reaction && since < ClipLength(wanted)) return;
            try
            {
                if (reaction != null && since < ClipLength(reaction)) await Task.Delay(ClipLength(reaction) - since, thinking.Token);
                reacted = DateTime.UtcNow;
                reaction = wanted;
                await ReactAsync(wanted, thinking.Token);
            }
            catch (OperationCanceledException) when (thinking.IsCancellationRequested) { }
        }, thinking.Token);
        var animation = Task.WhenAll(ThinkAsync(thinking.Token), ShowProgressAsync(DateTime.UtcNow, thinking.Token), watching);
        string reply;
        string? ending = null;
        var asked = DateTime.UtcNow;
        turn = stop;
        try
        {
            reply = await hermes.AskAsync(text, files, (now, repeat) =>
            {
                var next = new Progress(now, repeat);
                if (next == progress) return;
                progress = next;
                Log.Write("進度：" + now + (repeat > 1 ? $"（第 {repeat} 次）" : ""));
            }, stop.Token, times => { if (times == RunawayRepeats) _ = StopRunawayAsync($"同一個動作已經重複做了 {times} 次"); });
            if (reply.Length == 0) reply = "（Hermes 這次沒有回答內容）";
        }
        catch (HermesException error) { reply = "主人，Hermes 出了點狀況：" + error.Message; ending = FailedTaskAction; }
        catch (HttpRequestException) { reply = "主人，魚魚連不上 Hermes，請確認 Hermes 有在執行喔。"; ending = FailedTaskAction; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested && runaway != null)
        {
            reply = $"主人，Hermes 這件事好像在原地打轉（{runaway}），魚魚先請它停下來了。可以換個說法、把要做的事說得更具體一點，再試一次。";
            ending = FailedTaskAction;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested)
        {
            reply = "好，這件事先停下來了。需要的話再告訴魚魚一次要做什麼。";
            ending = StoppedAction;
        }
        // HttpClient reports its own timeout as a cancellation the bridge did not ask for.
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            reply = "主人，Hermes 太久沒有回應，這次先放棄了。";
            ending = FailedTaskAction;
        }
        finally
        {
            turn = null;
            // Hermes may have added, moved or deleted an event in this turn: the reminders read the calendar afresh.
            calendar.Forget();
            thinking.Cancel();
            try { await animation; }
            catch (Exception error) when (!token.IsCancellationRequested) { Log.Write("思考動作出錯：" + error.Message); }
        }
        Log.Write("回覆：" + reply);
        lock (answered)
        {
            answered.Add(new(asked, DateTime.UtcNow, reply));
            if (answered.Count > 20) answered.RemoveAt(0);
        }
        // Failures the turn ended too soon to react to still get the reaction they called for.
        int failedTools = Volatile.Read(ref failures);
        string? deserved = failedTools >= 3 ? StuckAction : failedTools > 0 ? FailedToolAction : null;
        if (ending == null && deserved != null && deserved != reaction) ending = deserved;
        // After the thinking loop has stopped, so nothing replays the thinking clip over it. /stop cuts in
        // at once (the user asked for it); the others wait behind anything Hermes lined up.
        if (ending == StoppedAction) await ActAsync(ending, token);
        else if (ending != null) await ReactAsync(ending, token);
        await DeliverAsync(Fit(reply), token);
        return reply;
    }

    // Clip lengths (ffprobe): the red ✕ about 4.9 s, the 余额 clips about 10 s.
    private static TimeSpan ClipLength(string action) => TimeSpan.FromSeconds(action == FailedToolAction ? 5.5 : 10.5);

    // interrupt_action clears the queue Hermes fills with play_action and stops a walk_to on the spot, so
    // while Hermes has a clip of its own playing or queued, or the fish is on its way somewhere, a
    // reaction waits in line instead of wiping it (the pet plays queued actions after arriving).
    private async Task ReactAsync(string action, CancellationToken token)
    {
        if (pet is not { } link) return;
        bool queue = false;
        if (await CallAsync(link, "get_settings", new { }, token) is { } json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var state = document.RootElement.GetProperty("state");
                string current = state.GetProperty("action").GetString() ?? "";
                string phase = state.GetProperty("animationPhase").GetString() ?? "";
                queue = state.GetProperty("queuedActions").GetArrayLength() > 0 || Walking(state, phase)
                    || phase == "External" && current != ThinkingAction && !Reactions.Contains(current);
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        await CallAsync(link, queue ? "play_action" : "interrupt_action", new { action }, token);
    }

    // WalkStart, WalkLoop and WalkStop: walk_to is moving the fish (or turning it round on arrival). Between
    // two stops of a walk_to tour the fish idles, but state.tour still says it has somewhere to go.
    private static bool Walking(JsonElement state, string phase) =>
        phase.StartsWith("Walk", StringComparison.Ordinal) || state.TryGetProperty("tour", out var tour) && tour.ValueKind == JsonValueKind.Object;

    // Null when the pet cannot be asked or its answer is not understood.
    private async Task<bool?> WalkingAsync(CancellationToken token)
    {
        if (pet is not { } link || await CallAsync(link, "get_settings", new { }, token) is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var state = document.RootElement.GetProperty("state");
            return Walking(state, state.GetProperty("animationPhase").GetString() ?? "");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
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
    // plays or queues an action of its own (interrupt_action would clear its queue) or sends the fish
    // walking (interrupt_action would stop it halfway), hold off until the pet has been idle for a while,
    // so a sequence of Hermes' actions is not cut in between.
    private async Task ThinkAsync(CancellationToken token)
    {
        try
        {
            int quiet = -1;
            // The previous answer may have sent the fish off without waiting for it to arrive.
            if (await WalkingAsync(token) == true) quiet = 0;
            else await ActAsync(ThinkingAction, token);
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
                if (state.GetProperty("queuedActions").GetArrayLength() > 0 || Walking(state, phase) || phase == "External" && action != ThinkingAction) { quiet = 0; continue; }
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
            Progress? shown = null;
            int shownFailures = 0;
            DateTime said = DateTime.MinValue, quietUntil = DateTime.MinValue;
            while (true)
            {
                var now = progress;
                int failed = Volatile.Read(ref failures);
                var since = DateTime.UtcNow - said;
                bool news = now != shown || failed != shownFailures;
                bool held = DateTime.UtcNow.Ticks < Interlocked.Read(ref progressHeld);
                if (DateTime.UtcNow >= quietUntil && !held && (news && since >= MinimumShow || since >= refresh))
                {
                    var bubble = said == DateTime.MinValue ? Bubble.Free : await BubbleAsync(token);
                    if (bubble == Bubble.PetBusy || bubble == Bubble.Closed && !news)
                        quietUntil = DateTime.UtcNow.AddSeconds(2);
                    else
                    {
                        // 正在電腦上執行指令…（第 12 次，3 分 05 秒）: a rising count shows a retry loop, not a hang.
                        var elapsed = DateTime.UtcNow - started;
                        var notes = new List<string>();
                        if (now.Repeat > 1) notes.Add($"第 {now.Repeat} 次");
                        if (failed > 0) notes.Add($"失敗 {failed} 次");
                        if (elapsed >= TimeSpan.FromSeconds(5)) notes.Add(ToolStatus.Elapsed(elapsed));
                        string text = Ellipsis(now.Text) + (notes.Count > 0 ? "（" + string.Join("，", notes) + "）" : "");
                        await SayAsync(text, token);
                        shown = now;
                        shownFailures = failed;
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
            // walkBubble: a walk_to tour is showing which stop the fish has reached.
            if (Flag("speechPending") == true || visible == true && (external == false || Flag("walkBubble") == true)) return Bubble.PetBusy;
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
        if (failed != null && await CallAsync(failed, "say", new { text }, token) != null) { spoken = new(DateTime.UtcNow, null); return; }
        if (reconnects)
        {
            Log.Write("魚魚暫時沒有連線，最多等兩分鐘再把回覆送過去。");
            var deadline = DateTime.UtcNow + ReconnectWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                if (pet is { } link && link != failed)
                {
                    if (await CallAsync(link, "say", new { text }, token) != null) { spoken = new(DateTime.UtcNow, null); return; }
                    break;
                }
            }
        }
        Log.Write("回覆沒能送到魚魚，內容請看上面的紀錄。");
    }

    private string JobList()
    {
        string list = CronReports.DescribeJobs(options.HermesHome);
        Log.Write("排程清單：" + list.Replace("\n", " / "));
        return list;
    }

    // Typed text not yet answered, from the moment the pet hands it over.
    private bool Busy => Volatile.Read(ref answering) > 0 || incoming.Reader.Count > 0;

    // Tells cron reports one at a time, oldest first, whenever the pet is free: never during a turn or
    // while the user types, and only after the bubble's last words had their time. A report counts as
    // told only once say went through, so one the pet missed waits for the next link (within the 12-hour
    // catch-up); of several runs of one job waiting together only the newest are told.
    private async Task ReportAsync(CancellationToken token)
    {
        var pending = new List<CronReport>();
        try
        {
            while (true)
            {
                if (pending.Count > 0 || Pending != null || recheck || DueEvents().Count > 0 || holding.Count > 0 || LapseUntold) await Task.Delay(TimeSpan.FromSeconds(1), token);
                else
                {
                    // Woken by a report, or after a second to see whether a briefing is on its way.
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(token);
                    tick.CancelAfter(TimeSpan.FromSeconds(1));
                    try { if (!await reports.Found.WaitToReadAsync(tick.Token)) return; }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                }
                while (reports.Found.TryRead(out var found)) pending.Add(found);
                try { await TellAsync(pending, token); }
                // Only stopping the bridge ends the loop; the report waits for the next round.
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Log.Write("說排程回報時出錯：" + error.Message);
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // One step of ReportAsync: an event about to start, the briefing, a lapsed calendar, the count or the oldest
    // report, if the pet is free for it now.
    // What is no longer news is sorted out first, even with the pet away, so it is saved as done right away.
    private async Task TellAsync(List<CronReport> pending, CancellationToken token)
    {
        pending.Sort((a, b) => a.Written.CompareTo(b.Written));
        Settle(pending);
        folded += Fold(pending);
        waiting = pending.Select(report => report.Job).ToArray();
        if (await RemindAsync(token)) return;
        if (Pending is { } brief && !await BriefingTurnAsync(brief, pending, token)) return;
        if (deciding) return;
        // A daily briefing still owed today goes before the reports too. The report loop sees each second whether
        // the user is at the computer and the fish free for it, and then asks BriefAsync to decide at once.
        if (recheck)
        {
            if (pet is { } free && DateTime.UtcNow.Ticks - Interlocked.Read(ref nudged) > TimeSpan.FromSeconds(10).Ticks
                && await FreeForReportAsync(free, token, 60) != null)
            {
                Interlocked.Exchange(ref nudged, DateTime.UtcNow.Ticks);
                events.Writer.TryWrite(new PetEvent("linked", default));
            }
            return;
        }
        if (await TellLapseAsync(token)) return;
        if (pending.Count == 0)
        {
            folded = 0;
            round = false;
            return;
        }
        if (Busy || pet is not { } link) return;
        if (await FreeForReportAsync(link, token) is not { } phase) return;
        // The wave and the count open a round of reports; they are not repeated for each one. Only an
        // idle fish waves: a clip of its own (an event, a reaction to a failed turn) plays on.
        if (DateTime.UtcNow - waved > TimeSpan.FromMinutes(2))
        {
            waved = DateTime.UtcNow;
            if (phase is "Idle" or "PuppetIdle") await ReactAsync(ReportAction, token);
        }
        long linked = Interlocked.Read(ref linkedAt);
        int missed = pending.Count(report => report.Written.Ticks < linked);
        // Runs folded into a round already under way only go to the log; a second count would contradict the first.
        if (round) folded = 0;
        string? count = folded > 0 ? $"{(missed > 0 ? "你不在的時候" : "剛才")}有 {folded + pending.Count} 則排程回報，同一個排程只說最新的結果。"
            : missed > 1 && counted != link ? $"你不在的時候有 {missed} 則排程回報。" : null;
        // A briefing decided meanwhile goes first.
        if (Pending != null || deciding) return;
        if (count != null)
        {
            counted = link;
            if (await CallAsync(link, "say", new { text = count }, token) != null)
            {
                Log.Write(count);
                folded = 0;
                round = true;
                spoken = new(DateTime.UtcNow, TimeSpan.FromSeconds(3));
                return;
            }
        }
        var next = pending[0];
        string text = Fit(next.Speech);
        if (await CallAsync(link, "say", new { text }, token) == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token); // kept for the next try, or the next link
            return;
        }
        Log.Write("排程回報：" + text);
        spoken = new(DateTime.UtcNow, null);
        pending.RemoveAt(0);
        waiting = pending.Select(report => report.Job).ToArray();
        reports.Done(next);
        round = pending.Count > 0;
    }

    // Leaves out reports too old to be news, failures that turned out to continue a streak already told,
    // and the ones a reply already told: a run saved during a turn (a few seconds either way, file and
    // clock times are not read at the same instant) whose answer the reply quotes from its start.
    private void Settle(List<CronReport> pending)
    {
        Answered[] turns;
        lock (answered) turns = [.. answered];
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var report = pending[i];
            if (DateTime.UtcNow - report.Written > CronReports.CatchUp)
            {
                Log.Write($"排程「{report.Job}」的結果已經超過 12 小時，不說了。");
                reports.Drop(report);
                pending.RemoveAt(i);
            }
            else if (report.Answer == null && reports.Repeats(report)) pending.RemoveAt(i);
            else if (turns.Any(t => report.Written >= t.Asked - Slack && report.Written <= t.Done + Slack && CronReports.AlreadySaid(report, t.Reply)))
            {
                Log.Write($"排程「{report.Job}」的結果已經在回覆裡說過了，不再重複。");
                reports.Skip([report]);
                pending.RemoveAt(i);
            }
        }
    }

    // Several runs of one job waiting together (a night away, a long turn) are not all news: its newest
    // answer is told, and a failure after it unless a later run had nothing to say (it worked again).
    // Returns how many were left out of jobs that still have one to tell, for the count said before them.
    private int Fold(List<CronReport> pending)
    {
        var older = new List<CronReport>();
        int replaced = 0;
        foreach (var runs in pending.GroupBy(report => report.JobId, StringComparer.OrdinalIgnoreCase))
        {
            var answer = runs.Where(report => report.Answer != null).MaxBy(report => report.Written);
            var failure = runs.Where(report => report.Answer == null).MaxBy(report => report.Written);
            if (failure != null && (answer != null && failure.Written < answer.Written || reports.QuietSince(runs.Key) is { } quiet && failure.Written < quiet))
                failure = null;
            var left = runs.Where(report => !ReferenceEquals(report, answer) && !ReferenceEquals(report, failure)).ToList();
            older.AddRange(left);
            if (answer != null || failure != null) replaced += left.Count;
        }
        if (older.Count == 0) return 0;
        foreach (var report in older) pending.Remove(report);
        reports.Skip(older);
        Log.Write($"{older.Count} 則排程回報已經有更新的結果，不說了。");
        return replaced;
    }

    // The fish's animation phase when a report may take the bubble now ("" from an older pet that does
    // not say): the fish is on screen, nobody is dragging, poking or typing to it, the pet is not showing
    // something of its own (weather, a speech test), and the bridge's last words had their time. Null
    // when the report has to wait.
    // `maxIdle` asks for more: the keyboard or mouse used within that many seconds.
    private async Task<string?> FreeForReportAsync(PetLink link, CancellationToken token, double? maxIdle = null)
    {
        if (await CallAsync(link, "get_settings", new { }, token) is not { } json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var state = document.RootElement.GetProperty("state");
            bool? Flag(string name) => state.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            string phase = state.TryGetProperty("animationPhase", out var current) && current.ValueKind == JsonValueKind.String ? current.GetString()! : "";
            // Held in the air, a click reaction, the greeting or goodbye, an animation previewed in settings.
            if (phase is "Drag" or "Click" or "Startup" or "Exit" or "Preview") return null;
            if (Flag("visible") == false || Flag("inputOpen") == true || Flag("speechPending") == true) return null;
            // Nobody at the computer to tell: it waits until the user is back (an older pet does not say). Right after
            // a break it waits a moment more, for the pet's user_returned to arrive and a briefing to be decided first.
            if (NumberOf(state, "idleSeconds") is double idle)
            {
                bool back = lastIdle >= BreakSeconds && idle < AwayIdle.TotalSeconds;
                lastIdle = idle;
                if (idle >= AwayIdle.TotalSeconds || maxIdle is double strict && idle > strict) return null;
                if (back) Interlocked.Exchange(ref returnHold, DateTime.UtcNow.AddSeconds(4).Ticks);
            }
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref returnHold)) return null;
            // Over a full-screen program even when the fish stays visible there, as the fish's own reminders do.
            if (Flag("fullscreen") == true) return null;
            bool? visible = Flag("bubbleVisible"), external = Flag("externalBubble");
            if (visible == true && external == false) return null;
            var last = spoken;
            if (visible != true || last.At == DateTime.MinValue) return phase;
            int seconds = 10;
            if (document.RootElement.TryGetProperty("settings", out var settings) && settings.TryGetProperty("BubbleDurationIndex", out var index)
                && index.TryGetInt32(out int at) && at >= 0 && at < BubbleSeconds.Length) seconds = BubbleSeconds[at];
            // A bubble that stays until closed, or for minutes, still gives way after half a minute.
            var keep = last.Keep ?? TimeSpan.FromSeconds(seconds == 0 ? 30 : Math.Clamp(seconds, 5, 30));
            return DateTime.UtcNow - last.At >= keep ? phase : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    // The briefing goes before any cron report. While it is being written the reports wait; once written it is
    // said the way a report is (same gates, a wave first), and the reports follow without a second count. False
    // while it holds the reports back, and in the round it is said; true once it is out of the way.
    private async Task<bool> BriefingTurnAsync(Briefing brief, List<CronReport> pending, CancellationToken token)
    {
        string name = brief.Kind == Briefings.Kind.Daily ? "今日簡報" : "回來的報告";
        // Written a while ago and still not said (the user went off again, a game, a long talk): it would tell an
        // older moment. A daily one is looked at afresh the next time the user is at the computer.
        if (brief.Text != null ? DateTime.UtcNow - brief.Ready > (brief.Kind == Briefings.Kind.Daily ? DailyStale : WelcomeStale) : DateTime.UtcNow - brief.Created > TimeSpan.FromMinutes(5))
        {
            Log.Write(name + "等太久都沒機會說，不說了。");
            if (Interlocked.CompareExchange(ref briefing, null, brief) == brief && brief.Kind == Briefings.Kind.Daily) recheck = true;
            return true;
        }
        // The user came back and started talking: a welcome back would only interrupt.
        if (brief.Kind == Briefings.Kind.Welcome && Volatile.Read(ref typedCount) != brief.Asked)
        {
            Log.Write("主人回來後已經在跟魚魚說話了，回來的報告就不說了。");
            Interlocked.CompareExchange(ref briefing, null, brief);
            return true;
        }
        if (brief.Text is not { } text || Busy || pet is not { } link) return false;
        // Only to someone there: the keyboard or mouse used since it was written, and within half a minute.
        if (await FreeForReportAsync(link, token, Math.Min(30, (DateTime.UtcNow - brief.Ready).TotalSeconds)) is not { } phase) return false;
        if (phase is "Idle" or "PuppetIdle")
        {
            waved = DateTime.UtcNow;
            await ReactAsync(ReportAction, token);
        }
        if (await CallAsync(link, "say", new { text }, token) == null) return false;
        Log.Write((brief.Kind == Briefings.Kind.Daily ? "今日簡報：" : "回來的報告：") + text);
        spoken = new(DateTime.UtcNow, null);
        Interlocked.Exchange(ref progressHeld, (DateTime.UtcNow + ListReading).Ticks); // a turn started right after leaves it up a while
        counted = link;
        folded = 0;
        round = pending.Count > 0;
        if (brief.Kind == Briefings.Kind.Daily)
        {
            try { BridgeState.Update(options.StatePath, root => root["briefing"] = new JsonObject { ["day"] = brief.Day.ToString("yyyy-MM-dd"), ["at"] = DateTimeOffset.Now.ToString("o") }); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Log.Write("無法記下今天已經講過簡報：" + error.Message); }
        }
        Interlocked.CompareExchange(ref briefing, null, brief);
        return false;
    }

    // Every 15 seconds: how long before an event the pet's EventReminderMinutes wants it told (0 or missing: never),
    // and, while the user is at the computer, the next hours of the calendar, read again once five minutes old.
    // Nobody there, nothing could be said: the calendar is not asked until they are back. TellAsync says what falls due.
    private async Task WatchCalendarAsync(CancellationToken token)
    {
        LoadCalendarState();
        try
        {
            while (true)
            {
                try
                {
                    await LookAheadAsync(token);
                    // Working again: a later lapse is told again.
                    if (Volatile.Read(ref lapseTold) && calendar.Working) SaveLapse(false);
                }
                catch (Exception error) when (!token.IsCancellationRequested) { Log.Write("看 Google 日曆的行程時出錯：" + error.Message); }
                await Task.Delay(calendarRefresh / 20, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // From a reminder held past its start to the furthest one that can come due before the next read.
    private (DateTimeOffset From, DateTimeOffset To) Window(int lead)
    {
        var now = DateTimeOffset.Now;
        return (now - EventReminders.Late, now + TimeSpan.FromMinutes(lead) + calendarRefresh);
    }

    private async Task LookAheadAsync(CancellationToken token)
    {
        if (pet is not { } link || await CallAsync(link, "get_settings", new { }, token) is not { } json) return;
        int lead;
        double? idle;
        try
        {
            using var document = JsonDocument.Parse(json);
            lead = IntOf(document.RootElement.GetProperty("settings"), "EventReminderMinutes") ?? 0;
            idle = document.RootElement.TryGetProperty("state", out var state) ? NumberOf(state, "idleSeconds") : null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return; }
        if (lead <= 0) { upcoming = new([], 0); return; }
        var events = upcoming.Events;
        // An older pet that does not say how long it has been idle counts as someone there.
        if (!(idle >= AwayIdle.TotalSeconds))
        {
            // A read that fails keeps the events already known.
            var (from, to) = Window(lead);
            events = await calendar.EventsAsync(from, to, calendarRefresh, token) ?? events;
        }
        upcoming = new(events, lead);
    }

    private List<CalendarEvent> DueEvents()
    {
        var soon = upcoming;
        lock (reminded) return EventReminders.Due(soon.Events, soon.Lead, DateTimeOffset.Now, reminded);
    }

    // An event about to start cannot wait: it goes before the briefing and the reports, and is said during a turn too
    // (the progress bubble waits, as for the job list); otherwise through the same gates as a report. True while one
    // is due, said or held, so nothing else takes the bubble first; held past its start it is still said until
    // EventReminders.Late after it, worded as begun, and then let go.
    private async Task<bool> RemindAsync(CancellationToken token)
    {
        var due = DueEvents();
        var now = DateTimeOffset.Now;
        // One held until it could no longer be said (the user away, a full-screen game) goes with a line in the log;
        // one no longer due for another reason (deleted, moved, the reminders switched off) quietly.
        foreach (var (key, held) in holding.Where(pair => !due.Any(e => EventReminders.Key(e) == pair.Key)).ToList())
        {
            holding.Remove(key);
            if (now - held.Start > EventReminders.Late) Log.Write($"{held.Start.ToLocalTime():HH:mm} 的行程「{Briefings.Clean(held.Summary, 30)}」開始超過 5 分鐘了，不提醒了。");
        }
        foreach (var e in due) holding[EventReminders.Key(e)] = e;
        if (due.FirstOrDefault() is not { } next) return false;
        if (pet is not { } link || await FreeForReportAsync(link, token) is not { } phase) return true;
        // The read may be minutes old (the user away, the computer asleep, a change made on Telegram), and during a turn
        // Hermes may be changing the calendar right now: it is looked at again, and an event no longer there at that
        // start is not told. A calendar that cannot say keeps what was known.
        int lead = upcoming.Lead;
        var (from, to) = Window(lead);
        if (await calendar.EventsAsync(from, to, turn != null ? TimeSpan.Zero : ConfirmAge, token) is { } fresh)
        {
            upcoming = new(fresh, lead);
            if (!fresh.Any(e => EventReminders.Key(e) == EventReminders.Key(next))) return true;
        }
        if (DateTime.UtcNow - waved > TimeSpan.FromMinutes(2))
        {
            waved = DateTime.UtcNow;
            if (phase is "Idle" or "PuppetIdle") await ReactAsync(ReportAction, token);
        }
        string text = EventReminders.Speech(next, DateTimeOffset.Now);
        if (turn != null) Interlocked.Exchange(ref progressHeld, (DateTime.UtcNow + ListReading).Ticks);
        if (await CallAsync(link, "say", new { text }, token) == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token); // kept for the next try, or the next link
            return true;
        }
        Log.Write("行程提醒：" + text);
        spoken = new(DateTime.UtcNow, null);
        holding.Remove(EventReminders.Key(next));
        Remember(next);
        return true;
    }

    // Told once, not even again after a restart; a day after its start an event cannot come due again, so it is let go.
    private void Remember(CalendarEvent told)
    {
        string[] keep;
        lock (reminded)
        {
            reminded.Add(EventReminders.Key(told));
            reminded.RemoveWhere(key => EventReminders.StartOf(key) is not { } start || DateTimeOffset.Now - start > TimeSpan.FromDays(1));
            keep = reminded.Order(StringComparer.Ordinal).ToArray();
        }
        SaveCalendar("reminded", new JsonArray(keep.Select(key => (JsonNode?)key).ToArray()), "無法記下已經提醒過的行程，橋接程式重新啟動後可能會再提醒一次：");
    }

    private bool LapseUntold => calendar.Lapsed && !Volatile.Read(ref lapseTold);

    // A calendar that worked and no longer does is told once, not even again after a restart, so the user does not
    // count on reminders that stopped; a read that works again clears it (WatchCalendarAsync). Through the same gates
    // as a report, which wait behind it.
    private async Task<bool> TellLapseAsync(CancellationToken token)
    {
        if (!LapseUntold) return false;
        if (Busy || pet is not { } link || await FreeForReportAsync(link, token) is null) return true;
        const string text = "主人，Google 日曆的授權失效了，行程提醒先停下來；要恢復的話跟魚魚說「設定 Google 日曆」。";
        if (await CallAsync(link, "say", new { text }, token) == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return true;
        }
        Log.Write("日曆授權失效：" + text);
        spoken = new(DateTime.UtcNow, null);
        SaveLapse(true);
        return true;
    }

    private void SaveLapse(bool told)
    {
        Volatile.Write(ref lapseTold, told);
        SaveCalendar("lapseTold", told, "無法記下已經說過日曆授權失效，橋接程式重新啟動後可能會再說一次：");
    }

    // The calendar key of the state file holds the starts reminded and whether a lapse was told; each is written alone.
    private void SaveCalendar(string name, JsonNode value, string trouble)
    {
        try
        {
            BridgeState.Update(options.StatePath, root =>
            {
                if (root["calendar"] is not JsonObject saved) root["calendar"] = saved = new JsonObject();
                saved[name] = value;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Log.Write(trouble + error.Message); }
    }

    private void LoadCalendarState()
    {
        try
        {
            var saved = BridgeState.Read(options.StatePath)["calendar"];
            Volatile.Write(ref lapseTold, saved?["lapseTold"] is JsonValue told && told.TryGetValue<bool>(out var was) && was);
            if (saved?["reminded"] is not JsonArray list) return;
            lock (reminded)
                foreach (var item in list)
                    if (item is JsonValue value && value.TryGetValue<string>(out var key)) reminded.Add(key);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write("讀不到已經提醒過的行程，最近的行程可能會再提醒一次：" + error.Message);
        }
    }

    // Hears the user come back (or a new link come up while the user is at the computer) and starts a daily
    // briefing or a welcome back when one is due. One at a time: while one is being written what is heard is
    // dropped; one written but not yet said gives way to a newer return. A link that came up with the user away
    // is looked at again every 15 seconds until they are back.
    private async Task BriefAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                PetEvent? heard;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    if (recheck) wait.CancelAfter(TimeSpan.FromSeconds(15));
                    try { if (!await events.Reader.WaitToReadAsync(wait.Token)) return; events.Reader.TryRead(out heard); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { heard = new PetEvent("linked", default); }
                }
                if (heard == null || pet is not { } link) continue;
                if (heard.Kind == "user_returned") recheck = false;
                var current = Pending;
                if (current is { Text: not null } && heard.Kind == "user_returned") Interlocked.CompareExchange(ref briefing, null, current);
                else if (current != null) continue;
                deciding = true;
                try { await ConsiderAsync(link, heard, null, token); }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    Log.Write("準備簡報時出錯：" + error.Message);
                    Interlocked.Exchange(ref briefing, null);
                }
                finally { deciding = false; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // `forced` (the --brief test path) skips the decision and the day already briefed. Returns the briefing, with
    // its text written, or null when none is due.
    private async Task<Briefing?> ConsiderAsync(PetLink link, PetEvent heard, Briefings.Kind? forced, CancellationToken token)
    {
        if (await CallAsync(link, "get_settings", new { }, token) is not { } json) return null;
        JsonElement settings, state;
        try
        {
            using var document = JsonDocument.Parse(json);
            settings = document.RootElement.GetProperty("settings").Clone();
            state = document.RootElement.GetProperty("state").Clone();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        var now = DateTime.Now;
        int? away = null;
        DateTime? since = null;
        bool watching = false, daily = BoolOf(settings, "DailyBriefing") == true;
        if (heard.Kind == "user_returned")
        {
            away = IntOf(heard.Detail, "breakMinutes");
            since = TextOf(heard.Detail, "since") is { } start && DateTimeOffset.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at.LocalDateTime : null;
            watching = BoolOf(heard.Detail, "watching") == true;
        }
        // On a new link only a user at the computer, with the fish free to say it, gets one (an older pet without the
        // settings never does); otherwise it is looked at again until then, if a daily briefing is still owed today.
        // A fish hidden over a full-screen game would otherwise get one written, dropped as stale and written again.
        else if (forced == null && !(NumberOf(state, "idleSeconds") is double idle && idle < 60 && BoolOf(state, "visible") != false
            && BoolOf(state, "fullscreen") != true && BoolOf(state, "inputOpen") != true && !Busy))
        {
            recheck = daily && BriefedDay() != Briefings.Day(now) && now.Hour < 22;
            return null;
        }
        else recheck = false;
        var kind = forced ?? Briefings.Decide(now, away, daily, IntOf(settings, "WelcomeBackMinutes") ?? 0, BriefedDay(), watching);
        if (kind == null) return null;
        var brief = new Briefing(kind.Value, Briefings.Day(now), Volatile.Read(ref typedCount));
        if (forced == null) { Volatile.Write(ref briefing, brief); deciding = false; } // the reports wait for it from now on
        Log.Write(kind == Briefings.Kind.Daily ? "準備今日簡報。" : $"主人離開了 {away} 分鐘，準備回來的報告。");
        using var chat = HermesChat.Separate(options.HermesUrl, options.ApiKey, "pet-brief-" + Guid.NewGuid().ToString("N"));
        // Hermes may still be starting (at login the fish and the bridge come up before its gateway): its session
        // list is needed below, so this waits first, alongside the weather.
        var up = WaitForHermesAsync(chat, token);
        string? weather = await WeatherAsync(link, token);
        // The first sit-down after a night: the fish fetches the forecast as it wakes up, so give it a moment. A
        // cache still fresh but of periods all over counts as none.
        for (int i = 0; Briefings.WeatherLine(weather, DateTime.Now) == null && kind == Briefings.Kind.Daily && BoolOf(settings, "PublicInfo") == true && i < 6; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            weather = await WeatherAsync(link, token);
        }
        await up;
        // A daily briefing tells what is left of today on the calendar, read alongside Hermes' session list.
        var agenda = kind == Briefings.Kind.Daily ? AgendaAsync(now, token) : Task.FromResult<IReadOnlyList<CalendarEvent>?>(null);
        // What happened since the user left; on a new link that is not known, so since the last briefing (at most 12 hours).
        var from = since ?? (LastBriefing() is { } last && last > now.AddHours(-12) ? last : now.AddHours(-12));
        // A daily briefing after a short break does not dwell on it.
        bool awhile = kind == Briefings.Kind.Welcome || away >= 60;
        var facts = new Briefings.Facts(kind.Value, now, awhile ? away : null, awhile ? since : null, weather, Briefings.TodayJobs(CronReports.ReadJobs(options.HermesHome), now), waiting,
            Briefings.Elsewhere(await chat.RecentSessionsAsync(token), new DateTimeOffset(from)),
            since is { } left ? Briefings.MissedMeals(left, now, BoolOf(settings, "MealReminder") == true, IntOf(settings, "BreakfastHour") ?? 8, IntOf(settings, "LunchHour") ?? 12, IntOf(settings, "DinnerHour") ?? 18) : [],
            Briefings.TodayEvents(await agenda, now), kind == Briefings.Kind.Daily ? calendar.Skipped : 0);
        brief.Write(await WriteAsync(chat, facts, token));
        return brief;
    }

    private static async Task WaitForHermesAsync(HermesChat chat, CancellationToken token)
    {
        for (int i = 0; i < 15 && !await chat.IsUpAsync(token); i++) await Task.Delay(TimeSpan.FromSeconds(3), token);
    }

    // Today's events for the daily briefing, which comes once a day: often at a wake-up, before the network is back and
    // a token due for refresh can be renewed. So a failure is not taken from the cache (the reminders may have just
    // met it), and one that may pass is tried again twice, five seconds apart. From as far back as the reminders read,
    // so a recent read of theirs serves, and one made here serves them.
    private async Task<IReadOnlyList<CalendarEvent>?> AgendaAsync(DateTime now, CancellationToken token)
    {
        for (int i = 0; ; i++)
        {
            if (await calendar.EventsAsync(new DateTimeOffset(now) - EventReminders.Late, new DateTimeOffset(now.Date.AddDays(1)), calendarRefresh, token, retryFailed: true) is { } events)
                return events;
            if (i == 2 || !calendar.MayPass) return null;
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    private async Task<string?> WeatherAsync(PetLink link, CancellationToken token)
    {
        if (await CallAsync(link, "get_context", new { }, token) is not { } context) return null;
        try { using var document = JsonDocument.Parse(context); return TextOf(document.RootElement, "weather"); }
        catch (JsonException) { return null; }
    }

    // Hermes puts the facts into words in a hidden session of its own. A tool call (it is told not to, and cannot be
    // kept from them), a failure, an empty answer or no answer within 90 seconds gets the plain text instead.
    private static async Task<string> WriteAsync(HermesChat chat, Briefings.Facts facts, CancellationToken token)
    {
        // Hermes keeps session titles unique, so each gets its time (a live test: the second of the day was refused).
        if (!await chat.HideAsync((facts.Kind == Briefings.Kind.Daily ? "魚魚的今日簡報 " : "魚魚的回來報告 ") + facts.Now.ToString("M/d HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), token))
            Log.Write("（簡報用的 Hermes 對話沒能設成隱藏，照樣產生。）");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(BriefingLimit);
        string? tool = null;
        Task stopping = Task.CompletedTask;
        string why;
        try
        {
            string reply = await chat.AskAsync(Briefings.Prompt(facts), [], (_, _) => { }, limit.Token, system: Briefings.Instructions, quick: true, toolStarted: name =>
            {
                if (tool != null) return;
                tool = name;
                // Stopped on Hermes' side first (the stream has to be open for that), then the stream is dropped.
                stopping = Task.Run(async () =>
                {
                    await chat.StopAsync(CancellationToken.None);
                    try { limit.Cancel(); } catch (ObjectDisposedException) { }
                });
            });
            if (tool == null && reply.Trim().Length > 0) return Fit(Briefings.WithAgenda(reply.Trim(), facts));
            why = tool != null ? $"Hermes 想用工具（{tool}）" : "Hermes 沒有寫內容";
        }
        catch (Exception error) when (error is HermesException or HttpRequestException or IOException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            why = tool != null ? $"Hermes 想用工具（{tool}）" : error is OperationCanceledException ? "Hermes 太久沒寫好" : "Hermes 出錯：" + error.Message;
        }
        await stopping;
        Log.Write(why + "，簡報改用魚魚自己整理的內容。");
        return Briefings.Fallback(facts);
    }

    private DateTime? LastBriefing()
    {
        try
        {
            return BridgeState.Read(options.StatePath)["briefing"]?["at"]?.GetValue<string>() is { } at
                && DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when) ? when.LocalDateTime : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    private DateTime? BriefedDay()
    {
        try
        {
            return BridgeState.Read(options.StatePath)["briefing"]?["day"]?.GetValue<string>() is { } day
                && DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    // Test path: one briefing through the same steps on the real pet and Hermes, said and printed, without
    // waiting for the user to come back or remembering the day.
    internal async Task<int> BriefOnceAsync(Briefings.Kind kind, int? awayMinutes, CancellationToken token)
    {
        var link = await PetLink.ConnectAsync(options.McpCommand, Channel.CreateUnbounded<PetMessage>().Writer, token);
        try
        {
            pet = link;
            var now = DateTimeOffset.Now;
            var heard = awayMinutes is int away
                ? new PetEvent("user_returned", JsonSerializer.SerializeToElement(new { breakMinutes = away, since = now.AddMinutes(-away).ToString("yyyy-MM-dd'T'HH:mm:sszzz"), at = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz") }))
                : new PetEvent("linked", default);
            if (await ConsiderAsync(link, heard, kind, token) is not { Text: { } text }) return 1;
            await CallAsync(link, "say", new { text }, token);
            Console.WriteLine(text);
            return 0;
        }
        finally
        {
            pet = null;
            await link.DisposeAsync();
        }
    }

    private static bool? BoolOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static int? IntOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static double? NumberOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    private static string? TextOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

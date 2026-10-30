// Fork-original file of the taiwan-edition fork by acer1204 (2026-10-28); not part of upstream Bai-Cute/big-blue-fish-desktop-pet.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BigBlueFish.HermesBridge;

// The daily briefing (the first time the user sits down each day) and the welcome back (after a long break):
// when one is due, the facts it may mention, what Hermes is asked to make of them, and the plain text the fish
// says when Hermes cannot help in time. The bridge gathers the facts itself: Hermes cannot be kept off its tools
// for one request, so it is only asked to put them into words.
internal static partial class Briefings
{
    internal enum Kind { Daily, Welcome }

    // Weather, the jobs still to run today, the reports waiting to be told, other conversations Hermes had and
    // meal times that passed while the user was away. AwayMinutes and Since are null when not known (the bridge
    // or the fish just started). Events are what is left of today on the user's Google Calendar (TodayEvents), for a
    // daily briefing only; null when there is no calendar to ask or it could not say. Unread is how many of the
    // calendars shown that read could not reach (Calendar.Skipped): their events are not among Events.
    internal sealed record Facts(Kind Kind, DateTime Now, int? AwayMinutes, DateTime? Since, string? Weather,
        IReadOnlyList<string> Today, IReadOnlyList<string> Reports, IReadOnlyList<Talk> Conversations, IReadOnlyList<string> MissedMeals,
        IReadOnlyList<CalendarEvent>? Events = null, int Unread = 0);

    // A conversation Hermes had elsewhere: where (「Telegram 上的對話」) and its title. Titles come from what someone
    // typed on that channel, so only the fallback the bridge writes itself quotes them; Hermes, which can run tools,
    // is told only where and how many.
    internal sealed record Talk(string Where, string Title);

    // One of Hermes' sessions as GET /api/sessions lists it.
    internal sealed record Conversation(string Source, string Title, DateTimeOffset LastActive, int Messages);

    // A day of briefings starts at 05:00, when the fish's late-night reminders end.
    internal static DateTime Day(DateTime local) => local.AddHours(-5).Date;

    // Daily first: it covers the night away as well. A daily briefing is not started after 22:00, and a welcome
    // back needs a known break of at least the user's chosen length, not spent watching the screen (a film, a game
    // played with a controller).
    internal static Kind? Decide(DateTime local, int? breakMinutes, bool daily, int welcomeMinutes, DateTime? briefedDay, bool watching = false)
    {
        if (daily && briefedDay != Day(local) && local.Hour is >= 5 and < 22) return Kind.Daily;
        if (!watching && welcomeMinutes > 0 && breakMinutes is int away && away >= welcomeMinutes) return Kind.Welcome;
        return null;
    }

    // The latest meal of today's briefing day (from 05:00) whose whole reminder window (the hour and 90 minutes
    // after it, as the fish uses) fell while the user was away: the fish could not remind of it, so the briefing
    // may ask. Last night's dinner is not asked about in the morning.
    internal static IReadOnlyList<string> MissedMeals(DateTime since, DateTime now, bool meals, int breakfast, int lunch, int dinner)
    {
        if (!meals || now <= since) return [];
        var day = Day(now);
        var missed = new[] { ("早餐", breakfast), ("午餐", lunch), ("晚餐", dinner) }
            .Select(meal => (meal.Item1, Start: day.AddHours(meal.Item2 < 5 ? meal.Item2 + 24 : meal.Item2)))
            .Where(meal => meal.Start >= since && meal.Start >= day.AddHours(5) && meal.Start.AddMinutes(90) <= now)
            .OrderBy(meal => meal.Start).Select(meal => meal.Item1).ToList();
        return missed.Count > 0 ? [missed[^1]] : [];
    }

    // 「15:00「匯率」」 for each job still to run today, soonest first.
    internal static IReadOnlyList<string> TodayJobs(IEnumerable<CronJob>? jobs, DateTime now) =>
        jobs == null ? [] : jobs.Where(job => job.State == "scheduled" && job.Next is { } next && next.LocalDateTime > now && next.LocalDateTime.Date == now.Date)
            .OrderBy(job => job.Next).Take(5).Select(job => $"{job.Next!.Value.LocalDateTime:HH:mm}「{Clean(job.Name, 20)}」").ToList();

    // Events under way or still to come today, all-day ones first. All of them, so Hermes hears how many there are;
    // Agenda reads out the first few.
    internal static IReadOnlyList<CalendarEvent>? TodayEvents(IReadOnlyList<CalendarEvent>? events, DateTime now) =>
        events?.Where(e => e.End.LocalDateTime > now && e.Start.LocalDateTime < now.Date.AddDays(1))
            .OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToList();

    private const int AgendaLimit = 6;

    // 「14:00–15:00「週會」（3F 會議室）、整天「連假」」: today's events in the fish's own words, titles and places cleaned
    // as quotes; past six,「，另外還有 3 個」. One that began before today or goes on past midnight reads「到 10:00」
    // 「23:00 起」, one ending at midnight「22:00–24:00」, one covering all of today (00:00 to midnight too)「整天」.
    internal static string Agenda(IReadOnlyList<CalendarEvent> events, DateTime now) => string.Join("、", events.Take(AgendaLimit).Select(e =>
    {
        DateTime start = e.Start.LocalDateTime, end = e.End.LocalDateTime, midnight = now.Date.AddDays(1);
        string from = start.ToString("HH:mm", CultureInfo.InvariantCulture), to = end == midnight ? "24:00" : end.ToString("HH:mm", CultureInfo.InvariantCulture);
        string when = e.AllDay || start <= now.Date && end >= midnight ? "整天" : start < now.Date ? $"到 {to}" : end > midnight ? $"{from} 起" : end == start ? from : $"{from}–{to}";
        string title = Clean(e.Summary, 20), place = Online(e.Location) ? "線上會議" : Clean(e.Location, 20);
        return when + (title.Length > 0 ? $"「{title}」" : " 一個行程") + (place.Length > 0 ? $"（{place}）" : "");
    })) + (events.Count > AgendaLimit ? $"，另外還有 {events.Count - AgendaLimit} 個" : "");

    // A place that is a meeting link (Zoom and Teams invitations put theirs there): not worth reading out.
    internal static bool Online(string location) => OnlinePlace().IsMatch(location);

    // What Hermes wrote, with today's events after it in the fish's own words: Hermes is told only how many (Prompt).
    // A calendar that could not be read is said there too, so a day with one missing is never left sounding empty.
    internal static string WithAgenda(string written, Facts facts) =>
        facts.Events is { } events && (events.Count > 0 || facts.Unread > 0) ? written + "\n" + Spoken(facts) : written;

    // Today's events as the fish says them, with the calendars the read missed: 「今天的行程：…。有 1 個日曆這次沒讀到，
    // 上面的行程不在裡面。」, 「讀得到的日曆今天沒有行程，有 1 個日曆這次沒讀到。」 or 「今天沒有行程。」. Null with no
    // calendar to ask.
    private static string? Spoken(Facts facts) => facts.Events switch
    {
        null => null,
        { Count: > 0 } events => "今天的行程：" + Agenda(events, facts.Now) + "。" + (facts.Unread > 0 ? $"有 {facts.Unread} 個日曆這次沒讀到，上面的行程不在裡面。" : ""),
        _ => facts.Unread > 0 ? $"讀得到的日曆今天沒有行程，有 {facts.Unread} 個日曆這次沒讀到。" : "今天沒有行程。"
    };

    // Conversations on other channels (Telegram, the desktop app, the terminal) active while the user was away.
    // The pet's own sessions and cron runs are left out: the bridge tells cron results itself.
    internal static IReadOnlyList<Talk> Elsewhere(IEnumerable<Conversation> sessions, DateTimeOffset since) =>
        sessions.Where(s => s.LastActive > since && s.Source is not ("api_server" or "cron") && s.Messages > 0)
            .OrderBy(s => s.LastActive).Take(3).Select(s => new Talk(SourceName(s.Source), Clean(s.Title, 30))).ToList();

    // A name or title someone typed, made safe to quote in a sentence: no line breaks or control characters, no
    // quotation marks that could close the quote around it, and short.
    internal static string Clean(string text, int limit)
    {
        var kept = new string(text.Where(c => !char.IsControl(c) && "「」『』\"'`<>".IndexOf(c) < 0).ToArray()).Trim();
        return Short(kept, limit);
    }

    private static string SourceName(string source) => source switch
    {
        "telegram" => "Telegram 上的對話", "desktop" => "Hermes 桌面版的對話", "tui" or "cli" => "終端機裡的對話", "oneshot" => "一次性的指令",
        "discord" => "Discord 上的對話", "line" => "LINE 上的對話", _ => source + " 的對話"
    };

    private static string Short(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    internal static string Duration(int minutes) => minutes < 60 ? $"{minutes} 分鐘" : minutes % 60 == 0 ? $"{minutes / 60} 小時" : $"{minutes / 60} 小時 {minutes % 60} 分鐘";

    private static string Period(DateTime local) => local.Hour switch { < 5 => "凌晨", < 11 => "早上", < 13 => "中午", < 18 => "下午", _ => "晚上" };

    // The system message of a briefing turn, sent instead of the chat instructions.
    internal const string Instructions =
        "你是使用者桌面上的「藍色大肥魚」，稱呼使用者「主人」。這一輪不是使用者打字，而是魚魚自動要說的一段話，結果會原樣顯示在魚魚的對話氣泡。"
        + "資料都在使用者訊息裡了：絕對不要呼叫任何工具（包括 tool_search、魚魚的 MCP 工具、終端機、上網），直接寫出要說的話。"
        + "只根據資料說，資料裡沒有的事（行程、新聞、天氣細節、數字）一律不要編；資料說「沒有」的項目就不要提。"
        + "用台灣繁體中文，口語、親切、像管家在報告，不用 Markdown、不要列點、不要標題，可以用一個表情符號。"
        + "天氣只說幾度和降雨機率，不要提醒帶傘（魚魚會另外提醒）。有排程回報時只說有幾則、等一下就說，不要先講內容。"
        + "吃飯、休息、喝水、早點睡、帶傘這些魚魚會另外提醒，除非資料寫了錯過的用餐時間，否則不要叮嚀，也不要另外給建議。"
        // A live test (2026-10-28): 150 minutes away came out as 「三個半小時」, and 24 度 as 「有點涼」.
        + "離開多久、時間、溫度和數量照資料原樣說（例如「2 小時 30 分鐘」），不要自己換算或四捨五入，也不要加上資料沒有的形容（涼、熱、悶）。";

    // The user message: the facts, labelled, with the length wanted.
    internal static string Prompt(Facts facts)
    {
        var text = new StringBuilder();
        var now = facts.Now;
        text.Append(facts.Kind == Kind.Daily ? "請說今天的「今日簡報」，80 到 150 個字。" : "主人離開電腦一陣子剛回來，請說「歡迎回來」和這段時間發生的事，40 到 100 個字；沒有新消息就一兩句問候就好。");
        text.Append($"\n現在：{now.Month}月{now.Day}日 {Weekday(now)} {now:HH:mm}（{Period(now)}）");
        if (facts.AwayMinutes is int away)
            text.Append($"\n主人離開了：{Duration(away)}" + (facts.Since is { } since ? $"（從 {Clock(since, now)} 起）" : ""));
        text.Append("\n天氣（魚魚快取的中央氣象署預報）：" + (WeatherLine(facts.Weather, now) ?? "沒有"));
        text.Append("\n今天接下來的排程：" + (facts.Today.Count > 0 ? string.Join("、", facts.Today) : "沒有"));
        // Anyone can put an event on a Google Calendar by sending an invitation, so its title and place are someone
        // else's words, and a known way of slipping an assistant an instruction. Like the titles of other conversations
        // they stay out of this turn (Hermes can run tools, and WriteAsync stops it only once one has started): Hermes
        // hears how many, and the times and titles follow its text in the fish's own words (WithAgenda), as in Fallback.
        // A calendar the read missed makes an empty day unknown: Hermes is kept from calling it free, and the fish says it.
        if (facts.Events is { } events)
            text.Append("\n今天的行程（Google 日曆）：" + (events.Count > 0 ? $"{events.Count} 個（時間和內容魚魚會接在你的話後面自己念，你不要提）"
                : facts.Unread > 0 ? "讀得到的日曆上沒有，但有日曆這次沒讀到（魚魚會接在你的話後面自己說，你不要提行程，也不要說今天沒有行程）" : "沒有"));
        text.Append("\n不在時收到、等一下要說的排程回報：" + (facts.Reports.Count > 0 ? $"{facts.Reports.Count} 則（{string.Join("、", facts.Reports.Select(job => Clean(job, 20)).Distinct().Take(5))}）" : "沒有"));
        text.Append("\n不在時 Hermes 在其他地方的對話：" + (facts.Conversations.Count > 0
            ? string.Join("、", facts.Conversations.GroupBy(talk => talk.Where).Select(where => $"{where.Key} {where.Count()} 段")) : "沒有"));
        text.Append("\n錯過的用餐時間：" + (facts.MissedMeals.Count > 0 ? string.Join("、", facts.MissedMeals) : "沒有"));
        return text.ToString();
    }

    // What the fish says itself when Hermes does not answer in time, fails, or reaches for a tool.
    internal static string Fallback(Facts facts)
    {
        var now = facts.Now;
        var text = new StringBuilder(facts.Kind == Kind.Daily
            ? $"{Greeting(now)}主人！今天是 {now.Month}月{now.Day}日 {Weekday(now)}。"
            : $"歡迎回來，主人！" + (facts.AwayMinutes is int away ? $"你離開了 {Duration(away)}。" : ""));
        if (WeatherLine(facts.Weather, now) is { } weather) text.Append("天氣：").Append(weather).Append('。');
        if (Spoken(facts) is { } agenda) text.Append(agenda);
        if (facts.Today.Count > 0) text.Append("今天還有排程：").Append(string.Join("、", facts.Today)).Append('。');
        if (facts.Conversations.Count > 0)
            text.Append("你不在的時候有 ").Append(string.Join("、", facts.Conversations.Select(talk => talk.Title.Length > 0 ? $"{talk.Where}「{talk.Title}」" : talk.Where))).Append('。');
        if (facts.MissedMeals.Count > 0) text.Append(string.Join("、", facts.MissedMeals)).Append("的時間過了，吃過了嗎？");
        if (facts.Reports.Count > 0) text.Append($"另外有 {facts.Reports.Count} 則排程回報，馬上告訴你。");
        else if (facts.Kind == Kind.Welcome && facts.Conversations.Count == 0 && facts.MissedMeals.Count == 0) text.Append("這段時間沒有新消息。");
        return text.ToString();
    }

    private static string Greeting(DateTime local) => local.Hour switch { < 11 => "早安", < 18 => "午安", _ => "晚安" };

    internal static string Weekday(DateTime local) => "星期" + "日一二三四五六"[(int)local.DayOfWeek];

    private static string Clock(DateTime time, DateTime now) =>
        (time.Date == now.Date ? "" : time.Date == now.Date.AddDays(-1) ? "昨天 " : $"{time.Month}/{time.Day} ") + time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // What is left of the fish's cached forecast, in a few words. The cache holds up to two CWA periods of three
    // hours and is refreshed hourly, so the first may be over already: those are left out, the rest run together.
    // 「10月29日06:00到10月29日09:00：晴。降雨機率10%。溫度攝氏22至24度。…」 at 05:30 reads「06:00 到 09:00 晴，降雨機率 10%，22 到 24 度」,
    // at 07:30「現在到 09:00 晴…」; Open-Meteo's day reads「晴朗，14 到 24 度，降雨機率 0%」.
    internal static string? WeatherLine(string? weather, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(weather)) return null;
        var periods = CwaPeriod().Matches(weather).Select(match => (Start: When(match, "fm", "fd", "from", now), End: When(match, "tm", "td", "to", now), Match: match))
            .Where(period => period.End > now).OrderBy(period => period.Start).ToList();
        if (periods.Count > 0)
        {
            var skies = periods.Select(period => period.Match.Groups["sky"].Value.Trim()).Distinct().ToList();
            int rain = periods.Max(period => int.Parse(period.Match.Groups["rain"].Value, CultureInfo.InvariantCulture));
            var lows = periods.Where(period => period.Match.Groups["low"].Success).Select(period => int.Parse(period.Match.Groups["low"].Value, CultureInfo.InvariantCulture)).ToList();
            var highs = periods.Where(period => period.Match.Groups["high"].Success).Select(period => int.Parse(period.Match.Groups["high"].Value, CultureInfo.InvariantCulture)).ToList();
            string from = periods[0].Start <= now ? "現在到" : periods[0].Start.ToString("HH:mm", CultureInfo.InvariantCulture) + " 到";
            return $"{from} {periods[^1].End:HH:mm} {string.Join("轉", skies)}，降雨機率 {rain}%" + (lows.Count > 0 ? $"，{lows.Min()} 到 {highs.Max()} 度" : "");
        }
        // Open-Meteo's day, if it is today's (a forecast fetched before midnight is still fresh just after it).
        if (OpenMeteo().Match(weather) is { Success: true } day && (!(OpenMeteoDay().Match(weather) is { Success: true } date)
            || DateTime.TryParseExact(date.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var of) && of == now.Date))
            return $"{day.Groups["sky"].Value}，{Round(day.Groups["low"].Value)} 到 {Round(day.Groups["high"].Value)} 度，降雨機率 {day.Groups["rain"].Value}%";
        return null;
    }

    // A period's month, day and clock as a time near now (the forecast carries no year).
    private static DateTime When(Match match, string month, string day, string clock, DateTime now)
    {
        var parts = match.Groups[clock].Value.Split(':');
        try
        {
            var time = new DateTime(now.Year, int.Parse(match.Groups[month].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[day].Value, CultureInfo.InvariantCulture))
                .AddHours(int.Parse(parts[0], CultureInfo.InvariantCulture)).AddMinutes(int.Parse(parts[1], CultureInfo.InvariantCulture));
            return time - now > TimeSpan.FromDays(180) ? time.AddYears(-1) : now - time > TimeSpan.FromDays(180) ? time.AddYears(1) : time;
        }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }

    private static string Round(string number) =>
        double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : number;

    [GeneratedRegex(@"(?<fm>\d{1,2})月(?<fd>\d{1,2})日(?<from>\d{1,2}:\d{2})到(?<tm>\d{1,2})月(?<td>\d{1,2})日(?<to>\d{1,2}:\d{2})：(?<sky>[^。]{1,20})。降雨機率(?<rain>\d{1,3})%。(?:溫度攝氏(?<low>-?\d+)至(?<high>-?\d+)度)?")]
    private static partial Regex CwaPeriod();

    [GeneratedRegex(@"記錄時(?<sky>[^，]{1,10})，.*?最低(?<low>-?[\d.]+)℃、最高(?<high>-?[\d.]+)℃，最高降水機率(?<rain>\d{1,3})%")]
    private static partial Regex OpenMeteo();

    [GeneratedRegex(@"資料日期(\d{4}-\d{2}-\d{2})")]
    private static partial Regex OpenMeteoDay();

    [GeneratedRegex(@"^\s*https?://|zoom\.us|teams\.microsoft|teams\.live|meet\.google", RegexOptions.IgnoreCase)]
    private static partial Regex OnlinePlace();
}

// Fork-original file of the taiwan-edition fork by acer1204 (2026-10-28); not part of upstream Bai-Cute/big-blue-fish-desktop-pet.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BigBlueFish.HermesBridge;

// One event on the user's Google Calendar (any of the calendars it shows), on this computer's clock; an all-day one
// runs from the midnight it starts to the midnight after its last day. Summary and Location are "" when not given. The
// description is not kept at all: it holds whatever an invitation's sender wrote (links, dial-in codes) and nothing
// here needs it; nor is the calendar it is on, which neither a reminder nor the briefing names.
internal sealed record CalendarEvent(string Id, string Summary, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Location);

// Reads the calendar through the google-calendar skill's list_events.py, which lists with the credentials of Hermes'
// own google-workspace skill (google_api.py), authorized for the calendar scope only: the bridge never holds a Google
// token itself. It reads every calendar the user shows in Google Calendar (hidden ones, such as a holiday calendar
// switched off, are skipped) and lists them as one, an event found on several of them once. Null whenever it cannot
// say: not authorized yet, Hermes' Python or a skill missing, too slow, or an answer it does not understand. Each kind
// of trouble is logged once until a read works again, not on every poll.
internal sealed partial class Calendar(string hermesHome)
{
    // Tests point these at a fake list_events.py.
    internal const string PythonVariable = "BIGBLUEFISH_CALENDAR_PYTHON", ScriptVariable = "BIGBLUEFISH_CALENDAR_SCRIPT";
    // Every read covers at least this far ahead and the rest of today, so the reminders' moving window and the daily
    // briefing's day can share one.
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(6);
    private static readonly UTF8Encoding Utf8 = new(false);
    private enum Trouble { NotAuthorized, NoSkill, NoWorkspace, NoPython, Slow, Failed, Unreadable }
    // The last read, failed ones too (Events null), so a calendar in trouble is not asked again on every poll. At is
    // Environment.TickCount64 when it was made: the wall clock can be set back (by hand, NTP, a wrong BIOS time), and
    // a read would then stay young for hours. Generation is the Forget count it was made under.
    private sealed record Read(DateTimeOffset From, DateTimeOffset To, long At, long Generation, IReadOnlyList<CalendarEvent>? Events);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Read? last;
    private long forgotten;
    private readonly HashSet<Trouble> told = [];
    private volatile bool working;
    // The ids of the calendars the last read that worked had to skip, each logged when it first was.
    private HashSet<string> skippedTold = new(StringComparer.Ordinal);
    private volatile int skipped;

    internal TimeSpan Limit { get; init; } = TimeSpan.FromSeconds(30);

    // The last read worked.
    internal bool Working => working;

    // How many of the calendars shown the last read that worked could not read (list_events.py skips one that is gone
    // or no longer shared, and says so on stderr): their events are missing, so a day without any is not known to be
    // empty.
    internal int Skipped => skipped;

    // The last read failed in a way that may pass by itself (no network yet after a wake-up, Google slow or erring),
    // so trying again in a moment can help; not for a calendar that is not authorized or a skill that is missing.
    internal bool MayPass { get; private set; }

    // The calendar was authorized (its token is there) and Google no longer takes it: revoked, or expired as an app
    // still "in testing" does after seven days. False again once a read works.
    internal bool Lapsed { get; private set; }

    // The events from `from` to `to` (Google's sense: ending after `from`, starting before `to`), soonest first. The
    // last read is reused while younger than `maxAge` and covering the window, a failed one only unless `retryFailed`;
    // two callers at once share one read.
    internal async Task<IReadOnlyList<CalendarEvent>?> EventsAsync(DateTimeOffset from, DateTimeOffset to, TimeSpan maxAge, CancellationToken token, bool retryFailed = false)
    {
        await gate.WaitAsync(token);
        try
        {
            long generation = Volatile.Read(ref forgotten);
            if (last is not { } read || read.Generation != generation || Environment.TickCount64 - read.At >= maxAge.TotalMilliseconds
                || (read.Events == null ? retryFailed : read.From > from || read.To < to))
            {
                var end = new[] { to, from + Ahead, new DateTimeOffset(DateTime.Today.AddDays(1)) }.Max();
                read = last = new(from, end, Environment.TickCount64, generation, await ReadAsync(from, end, token));
            }
            return read.Events?.Where(e => e.End > from && e.Start < to).ToList();
        }
        finally { gate.Release(); }
    }

    // Hermes may just have changed the calendar: the next read asks Google again, and one under way now does not count.
    internal void Forget() => Interlocked.Increment(ref forgotten);

    private async Task<IReadOnlyList<CalendarEvent>?> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        string python = Setting(PythonVariable) ?? Path.Combine(hermesHome, "hermes-agent", "venv", "Scripts", "python.exe");
        string script = Setting(ScriptVariable) ?? Path.Combine(hermesHome, "skills", "productivity", "google-calendar", "scripts", "list_events.py");
        // setup.py saves the token here once the user has authorized; only its presence is looked at.
        if (!File.Exists(Path.Combine(hermesHome, "google_token.json")))
        {
            Lapsed = false; // never authorized, or the user revoked it themselves
            return Fail(Trouble.NotAuthorized);
        }
        if (!File.Exists(script)) return Fail(Trouble.NoSkill, "找不到 Hermes 的 google-calendar 技能（" + script + "），行程相關的功能先略過。");
        if (Path.IsPathRooted(python) && !File.Exists(python)) return Fail(Trouble.NoPython, "找不到 Hermes 的 Python（" + python + "），行程相關的功能先略過。");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var argument in new[] { script, "--start", Iso(from), "--end", Iso(to), "--max", "50" }) start.ArgumentList.Add(argument);
        start.Environment["HERMES_HOME"] = hermesHome;
        // Into a pipe Python writes the ANSI code page (cp950 here), which has no emoji: a title with one would stop it.
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception error) { return Fail(Trouble.NoPython, "無法執行 Hermes 的 Python，行程相關的功能先略過：" + error.Message); }
        // Both pipes are drained from the start, so a chatty stderr cannot block the process.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(Limit);
        string text, problem;
        try
        {
            await process.WaitForExitAsync(limit.Token);
            text = await output.WaitAsync(limit.Token);
            problem = await errors.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            token.ThrowIfCancellationRequested();
            return Fail(Trouble.Slow, $"Google 日曆 {Limit.TotalSeconds:0} 秒都沒有回應，這次先略過。");
        }
        if (process.ExitCode != 0)
        {
            // google_api.py's own words for a token that is missing, expired or revoked: with the token there, an
            // authorization that worked no longer does.
            if (Unauthorized().IsMatch(problem))
            {
                Lapsed = true;
                return Fail(Trouble.NotAuthorized);
            }
            if (MissingWorkspace().IsMatch(problem))
                return Fail(Trouble.NoWorkspace, "找不到 Hermes 的 google-workspace 技能，行程相關的功能先略過；如果是被 curator 封存了，執行 hermes curator restore google-workspace。");
            return Fail(Trouble.Failed, "讀不到 Google 日曆，這次先略過：" + LastLine(problem));
        }
        if (Parse(text) is not { } events) return Fail(Trouble.Unreadable, "看不懂 Google 日曆的回應，這次先略過。");
        if (!working) Log.Write("讀得到 Google 日曆了。");
        working = true;
        MayPass = Lapsed = false;
        told.Clear();
        NoteSkipped(problem);
        return events;
    }

    // A calendar list_events.py skipped (its WARNING line) leaves the read a success with that calendar's events missing:
    // logged once, and again only after a read that had it.
    private void NoteSkipped(string errors)
    {
        var now = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match line in SkippedLine().Matches(errors))
        {
            string id = line.Groups["id"].Value, name = line.Groups["name"].Value;
            // Python's repr of the name: '公事行事曆', or "Tom's" when it holds a single quote.
            if (name.Length >= 2 && name[0] is '\'' or '"' && name[^1] == name[0]) name = name[1..^1];
            if (now.Add(id) && !skippedTold.Contains(id))
                Log.Write($"讀不到 Google 日曆裡的「{Briefings.Clean(name, 30)}」（{line.Groups["why"].Value.Trim()}），其他日曆照常；這個日曆上的行程先不會提醒，簡報也不會提。");
        }
        skippedTold = now;
        skipped = now.Count;
    }

    private IReadOnlyList<CalendarEvent>? Fail(Trouble trouble, string? why = null)
    {
        working = false;
        MayPass = trouble is Trouble.Slow or Trouble.Failed or Trouble.Unreadable;
        if (told.Add(trouble)) Log.Write(why ?? "Google 日曆還沒授權（或授權已經失效），行程提醒和簡報裡的行程先略過；要用的話請 Hermes 幫你設定 Google 日曆。");
        return null;
    }

    // list_events.py's list: [{id, summary, start, end, location, status, response, mine, guests, calendar, calendarId,
    // writable}], start and end a dateTime with its offset or, all day, a date. response is the answer of the calendar
    // the copy is listed from, which Google gives per calendar: of a meeting on several calendars list_events.py lists
    // the copy from the user's own side (the primary calendar, the default one for new events, then the one with most
    // rights), so it is the user's own answer as far as can be told. mine, guests, calendar, calendarId and writable
    // are for Hermes and not read here, nor is anything else a newer skill adds. Cancelled events and invitations
    // declined are left out (Google lists both), and one whose start cannot be read is skipped alone. Null when the
    // answer is not such a list.
    internal static List<CalendarEvent>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var events = new List<CalendarEvent>();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "status") == "cancelled" || Text(item, "response") == "declined"
                    || When(Text(item, "start")) is not { } start) continue;
                var end = When(Text(item, "end"))?.At is { } until && until > start.At ? until : start.At;
                // The skill's own stand-in for an event without a title.
                string summary = Text(item, "summary")?.Trim() is { } title && title != "(no title)" ? title : "";
                var read = new CalendarEvent(Text(item, "id") ?? "", summary, start.At, end, start.AllDay, Text(item, "location")?.Trim() ?? "");
                // list_events.py lists an event that is on several calendars once; should the same event and start still
                // come twice, the briefing would read it out twice. Without an id copies cannot be told apart.
                if (read.Id.Length > 0 && !listed.Add(EventReminders.Key(read))) continue;
                events.Add(read);
            }
            return events.OrderBy(e => e.Start).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static (DateTimeOffset At, bool AllDay)? When(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return (new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day)), true);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? (at.ToLocalTime(), false) : null;
    }

    // 2026-10-29T14:00:00+08:00: google_api.py passes a time with an offset on as it is.
    private static string Iso(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    // A traceback's last line says what went wrong; it never carries event contents.
    private static string LastLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) is { } line ? (line.Length > 160 ? line[..160] + "…" : line) : "（沒有說明）";

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("Not authenticated|NOT_AUTHENTICATED|Token is invalid|invalid_grant|RefreshError")]
    private static partial Regex Unauthorized();

    // list_events.py's words when Hermes' curator archived the google-workspace skill it borrows from.
    [GeneratedRegex("google-workspace skill is missing")]
    private static partial Regex MissingWorkspace();

    // WARNING: skipped the calendar '公事行事曆' (work@group.calendar.google.com): HttpError 403.
    [GeneratedRegex(@"^WARNING: skipped the calendar (?<name>.+) \((?<id>[^()\s]+)\): (?<why>.*?)\.?\r?$", RegexOptions.Multiline)]
    private static partial Regex SkippedLine();
}

// What the fish says before an event (EventReminderMinutes ahead), and which starts it already told: an event
// moved to another time is a new start and is reminded again.
internal static class EventReminders
{
    // Held past the start (the user away, a full-screen game), a reminder still says the event has begun for this
    // long; after that it is dropped.
    internal static readonly TimeSpan Late = TimeSpan.FromMinutes(5);

    // id|start, saved in the state file. The calendar is not part of it: Google gives each event a random id, which only
    // the copies of one event on several calendars (an invitation to two of them) share, and those are the same meeting,
    // listed once. With the calendar in it, that meeting would be told again whenever the copy listed came from another
    // calendar (one hidden or shown again in Google Calendar); without it, the starts saved when only the primary
    // calendar was read still match after the upgrade.
    internal static string Key(CalendarEvent e) => e.Id + "|" + e.Start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? StartOf(string key) =>
        key.LastIndexOf('|') is >= 0 and var bar && DateTimeOffset.TryParseExact(key[(bar + 1)..], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var start) ? start : null;

    // Not all-day, starting within `lead` minutes or begun no more than Late ago, not told yet; soonest first. One
    // under way for longer (a trip or a shift of several days, listed until it ends) is never due.
    internal static List<CalendarEvent> Due(IEnumerable<CalendarEvent> events, int lead, DateTimeOffset now, IReadOnlySet<string> told) =>
        lead <= 0 ? [] : events.Where(e => !e.AllDay && e.Start - now <= TimeSpan.FromMinutes(lead) && now - e.Start <= Late && !told.Contains(Key(e)))
            .OrderBy(e => e.Start).ToList();

    // 主人，10 分鐘後（14:00）有「週會」，在 3F 會議室。 Once it has begun: 主人，14:00 的「週會」已經開始了，在 3F 會議室。
    // Title and place come from whoever sent the invitation; the bridge says them itself, cleaned as a quote. A
    // meeting link as the place (Zoom, Teams, Meet) reads 是線上會議.
    internal static string Speech(CalendarEvent e, DateTimeOffset now)
    {
        string title = Briefings.Clean(e.Summary, 30), place = Briefings.Clean(e.Location, 30);
        // 在 3F 會議室, 在圖書館: a space only before a Latin letter or a digit.
        string where = Briefings.Online(e.Location) ? "，是線上會議" : place.Length == 0 ? "" : char.IsAsciiLetterOrDigit(place[0]) ? "，在 " + place : "，在" + place;
        string clock = e.Start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (now >= e.Start) return $"主人，{clock} 的{(title.Length > 0 ? $"「{title}」" : "行程")}已經開始了{where}。";
        int minutes = (int)Math.Ceiling((e.Start - now).TotalMinutes);
        return $"主人，{minutes} 分鐘後（{clock}）有{(title.Length > 0 ? $"「{title}」" : "一個行程")}{where}。";
    }
}

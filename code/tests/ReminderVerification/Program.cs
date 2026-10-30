// The fish's own reminders (CompanionReminders) through simulated days: an office day, a night owl, a laptop
// running down, rain on the way, a restart, a long agent conversation, a computer that slept. Each run prints
// what the fish would say and when, then checks it never nags an empty chair, never repeats itself too soon
// and says what it should.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BigBlueFish.Windows;

Console.OutputEncoding = Encoding.UTF8;
int failures = 0, passes = 0;
void Check(bool condition, string name)
{
    if (condition) passes++; else failures++;
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
}

var day = new DateTime(2026, 10, 28);
DateTime T(string clock, int days = 0) => day.AddDays(days) + TimeSpan.ParseExact(clock, @"hh\:mm", CultureInfo.InvariantCulture);
Span At(string from, string to, int fromDay = 0, int toDay = 0) => new(T(from, fromDay), T(to, toDay));
var defaults = new CompanionReminders.Settings(SitMinutes: 60, Meals: true, BreakfastHour: 8, LunchHour: 12, DinnerHour: 18, LateHour: 0, Battery: true, Rain: true);

// The window asks every 30 seconds while its bubble is free; busy spans (an agent conversation, a full-screen
// game) only let it watch. A reminder counts as said as soon as it is returned.
List<Said> Run(string name, CompanionReminders fish, DateTime from, DateTime to, Span[] present, CompanionReminders.Settings settings,
    Func<DateTime, (bool OnAC, int? Battery)>? power = null, Func<DateTime, string>? weather = null, Span[]? busy = null)
{
    var said = new List<Said>();
    for (var t = from; t < to; t += TimeSpan.FromSeconds(30))
    {
        double idle = Idle(t, present);
        if (busy?.Any(span => t >= span.From && t < span.To) == true) { fish.Observe(t, idle); continue; }
        var (onAC, battery) = power?.Invoke(t) ?? (true, null);
        if (fish.Next(new(t, idle, onAC, battery, weather?.Invoke(t) ?? ""), settings) is not { } reminder) continue;
        fish.Said(reminder, t);
        said.Add(new(t, reminder.Kind, reminder.Text, idle));
    }
    Console.WriteLine($"--- {name}");
    foreach (var entry in said) Console.WriteLine($"    {entry.At:MM-dd HH:mm:ss} {entry.Kind,-7} {entry.Text}");
    // What holds for every run.
    Check(said.All(entry => entry.Idle < 60), name + ": nothing is said while the user is away");
    Check(said.Zip(said.Skip(1)).All(pair => pair.Second.At - pair.First.At >= TimeSpan.FromMinutes(3)), name + ": reminders keep at least 3 minutes apart");
    Check(said.Select((entry, i) => (entry, i)).Where(x => x.entry.Kind == "sit")
        .All(x => x.i == 0 || x.entry.At - said[x.i - 1].At >= TimeSpan.FromMinutes(15)), name + ": a break reminder waits 15 minutes after any other");
    Check(said.Where(entry => entry.Kind == "meal").GroupBy(entry => (entry.At.Date, entry.Text.Contains("早餐") ? 0 : entry.Text.Contains("午餐") ? 1 : 2))
        .All(group => group.Count() == 1), name + ": each meal is mentioned once a day");
    Check(said.Where(entry => entry.Kind == "late").Zip(said.Where(entry => entry.Kind == "late").Skip(1))
        .All(pair => pair.Second.At - pair.First.At >= TimeSpan.FromMinutes(60)), name + ": a late night is mentioned at most once an hour");
    return said;
}

static double Idle(DateTime t, Span[] present)
{
    if (present.Any(span => t >= span.From && t < span.To)) return 4; // typing every few seconds
    var ended = present.Where(span => span.To <= t).Select(span => span.To).DefaultIfEmpty(DateTime.MinValue).Max();
    return ended == DateTime.MinValue ? 86400 : (t - ended).TotalSeconds;
}
Said? First(List<Said> said, string kind, string? text = null) => said.FirstOrDefault(entry => entry.Kind == kind && (text == null || entry.Text.Contains(text)));

// 1. An office day: a short pause in the afternoon is no break, lunch away is.
{
    var said = Run("office day", new(), T("08:00"), T("01:00", 1),
        [At("08:50", "12:10"), At("13:00", "15:00"), At("15:03", "18:30"), At("20:00", "00:40", 0, 1)], defaults);
    Check(First(said, "meal", "早餐") is { } breakfast && breakfast.At == T("08:50") && breakfast.Text == "早上 8 點多了，早餐吃了嗎？別餓著自己喔。", "office day: breakfast is mentioned on arrival at 08:50, as 8 點多了");
    Check(First(said, "sit") is { } sit && sit.At == T("09:50") && sit.Text == "已經連續用電腦 1 小時了，起來伸伸懶腰、喝口水吧～", "office day: the first break reminder comes after an hour of use");
    var morning = said.Where(entry => entry.Kind == "sit" && entry.At < T("12:00")).ToList();
    Check(morning.Select(entry => entry.At).SequenceEqual([T("09:50"), T("10:50"), T("11:50")]), "office day: unheeded, it comes back after every hour of use, not every few minutes");
    Check(morning.Count == 3 && morning[1].Text.StartsWith("還在忙嗎") && morning[2].Text.StartsWith("已經 3 小時了，眼睛和腰"), "office day: each follow-up is worded differently");
    Check(First(said, "meal", "午餐")?.At == T("12:00") && First(said, "meal", "午餐")!.Text == "中午 12 點了，該吃午餐囉～", "office day: lunch at 12:00");
    Check(said.FirstOrDefault(entry => entry.Kind == "sit" && entry.At > T("12:00")) is { } afternoon && afternoon.At == T("14:00")
        && afternoon.Text.StartsWith("已經連續用電腦 1 小時"), "office day: lunch away starts a new stretch, the next reminder is at 14:00");
    Check(said.Any(entry => entry.Kind == "sit" && entry.At == T("16:00") && entry.Text.Contains("3 小時")), "office day: a 3-minute pause at 15:00 does not count as a break");
    Check(First(said, "meal", "晚餐")?.At == T("18:00"), "office day: dinner at 18:00");
    Check(!said.Any(entry => entry.Kind == "sit" && entry.At > T("18:00") && entry.At < T("18:15")), "office day: no break reminder right after dinner");
    Check(First(said, "late") is { } late && late.At == T("00:00", 1) && late.Text == "已經 00:00 了，早點休息吧～明天再繼續也不遲。", "office day: midnight is mentioned at 00:00");
    Check(!said.Any(entry => entry.At >= T("12:10") && entry.At < T("13:00")), "office day: nothing during the lunch break");
}

// 2. A restart at lunchtime: what was said is remembered.
{
    var first = new CompanionReminders();
    var before = Run("before restart", first, T("11:55"), T("12:05"), [At("11:00", "14:00")], defaults with { SitMinutes = 0 });
    var after = Run("after restart", new(first.Remembered), T("12:05"), T("13:30"), [At("11:00", "14:00")], defaults with { SitMinutes = 0 });
    Check(before.Count(entry => entry.Kind == "meal") == 1 && after.All(entry => entry.Kind != "meal"), "restart: lunch is not mentioned again after a restart");
}

// 3. A night owl, late reminders from 23:00, two nights in a row.
{
    var fish = new CompanionReminders();
    var settings = defaults with { LateHour = 23, SitMinutes = 0, Meals = false };
    var first = Run("night owl, night 1", fish, T("22:00"), T("06:00", 1), [At("22:00", "02:30", 0, 1), At("05:30", "06:00", 1, 1)], settings);
    Check(first.Where(entry => entry.Kind == "late").Select(entry => entry.At).SequenceEqual([T("23:00"), T("00:00", 1), T("01:00", 1), T("02:00", 1)]), "night owl: 23:00, then once an hour until bed");
    Check(first.Where(entry => entry.Kind == "late").Skip(1).All(entry => entry.Text.Contains("還不睡嗎")), "night owl: later ones are worded as a follow-up");
    Check(!first.Any(entry => entry.At >= T("05:00", 1)), "night owl: nothing at 05:30, the night is over");
    var second = Run("night owl, night 2", fish, T("22:30", 1), T("00:30", 2), [At("23:10", "00:20", 1, 2)], settings);
    Check(First(second, "late") is { } next && next.At == T("23:10", 1) && next.Text.StartsWith("已經 23:10"), "night owl: the next night starts afresh");
    var off = Run("late reminders off", new(), T("22:00"), T("03:00", 1), [At("22:00", "03:00", 0, 1)], settings with { LateHour = -1 });
    Check(off.Count == 0, "late reminders off: nothing is said");
}

// 4. A laptop running down, plugged in at 8%, unplugged again later.
{
    (bool, int?) Power(DateTime t) => t < T("11:30") ? (false, 35 - (int)(t - T("10:00")).TotalMinutes / 3)
        : t < T("12:30") ? (true, 60) : (false, 50 - (int)(t - T("12:30")).TotalMinutes / 3);
    var said = Run("laptop battery", new(), T("10:00"), T("15:30"), [At("10:00", "15:30")], defaults with { SitMinutes = 0, Meals = false }, Power);
    var battery = said.Where(entry => entry.Kind == "battery").ToList();
    Check(battery.Select(entry => entry.At).SequenceEqual([T("10:45"), T("11:15"), T("14:00"), T("14:30")]) && battery[0].Text == "電量剩 20%，記得接上電源喔。"
        && battery[1].Text.StartsWith("電量只剩 10%"), "laptop battery: 20% and 10% once each per discharge, again after charging");
    var desktop = Run("desktop without battery", new(), T("10:00"), T("12:00"), [At("10:00", "12:00")], defaults with { SitMinutes = 0, Meals = false }, _ => (false, null));
    Check(desktop.Count == 0, "desktop without battery: nothing about power");
    var straight = Run("battery found at 9%", new(), T("10:00"), T("10:30"), [At("10:00", "10:30")], defaults with { SitMinutes = 0, Meals = false }, _ => (false, 9));
    Check(straight.Count == 1 && straight[0].Text.StartsWith("電量只剩 9%"), "battery found at 9%: one warning, not a 20% one after it");
}

// 5. Rain on the way (CWA forecast text as CompanionWeather writes it), refreshed in the afternoon.
{
    const string Morning = "地點：臺灣 · 雲林縣 · 虎尾鎮（中央氣象署鄉鎮天氣預報）。10月28日12:00到10月28日15:00：晴。降雨機率0%。溫度攝氏28至29度。舒適。10月28日15:00到10月28日18:00：短暫陣雨。降雨機率70%。溫度攝氏26至28度。";
    const string Afternoon = "地點：臺灣 · 雲林縣 · 虎尾鎮（中央氣象署鄉鎮天氣預報）。10月28日15:00到10月28日18:00：短暫陣雨。降雨機率70%。溫度攝氏26至28度。10月28日18:00到10月28日21:00：陣雨。降雨機率80%。溫度攝氏24至26度。";
    var said = Run("rainy afternoon", new(), T("07:00"), T("23:30"), [At("09:00", "23:30")], defaults with { SitMinutes = 0, Meals = false, LateHour = -1 },
        weather: t => t < T("14:00") ? Morning : Afternoon);
    var rain = said.Where(entry => entry.Kind == "rain").ToList();
    Check(rain.Count == 2 && rain[0].At == T("09:00") && rain[0].Text == "15:00 到 18:00 降雨機率 70%，出門記得帶傘喔！", "rainy afternoon: told on arrival, six hours ahead");
    Check(rain.Count == 2 && rain[1].At == T("15:00") && rain[1].Text == "現在到 21:00 降雨機率 80%，出門記得帶傘喔！", "rainy afternoon: back-to-back rainy periods read as one, not repeated within six hours");
    var night = Run("rain at night", new(), T("22:00"), T("06:00", 1), [At("22:00", "06:00", 0, 1)], defaults with { SitMinutes = 0, Meals = false, LateHour = -1 },
        weather: _ => "10月29日00:00到10月29日03:00：雨。降雨機率90%。10月29日03:00到10月29日06:00：雨。降雨機率90%。");
    Check(night.Count == 0, "rain at night: nothing between 22:00 and 06:00");
    var missing = CompanionReminders.Rains("10月28日12:00到10月28日15:00：晴。10月28日15:00到10月28日18:00：雨。降雨機率90%。", T("10:00"));
    Check(missing.Count == 1 && missing[0].Start == T("15:00") && missing[0].Percent == 90, "a period without a rain chance does not borrow the next one's");
    var daily = Run("Open-Meteo day", new(), T("08:00"), T("09:00"), [At("08:00", "09:00")], defaults with { SitMinutes = 0, Meals = false },
        weather: _ => "地點：安徽省 · 合肥市 · 蜀山区（區縣中心附近的天氣估算）。資料日期2026-10-28最低14.4℃、最高23.8℃，最高降水機率75%。");
    Check(daily.Count == 1 && daily[0].Text == "今天降雨機率 75%，出門記得帶傘喔！", "Open-Meteo day: one reminder for the whole day");
    var newYear = CompanionReminders.Rains("1月1日00:00到1月1日03:00：雨。降雨機率80%。", new DateTime(2026, 12, 31, 22, 0, 0));
    Check(newYear.Count == 1 && newYear[0].Start == new DateTime(2027, 1, 1), "a period after New Year belongs to the next year");
}

// 6. A long agent conversation over lunchtime: the reminder waits for it and still comes.
{
    var said = Run("conversation at lunch", new(), T("11:50"), T("13:00"), [At("11:50", "13:00")], defaults with { SitMinutes = 0 }, busy: [At("11:58", "12:25")]);
    Check(said.Count == 1 && said[0].At == T("12:25") && said[0].Text == "中午 12 點了，該吃午餐囉～", "conversation at lunch: lunch waits for the conversation, said once");
    var away = Run("away at lunch", new(), T("11:00"), T("15:00"), [At("11:00", "11:30"), At("13:45", "15:00")], defaults with { SitMinutes = 0 });
    Check(away.Count == 0, "away at lunch: not mentioned once lunchtime is over");
    var late = Run("back at 13:20", new(), T("11:00"), T("14:00"), [At("11:00", "11:30"), At("13:20", "14:00")], defaults with { SitMinutes = 0 });
    Check(late.Count == 1 && late[0].At == T("13:20") && late[0].Text == "中午 12 點多了，午餐吃了嗎？別餓著自己喔。", "back at 13:20: asked whether lunch was eaten");
}

// 7. The computer slept: the gap in the checks is a break; the next stretch starts on waking.
{
    var fish = new CompanionReminders();
    for (var t = T("09:00"); t < T("10:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, 3);
    Check(fish.ActiveFor(T("10:00")) is { } before && Math.Abs(before.TotalMinutes - 60) < 1, "sleep: an hour of use is counted");
    fish.Observe(T("10:40"), 2);
    Check(fish.ActiveFor(T("10:41")) is { } after && after < TimeSpan.FromMinutes(2) && fish.LastBreak is { } pause && Math.Abs(pause.TotalMinutes - 40) < 1,
        "sleep: after waking the stretch starts again and the 40-minute gap is the last break");
    fish.Observe(T("10:41"), 400);
    Check(fish.ActiveFor(T("10:41")) == null, "away: no stretch while away");
}

// 8. The clock is set back an hour (time sync, the user): counting starts afresh and nothing stalls.
{
    var fish = new CompanionReminders();
    for (var t = T("10:00"); t < T("11:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, 3);
    fish.Said(new("sit", "sit", "已經連續用電腦 1 小時了", null), T("11:00"));
    fish.Observe(T("10:00"), 3);
    Check(fish.ActiveFor(T("10:00")) is { } stretch && stretch < TimeSpan.FromMinutes(1), "clock set back: the stretch starts again");
    var said = Run("clock set back", fish, T("10:00"), T("12:30"), [At("09:00", "13:00")], defaults with { Meals = true });
    Check(First(said, "meal", "午餐")?.At == T("12:00") && First(said, "sit")?.At <= T("12:15"), "clock set back: lunch and the next break reminder are not held back by times now in the future");
}

// 9. The fish restarts (an update): a short restart keeps the stretch, a long one is a break.
{
    var fish = new CompanionReminders();
    for (var t = T("09:00"); t <= T("09:50"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, 3);
    var soon = new CompanionReminders(fish.Remembered);
    soon.Observe(T("09:51"), 2);
    Check(soon.ActiveFor(T("09:51")) is { } kept && Math.Abs(kept.TotalMinutes - 51) < 1, "restart: a one-minute restart keeps the 51-minute stretch");
    var later = new CompanionReminders(fish.Remembered);
    later.Observe(T("10:10"), 2);
    Check(later.ActiveFor(T("10:10")) is { } fresh && fresh < TimeSpan.FromMinutes(1), "restart: twenty minutes later it is a new stretch");
    Check(!new CompanionReminders(fish.Remembered).Remembered.Keys.Any(key => key.StartsWith("meal")), "restart: the stretch is remembered apart from what was said");
}

Check(CompanionReminders.Summary(new DateTime(2026, 10, 28, 15, 50, 0), 8, TimeSpan.FromMinutes(65), true, null) == "10月28日 星期三 15:50。使用者 8 秒前還在操作電腦，這次已經連續使用 1 小時 5 分鐘沒休息。有插電。",
    "summary: seconds stay seconds, the stretch reads in hours and minutes");
Check(CompanionReminders.Summary(new DateTime(2026, 10, 28, 9, 5, 0), 720, null, false, 35) == "10月28日 星期三 09:05。使用者已經 12 分鐘沒碰電腦，不在電腦前。沒插電，電量 35%。",
    "summary: away and on battery");

// 10. Coming back: the start of a stretch after a break is handed over once, with how long the break was.
{
    var fish = new CompanionReminders();
    fish.Observe(T("09:00"), 3);
    Check(fish.TakeReturn() is { Break: null } && fish.TakeReturn() == null, "return: the first stretch after a start has no known break, and is handed over once");
    for (var t = T("09:00"); t < T("10:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, 3);
    Check(fish.TakeReturn() == null, "return: nothing while the user keeps working");
    for (var t = T("10:00"); t < T("10:40"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("10:00")).TotalSeconds + 3);
    Check(fish.TakeReturn() == null, "return: nothing while away");
    fish.Observe(T("10:40"), 2);
    Check(fish.TakeReturn() is { Break: { } pause } back && Math.Abs(pause.TotalMinutes - 40) < 1 && back.At == T("10:40").AddSeconds(-2), "return: back after 40 minutes is told once, with the break");
    var restarted = new CompanionReminders(fish.Remembered);
    restarted.Observe(T("10:41"), 2);
    Check(restarted.TakeReturn() == null, "return: a quick restart of the fish is no return");
}

// 11. The wall clock moved forward (a time sync) is no break; a film watched with the keyboard still is no absence.
{
    var fish = new CompanionReminders();
    fish.Observe(T("09:00"), 3, TimeSpan.FromSeconds(1));
    fish.TakeReturn();
    for (var t = T("09:00"); t < T("10:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, 3, TimeSpan.FromSeconds(30));
    fish.Observe(T("18:00"), 3, TimeSpan.FromSeconds(1));
    Check(fish.TakeReturn() == null && fish.ActiveFor(T("18:00")) is { } kept && Math.Abs(kept.TotalMinutes - 60) < 1,
        "clock moved forward: no return, the hour of use carries on");
    for (var t = T("18:00"); t < T("19:30"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("18:00")).TotalSeconds + 3, TimeSpan.FromSeconds(30), watching: true);
    fish.Observe(T("19:30"), 2, TimeSpan.FromSeconds(30));
    Check(fish.TakeReturn() is { Watching: true, Break: { } film } && Math.Abs(film.TotalMinutes - 90) < 1, "a film: the return says the screen was being watched");
    for (var t = T("19:30"); t < T("20:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("19:30")).TotalSeconds + 3, TimeSpan.FromSeconds(30));
    fish.Observe(T("20:00"), 2, TimeSpan.FromSeconds(30));
    Check(fish.TakeReturn() is { Watching: false }, "the next break, really away, is not taken for watching");
    // Watching a video for two minutes while at work, then a real two-hour absence: the absence is no film.
    for (var t = T("20:00"); t < T("21:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("20:00")).TotalSeconds % 600 < 180 ? 3 : (t - T("20:00")).TotalSeconds % 600 - 177, TimeSpan.FromSeconds(30), watching: true);
    for (var t = T("21:00"); t < T("23:00"); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("21:00")).TotalSeconds + 3, TimeSpan.FromSeconds(30));
    fish.Observe(T("23:00"), 2, TimeSpan.FromSeconds(30));
    Check(fish.TakeReturn() is { Watching: false, Break: { } away } && away > TimeSpan.FromHours(1.9), "watching during work does not make a later absence a film");
    // Two hours away of which only the first ten minutes had a full-screen program: away, not watching.
    for (var t = T("23:00"); t < T("01:00", 1); t += TimeSpan.FromSeconds(30)) fish.Observe(t, (t - T("23:00")).TotalSeconds + 3, TimeSpan.FromSeconds(30), watching: t < T("23:10"));
    fish.Observe(T("01:00", 1), 2, TimeSpan.FromSeconds(30));
    Check(fish.TakeReturn() is { Watching: false }, "a break only partly spent watching is an absence");
}

// 11b. A wall clock moved forward after a break reminder does not bring the next one five minutes later.
{
    var fish = new CompanionReminders();
    var settings = defaults with { Meals = false, LateHour = -1 };
    var said = new List<string>();
    var t = T("09:00");
    for (; t < T("10:05"); t += TimeSpan.FromSeconds(30))
        if (fish.Next(new(t, 3, true, null, ""), settings) is { } reminder) { fish.Said(reminder, t); said.Add($"{t:HH:mm} {reminder.Kind}"); }
    fish.Observe(t + TimeSpan.FromHours(1), 3, TimeSpan.FromSeconds(30));
    for (t += TimeSpan.FromHours(1); t < T("11:55"); t += TimeSpan.FromSeconds(30))
    {
        fish.Observe(t, 3, TimeSpan.FromSeconds(30));
        if (fish.Next(new(t, 3, true, null, ""), settings, observe: false) is { } reminder) { fish.Said(reminder, t); said.Add($"{t:HH:mm} {reminder.Kind}"); }
    }
    Check(said.SequenceEqual(["10:00 sit", "11:59 sit"]) || said.SequenceEqual(["10:00 sit"]), "clock moved forward: the next break reminder keeps its hour: " + string.Join(", ", said));
}

// 12. Everything off.
{
    var said = Run("everything off", new(), T("07:00"), T("03:00", 1), [At("07:00", "03:00", 0, 1)],
        new(SitMinutes: 0, Meals: false, BreakfastHour: 8, LunchHour: 12, DinnerHour: 18, LateHour: -1, Battery: false, Rain: false),
        _ => (false, 5), _ => "10月28日12:00到10月28日15:00：雨。降雨機率90%。");
    Check(said.Count == 0, "everything off: a whole day passes in silence");
}

Check(CompanionReminders.Duration(TimeSpan.FromMinutes(45)) == "45 分鐘" && CompanionReminders.Duration(TimeSpan.FromMinutes(125)) == "2 小時 5 分鐘"
    && CompanionReminders.Duration(TimeSpan.FromMinutes(120)) == "2 小時", "durations read naturally");
Check(CompanionReminders.Hour(8) == "早上 8 點" && CompanionReminders.Hour(12) == "中午 12 點" && CompanionReminders.Hour(18) == "晚上 6 點"
    && CompanionReminders.Hour(15) == "下午 3 點" && CompanionReminders.Hour(0) == "凌晨 0 點", "hours read naturally");

Console.WriteLine($"passed {passes}, failed {failures}");
return failures == 0 ? 0 : 1;

internal sealed record Span(DateTime From, DateTime To);
internal sealed record Said(DateTime At, string Kind, string Text, double Idle);

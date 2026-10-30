// Fork-original file of the taiwan-edition fork by acer1204 (2026-10-28); not part of upstream Bai-Cute/big-blue-fish-desktop-pet.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BigBlueFish.Windows;

// The fish's own reminders: a break after a long stretch at the computer, meals, a late night, a low battery
// and rain ahead. Pure logic, so a whole simulated day can run through it: the window reports what it sees
// about once a second (Observe) and asks Next whenever nothing else is using the bubble. Nothing is said to an
// empty chair (the keyboard or mouse must have been used within the last minute), two reminders keep a few
// minutes apart, and what was said is remembered by key so a restart does not repeat it.
internal sealed class CompanionReminders
{
    internal sealed record Settings(int SitMinutes, bool Meals, int BreakfastHour, int LunchHour, int DinnerHour, int LateHour, bool Battery, bool Rain);
    // Local is the wall clock; Battery is null without a battery (or when Windows cannot tell); Weather is the cached forecast text.
    internal sealed record Moment(DateTime Local, double IdleSeconds, bool OnAC, int? Battery, string Weather);
    internal sealed record Reminder(string Kind, string Key, string Text, string? Action);
    internal sealed record Rain(DateTime Start, DateTime End, int Percent);

    // Idle this long is a break; a stretch of use starts again with the next input.
    internal static readonly TimeSpan BreakLength = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Present = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Spacing = TimeSpan.FromMinutes(3);
    // Any other reminder (a meal, a late night) already sends the user away from the screen for a while.
    private static readonly TimeSpan SitAfterOther = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LateAgain = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan RainAgain = TimeSpan.FromHours(6);
    private static readonly TimeSpan RainAhead = TimeSpan.FromHours(6);
    private static readonly TimeSpan MealWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan Kept = TimeSpan.FromDays(2);
    private const int RainLikely = 60;

    private readonly Dictionary<string, DateTime> said; // key → local time it was said
    private DateTime? activeSince, seen, lastInput;
    private DateTime lastSaid = DateTime.MinValue;
    private (DateTime? Stretch, int Count) sitTold; // break reminders said in the current stretch
    private (DateTime At, TimeSpan? Break, bool Watching)? returned; // the latest stretch start, until TakeReturn
    private TimeSpan watched; // how much of the stillness since the last input was spent watching the screen
    internal TimeSpan? LastBreak { get; private set; }
    internal (Reminder Reminder, DateTime At)? Last { get; private set; }

    private const string SinceKey = "stretch:since", SeenKey = "stretch:seen";

    internal CompanionReminders(IReadOnlyDictionary<string, DateTime>? remembered = null)
    {
        said = remembered == null ? new() : new(remembered);
        // A restart shorter than a break continues the stretch it interrupted; Observe ends it otherwise.
        if (said.Remove(SinceKey, out var since) & said.Remove(SeenKey, out var last)) (activeSince, seen) = (since, last);
    }

    // What was said, and the stretch under way, for the next start.
    internal IReadOnlyDictionary<string, DateTime> Remembered
    {
        get
        {
            var all = new Dictionary<string, DateTime>(said);
            if (activeSince is { } since && seen is { } last) (all[SinceKey], all[SeenKey]) = (since, last);
            return all;
        }
    }

    // How long the user has been at the computer without a break; null while away.
    internal TimeSpan? ActiveFor(DateTime local) => activeSince is { } since && local >= since ? local - since : null;

    // `elapsed` is the time since the last call on a clock that never jumps (and counts sleep); with it a wall clock
    // moved forward (a time sync, the user) is not mistaken for a break. `watching` says the screen kept the user's
    // eyes meanwhile (a full-screen film or game, the screen not locked): a break spent mostly so is no absence.
    internal void Observe(DateTime local, double idleSeconds, TimeSpan? elapsed = null, bool watching = false)
    {
        var input = local - TimeSpan.FromSeconds(Math.Max(0, idleSeconds));
        var step = elapsed ?? (seen is { } earlier && local > earlier ? local - earlier : TimeSpan.Zero);
        if (seen is { } previous && elapsed is { } real && local - previous >= BreakLength && real < BreakLength)
        {
            // Everything kept on the wall clock moves with it, or the next reminder would come too soon.
            var jump = local - previous - real;
            if (activeSince != null) activeSince += jump;
            if (lastInput != null) lastInput += jump;
            foreach (var key in said.Keys.ToList()) said[key] += jump;
            if (lastSaid != DateTime.MinValue) lastSaid += jump;
            if (sitTold.Stretch is { } stretch) sitTold = (stretch + jump, sitTold.Count);
            if (Last is { } told) Last = (told.Reminder, told.At + jump);
            previous += jump;
            seen = previous;
            step = real;
        }
        // Calls that stopped for longer than a break (the computer slept) are a break as well; a clock set back
        // (time sync, the user) starts counting afresh.
        if (seen is { } last && activeSince != null && (local < last || local - last >= BreakLength)) { activeSince = null; lastInput = local < last ? null : last; }
        seen = local;
        // Only the stillness since the last input counts: input during a stretch starts it again. The call that ends
        // a break keeps it, for the return to tell.
        if (idleSeconds < Present.TotalSeconds) { if (activeSince != null) watched = TimeSpan.Zero; }
        else if (watching) watched += step;
        if (idleSeconds >= BreakLength.TotalSeconds)
        {
            activeSince = null;
            lastInput = input;
            return;
        }
        if (activeSince != null) return;
        // The first input after a break starts the stretch; the break is how long there was none.
        activeSince = input;
        LastBreak = lastInput is { } before && input > before ? input - before : null;
        // Watching when most of the break (counted from a minute in, when watching is first looked at) was.
        returned = (input, LastBreak, LastBreak is { } pause && pause > Present && watched.TotalSeconds >= 0.8 * (pause - Present).TotalSeconds);
        watched = TimeSpan.Zero;
    }

    // The start of the latest stretch, once; Break is null when how long the user was away is not known (the fish
    // just started, or the clock was set back); Watching when the screen was kept busy all along.
    internal (DateTime At, TimeSpan? Break, bool Watching)? TakeReturn()
    {
        var start = returned;
        returned = null;
        return start;
    }

    // `observe` false when the caller already keeps Observe going on its own steady clock (the window does).
    internal Reminder? Next(Moment now, Settings settings, bool observe = true)
    {
        if (observe) Observe(now.Local, now.IdleSeconds);
        if (now.OnAC) { said.Remove("battery:20"); said.Remove("battery:10"); }
        if (now.IdleSeconds >= Present.TotalSeconds || Recent(lastSaid, now.Local, Spacing)) return null;
        return Battery(now, settings) ?? Late(now, settings) ?? Meal(now, settings) ?? RainSoon(now, settings) ?? Sit(now, settings);
    }

    internal void Said(Reminder reminder, DateTime local)
    {
        said[reminder.Key] = local;
        // Falling straight to 10% covers the 20% one too.
        if (reminder.Key == "battery:10") said["battery:20"] = local;
        if (reminder.Kind == "sit") sitTold = (activeSince, sitTold.Stretch == activeSince ? sitTold.Count + 1 : 1);
        lastSaid = local;
        Last = (reminder, local);
        foreach (var key in said.Where(entry => local - entry.Value > Kept).Select(entry => entry.Key).ToList()) said.Remove(key);
    }

    private Reminder? Battery(Moment now, Settings settings)
    {
        if (!settings.Battery || now.OnAC || now.Battery is not int percent) return null;
        string? key = percent <= 10 ? "battery:10" : percent <= 20 ? "battery:20" : null;
        if (key == null || said.ContainsKey(key)) return null;
        return new("battery", key, percent <= 10 ? $"電量只剩 {percent}% 了，快接上電源，不然電腦要關機囉！" : $"電量剩 {percent}%，記得接上電源喔。", null);
    }

    private Reminder? Late(Moment now, Settings settings)
    {
        if (settings.LateHour < 0) return null;
        int hour = now.Local.Hour;
        bool late = settings.LateHour >= 12 ? hour >= settings.LateHour || hour < 5 : hour >= settings.LateHour && hour < 5;
        if (!late) return null;
        // A night runs past midnight; it is named after the evening it began on.
        string key = "late:" + (hour < 12 ? now.Local.Date.AddDays(-1) : now.Local.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool again = said.TryGetValue(key, out var at);
        if (again && Recent(at, now.Local, LateAgain)) return null;
        string clock = now.Local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return new("late", key, again ? $"{clock} 了，還不睡嗎？身體要緊，早點休息喔。" : $"已經 {clock} 了，早點休息吧～明天再繼續也不遲。", "哈欠连天");
    }

    private Reminder? Meal(Moment now, Settings settings)
    {
        if (!settings.Meals) return null;
        foreach (var (name, hour, action) in new[] { ("早餐", settings.BreakfastHour, "吃早餐"), ("午餐", settings.LunchHour, "吃午餐"), ("晚餐", settings.DinnerHour, "吃晚餐") })
        {
            var start = now.Local.Date.AddHours(hour);
            if (now.Local < start || now.Local - start >= MealWindow) continue;
            string key = $"meal:{now.Local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:{name}";
            if (said.ContainsKey(key)) continue;
            string text = now.Local - start < TimeSpan.FromMinutes(30) ? $"{Hour(hour)}了，該吃{name}囉～" : $"{Hour(hour)}多了，{name}吃了嗎？別餓著自己喔。";
            return new("meal", key, text, action);
        }
        return null;
    }

    private Reminder? RainSoon(Moment now, Settings settings)
    {
        if (!settings.Rain || now.Local.Hour < 6 || now.Local.Hour >= 22) return null;
        if (said.TryGetValue("rain", out var at) && Recent(at, now.Local, RainAgain)) return null;
        var periods = Rains(now.Weather, now.Local).Where(r => r.End > now.Local && r.Start - now.Local <= RainAhead).OrderBy(r => r.Start).ToList();
        int first = periods.FindIndex(r => r.Percent >= RainLikely);
        if (first < 0) return null;
        // Back-to-back rainy periods are one stretch of rain.
        var (start, end, chance) = (periods[first].Start, periods[first].End, periods[first].Percent);
        for (int i = first + 1; i < periods.Count && periods[i].Percent >= RainLikely && periods[i].Start <= end; i++)
            (end, chance) = (periods[i].End, Math.Max(chance, periods[i].Percent));
        string when = end - start >= TimeSpan.FromHours(20) ? "今天" : start <= now.Local ? $"現在到 {Clock(end, now.Local)} " : $"{Clock(start, now.Local)} 到 {Clock(end, now.Local)} ";
        return new("rain", "rain", $"{when}降雨機率 {chance}%，出門記得帶傘喔！", null);
    }

    private Reminder? Sit(Moment now, Settings settings)
    {
        if (settings.SitMinutes <= 0 || ActiveFor(now.Local) is not { } stretch || stretch < TimeSpan.FromMinutes(settings.SitMinutes)
            || Recent(lastSaid, now.Local, SitAfterOther)) return null;
        // Unheeded, it comes back after the same length of use again (a reminder said before this stretch began,
        // or before a restart, belongs to an earlier one), worded differently each time.
        bool again = said.TryGetValue("sit", out var at) && at >= activeSince;
        if (again && Recent(at, now.Local, TimeSpan.FromMinutes(Math.Max(30, settings.SitMinutes)))) return null;
        int count = again && sitTold.Stretch == activeSince ? sitTold.Count : again ? 1 : 0;
        string length = Duration(stretch);
        return new("sit", "sit", count switch
        {
            0 => $"已經連續用電腦 {length}了，起來伸伸懶腰、喝口水吧～",
            1 => $"還在忙嗎？已經 {length}沒休息了，站起來走一走吧！",
            _ => $"已經 {length}了，眼睛和腰都需要休息，先離開螢幕五分鐘吧。"
        }, "超大伸懒腰");
    }

    // get_context's plain reading of the moment, so an agent does not have to turn seconds into minutes itself:
    // 10月28日 星期三 15:50。使用者 8 秒前還在操作電腦，這次已經連續使用 1 小時 5 分鐘沒休息。有插電。
    internal static string Summary(DateTime now, double idleSeconds, TimeSpan? active, bool onAC, int? battery)
    {
        var idle = TimeSpan.FromSeconds(Math.Max(0, idleSeconds));
        string since = idle < TimeSpan.FromMinutes(1) ? $"{(int)idle.TotalSeconds} 秒" : Duration(idle);
        string use = active is { } stretch ? $"使用者 {since}前還在操作電腦，這次已經連續使用 {(stretch < TimeSpan.FromMinutes(1) ? "不到 1 分鐘" : Duration(stretch))}沒休息。"
            : $"使用者已經 {since}沒碰電腦，不在電腦前。";
        string power = onAC ? "有插電。" : battery is int percent ? $"沒插電，電量 {percent}%。" : "沒插電。";
        return $"{now.Month}月{now.Day}日 {Weekday(now)} {now.ToString("HH:mm", CultureInfo.InvariantCulture)}。" + use + power;
    }

    internal static string Weekday(DateTime time) => "星期" + "日一二三四五六"[(int)time.DayOfWeek];

    // A time after now (the clock was set back since) is not recent.
    private static bool Recent(DateTime at, DateTime now, TimeSpan span) => now >= at && now - at < span;

    // 早上 8 點, 中午 12 點, 晚上 6 點.
    internal static string Hour(int hour) => hour switch
    {
        < 5 => $"凌晨 {hour} 點", < 11 => $"早上 {hour} 點", 11 => "上午 11 點", 12 => "中午 12 點", < 18 => $"下午 {hour - 12} 點", _ => $"晚上 {hour - 12} 點"
    };

    // 45 分鐘, 1 小時, 2 小時 5 分鐘.
    internal static string Duration(TimeSpan span)
    {
        int minutes = (int)span.TotalMinutes;
        return minutes < 60 ? $"{minutes} 分鐘" : minutes % 60 == 0 ? $"{minutes / 60} 小時" : $"{minutes / 60} 小時 {minutes % 60} 分鐘";
    }

    private static string Clock(DateTime time, DateTime now) =>
        (time.Date > now.Date ? "明天 " : "") + time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // CompanionWeather's forecast text: CWA periods「10月28日12:00到10月28日15:00：晴。降雨機率0%。…」, or the
    // Open-Meteo day「資料日期2026-10-28…最高降水機率40%」. The periods carry no year; the nearest one is meant.
    private static readonly Regex Period = new(@"(\d{1,2})月(\d{1,2})日(\d{1,2}):(\d{2})到(\d{1,2})月(\d{1,2})日(\d{1,2}):(\d{2})：(?:(?!\d{1,2}月\d{1,2}日\d{1,2}:\d{2}到).)*?降雨機率(\d{1,3})%",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Day = new(@"資料日期(\d{4}-\d{2}-\d{2}).*?最高降水機率(\d{1,3})%", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static IReadOnlyList<Rain> Rains(string weather, DateTime local)
    {
        var rains = new List<Rain>();
        if (string.IsNullOrEmpty(weather)) return rains;
        DateTime At(Match match, int group)
        {
            int Number(int index) => int.Parse(match.Groups[group + index].Value, CultureInfo.InvariantCulture);
            var time = new DateTime(local.Year, Number(0), Number(1)).AddHours(Number(2)).AddMinutes(Number(3));
            return time - local > TimeSpan.FromDays(180) ? time.AddYears(-1) : local - time > TimeSpan.FromDays(180) ? time.AddYears(1) : time;
        }
        foreach (Match match in Period.Matches(weather))
        {
            try { rains.Add(new(At(match, 1), At(match, 5), int.Parse(match.Groups[9].Value, CultureInfo.InvariantCulture))); }
            catch (ArgumentOutOfRangeException) { } // a date that does not exist
        }
        if (rains.Count == 0 && Day.Match(weather) is { Success: true } day
            && DateTime.TryParseExact(day.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            rains.Add(new(date, date.AddDays(1), int.Parse(day.Groups[2].Value, CultureInfo.InvariantCulture)));
        return rains;
    }
}

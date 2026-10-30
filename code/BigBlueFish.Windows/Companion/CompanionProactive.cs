// Fork-original file of the taiwan-edition fork by acer1204 (2026-10-28); not part of upstream Bai-Cute/big-blue-fish-desktop-pet.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace BigBlueFish.Windows;

// Wires CompanionReminders to the window (what it sees each second, and when the bubble is free for a
// reminder) and gives agents the same picture through the MCP tool get_context.
public partial class MainWindow
{
    private const string ReminderMemoryPath = "cache/reminders.json";
    // An agent that spoke this recently may still be in a conversation (the Hermes bridge refreshes its
    // progress bubble at least every 15 seconds, and a reply wants reading).
    private static readonly TimeSpan AgentQuiet = TimeSpan.FromSeconds(60);
    private CompanionReminders? reminders;
    // On Environment.TickCount64, which no clock change moves: the wall clock set back would stall these.
    private long nextObservation, nextReminder, nextMemorySave, observedAt;
    // --test-mode only: a shifted wall clock and a fixed idle time, so a test can visit lunchtime or midnight.
    private TimeSpan reminderClockShift;
    private double? reminderIdleOverride;
    private DateTime ReminderNow => DateTime.Now + reminderClockShift;
    private double ReminderIdle => reminderIdleOverride ?? CompanionNative.IdleSeconds;
    private CompanionReminders Reminders => reminders ??= new(ReadReminderMemory());
    private CompanionReminders.Settings ReminderSettings => new(preferences.SitReminderMinutes, preferences.MealReminder,
        preferences.BreakfastHour, preferences.LunchHour, preferences.DinnerHour, preferences.LateReminder ? preferences.LateReminderHour : -1, preferences.BatteryReminder, preferences.RainReminder);

    // Every frame of the main timer, hidden or not: a stretch of use goes on behind a full-screen game too.
    private void ObserveActivity(DateTime utc)
    {
        long ticks = Environment.TickCount64;
        if (ticks < nextObservation) return;
        nextObservation = ticks + 1000;
        double idle = ReminderIdle;
        // A full-screen film, or a full-screen game played with a controller: still keyboard and mouse then mean
        // watching, not away. Not while the screen is locked (the lock screen can be the full-screen window then).
        bool watching = idle >= 60 && CompanionNative.IsFullscreen() && !CompanionNative.Locked;
        Reminders.Observe(ReminderNow, idle, observedAt == 0 ? null : TimeSpan.FromMilliseconds(ticks - observedAt), watching);
        observedAt = ticks;
        // Agents hear when the user is back after a break (the Hermes bridge greets and tells what happened meanwhile).
        if (Reminders.TakeReturn() is { Break: { } pause } back)
            PublishMcpInteraction("user_returned", new { breakMinutes = (int)Math.Round(pause.TotalMinutes), since = Iso(back.At - pause), at = Iso(back.At), watching = back.Watching });
        // The stretch under way survives a restart of the fish (an update, a crash) shorter than a break.
        if (ticks < nextMemorySave) return;
        nextMemorySave = ticks + 60_000;
        SaveReminderMemory();
    }

    // From the main timer once the fish is on screen and no agent's bubble holds the bubble. A reminder also
    // waits while the fish prepares or still shows something of its own, while an agent spoke within the last
    // minute, during a walk, a drag or a click, with the settings or input window open, and while any
    // program is full screen, even one the fish stays visible over. True when a reminder took the bubble.
    private bool Remind(DateTime utc)
    {
        long ticks = Environment.TickCount64;
        if (ticks < nextReminder) return false;
        nextReminder = ticks + 5000;
        if (PuppetActive || companionSettings != null || modelInstaller != null || McpInputOpen || Walking || Touring
            || speechRequestPending || weatherRequestPending || animationPhase is "Drag" or "Click" or "Startup" or "Exit" or "Preview"
            || utc >= externalBubbleAt && utc - externalBubbleAt < AgentQuiet || bubble.Visibility == Visibility.Visible && (speech.IsTyping || bubbleUntil != DateTime.MaxValue)
            || CompanionNative.IsFullscreen()) return false;
        var now = ReminderNow;
        var reminder = Reminders.Next(new(now, ReminderIdle, CompanionNative.OnAC, CompanionNative.BatteryPercent,
            preferences.PublicInfo ? brain.CachedWeather : ""), ReminderSettings, observe: false);
        if (reminder == null) return false;
        ShowCompanionNotice(reminder.Text, true);
        Reminders.Said(reminder, now);
        SaveReminderMemory();
        // The fish's own chatter does not replace the reminder right away.
        var quiet = utc.AddMinutes(2);
        if (nextSpeech < quiet) nextSpeech = quiet;
        if (reminder.Action != null && animationPhase == "Idle" && animationPolicy.Rules.FirstOrDefault(r => r.Id == reminder.Action) is { } rule)
            StartAnimationEvent(rule);
        return true;
    }

    private static string Iso(DateTime local) => new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static Dictionary<string, DateTime>? ReadReminderMemory()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ReminderMemoryPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void SaveReminderMemory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReminderMemoryPath)!);
            File.WriteAllText(ReminderMemoryPath + ".tmp", JsonSerializer.Serialize(Reminders.Remembered));
            File.Move(ReminderMemoryPath + ".tmp", ReminderMemoryPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // --test-mode commands: reminder-clock:2026-10-28T12:05:00, reminder-idle:30, battery-percent:15 and
    // fullscreen:on (or off), each undone with the value real.
    private bool ReminderTestCommand(string command)
    {
        string? Value(string prefix) => command.StartsWith(prefix, StringComparison.Ordinal) ? command[prefix.Length..] : null;
        if (Value("reminder-clock:") is { } clock)
        {
            if (clock == "real") reminderClockShift = TimeSpan.Zero;
            else if (DateTime.TryParse(clock, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)) reminderClockShift = at - DateTime.Now;
            nextReminder = 0;
            // A test visiting another hour moves the clock on purpose: a gap in time, not on the steady clock.
            observedAt = 0;
            return true;
        }
        if (Value("reminder-idle:") is { } idle)
        {
            reminderIdleOverride = double.TryParse(idle, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
            return true;
        }
        if (Value("battery-percent:") is { } percent)
        {
            CompanionNative.TestBatteryOverride = int.TryParse(percent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : null;
            return true;
        }
        if (Value("fullscreen:") is { } full)
        {
            CompanionNative.TestFullscreenOverride = full switch { "on" => true, "off" => false, _ => null };
            return true;
        }
        return false;
    }

#if !COMPANION_SAFE
    // get_context: what the fish knows about the moment, without a screenshot or a network request.
    private object CurrentContext(IReadOnlyList<(string Title, string Folder)> folders)
    {
        var now = ReminderNow;
        double idle = ReminderIdle;
        var active = Reminders.ActiveFor(now);
        bool onAC = CompanionNative.OnAC;
        int? battery = CompanionNative.BatteryPercent;
        string weather = preferences.PublicInfo ? brain.CachedWeather : "";
        var handle = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
        return new
        {
            summary = CompanionReminders.Summary(now, idle, active, onAC, battery),
            localTime = Iso(now),
            weekday = CompanionReminders.Weekday(now),
            idleSeconds = Math.Round(idle),
            activeMinutes = active is { } stretch ? (int?)stretch.TotalMinutes : null,
            lastBreakMinutes = Reminders.LastBreak is { } pause ? (int?)Math.Round(pause.TotalMinutes) : null,
            fullscreen = CompanionNative.IsFullscreen(),
            power = new { onAC, batteryPercent = battery },
            foreground = preferences.ForegroundEnabled ? CompanionNative.ForegroundApp(handle) : null,
            weather = weather.Length > 0 ? weather : null,
            openFolders = folders.Select((folder, i) => new { folder = folder.Folder, window = folder.Title, front = i == 0 }).ToArray(),
            lastReminder = Reminders.Last is { } last ? new { kind = last.Reminder.Kind, text = last.Reminder.Text, at = Iso(last.At) } : null
        };
    }
#endif
}

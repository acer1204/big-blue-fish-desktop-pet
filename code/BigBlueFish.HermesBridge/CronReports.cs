using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace BigBlueFish.HermesBridge;

// One finished run of a Hermes cron job worth telling. Written is the file's UTC time, Ran the run time
// on Hermes' clock (from the file name), Answer null when the run failed.
internal sealed record CronReport(string Key, string Job, DateTime Written, DateTime Ran, string? Answer)
{
    internal string JobId => Key[..Key.IndexOf('/')];
    internal string Speech => Answer == null ? $"排程「{Job}」這次沒有成功。" : $"排程「{Job}」（{CronReports.Clock(Ran)}）：{Answer}";
}

// One entry of Hermes' cron/jobs.json, as /jobs tells it. State is Hermes' own word for it:
// scheduled, paused, completed or error.
internal sealed record CronJob(string Name, string Schedule, DateTimeOffset? Next, string State, string? LastStatus);

// Hermes saves every cron run as <HERMES_HOME>/cron/output/<job_id>/YYYY-MM-DD_HH-MM-SS.md, whatever the
// job's deliver says: written to a .output_*.tmp file first, then renamed. A job made from the pet's chat
// has nowhere else to deliver (the API server cannot push), so the bridge reads the new files itself and
// hands each report worth telling to Found. It only reads under HERMES_HOME; what was told is kept in the
// bridge's own state file.
internal sealed partial class CronReports(string hermesHome, string statePath)
{
    // After a night away only the last half day is news; older runs are never told.
    internal static readonly TimeSpan CatchUp = TimeSpan.FromHours(12);
    // FileSystemWatcher can miss events, and the folder appears only with the first job's first run.
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(60);
    private const string Title = "# Cron Job: ";
    private const string PromptHeading = "## Prompt\n\n";
    private readonly string folder = Path.Combine(hermesHome, "cron", "output");
    private readonly Channel<CronReport> found = Channel.CreateUnbounded<CronReport>(new UnboundedChannelOptions { SingleReader = true });
    // Paths the watcher saw; an empty one asks for a rescan with a new watcher.
    private readonly Channel<string> touched = Channel.CreateUnbounded<string>();
    private readonly object gate = new();
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);    // told, skipped or silent; persisted
    private readonly HashSet<string> waiting = new(StringComparer.OrdinalIgnoreCase); // handed to Found, not yet told
    // Job id -> the latest failed run of a streak the user heard about, and the run they heard; persisted.
    private readonly Dictionary<string, string> alerted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> heard = new(StringComparer.OrdinalIgnoreCase);
    // Hermes' own cron.failure_repeat_alert_hours default: a failure still going on is told again after this.
    private static readonly TimeSpan Remind = TimeSpan.FromHours(6);
    // Job id -> when its latest run with nothing to say was written: an earlier failure is no longer news.
    private readonly Dictionary<string, DateTime> quiet = new(StringComparer.OrdinalIgnoreCase);

    internal ChannelReader<CronReport> Found => found.Reader;

    internal async Task WatchAsync(CancellationToken token)
    {
        Load();
        FileSystemWatcher? watcher = null;
        var due = DateTime.MinValue;
        try
        {
            while (true)
            {
                if (watcher == null && Directory.Exists(folder) && Watch() is { } started) { watcher = started; due = DateTime.MinValue; }
                if (DateTime.UtcNow >= due)
                {
                    due = DateTime.UtcNow + Rescan;
                    Safely(Scan);
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(due - DateTime.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero);
                try { await touched.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
                // A Created event can come while a file is still being written; Hermes' rename never does.
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
                var paths = new List<string>();
                while (touched.Reader.TryRead(out var path))
                {
                    if (path.Length > 0) { paths.Add(path); continue; }
                    // Lost events, or the folder itself went away: start over from a full scan.
                    watcher?.Dispose();
                    watcher = null;
                    due = DateTime.MinValue;
                }
                Safely(() => Consider(paths));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { watcher?.Dispose(); }
    }

    // One run Hermes wrote in a way this does not expect must not end the watching for good.
    private static void Safely(Action work)
    {
        try { work(); }
        catch (Exception error) when (error is not OperationCanceledException) { Log.Write("讀排程結果時出錯：" + error.Message); }
    }

    private FileSystemWatcher? Watch()
    {
        try
        {
            var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName, InternalBufferSize = 64 * 1024 };
            watcher.Created += (_, e) => touched.Writer.TryWrite(e.FullPath);
            watcher.Renamed += (_, e) => touched.Writer.TryWrite(e.FullPath);
            watcher.Error += (_, _) => touched.Writer.TryWrite("");
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Write("無法監看排程結果資料夾，改為每分鐘檢查一次：" + error.Message);
            return null;
        }
    }

    private void Scan()
    {
        if (!Directory.Exists(folder)) return;
        try { Consider(Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).ToList()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("讀不到排程結果資料夾：" + error.Message);
        }
    }

    private void Consider(IEnumerable<string> paths)
    {
        bool changed = false;
        var news = new List<CronReport>();
        var runs = new List<(string Path, string Key)>();
        foreach (var path in paths) if (Key(path) is { } run) runs.Add((path, run));
        // Oldest first within a job (its file names are run times), so a failing streak is followed in order.
        runs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        foreach (var (path, key) in runs)
        {
            lock (gate) if (seen.Contains(key) || waiting.Contains(key)) continue;
            DateTime written;
            string text;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                written = info.LastWriteTimeUtc;
                // Old runs stay out by age alone, so they need no place in the state file.
                if (DateTime.UtcNow - written > CatchUp) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                text = reader.ReadToEnd();
            }
            // Pruned by Hermes in between, or still locked: the next scan tries again if it is still there.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            var report = Read(key, text, written);
            string job = key[..key.IndexOf('/')];
            if (report == null)
            {
                Log.Write($"排程結果沒有要說的內容（{key}）。");
                lock (gate)
                {
                    seen.Add(key);
                    if (!quiet.TryGetValue(job, out var since) || since < written) quiet[job] = written;
                }
                changed = true;
                continue;
            }
            if (report.Answer == null && Repeat(report))
            {
                changed = true;
                continue;
            }
            lock (gate) waiting.Add(key);
            Log.Write($"收到排程「{report.Job}」的結果（{key}）。");
            news.Add(report);
        }
        if (changed) Save();
        // Handed over together, so a failure is never told before a quiet run of the same scan that follows it.
        foreach (var report in news) found.Writer.TryWrite(report);
    }

    // job_id/YYYY-MM-DD_HH-MM-SS.md for a run document right inside a job's folder; null for anything else,
    // such as Hermes' .output_*.tmp files.
    private string? Key(string path)
    {
        string relative;
        try { relative = Path.GetRelativePath(folder, Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or IOException) { return null; }
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 2 || parts[0] is "" or "." or ".." || parts[1].StartsWith('.')
            || !parts[1].EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[0] + "/" + parts[1];
    }

    // Like Hermes' own alerts, a job that keeps failing is mentioned once, until a run in between works or
    // the failure the user heard is six hours old; a repeat is marked done without being told.
    private bool Repeat(CronReport report)
    {
        if (!ContinuesStreak(report.JobId, report.Key)) return false;
        Log.Write($"排程「{report.Job}」又沒有成功，之前已經提醒過，這次不重複說（{report.Key}）。");
        lock (gate)
        {
            waiting.Remove(report.Key);
            seen.Add(report.Key);
            alerted[report.JobId] = report.Key;
        }
        return true;
    }

    // The same test once more just before a failure is told: the failure before it may have been told
    // (or found in a reply) only after this one was found.
    internal bool Repeats(CronReport report)
    {
        if (!Repeat(report)) return false;
        Save();
        return true;
    }

    // Whether the run just before this one in the job's folder, however old, is the latest failure of a
    // streak the user heard about recently: then nothing worked in between. File names are run times, so
    // they sort in order.
    private bool ContinuesStreak(string job, string key)
    {
        string? last;
        lock (gate)
        {
            if (!alerted.TryGetValue(job, out last) || !heard.TryGetValue(job, out var told)) return false;
            if (RunTime(key) - RunTime(told) is not { } since || since >= Remind) return false;
        }
        string name = key[(key.IndexOf('/') + 1)..];
        try
        {
            string? previous = Directory.EnumerateFiles(Path.Combine(folder, job), "*.md")
                .Select(path => Path.GetFileName(path))
                .Where(file => !file.StartsWith('.') && StringComparer.OrdinalIgnoreCase.Compare(file, name) < 0)
                .Max(StringComparer.OrdinalIgnoreCase);
            return previous != null && string.Equals(job + "/" + previous, last, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    // On Hermes' clock, from the file name.
    private static DateTime? RunTime(string key) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(key[(key.IndexOf('/') + 1)..]), "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    // Told by the pet: never told again, not even after a restart. A failure told starts that job's streak.
    internal void Done(CronReport report)
    {
        lock (gate)
        {
            waiting.Remove(report.Key);
            seen.Add(report.Key);
            if (report.Answer == null && (!heard.TryGetValue(report.JobId, out var last) || StringComparer.OrdinalIgnoreCase.Compare(report.Key, last) > 0))
                heard[report.JobId] = alerted[report.JobId] = report.Key;
        }
        Save();
    }

    // Left out without being told (a newer run of its job, a reply that already covered it): never told
    // again, and no streak starts from it, so a mistaken match costs one report at most.
    internal void Skip(IReadOnlyCollection<CronReport> skipped)
    {
        lock (gate)
            foreach (var report in skipped)
            {
                waiting.Remove(report.Key);
                seen.Add(report.Key);
            }
        Save();
    }

    // When the job last ran with nothing to say, if it has since the bridge started.
    internal DateTime? QuietSince(string job)
    {
        lock (gate) return quiet.TryGetValue(job, out var since) ? since : null;
    }

    // Too old to be news by the time the pet could hear it; its age keeps it from coming back.
    internal void Drop(CronReport report)
    {
        lock (gate) waiting.Remove(report.Key);
    }

    private void Load()
    {
        try
        {
            var cron = BridgeState.Read(statePath)["cron"];
            lock (gate)
            {
                if (cron?["seen"] is JsonArray list)
                    foreach (var item in list)
                        if (item is JsonValue value && value.TryGetValue<string>(out var key)) seen.Add(key);
                ReadMap(cron?["alerted"], alerted);
                ReadMap(cron?["heard"], heard);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Write("讀不到排程回報的進度，最近 12 小時的回報可能會再說一次：" + error.Message);
        }
        static void ReadMap(JsonNode? node, Dictionary<string, string> into)
        {
            if (node is not JsonObject map) return;
            foreach (var (job, item) in map)
                if (item is JsonValue value && value.TryGetValue<string>(out var key)) into[job] = key;
        }
    }

    // Only runs young enough to be told need remembering; the file's age covers the rest. A job Hermes
    // removed takes its output folder along, and its failing streak with it.
    private void Save()
    {
        try
        {
            BridgeState.Update(statePath, root =>
            {
                // Taken under the file's own lock, so a save that lands later never writes an older picture.
                string[] keep;
                JsonObject tails, told;
                lock (gate)
                {
                    seen.RemoveWhere(key => !Young(key));
                    keep = seen.Order(StringComparer.Ordinal).ToArray();
                    tails = Map(alerted);
                    told = Map(heard);
                }
                root["cron"] = new JsonObject { ["seen"] = new JsonArray(keep.Select(key => (JsonNode?)key).ToArray()), ["alerted"] = tails, ["heard"] = told };
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write("無法儲存排程回報的進度，下次啟動可能會再說一次：" + error.Message);
        }
    }

    // Under gate: drops the jobs whose output folder is gone, then copies the rest in a stable order.
    private JsonObject Map(Dictionary<string, string> streaks)
    {
        foreach (var job in streaks.Keys.Where(job => !Directory.Exists(Path.Combine(folder, job))).ToList()) streaks.Remove(job);
        var map = new JsonObject();
        foreach (var (job, key) in streaks.OrderBy(pair => pair.Key, StringComparer.Ordinal)) map[job] = key;
        return map;
    }

    private bool Young(string key)
    {
        try
        {
            var info = new FileInfo(Path.Combine(folder, key));
            return info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc <= CatchUp;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    // The documents Hermes writes (cron/scheduler.py, scheduler_prompt.py): an agent run carries the
    // prompt and answer lengths, a failed one "(FAILED)" in its title and an ## Error section, a refused
    // one a **Status:** line or an Error: line, a script-only run its output after ---. Null when the run
    // has nothing to tell: a silent answer, a script that printed nothing, or not a run document.
    internal static CronReport? Read(string key, string document, DateTime written)
    {
        // Python wrote the file in text mode (CRLF on Windows) and counted the characters of its LF text.
        string text = document.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Trim().Length == 0) return null; // a pre-run script that printed nothing
        int slash = key.IndexOf('/');
        string id = key[..slash];
        var ran = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(key[(slash + 1)..]), "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp) ? stamp : written.ToLocalTime();
        int lineEnd = text.IndexOf('\n');
        string first = (lineEnd < 0 ? text : text[..lineEnd]).Trim();
        // A one-shot removed without running (missed, or cut off by a restart) leaves a note instead.
        if (first.StartsWith("# Cron job removed", StringComparison.OrdinalIgnoreCase))
            return new(key, NameLine().Match(text) is { Success: true } named && named.Groups[1].Value.Trim() is { Length: > 0 } name && name != "None" ? name : id,
                written, ran, null);
        if (!first.StartsWith(Title, StringComparison.Ordinal)) return null;
        string job = first[Title.Length..].Trim();
        bool failed = job.EndsWith(" (FAILED)", StringComparison.Ordinal);
        if (failed) job = job[..^" (FAILED)".Length].Trim();
        if (job.Length == 0) job = id;
        var failure = new CronReport(key, job, written, ran, null);
        if (failed) return failure;
        string? answer = FramedAnswer(text);
        if (answer == null)
        {
            // Everything before the first section: the **Key:** value lines, and a refusal's reason.
            var header = HeaderEnd().Match(text) is { Success: true } end ? text[..end.Index] : text;
            string status = StatusLine().Match(header) is { Success: true } line ? line.Groups[1].Value.Trim() : "";
            // A script or monitor that found nothing new, or a pre-run script that kept the agent asleep.
            if (status.StartsWith("silent", StringComparison.OrdinalIgnoreCase) || status.StartsWith("no_change", StringComparison.OrdinalIgnoreCase)
                || header.Contains("wakeAgent=false", StringComparison.Ordinal)) return null;
            if (status.Contains("blocked", StringComparison.OrdinalIgnoreCase) || status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || ErrorLine().IsMatch(header)) return failure;
            int rule = text.IndexOf("\n---\n", StringComparison.Ordinal);
            if (header.Contains("**Mode:** no_agent", StringComparison.Ordinal) && rule >= 0) answer = text[(rule + 5)..].Trim();
            // Older documents without lengths: the last heading is the writer's (the prompt may quote one).
            else if (text.LastIndexOf("## Response", StringComparison.Ordinal) is >= 0 and var at) answer = text[(at + "## Response".Length)..].Trim();
            else return failure;
        }
        // Hermes books an empty answer as a failed run (fail_empty_response), not as nothing to report.
        if (answer.Length == 0 || answer == "(No response generated)") return failure;
        if (IsSilent(answer)) return null;
        // The job's own way of saying it failed: the marker alone on the first line.
        if (answer.Split('\n')[0].TrimEnd() == "[CRON_FAILURE]") return failure;
        return new(key, job, written, ran, HermesChat.WithoutImages(answer));
    }

    // Mirrors Hermes' _archive_answer: skip the prompt by its stated length, then the answer must be
    // exactly as long as stated. Null when the lengths are missing or do not add up.
    private static string? FramedAnswer(string text)
    {
        var prompt = PromptFrame().Match(text);
        if (!prompt.Success || text.IndexOf(PromptHeading, StringComparison.Ordinal) != prompt.Index + prompt.Length - PromptHeading.Length
            || !long.TryParse(prompt.Groups[1].Value, CultureInfo.InvariantCulture, out long promptLength)) return null;
        int start = Advance(text, prompt.Index + prompt.Length, promptLength + 2); // the blank line after the prompt
        if (start < 0) return null;
        var response = ResponseFrame().Match(text, start);
        if (!response.Success || !long.TryParse(response.Groups[1].Value, CultureInfo.InvariantCulture, out long answerLength)) return null;
        var tail = text.AsSpan(response.Index + response.Length);
        if (CodePoints(tail) != answerLength + 1 || !tail.EndsWith("\n")) return null;
        return tail[..^1].ToString().Trim();
    }

    // Python counts characters as code points; one outside the BMP is two UTF-16 units here. -1 past the end.
    private static int Advance(string text, int index, long count)
    {
        for (; count > 0; count--)
        {
            if (index >= text.Length) return -1;
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        }
        return index;
    }

    private static long CodePoints(ReadOnlySpan<char> text)
    {
        long count = 0;
        for (int i = 0; i < text.Length; i++, count++)
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        return count;
    }

    // Hermes' own test for "nothing to report" (gateway/response_filters.py): a marker as the whole answer
    // or as its first or last line, ignoring case, spacing and stray punctuation round it, or an answer
    // that opens with a bracketed one. A marker in the middle of a sentence is real content.
    private static readonly string[] SilentMarkers = ["[SILENT]", "SILENT", "NO_REPLY", "NO REPLY", "[静默]", "静默", "[沉默]", "沉默"];
    private static readonly char[] LineBreaks = ['\n', '\r', '\v', '\f', '\x1c', '\x1d', '\x1e', '\x85', '\u2028', '\u2029'];

    internal static bool IsSilent(string answer)
    {
        string text = answer.Trim();
        if (text.Length == 0) return false;
        string upper = text.ToUpperInvariant();
        if (SilentMarkers.Any(marker => marker[0] == '[' && upper.StartsWith(marker, StringComparison.Ordinal))) return true;
        var lines = text.Split(LineBreaks).Where(line => line.Trim().Length > 0).ToArray();
        return IsMarker(text) || lines.Length > 0 && (IsMarker(lines[0]) || IsMarker(lines[^1]));
    }

    private static bool IsMarker(string candidate)
    {
        string text = candidate.Trim();
        if (CodePoints(text) is 0 or > 64) return false;
        return SilentMarkers.Contains(Canonical(text)) || SilentMarkers.Contains(Canonical(WithoutEdgePunctuation(text)));
    }

    private static string Canonical(string text) =>
        string.Join(' ', text.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Square brackets stay, so a broken "[SILENT" never turns into "SILENT".
    private static string WithoutEdgePunctuation(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        int start = 0, end = runes.Count;
        static bool Edge(Rune rune) => rune.Value is not ('[' or ']') && Rune.IsPunctuation(rune);
        while (start < end && Edge(runes[start])) start++;
        while (end > start && Edge(runes[end - 1])) end--;
        return string.Concat(runes.Skip(start).Take(end - start)).Trim();
    }

    // Asked to run a job now, Hermes runs it inside the turn and the model passes the result on, so the
    // saved run would be told twice. Compared on letters and digits, since the model may reword the
    // punctuation round it: the start of the answer; for a failed run (whose error the model was handed),
    // one sentence that names the job and says it failed, not that nothing failed or what happens if it does.
    internal static bool AlreadySaid(CronReport report, string reply)
    {
        if (report.Answer != null)
        {
            string head = string.Concat(LettersAndDigits(report.Answer).EnumerateRunes().Take(16));
            return head.Length > 0 && LettersAndDigits(reply).Contains(head, StringComparison.OrdinalIgnoreCase);
        }
        string name = LettersAndDigits(report.Job);
        return name.Length > 0 && SentenceBreak().Split(reply).Any(sentence =>
            LettersAndDigits(sentence).Contains(name, StringComparison.OrdinalIgnoreCase) && FailureWords().IsMatch(sentence) && !NotAFailure().IsMatch(sentence));
    }

    private static string LettersAndDigits(string text) => string.Concat(text.EnumerateRunes().Where(Rune.IsLetterOrDigit));

    // 08:00 today, 10/28 08:00 on any other day.
    internal static string Clock(DateTime local) =>
        local.ToString(local.Date == DateTime.Today ? "HH:mm" : "M/d HH:mm", CultureInfo.InvariantCulture);

    internal static bool IsJobsCommand(string text) => text.Equals("/jobs", StringComparison.OrdinalIgnoreCase) || text == "/排程";

    internal static int ActiveJobs(string hermesHome) => ReadJobs(hermesHome)?.Count(job => job.State == "scheduled") ?? 0;

    // What /jobs says, straight from jobs.json, without asking the model.
    internal static string DescribeJobs(string hermesHome)
    {
        var jobs = ReadJobs(hermesHome);
        if (jobs == null) return "讀不到 Hermes 的排程清單（cron/jobs.json），請稍後再試。";
        if (jobs.Count == 0) return "目前沒有排程。";
        var text = new StringBuilder($"目前有 {jobs.Count} 個排程：");
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            text.Append('\n').Append(i + 1).Append(". ").Append(job.Name).Append('：').Append(Schedule(job.Schedule)).Append('，').Append(job.State switch
            {
                "scheduled" => "啟用中", "paused" => "已暫停", "completed" => "已結束", "error" => "出錯停住了", _ => job.State
            });
            if (job.State == "scheduled" && job.Next is { } next) text.Append("，下次 ").Append(Clock(next.LocalDateTime));
            text.Append('，').Append(job.LastStatus switch
            {
                null or "" => "還沒執行過", "ok" => "上次成功", "error" => "上次失敗",
                "delivery_failed" => "上次有執行但沒送出", "blocked_config" => "上次因設定問題沒有執行", var other => "上次狀態：" + other
            });
        }
        return text.Append("\n要新增、暫停或取消，直接跟魚魚說就好。").ToString();
    }

    // Read-only and shared with Hermes, which replaces the file whole under its own lock. Null when it is
    // there but cannot be read or understood; empty when there are no jobs yet.
    internal static List<CronJob>? ReadJobs(string hermesHome)
    {
        string file = Path.Combine(hermesHome, "cron", "jobs.json");
        string json;
        try
        {
            if (!File.Exists(file)) return [];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            json = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // {"jobs": [...]} as Hermes writes it; a bare list or an id-keyed map, which Hermes also accepts.
            var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jobs", out var jobs) ? jobs : root;
            var entries = new List<(string? Id, JsonElement Job)>();
            if (list.ValueKind == JsonValueKind.Array) entries.AddRange(list.EnumerateArray().Select(job => (Text(job, "id"), job)));
            else if (list.ValueKind == JsonValueKind.Object) entries.AddRange(list.EnumerateObject().Select(job => ((string?)(Text(job.Value, "id") ?? job.Name), job.Value)));
            var result = new List<CronJob>();
            foreach (var (id, job) in entries)
            {
                if (job.ValueKind != JsonValueKind.Object) continue;
                string name = Text(job, "name") is { Length: > 0 } given ? given : id ?? "（沒有名稱）";
                string schedule = Text(job, "schedule_display")
                    ?? (job.TryGetProperty("schedule", out var parsed) && parsed.ValueKind == JsonValueKind.Object ? Text(parsed, "display") ?? Text(parsed, "expr") : null)
                    ?? "";
                DateTimeOffset? next = Text(job, "next_run_at") is { } at
                    && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when) ? when : null;
                result.Add(new(name, schedule, next, StateOf(job), Text(job, "last_status")));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    // Mirrors Hermes' effective_job_state: enabled decides, except for the terminal states.
    private static string StateOf(JsonElement job)
    {
        string stored = Text(job, "state")?.Trim() ?? "";
        if (stored is "completed" or "error") return stored;
        bool enabled = !job.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
        return enabled ? "scheduled" : "paused";
    }

    // Hermes keeps what was asked for ("every day at 8am", "0 8 * * *"); its own interval and one-shot
    // wordings read better in Chinese.
    private static string Schedule(string display)
    {
        var every = EveryMinutes().Match(display);
        if (every.Success && int.TryParse(every.Groups[1].Value, CultureInfo.InvariantCulture, out int minutes) && minutes > 0)
            return minutes % 1440 == 0 ? Every(minutes / 1440, "天") : minutes % 60 == 0 ? Every(minutes / 60, "小時") : $"每 {minutes} 分鐘";
        if (display.StartsWith("once", StringComparison.OrdinalIgnoreCase)) return "單次";
        return display.Length > 0 ? display : "（時間未知）";
        static string Every(int count, string unit) => count == 1 ? "每" + unit : $"每 {count} {unit}";
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"(?m)^\*\*Prompt Characters:\*\* (\d+)\n## Prompt\n\n")]
    private static partial Regex PromptFrame();

    [GeneratedRegex(@"\G\*\*Response Characters:\*\* (\d+)\n## Response\n\n")]
    private static partial Regex ResponseFrame();

    [GeneratedRegex(@"(?m)^(?:## |---$)")]
    private static partial Regex HeaderEnd();

    [GeneratedRegex(@"(?m)^\*\*Status:\*\* (.*)$")]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"(?m)^Error: ")]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"(?m)^- name: (.*)$")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^every (\d+)m$", RegexOptions.IgnoreCase)]
    private static partial Regex EveryMinutes();

    [GeneratedRegex(@"[。！？!?；;\n]")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex("失敗|沒有成功|沒成功|不成功|未成功|錯誤|出錯|故障|無法|沒辦法|問題|error|fail|unable|couldn't|could not", RegexOptions.IgnoreCase)]
    private static partial Regex FailureWords();

    // "沒有任何錯誤", "no errors", "如果失敗會通知你": a failure word that is not a failure.
    [GeneratedRegex(@"(沒有?|並無|毫無)(任何)?(錯誤|失敗|問題|狀況)|\bno (errors?|problems?|issues?)\b|\bwithout (errors?|problems?)\b|如果|假如|萬一|若|\bif\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAFailure();
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

// Taiwan edition speech-bubble filter: whatever the fish is about to show, Simplified-only characters
// become Taiwan Traditional, while text that is already Traditional Chinese or English comes back
// unchanged. Data: OpenCC 1.4.2 dictionaries in assets/opencc (Apache-2.0, licenses/OpenCC-Apache-2.0.txt).
//  - No trigger (Simplified-only) or practically-Simplified character: unchanged.
//  - Triggers and no Traditional-only character: plain OpenCC s2twp of the whole text.
//  - Otherwise each run of CJK ideographs separately; a run mixing both scripts changes only its
//    Simplified positions, with STPhrases matches deciding the right Traditional character.
// Positions are code points, so Ext-B ideographs and emoji are never split.
internal sealed class CompanionTraditional
{
    // Ambiguous characters (valid Traditional too) that OpenCC keeps unchanged in at most 12% of its
    // phrases; they change only on phrase evidence (以后, 价格, 确定). 台, 雇 and 厘 stay out: Taiwan uses them.
    private const string PracticallySimplified = "亘挂极蚝蝎万厂据虫种胜柜叶适党价蜡愿广帘确几云丰后腊淀苹";
    // Not an STCharacters key, but TWVariants writes it as 著.
    private const string ExtraTriggers = "着";
    // Fork fixes for OpenCC 1.4.2 data that reads wrong in Taiwan: maximum matching took 一起干 (幹)
    // before 干杯, and TWPhrases' 的士 (taxi) turned 的士兵, 的士氣 and 的士林 into 計程車.
    private static readonly Dictionary<string, string[]> PhraseFixes = new(StringComparer.Ordinal) { ["一起干杯"] = ["一起乾杯"] };
    private const string DroppedTaiwanPhrase = "的士";
    private const string ResourcePrefix = "BigBlueFish.Windows.assets.opencc.";
    private static readonly Lazy<CompanionTraditional?> shared = new(Load);

    private readonly Table stCharacters, stPhrases;
    private readonly Table[] twGroup;
    private readonly int twLength;
    private readonly HashSet<int> trigger = new(), practicallySimplified = new(), ambiguous = new(), traditionalOnly = new();
    private readonly Dictionary<int, int> toSimplified = new();

    internal static string Convert(string text) =>
        string.IsNullOrEmpty(text) || shared.Value is not { } filter ? text : filter.Apply(text);

    // For a streamed snapshot: when the text needs converting, its trailing run of ideographs may end in
    // the middle of a phrase (打扫干|净 would flash 幹), so that run waits for the next snapshot.
    internal static string ConvertPartial(string text)
    {
        if (string.IsNullOrEmpty(text) || shared.Value is not { } filter) return text;
        var points = new Points(text);
        if (!filter.Candidates(points, out _, out _)) return text;
        int end = points.Count;
        while (end > 0 && Ideograph(points.At(end - 1))) end--;
        return filter.Apply(points.Slice(0, end));
    }

    // Parses the 1 MB phrase table off the UI thread before the first bubble needs it.
    internal static void Warm() => Task.Run(() => shared.Value);

    private static CompanionTraditional? Load()
    {
        try { return new CompanionTraditional(name => typeof(CompanionTraditional).Assembly.GetManifestResourceStream(ResourcePrefix + name)); }
        // A broken build must not take the speech bubble down; the text is then shown as given.
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    internal CompanionTraditional(Func<string, Stream?> open)
    {
        var characters = Read(open, "STCharacters.txt");
        stCharacters = new Table(characters);
        stPhrases = new Table(PhraseFixes);
        stPhrases.AddMissing(Read(open, "STPhrases.txt"));
        // OpenCC segments with the union of both; STPhrases wins on equal keys.
        stPhrases.AddMissing(Read(open, "STPhrases_GeneratedFromRegionalPhrases.txt"));
        var taiwanPhrases = new Table(Read(open, "TWPhrases.txt"));
        taiwanPhrases.Remove(DroppedTaiwanPhrase);
        twGroup = [taiwanPhrases, new Table(Read(open, "TWVariantsPhrases.txt")), new Table(Read(open, "TWVariants.txt"))];
        twLength = twGroup.Max(table => table.Length);

        foreach (var (key, candidates) in characters)
        {
            if (Single(key) is not { } point) continue;
            if (candidates.Contains(key)) ambiguous.Add(point);
            // Simplified-only, except the few that the Taiwan variants turn back into themselves (峰, 床, 群...).
            else if (Tw(candidates[0], null) != key) trigger.Add(point);
        }
        foreach (var rune in ExtraTriggers.EnumerateRunes()) trigger.Add(rune.Value);
        foreach (var rune in PracticallySimplified.EnumerateRunes())
            if (ambiguous.Contains(rune.Value)) practicallySimplified.Add(rune.Value);
        foreach (var (key, candidates) in Read(open, "TSCharacters.txt"))
            if (Single(key) is { } point && !candidates.Contains(key) && !characters.ContainsKey(key))
            {
                traditionalOnly.Add(point);
                if (Single(candidates[0]) is { } simplified) toSimplified[point] = simplified;
            }
    }

    internal string Apply(string text)
    {
        var all = new Points(text);
        if (!Candidates(all, out bool triggers, out bool traditional)) return text;
        if (triggers && !traditional) return S2twp(all);
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < all.Count;)
        {
            if (!Ideograph(all.At(i))) { result.Append(all.Span(i, 1)); i++; continue; }
            int start = i;
            while (i < all.Count && Ideograph(all.At(i))) i++;
            var run = new Points(all.Slice(start, i - start));
            if (!Candidates(run, out bool runTriggers, out bool runTraditional)) result.Append(run.Text);
            else if (runTriggers && !runTraditional) result.Append(S2twp(run));
            else result.Append(Guided(run));
        }
        return result.ToString();
    }

    // Whether the text has anything that may need converting (a trigger or practically-Simplified
    // character), and whether it has Simplified-only and Traditional-only characters.
    private bool Candidates(Points text, out bool triggers, out bool traditional)
    {
        bool any = false;
        triggers = traditional = false;
        for (int i = 0; i < text.Count; i++)
        {
            int point = text.At(i);
            if (trigger.Contains(point)) triggers = any = true;
            else if (practicallySimplified.Contains(point)) any = true;
            if (traditionalOnly.Contains(point)) traditional = true;
        }
        return any;
    }

    // OpenCC 1.4.2 s2twp without its compatibility-ideograph normalisation: segment by STPhrases,
    // convert each segment with STPhrases then STCharacters, then the Taiwan group per segment.
    private string S2twp(Points text)
    {
        var result = new StringBuilder(text.Text.Length);
        foreach (var (start, end, _) in Segments(text))
            result.Append(Tw(St(new Points(text.Slice(start, end - start))), null));
        return result.ToString();
    }

    // A run mixing Traditional-only and Simplified characters: phrases are matched on a copy whose
    // Traditional-only characters read as Simplified, but only Simplified positions are rewritten.
    private string Guided(Points run)
    {
        var copy = new StringBuilder(run.Text.Length);
        for (int k = 0; k < run.Count; k++)
            if (toSimplified.TryGetValue(run.At(k), out int simplified)) copy.Append(char.ConvertFromUtf32(simplified));
            else copy.Append(run.Span(k, 1));
        var matched = new Points(copy.ToString());
        var result = new StringBuilder(run.Text.Length);
        foreach (var (start, end, phrase) in Segments(matched))
        {
            var piece = new StringBuilder();
            var changed = new List<bool>();
            string? value = null;
            if (phrase) stPhrases.TryGet(matched.Span(start, end - start), out value);
            var target = value == null ? default : new Points(value);
            if (value != null && target.Count == end - start)
            {
                bool evidence = false;
                for (int k = 0; k < target.Count; k++)
                {
                    int point = run.At(start + k);
                    evidence |= trigger.Contains(point) || practicallySimplified.Contains(point) && target.At(k) != point;
                }
                for (int k = 0; k < target.Count; k++)
                {
                    int point = run.At(start + k);
                    bool change = evidence && (trigger.Contains(point) || ambiguous.Contains(point) && target.At(k) != point);
                    Append(piece, changed, change ? target.Span(k, 1) : run.Span(start + k, 1), change);
                }
            }
            else
                for (int k = start; k < end; k++)
                {
                    if (!trigger.Contains(run.At(k))) { Append(piece, changed, run.Span(k, 1), false); continue; }
                    string? candidate = stCharacters.TryGet(run.Span(k, 1), out var found) ? found : null;
                    Append(piece, changed, candidate ?? run.Span(k, 1).ToString(), true);
                }
            result.Append(changed.Contains(true) ? Tw(piece.ToString(), changed) : piece.ToString());
        }
        return result.ToString();
    }

    // One flag per code point of the appended text, so the Taiwan pass knows what was rewritten.
    private static void Append(StringBuilder piece, List<bool> changed, ReadOnlySpan<char> text, bool change)
    {
        piece.Append(text);
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text[i], i + 1 < text.Length ? text[i + 1] : '\0') ? 2 : 1)
            changed.Add(change);
    }

    // OpenCC MaxMatchSegmentation: phrase matches, with unmatched characters gathered into one segment.
    private List<(int Start, int End, bool Phrase)> Segments(Points text)
    {
        var segments = new List<(int, int, bool)>();
        int pending = -1;
        for (int i = 0; i < text.Count;)
        {
            int length = Prefix(text, i, stPhrases, stPhrases.Length, out _);
            if (length == 0) { if (pending < 0) pending = i; i++; continue; }
            if (pending >= 0) { segments.Add((pending, i, false)); pending = -1; }
            segments.Add((i, i + length, true));
            i += length;
        }
        if (pending >= 0) segments.Add((pending, text.Count, false));
        return segments;
    }

    private string St(Points segment)
    {
        var result = new StringBuilder(segment.Text.Length);
        for (int i = 0; i < segment.Count;)
        {
            int length = Prefix(segment, i, stPhrases, stPhrases.Length, out var value);
            if (length == 0) length = Prefix(segment, i, stCharacters, 1, out value);
            if (length == 0) { result.Append(segment.Span(i, 1)); i++; }
            else { result.Append(value); i += length; }
        }
        return result.ToString();
    }

    // The Taiwan group (TWPhrases, TWVariantsPhrases, TWVariants; the first table that matches wins).
    // With `changed`, a match applies only if it covers a rewritten position; null applies every match.
    private string Tw(string text, List<bool>? changed)
    {
        var segment = new Points(text);
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < segment.Count;)
        {
            int length = 0;
            string? value = null;
            foreach (var table in twGroup)
                if ((length = Prefix(segment, i, table, twLength, out value)) > 0) break;
            if (length == 0) { result.Append(segment.Span(i, 1)); i++; continue; }
            bool apply = changed == null;
            for (int k = i; !apply && k < i + length && k < changed!.Count; k++) apply = changed[k];
            if (apply) result.Append(value); else result.Append(segment.Span(i, length));
            i += length;
        }
        return result.ToString();
    }

    private static int Prefix(Points text, int start, Table table, int longest, out string? value)
    {
        for (int length = Math.Min(longest, text.Count - start); length > 0; length--)
            if (table.TryGet(text.Span(start, length), out value)) return length;
        value = null;
        return 0;
    }

    private static bool Ideograph(int point) =>
        point is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF;

    private static int? Single(string text)
    {
        var points = new Points(text);
        return points.Count == 1 ? points.At(0) : null;
    }

    // OpenCC text dictionaries: "key<TAB>candidate candidate…" per line, '#' comments and blank lines skipped.
    // A later line for the same key replaces the earlier one.
    private static Dictionary<string, string[]> Read(Func<string, Stream?> open, string name)
    {
        using var stream = open(name) ?? throw new FileNotFoundException("缺少 OpenCC 字典：" + name);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var entries = new Dictionary<string, string[]>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1) throw new InvalidDataException($"{name} 格式錯誤：{line}");
            entries[line[..tab]] = line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return entries;
    }

    // Keys to their first candidate, looked up by span without allocating.
    private sealed class Table
    {
        private readonly Dictionary<string, string> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup;

        internal Table(Dictionary<string, string[]> source)
        {
            lookup = entries.GetAlternateLookup<ReadOnlySpan<char>>();
            AddMissing(source);
        }

        internal int Length { get; private set; } // longest key, in code points

        internal void AddMissing(Dictionary<string, string[]> source)
        {
            foreach (var (key, candidates) in source)
                if (candidates.Length > 0 && entries.TryAdd(key, candidates[0]))
                    Length = Math.Max(Length, new Points(key).Count);
        }

        internal void Remove(string key) => entries.Remove(key);

        internal bool TryGet(ReadOnlySpan<char> key, out string? value)
        {
            bool found = lookup.TryGetValue(key, out var hit);
            value = hit;
            return found;
        }
    }

    // A string indexed by code point; a lone surrogate counts as one position.
    private readonly struct Points
    {
        private readonly int[] starts;

        internal Points(string text)
        {
            Text = text;
            var list = new List<int>(text.Length + 1);
            for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1) list.Add(i);
            list.Add(text.Length);
            starts = list.ToArray();
        }

        internal string Text { get; }
        internal int Count => starts.Length - 1;
        internal ReadOnlySpan<char> Span(int start, int count) => Text.AsSpan(starts[start], starts[start + count] - starts[start]);
        internal string Slice(int start, int count) => Span(start, count).ToString();
        internal int At(int index) => starts[index + 1] - starts[index] == 2
            ? char.ConvertToUtf32(Text[starts[index]], Text[starts[index] + 1]) : Text[starts[index]];
    }
}

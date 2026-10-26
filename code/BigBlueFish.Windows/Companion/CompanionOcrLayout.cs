// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BigBlueFish.Windows;

internal sealed record CompanionOcrWord(string Text, double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double CenterY => Y + Height / 2;
}

// Retain screen evidence instead of classifying the activity in application code.
// Join words into rows, split distant columns, then order each region top-to-bottom.
internal static class CompanionOcrLayout
{
    private sealed record Segment(string Text, double X, double Y, double Right, double Height);
    internal static string Format(IReadOnlyList<CompanionOcrWord> words, int width, int height)
    {
        if (words.Count == 0)
            return "螢幕文字：目前畫面沒有辨識到文字。";
        var rows = new List<List<CompanionOcrWord>>();
        foreach (var word in words.Where(x => !string.IsNullOrWhiteSpace(x.Text) && x.Height > 0)
            .OrderBy(x => x.CenterY).ThenBy(x => x.X))
        {
            // Compare vertical centers and heights, not OCR's potentially interleaved line order.
            var row = rows.LastOrDefault(x => Math.Abs(x.Average(w => w.CenterY) - word.CenterY)
                <= Math.Min(x.Average(w => w.Height), word.Height) * .48);
            if (row == null) rows.Add(new List<CompanionOcrWord> { word });
            else row.Add(word);
        }
        var segments = new List<Segment>();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(x => x.X).ToArray();
            var group = new List<CompanionOcrWord>();
            foreach (var word in ordered)
            {
                if (group.Count > 0 && word.X - group[^1].Right > Math.Max(40, word.Height * 2.3))
                {
                    segments.Add(ToSegment(group));
                    group.Clear();
                }
                group.Add(word);
            }
            if (group.Count > 0) segments.Add(ToSegment(group));
        }

        var regions = new List<Segment>[] { new(), new(), new(), new(), new() };
        foreach (var s in segments)
        {
            var region = s.Y < height * .12 ? 0 : s.Y >= height * .89 ? 4
                : s.Right < width * .27 ? 1 : s.X > width * .80 ? 3 : 2;
            regions[region].Add(s);
        }
        string[] names = { "畫面上部", "畫面左側", "畫面主體", "畫面右側", "畫面底部" };
        int[] budgets = { 700, 650, 3000, 500, 650 };
        var output = new StringBuilder("目前前臺視窗的螢幕文字（按區域分別從上到下排列）：\n");
        for (int i = 0; i < regions.Length; i++)
        {
            var readable = regions[i].Where(s => s.Text.Any(char.IsLetterOrDigit)).ToArray();
            if (readable.Length == 0) continue;
            output.Append('【').Append(names[i]).AppendLine("】");
            var text = new StringBuilder();
            Segment? previous = null;
            foreach (var s in readable.OrderBy(x => x.Y).ThenBy(x => x.X))
            {
                if (previous != null && s.Y - (previous.Y + previous.Height) > Math.Max(14, s.Height * .9))
                    text.AppendLine();
                text.AppendLine(s.Text);
                previous = s;
            }
            output.AppendLine(Bound(text.ToString().Trim(), budgets[i]));
        }
        return output.ToString().Trim();
    }

    private static Segment ToSegment(List<CompanionOcrWord> group)
    {
        var text = new StringBuilder();
        foreach (var w in group)
        {
            var word = string.Join(" ", w.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (word.Length == 0) continue;
            // Chinese OCR emits individual characters: keep Chinese continuous, but preserve Latin word spaces.
            if (text.Length > 0 && !IsCjk(text[^1]) && !IsCjk(word[0])) text.Append(' ');
            text.Append(word);
        }
        return new Segment(text.ToString(), group.Min(x => x.X), group.Min(x => x.Y),
            group.Max(x => x.Right), group.Max(x => x.Height));
    }

    private static bool IsCjk(char c) => c is >= '\u2E80' and <= '\u9FFF' or >= '\uFF00' and <= '\uFFEF';

    private static string Bound(string text, int budget)
    {
        if (text.Length <= budget) return text;
        // Preserve the start (document identity) and end (recent conversation / current task).
        int start = budget * 2 / 5;
        int end = budget - start;
        int prefix = text.LastIndexOf('\n', Math.Min(start, text.Length - 1));
        int suffix = text.IndexOf('\n', text.Length - end);
        if (prefix < 0) prefix = start;
        if (suffix < 0) suffix = text.Length - end;
        return text[..prefix] + "\n…（中間較長內容省略）…\n" + text[suffix..].TrimStart();
    }
}

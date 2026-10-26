// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BigBlueFish.Windows;

internal static class CompanionNews
{
    internal const double Probability = .03;
    internal static bool ShouldSelect(double roll) => roll >= 0 && roll < Probability;

    internal sealed record Item(string Title, string Summary, DateTimeOffset Published)
    {
        internal string Context => $"來源：IT之家\n釋出時間：{Published.ToLocalTime():yyyy-MM-dd HH:mm}\n標題：{Title}\n新聞摘要：{Summary}";
    }

    internal static Item[] ReadFeed(string xml, DateTimeOffset now)
    {
        return XDocument.Parse(xml).Descendants("item")
            .Where(i => DateTimeOffset.TryParse((string?)i.Element("pubDate"), out var date)
                && date <= now && now - date < TimeSpan.FromDays(2))
            .Select(i => new Item(PlainText((string?)i.Element("title"), 240),
                PlainText((string?)i.Element("description"), 1600),
                DateTimeOffset.Parse((string)i.Element("pubDate")!)))
            .Where(i => i.Title.Length > 0)
            .DistinctBy(i => i.Title).Take(30).ToArray();
    }

    private static string PlainText(string? html, int limit)
    {
        var text = Regex.Replace(html ?? "", @"<\s*(script|style)\b[^>]*>[\s\S]*?</\s*\1\s*>", " ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]*>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= limit ? text : text[..limit];
    }
}

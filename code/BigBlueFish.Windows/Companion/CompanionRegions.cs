// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BigBlueFish.Windows;

internal sealed record CompanionRegion(string Code, string ParentCode, int Level, string Name, double? Latitude, double? Longitude);

internal static class CompanionRegions
{
    internal const string DefaultCode = "340104";
    // Taiwan townships use CWA township forecasts; codes are TW-<F-D0047 dataset>-<index>.
    internal const string TaiwanRoot = "TW", TaiwanDefaultCode = "TW-061-01";
    internal static readonly IReadOnlyList<CompanionRegion> All = [.. Load("china-regions.json"), .. Load("taiwan-regions.json")];
    private static readonly Dictionary<string, CompanionRegion> ByCode = All.ToDictionary(x => x.Code);

    private static CompanionRegion[] Load(string file)
    {
        using var stream = typeof(CompanionRegions).Assembly.GetManifestResourceStream(
            "BigBlueFish.Windows.assets.regions." + file)
            ?? throw new InvalidOperationException("缺少省市區資料");
        return JsonSerializer.Deserialize<CompanionRegion[]>(stream) ?? throw new InvalidOperationException("省市區資料為空");
    }

    internal static bool IsTaiwan(CompanionRegion region) => region.Code.StartsWith(TaiwanRoot + "-", StringComparison.Ordinal);
    internal static string CwaDataset(CompanionRegion district) => "F-D0047-" + district.ParentCode[3..];

    internal static CompanionRegion District(string? code) =>
        code != null && ByCode.TryGetValue(code, out var region) && region.Level == 2 ? region : ByCode[DefaultCode];

    internal static CompanionRegion Parent(CompanionRegion region) => ByCode[region.ParentCode];
    internal static CompanionRegion[] Children(string code) => All.Where(x => x.ParentCode == code).ToArray();
    internal static string FullName(CompanionRegion district) => string.Join(" · ",
        new[] { Parent(Parent(district)).Name, Parent(district).Name, district.Name }.Distinct());
}

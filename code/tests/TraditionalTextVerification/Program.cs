// Speech-bubble Traditional filter (CompanionTraditional) against the corpora in corpora/: each X.txt holds one
// sample per line and X.expected.txt the output of the Python reference implementation used to choose the
// algorithm (OpenCC 1.4.2 data). traditional_* and tw_regions_traditional must come back unchanged.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BigBlueFish.Windows;

Console.OutputEncoding = Encoding.UTF8;
int failures = 0;
void Check(bool condition, string name)
{
    if (condition) return;
    failures++;
    Console.WriteLine("FAIL " + name);
}

var clock = Stopwatch.StartNew();
Check(CompanionTraditional.Convert("说") == "說", "dictionaries load from embedded resources");
Console.WriteLine($"load {clock.ElapsedMilliseconds} ms");

string directory = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "corpora");
string[] Lines(string path)
{
    var text = File.ReadAllText(path, new UTF8Encoding(false));
    if (text.EndsWith('\n')) text = text[..^1];
    return text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
}
int samples = 0;
foreach (var input in Directory.GetFiles(directory, "*.txt").Where(path => !path.EndsWith(".expected.txt")).Order())
{
    string name = Path.GetFileNameWithoutExtension(input);
    var sources = Lines(input);
    var expected = Lines(Path.ChangeExtension(input, null) + ".expected.txt");
    Check(sources.Length == expected.Length, name + " has one expected line per sample");
    int wrong = 0, unstable = 0;
    clock.Restart();
    for (int i = 0; i < Math.Min(sources.Length, expected.Length); i++)
    {
        var actual = CompanionTraditional.Convert(sources[i]);
        if (actual != expected[i] && ++wrong <= 5)
            Console.WriteLine($"  {name}:{i + 1}\n    in:       {sources[i]}\n    expected: {expected[i]}\n    actual:   {actual}");
        if (CompanionTraditional.Convert(actual) != actual) unstable++;
    }
    samples += sources.Length;
    Console.WriteLine($"{name}: {sources.Length} samples, {wrong} mismatches, {unstable} not idempotent, {clock.ElapsedMilliseconds} ms");
    Check(wrong == 0, name + " matches the reference");
    Check(unstable == 0, name + " output is stable when converted again");
    // traditional_app quotes Simplified action ids and font names, and traditional_stress keeps the one known
    // trade-off (選后 → 選後, the price of fixing 以后/然后/之后); the region sentences are pure Traditional.
    if (name == "tw_regions_traditional")
        Check(sources.SequenceEqual(expected), name + " expectation is the unchanged input");
}

// Text the fish really shows: a Hermes reply with one Simplified slip, the MCP verification string,
// the bridge's progress line, and inputs that must pass through untouched.
Check(CompanionTraditional.Convert("搞定了！新的空白文件已經开好，文章也寫完並排好版了。") == "搞定了！新的空白文件已經開好，文章也寫完並排好版了。",
    "a Simplified slip changes alone; the Traditional word 文件 stays");
const string raw = "主人，MCP傳來的原文：新衣服、標點 & <>，都應原樣顯示~";
Check(CompanionTraditional.Convert(raw) == raw, "MCP verification text unchanged");
const string progress = "正在上網搜尋：虎尾鎮 今天 天氣…（1 分 05 秒）";
Check(CompanionTraditional.Convert(progress) == progress, "bridge progress text unchanged");
Check(CompanionTraditional.Convert("我已經讓魚魚跳了「轻快摇摆舞」") == "我已經讓魚魚跳了「輕快搖擺舞」", "quoted Simplified action name converted");
// Fork fixes for OpenCC data that reads wrong in Taiwan.
Check(CompanionTraditional.Convert("我们的士兵很勇敢") == "我們的士兵很勇敢", "的士兵 is not a taxi");
Check(!CompanionTraditional.Convert("你们去过台北的士林夜市吗？").Contains("計程車"), "士林 survives after 的");
Check(CompanionTraditional.Convert("主人，我们一起干杯吧！") == "主人，我們一起乾杯吧！", "一起干杯 toasts with 乾");
// Streamed snapshots that need converting hold back their unfinished last run of Chinese.
Check(CompanionTraditional.ConvertPartial("好的，我把房间打扫干") == "好的，", "unfinished Simplified run waits");
Check(CompanionTraditional.ConvertPartial("我把房间打扫干") == "", "a single unfinished run shows nothing yet");
Check(CompanionTraditional.ConvertPartial("主人，今天天氣很好") == "主人，今天天氣很好", "Traditional snapshots stream unchanged");
Check(CompanionTraditional.ConvertPartial("我们走吧。") == "我們走吧。", "finished Simplified snapshot converts fully");
foreach (var same in new[] { "", "Hello, world! 123", "\uD800 lone high surrogate", "lone low \uDC00", "𠮷野家👨‍👩‍👧", "台灣、干擾、皇后、公里、面對、一台電腦" })
    Check(CompanionTraditional.Convert(same) == same, "unchanged: " + same);

// A full-length say (4000 UTF-16 units) must not stall the UI thread.
var longText = string.Concat(Enumerable.Repeat("今天天气很好，我們一起去公园散步吧！Today is nice. ", 160))[..4000];
clock.Restart();
for (int i = 0; i < 20; i++) CompanionTraditional.Convert(longText);
double each = clock.Elapsed.TotalMilliseconds / 20;
Console.WriteLine($"4000-unit text: {each:0.0} ms per conversion");
Check(each < 50, "4000-unit conversion under 50 ms");

Console.WriteLine(failures == 0 ? $"RESULT OK {samples} corpus samples" : $"RESULT {failures} FAILURES");
return failures == 0 ? 0 : 1;

namespace BigBlueFish.HermesBridge;

// What the speech bubble says while Hermes works: a short phrase per tool, plus the tool's own
// one-line preview (search words, address, file name) where that helps.
internal static class ToolStatus
{
    private const int PreviewLimit = 30;

    // Null for calls not worth showing, such as Hermes driving the pet itself.
    internal static string? Describe(string tool, string? preview)
    {
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
            return tool.Contains("big_blue_fish", StringComparison.OrdinalIgnoreCase)
                || tool.Contains("big-blue-fish", StringComparison.OrdinalIgnoreCase) ? null : "正在使用外掛工具";
        string? detail = Short(preview);
        return tool switch
        {
            "web_search" => With("正在上網搜尋", detail),
            "web_extract" => "正在閱讀網頁",
            _ when tool.StartsWith("browser", StringComparison.Ordinal) => "正在操作瀏覽器",
            "read_file" => With("正在讀取檔案", FileName(preview)),
            "search_files" => With("正在搜尋檔案", detail),
            "write_file" or "patch" => With("正在寫入檔案", FileName(preview)),
            "terminal" or "process" or "execute_code" => "正在電腦上執行指令",
            "vision_analyze" => "正在看圖片",
            "image_generate" => "正在畫圖",
            "delegate_task" => "正在請小幫手分頭處理",
            "session_search" => "正在翻找以前的對話",
            "memory" => "正在整理記憶",
            "clarify" => "正在確認問題",
            "send_message" => "正在傳送訊息",
            _ when tool.StartsWith("cronjob", StringComparison.Ordinal) => "正在設定排程",
            _ when tool.StartsWith("todo", StringComparison.Ordinal) => "正在規劃步驟",
            _ when tool.StartsWith("skill", StringComparison.Ordinal) => "正在查看技能",
            _ => "正在使用工具 " + tool
        };
    }

    private static string With(string verb, string? detail) => detail == null ? verb : verb + "：" + detail;

    private static string? FileName(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview)) return null;
        var path = preview.Trim().Trim('"', '\'');
        int slash = path.LastIndexOfAny(['/', '\\']);
        return Short(slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path);
    }

    internal static string? Short(string? text, int limit = PreviewLimit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length <= limit) return line;
        int cut = limit - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut] + "…";
    }

    internal static string Elapsed(TimeSpan time) =>
        time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} 分 {time.Seconds:00} 秒" : $"{(int)time.TotalSeconds} 秒";
}

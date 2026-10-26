namespace BigBlueFish.HermesBridge;

// What the speech bubble says while Hermes works: a short phrase per tool, plus the tool's own
// one-line preview (search words, address, file name) where that helps. MCP tools of any server,
// including ones added later, are described from their names: mcp__divebird__get_download reads
// as 正在用 Divebird 查詢下載.
internal static class ToolStatus
{
    private const int PreviewLimit = 30;

    // First word of a tool name → what is being done.
    private static readonly Dictionary<string, string> Verbs = Table(
        ("查詢", "get list read fetch query search find lookup probe check describe show view inspect status info"),
        ("下載", "download"), ("上傳", "upload"),
        ("建立", "create add new make generate build"),
        ("調整", "update set edit modify write save rename configure config control pause resume move"),
        ("刪除", "delete remove clear purge"), ("停止", "cancel stop kill abort"),
        ("傳送", "send post reply notify publish push"),
        ("執行", "run exec execute start launch open invoke call trigger"),
        ("擷取畫面", "capture screenshot snap"), ("播放", "play"), ("切換", "turn toggle switch enable disable"),
        ("翻譯", "translate"), ("分析", "analyze analyse summarize"), ("轉換", "convert"),
        ("管理", "manage"));

    // Later words → what it is done to; the first known one wins.
    private static readonly Dictionary<string, string> Objects = Table(
        ("下載", "download downloads"), ("狀態", "status health"), ("設定", "settings setting config"),
        ("網址", "url urls link links"), ("檔案", "file files document documents"), ("資料夾", "folder folders directory"),
        ("影片", "video videos"), ("媒體", "media"), ("圖片", "image images photo photos"),
        ("訊息", "message messages chat"), ("郵件", "email emails mail"), ("行事曆", "calendar"),
        ("行程", "event events"), ("工作", "task tasks job jobs"), ("筆記", "note notes"),
        ("網頁", "page pages web"), ("動作", "action actions"), ("連線", "connection connections"),
        ("資料", "data record records"), ("清單", "list"));

    // Null for calls not worth showing, such as Hermes driving the pet itself.
    internal static string? Describe(string tool, string? preview)
    {
        if (tool.StartsWith("mcp__", StringComparison.Ordinal)) return DescribeMcp(tool);
        string? detail = Short(preview);
        return tool switch
        {
            "tool_search" => "正在查找可用的工具",
            "tool_describe" => "正在查看工具的用法",
            "tool_call" => "正在使用外掛工具",
            "web_search" => With("正在上網搜尋", detail),
            "web_extract" => "正在閱讀網頁",
            _ when tool.StartsWith("browser", StringComparison.Ordinal) => "正在操作瀏覽器",
            "read_file" => With("正在讀取檔案", FileName(preview)),
            "search_files" => With("正在搜尋檔案", detail),
            "write_file" or "patch" => With("正在寫入檔案", FileName(preview)),
            "terminal" or "process" or "process_manage" or "execute_code" => "正在電腦上執行指令",
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
            _ => Action(Words(tool)) is { } action ? "正在" + action : "正在處理中"
        };
    }

    // mcp__<server>__<tool>, as Hermes names MCP tools; the pet's own server stays out of the bubble.
    private static string? DescribeMcp(string tool)
    {
        var parts = tool.Split("__", 3);
        if (parts.Length < 3) return "正在使用外掛工具";
        var server = Words(parts[1]);
        if (string.Join(' ', server) == "big blue fish") return null;
        string name = string.Join(' ', server.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
        return Action(Words(parts[2])) is { } action ? $"正在用 {name} {action}" : $"正在使用 {name} 的工具";
    }

    private static string? Action(List<string> words)
    {
        if (words.Count == 0 || !Verbs.TryGetValue(words[0], out var verb)) return null;
        string? thing = words.Skip(1).Select(word => Objects.GetValueOrDefault(word)).FirstOrDefault(found => found != null);
        return thing == null || verb.EndsWith(thing) ? verb : verb + thing;
    }

    // snake_case, kebab-case and camelCase names to lower-case words.
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = c is '_' or '-' or ' ' or '.';
            if (boundary || char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]))
            {
                if (current.Length > 0) words.Add(current.ToString());
                current.Clear();
                if (boundary) continue;
            }
            current.Append(char.ToLowerInvariant(c));
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    private static Dictionary<string, string> Table(params (string Text, string Words)[] rows)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (text, words) in rows)
            foreach (var word in words.Split(' ')) table.TryAdd(word, text);
        return table;
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

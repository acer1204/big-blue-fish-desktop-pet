// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Control;

public static class ControlTools
{
    public const string PrivacyWarning = "開啟 MCP 介面後，連線的 Agent 可以讀取和修改大肥魚的設定、取得目前前景視窗或整個螢幕的截圖，並接收你點選、拖曳大肥魚的事件。讀取到的螢幕內容會被 MCP 連線者取得，並可能由對方傳送給其使用的雲端服務。請留意隱私安全，僅連線你信任的 Agent。";
    public const string PuppetWarning = "開啟提線木偶模式後，大肥魚會停止自身的吐字和動作切換，完全由外部 Agent 接管。如果外部 Agent 程式關閉或斷開，大肥魚不會自動恢復原有功能；需關閉提線木偶模式才能恢復。";
    public static string PipeName => "BigBlueFish.Control." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))).Substring(0, 20);
    public static object Text(object value) => new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value) } } };
    public static object Error(string message) => new { isError = true, content = new[] { new { type = "text", text = message } } };
    public static object Image(string dataUrl, object metadata) => new { content = new object[] {
        new { type = "text", text = JsonSerializer.Serialize(metadata) },
        new { type = "image", mimeType = dataUrl[5..dataUrl.IndexOf(';')], data = dataUrl[(dataUrl.IndexOf(',') + 1)..] } } };
    public static readonly string[] SettingNames = ["ModelEnabled", "GpuEnabled", "PublicInfo", "ForegroundEnabled", "StopModelOnBattery",
        "HideOnFullscreen", "CheckUpdatesOnStartup", "WeatherRegionCode", "Scale", "BubbleScale", "SpeechMinMinutes",
        "BubbleDurationIndex", "AnimationIntervalSeconds", "BreakfastHour", "LunchHour", "DinnerHour", "Left", "Top", "LastBrushDate", "StartupEnabled", "PuppetMode", "McpChatEnabled"];
    private const string SettingsInput = """
        {"settings":{"type":"object","additionalProperties":false,"description":"按需提供欄位；省略的欄位保留。欄位名區分大小寫。McpEnabled 只讀。成功後讀取實際值；位置可能因工作區限制調整。","properties":{
          "ModelEnabled":{"type":"boolean","description":"啟用內建模型；不限制外部 say。"},
          "GpuEnabled":{"type":"boolean","description":"本機推理使用 GPU；切換會停止目前模型任務。"},
          "PublicInfo":{"type":"boolean","description":"內建天氣和科技新聞。"},
          "ForegroundEnabled":{"type":"boolean","description":"內建模型是否讀取螢幕；不是 MCP 截圖的權限開關。"},
          "StopModelOnBattery":{"type":"boolean","description":"拔掉電源時暫停內建模型並釋放記憶體；不限制外部文字。"},
          "HideOnFullscreen":{"type":"boolean","description":"外部全螢幕時隱藏桌寵；不限制 MCP 截圖。"},
          "CheckUpdatesOnStartup":{"type":"boolean","description":"啟動時檢查更新。"},
          "WeatherRegionCode":{"type":"string","pattern":"^([0-9]{6}|[0-9]{9}|TW-[0-9]{3}-[0-9]{2})$","description":"內建地區表中存在的鄉鎮市區代碼。Windows 版可用臺灣鄉鎮代碼，如臺北市中正區 TW-061-01（要有天氣資料，需先在設定填入中央氣象署授權碼）；也接受原有的六位或九位數區縣代碼，如340104（Linux 版只接受這種）。不是縣市名稱或縣市層級代碼。"},
          "Scale":{"type":"number","minimum":0.5,"maximum":2,"description":"角色大小倍率；1為100%。"},
          "BubbleScale":{"type":"number","minimum":0.5,"maximum":2,"description":"獨立氣泡大小倍率；不改變角色大小。"},
          "SpeechMinMinutes":{"type":"integer","enum":[2,3,5,10,15,20],"description":"說話區間檔位：2對應2–3分鐘，3對應3–5，5對應5–8，10對應10–16，15對應15–23，20對應20–30；不是任意分鐘數。"},
          "BubbleDurationIndex":{"type":"integer","minimum":0,"maximum":7,"description":"索引0–7依次為5、10、15、30、60、120、180秒，以及直到下一句。"},
          "AnimationIntervalSeconds":{"type":"integer","minimum":30,"maximum":300,"description":"普通隨機動作事件間隔，單位秒，預設40；等目前片段結束再切換。不是幀率、播放速度或說話間隔。提線木偶模式暫停該排程。"},
          "BreakfastHour":{"type":"integer","minimum":0,"maximum":23,"description":"本地時間的早餐小時。"},
          "LunchHour":{"type":"integer","minimum":0,"maximum":23,"description":"本地時間的午餐小時。"},
          "DinnerHour":{"type":"integer","minimum":0,"maximum":23,"description":"本地時間的晚餐小時。"},
          "Left":{"type":["number","null"],"minimum":-1000000,"maximum":1000000,"description":"視窗左座標：Windows邏輯單位，Linux螢幕畫素；Linux擷取整數。null不重置位置。"},
          "Top":{"type":["number","null"],"minimum":-1000000,"maximum":1000000,"description":"視窗上座標，與Left相同的平臺語義；最終座標受螢幕工作區約束。"},
          "LastBrushDate":{"type":["string","null"],"pattern":"^[0-9]{4}-[0-9]{2}-[0-9]{2}$","description":"有效本地日期yyyy-MM-dd或null，用於每日刷牙動作去重；通常無需修改。"},
          "StartupEnabled":{"type":"boolean","description":"目前使用者的系統開機啟動設定。"},
          "PuppetMode":{"type":"boolean","description":"完全外部接管。每次從false改true須等待使用者在桌面確認；視窗等待期間桌寵仍可拖曳。中斷連線不會自動退出該模式。"},
          "McpChatEnabled":{"type":"boolean","description":"允許雙擊開啟輸入框；還必須有完成初始化的真實MCP連線。文字通過user_message交給客戶端。"}
        }}}
        """;
    public static object[] Definitions => [
        Tool("get_settings", "讀取settings與state。McpEnabled只讀，其餘可寫欄位和範圍見update_settings的schema。state是執行快照，不可寫；modelAvailable僅表示模型檔案可用，不等於推理服務已載入就緒。content[0].text為JSON字串。", "{}"),
        Tool("update_settings", "按欄位修改設定並嘗試儲存；省略欄位保留，空物件也會走應用設定流程。先驗證整個補丁，欄位或使用者確認失敗時不應用。不會開啟或啟用設定視窗。Windows按相關欄位停止模型或重排計時；Linux每次應用都會停止模型並重排計時。修改前先讀狀態；逾時、取消或IO錯誤後先讀回，勿盲目重試。啟用PuppetMode須等待真實使用者確認；本工具不能修改McpEnabled。", SettingsInput, ["settings"]),
        Tool("list_actions", "列出106個動作的精確action名稱、pool、基礎weight與名義duration_seconds。複製action原文傳給動作工具；分類和權重是自主排程後設資料，Disabled動作也可外部指定。名義時長不是完成通知。", "{}"),
        Tool("capture_foreground", "按大肥魚現有前景視窗選擇規則截圖，返回記憶體中的 PNG 影像。會把螢幕內容交給 MCP 連線者。", """{"max_dimension":{"type":"integer","minimum":256,"maximum":16384,"default":2560}}"""),
        Tool("capture_screen", "擷取整個虛擬桌面，包含所有顯示器，返回記憶體中的 PNG 影像。會把全螢幕內容交給 MCP 連線者。", """{"max_dimension":{"type":"integer","minimum":256,"maximum":16384,"default":4096}}"""),
        Tool("play_action", "將指定動作加入最多100項的FIFO佇列；重複動作不去重。目前片段結束、未隱藏且未拖曳時取下一項。呼叫成功只表示入隊，不表示正在播放或完成；正常點選仍可打斷，獨佔控制請用提線木偶模式。action必須精確匹配list_actions。", """{"action":{"type":"string","minLength":1,"maxLength":200}}""", ["action"]),
        Tool("interrupt_action", "立即切換到指定動作，清除尚未播放的全部外部動作並取消移動安排。不會自動顯示被隱藏的角色。提線木偶模式下播完回到固定待機；普通模式恢復自主排程。成功僅表示已發起播放，不是播放完成。", """{"action":{"type":"string","minLength":1,"maxLength":200}}""", ["action"]),
        Tool("say", "立即按約28個組合字元/秒顯示傳入text原文，替換目前氣泡並取消目前本機生成；不呼叫模型，不改寫、朗讀或解析Markdown。不能全為空白，執行時最多4000個UTF-16單元。無需本機模型或插電；不自動恢復隱藏角色，不清除say_next待說原文。displayed=true表示進入顯示流程，並非使用者已看見或吐字完成。", """{"text":{"type":"string","minLength":1,"maxLength":4000}}""", ["text"]),
        Tool("say_next", "僅儲存一條待說原文，新呼叫覆蓋舊呼叫。下次滿足正常說話倒計時、可見、未拖曳、未開設定/安裝視窗、鍵鼠空閒8–600秒等條件時顯示；不要求本機模型可用。pending=true只表示已儲存，不保證何時顯示。提線木偶模式暫停該節奏，吐字測試不消費它；關閉MCP或退出清除，單純斷開仍保留。", """{"text":{"type":"string","minLength":1,"maxLength":4000}}""", ["text"])
    ];
    private static object Tool(string name, string description, string properties, string[]? required = null) => new {
        name, description, inputSchema = new { type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties),
            required = required ?? [], additionalProperties = false } };
    public static string RequiredText(JsonElement args, string name, int maximum = 4000)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximum)
            throw new ArgumentException($"{name} 必須是 1–{maximum} 字元的文字。");
        return value.GetString()!;
    }
    public static int ImageLimit(JsonElement args, int fallback)
    {
        if (!args.TryGetProperty("max_dimension", out var value)) return fallback;
        if (!value.TryGetInt32(out var size) || size < 256 || size > 16384) throw new ArgumentException("max_dimension 範圍為256–16384。");
        return size;
    }
}

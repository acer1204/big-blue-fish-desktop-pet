// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Control;

public static class ControlTools
{
    public const string PrivacyWarning = "開啟 MCP 介面後，連線的 Agent 可以讀取和修改大肥魚的設定、取得目前前景視窗或整個螢幕的截圖、讀取你的使用狀態（閒置時間、前景程式名稱與視窗標題、電源與電量），並接收你點選、拖曳大肥魚的事件。讀取到的螢幕內容會被 MCP 連線者取得，並可能由對方傳送給其使用的雲端服務。請留意隱私安全，僅連線你信任的 Agent。";
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
        "BubbleDurationIndex", "AnimationIntervalSeconds", "BreakfastHour", "LunchHour", "DinnerHour", "Left", "Top", "LastBrushDate", "StartupEnabled", "PuppetMode", "McpChatEnabled",
        "SitReminderMinutes", "MealReminder", "LateReminder", "LateReminderHour", "BatteryReminder", "RainReminder", "DailyBriefing", "WelcomeBackMinutes", "EventReminderMinutes"];
    private const string SettingsInput = """
        {"settings":{"type":"object","additionalProperties":false,"description":"按需提供欄位；省略的欄位保留。欄位名區分大小寫。McpEnabled 只讀。成功後讀取實際值；位置可能因工作區限制調整。","properties":{
          "ModelEnabled":{"type":"boolean","description":"設定頁的「啟用模型說話」：允許桌寵用內建或遠端模型自主說話與播報天氣；不限制外部 say。"},
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
          "McpChatEnabled":{"type":"boolean","description":"允許雙擊開啟輸入框；還必須有完成初始化的真實MCP連線。文字與附件路徑（圖片、影片或檔案，經迴紋針、Ctrl+V或拖曳加入）通過user_message交給客戶端。"},
          "SitReminderMinutes":{"type":"integer","enum":[0,30,45,60,90,120],"description":"Windows版魚魚自己的久坐提醒：連續使用電腦（中間沒有離開5分鐘以上）幾分鐘後提醒起來活動，之後仍沒休息，每再連續使用同樣的分鐘數（至少30分鐘）再提醒一次；0為關閉。"},
          "MealReminder":{"type":"boolean","description":"Windows版的用餐提醒：BreakfastHour、LunchHour、DinnerHour整點起90分鐘內，使用者在電腦前時各提醒一次。"},
          "LateReminder":{"type":"boolean","description":"Windows版的深夜提醒開關：true時從LateReminderHour到早上5點，使用者還在用電腦就提醒休息，每小時一次；要關掉深夜提醒時設成false。"},
          "LateReminderHour":{"type":"integer","enum":[22,23,0,1,2],"description":"深夜提醒從哪個整點開始：22、23、0（凌晨0點，不是關閉）、1、2；開關是LateReminder。"},
          "BatteryReminder":{"type":"boolean","description":"Windows版的低電量提醒：沒插電且電量剩20%、10%時各提醒一次，插上電源後重新計算。"},
          "RainReminder":{"type":"boolean","description":"Windows版的下雨提醒：PublicInfo開啟且有天氣資料時，6小時內降雨機率60%以上就提醒帶傘，6小時內不重複；只在6–22點。"},
          "DailyBriefing":{"type":"boolean","description":"每天第一次坐下時由Hermes橋接程式講今日簡報（天氣、今天的Google日曆行程（已設定Google日曆時）、今天還要跑的Hermes排程、昨晚以來的消息）；魚魚自己不講，沒有橋接程式時沒有作用。"},
          "WelcomeBackMinutes":{"type":"integer","enum":[0,30,60,120,240],"description":"離開電腦至少這麼多分鐘後回來時，由Hermes橋接程式說這段時間發生的事（排程回報、其他對話、錯過的用餐時間）；0為關閉，沒有橋接程式時沒有作用。"},
          "EventReminderMinutes":{"type":"integer","enum":[0,5,10,15,30],"description":"Google日曆（勾選顯示的所有日曆）有開始時間的行程開始前幾分鐘，使用者在電腦前時由Hermes橋接程式提醒；整天的行程和已回覆不參加的邀請不提醒；0為關閉。需要先替Hermes設定好Google日曆，沒有橋接程式時沒有作用。"}
        }}}
        """;
    public static object[] Definitions => [
        Tool("get_settings", "讀取settings與state。McpEnabled只讀，其餘可寫欄位和範圍見update_settings的schema。state是執行快照，不可寫；modelAvailable僅表示模型檔案可用，不等於推理服務已載入就緒。Windows版的bubbleVisible表示氣泡顯示中，externalBubble表示目前氣泡來自say或say_next，speechPending表示魚魚正在準備自己的說話或天氣，fullscreen表示前景是全螢幕程式，idleSeconds是鍵盤滑鼠多久沒動、activeMinutes是這次連續使用幾分鐘（不在電腦前為null）、lastBreakMinutes是上一次休息多久。content[0].text為JSON字串。", "{}"),
        Tool("get_context", "讀取使用者此刻的狀況（唯讀，不截圖、不連網，Windows版）：summary（已換算好的一段中文說明：日期、星期、時間、多久前還在操作電腦、這次連續使用多久、有沒有插電，回答使用者時以它為準）、localTime（本地時間含時區）、weekday、idleSeconds（鍵盤滑鼠多久沒動）、activeMinutes（這次連續使用電腦幾分鐘，中間離開5分鐘以上才算休息；不在電腦前時為null）、lastBreakMinutes（上一次休息多久，不知道時為null）、fullscreen（前景是否為全螢幕程式）、power（onAC是否插電、batteryPercent電量，沒有電池時為null）、foreground（前景程式process與視窗title；使用者關閉ForegroundEnabled時為null）、weather（魚魚快取的天氣預報原文，沒有時為null）、openFolders（畫面上開著的檔案總管視窗：folder是它開著的資料夾完整路徑、window是視窗標題，最上層的那個排第一且front=true；使用者說「這個資料夾」時就是它）與lastReminder（魚魚自己最近一次的主動提醒kind、text、at）。要主動關心、決定現在適不適合打擾、回答現在幾點或要不要帶傘之前先讀；提醒的開關在get_settings的SitReminderMinutes、MealReminder、LateReminder（與LateReminderHour）、BatteryReminder、RainReminder。content[0].text為JSON字串。", "{}"),
        Tool("update_settings", "按欄位修改設定並嘗試儲存；省略欄位保留，空物件也會走應用設定流程。先驗證整個補丁，欄位或使用者確認失敗時不應用。不會開啟或啟用設定視窗。Windows按相關欄位停止模型或重排計時；Linux每次應用都會停止模型並重排計時。修改前先讀狀態；逾時、取消或IO錯誤後先讀回，勿盲目重試。啟用PuppetMode須等待真實使用者確認；本工具不能修改McpEnabled。", SettingsInput, ["settings"]),
        Tool("list_actions", "列出106個動作的精確action名稱、pool、基礎weight與名義duration_seconds。複製action原文傳給動作工具；分類和權重是自主排程後設資料，Disabled動作也可外部指定。名義時長不是完成通知。", "{}"),
        Tool("capture_foreground", "按大肥魚現有前景視窗選擇規則截圖，返回記憶體中的 PNG 影像。會把螢幕內容交給 MCP 連線者。" + CaptureMetadata, """{"max_dimension":{"type":"integer","minimum":256,"maximum":16384,"default":2560}}"""),
        Tool("capture_screen", "擷取整個虛擬桌面，包含所有顯示器，返回記憶體中的 PNG 影像。會把全螢幕內容交給 MCP 連線者。" + CaptureMetadata, """{"max_dimension":{"type":"integer","minimum":256,"maximum":16384,"default":4096}}"""),
        Tool("play_action", "將指定動作加入最多100項的FIFO佇列；重複動作不去重。目前片段結束、未隱藏且未拖曳時取下一項。呼叫成功只表示入隊，不表示正在播放或完成；正常點選仍可打斷，獨佔控制請用提線木偶模式。action填list_actions的名稱（名稱是簡體字，寫成繁體也認得）；名稱不對時錯誤訊息會列出最接近的名稱。", """{"action":{"type":"string","minLength":1,"maxLength":200}}""", ["action"]),
        Tool("interrupt_action", "立即切換到指定動作，清除尚未播放的全部外部動作並取消移動安排。不會自動顯示被隱藏的角色。提線木偶模式下播完回到固定待機；普通模式恢復自主排程。成功僅表示已發起播放，不是播放完成。", """{"action":{"type":"string","minLength":1,"maxLength":200}}""", ["action"]),
        Tool("walk_to", "讓魚魚小步快跑到螢幕上的目標旁邊並停在那裡，不會自動跑回原位。目標用中心點x、y（可加width、height）或desktop_item指定，find_on_screen的結果可原樣填入；stops可一次依序跑過多個目標（見stops）。魚魚站在目標左右側（放不下時改到下方或上方），身體不遮住目標，抵達時面向它。水平為主播奔跑、垂直為主播漂浮踏步；速度約每秒1.5個身長，最長約8秒。開始移動時會收起雙擊輸入框；抵達後位置會儲存，之後的自主動作以新位置為準，再開的輸入框也避開目標；行走中的play_action等抵達後再播。行走中get_settings的state.animationPhase為WalkStart、WalkLoop或WalkStop，state.walk為進度（否則為null），state.lastWalk為最近一次的結果。使用者拖曳或點選、interrupt_action、新的walk_to、隱藏、退出、開啟設定或修改位置與大小都會讓魚魚停在當下位置，cancelled說明原因（drag、click、interrupt、replaced、hidden、exit、settings等）。不會自動顯示被隱藏的角色；目標不在任何顯示器上會回錯誤。回傳的from、to、Left、Top與get_settings的Left、Top同為邏輯單位，target與petBounds為實體畫素。有stops時每站抵達後氣泡顯示「第幾個（共幾個）：label」，停pause_seconds秒再跑下一站，任何一站被打斷就整趟停在當下位置；回傳stops、reached（抵達的站數）、arrived、cancelled、note（被使用者打斷時該怎麼做）與每站的legs，行走中get_settings的state.tour為進度。", """
            {"x":{"type":"number","description":"目標中心X，座標系見space；find_on_screen回傳的x可原樣填入。"},
             "y":{"type":"number","description":"目標中心Y。"},
             "width":{"type":"number","minimum":0,"maximum":100000,"default":0,"description":"目標寬度（同space）；0表示只給一點，以約一個桌面圖示的大小避讓。"},
             "height":{"type":"number","minimum":0,"maximum":100000,"default":0,"description":"目標高度（同space）。"},
             "space":{"type":"string","enum":["screen","capture"],"default":"screen","description":"screen：Windows實體畫素的虛擬桌面座標，與find_on_screen的結果、UI Automation及computer_use元素bounds相同，不是截圖上的畫素。capture：本服務capture_screen或capture_foreground回傳影像上的畫素，依該次截圖的screen與scale換算；看到的是縮小後的圖時另填image_width、image_height。填了capture_id、image_width或image_height時省略space也視為capture，和space=screen一起填會回錯誤。"},
             "capture_id":{"type":"integer","minimum":1,"description":"space=capture時使用的captureId；省略用最近一次，只保留最近8次。"},
             "image_width":{"type":"number","minimum":1,"maximum":100000,"description":"space=capture時，x、y、width、height所在那張圖（你看到的那張）的寬度。客戶端把截圖縮小後會註明，例如Image downscaled from 3840x2160 to 960x540，這時填960（to後面的寬度），x、y照看到的圖填，不要照那句話自己乘回原尺寸，魚魚會換算；省略表示座標就是原截圖畫素。座標超出這張圖會回錯誤。"},
             "image_height":{"type":"number","minimum":1,"maximum":100000,"description":"同image_width，那張圖的高度；只給其中一個時按比例換算。"},
             "desktop_item":{"type":"string","minLength":1,"maxLength":260,"description":"改以一個桌面圖示的完整名稱定位（UI Automation讀取，不分大小寫：先完全相同，再單向比對副檔名（報告1找得到報告1.docx，報告1.pdf不會配到報告1.docx，版本號如3.12不算副檔名），再唯一一個包含它的名稱，三個字以上時再容許只差一個非數字的字且沒有其他同樣接近的名稱）；被視窗擋住的圖示也找得到。找一類東西（所有PDF、資料夾）請用find_on_screen。提供時忽略x、y、space。同一步有多個名稱符合時回錯誤並列出這些名稱；找不到時回錯誤，只列出真的相近的名稱。"},
             "side":{"type":"string","enum":["auto","left","right","above","below"],"default":"auto","description":"魚魚站在目標的哪一側；auto選空間足夠且離目前位置較近的一側，指定側放不下時改用auto。"},
             "arrive_action":{"type":"string","minLength":1,"maxLength":200,"description":"抵達後接著播放的動作，需精確匹配list_actions；省略時轉身面向使用者後回到待機。"},
             "say":{"type":"string","minLength":1,"maxLength":4000,"description":"抵達時顯示的氣泡原文，規則同say。"},
             "label":{"type":"string","minLength":1,"maxLength":80,"description":"抵達時氣泡裡顯示的簡短名稱；有stops時用於沒有自己label的每一站。"},
             "wait":{"type":"boolean","default":true,"description":"true等到抵達（有stops時為整趟跑完）或被打斷才回傳（每站通常不超過約9秒加上pause_seconds）；false啟動後立即回傳started=true。"},
             "stops":{"type":"array","minItems":1,"maxItems":20,"description":"依序要跑的目標，第一站先跑；提供時忽略最上層的x、y、width、height、desktop_item與say。space、capture_id、image_width、image_height與side套用到每一站，arrive_action只在最後一站播放。開始前會先檢查每一站，有一站不行就回錯誤，魚魚不動。","items":{"type":"object","additionalProperties":false,"properties":{
               "x":{"type":"number","description":"這站目標中心X，座標系同space。"},"y":{"type":"number","description":"這站目標中心Y。"},
               "width":{"type":"number","minimum":0,"maximum":100000,"description":"這站目標寬度。"},"height":{"type":"number","minimum":0,"maximum":100000,"description":"這站目標高度。"},
               "desktop_item":{"type":"string","minLength":1,"maxLength":260,"description":"改以桌面圖示名稱指定這站，規則同最上層的desktop_item。"},
               "label":{"type":"string","minLength":1,"maxLength":80,"description":"抵達這站時氣泡裡顯示的簡短名稱。"}}}},
             "pause_seconds":{"type":"number","minimum":0,"maximum":10,"default":2,"description":"有stops時，每站抵達後停留幾秒再跑下一站。"}}
            """),
        Tool("find_on_screen", "在目前的畫面上依名稱找出看得到的東西：桌面圖示（附它對應的檔名，副檔名被隱藏也比對得到）、視窗標題，以及每個看得到的視窗裡的按鈕、分頁、清單項目、連結、文字等介面元素（UI Automation）。只回傳現在看得到的：被其他視窗擋住、捲到畫面外的不算，魚魚自己的視窗也不算。matches依閱讀順序（由上而下、由左而右）排列，每筆的x、y是中心點、width與height是大小，都是Windows實體畫素，可原樣填進walk_to（或它的stops，space=screen）；kind為desktop_icon、window或element；檔案總管視窗和它裡面的元素附openFolder（那個視窗開著的資料夾完整路徑），確定是哪個檔案的桌面圖示附path（完整路徑），要對這些檔案動手時直接用這些路徑。total是全部符合的數量，returned是這次列出的數量。coveredDesktopIcons表示另有幾個符合的桌面圖示被視窗擋住；unfinished列出來不及讀完的視窗；沒有符合時similar列出名稱相近的；note是下一步該怎麼做的提醒（符合太多個、或都被視窗擋住時）。只有圖案、沒有名稱的東西找不到，請改用capture_screen。content[0].text為JSON字串。", """
            {"query":{"type":"string","minLength":1,"maxLength":200,"description":"名稱裡要包含的文字，不分大小寫；用|分隔多種說法，例如\"docx|doc|Word 文件\"或\"資源回收筒|回收\"。英文字母要從字的開頭到字的結尾整段對上（tar不會配到Star，doc不會配到Docker或docx），數字要從數字的開頭對上，中文任何位置都算。桌面圖示的檔名（含副檔名）與檔案總管的檔案類型也會比對。"},
             "where":{"type":"string","enum":["all","desktop","windows"],"default":"all","description":"all：桌面圖示與所有看得到的視窗；desktop：只找桌面圖示；windows：只找視窗標題與視窗裡的元素。"},
             "limit":{"type":"integer","minimum":1,"maximum":50,"default":20,"description":"最多列出幾筆。"}}
            """, ["query"]),
        Tool("say", "立即按約28個組合字元/秒顯示傳入text原文（Windows台灣版會先把其中的簡體字轉成繁體），替換目前氣泡並取消目前本機生成；不呼叫模型，不改寫內容、朗讀或解析Markdown。不能全為空白，執行時最多4000個UTF-16單元。無需本機模型或插電；不自動恢復隱藏角色，不清除say_next待說原文。displayed=true表示進入顯示流程，並非使用者已看見或吐字完成。", """{"text":{"type":"string","minLength":1,"maxLength":4000}}""", ["text"]),
        Tool("say_next", "僅儲存一條待說原文，新呼叫覆蓋舊呼叫。下次滿足正常說話倒計時、可見、未拖曳、未開設定/安裝視窗、鍵鼠空閒8–600秒等條件時顯示；不要求本機模型可用。pending=true只表示已儲存，不保證何時顯示。提線木偶模式暫停該節奏，吐字測試不消費它；關閉MCP或退出清除，單純斷開仍保留。", """{"text":{"type":"string","minLength":1,"maxLength":4000}}""", ["text"])
    ];
    private const string CaptureMetadata = "content[0].text的metadata另含captureId、screen（影像在虛擬桌面上的來源矩形，Windows實體畫素）、scale（影像畫素÷實體畫素）、coordinateSpace與pet（魚身的實體畫素矩形）；螢幕X＝screen.x＋影像X÷scale，Y同理。影像上的位置可直接交給walk_to（space=capture，附captureId）。";
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
    // ControlServer checks only the tool name, so optional arguments are checked against their schema here. null counts as omitted.
    public static double? OptionalNumber(JsonElement args, string name, double minimum, double maximum)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new ArgumentException($"{name} 必須是 {minimum}–{maximum} 的數字。");
        return number;
    }
    public static string? OptionalText(JsonElement args, string name, int maximum = 4000) =>
        args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? RequiredText(args, name, maximum) : null;
    public static bool OptionalBool(JsonElement args, string name, bool fallback)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ArgumentException($"{name} 必須是 true 或 false。") };
    }
    // The first choice is the default.
    public static string OptionalChoice(JsonElement args, string name, params string[] choices)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return choices[0];
        if (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString())) throw new ArgumentException($"{name} 只能是 {string.Join("、", choices)}。");
        return value.GetString()!;
    }
}

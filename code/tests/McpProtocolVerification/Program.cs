// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System.Diagnostics;
using System.Text.Json;
using BigBlueFish.Control;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var pipe = "BigBlueFish.Verification." + Guid.NewGuid().ToString("N");
using var server = new ControlServer((name, args, token) => Task.FromResult(ControlTools.Text(new { name, args })), pipe);
var bridge = Path.GetFullPath(args[0]);
Process Start() => Process.Start(new ProcessStartInfo(bridge) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    ArgumentList = { "--pipe", pipe } })!;
using var process = Start();
var notices = new List<JsonElement>();
async Task<JsonElement> Request(int id, string method, object? parameters = null)
{
    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters ?? new { } }));
    using var timeout = new CancellationTokenSource(10000);
    while (true)
    {
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Bridge exited: " + await process.StandardError.ReadToEndAsync());
        using var doc = JsonDocument.Parse(line); var value = doc.RootElement.Clone();
        if (value.TryGetProperty("id", out var found) && found.ValueKind == JsonValueKind.Number && found.GetInt32() == id) return value;
        notices.Add(value);
    }
}
var response = await Request(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "verification", version = "1" } });
Check(response.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "Protocol handshake");
Check(response.GetProperty("result").GetProperty("instructions").GetString()!.Contains("user_message"), "Handshake explains input event handling");
Check(response.GetProperty("result").GetProperty("instructions").GetString()!.Contains("user_returned"), "Handshake names the user_returned event");
await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
response = await Request(2, "tools/list");
var tools = response.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
Check(tools.Length == 12, "Twelve tools");
foreach (var tool in tools) Check(tool.GetProperty("inputSchema").GetProperty("type").GetString() == "object", "Object schema");
var toolNames = tools.Select(t => t.GetProperty("name").GetString()).ToList();
Check(toolNames.IndexOf("walk_to") == toolNames.IndexOf("interrupt_action") + 1, "walk_to listed right after interrupt_action");
var walkSchema = tools.Single(t => t.GetProperty("name").GetString() == "walk_to").GetProperty("inputSchema");
var walkProperties = walkSchema.GetProperty("properties");
Check(walkSchema.GetProperty("required").GetArrayLength() == 0 && !walkSchema.GetProperty("additionalProperties").GetBoolean()
    && walkProperties.GetProperty("space").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "screen", "capture" })
    && new[] { "x", "y", "width", "height", "capture_id", "image_width", "image_height", "desktop_item", "side", "arrive_action", "say", "wait", "stops", "pause_seconds" }.All(p => walkProperties.TryGetProperty(p, out _)),
    "walk_to documents screen and capture coordinates, desktop items, stops and its options");
var stopItems = walkProperties.GetProperty("stops").GetProperty("items");
Check(walkProperties.GetProperty("stops").GetProperty("maxItems").GetInt32() == 20 && !stopItems.GetProperty("additionalProperties").GetBoolean()
    && new[] { "x", "y", "width", "height", "desktop_item", "label" }.All(p => stopItems.GetProperty("properties").TryGetProperty(p, out _)), "walk_to stops take a target and a label each");
var find = tools.Single(t => t.GetProperty("name").GetString() == "find_on_screen");
Check(toolNames.IndexOf("find_on_screen") == toolNames.IndexOf("walk_to") + 1 && find.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "query" })
    && find.GetProperty("inputSchema").GetProperty("properties").GetProperty("where").GetProperty("enum").GetArrayLength() == 3
    && find.GetProperty("description").GetString()!.Contains("walk_to") && find.GetProperty("description").GetString()!.Contains("openFolder"),
    "find_on_screen follows walk_to, needs a query, hands its matches to walk_to and names Explorer folders");
var context = tools.Single(t => t.GetProperty("name").GetString() == "get_context");
Check(toolNames.IndexOf("get_context") == toolNames.IndexOf("get_settings") + 1 && context.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Count() == 0
    && new[] { "summary", "idleSeconds", "activeMinutes", "fullscreen", "batteryPercent", "foreground", "weather", "openFolders", "lastReminder", "SitReminderMinutes" }.All(context.GetProperty("description").GetString()!.Contains),
    "get_context follows get_settings, takes no arguments and names what it reports and where its reminders are switched");
Check(ControlTools.PrivacyWarning.Contains("閒置時間") && ControlTools.PrivacyWarning.Contains("視窗標題"), "The consent warning mentions the usage state get_context reads");
foreach (var capture in new[] { "capture_foreground", "capture_screen" })
    Check(tools.Single(t => t.GetProperty("name").GetString() == capture).GetProperty("description").GetString()!.Contains("captureId"), capture + " describes its coordinate metadata");
// ControlServer checks only tool names, so walk_to's optional arguments are validated by these helpers.
var sample = JsonSerializer.SerializeToElement(new { x = 12.5, wait = false, space = "capture", word = "text", big = 1e9, empty = (string?)null });
Check(ControlTools.OptionalNumber(sample, "x", -10, 100) == 12.5 && ControlTools.OptionalNumber(sample, "y", 0, 1) == null && ControlTools.OptionalNumber(sample, "empty", 0, 1) == null
    && !ControlTools.OptionalBool(sample, "wait", true) && ControlTools.OptionalBool(sample, "missing", true)
    && ControlTools.OptionalChoice(sample, "space", "screen", "capture") == "capture" && ControlTools.OptionalChoice(sample, "side", "auto", "left") == "auto"
    && ControlTools.OptionalText(sample, "word", 10) == "text" && ControlTools.OptionalText(sample, "empty") == null, "Optional arguments parse with their defaults");
bool Rejected(Action parse) { try { parse(); return false; } catch (ArgumentException) { return true; } }
Check(Rejected(() => ControlTools.OptionalNumber(sample, "word", 0, 1)) && Rejected(() => ControlTools.OptionalNumber(sample, "big", 0, 100))
    && Rejected(() => ControlTools.OptionalBool(sample, "x", true)) && Rejected(() => ControlTools.OptionalChoice(sample, "space", "screen"))
    && Rejected(() => ControlTools.OptionalText(sample, "word", 2)), "Invalid optional arguments are rejected");
var settingSchema = tools.Single(t => t.GetProperty("name").GetString() == "update_settings").GetProperty("inputSchema").GetProperty("properties").GetProperty("settings");
Check(!settingSchema.GetProperty("additionalProperties").GetBoolean(), "Settings schema rejects undocumented fields");
var settingProperties = settingSchema.GetProperty("properties");
Check(settingProperties.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(ControlTools.SettingNames.Order()), "Every writable setting is documented exactly once");
Check(!settingProperties.TryGetProperty("McpEnabled", out _), "User-only MCP switch absent from writable schema");
foreach (var property in settingProperties.EnumerateObject()) Check(!string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()), "Setting description present: " + property.Name);
var interval = settingProperties.GetProperty("AnimationIntervalSeconds");
Check(interval.GetProperty("type").GetString()=="integer" && interval.GetProperty("minimum").GetInt32()==30 && interval.GetProperty("maximum").GetInt32()==300, "Animation interval schema matches runtime bounds");
Check(settingProperties.GetProperty("SpeechMinMinutes").GetProperty("enum").EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(new[]{2,3,5,10,15,20}), "Speech range enum documented");
// The Hermes bridge reads this one from get_settings, so its choices are a contract with it.
Check(settingProperties.GetProperty("EventReminderMinutes").GetProperty("enum").EnumerateArray().Select(v=>v.GetInt32()).SequenceEqual(new[]{0,5,10,15,30}), "Event reminder choices documented");
foreach (var name in new[]{"Left","Top","LastBrushDate"}) Check(settingProperties.GetProperty(name).GetProperty("type").EnumerateArray().Any(t=>t.GetString()=="null"),"Nullable setting accurately documented: " + name);
response = await Request(3, "tools/call", new { name = "say", arguments = new { text = "主人好~原文含有英文、換行\n和標點。" } });
Check(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!.Contains("say"), "Tool result structure");
await Request(4, "resources/subscribe", new { uri = "bigbluefish://interactions" });
server.Publish("click", new { button = "left", x = 20, y = 30 });
response = await Request(5, "resources/read", new { uri = "bigbluefish://interactions" });
Check(response.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!.Contains("click"), "Readable interaction history");
Check(notices.Any(n => n.TryGetProperty("method", out var m) && m.GetString() == "notifications/message"), "Interaction pushed without polling");
Check(notices.Any(n => n.TryGetProperty("method", out var m) && m.GetString() == "notifications/resources/updated"), "Subscription pushed");
response = await Request(6, "tools/call", new { name = "unknown", arguments = new { } });
Check(response.GetProperty("result").GetProperty("isError").GetBoolean(), "Unknown tool returns tool error");
response = await Request(7, "resources/read", new { uri = "other://wrong" });
Check(response.GetProperty("error").GetProperty("code").GetInt32() == -32602, "Unknown resource rejected");
response = await Request(8, "logging/setLevel", new { level = "not-a-level" });
Check(response.TryGetProperty("error", out _), "Invalid log level rejected");
response = await Request(9, "ping"); Check(response.TryGetProperty("result", out _), "Ping after errors");
await Request(10,"logging/setLevel",new{level="warning"}); int beforeNotices=notices.Count;
server.Publish("user_message",new{text="原文\n第二行<> &"});
response=await Request(11,"resources/read",new{uri="bigbluefish://interactions"});
var records=JsonDocument.Parse(response.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement;
Check(records[records.GetArrayLength()-1].GetProperty("detail").GetProperty("text").GetString()=="原文\n第二行<> &","Input resource contains literal submitted text");
Check(!notices.Skip(beforeNotices).Any(n=>n.TryGetProperty("method",out var m)&&m.GetString()=="notifications/message"),"Warning log level filters info interaction notifications");
Check(notices.Skip(beforeNotices).Any(n=>n.TryGetProperty("method",out var m)&&m.GetString()=="notifications/resources/updated"),"Resource subscription still works with info logs filtered");
// Attachments travel as local paths and metadata only, so the event line stays small however large the files are.
var attachments=new[]{
    new{path=@"C:\Users\主人\AppData\Local\BigBlueFish\attachments\paste-20261026-101500-123-0a1b.png",name="paste-20261026-101500-123-0a1b.png",kind="image",mimeType="image/png",size=8_294_400L,source="clipboard"},
    new{path=@"D:\影片\海邊 & 夕陽.mp4",name="海邊 & 夕陽.mp4",kind="video",mimeType="video/mp4",size=1_073_741_824L,source="drop"},
    new{path=@"C:\Users\主人\Documents\報告.pdf",name="報告.pdf",kind="file",mimeType="application/pdf",size=52_431L,source="picker"}};
server.Publish("user_message",new{text="",attachments});
response=await Request(12,"resources/read",new{uri="bigbluefish://interactions"});
records=JsonDocument.Parse(response.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement;
var withFiles=records[records.GetArrayLength()-1].GetProperty("detail");
Check(withFiles.GetProperty("text").GetString()=="","Attachment-only message keeps an empty text field");
Check(withFiles.GetProperty("attachments").EnumerateArray().Select(a=>(a.GetProperty("path").GetString(),a.GetProperty("name").GetString(),a.GetProperty("kind").GetString(),a.GetProperty("mimeType").GetString(),a.GetProperty("size").GetInt64(),a.GetProperty("source").GetString()))
    .SequenceEqual(attachments.Select(a=>((string?)a.path,(string?)a.name,(string?)a.kind,(string?)a.mimeType,a.size,(string?)a.source))),"Attachments round-trip through the interaction resource unchanged");
Check(System.Text.Encoding.UTF8.GetByteCount(records[records.GetArrayLength()-1].GetRawText())<4096,"Attachment event stays far below 64 KB");
Check(!records[records.GetArrayLength()-2].GetProperty("detail").TryGetProperty("attachments",out _),"Plain text user_message has no attachments field");
Check(server.Connections == 1, "Connection tracked");
process.StandardInput.Close();
await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(process.ExitCode == 0, "Bridge exits at stdin EOF");
for (int i = 0; i < 50 && server.Connections != 0; i++) await Task.Delay(20);
Check(server.Connections == 0, "Connection released");
using var second = Start();
await second.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
var secondLine = await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(secondLine!.Contains("2025-03-26"), "Reconnect and older protocol negotiation");
server.Dispose();
await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(second.HasExited, "Disabling service disconnects bridge");
Console.WriteLine($"MCP protocol: {checks} checks passed.");

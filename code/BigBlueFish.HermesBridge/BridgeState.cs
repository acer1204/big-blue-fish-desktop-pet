using System.Text.Json;
using System.Text.Json.Nodes;

namespace BigBlueFish.HermesBridge;

// hermes-bridge.json keeps the Hermes session (HermesChat), which cron reports were already told
// (CronReports), and the day briefed, the event starts reminded and whether a lapsed calendar was told
// (Bridge). Each rewrites only its own key, so none wipes out another.
internal static class BridgeState
{
    private static readonly object Gate = new();

    // Empty when there is no file yet or it is not JSON (its old contents are lost either way); a file
    // that is there but cannot be read throws, so a passing lock never costs the other key its value.
    internal static JsonObject Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new JsonObject(); }
        try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    // Throws IOException, UnauthorizedAccessException or ArgumentException when the file cannot be written.
    internal static void Update(string path, Action<JsonObject> change)
    {
        lock (Gate)
        {
            var root = Read(path);
            change(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", root.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}

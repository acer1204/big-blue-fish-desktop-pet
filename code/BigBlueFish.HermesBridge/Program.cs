// Chat bridge for the taiwan-edition fork: text typed into the pet's double-click box goes to the
// Hermes Agent API server, and Hermes' answer comes back as the pet's speech bubble. The pet side is
// the stdio MCP bridge (BigBlueFish.Mcp.exe); the Hermes side is its local OpenAI-compatible API.
using System.Text;

namespace BigBlueFish.HermesBridge;

internal sealed record BridgeOptions(string McpCommand, Uri HermesUrl, string ApiKey, string StatePath, string? Ask);

internal static class Program
{
    private const string KeyVariable = "BIGBLUEFISH_HERMES_KEY";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        BridgeOptions options;
        try { options = ReadOptions(args); }
        catch (ArgumentException error)
        {
            Log.Write(error.Message);
            Console.WriteLine("用法：BigBlueFish.HermesBridge [--mcp BigBlueFish.Mcp.exe 路徑] [--hermes-url http://127.0.0.1:8642] [--state 對話紀錄檔] [--ask 測試訊息]");
            return 2;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var bridge = new Bridge(options);
            if (options.Ask != null) return await bridge.AskOnceAsync(options.Ask, stop.Token);
            // Two listening bridges would answer every message twice.
            using var single = new Mutex(true, @"Local\BigBlueFish.HermesBridge", out bool first);
            if (!first) { Log.Write("另一個橋接程式已經在執行，這個就先結束。"); return 1; }
            return await bridge.RunAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 130; }
        catch (Exception error) when (error is IOException or HermesException or HttpRequestException
            or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log.Write(error.Message);
            return 1;
        }
    }

    private static BridgeOptions ReadOptions(string[] args)
    {
        string? mcp = Environment.GetEnvironmentVariable("BIGBLUEFISH_MCP"), ask = null, state = null;
        string url = Environment.GetEnvironmentVariable("HERMES_API_URL") ?? "http://127.0.0.1:8642";
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} 後面缺少參數。");
            switch (args[i])
            {
                case "--mcp": mcp = Value(); break;
                case "--hermes-url": url = Value(); break;
                case "--state": state = Value(); break;
                case "--ask": ask = Value(); break;
                default: throw new ArgumentException("不認得的參數：" + args[i]);
            }
        }
        if (string.IsNullOrWhiteSpace(mcp)) mcp = FindMcpBridge() ?? throw new ArgumentException("找不到 BigBlueFish.Mcp.exe，請用 --mcp 指定它的路徑。");
        if (!File.Exists(mcp)) throw new ArgumentException("BigBlueFish.Mcp.exe 不存在：" + mcp);
        var hermesUrl = CheckHermesUrl(url);
        var fromEnvironment = Environment.GetEnvironmentVariable(KeyVariable);
        string key = (string.IsNullOrWhiteSpace(fromEnvironment) ? ReadHermesKey() : fromEnvironment.Trim())
            ?? throw new ArgumentException($"找不到 Hermes 的 API_SERVER_KEY：請在 Hermes 的 .env 設定，或用 {KeyVariable} 環境變數提供。");
        state = Path.GetFullPath(state ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigBlueFish", "hermes-bridge.json"));
        return new(Path.GetFullPath(mcp), hermesUrl, key, state, ask);
    }

    // The bearer key goes with every chat request, so it may only travel to this machine or over https.
    private static Uri CheckHermesUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("Hermes 網址必須是 http 或 https：" + url);
        if (parsed.UserInfo.Length > 0) throw new ArgumentException("Hermes 網址不能包含帳號密碼。");
        if (parsed.Scheme == Uri.UriSchemeHttp && !parsed.IsLoopback)
            throw new ArgumentException("不在這台電腦上的 Hermes 必須使用 https，避免金鑰以明文傳送。");
        // A trailing slash keeps a profile prefix such as /p/name/ when relative paths are appended.
        return new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
    }

    // Only fixed places: the bridge's own folder, this checkout's pet build output, and an installed pet.
    private static string? FindMcpBridge()
    {
        string checkout = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "BigBlueFish.Windows", "bin", "x64", "Release", "net10.0-windows10.0.26100.0", "BigBlueFish.Mcp.exe"));
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Mcp.exe"),
            checkout,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "蓝色大肥鱼", "BigBlueFish.Mcp.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // The key stays in Hermes' own .env; the bridge only reads it at startup and never prints it.
    private static string? ReadHermesKey()
    {
        string home = Environment.GetEnvironmentVariable("HERMES_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");
        string file = Path.Combine(home, ".env");
        if (!File.Exists(file)) return null;
        foreach (var line in File.ReadLines(file))
        {
            var text = line.Trim();
            if (!text.StartsWith("API_SERVER_KEY", StringComparison.Ordinal)) continue;
            int equals = text.IndexOf('=');
            if (equals < 0 || text[..equals].Trim() != "API_SERVER_KEY") continue;
            var value = text[(equals + 1)..].Trim().Trim('"', '\'');
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    internal static void Write(string message)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}

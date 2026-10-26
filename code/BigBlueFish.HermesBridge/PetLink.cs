using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BigBlueFish.HermesBridge;

// One MCP session with the pet through BigBlueFish.Mcp.exe: newline-delimited JSON-RPC 2.0 on stdio.
// Typed messages arrive as notifications/message from the bigbluefish.interaction logger.
internal sealed class PetLink : IAsyncDisposable
{
    private readonly Process process;
    private readonly ChannelWriter<string> inbox;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextId, lastSequence = -1;

    private PetLink(Process process, ChannelWriter<string> inbox)
    {
        this.process = process;
        this.inbox = inbox;
        _ = ReadAsync();
        _ = ReadErrorsAsync();
    }

    internal Task Closed => closed.Task;

    internal static async Task<PetLink> ConnectAsync(string command, ChannelWriter<string> inbox, CancellationToken token)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var process = Process.Start(start) ?? throw new IOException("無法啟動 " + command);
        process.StandardInput.NewLine = "\n";
        var link = new PetLink(process, inbox);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            await link.RequestAsync("initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "BigBlueFish.HermesBridge", version = "1.0" }
            }, limit.Token);
            await link.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
            // Interaction events are info-level logs; keep the threshold there.
            await link.RequestAsync("logging/setLevel", new { level = "info" }, limit.Token);
            return link;
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    // Returns the first text content of the tool result; a tool-level failure is an IOException.
    internal async Task<string?> CallToolAsync(string name, object arguments, CancellationToken token)
    {
        var result = await RequestAsync("tools/call", new { name, arguments }, token);
        var text = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            && content[0].TryGetProperty("text", out var first) ? first.GetString() : null;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new IOException($"魚魚的 {name} 工具回報錯誤：{text}");
        return text;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        long id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
            using var registration = token.Register(() => reply.TrySetCanceled(token));
            return await reply.Task;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message)
    {
        var line = JsonSerializer.Serialize(message);
        await writeGate.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
        }
        finally { writeGate.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                JsonElement root;
                try { using var document = JsonDocument.Parse(line); root = document.RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && pending.TryGetValue(number, out var reply))
                {
                    if (root.TryGetProperty("error", out var error))
                        reply.TrySetException(new IOException("魚魚的 MCP 回報錯誤：" + (error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString())));
                    else reply.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
                }
                else Notify(root);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            foreach (var reply in pending.Values) reply.TrySetException(new IOException("和魚魚的 MCP 連線已中斷。"));
            closed.TrySetResult();
        }
    }

    private void Notify(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var method) || method.GetString() != "notifications/message"
            || !root.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("logger", out var logger) || logger.GetString() != "bigbluefish.interaction"
            || !parameters.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("kind", out var kind) || kind.GetString() != "user_message"
            || !data.TryGetProperty("detail", out var detail) || !detail.TryGetProperty("text", out var text))
            return;
        // Sequence numbers restart with each pet session; within one link, never handle one twice.
        if (data.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out var value))
        {
            if (value <= lastSequence) return;
            lastSequence = value;
        }
        inbox.TryWrite(text.GetString() ?? "");
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (line.Trim().Length > 0) Log.Write("[魚魚 MCP] " + line.Trim());
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        process.Dispose();
    }
}

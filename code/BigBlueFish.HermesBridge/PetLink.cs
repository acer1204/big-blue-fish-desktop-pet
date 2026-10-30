using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BigBlueFish.HermesBridge;

// One message from the pet's input box. The text may be empty when files came with it.
internal sealed record PetMessage(string Text, IReadOnlyList<Attachment> Attachments);

// Something the pet tells about the user rather than says to Hermes: user_returned (back at the computer after
// a break, detail.breakMinutes). "linked" is the bridge's own: a new link came up.
internal sealed record PetEvent(string Kind, JsonElement Detail);

// A file the user attached, by its absolute local path: picked, dropped and copied files are the
// originals, pasted screenshots were saved as PNG by the pet before the message was sent. Kind is
// image, video or file.
internal sealed record Attachment(string Path, string Name, string Kind, string MimeType, long Size, string Source)
{
    internal const int Limit = 10; // the pet's input box takes no more

    internal static Attachment FromFile(string path, string source)
    {
        var info = new FileInfo(path);
        string mime = MimeOf(info.Extension);
        return new(info.FullName, info.Name, KindOf(mime), mime, info.Exists ? info.Length : -1, source);
    }

    // The same table as the pet's, for the few places that only know a path.
    internal static string MimeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", ".webm" => "video/webm",
        ".avi" => "video/x-msvideo", ".m4v" => "video/x-m4v",
        _ => "application/octet-stream"
    };

    internal static string KindOf(string mime) =>
        mime.StartsWith("image/", StringComparison.Ordinal) ? "image" : mime.StartsWith("video/", StringComparison.Ordinal) ? "video" : "file";
}

// One MCP session with the pet through BigBlueFish.Mcp.exe: newline-delimited JSON-RPC 2.0 on stdio.
// Typed messages arrive as notifications/message from the bigbluefish.interaction logger.
internal sealed class PetLink : IAsyncDisposable
{
    private readonly Process process;
    private readonly ChannelWriter<PetMessage> inbox;
    private readonly ChannelWriter<PetEvent>? events;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextId, lastSequence = -1;

    private PetLink(Process process, ChannelWriter<PetMessage> inbox, ChannelWriter<PetEvent>? events)
    {
        this.process = process;
        this.inbox = inbox;
        this.events = events;
        _ = ReadAsync();
        _ = ReadErrorsAsync();
    }

    internal Task Closed => closed.Task;

    internal static async Task<PetLink> ConnectAsync(string command, ChannelWriter<PetMessage> inbox, CancellationToken token, ChannelWriter<PetEvent>? events = null)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var process = Process.Start(start) ?? throw new IOException("無法啟動 " + command);
        process.StandardInput.NewLine = "\n";
        var link = new PetLink(process, inbox, events);
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
            || !data.TryGetProperty("kind", out var kind) || kind.GetString() is not ("user_message" or "user_returned")
            || !data.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            return;
        // Sequence numbers restart with each pet session and count every kind; within one link, never handle one twice.
        if (data.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out var value))
        {
            if (value <= lastSequence) return;
            lastSequence = value;
        }
        if (kind.GetString() == "user_returned") { events?.TryWrite(new PetEvent("user_returned", detail.Clone())); return; }
        if (!detail.TryGetProperty("text", out var text)) return;
        inbox.TryWrite(new PetMessage(text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "", ReadAttachments(detail)));
    }

    // detail.attachments is left out when there are none. Entries without a whole local path are dropped:
    // the path is all Hermes gets, and a control character in it could forge a line of the message.
    private static IReadOnlyList<Attachment> ReadAttachments(JsonElement detail)
    {
        if (!detail.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var found = new List<Attachment>();
        foreach (var item in list.EnumerateArray())
        {
            if (found.Count == Attachment.Limit) break;
            if (item.ValueKind != JsonValueKind.Object || Text(item, "path") is not { } path
                || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) continue;
            string name = Text(item, "name") is { } given && given.Trim().Length > 0 && !given.Any(char.IsControl) ? given : Path.GetFileName(path);
            string mime = Text(item, "mimeType") is { Length: > 0 } type && !type.Any(char.IsControl) ? type : Attachment.MimeOf(Path.GetExtension(path));
            string kind = Text(item, "kind") is { } stated && stated is "image" or "video" or "file" ? stated : Attachment.KindOf(mime);
            long size = item.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var length) ? length : -1;
            found.Add(new(path, name, kind, mime, size, Text(item, "source") ?? ""));
        }
        return found;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

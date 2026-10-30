using System.Text;
using System.Text.RegularExpressions;

namespace BigBlueFish.HermesBridge;

// Hermes' stream says when a tool starts and ends but not whether it failed; its own agent.log does:
// "WARNING [<session>] agent.tool_executor: Tool <name> returned error". This follows that log
// read-only for one session's failures. If the log is missing, rotated away or changes format, the
// failures simply go unnoticed; nothing else depends on it.
internal sealed partial class HermesLog(string path)
{
    // Polls once a second until `token` ends, then reads once more: a turn often ends a moment after
    // its last tool failed, before the next poll would have seen that line.
    internal async Task WatchAsync(string session, Func<string, Task> failed, CancellationToken token)
    {
        long position = Length();
        var carry = new List<byte>();
        async Task ReadNewAsync()
        {
            long length = Length();
            if (length < position) { position = 0; carry.Clear(); } // rotated: a new agent.log started
            if (length == position) return;
            byte[] chunk;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(position, SeekOrigin.Begin);
                chunk = new byte[Math.Min(length - position, 4 << 20)];
                chunk = chunk[..await stream.ReadAsync(chunk, CancellationToken.None)];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            position += chunk.Length;
            // Lines are split on bytes, so a UTF-8 character cut between two reads stays whole.
            carry.AddRange(chunk);
            int end;
            while ((end = carry.IndexOf((byte)'\n')) >= 0)
            {
                var line = Encoding.UTF8.GetString(carry.GetRange(0, end).ToArray());
                carry.RemoveRange(0, end + 1);
                var match = ToolError().Match(line);
                if (match.Success && match.Groups["session"].Value == session && Counts(match)) await failed(match.Groups["tool"].Value);
            }
        }
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                await ReadNewAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        await ReadNewAsync();
    }

    private long Length()
    {
        try { return new FileInfo(path).Exists ? new FileInfo(path).Length : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Not every logged failure belongs to what the user asked for: a command cut short by /stop or by the
    // next message logs "[Command interrupted]" a moment later, and Hermes' background review of a finished
    // turn runs its own bookkeeping tools under the same session tag.
    private static bool Counts(Match match) =>
        !Bookkeeping.Contains(match.Groups["tool"].Value) && !match.Groups["detail"].Value.Contains("[Command interrupted]");

    private static readonly HashSet<string> Bookkeeping =
        new(["memory", "todo_list", "skill_manage", "skill_view", "skills_list", "session_search", "setup_beat"], StringComparer.Ordinal);

    [GeneratedRegex(@"\bWARNING \[(?<session>[^\]]+)\] agent\.tool_executor: Tool (?<tool>\S+) returned error(?<detail>.*)")]
    private static partial Regex ToolError();
}

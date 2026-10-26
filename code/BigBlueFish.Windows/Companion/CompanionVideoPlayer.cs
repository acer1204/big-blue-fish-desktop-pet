using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.Windows;

// Streaming decoder: one raw frame in flight, original alpha and 24 fps, no temp images.
internal sealed class CompanionVideoPlayer : IDisposable
{
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint period);
    internal const int VideoWidth = 640, VideoHeight = 360, Fps = 24;
    private const int FrameBytes = VideoWidth * VideoHeight * 4, PreloadedFrames = 6;
    private readonly Image image;
    private readonly CompanionJob job = new();
    private readonly WriteableBitmap bitmap = new(VideoWidth, VideoHeight, 96, 96, PixelFormats.Bgra32, null);
    private readonly Dictionary<(string File, double Start), byte[][]> preloaded = new();
    private CancellationTokenSource? cancellation;
    private Process? decoder;
    private volatile bool paused;
    private bool fineTimer;
    private long generation;
    private bool disposed;
    internal long Frames { get; private set; }
    internal double PositionSeconds { get; private set; }
    internal double PlaybackRate { get; private set; } = 1;
    internal double StartSeconds { get; private set; }
    internal bool Paused { get => paused; set { paused = value; UpdateTimerResolution(); } }
    internal event Action<string>? Failed;
    internal event Action<double>? FramePresented;
    internal CompanionVideoPlayer(Image target) { image = target; image.Source = bitmap; UpdateTimerResolution(); }

    // The default 15.6 ms system timer made frames alternate between roughly 31 and 47 ms.
    // Request 1 ms resolution only while frames are shown; hidden pets release it.
    private void UpdateTimerResolution()
    {
        bool fine = !paused && !disposed;
        if (fine == fineTimer) return;
        fineTimer = fine;
        if (fine) timeBeginPeriod(1); else timeEndPeriod(1);
    }

    // Interaction clips keep their opening frames decoded, so a click or drag shows motion
    // immediately instead of holding the old frame while a new ffmpeg process starts.
    internal void Preload(string file, double startSeconds = 0)
    {
        var key = (file, startSeconds);
        if (disposed || preloaded.ContainsKey(key)) return;
        preloaded[key] = [];
        _ = Task.Run(async () =>
        {
            var frames = new List<byte[]>();
            try
            {
                using var process = new Process { StartInfo = Decoder(file, startSeconds, null, PreloadedFrames) };
                process.Start();
                if (!job.Attach(process)) { process.Kill(); return; }
                var errors = process.StandardError.ReadToEndAsync();
                while (frames.Count < PreloadedFrames)
                {
                    var pixels = new byte[FrameBytes];
                    if (await ReadFrame(process.StandardOutput.BaseStream, pixels, CancellationToken.None) != FrameBytes) break;
                    ClearResidue(pixels);
                    frames.Add(pixels);
                }
                await process.WaitForExitAsync();
                await errors;
                if (process.ExitCode != 0) frames.Clear();
            }
            catch { frames.Clear(); }
            await image.Dispatcher.InvokeAsync(() => { if (!disposed) preloaded[key] = frames.ToArray(); });
        });
    }

    internal void Play(string file, bool mirrored, Action completed, double startSeconds = 0,
        double? endSeconds = null, double playbackRate = 1)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || !double.IsFinite(playbackRate) || playbackRate <= 0
            || (endSeconds.HasValue && (!double.IsFinite(endSeconds.Value) || endSeconds.Value <= startSeconds)))
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        Stop();
        if (disposed) return;
        image.RenderTransformOrigin = new(.5, .5);
        image.RenderTransform = mirrored ? new ScaleTransform(-1, 1) : Transform.Identity;
        Frames = 0; PositionSeconds = startSeconds;
        StartSeconds = startSeconds; PlaybackRate = playbackRate;
        cancellation = new();
        var token = cancellation.Token;
        var id = ++generation;
        var opening = preloaded.TryGetValue((file, startSeconds), out var cached) ? cached : [];
        if (endSeconds.HasValue) opening = opening[..(int)Math.Min(opening.Length, Math.Floor((endSeconds.Value - startSeconds) * Fps))];
        var process = new Process { StartInfo = Decoder(file, startSeconds, endSeconds, null) };
        try
        {
            process.Start(); decoder = process;
            if (!job.Attach(process)) throw new IOException("無法關聯動畫解碼程式");
            _ = Task.Run(() => Decode(process, id, token, completed, startSeconds, playbackRate, opening));
        }
        catch (Exception ex)
        {
            Stop(); process.Dispose(); Failed?.Invoke(ex.Message);
        }
    }

    private static ProcessStartInfo Decoder(string file, double startSeconds, double? endSeconds, int? frameLimit)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-nostdin", "-threads", "1", "-c:v", "libvpx-vp9" }) start.ArgumentList.Add(arg);
        if (startSeconds > 0) { start.ArgumentList.Add("-ss"); start.ArgumentList.Add(startSeconds.ToString("R", CultureInfo.InvariantCulture)); }
        start.ArgumentList.Add("-i"); start.ArgumentList.Add(file);
        if (endSeconds.HasValue) { start.ArgumentList.Add("-t"); start.ArgumentList.Add((endSeconds.Value - startSeconds).ToString("R", CultureInfo.InvariantCulture)); }
        if (frameLimit.HasValue) { start.ArgumentList.Add("-frames:v"); start.ArgumentList.Add(frameLimit.Value.ToString(CultureInfo.InvariantCulture)); }
        foreach (var arg in new[] { "-an", "-sn", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task<int> ReadFrame(Stream stream, byte[] pixels, CancellationToken token)
    {
        int offset = 0;
        while (offset < FrameBytes)
        {
            int read = await stream.ReadAsync(pixels.AsMemory(offset, FrameBytes - offset), token);
            if (read == 0) break;
            offset += read;
        }
        return offset;
    }

    // VP9 alpha can contain tiny compression residue in nominally empty pixels.
    // Clear it so the layered window remains click-through around the character.
    private static void ClearResidue(byte[] pixels)
    {
        for (int alpha = 3; alpha < FrameBytes; alpha += 4)
            if (pixels[alpha] < 8) pixels[alpha] = 0;
    }

    private async Task Decode(Process process, long id, CancellationToken token, Action completed, double startSeconds, double playbackRate, byte[][] opening)
    {
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            var data = new byte[FrameBytes];
            // The clip clock starts with its first visible frame, so decoder startup is not
            // followed by a burst of frames that look already overdue.
            var clock = new Stopwatch();
            int count = 0;
            async Task WaitUntil(double target)
            {
                while (true)
                {
                    if (paused) { clock.Stop(); await Task.Delay(15, token); continue; }
                    if (count > 0) clock.Start();
                    double remaining = target - clock.Elapsed.TotalSeconds;
                    if (remaining <= 0) return;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining * 1000 - .5)), token);
                }
            }
            async Task Show(byte[] pixels)
            {
                await WaitUntil(count / (Fps * playbackRate));
                await image.Dispatcher.InvokeAsync(() =>
                {
                    if (id != generation || disposed) return;
                    bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, VideoWidth, VideoHeight), pixels, VideoWidth * 4, 0);
                    Frames = ++count; PositionSeconds = startSeconds + (count - 1) / (double)Fps;
                    FramePresented?.Invoke(PositionSeconds);
                }, System.Windows.Threading.DispatcherPriority.Render, token);
                clock.Start();
            }
            foreach (var pixels in opening) await Show(pixels);
            long decoded = 0;
            while (true)
            {
                int offset = await ReadFrame(process.StandardOutput.BaseStream, data, token);
                if (offset == 0) break;
                if (offset != FrameBytes) throw new IOException("動畫幀資料不完整");
                if (decoded++ < opening.Length) continue; // Already shown from the preloaded opening.
                ClearResidue(data);
                // Never reuse the frame buffer before the dispatcher has copied it.
                await Show(data);
            }
            await WaitUntil(count / (Fps * playbackRate)); // Give the final frame its full display interval.
            await process.WaitForExitAsync(token);
            var error = await errors;
            if (process.ExitCode != 0 || count == 0) throw new IOException("動畫解碼失敗：" + error);
            await image.Dispatcher.InvokeAsync(() => { if (id == generation && !disposed) completed(); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await image.Dispatcher.InvokeAsync(() => { if (id == generation && !disposed) Failed?.Invoke(ex.Message); });
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose();
            if (ReferenceEquals(decoder, process)) decoder = null;
            try { await errors; } catch { }
        }
    }
    internal void Stop()
    {
        generation++;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        try { if (decoder != null && !decoder.HasExited) decoder.Kill(); } catch { }
        decoder = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; Stop(); UpdateTimerResolution(); job.Dispose(); }
}

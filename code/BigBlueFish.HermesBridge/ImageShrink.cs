using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.HermesBridge;

// Re-encodes an image as JPEG, smaller step by step, until it fits a byte budget. WPF imaging (WIC) objects
// belong to the thread that made them, so each image gets a short-lived STA thread of its own.
internal static class ImageShrink
{
    // Long edge in pixels (0 = as it is) and JPEG quality, tried in order.
    private static readonly (int Edge, int Quality)[] Steps = [(0, 90), (2560, 90), (1920, 85), (1280, 80), (960, 75), (640, 70)];

    // Null when the bytes are not an image WIC can read, or no step fits.
    internal static Task<byte[]?> ToJpegAsync(byte[] source, long budget, CancellationToken token)
    {
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Encode(source, budget, token)); }
            catch (OperationCanceledException) { done.TrySetCanceled(token); }
            catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException
                or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException)
            {
                done.TrySetResult(null);
            }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "ImageShrink" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static byte[]? Encode(byte[] source, long budget, CancellationToken token)
    {
        using var input = new MemoryStream(source);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        // JPEG has no alpha; the screenshots and photos sent here have none worth keeping.
        BitmapSource opaque = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int longEdge = Math.Max(frame.PixelWidth, frame.PixelHeight);
        foreach (var (edge, quality) in Steps)
        {
            token.ThrowIfCancellationRequested();
            if (edge != 0 && edge >= longEdge) continue;
            double scale = edge == 0 ? 1 : (double)edge / longEdge;
            var sized = scale >= 1 ? opaque : new TransformedBitmap(opaque, new ScaleTransform(scale, scale));
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(sized));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length <= budget) return output.ToArray();
        }
        return null;
    }
}

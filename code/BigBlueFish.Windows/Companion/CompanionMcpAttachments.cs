#if !COMPANION_SAFE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.Windows;

// Attachments of the double-click input. user_message carries local paths and metadata only: every event is kept in
// the 128-entry history and serialized once per MCP session, so image bytes would multiply in memory and on the pipe.
public partial class MainWindow
{
    private const int McpAttachmentLimit = 10;
    private static readonly FontFamily McpGlyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly Dictionary<string, (string Kind, string Mime)> McpAttachmentTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".png"] = ("image", "image/png"), [".jpg"] = ("image", "image/jpeg"), [".jpeg"] = ("image", "image/jpeg"), [".gif"] = ("image", "image/gif"),
        [".webp"] = ("image", "image/webp"), [".bmp"] = ("image", "image/bmp"), [".tif"] = ("image", "image/tiff"), [".tiff"] = ("image", "image/tiff"),
        [".heic"] = ("image", "image/heic"), [".heif"] = ("image", "image/heif"),
        [".mp4"] = ("video", "video/mp4"), [".m4v"] = ("video", "video/x-m4v"), [".mov"] = ("video", "video/quicktime"), [".mkv"] = ("video", "video/x-matroska"),
        [".webm"] = ("video", "video/webm"), [".avi"] = ("video", "video/x-msvideo"), [".wmv"] = ("video", "video/x-ms-wmv"), [".mpg"] = ("video", "video/mpeg"),
        [".mpeg"] = ("video", "video/mpeg"), [".3gp"] = ("video", "video/3gpp"),
        [".pdf"] = ("file", "application/pdf"), [".txt"] = ("file", "text/plain"), [".md"] = ("file", "text/markdown"), [".csv"] = ("file", "text/csv"),
        [".json"] = ("file", "application/json"), [".zip"] = ("file", "application/zip"),
        [".docx"] = ("file", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".xlsx"] = ("file", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".pptx"] = ("file", "application/vnd.openxmlformats-officedocument.presentationml.presentation") };
    private static string? mcpAttachmentFolder;
    private static DateTime mcpAttachmentSweep;
    private McpAttachmentTray? mcpTray;

    // Pasted screenshots have no path of their own. Not the exe folder: installs may be read-only and updates replace it.
    private static string McpAttachmentFolder => mcpAttachmentFolder ??= App.Args.Contains("--test-mode")
        ? Path.Combine(Environment.CurrentDirectory, "attachments")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigBlueFish", "attachments");

    // Shared by the file picker, drag-and-drop and the UI tests (DragEventArgs cannot be constructed).
    private int AddMcpAttachments(IEnumerable<string> paths, string source) => mcpTray?.Add(paths, source) ?? 0;

    private static (string Kind, string Mime) McpAttachmentType(string path) =>
        McpAttachmentTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : ("file", "application/octet-stream");

    private static string[]? PickMcpAttachments(Window owner)
    {
        string Patterns(string kind) => string.Join(";", McpAttachmentTypes.Where(t => t.Value.Kind == kind).Select(t => "*" + t.Key));
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "附加圖片、影片或檔案", Multiselect = true,
            Filter = $"圖片與影片|{Patterns("image")};{Patterns("video")}|圖片|{Patterns("image")}|影片|{Patterns("video")}|所有檔案|*.*" };
        try { return dialog.ShowDialog(owner) == true ? dialog.FileNames : null; }
        catch (Exception e) when (e is InvalidOperationException or ExternalException) { return null; }
    }

    private static void AcceptMcpPaste(Window window, McpAttachmentTray tray)
    {
        window.PreviewKeyDown += (_, e) => {
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if ((key == Key.V && Keyboard.Modifiers == ModifierKeys.Control || key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift) && tray.TryPaste()) e.Handled = true;
        };
        // The TextBox context menu pastes through the routed command; with only an image on the clipboard the TextBox would disable it.
        CommandManager.AddPreviewCanExecuteHandler(window, (_, e) => {
            if (e.Command == ApplicationCommands.Paste && tray.HasClipboardAttachment()) { e.CanExecute = true; e.Handled = true; }
        });
        CommandManager.AddPreviewExecutedHandler(window, (_, e) => { if (e.Command == ApplicationCommands.Paste && tray.TryPaste()) e.Handled = true; });
    }

    // Tunneling events: the TextBox rejects non-text drags and marks them handled before bubbling handlers run.
    private static void AcceptMcpDrops(Window window, Border frame, McpAttachmentTray tray, Action focus)
    {
        var normal = frame.BorderBrush; var highlight = new SolidColorBrush(Color.FromRgb(64, 140, 230));
        window.AllowDrop = true;
        DragEventHandler over = (_, e) => {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = DragDropEffects.Copy; e.Handled = true; frame.BorderBrush = highlight;
        };
        window.PreviewDragEnter += over; window.PreviewDragOver += over;
        window.PreviewDragLeave += (_, _) => frame.BorderBrush = normal;
        window.PreviewDrop += (_, e) => {
            frame.BorderBrush = normal;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Effects = DragDropEffects.Copy; e.Handled = true;
            // e.Data is only valid during the event and Explorer stays blocked until Drop returns; file checks come afterwards.
            window.Dispatcher.BeginInvoke(() => { tray.Add(paths, "drop"); window.Activate(); focus(); });
        };
    }

    // Sent screenshots stay a week so the agent can look back at them; the folder is capped near 500 MB, oldest first.
    private static void SweepMcpAttachments()
    {
        if (DateTime.UtcNow - mcpAttachmentSweep < TimeSpan.FromHours(6)) return;
        mcpAttachmentSweep = DateTime.UtcNow;
        var folder = McpAttachmentFolder;
        _ = Task.Run(() => {
            try
            {
                if (!Directory.Exists(folder)) return;
                long kept = 0; DateTime now = DateTime.UtcNow;
                foreach (var file in new DirectoryInfo(folder).GetFiles("paste-*").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    bool stale = file.LastWriteTimeUtc < now.AddDays(-7) || file.Extension == ".tmp" && file.LastWriteTimeUtc < now.AddHours(-1);
                    if (!stale && kept + file.Length <= 500L << 20) { kept += file.Length; continue; }
                    try { file.Delete(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }

    private sealed class McpAttachment(string path, string kind, string mimeType, long size, string source, bool owned)
    {
        public readonly string Path = path, Name = System.IO.Path.GetFileName(path), Kind = kind, MimeType = mimeType, Source = source;
        public readonly bool Owned = owned; // a pasted screenshot in our folder, deleted if it is never sent
        public long Size = size;
        public Border? Tile;
    }

    private sealed class McpAttachmentTray
    {
        internal readonly List<McpAttachment> Items = [];
        internal readonly ScrollViewer View;
        internal Action? Changed;
        internal int Saving { get; private set; }
        private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal };
        private readonly Window owner;
        private readonly Brush accent;
        private readonly Action<string> notice;
        private bool closed;

        internal McpAttachmentTray(Window owner, Brush accent, Action<string> notice)
        {
            this.owner = owner; this.accent = accent; this.notice = notice;
            // A scroll bar would not fit under the 52 px tiles; the wheel scrolls the row sideways instead.
            View = new ScrollViewer { Content = strip, Height = 56, Margin = new Thickness(0, 6, 0, 0), Focusable = false, Visibility = Visibility.Collapsed,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            View.PreviewMouseWheel += (_, e) => { View.ScrollToHorizontalOffset(View.HorizontalOffset - e.Delta / 2.0); e.Handled = true; };
        }

        // Picked, dropped and copied files are referenced by their original path and never copied.
        internal int Add(IEnumerable<string> paths, string source, bool owned = false)
        {
            int added = 0; string? problem = null;
            foreach (var raw in paths)
            {
                if (closed) break;
                string path;
                try { path = System.IO.Path.GetFullPath(raw); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { problem = "無法辨識的檔案路徑"; continue; }
                var name = System.IO.Path.GetFileName(path);
                if (Directory.Exists(path)) { problem = "不支援資料夾：" + name; continue; }
                if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) { problem = "已經附加過：" + name; continue; }
                if (Items.Count >= McpAttachmentLimit) { problem = $"最多只能附加 {McpAttachmentLimit} 個檔案"; break; }
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { problem = "找不到或無法讀取：" + name; continue; }
                var type = McpAttachmentType(path);
                var item = new McpAttachment(path, type.Kind, type.Mime, size, source, owned);
                Items.Add(item); strip.Children.Add(item.Tile = Tile(item)); added++;
                // Agents usually receive images as data URLs, a third larger than the file.
                if (type.Kind == "image" && size > 20L << 20) problem = "圖片超過 20 MB，Agent 可能無法直接檢視：" + name;
            }
            if (added > 0) { View.Visibility = Visibility.Visible; View.ScrollToRightEnd(); Changed?.Invoke(); }
            if (problem != null || added > 0) notice(problem ?? $"已附加 {Items.Count} 個檔案");
            return added;
        }

        internal void Remove(McpAttachment item)
        {
            if (!Items.Remove(item)) return;
            strip.Children.Remove(item.Tile);
            if (item.Owned) Delete(item.Path);
            if (Items.Count == 0) View.Visibility = Visibility.Collapsed;
            notice(Items.Count == 0 ? "Ctrl+Enter 傳送" : $"已附加 {Items.Count} 個檔案");
            Changed?.Invoke();
        }

        // Detail entries for user_message. Files that disappeared since they were added are left out.
        internal object[] Take(out int missing)
        {
            var sent = new List<object>(); missing = 0;
            foreach (var item in Items)
            {
                try { item.Size = new FileInfo(item.Path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { missing++; continue; }
                sent.Add(new { path = item.Path, name = item.Name, kind = item.Kind, mimeType = item.MimeType, size = item.Size, source = item.Source });
            }
            Items.Clear(); strip.Children.Clear(); View.Visibility = Visibility.Collapsed; Changed?.Invoke();
            return sent.ToArray();
        }

        // Unsent pasted screenshots belong to this draft only; sent ones stay for the agent until the sweep.
        internal void Discard()
        {
            closed = true;
            foreach (var item in Items) if (item.Owned) Delete(item.Path);
            Items.Clear();
        }

        internal bool HasClipboardAttachment()
        {
            try { return Clipboard.ContainsFileDropList() || !Clipboard.ContainsText() && (ClipboardPngFormat() != null || Clipboard.ContainsImage()); }
            catch (ExternalException) { return false; }
        }

        // Copied files keep their paths (browsers and LINE add a bitmap beside the real file); a bare image becomes a PNG;
        // anything with text, such as cells copied from Excel or Word, pastes as text.
        internal bool TryPaste()
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) { Add(Clipboard.GetFileDropList().Cast<string>().ToArray(), "clipboard-files"); return true; }
                if (Clipboard.ContainsText() || ClipboardPngFormat() == null && !Clipboard.ContainsImage()) return false;
                if (Items.Count >= McpAttachmentLimit) { notice($"最多只能附加 {McpAttachmentLimit} 個檔案"); return true; }
                // The source app's own PNG is lossless and keeps transparency; Print Screen only offers the DIB.
                var png = ReadClipboardPng();
                var bitmap = png == null ? Clipboard.GetImage() : null;
                if (png == null && bitmap == null) { notice("無法讀取剪貼簿中的圖片"); return true; }
                if (bitmap is { IsFrozen: false }) bitmap = (BitmapSource)(bitmap.CanFreeze ? bitmap.GetAsFrozen() : new WriteableBitmap(bitmap).GetAsFrozen());
                _ = SavePaste(png, bitmap);
                return true;
            }
            catch (ExternalException) { notice("剪貼簿忙碌，請再試一次"); return true; }
        }

        private static string? ClipboardPngFormat() => new[] { "PNG", "image/png" }.FirstOrDefault(Clipboard.ContainsData);

        private static byte[]? ReadClipboardPng()
        {
            var format = ClipboardPngFormat();
            if (format == null) return null;
            var bytes = Clipboard.GetData(format) switch { MemoryStream memory => memory.ToArray(), byte[] raw => raw, _ => null };
            return bytes != null && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
        }

        // Encoding a 4K screenshot takes a few hundred milliseconds, too long for the 40 ms animation timer.
        private async Task SavePaste(byte[]? png, BitmapSource? bitmap)
        {
            Saving++; Changed?.Invoke(); notice("正在儲存貼上的圖片…");
            string? path = null;
            try { path = await Task.Run(() => WritePaste(png, bitmap)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ExternalException)
            { notice("無法儲存貼上的圖片：" + e.Message); }
            finally { Saving--; if (!closed) Changed?.Invoke(); } // the input may have closed while the file was written
            if (path != null && Add([path], "clipboard", true) == 0) Delete(path);
        }

        private static string WritePaste(byte[]? png, BitmapSource? bitmap)
        {
            if (png == null)
            {
                // 32-bit clipboard DIBs often carry an all-zero alpha channel and would save as a fully transparent image.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap!, PixelFormats.Bgr32, null, 0)));
                using var buffer = new MemoryStream(); encoder.Save(buffer); png = buffer.ToArray();
            }
            var folder = McpAttachmentFolder; Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Random.Shared.Next(0x10000):x4}.png");
            // The bridge may open the file as soon as the event arrives, so only a complete file gets the final name.
            File.WriteAllBytes(path + ".tmp", png); File.Move(path + ".tmp", path);
            return path;
        }

        // Only our own pasted files are ever deleted, never the user's originals.
        private static void Delete(string path)
        {
            if (!path.StartsWith(McpAttachmentFolder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private Border Tile(McpAttachment item)
        {
            var glyph = new TextBlock { Text = item.Kind switch { "image" => "\uE91B", "video" => "\uE714", _ => "\uE8A5" }, FontFamily = McpGlyphs, FontSize = 18,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = item.Name, FontSize = 10, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 3, 3, 0) };
            var placeholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { glyph, label } };
            var remove = new Button { Content = "×", Width = 16, Height = 16, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 2, 2, 0), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Focusable = false, BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), Foreground = accent, ToolTip = "移除附件" };
            AutomationProperties.SetName(remove, "移除附件 " + item.Name);
            remove.Click += (_, _) => Remove(item);
            var tile = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 237)), Background = new SolidColorBrush(Color.FromRgb(238, 241, 248)),
                ToolTip = item.Path + "\n" + FormatSize(item.Size), Child = new Grid { Children = { placeholder, remove } } };
            AutomationProperties.SetName(tile, item.Name);
            if (item.Kind == "image") _ = ShowThumbnail(item, tile, placeholder);
            return tile;
        }

        private async Task ShowThumbnail(McpAttachment item, Border tile, UIElement placeholder)
        {
            int pixels = (int)Math.Ceiling(52 * VisualTreeHelper.GetDpi(owner).DpiScaleY);
            var image = await Task.Run(() => Thumbnail(item.Path, pixels));
            // HEIC, or WebP without its codec, keeps the generic icon instead of blocking the attachment.
            if (image == null || !Items.Contains(item)) return;
            tile.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            placeholder.Visibility = Visibility.Collapsed;
        }

        private static BitmapSource? Thumbnail(string path, int pixels)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                bool tall = frame.PixelHeight > frame.PixelWidth;
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.StreamSource = stream;
                // Decode the shorter side at tile size so UniformToFill never upscales a portrait screenshot.
                if (tall) image.DecodePixelWidth = pixels; else image.DecodePixelHeight = pixels;
                image.EndInit(); image.Freeze();
                return image;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException
                or InvalidOperationException or ExternalException or OverflowException) { return null; }
        }

        private static string FormatSize(long bytes) => bytes switch {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB", >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
            >= 1L << 10 => $"{bytes / 1024.0:0.#} KB", _ => $"{bytes} B" };
    }
}
#endif

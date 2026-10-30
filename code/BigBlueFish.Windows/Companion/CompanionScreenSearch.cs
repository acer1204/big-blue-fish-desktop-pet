#if !COMPANION_SAFE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using BigBlueFish.Control;

namespace BigBlueFish.Windows;

// find_on_screen: what carries a name on the screen right now, in the physical pixels walk_to takes as they are.
// Desktop icons (with the file behind each, so a hidden extension still matches), window titles and the UI Automation
// elements of every visible window. Whatever another window covers, the fish's own windows and elements scrolled out
// of view are left out.
internal static class CompanionScreenSearch
{
    // Source: the window a match was found in (the desktop host for icons). ItemType: what an Explorer item is
    // (Microsoft Word 文件), compared with the query, unlike Type, which may only say what kind of control it is.
    // Files: every file an icon may be when its shown name fits more than one (報告 for 報告.docx and 報告.pdf).
    // OpenFolder: the folder a File Explorer window shows (on the window and on everything in it); Path: a desktop
    // icon's file. Without them a model guessed the folder from the window title and worked on the wrong one.
    internal sealed record Match(string Kind, string Name, Rect Bounds, string? File = null, bool Folder = false, string? Type = null,
        string? Window = null, string? Process = null, bool Walkable = true, IntPtr Source = default, string? ItemType = null, IReadOnlyList<string>? Files = null,
        string? OpenFolder = null, string? Path = null);
    internal sealed record Result(IReadOnlyList<Match> Matches, int CoveredIcons, int CoveredInWindows, IReadOnlyList<string> Unfinished, IReadOnlyList<string> Similar);
    private sealed record Shown(IntPtr Handle, Rect Bounds, string Title, string Class, uint Process);
    internal sealed record DesktopFile(string Name, bool Folder, bool Hidden, string FullName);
    private const string FolderWords = "檔案資料夾 folder";

    // A window still building its accessibility tree (a browser on its first query) is reported, not waited for.
    private static readonly TimeSpan ElementBudget = TimeSpan.FromSeconds(6);

    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(CompanionNative.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out CompanionNative.Rect value, int size);

    // Off the UI thread: the hit tests, window rectangles and UI Automation bounds are all physical pixels here.
    internal static Result Find(IReadOnlyList<string> terms, string where, CancellationToken token)
    {
        using var dpi = new CompanionScreenCapture.CaptureDpiScope();
        var windows = TopWindows();
        var matches = new List<Match>();
        var names = new List<string>();
        var unfinished = new List<string>();
        int coveredIcons = where == "windows" ? 0 : FindIcons(terms, windows, matches, names, unfinished, token);
        int coveredInWindows = where == "desktop" ? 0 : FindInWindows(terms, windows, matches, names, unfinished, token);
        uint system = CompanionNative.GetDpiForSystem();
        // walk_to cannot reach a monitor scaled differently from the main one.
        var kept = ReadingOrder(Distinct(matches)).Select(m => m with { Walkable = CompanionNative.MonitorDpi(Pixel(m.Bounds)) == system }).ToList();
        // A name that matches was only covered; suggesting it again as a similar one would send the agent round in a circle.
        return new(kept, coveredIcons, coveredInWindows, unfinished, kept.Count == 0 ? Similar(names.Where(n => !Matches(terms, n)), terms) : []);
    }

    // Every visible, uncloaked, unminimized top-level window of another process, top of the z-order first.
    private static List<Shown> TopWindows()
    {
        var shown = new List<Shown>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            if (DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            CompanionNative.GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId) return true;
            // Click-through overlays hide nothing under them.
            if (((long)CompanionNative.GetStyle(window, -20 /* GWL_EXSTYLE */) & 0x20 /* WS_EX_TRANSPARENT */) != 0) return true;
            if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out CompanionNative.Rect r, Marshal.SizeOf<CompanionNative.Rect>()) != 0
                && !CompanionNative.GetWindowRect(window, out r)) return true;
            if (r.Right <= r.Left || r.Bottom <= r.Top) return true;
            var title = new StringBuilder(512);
            CompanionNative.GetWindowText(window, title, title.Capacity);
            var type = new StringBuilder(128);
            CompanionNative.GetClassName(window, type, type.Capacity);
            shown.Add(new(window, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), Normalize(title.ToString()), type.ToString(), process));
            return true;
        }, IntPtr.Zero);
        return shown;
    }

    // The top-level window showing at a physical point. The fish's own windows are see-through here, and so is a
    // disabled window to WindowFromPoint (the owner of a modal dialog): those fall back on the z-order of the others.
    private static bool ShowsAt(IntPtr window, CompanionNative.Point point, List<Shown> windows)
    {
        if (CompanionNative.MonitorDpi(point) == null) return false;
        var hit = WindowFromPoint(point);
        var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2 /* GA_ROOT */);
        if (root == window) return true;
        CompanionNative.GetWindowThreadProcessId(root, out var process);
        if (root != IntPtr.Zero && process != Environment.ProcessId && IsWindowEnabled(window)) return false;
        // The desktop is several Progman and WorkerW windows stacked; any of them showing there shows the icons.
        var top = windows.FirstOrDefault(w => w.Bounds.Contains(point.X, point.Y))?.Handle ?? IntPtr.Zero;
        return top == window || top != IntPtr.Zero && CompanionNative.IsDesktopWindow(top) && CompanionNative.IsDesktopWindow(window);
    }

    private static int FindIcons(IReadOnlyList<string> terms, List<Shown> windows, List<Match> matches, List<string> names, List<string> unfinished, CancellationToken token)
    {
        var list = CompanionNative.DesktopListView();
        if (list == IntPtr.Zero) return 0; // Desktop icons switched off: there are none to see.
        // explorer busy or restarting: said, not taken for an empty desktop, and the windows are still searched.
        var reading = Task.Run(() => { using var dpi = new CompanionScreenCapture.CaptureDpiScope(); return DesktopIcons(); });
        List<(string Name, Rect Bounds)> icons;
        try { icons = reading.Wait(ElementBudget, token) ? reading.Result : throw new TimeoutException(); }
        catch (Exception error) when (error is AggregateException or TimeoutException) { unfinished.Add("桌面圖示"); return 0; }
        var host = GetAncestor(list, 2 /* GA_ROOT */);
        var files = DesktopFiles();
        int covered = 0;
        foreach (var icon in icons)
        {
            names.Add(icon.Name);
            var (sure, possible) = FilesOf(icon.Name, icons, files);
            bool folder = sure?.Folder ?? possible.Any(f => f.Folder);
            if (!Matches(terms, [icon.Name, folder ? FolderWords : null, .. possible.Select(f => f.Name)])) continue;
            if (!ShowsAt(host, Pixel(icon.Bounds), windows)) { covered++; continue; }
            matches.Add(new("desktop_icon", icon.Name, icon.Bounds, sure?.Name, sure?.Folder == true, sure?.Folder == true ? "檔案資料夾" : null, Source: host,
                Files: sure == null && possible.Count > 0 ? possible.Select(f => f.Name).ToArray() : null, Path: sure?.FullName));
        }
        return covered;
    }

    // Names and bounds of the desktop icons in one cross-process round trip instead of two per icon.
    internal static List<(string Name, Rect Bounds)> DesktopIcons()
    {
        var list = CompanionNative.DesktopListView();
        if (list == IntPtr.Zero) throw new InvalidOperationException("找不到桌面圖示，桌面圖示可能已隱藏。");
        var items = new List<(string Name, Rect Bounds)>();
        try
        {
            var cache = new CacheRequest();
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            using (cache.Activate())
                foreach (AutomationElement element in AutomationElement.FromHandle(list).FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
                {
                    var name = (element.Cached.Name ?? "").Trim();
                    var bounds = element.Cached.BoundingRectangle;
                    if (name.Length > 0 && !bounds.IsEmpty && bounds.Width > 0) items.Add((name, bounds));
                }
        }
        // UIA_E_TIMEOUT reaches managed code as a TimeoutException.
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException or TimeoutException)
        { throw new InvalidOperationException("無法讀取桌面圖示：" + error.Message); }
        return items;
    }

    // The user's and the public desktop folders; desktop.ini and its kind (hidden system files) never show.
    internal static List<DesktopFile> DesktopFiles()
    {
        var files = new List<DesktopFile>();
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (path.Length == 0 || !Directory.Exists(path)) continue;
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    bool hidden = entry.Attributes.HasFlag(FileAttributes.Hidden);
                    if (!hidden || !entry.Attributes.HasFlag(FileAttributes.System)) files.Add(new(entry.Name, entry is DirectoryInfo, hidden, entry.FullName));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return files;
    }

    // The files an icon may show, and the one it does show when there is no doubt: 報告 next to both 報告.docx and
    // 報告.pdf may be either, so a search for pdf finds both icons and says it cannot tell which is which.
    private static (DesktopFile? Sure, List<DesktopFile> Possible) FilesOf(string shown, List<(string Name, Rect Bounds)> icons, List<DesktopFile> files)
    {
        var candidates = files.Where(f => Same(f.Name, shown) || !f.Folder && Same(Path.GetFileNameWithoutExtension(f.Name), shown)).ToList();
        if (candidates.Any(f => !f.Hidden)) candidates = candidates.Where(f => !f.Hidden).ToList();
        return (candidates.Count == 1 && icons.Count(i => Same(i.Name, shown)) == 1 ? candidates[0] : null, candidates);
    }

    // How many matches other windows covered.
    private static int FindInWindows(IReadOnlyList<string> terms, List<Shown> windows, List<Match> matches, List<string> names, List<string> unfinished, CancellationToken token)
    {
        var processes = new Dictionary<uint, string>();
        var folders = windows.Any(w => w.Class == ExplorerClass) ? ExplorerFolders(windows) : new Dictionary<IntPtr, string>();
        var searched = new List<(Shown Window, string Label)>();
        foreach (var window in windows)
        {
            if (CompanionNative.IsDesktopWindow(window.Handle) || !Samples(window.Bounds).Any(p => ShowsAt(window.Handle, p, windows))) continue;
            var label = window.Title.Length > 0 ? Short(window.Title, 60) : window.Class is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" ? "工作列" : ProcessName(window.Process, processes);
            searched.Add((window, label));
            if (window.Title.Length == 0) continue;
            names.Add(window.Title);
            if (Matches(terms, window.Title))
                matches.Add(new("window", window.Title, window.Bounds, Process: ProcessName(window.Process, processes), Source: window.Handle, OpenFolder: folders.GetValueOrDefault(window.Handle)));
        }
        // One UI Automation search per window, side by side.
        var searches = searched.Select(s => (s.Window, s.Label, Task: Task.Run(() => Elements(s.Window.Handle)))).ToList();
        Task.WhenAny(Task.WhenAll(searches.Select(s => s.Task)), Task.Delay(ElementBudget, token)).Wait(token);
        token.ThrowIfCancellationRequested();
        int covered = 0;
        foreach (var (window, label, task) in searches)
        {
            if (task.Status != TaskStatus.RanToCompletion || task.Result == null) { unfinished.Add(label); continue; }
            foreach (var element in task.Result)
            {
                if (element.Name.Length <= 60) names.Add(element.Name);
                if (!Matches(terms, element.Name, element.ItemType)) continue;
                if (ShowsAt(window.Handle, Pixel(element.Bounds), windows))
                    matches.Add(element with { Window = label, Source = window.Handle, OpenFolder = folders.GetValueOrDefault(window.Handle) });
                else covered++;
            }
        }
        return covered;
    }

    private const string ExplorerClass = "CabinetWClass";

    // The folder each File Explorer window shows, from the Shell's own list of its windows. A window with tabs is
    // listed once per tab under one handle: the tab it shows is the one its title names. Folders that are not on a
    // disk (This PC, Recycle Bin) are left out. The Shell objects want an STA thread; a Shell that does not answer
    // within a few seconds only costs the paths.
    // get_context: the File Explorer windows on screen, the one in front first, with the folder each shows.
    internal static IReadOnlyList<(string Title, string Folder)> OpenFolders()
    {
        var windows = TopWindows().Where(w => w.Class == ExplorerClass).ToList();
        if (windows.Count == 0) return [];
        var folders = ExplorerFolders(windows);
        return windows.Where(w => folders.ContainsKey(w.Handle)).Select(w => (w.Title, folders[w.Handle])).ToList();
    }

    private static Dictionary<IntPtr, string> ExplorerFolders(List<Shown> windows)
    {
        var tabs = new List<(IntPtr Handle, string Name, string Path)>();
        var thread = new Thread(() =>
        {
            try
            {
                if (Type.GetTypeFromProgID("Shell.Application") is not { } type) return;
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic list = shell.Windows();
                    for (int i = 0, count = (int)list.Count; i < count; i++)
                        try
                        {
                            dynamic tab = list.Item(i);
                            if (tab == null) continue;
                            string url = (string)(tab.LocationURL ?? "");
                            if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                            var entry = (new IntPtr((long)tab.HWND), (string)(tab.LocationName ?? ""), new Uri(url).LocalPath);
                            lock (tabs) tabs.Add(entry);
                        }
                        catch (Exception error) when (error is COMException or InvalidCastException or UriFormatException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(3));
        var folders = new Dictionary<IntPtr, string>();
        lock (tabs)
            foreach (var group in tabs.GroupBy(t => t.Handle))
            {
                var title = windows.FirstOrDefault(w => w.Handle == group.Key)?.Title ?? "";
                var shown = group.Count() == 1 ? group.First() : group.FirstOrDefault(t => t.Name.Length > 0 && title.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase));
                if (shown.Path != null) folders[group.Key] = shown.Path;
            }
        return folders;
    }

    // Named elements of one window that are not scrolled out of view; null when the window cannot be read
    // (it closed meanwhile, or runs elevated).
    private static List<Match>? Elements(IntPtr window)
    {
        using var dpi = new CompanionScreenCapture.CaptureDpiScope();
        try
        {
            var root = AutomationElement.FromHandle(window);
            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.LocalizedControlTypeProperty);
            cache.Add(AutomationElement.ItemTypeProperty);
            var found = new List<Match>();
            using (cache.Activate())
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsOffscreenProperty, false)))
                {
                    var name = Normalize(element.Cached.Name);
                    var bounds = element.Cached.BoundingRectangle;
                    if (name.Length == 0 || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) continue;
                    // Explorer gives files their kind (Microsoft Word 文件); other elements say what control they are,
                    // which is shown but not searched (文件 is also what every web page is).
                    var itemType = Normalize(element.Cached.ItemType);
                    found.Add(new("element", Short(name, 200), bounds, Type: itemType.Length > 0 ? itemType : Normalize(element.Cached.LocalizedControlType),
                        ItemType: itemType.Length > 0 ? itemType : null));
                }
            return found;
        }
        catch
        {
            return null;
        }
    }

    // Five by five points across a window plus a ring just inside its edges, so a strip left showing (a tab row
    // under a window in front) still counts: one point showing is enough.
    private static IEnumerable<CompanionNative.Point> Samples(Rect bounds)
    {
        static IEnumerable<double> Across(double start, double size) =>
            new[] { Math.Min(16, size / 2) }.Concat(Enumerable.Range(0, 5).Select(i => size * (i + .5) / 5)).Append(Math.Max(size - 16, size / 2)).Select(d => start + d);
        foreach (var y in Across(bounds.Top, bounds.Height))
            foreach (var x in Across(bounds.Left, bounds.Width))
                yield return new() { X = (int)x, Y = (int)y };
    }

    internal static bool Matches(IReadOnlyList<string> terms, params string?[] texts) =>
        terms.Any(term => texts.Any(text => text != null && Contains(text, term)));

    // Ignoring case. A term's letters start and end on a word's edge, so tar is not found in Star nor doc in Docker
    // (a camelCase hump counts: Report in MyReportFinal), and its digits start on a number's edge (2026 finds
    // 20260911, 005 does not find PC2005). Chinese has no word edges and matches anywhere.
    internal static bool Contains(string text, string term)
    {
        for (int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase))
            if (Edge(text, at, term[0], true) && Edge(text, at + term.Length, term[^1], false)) return true;
        return false;
    }

    private static bool Edge(string text, int at, char side, bool start)
    {
        if (at == 0 || at == text.Length || !char.IsAsciiLetterOrDigit(side)) return true;
        char before = text[at - 1], after = text[at];
        if (char.IsAsciiDigit(side)) return !start || !char.IsAsciiDigit(before);
        return !char.IsAsciiLetter(before) || !char.IsAsciiLetter(after) || char.IsAsciiLetterLower(before) && char.IsAsciiLetterUpper(after);
    }

    // Nested matches name one thing once: an Explorer item and its label, a window and its title bar or page.
    internal static List<Match> Distinct(IEnumerable<Match> matches)
    {
        // Only within one window: an item in a window in front is not the title of a larger one behind it.
        var kept = new List<Match>();
        foreach (var match in matches.OrderByDescending(m => m.Bounds.Width * m.Bounds.Height))
            if (!kept.Any(k => k.Source == match.Source && Rect.Inflate(k.Bounds, 2, 2).Contains(match.Bounds) && k.Name.Contains(match.Name, StringComparison.OrdinalIgnoreCase)))
                kept.Add(match);
        return kept;
    }

    // Top to bottom in rows, left to right within a row: the order the fish visits them and the answer lists them.
    internal static List<Match> ReadingOrder(IEnumerable<Match> matches)
    {
        var rows = new List<List<Match>>();
        foreach (var match in matches.OrderBy(m => Middle(m.Bounds).Y))
        {
            var first = rows.LastOrDefault()?[0];
            if (first != null && Middle(match.Bounds).Y - Middle(first.Bounds).Y <= Math.Max(4, Math.Min(first.Bounds.Height, match.Bounds.Height) / 2)) rows[^1].Add(match);
            else rows.Add([match]);
        }
        return rows.SelectMany(row => row.OrderBy(m => Middle(m.Bounds).X)).ToList();
    }

    // Names a slip or two away from a term, for an answer that found nothing (資源回收桶 for 資源回收筒).
    internal static List<string> Similar(IEnumerable<string> names, IReadOnlyList<string> terms)
    {
        static int Reach(string term) => Math.Max(1, term.Length / 3);
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Over: terms.Where(t => Math.Abs(n.Length - t.Length) <= Reach(t)).Select(t => EditDistance(n, t) - Reach(t)).DefaultIfEmpty(1).Min()))
            .Where(n => n.Over <= 0).OrderBy(n => n.Over).Take(8).Select(n => n.Name).ToList();
    }

    // Without the extension Explorer hides: a short tail of letters and digits after the last dot, with a letter in
    // it (docx, mp4, 7z). "Python 3.12" keeps its version number.
    internal static string Stem(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length - dot - 1 is < 1 or > 5) return name;
        var tail = name[(dot + 1)..];
        return tail.All(char.IsAsciiLetterOrDigit) && tail.Any(char.IsAsciiLetter) ? name[..dot] : name;
    }

    internal static int EditDistance(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = Math.Min(Math.Min(above + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return row[b.Length];
    }

    private static string ProcessName(uint id, Dictionary<uint, string> known)
    {
        if (known.TryGetValue(id, out var name)) return name;
        try { using var process = Process.GetProcessById((int)id); name = process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { name = "未知程式"; }
        return known[id] = name;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static Point Middle(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    private static CompanionNative.Point Pixel(Rect r) => new() { X = (int)Math.Round(r.X + r.Width / 2), Y = (int)Math.Round(r.Y + r.Height / 2) };
    private static string Normalize(string? text) => string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Short(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}

public partial class MainWindow
{
    // More than this many matches are asked about before the fish runs to them.
    private const int ManyMatches = 8;

    private async Task<object> FindOnScreenMcp(JsonElement args, CancellationToken token)
    {
        var terms = ControlTools.RequiredText(args, "query", 200).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        if (terms.Length == 0) throw new ArgumentException("query 要寫出要找的名稱。");
        var where = ControlTools.OptionalChoice(args, "where", "all", "desktop", "windows");
        double limit = ControlTools.OptionalNumber(args, "limit", 1, 50) ?? 20;
        if (limit != Math.Floor(limit)) throw new ArgumentException("limit 必須是整數。");
        CompanionScreenSearch.Result found;
        try { found = await Task.Run(() => CompanionScreenSearch.Find(terms, where, token), token).WaitAsync(TimeSpan.FromSeconds(20), token); }
        catch (TimeoutException) { throw new TimeoutException("讀取畫面內容逾時，請稍後再試。"); }
        // Only the fields a match has, so a long list stays short; x and y are centres, as walk_to takes them.
        var matches = found.Matches.Take((int)limit).Select((m, i) =>
        {
            var row = new Dictionary<string, object?> { ["n"] = i + 1, ["kind"] = m.Kind, ["name"] = m.Name };
            if (m.File != null) row["file"] = m.File;
            if (m.Files != null) row["possibleFiles"] = m.Files; // Extensions hidden and the name shared: one of these.
            if (m.Path != null) row["path"] = m.Path;
            if (m.OpenFolder != null) row["openFolder"] = m.OpenFolder;
            if (m.Folder) row["folder"] = true;
            if (!string.IsNullOrEmpty(m.Type)) row["type"] = m.Type;
            if (m.Window != null) row["window"] = m.Window;
            if (m.Process != null) row["process"] = m.Process;
            row["x"] = (int)Math.Round(m.Bounds.X + m.Bounds.Width / 2); row["y"] = (int)Math.Round(m.Bounds.Y + m.Bounds.Height / 2);
            row["width"] = (int)Math.Round(m.Bounds.Width); row["height"] = (int)Math.Round(m.Bounds.Height);
            if (!m.Walkable) row["walkable"] = false;
            return row;
        }).ToArray();
        var result = new Dictionary<string, object?> { ["query"] = string.Join("|", terms), ["total"] = found.Matches.Count, ["returned"] = matches.Length, ["matches"] = matches };
        if (found.CoveredIcons > 0) result["coveredDesktopIcons"] = found.CoveredIcons;
        if (found.CoveredInWindows > 0) result["coveredInWindows"] = found.CoveredInWindows;
        if (found.Unfinished.Count > 0) result["unfinished"] = found.Unfinished;
        if (found.Similar.Count > 0) result["similar"] = found.Similar;
        // Said where the agent decides: left to its instructions, a model ran a 35-stop tour and kept capturing a
        // desktop that windows covered.
        if (found.Matches.Count > ManyMatches)
            result["note"] = $"符合的有 {found.Matches.Count} 個，全部跑一遍要好幾分鐘。先告訴使用者總數和幾個名稱，問要跑哪些，等使用者回答再跑。";
        else if (found.Matches.Count == 0 && found.CoveredIcons > 0)
            result["note"] = $"有 {found.CoveredIcons} 個符合的桌面圖示被視窗擋住，現在看不到。告訴使用者，請他先顯示桌面（例如按 Win+D）再找一次；不要改用截圖找。";
        else if (found.Matches.Count == 0 && found.CoveredInWindows > 0)
            result["note"] = $"有 {found.CoveredInWindows} 個符合的項目被前面的視窗擋住，現在看不到。告訴使用者是被擋住了，不要改用截圖找。";
        else if (found.Unfinished.Count > 0)
            result["note"] = $"這些還沒讀完：{string.Join("、", found.Unfinished)}。先再呼叫一次 find_on_screen（通常第二次就讀得到）；還是沒讀完，回答時告訴使用者這些沒有找。";
        else if (matches.Any(m => m.ContainsKey("possibleFiles")))
            result["note"] = "有些桌面圖示的副檔名被隱藏、名稱又相同，possibleFiles 列出它可能是哪些檔案，分不出是哪一個；回答時要說明。";
        return result;
    }
}
#endif

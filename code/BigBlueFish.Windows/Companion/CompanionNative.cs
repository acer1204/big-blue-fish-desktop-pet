// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace BigBlueFish.Windows;
internal static class CompanionNative
{
    internal static bool? TestPowerOverride;
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref Input i);
    [DllImport("kernel32.dll")]
    internal static extern bool GetSystemPowerStatus(out Power p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetStyle(IntPtr h, int n);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetStyle(IntPtr h, int n, IntPtr v);
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Size, Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Power
    {
        public byte AC, Battery, Percent, Reserved;
        public uint Life, FullLife;
    }

    internal static bool OnAC => TestPowerOverride ?? (GetSystemPowerStatus(out var p) && p.AC == 1);

    internal static double IdleSeconds
    {
        get
        {
            var i = new Input
            {
                Size = (uint)Marshal.SizeOf<Input>()
            };
            return GetLastInputInfo(ref i) ? unchecked((uint)Environment.TickCount - i.Time) / 1000.0 : 0;
        }
    }

    internal static string ForegroundContext(IntPtr preferredHandle = default)
    {
        var h = preferredHandle == IntPtr.Zero ? GetForegroundWindow() : preferredHandle;
        if (h == IntPtr.Zero)
            return "前臺視窗：Windows 桌面\n目前可見活動線索：桌面沒有活動視窗標題。";
        GetWindowThreadProcessId(h, out var id);
        if (id == Environment.ProcessId)
            return "使用者正在檢視桌寵";
        var s = new StringBuilder(256);
        GetWindowText(h, s, 256);
        var className = new StringBuilder(128);
        GetClassName(h, className, className.Capacity);
        string name = "未知應用";
        try
        {
            name = Process.GetProcessById((int)id).ProcessName;
        }
        catch
        {
        }

        var snapshot = ReadAccessibilitySnapshot(h);
        return BuildForegroundContext(name, s.ToString(), className.ToString(), snapshot.Name, snapshot.ControlType, snapshot.Descendants);
    }

    internal static CompanionWindowImage? ForegroundWindowImage(IntPtr preferredHandle = default, int maxDimension = 768)
    {
        var handle = preferredHandle == IntPtr.Zero ? GetForegroundWindow() : preferredHandle;
        if (handle == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(handle, out var id);
        return id == Environment.ProcessId ? null : CompanionScreenCapture.Capture(handle, maxDimension);
    }

    // Supplemental identity only; OCR screen content is the primary evidence in OCR mode.
    // Avoid the accessibility traversal, which can be noisy or slow in custom-rendered apps.
    internal static string ForegroundIdentity(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var id);
        var title = new StringBuilder(512);
        GetWindowText(handle, title, title.Capacity);
        var category = new StringBuilder(128);
        GetClassName(handle, category, category.Capacity);
        if (IsDesktopWindow(handle))
            return "目前前臺畫面：Windows 桌面，桌布與捷徑圖示。";
        string name = "未知應用";
        try { using var process = Process.GetProcessById((int)id); name = process.ProcessName; } catch { }
        return "前臺輔助資訊（以螢幕文字為主要依據）：\n程式：" + name + "\n標題：" + title;
    }

    internal static bool IsDesktopWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        if (handle == GetShellWindow()) return true;
        var category = new StringBuilder(128);
        GetClassName(handle, category, category.Capacity);
        if (category.ToString() is not ("Progman" or "WorkerW")) return false;
        GetWindowThreadProcessId(handle, out var id);
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static bool IsVisibleCaptureTarget(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle))
            return false;
        return DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    internal static bool IsExternalForegroundCandidate(IntPtr handle)
    {
        if (!IsVisibleCaptureTarget(handle))
            return false;
        GetWindowThreadProcessId(handle, out var id);
        if (id == Environment.ProcessId)
            return false;
        var className = new StringBuilder(128);
        GetClassName(handle, className, className.Capacity);
        return className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "#32768");
    }

    // The pet/menu may own focus at the click. A remembered window is usable only
    // while it still belongs to the visible desktop; PrintWindow can render a
    // minimized application even though it is no longer the user's foreground.
    internal static IntPtr ResolveSpeechForeground(IntPtr current, IntPtr remembered)
    {
        if (current == IntPtr.Zero)
            return IntPtr.Zero;
        if (IsExternalForegroundCandidate(current))
            return current;
        if (IsExternalForegroundCandidate(remembered))
            return remembered;
        var desktop = GetShellWindow();
        return IsExternalForegroundCandidate(desktop) ? desktop : IntPtr.Zero;
    }

    internal static string BuildForegroundContext(string processName, string title, string className, string accessibleName, string controlType, IReadOnlyList<string> descendants)
    {
        var lines = new List<string>
        {
            "前臺應用：" + Limit(processName, 120),
            "視窗標題：" + (Limit(title, 220) is { Length: > 0 } safeTitle ? safeTitle : "（沒有視窗標題）"),
            "視窗類別：" + (Limit(className, 120) is { Length: > 0 } safeClass ? safeClass : "（未知）"),
            "無障礙名稱：" + (Limit(accessibleName, 180) is { Length: > 0 } safeName ? safeName : "（沒有提供）"),
            "無障礙控制元件型別：" + (Limit(controlType, 120) is { Length: > 0 } safeType ? safeType : "（未知）")
        };
        var clues = descendants
            .Select(x => Normalize(x, 120))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        lines.Add(clues.Length == 0
            ? "可見介面線索：沒有讀取到子控制元件名稱。"
            : "可見介面線索：\n" + string.Join("\n", clues.Select(x => "- " + x)));
        return string.Join("\n", lines);
    }

    private static (string Name, string ControlType, IReadOnlyList<string> Descendants) ReadAccessibilitySnapshot(IntPtr handle)
    {
        try
        {
            var root = AutomationElement.FromHandle(handle);
            var current = root.Current;
            var descendants = new List<string>();
            var walker = TreeWalker.ControlViewWalker;
            CollectAccessibilityChildren(walker, root, descendants, 0, 3, 32);
            return (current.Name ?? "", current.ControlType?.ProgrammaticName ?? "", descendants);
        }
        catch
        {
            return ("", "", Array.Empty<string>());
        }
    }

    private static void CollectAccessibilityChildren(TreeWalker walker, AutomationElement parent, List<string> values, int depth, int maxDepth, int maxItems)
    {
        if (depth >= maxDepth || values.Count >= maxItems)
            return;
        AutomationElement? child = null;
        try { child = walker.GetFirstChild(parent); } catch { return; }
        while (child != null && values.Count < maxItems)
        {
            try
            {
                var current = child.Current;
                var name = Normalize(current.Name, 120);
                var type = current.ControlType?.ProgrammaticName ?? "";
                if (name.Length > 0)
                    values.Add(type.Length > 0 ? type + "：" + name : name);
                CollectAccessibilityChildren(walker, child, values, depth + 1, maxDepth, maxItems);
                child = walker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }
    }

    private static string Normalize(string value, int maxLength)
    {
        var normalized = string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private static string Limit(string value, int maxLength) => Normalize(value, maxLength);

    internal static bool IsFullscreen()
    {
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out var id);
        if (id == Environment.ProcessId || !GetWindowRect(h, out var r))
            return false;
        try
        {
            var n = Process.GetProcessById((int)id).ProcessName;
            if (n == "explorer")
                return false;
        }
        catch
        {
        }

        var b = System.Windows.Forms.Screen.FromHandle(h).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}

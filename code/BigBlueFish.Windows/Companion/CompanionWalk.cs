#if COMPANION_SAFE
namespace BigBlueFish.Windows;

// walk_to is an MCP tool; the Safe edition keeps only the hooks the shared animation code calls.
public partial class MainWindow
{
    private bool Walking => false;
    private bool Touring => false;
    private void CancelWalk(string reason) { }
    private void PressWalk() { }
    private bool ContinueWalk() => false;
    private void PreloadWalk() { }
}
#else
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using BigBlueFish.Control;

namespace BigBlueFish.Windows;

// walk_to: the fish runs (or floats) to a point on the screen, stops beside it without covering it and stays there.
// Screen targets are physical pixels of the virtual desktop. This process is System-DPI-aware, so Left/Top form one
// linear DIP space; a physical point is converted once and the path is a straight line in DIP.
public partial class MainWindow
{
    private const string RunClip = "原地左转奔跑", FloatClip = "原地漂浮踏步";
    // Run clip: 1.4-2.0 s turns from the front to the left, 2.000-3.958 s loops seamlessly, 7.5-10 s stops and turns
    // back to the front with a smile (the trip and fall in between is skipped). The float clip steps in place facing front.
    private const double RunTurn = 1.4, RunLoop = 2.0, RunLoopEnd = 3.958, RunStop = 7.5, RunStopEnd = 10, FloatLoop = .5, FloatLoopEnd = 9.42;
    private const double WalkGap = 8, WalkRamp = .15;

    private sealed class WalkPlan
    {
        internal Point From, To, Last;      // window Left/Top in DIP; Last is where the previous frame left it
        internal Rect Target, Pixels;       // the target in DIP and in physical pixels
        internal string? DesktopItem, ArriveAction, Say, Cancelled;
        internal string Motion = "none", Side = "right";
        internal bool Overlaps, Mirrored, FaceMirrored, Arrived;
        internal double Distance, Lead, Duration, Rate = 1, Scale;
        internal readonly Stopwatch Clock = new();
        internal readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record WalkTarget(Rect Pixels, CompanionNative.Rect Work, string? DesktopItem);
    private sealed record WalkStop(Rect Pixels, string? Item, string? Label);
    // walk_to with stops: one walk after another, with a pause at each stop.
    private sealed class WalkTour(int count)
    {
        internal readonly int Count = count;
        internal int Reached;
        internal string? Cancelled;
        internal bool Pressed;           // held by the mouse between two stops; the release names the reason
        internal readonly List<object> Legs = new();
        internal readonly TaskCompletionSource<object> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private const int MaxStops = 20, TourLimitSeconds = 280;

    private WalkPlan? walk;
    private WalkTour? tour;
    // The bubble shows a walk's own text (a stop of a tour); the Hermes bridge keeps its progress line off it.
    private bool walkBubble;
    // Stopped by a mouse press; the release (click) or the first drag movement names the reason.
    private WalkPlan? walkPressed;
    private object? lastWalk;
    private bool walkFrames;
    // Where the last walk left the fish; the double-click input keeps off that target while the fish stays put.
    private (Rect Target, Point At, double Scale)? walkAvoid;
    private readonly List<(int Id, System.Drawing.Rectangle Source, int Width, int Height)> mcpCaptures = new();
    private int mcpCaptureCount;
    private bool Walking => walk != null;
    private bool Touring => tour != null;

    private async Task<object> WalkToMcp(JsonElement args, CancellationToken token)
    {
        double? seenWidth = ControlTools.OptionalNumber(args, "image_width", 1, 100_000), seenHeight = ControlTools.OptionalNumber(args, "image_height", 1, 100_000);
        var captureId = ControlTools.OptionalNumber(args, "capture_id", 1, int.MaxValue);
        var space = ControlTools.OptionalChoice(args, "space", "screen", "capture");
        // Screenshot details mean screenshot pixels even with space left out. Next to an explicit space=screen they
        // show the two were mixed up, which would otherwise send the fish somewhere else without a word.
        if (captureId != null || seenWidth != null || seenHeight != null)
        {
            if (space == "screen" && args.TryGetProperty("space", out var given) && given.ValueKind == JsonValueKind.String)
                throw new ArgumentException("capture_id、image_width、image_height 只用在截圖上的座標，這時 space 要填 \"capture\"；螢幕實體畫素（例如 find_on_screen 的結果）不要填這三個。");
            space = "capture";
        }
        var side = ControlTools.OptionalChoice(args, "side", "auto", "left", "right", "above", "below");
        var arriveAction = ControlTools.OptionalText(args, "arrive_action", 200);
        var say = ControlTools.OptionalText(args, "say");
        // A label next to stops instead of inside each one is taken as meant for every stop.
        var label = ControlTools.OptionalText(args, "label", 80);
        bool wait = ControlTools.OptionalBool(args, "wait", true);
        double pause = ControlTools.OptionalNumber(args, "pause_seconds", 0, 10) ?? 2;
        if (captureId is { } wanted && wanted != Math.Floor(wanted)) throw new ArgumentException("capture_id 必須是整數。");
        if (arriveAction != null) arriveAction = ResolveAction(arriveAction) ?? throw new ArgumentException(UnknownAction("arrive_action", arriveAction));
        var stops = new List<WalkStop>();
        bool touring = args.TryGetProperty("stops", out var list) && list.ValueKind != JsonValueKind.Null;
        if (touring)
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxStops) throw new ArgumentException($"stops 必須是 1–{MaxStops} 個目標的陣列。");
            foreach (var stop in list.EnumerateArray())
                try
                {
                    if (stop.ValueKind != JsonValueKind.Object) throw new ArgumentException("每一站必須是物件。");
                    stops.Add(ReadStop(stop, space, captureId, seenWidth, seenHeight, ControlTools.OptionalText(stop, "label", 80) ?? label));
                }
                catch (ArgumentException error) { throw new ArgumentException($"stops 第 {stops.Count + 1} 站：{error.Message}"); }
        }
        else stops.Add(ReadStop(args, space, captureId, seenWidth, seenHeight, null));
        EnsureWalkable();
        List<WalkTarget> targets;
        try { targets = await Task.Run(() => ResolveWalkTargets(stops), token).WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch (TimeoutException) { throw new TimeoutException("讀取桌面圖示逾時，請稍後再試。"); }
        token.ThrowIfCancellationRequested();
        if (!preferences.McpEnabled) throw new OperationCanceledException();
        // A leg takes at most about 8.5 s; past Hermes' 300 s tool timeout the agent would see a failure mid-tour.
        if (touring && wait && targets.Count * 8.5 + (targets.Count - 1) * pause > TourLimitSeconds)
            throw new ArgumentException($"這趟最久可能要 {targets.Count * 8.5 + (targets.Count - 1) * pause:0} 秒，超過 {TourLimitSeconds} 秒。請減少站數或縮短 pause_seconds，或分成幾趟。");
        EnsureWalkable(); // The pet may have been hidden or grabbed while the targets were resolved.
        if (!touring)
        {
            var plan = StartWalk(targets[0], side, arriveAction, say ?? label);
            if (plan.Done.Task.IsCompleted) return await plan.Done.Task; // Already standing there.
            if (!wait) return WalkReport(plan, true);
            using (token.Register(() => Dispatcher.BeginInvoke(() => { if (walk == plan || walkPressed == plan) CancelWalk("request_cancelled"); })))
                return await plan.Done.Task;
        }
        StopTour("replaced");
        var trip = new WalkTour(targets.Count);
        tour = trip;
        _ = RunTour(trip, targets, stops.Select(s => s.Label).ToArray(), side, arriveAction, pause);
        if (!wait) return TourReport(trip, true);
        using (token.Register(() => Dispatcher.BeginInvoke(() => { if (tour == trip) CancelWalk("request_cancelled"); })))
            return await trip.Done.Task;
    }

    // A target from walk_to's own fields or from one of its stops; space, capture and the seen size are shared.
    private WalkStop ReadStop(JsonElement at, string space, double? captureId, double? seenWidth, double? seenHeight, string? label)
    {
        var item = ControlTools.OptionalText(at, "desktop_item", 260)?.Trim();
        if (!string.IsNullOrEmpty(item)) return new(Rect.Empty, item, label);
        double? x = ControlTools.OptionalNumber(at, "x", -1_000_000, 1_000_000), y = ControlTools.OptionalNumber(at, "y", -1_000_000, 1_000_000);
        double width = ControlTools.OptionalNumber(at, "width", 0, 100_000) ?? 0, height = ControlTools.OptionalNumber(at, "height", 0, 100_000) ?? 0;
        if (x == null || y == null) throw new ArgumentException("請提供目標中心 x 與 y，或 desktop_item。");
        if (space == "screen") return new(new Rect(x.Value - width / 2, y.Value - height / 2, width, height), null, label);
        var capture = captureId is { } id ? mcpCaptures.LastOrDefault(c => c.Id == (int)id) : mcpCaptures.LastOrDefault();
        if (capture.Id == 0) throw new ArgumentException(captureId == null ? "還沒有截圖可換算，請先呼叫 capture_screen 或 capture_foreground。" : "找不到這個 capture_id，只保留最近 8 次截圖。");
        return new(CapturePixels(capture, seenWidth, seenHeight, x.Value, y.Value, width, height), null, label);
    }

    // An agent may have looked at a smaller copy (Hermes shrinks screenshots to its vision budget): its pixels first go
    // back onto the image this capture returned, one side alone keeping the aspect, then onto the physical rectangle
    // that image showed.
    private static Rect CapturePixels((int Id, System.Drawing.Rectangle Source, int Width, int Height) capture, double? seenWidth, double? seenHeight,
        double x, double y, double width, double height)
    {
        double fx = seenWidth is { } vw ? capture.Width / vw : seenHeight is { } vh0 ? capture.Height / vh0 : 1;
        double fy = seenHeight is { } vh ? capture.Height / vh : fx;
        double imageWidth = capture.Width / fx, imageHeight = capture.Height / fy;
        // A point off the image it was read from has usually been multiplied back to full size by hand as well.
        if (x < -1 || y < -1 || x > imageWidth + 1 || y > imageHeight + 1)
            throw new ArgumentException($"座標 ({x:0.#}, {y:0.#}) 不在 {imageWidth:0.#}x{imageHeight:0.#} 的圖內。x、y 填你看到的那張圖上的位置，不要自己乘回原尺寸；看到的是縮小的圖時，image_width、image_height 填縮小後的寬高。");
        x *= fx; y *= fy; width *= fx; height *= fy;
        double sx = capture.Source.Width / (double)capture.Width, sy = capture.Source.Height / (double)capture.Height;
        return new Rect(capture.Source.X + (x - width / 2) * sx, capture.Source.Y + (y - height / 2) * sy, width * sx, height * sy);
    }

    private void EnsureWalkable()
    {
        if (!companionReady || !IsVisible || hiddenByUser || fullscreenHidden || dragging || exitAnimation || PresentationSource.FromVisual(this) == null)
            throw new InvalidOperationException("魚魚目前隱藏、拖曳中或尚未就緒，無法移動。");
    }

    // Off the UI thread, per-monitor aware, so window, monitor and UI Automation rectangles are all physical pixels.
    // Every stop is checked before the fish moves, and the desktop icons are read once for all of them.
    private static List<WalkTarget> ResolveWalkTargets(IReadOnlyList<WalkStop> stops)
    {
        using var dpi = new CompanionScreenCapture.CaptureDpiScope();
        List<(string Name, Rect Bounds)>? icons = null;
        string[]? files = null;
        var targets = new List<WalkTarget>();
        foreach (var stop in stops)
            try
            {
                var (pixels, item) = (stop.Pixels, stop.Item);
                if (item != null)
                {
                    icons ??= CompanionScreenSearch.DesktopIcons();
                    files ??= CompanionScreenSearch.DesktopFiles().Where(f => !f.Folder).Select(f => f.Name).ToArray();
                    (pixels, item) = MatchDesktopItem(icons, item, files);
                }
                var center = new CompanionNative.Point { X = (int)Math.Round(pixels.X + pixels.Width / 2), Y = (int)Math.Round(pixels.Y + pixels.Height / 2) };
                if (!CompanionNative.MonitorWorkArea(center, out var work)) throw new ArgumentException("目標不在任何顯示器上，請確認座標是 Windows 實體畫素。");
                // This process is System-DPI-aware: a monitor at another scale is stretched by Windows, and the
                // physical-to-DIP conversion through the fish's own window would land in the wrong place there.
                if (CompanionNative.MonitorDpi(center) is { } scale && scale != CompanionNative.GetDpiForSystem())
                    throw new ArgumentException("目前不支援移動到縮放比例和主螢幕不同的螢幕。");
                targets.Add(new(pixels, work, item));
            }
            catch (ArgumentException error) when (stops.Count > 1) { throw new ArgumentException($"stops 第 {targets.Count + 1} 站：{error.Message}"); }
        return targets;
    }

    // Ignoring case: the exact name, then the same name with or without its extension, then the one
    // name containing the query, then a one-character slip (資源回收桶 for 資源回收筒) that no other name
    // matches as well, so a typo costs no second call. Several names at one step are listed instead of
    // picking whichever UI Automation happens to give first (報告1 against 報告10.docx and 報告1.pdf).
    // files: the desktop's file names, when known, so an icon shown without its extension is taken for
    // 報告1.docx only if that file is there (not 報告1.pdf, nor a folder 報告1).
    private static (Rect Pixels, string Name) MatchDesktopItem(IReadOnlyList<(string Name, Rect Bounds)> items, string query, IReadOnlyCollection<string>? files)
    {
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        (Rect, string)? One(List<(string Name, Rect Bounds)> found, string several)
        {
            if (found.Count == 0) return null;
            var names = found.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > 1) throw new ArgumentException($"有 {names.Length} 個桌面圖示的名稱{several}「{query}」，請用完整名稱指定其中一個：{string.Join("、", names.Take(12))}");
            // Extensions hidden: 報告.docx and 報告.pdf both show as 報告, and nothing on the screen tells them apart.
            if (found.Count > 1)
                throw new ArgumentException($"桌面上有 {found.Count} 個圖示都叫「{names[0]}」（副檔名被隱藏），從名稱分不出是哪一個。它們在 "
                    + string.Join("、", found.Select(f => $"x={f.Bounds.X + f.Bounds.Width / 2:0}, y={f.Bounds.Y + f.Bounds.Height / 2:0}, width={f.Bounds.Width:0}, height={f.Bounds.Height:0}"))
                    + "。可以用 stops 每個都跑，或用 capture_screen 從圖示分辨。");
            return (found[0].Bounds, found[0].Name);
        }
        // The extension one way only: 報告1 finds 報告1.docx and 報告1.docx finds an icon shown as 報告1, but 報告1.pdf never
        // stands in for 報告1.docx, and a version number is no extension ("Python 3" is not "Python 3.12").
        var bare = CompanionScreenSearch.Stem(query);
        bool hiddenThere = bare == query || files == null || files.Contains(query, StringComparer.OrdinalIgnoreCase);
        if (One(items.Where(i => Same(i.Name, query)).ToList(), "是") is { } exact) return exact;
        if (One(items.Where(i => Same(CompanionScreenSearch.Stem(i.Name), query) || hiddenThere && Same(i.Name, bare)).ToList(), "去掉副檔名後都是") is { } stem) return stem;
        // A number in the query is a whole number in the name: 報告1 is not part of 報告10.docx.
        if (One(items.Where(i => WholeNumbers(i.Name, query)).ToList(), "包含") is { } part) return part;
        // Two characters or fewer, one wrong one is half the name: too much of a guess. Nor is a different
        // number a slip: 報告2 is another file than 報告1.
        if (query.EnumerateRunes().Count() >= 3
            && items.Where(i => CompanionScreenSearch.EditDistance(i.Name, query) == 1 && Digits(i.Name) == Digits(query)).ToList() is { Count: > 0 } close
            && close.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return One(close, "接近")!.Value;
        if (items.Count == 0) throw new ArgumentException("桌面上沒有可辨識的圖示。");
        // Only names close enough to be what was meant; listing unrelated ones invites a wrong guess.
        int reach = Math.Max(2, query.Length / 3);
        var near = items.Select(i => i.Name).Distinct().Select(n => (Name: n, Distance: CompanionScreenSearch.EditDistance(n, query))).Where(n => n.Distance <= reach)
            .OrderBy(n => n.Distance).Take(8).Select(n => n.Name).ToArray();
        throw new ArgumentException(near.Length == 0 ? $"桌面上沒有名為「{query}」或相近名稱的圖示。" : $"找不到名為「{query}」的桌面圖示。相近的名稱：{string.Join("、", near)}");
    }

    private static string Digits(string text) => new(text.Where(char.IsDigit).ToArray());

    private static bool WholeNumbers(string name, string query)
    {
        for (int at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = name.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + query.Length;
            if (!(char.IsDigit(query[0]) && at > 0 && char.IsDigit(name[at - 1])) && !(char.IsDigit(query[^1]) && end < name.Length && char.IsDigit(name[end]))) return true;
        }
        return false;
    }

    // Runs on the dispatcher. Whatever stops a walk (drag, click, interrupt_action, a new walk_to, hiding, settings)
    // ends the whole tour where the fish is, between two stops as well.
    private async Task RunTour(WalkTour trip, IReadOnlyList<WalkTarget> targets, IReadOnlyList<string?> labels, string side, string? arriveAction, double pause)
    {
        try
        {
            for (int i = 0; i < targets.Count && tour == trip; i++)
            {
                if (i > 0)
                {
                    var until = DateTime.UtcNow.AddSeconds(pause);
                    while (tour == trip && (trip.Pressed || DateTime.UtcNow < until)) await Task.Delay(100);
                    if (tour != trip) break;
                    EnsureWalkable();
                }
                var say = $"第 {i + 1} 個（共 {targets.Count} 個）" + (labels[i] is { } label ? "：" + label : "");
                var plan = StartWalk(targets[i], side, i == targets.Count - 1 ? arriveAction : null, say, trip);
                await plan.Done.Task;
                if (plan.Arrived) trip.Reached++;
                trip.Legs.Add(new { n = i + 1, label = labels[i], arrived = plan.Arrived, cancelled = plan.Cancelled, side = plan.Side, target = PixelBox(plan.Pixels), desktopItem = plan.DesktopItem });
                if (plan.Cancelled != null) trip.Cancelled ??= plan.Cancelled;
            }
        }
        catch (InvalidOperationException) { trip.Cancelled ??= "hidden"; } // Hidden or grabbed during a pause.
        finally
        {
            if (tour == trip) tour = null;
            var report = TourReport(trip);
            // The walk that replaced this tour reports for itself, possibly already.
            if (trip.Cancelled != "replaced") lastWalk = report;
            trip.Done.TrySetResult(report);
            // Clips queued during the tour waited for its end (PlayQueuedMcpAction); a last run leg still turning
            // round plays them when its stop clip ends.
            if (tour == null && walk == null && animationPhase is "Idle" or "PuppetIdle" && companionReady && !dragging && !hiddenByUser && !fullscreenHidden && !exitAnimation)
                PlayQueuedMcpAction();
        }
    }

    // The tour (if any) ends; its current walk, if one is under way, is stopped by the caller.
    private void StopTour(string reason)
    {
        if (tour is not { } trip) return;
        tour = null;
        trip.Cancelled ??= reason;
    }

    // note: a model that saw cancelled=click carried on with the rest, and one that saw hidden switched HideOnFullscreen off.
    private object TourReport(WalkTour trip, bool started = false) => new {
        stops = trip.Count, reached = trip.Reached, arrived = !started && trip.Cancelled == null && trip.Reached == trip.Count, cancelled = trip.Cancelled, started,
        note = trip.Cancelled switch {
            "click" or "drag" => "使用者點了或拖了魚魚，表示要牠停下來：不要再跑剩下的，直接回答跑到第幾個、還剩哪些。",
            "hidden" => "魚魚被藏起來了（使用者隱藏，或有程式全螢幕）：不要改魚魚的設定繞過，直接回答跑到第幾個、還剩哪些。",
            _ => null },
        legs = trip.Legs.ToArray(), Left = Math.Round(Left, 2), Top = Math.Round(Top, 2), petBounds = PetPhysicalBounds() };

    private object? TourSnapshot() => tour is { } trip ? new { stops = trip.Count, reached = trip.Reached } : null;

    private WalkPlan StartWalk(WalkTarget target, string side, string? arriveAction, string? say, WalkTour? trip = null)
    {
        if (trip == null) StopTour("replaced");
        CancelWalk("replaced"); CancelMovement();
        PreloadWalk();
        var body = CompanionEdgeLayout.PetBounds(this);
        var work = target.Work;
        var area = PhysicalToDip(new Rect(new Point(work.Left, work.Top), new Point(work.Right, work.Bottom)));
        var aim = PhysicalToDip(target.Pixels);
        // A bare point stands for about one desktop icon (76x82 DIP on this 225% 4K desktop).
        if (target.Pixels.Width < 1) aim = new Rect(aim.X + aim.Width / 2 - 40, aim.Y, 80, aim.Height);
        if (target.Pixels.Height < 1) aim = new Rect(aim.X, aim.Y + aim.Height / 2 - 40, aim.Width, 80);
        var plan = new WalkPlan { From = new(Left, Top), Last = new(Left, Top), Target = aim, Pixels = target.Pixels, DesktopItem = target.DesktopItem,
            ArriveAction = arriveAction, Say = say, Scale = preferences.Scale };
        (plan.To, plan.Side, plan.Overlaps) = PlanWalk(aim, area, body, side);
        var move = plan.To - plan.From;
        plan.Distance = move.Length;
        plan.Motion = plan.Distance < 12 ? "none" : Math.Abs(move.X) >= .5 * Math.Abs(move.Y) ? "run" : "float";
        plan.Mirrored = plan.Motion == "run" && move.X > 0; // The clips face left.
        // The user wants the double-click input gone while the fish runs (not when she stays put); a running
        // file picker would go down with it.
        if (plan.Motion != "none" && !mcpInputPicking) mcpInput?.Close();
        double bodyCenter = plan.To.X + body.Left + body.Width / 2, aimCenter = aim.Left + aim.Width / 2;
        plan.FaceMirrored = Math.Abs(aimCenter - bodyCenter) < body.Width / 2 ? plan.Mirrored : aimCenter > bodyCenter;
        walkAvoid = null;
        walk = plan;
        plan.Clock.Start();
        if (plan.Motion == "none") { Arrive(plan); return plan; }
        // About one and a half body lengths a second; a long way speeds up rather than taking more than 8 s,
        // and the stride follows the speed.
        double speed = 260 * preferences.Scale;
        plan.Duration = Math.Clamp(plan.Distance / speed, .6, 8);
        plan.Rate = 1.25 * Math.Clamp(plan.Distance / plan.Duration / speed, .8, 1.6);
        // Moving starts once the run clip has turned round.
        plan.Lead = plan.Motion == "run" ? (RunLoop - RunTurn) / plan.Rate : 0;
        PlayWalkSegment(plan, plan.Motion == "run" ? "WalkStart" : "WalkLoop");
        if (!walkFrames) { CompositionTarget.Rendering += WalkFrame; walkFrames = true; }
        return plan;
    }

    // Beside the target, inside its work area, never covering it: left or right (whichever is nearer), else below, else above.
    // A requested side is used when it fits. No ClampCompanion at the end: its 14 DIP edge snap could push the body onto the target.
    private (Point To, string Side, bool Overlaps) PlanWalk(Rect aim, Rect area, Rect body, string side)
    {
        double FitLeft(double v) => Math.Clamp(v, area.Left - body.Left, Math.Max(area.Left - body.Left, area.Right - body.Right));
        double FitTop(double v) => Math.Clamp(v, area.Top - body.Top, Math.Max(area.Top - body.Top, area.Bottom - body.Bottom));
        double beside = FitTop(aim.Top + aim.Height / 2 - body.Top - body.Height / 2), level = FitLeft(aim.Left + aim.Width / 2 - body.Left - body.Width / 2);
        var places = new (string Side, Point At)[] {
            ("left", new(aim.Left - WalkGap - body.Right, beside)), ("right", new(aim.Right + WalkGap - body.Left, beside)),
            ("below", new(level, aim.Bottom + WalkGap - body.Top)), ("above", new(level, aim.Top - WalkGap - body.Bottom)) };
        Rect Body(Point at) => new(at.X + body.Left, at.Y + body.Top, body.Width, body.Height);
        bool Inside(Rect r) => r.Left >= area.Left - .5 && r.Top >= area.Top - .5 && r.Right <= area.Right + .5 && r.Bottom <= area.Bottom + .5;
        double Covered(Rect r) { var cut = Rect.Intersect(r, aim); return cut.IsEmpty ? 0 : cut.Width * cut.Height; }
        bool Free(Point at) => Inside(Body(at)) && Covered(Body(at)) < .01;
        double Away(Point at) => (at - new Point(Left, Top)).Length;
        var chosen = places.FirstOrDefault(p => p.Side == side && Free(p.At));
        if (chosen.Side == null) chosen = places.Take(2).Where(p => Free(p.At)).OrderBy(p => Away(p.At)).FirstOrDefault();
        if (chosen.Side == null) chosen = places.Skip(2).FirstOrDefault(p => Free(p.At));
        if (chosen.Side != null) return (chosen.At, chosen.Side, false);
        // The target nearly fills its work area: cover as little of it as possible.
        var squeezed = places.Select(p => (p.Side, At: new Point(FitLeft(p.At.X), FitTop(p.At.Y))))
            .OrderBy(p => Covered(Body(p.At))).ThenBy(p => Away(p.At)).First();
        return (squeezed.At, squeezed.Side, Covered(Body(squeezed.At)) >= .01);
    }

    private void PlayWalkSegment(WalkPlan plan, string phase)
    {
        if (!companionReady || companionClosed) return;
        EndGreetingNotice();
        bool run = plan.Motion != "float";
        var rule = animationPolicy.Find(run ? RunClip : FloatClip);
        var (start, end, mirror, rate) = phase switch {
            "WalkStart" => (RunTurn, RunLoop, plan.Mirrored, plan.Rate),
            "WalkLoop" => run ? (RunLoop, RunLoopEnd, plan.Mirrored, plan.Rate) : (FloatLoop, FloatLoopEnd, false, plan.Rate),
            _ => (RunStop, RunStopEnd, plan.FaceMirrored, 1.25) };
        currentAction = rule.Id; animationPhase = phase;
        videoPlayer!.Play(Path.Combine(AnimationDirectory, rule.File), mirror, AnimationFinished, start, end, rate);
    }

    // Movement runs on its own clock, so replaying the loop clip never makes the position jump.
    private void WalkFrame(object? sender, EventArgs e)
    {
        if (walk is not { Arrived: false } plan) { StopWalkFrames(); return; }
        if (!IsVisible) { CancelWalk("hidden"); return; }
        // Something else moved or resized the fish (settings window, edge snap): stop where it is now.
        if (Math.Abs(preferences.Scale - plan.Scale) > 1e-9 || Math.Abs(Left - plan.Last.X) > 1 || Math.Abs(Top - plan.Last.Y) > 1) { CancelWalk("settings"); return; }
        double t = plan.Clock.Elapsed.TotalSeconds - plan.Lead;
        if (t >= plan.Duration) { Arrive(plan); return; }
        double f = Travelled(t, plan.Duration);
        double x = plan.From.X + (plan.To.X - plan.From.X) * f, y = plan.From.Y + (plan.To.Y - plan.From.Y) * f;
        // Under half a physical pixel the window would not move; skip the layout work of the bubble and input.
        var dpi = VisualTreeHelper.GetDpi(this);
        if (Math.Abs(x - Left) * dpi.DpiScaleX >= .5) Left = x;
        if (Math.Abs(y - Top) * dpi.DpiScaleY >= .5) Top = y;
        plan.Last = new(Left, Top);
    }

    // Share of the way covered after t seconds: speed rises and falls along a smoothstep over the first and last .15 s.
    private static double Travelled(double t, double duration)
    {
        static double Ramp(double u) => u * u * u - u * u * u * u / 2; // integral of 3u²-2u³
        if (t <= 0) return 0;
        if (t >= duration) return 1;
        double peak = 1 / (duration - WalkRamp);
        if (t < WalkRamp) return peak * WalkRamp * Ramp(t / WalkRamp);
        if (t > duration - WalkRamp) return 1 - peak * WalkRamp * Ramp((duration - t) / WalkRamp);
        return peak * (t - WalkRamp / 2);
    }

    private void Arrive(WalkPlan plan)
    {
        plan.Arrived = true; plan.Clock.Stop(); StopWalkFrames();
        Left = plan.To.X; Top = plan.To.Y;
        CompanionEdgeLayout.Refresh(this);
        anchor = new(Left, Top); SaveCompanion();
        // A random movement event right away would carry the fish off again.
        nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
        walkAvoid = (plan.Target, new Point(Left, Top), preferences.Scale);
        ReportWalk(plan, null);
        if (plan.Say != null) { ShowMcpSpeech(plan.Say); walkBubble = true; }
        if (plan.Motion == "run") PlayWalkSegment(plan, "WalkStop");
        else if (!EndWalk(plan) && plan.Motion == "float") ResumeAfterWalk();
    }

    // The requested action, facing the target; false leaves the next clip to the ordinary flow.
    private bool EndWalk(WalkPlan plan)
    {
        if (walk == plan) walk = null;
        if (plan.ArriveAction == null || !companionReady || exitAnimation || companionClosed) return false;
        PlayAnimation(animationPolicy.Find(plan.ArriveAction), "External", plan.FaceMirrored);
        return true;
    }

    private void ResumeAfterWalk()
    {
        if (!dragging && !hiddenByUser && !fullscreenHidden && PlayQueuedMcpAction()) return;
        AnimationBoundary();
    }

    // From AnimationFinished: the walk picks its own next clip, so queued play_action clips wait for the arrival.
    private bool ContinueWalk()
    {
        if (walk is not { } plan || animationPhase is not ("WalkStart" or "WalkLoop" or "WalkStop")) return false;
        if (animationPhase != "WalkStop" && !plan.Arrived) { PlayWalkSegment(plan, "WalkLoop"); return true; }
        if (animationPhase != "WalkStop" && plan.Motion == "run") { PlayWalkSegment(plan, "WalkStop"); return true; }
        return EndWalk(plan);
    }

    // Always stops where the fish is now; it never runs back. Only a tour's own next walk replaces a walk
    // without ending the tour (StartWalk ends it for any other).
    private void CancelWalk(string reason)
    {
        if (reason != "replaced") StopTour(reason);
        if (walkPressed is { } pressed) { walkPressed = null; ReportWalk(pressed, reason); }
        if (walk is not { } plan) return;
        walk = null; plan.Clock.Stop(); StopWalkFrames();
        anchor = new(Left, Top);
        if (plan.Arrived) return;
        SaveCompanion();
        ReportWalk(plan, reason);
        // Drag, click, interrupt_action, the next walk, a preview, puppet mode and the farewell play their own clip next.
        if (reason is ("request_cancelled" or "settings" or "display_changed" or "mcp_disabled" or "hidden")
            && animationPhase is ("WalkStart" or "WalkLoop" or "WalkStop") && companionReady && !exitAnimation && !companionClosed)
            ResumeAfterWalk();
    }

    private void PressWalk()
    {
        if (tour is { } trip) trip.Pressed = true; // The next stop waits for the release, which ends the tour.
        if (walk is not { } plan) return;
        walk = null; plan.Clock.Stop(); StopWalkFrames();
        anchor = new(Left, Top);
        if (!plan.Arrived) walkPressed = plan;
    }

    private void StopWalkFrames()
    {
        if (!walkFrames) return;
        CompositionTarget.Rendering -= WalkFrame;
        walkFrames = false;
    }

    private void ReportWalk(WalkPlan plan, string? cancelled)
    {
        plan.Cancelled = cancelled;
        var result = WalkReport(plan);
        lastWalk = result;
        plan.Done.TrySetResult(result);
    }

    private object? WalkSnapshot() => walk is { } plan ? WalkReport(plan) : null;

    private object WalkReport(WalkPlan plan, bool started = false)
    {
        double elapsed = plan.Clock.Elapsed.TotalSeconds;
        return new {
            arrived = plan.Arrived, cancelled = plan.Cancelled, started, phase = walk == plan && animationPhase.StartsWith("Walk", StringComparison.Ordinal) ? animationPhase : null,
            motion = plan.Motion, side = plan.Side, overlapsTarget = plan.Overlaps, target = PixelBox(plan.Pixels), desktopItem = plan.DesktopItem, arriveAction = plan.ArriveAction,
            from = new { Left = Math.Round(plan.From.X, 2), Top = Math.Round(plan.From.Y, 2) }, to = new { Left = Math.Round(plan.To.X, 2), Top = Math.Round(plan.To.Y, 2) },
            Left = Math.Round(Left, 2), Top = Math.Round(Top, 2), petBounds = PetPhysicalBounds(),
            distance = Math.Round(plan.Distance, 1), durationSeconds = Math.Round(plan.Lead + plan.Duration, 2), elapsedSeconds = Math.Round(elapsed, 2),
            etaSeconds = plan.Arrived || plan.Cancelled != null ? 0 : Math.Round(Math.Max(0, plan.Lead + plan.Duration - elapsed), 2) };
    }

    private static object PixelBox(Rect r) => new { x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y), width = (int)Math.Round(r.Width), height = (int)Math.Round(r.Height) };

    // The visible body in physical pixels: what the fish covers in a screenshot.
    private object? PetPhysicalBounds()
    {
        if (!IsVisible || PresentationSource.FromVisual(this) == null) return null;
        var body = CompanionEdgeLayout.PetBounds(this);
        return PixelBox(new Rect(DipToPhysical(Left + body.Left, Top + body.Top), DipToPhysical(Left + body.Right, Top + body.Bottom)));
    }

    // Physical pixels to this window's logical screen space (unchanged on monitors at the system DPI), then into DIP.
    private Point PhysicalToDip(double x, double y)
    {
        var point = new CompanionNative.Point { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
        try { CompanionNative.PhysicalToLogicalPointForPerMonitorDPI(new WindowInteropHelper(this).Handle, ref point); }
        catch (EntryPointNotFoundException) { }
        var local = PointFromScreen(new Point(point.X, point.Y));
        return new(Left + local.X, Top + local.Y);
    }
    private Rect PhysicalToDip(Rect pixels) => new(PhysicalToDip(pixels.Left, pixels.Top), PhysicalToDip(pixels.Right, pixels.Bottom));
    private Point DipToPhysical(double x, double y)
    {
        var screen = PointToScreen(new Point(x - Left, y - Top));
        var point = new CompanionNative.Point { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        try { CompanionNative.LogicalToPhysicalPointForPerMonitorDPI(new WindowInteropHelper(this).Handle, ref point); }
        catch (EntryPointNotFoundException) { }
        return new(point.X, point.Y);
    }

    // walk_to with space=capture maps image pixels through the screenshot they came from.
    private int RememberMcpCapture(CompanionWindowImage image)
    {
        mcpCaptures.Add((++mcpCaptureCount, image.Source, image.Width, image.Height));
        if (mcpCaptures.Count > 8) mcpCaptures.RemoveAt(0);
        return mcpCaptureCount;
    }

    // Decoded openings of the walk segments, so a turn, a replayed stride or the stop shows without waiting for ffmpeg.
    private void PreloadWalk()
    {
        if (videoPlayer == null || !preferences.McpEnabled) return;
        var run = Path.Combine(AnimationDirectory, animationPolicy.Find(RunClip).File);
        foreach (var start in new[] { RunTurn, RunLoop, RunStop }) videoPlayer.Preload(run, start);
        videoPlayer.Preload(Path.Combine(AnimationDirectory, animationPolicy.Find(FloatClip).File), FloatLoop);
    }

    // The input usually opens to the left of the body, right on a target the fish just ran up to from the right.
    private (double X, double Y) AvoidWalkTarget(double x, double y, double width, double height, Rect body, Rect area)
    {
        if (walkAvoid is not { } avoid || Math.Abs(avoid.At.X - Left) + Math.Abs(avoid.At.Y - Top) > 1 || Math.Abs(avoid.Scale - preferences.Scale) > 1e-9) return (x, y);
        Rect Placed(double px, double py) => new(Math.Clamp(px, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8)),
            Math.Clamp(py, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8)), width, height);
        double middle = Left + body.Left + body.Width / 2 - width / 2;
        foreach (var (px, py) in new[] { (x, y), (Left + body.Right + 8, y), (Left + body.Left - width - 8, y), (middle, Top + body.Bottom + 8), (middle, Top + body.Top - height - 8) })
            if (!Placed(px, py).IntersectsWith(avoid.Target)) return (px, py);
        return (x, y);
    }
}
#endif

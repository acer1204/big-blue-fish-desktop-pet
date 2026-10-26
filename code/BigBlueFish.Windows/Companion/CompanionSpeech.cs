// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// All model replies use this display path. Measure the whole reply before revealing it.
// Every bubble text passes CompanionTraditional first, so Simplified characters never reach the screen.
internal sealed class CompanionSpeech(TextBlock text, Border bubble, Action refresh, Action completed) : IDisposable
{
    internal const double CharactersPerSecond = 28;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly Stopwatch elapsed = new();
    private int[] boundaries = Array.Empty<int>();
    private int shown;
    private bool subscribed;
    internal string FullText { get; private set; } = "";
    internal bool IsTyping => timer.IsEnabled;

    internal void Show(string message, bool animate)
    {
        message = CompanionTraditional.Convert(message);
        Cancel();
        if (!subscribed) { timer.Tick += Tick; subscribed = true; }
        FullText = message;
        boundaries = StringInfo.ParseCombiningCharacters(message);
        shown = animate && boundaries.Length > 0 ? 1 : boundaries.Length;
        text.Text = Prefix(shown);
        bubble.Visibility = Visibility.Visible;
        refresh();
        if (shown < boundaries.Length) { elapsed.Restart(); timer.Start(); }
        else completed();
    }

    // A streamed snapshot (not final) that needs converting waits for its unfinished last run of Chinese.
    internal void ShowPartial(string message, bool final = false)
    {
        message = final ? CompanionTraditional.Convert(message) : CompanionTraditional.ConvertPartial(message);
        if (message.Length == 0) return;
        Cancel();
        if (!subscribed) { timer.Tick += Tick; subscribed = true; }
        FullText = message;
        boundaries = StringInfo.ParseCombiningCharacters(message);
        shown = boundaries.Length;
        text.Text = message;
        bubble.Visibility = Visibility.Visible;
        refresh();
    }

    private string Prefix(int count) => count >= boundaries.Length ? FullText : FullText[..boundaries[count]];

    private void Tick(object? sender, EventArgs e)
    {
        if (bubble.Visibility != Visibility.Visible) { Cancel(); return; }
        int count = Math.Min(boundaries.Length, 1 + (int)(elapsed.Elapsed.TotalSeconds * CharactersPerSecond));
        if (count <= shown) return;
        shown = count;
        text.Text = Prefix(count);
        if (shown == boundaries.Length) { Cancel(); completed(); }
    }

    internal void Cancel() { timer.Stop(); elapsed.Stop(); }
    public void Dispose() { Cancel(); timer.Tick -= Tick; }
}

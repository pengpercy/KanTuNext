using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace KanTuNext.Desktop.Views;

/// <summary>Small frame-driven animations shared by the viewer's panels and copy buttons.</summary>
internal static class Motion
{
    private sealed class Run
    {
        public int Generation;
    }

    // One animation per control: starting another (a second toggle mid-slide) takes over from where it is
    private static readonly ConditionalWeakTable<Control, Run> Runs = new();

    private static int Next(Control control) => ++Runs.GetOrCreateValue(control).Generation;

    private static bool IsCurrent(Control control, int generation) =>
        Runs.TryGetValue(control, out var run) && run.Generation == generation;

    /// <summary>Drives <paramref name="frame"/> with the elapsed fraction 0–1 every frame for <paramref name="ms"/>.</summary>
    private static void Animate(Control control, double ms, Action<double> frame, Action? done = null)
    {
        var generation = Next(control);
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            frame(1);
            done?.Invoke();
            return;
        }
        var start = -1d;
        void Step(TimeSpan now)
        {
            if (!IsCurrent(control, generation)) return;
            if (start < 0) start = now.TotalMilliseconds;
            var t = Math.Clamp((now.TotalMilliseconds - start) / ms, 0, 1);
            frame(t);
            if (t < 1) top.RequestAnimationFrame(Step);
            else done?.Invoke();
        }
        top.RequestAnimationFrame(Step);
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>
    /// Slides a side panel in or out: its clipping host eases its width (or height) to <paramref name="to"/>
    /// while the panel inside keeps its full size, so it moves in from the window's edge instead of squeezing.
    /// The host is hidden once closed.
    /// </summary>
    public static void Slide(Control host, bool horizontal, double to, double ms = 240)
    {
        var from = horizontal ? host.Bounds.Width : host.Bounds.Height;
        if (!host.IsVisible) from = 0;
        host.IsVisible = true;
        void Set(double size)
        {
            if (horizontal) host.Width = size;
            else host.Height = size;
        }
        Set(from);
        Animate(host, ms, t => Set(from + (to - from) * EaseOut(t)), () => host.IsVisible = to > 0);
    }

    /// <summary>Opens or closes a sliding panel at once (startup, or the panel moving to another edge).</summary>
    public static void Place(Control host, bool horizontal, double size)
    {
        Next(host);
        if (horizontal) host.Width = size;
        else host.Height = size;
        host.IsVisible = size > 0;
    }

    /// <summary>
    /// A copy button's confirmation: its icon gives way to a green check for a moment, then comes back
    /// (the <c>copy</c> button styles in Resources/Styles.axaml).
    /// </summary>
    public static void Copied(Button button)
    {
        var generation = Next(button);
        button.Classes.Add("copied");
        DispatcherTimer.RunOnce(() =>
        {
            if (IsCurrent(button, generation)) button.Classes.Remove("copied");
        }, TimeSpan.FromMilliseconds(1300));
    }

    /// <summary>
    /// Rings what was just copied: the outline brightens with a soft glow, holds, and fades back, so it reads
    /// as "all of this" went to the clipboard.
    /// </summary>
    public static void Glow(Border ring)
    {
        if (ring.RenderTransform is not ScaleTransform scale) ring.RenderTransform = scale = new ScaleTransform();
        Animate(ring, 950, t =>
        {
            var ms = t * 950;
            // Up in 160 ms, hold, then down over the last 520
            ring.Opacity = ms < 160 ? EaseOut(ms / 160) : ms < 430 ? 1 : 1 - EaseInOut((ms - 430) / 520);
            // Settles outward onto the text as it lights up
            var grow = EaseOut(Math.Min(1, ms / 260));
            scale.ScaleX = scale.ScaleY = 0.97 + 0.03 * grow;
        }, () => ring.Opacity = 0);
    }

    private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}

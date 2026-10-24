using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickCut.Services;

namespace QuickCut.Controls;

/// <summary>
/// Ruler, per-file filmstrip, waveform, in/out range with draggable handles, and playhead.
/// Shows a window of the timeline (all of it by default) that can be zoomed and scrolled:
/// Ctrl+wheel zooms at the cursor, the wheel scrolls, and dragging past an edge scrolls along.
/// Clicking or dragging elsewhere scrubs. Right-clicking moves the nearer of the in/out points to the clicked time.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    const double Pad = 8;
    const double FilmTop = 22;
    const double HandleHit = 8;
    const double MinRange = 0.1;
    const double MinViewLength = 1;
    const double EdgeZone = 24;

    // The filmstrip and waveform share whatever height the layout gives the control (roughly 2:1).
    double Tracks => Math.Max(50, ActualHeight - FilmTop - 10);
    double FilmH => Math.Round(Tracks * 0.66);
    double WaveTop => FilmTop + FilmH + 4;
    double WaveH => Tracks - FilmH - 4;

    // Theme-dependent colours come from the active palette (Themes/Dark.xaml, Themes/Light.xaml).
    Brush R(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    Brush SegFill(int i) => R("TlSeg" + i % 3);
    Brush SegLabel(int i) => R("TlLabel" + i % 3);
    Brush TickBrush => R("TlTick");
    Brush RulerText => R("Text3Brush");
    Brush WaveTrack => R("TlTrack");
    Brush WaveIdle => R("TlWaveIdle");
    Brush WaveActive => R("TlWaveActive");
    Brush Dim => R("TlDim");
    Brush LabelBack => R("TlLabelBack");
    static readonly Brush SelFill = Frozen("#225B8CFF");
    static readonly Pen SelPen = FrozenPen("#E05B8CFF", 1.5);
    static readonly Pen SelGlow1 = FrozenPen("#1A5B8CFF", 8);
    static readonly Pen SelGlow2 = FrozenPen("#335B8CFF", 4);
    static readonly Brush InBrush = Frozen("#34D399");
    static readonly Brush OutBrush = Frozen("#FB7185");
    static readonly Pen InGlow = FrozenPen("#5534D399", 5);
    static readonly Pen OutGlow = FrozenPen("#55FB7185", 5);
    static readonly Brush FilmShade;

    static TimelineControl()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 1));
        g.Freeze();
        FilmShade = g;
    }

    enum Drag { None, Scrub, In, Out }
    Drag drag;
    Timeline timeline;
    double position, inPoint, outPoint;
    double viewStart, viewLength; // viewLength 0 = whole timeline
    double lastDragX;
    double grabOffset; // handle time minus mouse time when it was grabbed, so grabbing doesn't move it
    readonly DispatcherTimer edgeScroll;

    public TimelineControl()
    {
        MinHeight = 96;
        Cursor = Cursors.Hand;
        ToolTipService.SetInitialShowDelay(this, 300);
        edgeScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        edgeScroll.Tick += (_, _) => EdgeScrollTick();
    }

    public event Action<double> SeekRequested;
    public event Action<double, double> RangeChanged;

    /// <summary>Raised when the visible window changes; the argument is true when the user zoomed or scrolled it directly.</summary>
    public event Action<bool> ViewChanged;

    public Timeline Timeline
    {
        get => timeline;
        set
        {
            timeline = value;
            viewStart = 0;
            viewLength = 0;
            ViewChanged?.Invoke(false);
            InvalidateVisual();
        }
    }

    public double Position
    {
        get => position;
        set { position = value; InvalidateVisual(); }
    }

    public bool IsDragging => drag != Drag.None;

    public double InPoint => inPoint;
    public double OutPoint => outPoint;

    public void SetRange(double a, double b)
    {
        inPoint = a;
        outPoint = b;
        InvalidateVisual();
    }

    // ── Visible window ──

    double Duration => Math.Max(0.001, timeline?.Duration ?? 1);
    public double ViewStart => viewStart;
    public double ViewLength => viewLength > 0 ? viewLength : Duration;
    public double ViewEnd => ViewStart + ViewLength;
    public bool IsZoomed => timeline != null && ViewLength < Duration - 1e-6;
    public bool CanZoomIn => timeline != null && ViewLength > Math.Min(MinViewLength, Duration) + 1e-6;
    public double TimelineDuration => Duration;

    public void ShowAll() => SetView(0, Duration, false);

    public void ShowRange(double from, double to) => SetView(from, to - from, false);

    /// <summary>Scrolls just enough to bring <paramref name="t"/> into view, a page at a time.</summary>
    public void EnsureVisible(double t)
    {
        if (!IsZoomed || (t >= ViewStart && t <= ViewEnd)) return;
        SetView(t < ViewStart ? t - ViewLength * 0.9 : t - ViewLength * 0.1, ViewLength, false);
    }

    public void ScrollTo(double start, bool user = true) => SetView(start, ViewLength, user);

    public void ZoomBy(double factor, double anchorTime, bool user = true)
    {
        double len = ViewLength / factor;
        double frac = (anchorTime - ViewStart) / ViewLength;
        SetView(anchorTime - frac * len, len, user);
    }

    void SetView(double start, double length, bool user)
    {
        double dur = Duration;
        length = Math.Max(Math.Min(MinViewLength, dur), Math.Min(dur, length));
        start = Math.Max(0, Math.Min(dur - length, start));
        bool whole = length >= dur - 1e-6;
        viewStart = whole ? 0 : start;
        viewLength = whole ? 0 : length;
        ViewChanged?.Invoke(user);
        InvalidateVisual();
    }

    double Usable => Math.Max(1, ActualWidth - 2 * Pad);
    double X(double t) => Pad + (t - ViewStart) / ViewLength * Usable;
    double T(double x) => Math.Max(0, Math.Min(Duration, ViewStart + (x - Pad) / Usable * ViewLength));

    // ── Drawing ──

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (timeline == null)
        {
            dc.DrawRoundedRectangle(WaveTrack, null, new Rect(Pad, FilmTop, w - 2 * Pad, FilmH), 7, 7);
            dc.DrawRoundedRectangle(WaveTrack, null, new Rect(Pad, WaveTop, w - 2 * Pad, WaveH), 5, 5);
            return;
        }

        double left = Pad, right = w - Pad;
        dc.PushClip(new RectangleGeometry(new Rect(left - 4, 0, right - left + 8, ActualHeight)));
        DrawRuler(dc, ppd);

        // Filmstrip, one rounded block per file.
        for (int i = 0; i < timeline.Segments.Count; i++)
        {
            var s = timeline.Segments[i];
            if (s.End < ViewStart || s.Offset > ViewEnd) continue;
            double x0 = X(s.Offset) + (i > 0 ? 1.5 : 0);
            double x1 = X(s.End) - (i < timeline.Segments.Count - 1 ? 1.5 : 0);
            var rect = new Rect(x0, FilmTop, Math.Max(1, x1 - x0), FilmH);
            dc.PushClip(new RectangleGeometry(rect, 7, 7));
            dc.DrawRectangle(SegFill(i), null, rect);
            var thumbs = s.Thumbs;
            if (thumbs != null && thumbs.Count > 0)
            {
                double tw = FilmH * thumbs[0].PixelWidth / thumbs[0].PixelHeight;
                int n = (int)Math.Ceiling(rect.Width / tw);
                int k0 = Math.Max(0, (int)Math.Floor((left - rect.X) / tw));
                int k1 = Math.Min(n, (int)Math.Ceiling((right - rect.X) / tw));
                for (int k = k0; k < k1; k++)
                {
                    int idx = (int)((k + 0.5) * tw / rect.Width * thumbs.Count);
                    idx = Math.Max(0, Math.Min(thumbs.Count - 1, idx));
                    dc.DrawImage(thumbs[idx], new Rect(rect.X + k * tw, FilmTop, tw, FilmH));
                }
            }
            dc.DrawRectangle(FilmShade, null, rect);
            DrawSegmentLabel(dc, i, s, Math.Max(rect.Left, left), Math.Min(rect.Right, right), ppd);
            dc.Pop();
        }

        // Waveform.
        dc.DrawRoundedRectangle(WaveTrack, null, new Rect(left, WaveTop, right - left, WaveH), 5, 5);
        double mid = WaveTop + WaveH / 2;
        for (double x = left + 1; x < right - 1; x += 3)
        {
            double t = T(x + 1);
            timeline.Locate(t, out var si, out var local);
            var seg = timeline.Segments[si];
            if (seg.Peaks == null || seg.Peaks.Length == 0) continue;
            int pi = Math.Min(seg.Peaks.Length - 1, (int)(local / seg.PeakInterval));
            double hgt = Math.Max(1, seg.Peaks[pi] * (WaveH - 4));
            bool inside = t >= inPoint && t <= outPoint;
            dc.DrawRoundedRectangle(inside ? WaveActive : WaveIdle, null, new Rect(x, mid - hgt / 2, 2, hgt), 1, 1);
        }

        // Dim outside the selection, then the glowing selection box and handles.
        double sx = X(inPoint), ex = X(outPoint);
        double top = FilmTop - 3, bottom = WaveTop + WaveH + 3;
        if (sx > left) dc.DrawRectangle(Dim, null, new Rect(left, FilmTop, Math.Min(sx, right) - left, bottom - 3 - FilmTop));
        if (ex < right) dc.DrawRectangle(Dim, null, new Rect(Math.Max(ex, left), FilmTop, right - Math.Max(ex, left), bottom - 3 - FilmTop));

        var sel = new Rect(sx, top, Math.Max(2, ex - sx), bottom - top);
        dc.DrawRoundedRectangle(null, SelGlow1, sel, 8, 8);
        dc.DrawRoundedRectangle(null, SelGlow2, sel, 8, 8);
        dc.DrawRoundedRectangle(SelFill, SelPen, sel, 8, 8);

        double hh = Math.Min(60, (bottom - top) * 0.45);
        double hy = top + (bottom - top - hh) / 2;
        var inHandle = new Rect(sx - 3.5, hy, 7, hh);
        var outHandle = new Rect(ex - 3.5, hy, 7, hh);
        dc.DrawRoundedRectangle(null, InGlow, inHandle, 3.5, 3.5);
        dc.DrawRoundedRectangle(InBrush, null, inHandle, 3.5, 3.5);
        dc.DrawRoundedRectangle(null, OutGlow, outHandle, 3.5, 3.5);
        dc.DrawRoundedRectangle(OutBrush, null, outHandle, 3.5, 3.5);

        // Playhead.
        double px = X(position);
        var head = R("TlHead");
        var headGlow = new SolidColorBrush(((SolidColorBrush)head).Color) { Opacity = 0.25 };
        dc.DrawLine(new Pen(headGlow, 6), new Point(px, FilmTop - 6), new Point(px, bottom));
        dc.DrawLine(new Pen(head, 2), new Point(px, FilmTop - 6), new Point(px, bottom));
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(px - 6, FilmTop - 12), true, true);
            g.LineTo(new Point(px + 6, FilmTop - 12), true, false);
            g.LineTo(new Point(px, FilmTop - 4), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(head, null, tri);
        dc.Pop();
    }

    /// <summary>File name in the visible part of a block: full, then shortened in the middle, then "#n".</summary>
    void DrawSegmentLabel(DrawingContext dc, int i, Segment s, double visLeft, double visRight, double ppd)
    {
        double avail = visRight - visLeft - 14;
        if (avail < 16) return;
        var brush = SegLabel(i);
        var label = Text(s.Name, 10, brush, ppd);
        if (label.Width > avail)
        {
            label = null;
            var ext = System.IO.Path.GetExtension(s.Name);
            var stem = System.IO.Path.GetFileNameWithoutExtension(s.Name);
            for (int keep = stem.Length - 1; keep >= 6; keep--)
            {
                int head = (keep + 1) / 2, tail = keep - head;
                var t = Text(stem.Substring(0, head) + "…" + stem.Substring(stem.Length - tail) + ext, 10, brush, ppd);
                if (t.Width <= avail) { label = t; break; }
            }
            if (label == null)
            {
                label = Text("#" + (i + 1), 10, brush, ppd);
                if (label.Width > avail) return;
            }
        }
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(visLeft + 5, FilmTop + FilmH - 17, label.Width + 8, 14), 4, 4);
        dc.DrawText(label, new Point(visLeft + 9, FilmTop + FilmH - 16));
    }

    void DrawRuler(DrawingContext dc, double ppd)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 3600 };
        double step = steps[steps.Length - 1];
        foreach (var s in steps)
            if (s / ViewLength * Usable >= 64) { step = s; break; }

        double first = Math.Floor(ViewStart / step) * step;
        for (double t = first; t <= ViewEnd + 1e-6; t += step)
        {
            double x = X(t);
            if (x < Pad - 1) continue;
            dc.DrawRectangle(TickBrush, null, new Rect(x, 11, 1, 5));
            var text = step < 1 ? TimeFormat.Format(t).Substring(0, TimeFormat.Format(t).Length - 2) : TimeFormat.FormatShort(t);
            var label = Text(text, 10, RulerText, ppd, true);
            if (x + label.Width + 4 < ActualWidth) dc.DrawText(label, new Point(x + 3, 0));
        }
        // Small sub-ticks.
        for (double t = first; t <= ViewEnd; t += step / 5)
            if (X(t) >= Pad) dc.DrawRectangle(TickBrush, null, new Rect(X(t), 14, 1, 2));
    }

    static FormattedText Text(string s, double size, Brush brush, double ppd, bool mono = false) =>
        new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            mono ? MonoFace : UiFace, size, brush, ppd);

    static readonly Typeface UiFace = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface MonoFace = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // ── Mouse ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (timeline == null) return;
        var p = e.GetPosition(this);
        drag = HitHandle(p);
        CaptureMouse();
        lastDragX = p.X;
        if (drag == Drag.None)
        {
            drag = Drag.Scrub;
            Apply(p.X);
        }
        else
        {
            double handle = drag == Drag.In ? inPoint : outPoint;
            grabOffset = handle - T(p.X);
            SeekRequested?.Invoke(handle);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (drag != Drag.None && IsMouseCaptured)
        {
            lastDragX = p.X;
            Apply(p.X);
            // Past an edge while zoomed: keep scrolling that way so the drag can reach other files.
            bool atEdge = IsZoomed && (p.X < Pad + EdgeZone || p.X > ActualWidth - Pad - EdgeZone);
            if (atEdge && !edgeScroll.IsEnabled) edgeScroll.Start();
            else if (!atEdge) edgeScroll.Stop();
            return;
        }
        Cursor = timeline != null && HitHandle(p) != Drag.None ? Cursors.SizeWE : Cursors.Hand;
        UpdateToolTip(p);
    }

    void EdgeScrollTick()
    {
        if (drag == Drag.None || !IsMouseCaptured) { edgeScroll.Stop(); return; }
        double speed = ViewLength * 0.03;
        if (lastDragX < Pad + EdgeZone) ScrollTo(ViewStart - speed);
        else if (lastDragX > ActualWidth - Pad - EdgeZone) ScrollTo(ViewStart + speed);
        Apply(lastDragX);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        EndDrag();
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => EndDrag();

    /// <summary>Right-click moves whichever of the in/out points is nearer to the clicked time.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (timeline == null || drag != Drag.None) return;
        double t = T(e.GetPosition(this).X);
        if (Math.Abs(t - inPoint) <= Math.Abs(t - outPoint))
            inPoint = Math.Min(t, outPoint - MinRange);
        else
            outPoint = Math.Max(t, inPoint + MinRange);
        RangeChanged?.Invoke(inPoint, outPoint);
        SeekRequested?.Invoke(t);
        InvalidateVisual();
        e.Handled = true;
    }

    void EndDrag()
    {
        drag = Drag.None;
        edgeScroll.Stop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (timeline == null) return;
        double notches = e.Delta / 120.0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            ZoomBy(Math.Pow(1.25, notches), T(e.GetPosition(this).X));
        else if (IsZoomed)
            ScrollTo(ViewStart - notches * ViewLength * 0.15);
        else
            return;
        e.Handled = true;
    }

    void UpdateToolTip(Point p)
    {
        if (timeline == null || p.Y < FilmTop || p.Y > FilmTop + FilmH) { ToolTip = null; return; }
        timeline.Locate(T(p.X), out var i, out _);
        var s = timeline.Segments[i];
        var tip = $"#{i + 1}  {s.Name}\n長度 {TimeFormat.FormatShort(s.Duration)} · 從 {TimeFormat.FormatShort(s.Offset)} 開始";
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    Drag HitHandle(Point p)
    {
        if (p.Y < FilmTop - 6) return Drag.None;
        double din = Math.Abs(p.X - X(inPoint)), dout = Math.Abs(p.X - X(outPoint));
        if (din <= HandleHit && din <= dout) return Drag.In;
        if (dout <= HandleHit) return Drag.Out;
        return Drag.None;
    }

    void Apply(double x)
    {
        double t = T(x);
        if (drag == Drag.In || drag == Drag.Out) t = Math.Max(0, Math.Min(Duration, t + grabOffset));
        switch (drag)
        {
            case Drag.In:
                inPoint = Math.Min(t, outPoint - MinRange);
                RangeChanged?.Invoke(inPoint, outPoint);
                SeekRequested?.Invoke(inPoint);
                break;
            case Drag.Out:
                outPoint = Math.Max(t, inPoint + MinRange);
                RangeChanged?.Invoke(inPoint, outPoint);
                SeekRequested?.Invoke(outPoint);
                break;
            case Drag.Scrub:
                SeekRequested?.Invoke(t);
                break;
        }
        InvalidateVisual();
    }

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static Pen FrozenPen(string hex, double width)
    {
        var p = new Pen(Frozen(hex), width);
        p.Freeze();
        return p;
    }
}

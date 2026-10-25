using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
/// Clicking or dragging elsewhere scrubs.
/// Sped-up and cut regions show as bands: their edges drag, Alt+drag moves a band, Shift+drag draws a new one.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    const double Pad = 8;
    const double FilmTop = 22;
    const double HandleHit = 8;
    const double MinRange = 0.1;
    const double MinViewLength = 1;
    const double EdgeZone = 24;
    const double MaxFilmH = 90; // taller tiles would also be wider, leaving only a few on screen

    // The filmstrip and waveform share whatever height the layout gives the control (roughly 2:1);
    // past the filmstrip's limit the extra height all goes to the waveform.
    double Tracks => Math.Max(50, ActualHeight - FilmTop - 10);
    double FilmH => Math.Min(MaxFilmH, Math.Round(Tracks * 0.66));
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
    Brush RegionFill => R("TlRegion");
    Brush RegionEdge => R("TlRegionEdge");
    Brush RegionText => R("TlRegionText");
    Brush CutEdge => R("TlCutEdge");
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

    enum Drag { None, Scrub, In, Out, RegionStart, RegionEnd, RegionMove, NewRegion }
    Drag drag;
    Timeline timeline;
    IList<TimeRegion> regions = new List<TimeRegion>();
    TimeRegion dragRegion;
    double boundLo, boundHi;                      // where the dragged region or the new range may reach
    double pendingStart = double.NaN, pendingEnd; // range drawn with Shift, kept on screen until ClearPending
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

    /// <summary>A region was resized or moved by dragging.</summary>
    public event Action RegionsChanged;

    /// <summary>A range was drawn with Shift+drag; the owner asks what to make of it, then calls <see cref="ClearPending"/>.</summary>
    public event Action<double, double> NewRegionRequested;

    /// <summary>Shared with the owner, which adds and removes regions; this control only resizes and moves them.</summary>
    public IList<TimeRegion> Regions
    {
        get => regions;
        set { regions = value ?? new List<TimeRegion>(); InvalidateVisual(); }
    }

    public void ClearPending()
    {
        pendingStart = double.NaN;
        InvalidateVisual();
    }

    TimeRegion RegionAt(double t) => regions.FirstOrDefault(r => r.Contains(t));
    bool InCut(double t) => regions.Any(r => r.Kind == RegionKind.Cut && r.Contains(t));

    static float? PeakAt(float[] peaks, double interval, double local) =>
        peaks == null || peaks.Length == 0 || interval <= 0 ? null : peaks[Math.Min(peaks.Length - 1, (int)(local / interval))];

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
            if (s.End < ViewStart || s.Offset > ViewEnd)
            {
                s.Thumbs?.Want(Array.Empty<double>(), 0);
                continue;
            }
            double x0 = X(s.Offset) + (i > 0 ? 1.5 : 0);
            double x1 = X(s.End) - (i < timeline.Segments.Count - 1 ? 1.5 : 0);
            var rect = new Rect(x0, FilmTop, Math.Max(1, x1 - x0), FilmH);
            dc.PushClip(new RectangleGeometry(rect, 7, 7));
            dc.DrawRectangle(SegFill(i), null, rect);
            var strip = s.Thumbs;
            if (strip != null && s.Media.Width > 0 && s.Media.Height > 0)
            {
                // Each tile asks for the frame at its middle and shows the nearest one made so far.
                double tw = FilmH * s.Media.Width / s.Media.Height;
                double tileSeconds = tw / rect.Width * s.Duration;
                int n = (int)Math.Ceiling(rect.Width / tw);
                int k0 = Math.Max(0, (int)Math.Floor((left - rect.X) / tw));
                int k1 = Math.Min(n, (int)Math.Ceiling((right - rect.X) / tw));
                var times = new double[Math.Max(0, k1 - k0)];
                for (int k = k0; k < k1; k++)
                {
                    double t = (k + 0.5) * tileSeconds;
                    times[k - k0] = t;
                    var img = strip.Nearest(t);
                    if (img != null) dc.DrawImage(img, new Rect(rect.X + k * tw, FilmTop, tw, FilmH));
                }
                strip.Want(times, tileSeconds);
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
            // The full waveform once it is done, the rough one until then.
            float? level = PeakAt(seg.Peaks, seg.PeakInterval, local) ?? PeakAt(seg.RoughPeaks, seg.RoughInterval, local);
            if (level == null) continue;
            double hgt = Math.Max(1, level.Value * (WaveH - 4));
            bool inside = t >= inPoint && t <= outPoint && !InCut(t);
            dc.DrawRoundedRectangle(inside ? WaveActive : WaveIdle, null, new Rect(x, mid - hgt / 2, 2, hgt), 1, 1);
        }

        double top = FilmTop - 3, bottom = WaveTop + WaveH + 3;
        DrawRegions(dc, left, right, bottom - 3, ppd);

        // Dim outside the selection, then the glowing selection box and handles.
        double sx = X(inPoint), ex = X(outPoint);
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

    /// <summary>Sped-up regions as amber bands labelled "×12.5 → 02:00", cut ones dimmed and hatched, plus a range being drawn.</summary>
    void DrawRegions(DrawingContext dc, double left, double right, double bottom, double ppd)
    {
        foreach (var r in regions)
        {
            if (r.End < ViewStart || r.Start > ViewEnd) continue;
            double x0 = X(r.Start), x1 = X(r.End);
            var band = new Rect(x0, FilmTop, Math.Max(1, x1 - x0), bottom - FilmTop);
            bool cut = r.Kind == RegionKind.Cut;
            var edge = cut ? CutEdge : RegionEdge;
            if (cut)
            {
                dc.DrawRectangle(Dim, null, band);
                dc.DrawRectangle(Hatch(((SolidColorBrush)CutEdge).Color), null, band);
            }
            else dc.DrawRectangle(RegionFill, null, band);
            dc.DrawRectangle(edge, null, new Rect(x0 - 1, FilmTop, 2, band.Height));
            dc.DrawRectangle(edge, null, new Rect(x1 - 1, FilmTop, 2, band.Height));

            double visLeft = Math.Max(x0, left), visRight = Math.Min(x1, right);
            var brush = cut ? R("Text2Brush") : RegionText;
            string full = cut ? "刪除 " + TimeFormat.FormatShort(r.Length) : $"×{TimeRegion.FormatSpeed(r.Speed)} → {TimeFormat.FormatShort(r.Target)}";
            string brief = cut ? "刪除" : "×" + TimeRegion.FormatSpeed(r.Speed);
            var label = Text(full, 10, brush, ppd, true);
            if (label.Width + 12 > visRight - visLeft) label = Text(brief, 10, brush, ppd, true);
            if (label.Width + 12 > visRight - visLeft) continue;
            double lx = (visLeft + visRight - label.Width) / 2;
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(lx - 4, FilmTop + 3, label.Width + 8, 14), 4, 4);
            dc.DrawText(label, new Point(lx, FilmTop + 4));
        }

        if (!double.IsNaN(pendingStart))
        {
            double a = X(Math.Min(pendingStart, pendingEnd)), b = X(Math.Max(pendingStart, pendingEnd));
            dc.DrawRectangle(SelFill, PendingPen, new Rect(a, FilmTop, Math.Max(1, b - a), bottom - FilmTop));
        }
    }

    static readonly Pen PendingPen = DashedPen("#E05B8CFF", 1.5);

    Color hatchColor;
    Brush hatch;

    /// <summary>Diagonal stripes for cut regions, rebuilt when the theme changes their colour.</summary>
    Brush Hatch(Color c)
    {
        if (hatch != null && hatchColor == c) return hatch;
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 8), new Point(8, 0)));
        lines.Children.Add(new LineGeometry(new Point(-2, 2), new Point(2, -2)));
        lines.Children.Add(new LineGeometry(new Point(6, 10), new Point(10, 6)));
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(new SolidColorBrush(c), 1.5), lines))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        b.Freeze();
        hatchColor = c;
        return hatch = b;
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
        double t = T(p.X);
        var mods = Keyboard.Modifiers;
        bool onTracks = p.Y >= FilmTop - 6;
        dragRegion = null;
        if ((mods & ModifierKeys.Shift) != 0 && onTracks && TimeRegion.Gap(regions, t, Duration, out boundLo, out boundHi))
        {
            drag = Drag.NewRegion;
            pendingStart = pendingEnd = t;
        }
        else if ((mods & ModifierKeys.Alt) != 0 && onTracks && RegionAt(t) is TimeRegion moved)
        {
            drag = Drag.RegionMove;
            dragRegion = moved;
            grabOffset = moved.Start - t;
        }
        else drag = HitHandle(p, out dragRegion);
        if (dragRegion != null) TimeRegion.Bounds(regions, dragRegion, Duration, out boundLo, out boundHi);
        CaptureMouse();
        lastDragX = p.X;
        switch (drag)
        {
            case Drag.None:
                drag = Drag.Scrub;
                Apply(p.X);
                break;
            case Drag.In:
            case Drag.Out:
                double handle = drag == Drag.In ? inPoint : outPoint;
                grabOffset = handle - t;
                SeekRequested?.Invoke(handle);
                break;
            case Drag.RegionStart:
            case Drag.RegionEnd:
                double edge = drag == Drag.RegionStart ? dragRegion.Start : dragRegion.End;
                grabOffset = edge - t;
                SeekRequested?.Invoke(edge);
                break;
            default:
                Apply(p.X);
                break;
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
        Cursor = timeline == null ? Cursors.Hand
            : HitHandle(p, out _) != Drag.None ? Cursors.SizeWE
            : (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && p.Y >= FilmTop - 6 && RegionAt(T(p.X)) != null ? Cursors.SizeAll
            : Cursors.Hand;
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
        if (drag == Drag.NewRegion)
        {
            drag = Drag.Scrub; // so losing the capture below doesn't drop the range
            ReleaseMouseCapture();
            EndDrag();
            double a = Math.Min(pendingStart, pendingEnd), b = Math.Max(pendingStart, pendingEnd);
            if (b - a >= TimeRegion.MinLength) NewRegionRequested?.Invoke(a, b);
            else ClearPending();
            return;
        }
        EndDrag();
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => EndDrag();

    void EndDrag()
    {
        // Losing the mouse while drawing (e.g. Alt+Tab) drops the range rather than asking about it.
        if (drag == Drag.NewRegion) ClearPending();
        drag = Drag.None;
        dragRegion = null;
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
        if (timeline == null || p.Y < FilmTop || p.Y > WaveTop + WaveH) { ToolTip = null; return; }
        string tip;
        if (RegionAt(T(p.X)) is TimeRegion r)
        {
            string span = $"{TimeFormat.FormatShort(r.Start)}–{TimeFormat.FormatShort(r.End)}";
            tip = (r.Kind == RegionKind.Cut
                ? $"刪除 {span}（{TimeFormat.FormatShort(r.Length)}）"
                : $"縮時 {span} → {TimeFormat.FormatShort(r.Target)}（×{TimeRegion.FormatSpeed(r.Speed)}）")
                + "\n拖曳兩端調整 · Alt＋拖曳移動";
        }
        else if (p.Y <= FilmTop + FilmH)
        {
            timeline.Locate(T(p.X), out var i, out _);
            var s = timeline.Segments[i];
            tip = $"#{i + 1}  {s.Name}\n長度 {TimeFormat.FormatShort(s.Duration)} · 從 {TimeFormat.FormatShort(s.Offset)} 開始";
        }
        else { ToolTip = null; return; }
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    /// <summary>The in/out handles win over region edges; otherwise the nearest region edge.</summary>
    Drag HitHandle(Point p, out TimeRegion region)
    {
        region = null;
        if (p.Y < FilmTop - 6) return Drag.None;
        double din = Math.Abs(p.X - X(inPoint)), dout = Math.Abs(p.X - X(outPoint));
        if (din <= HandleHit && din <= dout) return Drag.In;
        if (dout <= HandleHit) return Drag.Out;
        var hit = Drag.None;
        TimeRegion found = null;
        double best = double.MaxValue;
        void Consider(Drag kind, TimeRegion r, double x)
        {
            double d = Math.Abs(p.X - x);
            if (d > HandleHit) return;
            // Where one region ends and the next begins, the side the mouse is on decides.
            bool inside = kind == Drag.RegionStart ? p.X >= x : p.X <= x;
            if (d < best - 0.01 || (d <= best + 0.01 && inside)) { best = d; hit = kind; found = r; }
        }
        foreach (var r in regions)
        {
            Consider(Drag.RegionStart, r, X(r.Start));
            Consider(Drag.RegionEnd, r, X(r.End));
        }
        region = found;
        return hit;
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
            case Drag.RegionStart:
                t = Math.Max(0, Math.Min(Duration, t + grabOffset));
                dragRegion.Start = Math.Max(boundLo, Math.Min(t, dragRegion.End - dragRegion.MinLengthFor));
                RegionsChanged?.Invoke();
                SeekRequested?.Invoke(dragRegion.Start);
                break;
            case Drag.RegionEnd:
                t = Math.Max(0, Math.Min(Duration, t + grabOffset));
                dragRegion.End = Math.Min(boundHi, Math.Max(t, dragRegion.Start + dragRegion.MinLengthFor));
                RegionsChanged?.Invoke();
                SeekRequested?.Invoke(dragRegion.End);
                break;
            case Drag.RegionMove:
                double len = dragRegion.Length;
                dragRegion.Start = Math.Max(boundLo, Math.Min(boundHi - len, t + grabOffset));
                dragRegion.End = dragRegion.Start + len;
                RegionsChanged?.Invoke();
                SeekRequested?.Invoke(dragRegion.Start);
                break;
            case Drag.NewRegion:
                pendingEnd = Math.Max(boundLo, Math.Min(boundHi, t));
                SeekRequested?.Invoke(pendingEnd);
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

    static Pen DashedPen(string hex, double width)
    {
        var p = new Pen(Frozen(hex), width) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
        p.Freeze();
        return p;
    }
}

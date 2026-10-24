using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuickCut.Controls;

/// <summary>Circular gauge for "estimated size / limit"; turns red past 100%.</summary>
public sealed class RingGauge : FrameworkElement
{
    const double Thickness = 6;

    static readonly Pen ArcPen;
    static readonly Pen OverPen = MakePen(new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)));

    static RingGauge()
    {
        var g = new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x8B, 0x5C, 0xF6), new Point(0, 0), new Point(1, 1));
        ArcPen = MakePen(g);
    }

    double value;
    bool hasValue;

    /// <summary>Fraction of the limit (0..1, may exceed 1). NaN shows an empty gauge.</summary>
    public double Value
    {
        get => value;
        set
        {
            this.value = value;
            hasValue = !double.IsNaN(value);
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2;

        var track = new Pen(TryFindResource("RingTrackBrush") as Brush ?? Brushes.Gray, Thickness);
        dc.DrawEllipse(null, track, c, r, r);

        double frac = hasValue ? Math.Max(0, Math.Min(1, value)) : 0;
        var pen = hasValue && value > 1 ? OverPen : ArcPen;
        if (frac >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
        }
        else if (frac > 0.002)
        {
            double a = frac * 2 * Math.PI;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(start, false, false);
                g.ArcTo(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        var text = hasValue ? $"{Math.Round(value * 100):0}%" : "—";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13, hasValue && value > 1 ? OverPen.Brush : TryFindResource("TextBrush") as Brush ?? Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
    }

    static Pen MakePen(Brush b)
    {
        var p = new Pen(b, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}

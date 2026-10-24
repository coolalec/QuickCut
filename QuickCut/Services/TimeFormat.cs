using System;
using System.Globalization;

namespace QuickCut.Services;

public static class TimeFormat
{
    /// <summary>mm:ss.fff, or h:mm:ss.fff past one hour.</summary>
    public static string Format(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ms = (long)Math.Round(seconds * 1000);
        long h = ms / 3600000, m = ms / 60000 % 60, s = ms / 1000 % 60, f = ms % 1000;
        return h > 0 ? $"{h}:{m:00}:{s:00}.{f:000}" : $"{m:00}:{s:00}.{f:000}";
    }

    public static string FormatShort(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    /// <summary>Accepts ss, ss.fff, mm:ss(.fff) and h:mm:ss(.fff).</summary>
    public static bool TryParse(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    public static string Ff(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);
}

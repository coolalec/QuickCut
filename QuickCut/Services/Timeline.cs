using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickCut.Services;

/// <summary>One source file placed on the combined timeline.</summary>
public sealed class Segment
{
    public Segment(MediaInfo media, double offset)
    {
        Media = media;
        Offset = offset;
    }

    public MediaInfo Media { get; }
    public double Offset { get; }
    public double Duration => Media.Duration;
    public double End => Offset + Media.Duration;
    public string Name => System.IO.Path.GetFileName(Media.Path);

    // Filled in asynchronously by MediaAnalyzer; read by the timeline control.
    public float[] Peaks { get; set; }
    public double PeakInterval { get; set; }
    public float[] RoughPeaks { get; set; } // from a few seeks, shown until Peaks is done
    public double RoughInterval { get; set; }
    public ThumbnailStrip Thumbs { get; set; }
}

/// <summary>A cut range inside one segment, in that file's own time, played back <see cref="Speed"/> times faster.</summary>
public readonly struct Piece
{
    public Piece(int segment, double start, double duration, double speed = 1)
    {
        Segment = segment;
        Start = start;
        Duration = duration;
        Speed = speed;
    }

    public int Segment { get; }
    public double Start { get; }
    public double Duration { get; }
    public double Speed { get; }
    public double OutDuration => Duration / Speed;
}

public enum RegionKind { Speed, Cut }

/// <summary>A stretch of the timeline that is sped up to last <see cref="Target"/> seconds, or left out of the output.</summary>
public sealed class TimeRegion
{
    public const double MinLength = 2;
    public const double MinTarget = 1;
    public const double MinSpeed = 1.1;

    public RegionKind Kind;
    public double Start, End;
    public double Target; // output seconds, Speed regions only

    public double Length => End - Start;
    public double Speed => Kind == RegionKind.Speed ? Length / Target : 1;
    public bool Contains(double t) => t >= Start && t < End;

    /// <summary>Shortest the region may become: a sped-up one stays at least <see cref="MinSpeed"/>× its target.</summary>
    public double MinLengthFor => Kind == RegionKind.Speed ? Math.Max(MinLength, Target * MinSpeed) : MinLength;

    public TimeRegion Clone() => (TimeRegion)MemberwiseClone();

    /// <summary>"12.5" for ×12.5; whole numbers from ×100 up.</summary>
    public static string FormatSpeed(double speed) => speed.ToString(speed < 100 ? "0.#" : "0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The free stretch <paramref name="r"/> may move or grow within: up to its neighbours or the timeline's ends.</summary>
    public static void Bounds(IEnumerable<TimeRegion> regions, TimeRegion r, double duration, out double lo, out double hi)
    {
        lo = 0;
        hi = duration;
        foreach (var o in regions)
        {
            if (o == r) continue;
            if (o.End <= r.Start + 1e-9) lo = Math.Max(lo, o.End);
            else if (o.Start >= r.End - 1e-9) hi = Math.Min(hi, o.Start);
        }
    }

    /// <summary>The free stretch around <paramref name="t"/>; false when <paramref name="t"/> is inside a region.</summary>
    public static bool Gap(IEnumerable<TimeRegion> regions, double t, double duration, out double lo, out double hi)
    {
        lo = 0;
        hi = duration;
        foreach (var o in regions)
        {
            if (o.Contains(t)) return false;
            if (o.End <= t) lo = Math.Max(lo, o.End);
            else hi = Math.Min(hi, o.Start);
        }
        return true;
    }

    /// <summary>The kept stretches of [<paramref name="from"/>, <paramref name="to"/>] in timeline time, with their speed.</summary>
    public static IEnumerable<(double From, double To, double Speed)> Spans(double from, double to, IEnumerable<TimeRegion> regions)
    {
        double t = from;
        foreach (var r in (regions ?? Enumerable.Empty<TimeRegion>()).OrderBy(x => x.Start))
        {
            if (r.End <= t || r.Start >= to) continue;
            if (r.Start > t) yield return (t, r.Start, 1);
            double a = Math.Max(t, r.Start), b = Math.Min(to, r.End);
            if (r.Kind == RegionKind.Speed) yield return (a, b, r.Speed);
            t = b;
        }
        if (to > t) yield return (t, to, 1);
    }

    /// <summary>How much shorter [<paramref name="from"/>, <paramref name="to"/>] comes out through speeding up and through cutting.</summary>
    public static void Savings(double from, double to, IEnumerable<TimeRegion> regions, out double sped, out double cut)
    {
        sped = cut = 0;
        foreach (var r in regions)
        {
            double len = Math.Min(to, r.End) - Math.Max(from, r.Start);
            if (len <= 0) continue;
            if (r.Kind == RegionKind.Cut) cut += len;
            else sped += len - len / r.Speed;
        }
    }

    public static double OutputDuration(double from, double to, IEnumerable<TimeRegion> regions) =>
        Spans(from, to, regions).Sum(s => (s.To - s.From) / s.Speed);
}

/// <summary>Consecutive video files joined into one continuous timeline.</summary>
public sealed class Timeline
{
    readonly List<Segment> segments = new List<Segment>();

    public Timeline(IEnumerable<MediaInfo> medias)
    {
        double offset = 0;
        foreach (var m in medias)
        {
            segments.Add(new Segment(m, offset));
            offset += m.Duration;
        }
        if (segments.Count == 0) throw new ArgumentException("No media", nameof(medias));
        Duration = offset;
    }

    public IReadOnlyList<Segment> Segments => segments;
    public double Duration { get; }
    public double Fps => segments[0].Media.Fps;
    public double FrameDuration => 1.0 / Fps;

    /// <summary>Whether the files can be joined by stream copy: same picture size, orientation and codecs.</summary>
    public static bool SameFormat(IList<MediaInfo> medias)
    {
        var f = medias[0];
        return medias.All(m => m.Width == f.Width && m.Height == f.Height && m.Rotation == f.Rotation && m.VideoCodec == f.VideoCodec
                               && m.HasAudio == f.HasAudio && m.AudioCodec == f.AudioCodec);
    }

    public int IndexAt(double t)
    {
        for (int i = 0; i < segments.Count; i++)
            if (t < segments[i].End) return i;
        return segments.Count - 1;
    }

    public void Locate(double t, out int index, out double local)
    {
        index = IndexAt(t);
        local = Math.Max(0, t - segments[index].Offset);
    }

    /// <summary>The pieces to export, split at file and region boundaries; cut regions are left out.</summary>
    public List<Piece> Slice(double from, double to, IEnumerable<TimeRegion> regions = null)
    {
        var list = new List<Piece>();
        foreach (var span in TimeRegion.Spans(from, to, regions))
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                double st = Math.Max(span.From, s.Offset), en = Math.Min(span.To, s.End);
                if (en - st > 0.001) list.Add(new Piece(i, st - s.Offset, en - st, span.Speed));
            }
        return list;
    }
}

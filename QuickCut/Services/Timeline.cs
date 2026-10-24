using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;

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
    public IReadOnlyList<BitmapSource> Thumbs { get; set; }
}

/// <summary>A cut range inside one segment, in that file's own time.</summary>
public readonly struct Piece
{
    public Piece(int segment, double start, double duration)
    {
        Segment = segment;
        Start = start;
        Duration = duration;
    }

    public int Segment { get; }
    public double Start { get; }
    public double Duration { get; }
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

    public bool IsUniform
    {
        get
        {
            var f = segments[0].Media;
            return segments.All(s => s.Media.Width == f.Width && s.Media.Height == f.Height && s.Media.Rotation == f.Rotation && s.Media.VideoCodec == f.VideoCodec);
        }
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

    public List<Piece> Slice(double from, double to)
    {
        var list = new List<Piece>();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            double st = Math.Max(from, s.Offset), en = Math.Min(to, s.End);
            if (en - st > 0.001) list.Add(new Piece(i, st - s.Offset, en - st));
        }
        return list;
    }
}

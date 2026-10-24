using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickCut.Services;

public enum AudioMode { None, Copy, Aac }

/// <summary>What sped-up regions sound like: silence, or the original sped up (same pitch).</summary>
public enum TimelapseAudio { Mute, Fast }

public sealed class AudioPlan
{
    public AudioMode Mode;
    public double Kbps;
    public bool SourceHasNoAudio;

    public string Describe() => Mode switch
    {
        AudioMode.Copy => "直接複製原始聲音（無損）",
        AudioMode.Aac => $"轉為 AAC {Kbps:0}k",
        _ => SourceHasNoAudio ? "來源影片沒有聲音" : "輸出不含聲音",
    };
}

public sealed class SizePlan
{
    public double VideoKbps;
    public double AudioKbps;
    public double EstimatedMB;
    public bool CappedBySource;
    public bool LowBitrate;
    public bool OverLimit;
}

/// <summary>Decides audio handling, output size and bitrate, and per-vendor encoder arguments.</summary>
public static class EncodePlanner
{
    const double AacMaxKbps = 128;
    const double AacMinKbps = 64;
    const double KbitPerMB = 8388.608; // 1 MiB = 8,388,608 bits
    const double SafetyMargin = 0.92;  // leaves room for VBR overshoot and container overhead
    const double ContainerOverhead = 1.01;
    const double TimelapseBitrateFactor = 1.5; // frames of a sped-up stretch differ far more than the source's own

    public static AudioPlan PlanAudio(Timeline tl, IList<Piece> pieces, bool remove, bool lossless)
    {
        if (remove) return new AudioPlan { Mode = AudioMode.None };
        var medias = pieces.Select(p => tl.Segments[p.Segment].Media).ToList();
        if (medias.Count == 0 || medias.Any(m => !m.HasAudio))
            return new AudioPlan { Mode = AudioMode.None, SourceHasNoAudio = true };

        // AAC/MP3 can go into MP4 untouched. Joining several files through the concat filter needs a re-encode.
        bool copyable = medias.All(m => m.AudioCodec == "aac" || m.AudioCodec == "mp3");
        if (pieces.Count > 1 && !lossless) copyable = false;
        if (pieces.Any(p => p.Speed > 1)) copyable = false; // silence or atempo goes through the filter graph

        if (copyable)
        {
            var kbps = medias.Max(m => m.AudioBitrate) / 1000.0;
            return new AudioPlan { Mode = AudioMode.Copy, Kbps = kbps > 0 ? kbps : AacMaxKbps };
        }
        // Re-encoding never spends more than the source had (a 96k original stays 96k, a 192k one becomes 128k).
        var source = medias.Max(m => m.AudioBitrate) / 1000.0;
        var aac = source > 0 ? Math.Max(AacMinKbps, Math.Min(AacMaxKbps, Math.Round(source))) : AacMaxKbps;
        return new AudioPlan { Mode = AudioMode.Aac, Kbps = aac };
    }

    /// <summary>
    /// Source video kbps per second of output, the ceiling for the encode. Sped-up pieces pack many seconds of source
    /// into each output second and compress worse, so they're allowed more than the source had.
    /// </summary>
    public static double SourceKbps(Timeline tl, IList<Piece> pieces)
    {
        double outDuration = pieces.Sum(p => p.OutDuration);
        if (outDuration <= 0) return 0;
        return pieces.Sum(p => tl.Segments[p.Segment].Media.VideoBitrate * p.OutDuration * (p.Speed > 1 ? TimelapseBitrateFactor : 1))
               / outDuration / 1000;
    }

    /// <summary>720p/1080p name the shorter edge, so a portrait 1080×1920 clip stays 1080×1920 at "1080p".</summary>
    public static void OutputSize(MediaInfo m, int targetHeight, out int width, out int height)
    {
        int shortSide = Math.Min(m.Width, m.Height);
        if (targetHeight > 0 && shortSide > targetHeight)
        {
            double scale = (double)targetHeight / shortSide;
            width = (int)Math.Round(m.Width * scale) & ~1;
            height = (int)Math.Round(m.Height * scale) & ~1;
        }
        else
        {
            width = m.Width & ~1;
            height = m.Height & ~1;
        }
    }

    public static SizePlan ComputeSize(double limitMB, double durationSec, double audioKbps, double sourceVideoKbps,
        double pixelRatio, string sourceCodec, string outputCodec, bool lossless)
    {
        durationSec = Math.Max(0.1, durationSec);
        var plan = new SizePlan { AudioKbps = audioKbps };

        if (lossless)
        {
            plan.VideoKbps = sourceVideoKbps;
        }
        else
        {
            double video = limitMB * KbitPerMB * SafetyMargin / durationSec - audioKbps;

            // Never spend more than the source had: a short clip shouldn't come out bigger than the original.
            double codecFactor = 1;
            if (sourceCodec == "h264" && outputCodec == "hevc") codecFactor = 0.7;
            else if (sourceCodec == "hevc" && outputCodec == "h264") codecFactor = 1.4;
            double cap = sourceVideoKbps * pixelRatio * codecFactor;
            if (cap > 0 && video > cap)
            {
                video = cap;
                plan.CappedBySource = true;
            }
            plan.VideoKbps = Math.Max(200, video);
            // Only a limit-imposed squeeze is worth warning about; a low-bitrate source just stays as it was.
            plan.LowBitrate = plan.VideoKbps < 800 && !plan.CappedBySource;
        }

        plan.EstimatedMB = (plan.VideoKbps + audioKbps) * durationSec / KbitPerMB * ContainerOverhead;
        plan.OverLimit = plan.EstimatedMB > limitMB;
        return plan;
    }

    public static string EncoderArgs(EncoderChoice enc, string codec, double videoKbps, double fps)
    {
        long b = (long)Math.Round(videoKbps);
        long max = (long)Math.Round(videoKbps * 1.2);
        long buf = b * 2;
        int gop = Math.Max(1, (int)Math.Round(fps * 2));
        string name = enc.Encoder(codec);

        string rate = $"-b:v {b}k -maxrate {max}k -bufsize {buf}k";
        string args = enc.Id switch
        {
            "nvenc" => $"-c:v {name} -preset p5 -tune hq -rc vbr {rate} -pix_fmt yuv420p",
            "amf" => $"-c:v {name} -quality quality -rc vbr_peak {rate} -pix_fmt nv12",
            "qsv" => $"-c:v {name} -preset slower {rate} -pix_fmt nv12",
            _ => codec == "hevc"
                ? $"-c:v libx265 -preset medium {rate} -pix_fmt yuv420p -x265-params log-level=error"
                : $"-c:v libx264 -preset medium {rate} -pix_fmt yuv420p",
        };
        args += $" -g {gop}";
        if (codec == "hevc") args += " -tag:v hvc1";
        return args;
    }
}

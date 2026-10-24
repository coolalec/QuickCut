using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCut.Services;

/// <summary>Builds the timeline's audio waveform and keyframe filmstrip in the background.</summary>
public static class MediaAnalyzer
{
    public const double PeakInterval = 0.05;
    const int PeakSampleRate = 4000;

    public static async Task<float[]> WaveformAsync(MediaInfo m, CancellationToken ct)
    {
        if (!m.HasAudio) return null;
        var args = $"-hide_banner -loglevel error -nostdin -i {ProcessUtil.Quote(m.Path)} -map 0:a:0 -vn -ac 1 -ar {PeakSampleRate} -f s16le pipe:1";
        var pcm = await ProcessUtil.RunBinaryAsync(FfmpegLocator.FfmpegPath, args, ct).ConfigureAwait(false);

        int perBucket = (int)(PeakSampleRate * PeakInterval);
        int samples = pcm.Length / 2;
        var peaks = new float[(samples + perBucket - 1) / perBucket];
        float max = 1;
        for (int b = 0; b < peaks.Length; b++)
        {
            int peak = 0;
            int end = Math.Min(samples, (b + 1) * perBucket);
            for (int i = b * perBucket; i < end; i++)
            {
                int v = Math.Abs((int)BitConverter.ToInt16(pcm, i * 2));
                if (v > peak) peak = v;
            }
            peaks[b] = peak;
            if (peak > max) max = peak;
        }
        // Square-root scaling keeps quiet engine noise visible next to horn blasts.
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = (float)Math.Sqrt(peaks[i] / max);
        return peaks;
    }

    /// <summary>Decodes keyframes only (fast) into small thumbnails of the given height.</summary>
    public static async Task<IReadOnlyList<BitmapSource>> ThumbnailsAsync(MediaInfo m, int thumbHeight, CancellationToken ct)
    {
        int h = thumbHeight & ~1;
        int w = (int)Math.Round(h * (double)m.Width / Math.Max(1, m.Height)) & ~1;
        var args = $"-hide_banner -loglevel error -nostdin -skip_frame nokey -i {ProcessUtil.Quote(m.Path)} " +
                   $"-map 0:v:0 -an -sn -fps_mode passthrough -vf scale={w}:{h} -pix_fmt bgra -f rawvideo pipe:1";
        var raw = await ProcessUtil.RunBinaryAsync(FfmpegLocator.FfmpegPath, args, ct).ConfigureAwait(false);

        int frameBytes = w * h * 4;
        var list = new List<BitmapSource>();
        for (int off = 0; off + frameBytes <= raw.Length; off += frameBytes)
        {
            var slice = new byte[frameBytes];
            Buffer.BlockCopy(raw, off, slice, 0, frameBytes);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, slice, w * 4);
            bmp.Freeze();
            list.Add(bmp);
        }
        return list;
    }
}

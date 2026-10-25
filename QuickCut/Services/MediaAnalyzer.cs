using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickCut.Services;

/// <summary>Builds the timeline's audio waveform and filmstrip thumbnails in the background.</summary>
public static class MediaAnalyzer
{
    public const double PeakInterval = 0.05;
    const int PeakSampleRate = 4000;
    const double RoughSnippet = 0.4; // seconds of audio read at each point of the rough waveform

    /// <summary>Peak level of every <see cref="PeakInterval"/> of the first audio track, scaled 0..1.</summary>
    public static async Task<float[]> WaveformAsync(MediaInfo m, CancellationToken ct)
    {
        if (!m.HasAudio) return null;
        // -vn/-sn/-dn on the input keep the other streams out of decoding; their packets are still read past.
        var args = $"-hide_banner -loglevel error -nostdin -vn -sn -dn -i {ProcessUtil.Quote(m.Path)} -map 0:a:0 -ac 1 -ar {PeakSampleRate} -f s16le pipe:1";
        var pcm = await ProcessUtil.RunBinaryAsync(FfmpegLocator.FfmpegPath, args, ct).ConfigureAwait(false);
        return Peaks(pcm, (int)(PeakSampleRate * PeakInterval));
    }

    /// <summary>
    /// A rough waveform to show until the full one is done: one peak per <c>Interval</c>, each from a short snippet.
    /// Like the filmstrip's thumbnails it seeks to the keyframe before each point and reads only from there,
    /// instead of reading the whole file.
    /// </summary>
    public static async Task<(float[] Peaks, double Interval)> RoughWaveformAsync(MediaInfo m, CancellationToken ct)
    {
        if (!m.HasAudio || m.Duration <= 0) return (null, 0);
        int k = Math.Max(20, Math.Min(120, (int)(m.Duration / 5)));
        double slot = m.Duration / k;
        int n = (int)(PeakSampleRate * RoughSnippet);
        string input = ProcessUtil.Quote(m.Path);

        var args = new StringBuilder("-hide_banner -loglevel error -nostdin ");
        var graph = new StringBuilder();
        for (int i = 0; i < k; i++)
        {
            double t = Math.Max(0, (i + 0.5) * slot - RoughSnippet / 2);
            args.Append($"-noaccurate_seek -ss {TimeFormat.Ff(t)} -t {TimeFormat.Ff(RoughSnippet)} -i {input} ");
            // Restamped (a keyframe seek can start before zero), then padded or trimmed to exactly n samples,
            // so snippet i is samples [i*n, (i+1)*n) of the output.
            graph.Append($"[{i}:a:0]aformat=channel_layouts=mono,aresample={PeakSampleRate},asetpts=N/SR/TB,apad=whole_len={n},atrim=end_sample={n}[a{i}];");
        }
        for (int i = 0; i < k; i++) graph.Append($"[a{i}]");
        graph.Append($"concat=n={k}:v=0:a=1");

        // The graph would overflow the 32K command line, so FFmpeg reads it from a file (-/option).
        var script = Path.Combine(Path.GetTempPath(), $"quickcut_{Guid.NewGuid():N}.graph");
        try
        {
            File.WriteAllText(script, graph.ToString());
            args.Append($"-/filter_complex {ProcessUtil.Quote(script)} -f s16le pipe:1");
            var pcm = await ProcessUtil.RunBinaryAsync(FfmpegLocator.FfmpegPath, args.ToString(), ct).ConfigureAwait(false);
            return (Peaks(pcm, n), slot);
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    /// <summary>
    /// The peak of every <paramref name="perBucket"/> samples of mono s16le PCM. The scale is fixed (full scale = 1),
    /// so the rough and full waveforms match; square-root scaling keeps quiet engine noise visible next to horn blasts.
    /// </summary>
    static float[] Peaks(byte[] pcm, int perBucket)
    {
        int samples = pcm.Length / 2;
        var peaks = new float[(samples + perBucket - 1) / perBucket];
        for (int b = 0; b < peaks.Length; b++)
        {
            int peak = 0;
            int end = Math.Min(samples, (b + 1) * perBucket);
            for (int i = b * perBucket; i < end; i++)
            {
                int v = Math.Abs((int)BitConverter.ToInt16(pcm, i * 2));
                if (v > peak) peak = v;
            }
            peaks[b] = (float)Math.Sqrt(peak / 32768.0);
        }
        return peaks;
    }

    /// <summary>
    /// Decodes the keyframe at or before <paramref name="t"/> into a small thumbnail of the given height:
    /// one seek and one decoded frame, so it costs the same anywhere in a long file. Null if nothing was decoded.
    /// </summary>
    public static async Task<BitmapSource> ThumbnailAtAsync(MediaInfo m, double t, int thumbHeight, CancellationToken ct)
    {
        int h = thumbHeight & ~1;
        int w = (int)Math.Round(h * (double)m.Width / Math.Max(1, m.Height)) & ~1;
        var args = $"-hide_banner -loglevel error -nostdin -noaccurate_seek -skip_frame nokey -ss {TimeFormat.Ff(t)} -i {ProcessUtil.Quote(m.Path)} " +
                   $"-map 0:v:0 -an -sn -frames:v 1 -vf scale={w}:{h} -pix_fmt bgra -f rawvideo pipe:1";
        var raw = await ProcessUtil.RunBinaryAsync(FfmpegLocator.FfmpegPath, args, ct).ConfigureAwait(false);

        int frameBytes = w * h * 4;
        if (raw.Length < frameBytes) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, raw, w * 4);
        bmp.Freeze();
        return bmp;
    }
}

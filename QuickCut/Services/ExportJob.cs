using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCut.Services;

public sealed class ExportRequest
{
    public Timeline Timeline;
    public double In;
    public double Out;
    public string Codec;          // h264 / hevc
    public EncoderChoice Encoder;
    public int TargetHeight;      // 0 = keep source resolution
    public bool RemoveAudio;
    public bool Lossless;
    public double LimitMB;
    public string OutputPath;
}

public sealed class ExportResult
{
    public bool Success;
    public bool Canceled;
    public bool OverLimit;
    public int Attempts;
    public long Bytes;
    public string OutputPath;
    public string Error;
}

/// <summary>Builds and runs the FFmpeg command for one cut, re-encoding once if the file overshoots the size limit.</summary>
public sealed class ExportJob
{
    public event Action<double, string> Progress; // fraction 0..1, speed text; raised on a background thread
    public event Action<string> Log;

    public async Task<ExportResult> RunAsync(ExportRequest r, CancellationToken ct)
    {
        var pieces = r.Timeline.Slice(r.In, r.Out);
        if (pieces.Count == 0) return new ExportResult { Error = "剪輯範圍是空的" };

        double duration = pieces.Sum(p => p.Duration);
        var first = r.Timeline.Segments[pieces[0].Segment].Media;
        var audio = EncodePlanner.PlanAudio(r.Timeline, pieces, r.RemoveAudio, r.Lossless);
        EncodePlanner.OutputSize(first, r.TargetHeight, out int w, out int h);

        double videoKbps = 0;
        if (!r.Lossless)
        {
            double srcKbps = pieces.Sum(p => r.Timeline.Segments[p.Segment].Media.VideoBitrate * p.Duration) / duration / 1000;
            double pixelRatio = (double)w * h / Math.Max(1, first.Width * first.Height);
            videoKbps = EncodePlanner.ComputeSize(r.LimitMB, duration, audio.Kbps, srcKbps, pixelRatio,
                first.VideoCodec, r.Codec, false).VideoKbps;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(r.OutputPath));
        string listFile = null;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                var args = BuildArgs(r, pieces, audio, w, h, videoKbps, ref listFile);
                Log?.Invoke("ffmpeg " + args);
                Progress?.Invoke(0, "");

                double outTime = 0;
                string speed = "";
                var res = await ProcessUtil.RunAsync(FfmpegLocator.FfmpegPath, args, ct,
                    line =>
                    {
                        if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                            && long.TryParse(line.Substring(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                            outTime = us / 1e6;
                        else if (line.StartsWith("speed=", StringComparison.Ordinal))
                            speed = line.Substring(6).Trim();
                        else if (line.StartsWith("progress=", StringComparison.Ordinal))
                            Progress?.Invoke(Math.Min(1, Math.Max(0, outTime / duration)), speed == "N/A" ? "" : speed);
                    },
                    line => Log?.Invoke(line)).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    TryDelete(r.OutputPath);
                    return new ExportResult { Canceled = true, Attempts = attempt };
                }
                if (res.ExitCode != 0 || !File.Exists(r.OutputPath))
                {
                    TryDelete(r.OutputPath);
                    var tail = string.Join("\n", res.StdErr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Reverse().Take(3).Reverse());
                    return new ExportResult { Error = string.IsNullOrEmpty(tail) ? $"FFmpeg 結束代碼 {res.ExitCode}" : tail, Attempts = attempt };
                }

                long bytes = new FileInfo(r.OutputPath).Length;
                double limitBytes = r.LimitMB * 1024 * 1024;
                if (r.Lossless || bytes <= limitBytes || attempt >= 2)
                    return new ExportResult { Success = true, Bytes = bytes, OutputPath = r.OutputPath, OverLimit = bytes > limitBytes, Attempts = attempt };

                // Hardware VBR overshot the target: scale the bitrate by how far we missed and try once more.
                double total = (videoKbps + audio.Kbps) * (limitBytes / bytes) * 0.96;
                videoKbps = Math.Max(150, total - audio.Kbps);
                Log?.Invoke($"檔案 {bytes / 1048576.0:0.0} MB 超過上限 {r.LimitMB:0.#} MB，改用 {videoKbps:0} kbps 重新編碼…");
            }
        }
        finally
        {
            if (listFile != null) TryDelete(listFile);
        }
    }

    static string BuildArgs(ExportRequest r, List<Piece> pieces, AudioPlan audio, int w, int h, double videoKbps, ref string listFile)
    {
        var tl = r.Timeline;
        var first = tl.Segments[pieces[0].Segment].Media;
        var sb = new StringBuilder("-hide_banner -nostdin -y -loglevel warning -progress pipe:1 -nostats ");
        string F(double d) => TimeFormat.Ff(d);
        string Q(string s) => ProcessUtil.Quote(s);

        if (r.Lossless)
        {
            if (pieces.Count == 1)
            {
                var p = pieces[0];
                sb.Append($"-ss {F(p.Start)} -i {Q(tl.Segments[p.Segment].Media.Path)} -t {F(p.Duration)} ");
            }
            else
            {
                listFile ??= WriteConcatList(tl, pieces);
                sb.Append($"-f concat -safe 0 -i {Q(listFile)} ");
            }
            sb.Append("-map 0:v:0 -c:v copy ");
            AppendAudio(sb, audio, "0:a:0?");
            sb.Append("-avoid_negative_ts make_zero ");
        }
        else if (pieces.Count == 1)
        {
            var p = pieces[0];
            sb.Append($"-hwaccel auto -ss {F(p.Start)} -t {F(p.Duration)} -i {Q(tl.Segments[p.Segment].Media.Path)} ");
            sb.Append("-map 0:v:0 ");
            if (w != first.Width || h != first.Height)
                sb.Append($"-vf scale={w}:{h}:flags=lanczos,setsar=1 ");
            sb.Append(EncodePlanner.EncoderArgs(r.Encoder, r.Codec, videoKbps, first.Fps)).Append(' ');
            AppendAudio(sb, audio, "0:a:0?");
        }
        else
        {
            // Several files: trim each input precisely, normalise, then join with the concat filter.
            var filter = new StringBuilder();
            var joins = new StringBuilder();
            bool withAudio = audio.Mode != AudioMode.None;
            string fps = first.Fps.ToString("0.###", CultureInfo.InvariantCulture);
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                sb.Append($"-hwaccel auto -ss {F(p.Start)} -t {F(p.Duration)} -i {Q(tl.Segments[p.Segment].Media.Path)} ");
                // Fit inside the first file's frame; a file of the other orientation gets black bars instead of being stretched.
                filter.Append($"[{i}:v:0]scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos," +
                              $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}[v{i}];");
                joins.Append($"[v{i}]");
                if (withAudio)
                {
                    filter.Append($"[{i}:a:0]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a{i}];");
                    joins.Append($"[a{i}]");
                }
            }
            filter.Append(joins).Append($"concat=n={pieces.Count}:v=1:a={(withAudio ? 1 : 0)}[v]");
            if (withAudio) filter.Append("[a]");
            sb.Append($"-filter_complex {Q(filter.ToString())} -map [v] ");
            sb.Append(EncodePlanner.EncoderArgs(r.Encoder, r.Codec, videoKbps, first.Fps)).Append(' ');
            if (withAudio) sb.Append($"-map [a] -c:a aac -b:a {audio.Kbps:0}k ");
            else sb.Append("-an ");
        }

        sb.Append("-movflags +faststart ").Append(Q(r.OutputPath));
        return sb.ToString();
    }

    static void AppendAudio(StringBuilder sb, AudioPlan audio, string map)
    {
        switch (audio.Mode)
        {
            case AudioMode.Copy: sb.Append($"-map {map} -c:a copy "); break;
            case AudioMode.Aac: sb.Append($"-map {map} -c:a aac -b:a {audio.Kbps:0}k "); break;
            default: sb.Append("-an "); break;
        }
    }

    static string WriteConcatList(Timeline tl, List<Piece> pieces)
    {
        var sb = new StringBuilder("ffconcat version 1.0\n");
        foreach (var p in pieces)
        {
            var seg = tl.Segments[p.Segment];
            sb.Append("file '").Append(seg.Media.Path.Replace("'", "'\\''")).Append("'\n");
            if (p.Start > 0.001) sb.Append("inpoint ").Append(TimeFormat.Ff(p.Start)).Append('\n');
            if (p.Start + p.Duration < seg.Duration - 0.001) sb.Append("outpoint ").Append(TimeFormat.Ff(p.Start + p.Duration)).Append('\n');
        }
        var path = Path.Combine(Path.GetTempPath(), $"quickcut_{Guid.NewGuid():N}.ffconcat");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QuickCut.Services;

public sealed class MediaInfo
{
    public string Path;
    public string FormatName;
    public long FileSize;
    public double Duration;
    public long FormatBitrate;
    public DateTime? CreationTime; // recording time stored in the file (UTC), if any

    public string VideoCodec;
    public int Width;    // as displayed: after the rotation flag and non-square pixels are applied
    public int Height;
    public int Rotation; // 0, 90, 180 or 270 (phones store portrait video as landscape plus a rotation flag)
    public double Fps;
    public long VideoBitrate;

    public bool HasAudio;
    public string AudioCodec;
    public long AudioBitrate;
    public int AudioChannels;
    public int AudioSampleRate;

    public string CodecDisplay => VideoCodec switch
    {
        "h264" => "H.264",
        "hevc" => "H.265",
        null => "—",
        _ => VideoCodec.ToUpperInvariant(),
    };

    public string AudioDisplay
    {
        get
        {
            if (!HasAudio) return "無";
            // Sample rate and bitrate shown separately: "48k" alone reads like a bitrate.
            var parts = new List<string> { AudioCodec?.ToUpperInvariant() };
            if (AudioSampleRate > 0) parts.Add($"{AudioSampleRate / 1000.0:0.#} kHz");
            parts.Add(AudioChannels == 1 ? "mono" : AudioChannels == 2 ? "stereo" : AudioChannels + "ch");
            if (AudioBitrate > 0) parts.Add($"{AudioBitrate / 1000.0:0} kbps");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Reads stream information with ffprobe.</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var args = $"-v error -print_format json -show_format -show_streams {ProcessUtil.Quote(path)}";
        var r = await ProcessUtil.RunAsync(FfmpegLocator.FfprobePath, args, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"無法讀取 {System.IO.Path.GetFileName(path)}：{r.StdErr.Trim()}");

        var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(r.StdOut) as Dictionary<string, object>
                   ?? throw new InvalidOperationException($"無法解析 {System.IO.Path.GetFileName(path)} 的影片資訊");

        var info = new MediaInfo { Path = path, FileSize = new FileInfo(path).Length };

        if (root.TryGetValue("format", out var fo) && fo is Dictionary<string, object> format)
        {
            info.FormatName = Str(format, "format_name");
            info.Duration = Dbl(format, "duration");
            info.FormatBitrate = Lng(format, "bit_rate");
            info.CreationTime = CreationTime(format);
        }

        Dictionary<string, object> video = null, audio = null;
        if (root.TryGetValue("streams", out var so) && so is object[] streams)
        {
            foreach (var o in streams)
            {
                if (o is not Dictionary<string, object> s) continue;
                var type = Str(s, "codec_type");
                if (type == "video" && video == null && !IsAttachedPicture(s)) video = s;
                else if (type == "audio" && audio == null) audio = s;
            }
        }

        if (video == null)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(path)} 沒有影像軌");

        info.VideoCodec = Str(video, "codec_name");
        // FFmpeg rotates frames upright and keeps non-square pixels when decoding, so every size used later is the displayed one.
        int w = (int)Lng(video, "width"), h = (int)Lng(video, "height");
        double sar = Rate(Str(video, "sample_aspect_ratio")?.Replace(':', '/'));
        if (sar > 0 && Math.Abs(sar - 1) > 0.01) w = (int)Math.Round(w * sar) & ~1;
        info.Rotation = Rotation(video);
        bool sideways = info.Rotation == 90 || info.Rotation == 270;
        info.Width = sideways ? h : w;
        info.Height = sideways ? w : h;
        info.Fps = Rate(Str(video, "avg_frame_rate"));
        if (info.Fps <= 0 || info.Fps > 240) info.Fps = Rate(Str(video, "r_frame_rate"));
        if (info.Fps <= 0 || info.Fps > 240) info.Fps = 30;
        info.VideoBitrate = Lng(video, "bit_rate");
        info.CreationTime ??= CreationTime(video);
        if (info.Duration <= 0) info.Duration = Dbl(video, "duration");

        if (audio != null)
        {
            info.HasAudio = true;
            info.AudioCodec = Str(audio, "codec_name");
            info.AudioBitrate = Lng(audio, "bit_rate");
            info.AudioChannels = (int)Lng(audio, "channels");
            info.AudioSampleRate = (int)Lng(audio, "sample_rate");
        }

        if (info.FormatBitrate <= 0 && info.Duration > 0)
            info.FormatBitrate = (long)(info.FileSize * 8 / info.Duration);
        if (info.VideoBitrate <= 0)
            info.VideoBitrate = Math.Max(0, info.FormatBitrate - info.AudioBitrate);

        if (info.Duration <= 0)
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(path)} 無法取得影片長度");
        return info;
    }

    /// <summary>Time of the last keyframe at or before <paramref name="t"/>; where a stream-copy cut really starts.</summary>
    public static async Task<double> KeyframeAtOrBeforeAsync(string path, double t, CancellationToken ct = default)
    {
        var from = Math.Max(0, t - 30);
        var args = $"-v error -select_streams v:0 -skip_frame nokey -read_intervals {TimeFormat.Ff(from)}%{TimeFormat.Ff(t + 0.05)} " +
                   $"-show_entries frame=pts_time -of csv=p=0 {ProcessUtil.Quote(path)}";
        var r = await ProcessUtil.RunAsync(FfmpegLocator.FfprobePath, args, ct).ConfigureAwait(false);
        double best = 0;
        foreach (var line in r.StdOut.Split('\n'))
            if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var pts) && pts <= t + 0.001 && pts > best)
                best = pts;
        return best;
    }

    /// <summary>The display matrix rotation (tags.rotate in older files), rounded to a quarter turn.</summary>
    static int Rotation(Dictionary<string, object> s)
    {
        double? deg = null;
        if (s.TryGetValue("side_data_list", out var sd) && sd is object[] list)
            foreach (var o in list)
                if (o is Dictionary<string, object> d && Str(d, "side_data_type") == "Display Matrix" && d.ContainsKey("rotation"))
                    deg = Dbl(d, "rotation");
        if (deg == null && s.TryGetValue("tags", out var t) && t is Dictionary<string, object> tags && tags.ContainsKey("rotate"))
            deg = Dbl(tags, "rotate");
        int q = (int)Math.Round((deg ?? 0) / 90) * 90;
        return (q % 360 + 360) % 360;
    }

    static bool IsAttachedPicture(Dictionary<string, object> s) =>
        s.TryGetValue("disposition", out var d) && d is Dictionary<string, object> disp && Lng(disp, "attached_pic") == 1;

    /// <summary>tags.creation_time, ignoring the zero/epoch dates some cameras write when their clock isn't set.</summary>
    static DateTime? CreationTime(Dictionary<string, object> d)
    {
        if (!(d.TryGetValue("tags", out var t) && t is Dictionary<string, object> tags)) return null;
        var text = Str(tags, "creation_time");
        if (string.IsNullOrEmpty(text)) return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) return null;
        return when.Year >= 1990 ? when : (DateTime?)null;
    }

    static string Str(Dictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    static double Dbl(Dictionary<string, object> d, string key) =>
        double.TryParse(Str(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static long Lng(Dictionary<string, object> d, string key) =>
        long.TryParse(Str(d, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long)Dbl(d, key);

    static double Rate(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d > 0)
            return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

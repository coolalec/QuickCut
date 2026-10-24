using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QuickCut.Services;

/// <summary>Saves one frame of a file as a PNG at the source resolution.</summary>
public static class FrameGrabber
{
    public static async Task SaveAsync(MediaInfo m, double local, string pngPath)
    {
        // Seek a quarter frame early so the frame sitting exactly on `local` is the first one out (as FrameDecoder does).
        double ss = Math.Max(0, local - 0.25 / m.Fps);
        var args = $"-hide_banner -loglevel error -nostdin -y -ss {TimeFormat.Ff(ss)} -i {ProcessUtil.Quote(m.Path)} " +
                   $"-map 0:v:0 -frames:v 1 -update 1 {ProcessUtil.Quote(pngPath)}";
        var r = await ProcessUtil.RunAsync(FfmpegLocator.FfmpegPath, args).ConfigureAwait(false);
        if (r.ExitCode != 0 || !File.Exists(pngPath))
        {
            var tail = r.StdErr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new InvalidOperationException(tail ?? $"FFmpeg 結束代碼 {r.ExitCode}");
        }
    }
}

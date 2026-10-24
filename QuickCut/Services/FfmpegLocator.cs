using System;
using System.Collections.Generic;
using System.IO;

namespace QuickCut.Services;

/// <summary>Finds ffmpeg.exe / ffprobe.exe next to the app, in a dev checkout, or on PATH.</summary>
public static class FfmpegLocator
{
    public static string FfmpegPath { get; private set; }
    public static string FfprobePath { get; private set; }

    public static bool Locate()
    {
        foreach (var dir in CandidateDirs(AppDomain.CurrentDomain.BaseDirectory))
        {
            var ff = Path.Combine(dir, "ffmpeg.exe");
            var fp = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ff) && File.Exists(fp))
            {
                FfmpegPath = ff;
                FfprobePath = fp;
                return true;
            }
        }
        return false;
    }

    static IEnumerable<string> CandidateDirs(string baseDir)
    {
        yield return Path.Combine(baseDir, "ffmpeg", "bin");
        yield return Path.Combine(baseDir, "ffmpeg");
        yield return baseDir;

        // Development layout: an FFmpeg build folder (ffmpeg-*) somewhere above the output directory.
        var d = new DirectoryInfo(baseDir);
        for (int i = 0; d != null && i < 6; i++, d = d.Parent)
        {
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories("ffmpeg*"); }
            catch { continue; }
            foreach (var s in subs)
                yield return Path.Combine(s.FullName, "bin");
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';'))
            if (p.Trim().Length > 0)
                yield return p.Trim();
    }
}

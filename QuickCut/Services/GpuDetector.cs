using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCut.Services;

public sealed class EncoderChoice
{
    // Properties rather than fields so the encoder list can data-bind.
    public string Id { get; set; }          // nvenc / amf / qsv / cpu
    public string DisplayName { get; set; }
    public string H264 { get; set; }
    public string Hevc { get; set; }
    public bool H264Ok { get; set; }
    public bool HevcOk { get; set; }
    public bool IsHardware => Id != "cpu";

    public string Encoder(string codec) => codec == "hevc" ? Hevc : H264;
    public bool Supports(string codec) => codec == "hevc" ? HevcOk : H264Ok;
    public override string ToString() => DisplayName;
}

/// <summary>
/// Lists the graphics adapters via WMI, then confirms each vendor's encoder with a tiny test encode,
/// since a driver can be present without a working encoder (old cards, remote sessions, VMs).
/// </summary>
public static class GpuDetector
{
    public static async Task<(List<string> Adapters, List<EncoderChoice> Encoders)> DetectAsync()
    {
        var adapters = await Task.Run(QueryAdapters).ConfigureAwait(false);
        var joined = string.Join(" ", adapters).ToUpperInvariant();

        // Priority order: discrete NVIDIA first, then AMD, then Intel Quick Sync.
        var candidates = new List<EncoderChoice>();
        if (joined.Contains("NVIDIA"))
            candidates.Add(new EncoderChoice { Id = "nvenc", DisplayName = "NVIDIA NVENC", H264 = "h264_nvenc", Hevc = "hevc_nvenc" });
        if (joined.Contains("AMD") || joined.Contains("RADEON") || joined.Contains("ATI "))
            candidates.Add(new EncoderChoice { Id = "amf", DisplayName = "AMD AMF", H264 = "h264_amf", Hevc = "hevc_amf" });
        if (joined.Contains("INTEL"))
            candidates.Add(new EncoderChoice { Id = "qsv", DisplayName = "Intel Quick Sync", H264 = "h264_qsv", Hevc = "hevc_qsv" });

        await Task.WhenAll(candidates.Select(async c =>
        {
            var results = await Task.WhenAll(TestAsync(c.H264), TestAsync(c.Hevc)).ConfigureAwait(false);
            c.H264Ok = results[0];
            c.HevcOk = results[1];
        })).ConfigureAwait(false);

        var encoders = candidates.Where(c => c.H264Ok || c.HevcOk).ToList();
        encoders.Add(new EncoderChoice { Id = "cpu", DisplayName = "CPU 軟體編碼", H264 = "libx264", Hevc = "libx265", H264Ok = true, HevcOk = true });
        return (adapters, encoders);
    }

    static List<string> QueryAdapters()
    {
        var list = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"] as string;
                var vendor = mo["AdapterCompatibility"] as string;
                if (!string.IsNullOrWhiteSpace(name)) list.Add((vendor + " " + name).Trim());
            }
        }
        catch { }
        return list;
    }

    static async Task<bool> TestAsync(string encoder)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var args = "-hide_banner -loglevel error -nostdin -f lavfi -i color=c=black:s=320x240:r=30 -t 0.2 " +
                       $"-pix_fmt nv12 -c:v {encoder} -f null -";
            var r = await ProcessUtil.RunAsync(FfmpegLocator.FfmpegPath, args, cts.Token).ConfigureAwait(false);
            return r.ExitCode == 0 && !cts.IsCancellationRequested;
        }
        catch
        {
            return false;
        }
    }
}

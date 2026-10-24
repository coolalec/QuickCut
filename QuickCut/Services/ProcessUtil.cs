using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickCut.Services;

public sealed class ProcessResult
{
    public int ExitCode;
    public string StdOut;
    public string StdErr;
}

/// <summary>FFmpeg (or ffprobe) could not be started: the file is gone, e.g. the ffmpeg folder was deleted while the app runs.</summary>
public sealed class FfmpegMissingException : Exception
{
    public FfmpegMissingException(string path, Exception inner)
        : base($"找不到 {(string.IsNullOrEmpty(path) ? "FFmpeg" : Path.GetFileName(path))}{(string.IsNullOrEmpty(path) ? "" : $"（{path}）")}。" +
               "請重新解壓 QuickCut 的 zip，或把 ffmpeg 資料夾放回程式旁。", inner) { }
}

public static class ProcessUtil
{
    const int DllNotFound = unchecked((int)0xC0000135);        // STATUS_DLL_NOT_FOUND
    const int EntryPointNotFound = unchecked((int)0xC0000139); // STATUS_ENTRYPOINT_NOT_FOUND: a DLL from another FFmpeg version

    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>Starts the process, turning "file not found" into a message that says FFmpeg is missing.</summary>
    public static void Start(Process p)
    {
        try { p.Start(); }
        catch (Win32Exception ex) { throw new FfmpegMissingException(p.StartInfo.FileName, ex); }
        catch (InvalidOperationException ex) when (string.IsNullOrEmpty(p.StartInfo.FileName)) { throw new FfmpegMissingException(null, ex); }
    }

    /// <summary>What a failed exit code means when FFmpeg printed nothing, e.g. a shared build missing one of its DLLs.</summary>
    public static string DescribeExit(int code) => code == DllNotFound || code == EntryPointNotFound
        ? "FFmpeg 檔案不完整（缺少 DLL 或版本不符），請重新解壓 QuickCut 的 zip。"
        : $"FFmpeg 結束代碼 {code}";

    public static ProcessStartInfo CreateInfo(string exe, string args) => new ProcessStartInfo(exe, args)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    /// <summary>Runs a process to completion, capturing text output line by line. Cancelling kills the process.</summary>
    public static async Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct = default,
        Action<string> onStdOut = null, Action<string> onStdErr = null)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var p = new Process { StartInfo = CreateInfo(exe, args), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (stdout) stdout.AppendLine(e.Data); onStdOut?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (stderr) stderr.AppendLine(e.Data); onStdErr?.Invoke(e.Data); };
        p.Exited += (_, _) => exited.TrySetResult(true);

        Start(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => Kill(p)))
            await exited.Task.ConfigureAwait(false);

        p.WaitForExit(); // flushes the async output readers
        return new ProcessResult { ExitCode = p.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString() };
    }

    /// <summary>Runs a process and returns its raw stdout bytes (for PCM / rawvideo pipes).</summary>
    public static async Task<byte[]> RunBinaryAsync(string exe, string args, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = CreateInfo(exe, args) };
        Start(p);
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        using (ct.Register(() => Kill(p)))
        {
            using var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
            p.WaitForExit();
            ct.ThrowIfCancellationRequested();
            return ms.ToArray();
        }
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
    }
}

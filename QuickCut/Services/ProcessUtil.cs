using System;
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

public static class ProcessUtil
{
    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

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

        p.Start();
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
        p.Start();
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

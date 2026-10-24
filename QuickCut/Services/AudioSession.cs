using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuickCut.Services;

/// <summary>
/// Plays the timeline's audio from a start position through WinMM waveOut, decoding with FFmpeg
/// and rolling into the next file at EOF. Its playback position doubles as the preview's master clock.
/// </summary>
sealed class AudioSession : IDisposable
{
    const int Rate = 48000;
    const int BytesPerSecond = Rate * 4;           // stereo s16le
    const int BufferBytes = BytesPerSecond / 20;   // 50 ms
    const int BufferCount = 6;

    readonly Timeline timeline;
    readonly double start, speed;
    readonly Thread thread;
    readonly AutoResetEvent done = new AutoResetEvent(false);
    readonly object gate = new object();
    volatile bool stopping;

    IntPtr handle;
    Process decoder;
    Stream pcm;
    int segment;

    public AudioSession(Timeline tl, double start, double speed)
    {
        timeline = tl;
        this.start = start;
        this.speed = speed;
        thread = new Thread(Run) { IsBackground = true, Name = "PreviewAudio" };
        thread.Start();
    }

    public volatile bool Started;
    public volatile bool Failed;
    public volatile bool Ended;
    public string Error;

    /// <summary>Media time currently coming out of the speakers.</summary>
    public double Clock
    {
        get
        {
            if (!Started) return start;
            lock (gate)
            {
                if (handle == IntPtr.Zero) return start;
                var t = new MMTIME { wType = TIME_BYTES };
                if (waveOutGetPosition(handle, ref t, Marshal.SizeOf(typeof(MMTIME))) != 0) return start;
                double seconds = t.wType == TIME_SAMPLES ? (double)(uint)t.value / Rate : (double)(uint)t.value / BytesPerSecond;
                return start + seconds * speed;
            }
        }
    }

    void Run()
    {
        var headers = new IntPtr[BufferCount];
        int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        int flagsOffset = Marshal.OffsetOf(typeof(WAVEHDR), nameof(WAVEHDR.dwFlags)).ToInt32();
        var chunk = new byte[BufferBytes];
        try
        {
            timeline.Locate(start, out segment, out var local);
            OpenDecoder(local);

            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, nAvgBytesPerSec = BytesPerSecond, nBlockAlign = 4, wBitsPerSample = 16 };
            int r = waveOutOpen(out var h, new IntPtr(-1), ref fmt, done.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
            if (r != 0) throw new InvalidOperationException($"無法開啟音效裝置（waveOut {r}）");
            lock (gate) handle = h;
            waveOutPause(h);

            for (int i = 0; i < BufferCount; i++)
            {
                var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BufferBytes), dwBufferLength = BufferBytes };
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                Marshal.StructureToPtr(hdr, headers[i], false);
                waveOutPrepareHeader(h, headers[i], hdrSize);
            }

            // Prime every buffer before starting so the clock never runs ahead of real sound.
            var inFlight = new bool[BufferCount];
            bool exhausted = false;
            for (int i = 0; i < BufferCount && !stopping && !exhausted; i++)
                exhausted = !(inFlight[i] = Fill(headers[i], chunk, hdrSize));
            if (!inFlight[0]) throw new InvalidOperationException("沒有可播放的聲音");
            waveOutRestart(h);
            Started = true;

            while (!stopping)
            {
                done.WaitOne(30);
                int busy = 0;
                for (int i = 0; i < BufferCount && !stopping; i++)
                {
                    if (inFlight[i] && (Marshal.ReadInt32(headers[i], flagsOffset) & WHDR_DONE) != 0)
                        inFlight[i] = false;
                    if (!inFlight[i] && !exhausted)
                        exhausted = !(inFlight[i] = Fill(headers[i], chunk, hdrSize));
                    if (inFlight[i]) busy++;
                }
                if (exhausted && busy == 0)
                {
                    Ended = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Failed = true;
        }
        finally
        {
            KillDecoder();
            lock (gate)
            {
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                    foreach (var p in headers)
                    {
                        if (p == IntPtr.Zero) continue;
                        waveOutUnprepareHeader(handle, p, hdrSize);
                        Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
                        Marshal.FreeHGlobal(p);
                    }
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
            }
        }
    }

    /// <summary>Decodes the next 50 ms into a header and queues it. False once every file is exhausted.</summary>
    bool Fill(IntPtr header, byte[] chunk, int hdrSize)
    {
        int n = ReadPcm(chunk);
        if (n <= 0) return false;
        var hdr = Marshal.PtrToStructure<WAVEHDR>(header);
        Marshal.Copy(chunk, 0, hdr.lpData, n);
        hdr.dwBufferLength = n;
        hdr.dwFlags &= ~WHDR_DONE;
        Marshal.StructureToPtr(hdr, header, false);
        waveOutWrite(handle, header, hdrSize);
        return true;
    }

    int ReadPcm(byte[] buf)
    {
        int off = 0;
        while (off < buf.Length && !stopping)
        {
            int n = 0;
            try { n = pcm?.Read(buf, off, buf.Length - off) ?? 0; }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (n > 0) { off += n; continue; }
            if (stopping) break;

            KillDecoder();
            if (segment + 1 >= timeline.Segments.Count) break;
            segment++;
            OpenDecoder(0);
        }
        return off - off % 4; // whole stereo samples only
    }

    void OpenDecoder(double local)
    {
        var m = timeline.Segments[segment].Media;
        var tempo = speed switch
        {
            0.25 => "-af atempo=0.5,atempo=0.5 ",
            1 => "",
            _ => $"-af atempo={speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} ",
        };
        var seek = local > 0 ? $"-ss {TimeFormat.Ff(local)} " : "";
        var args = $"-hide_banner -loglevel error -nostdin {seek}-i {ProcessUtil.Quote(m.Path)} -map 0:a:0 -vn -sn -dn -ac 2 -ar {Rate} {tempo}-f s16le pipe:1";
        decoder = new Process { StartInfo = ProcessUtil.CreateInfo(FfmpegLocator.FfmpegPath, args) };
        decoder.Start();
        decoder.ErrorDataReceived += (_, _) => { };
        decoder.BeginErrorReadLine();
        pcm = decoder.StandardOutput.BaseStream;
    }

    void KillDecoder()
    {
        var d = decoder;
        decoder = null;
        pcm = null;
        if (d == null) return;
        ProcessUtil.Kill(d);
        d.Dispose();
    }

    public void Dispose()
    {
        stopping = true;
        var d = decoder;
        if (d != null) ProcessUtil.Kill(d);
        done.Set();
        thread.Join(500);
    }

    // ── WinMM ──
    const int CALLBACK_EVENT = 0x50000;
    const int WHDR_DONE = 1;
    const int TIME_SAMPLES = 2;
    const int TIME_BYTES = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public int dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MMTIME
    {
        [FieldOffset(0)] public int wType;
        [FieldOffset(4)] public int value;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, IntPtr uDeviceID, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME time, int size);
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickCut.Services;

/// <summary>
/// Decodes one file from a start position into raw BGRA frames through an FFmpeg pipe.
/// Frame times assume constant frame rate, which is what cameras and phones normally record.
/// </summary>
sealed class FrameDecoder : IDisposable
{
    readonly Process process;
    readonly Stream stdout;
    readonly int frameBytes;
    readonly double fps;
    readonly long firstIndex;
    long index;

    public FrameDecoder(string path, double start, int width, int height, double fps)
    {
        this.fps = fps;
        frameBytes = width * height * 4;

        // Seek a quarter frame early so the frame sitting exactly on `start` is never rounded away.
        double ss = Math.Max(0, start - 0.25 / fps);
        firstIndex = ss <= 0 ? 0 : (long)Math.Ceiling(ss * fps - 1e-6);

        var seek = ss > 0 ? $"-ss {TimeFormat.Ff(ss)} " : "";
        // The canvas follows the first file; a file of the other orientation is fitted inside it with black bars, as on export.
        var args = $"-hide_banner -loglevel error -nostdin -hwaccel auto {seek}-i {ProcessUtil.Quote(path)} " +
                   $"-map 0:v:0 -an -sn -dn -fps_mode passthrough " +
                   $"-vf scale={width}:{height}:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2 " +
                   "-pix_fmt bgra -f rawvideo pipe:1";
        process = new Process { StartInfo = ProcessUtil.CreateInfo(FfmpegLocator.FfmpegPath, args) };
        process.Start();
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        stdout = process.StandardOutput.BaseStream;
    }

    /// <summary>File-relative time of the frame most recently read.</summary>
    public double LastFrameTime => (firstIndex + index - 1) / fps;

    public bool Read(byte[] buffer)
    {
        int off = 0;
        try
        {
            while (off < frameBytes)
            {
                int n = stdout.Read(buffer, off, frameBytes - off);
                if (n <= 0) return false;
                off += n;
            }
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        index++;
        return true;
    }

    public void Dispose()
    {
        ProcessUtil.Kill(process);
        process.Dispose();
    }
}

/// <summary>
/// Silent preview player. A worker thread owns the decoder and paces frames against a stopwatch;
/// the UI only posts commands (seek / step / play / pause), which are coalesced.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    const int MaxPreviewSide = 1280; // longer edge, so portrait video previews at 720×1280

    readonly Dispatcher ui;
    readonly object gate = new object();
    readonly AutoResetEvent signal = new AutoResetEvent(false);
    readonly Stopwatch clock = Stopwatch.StartNew();
    volatile bool disposed;

    // Shared with the UI thread, guarded by `gate`.
    Timeline timeline;
    WriteableBitmap[] targets;     // one per file, shared by files of the same size; only touched on the UI thread
    (int W, int H)[] targetSizes;  // their sizes, for the worker
    int generation;
    double? pendingSeek;
    int pendingStep;
    bool playing;
    double speed = 1;
    bool reanchor;
    bool muted;

    // Owned by the worker thread.
    volatile FrameDecoder decoder;
    int decoderSegment;
    (int W, int H)[] sizes;
    int workerGeneration = -1;
    byte[] buffer;
    int width, height;
    bool held;
    double heldTime, currentTime;
    double anchorWall, anchorMedia, lastPresentWall;
    AudioSession audio;      // while playing with sound, its position is the clock
    bool audioTried;         // audio already started (or ruled out) for this run of playback
    double audioStartWall;

    public PreviewPlayer(Dispatcher ui)
    {
        this.ui = ui;
        new Thread(Run) { IsBackground = true, Name = "Preview" }.Start();
    }

    public event Action<double> PositionChanged;
    public event Action<bool> PlayingChanged;
    public event Action<string> Log;

    /// <summary>Raised on the UI thread when playback crosses into a file of a different size or orientation.</summary>
    public event Action BitmapChanged;

    /// <summary>The picture currently shown; each file previews at its own proportions.</summary>
    public WriteableBitmap Bitmap { get; private set; }
    WriteableBitmap[] shownTargets; // UI thread: the set Present may still write to
    public double Position { get; private set; }
    public bool IsPlaying { get { lock (gate) return playing; } }
    public double Speed { get { lock (gate) return speed; } }

    /// <summary>
    /// Shows <paramref name="tl"/> from <paramref name="start"/>. Until its first frame is decoded the previous
    /// picture stays up (BitmapChanged then switches to the new bitmap), so reloading doesn't flash blank.
    /// </summary>
    public void Load(Timeline tl, double start = 0)
    {
        int n = tl.Segments.Count;
        var bitmaps = new WriteableBitmap[n];
        var bitmapSizes = new (int W, int H)[n];
        for (int i = 0; i < n; i++)
        {
            var m = tl.Segments[i].Media;
            double fit = Math.Min(1, (double)MaxPreviewSide / Math.Max(m.Width, m.Height));
            var size = ((int)Math.Round(m.Width * fit) & ~1, (int)Math.Round(m.Height * fit) & ~1);
            int same = Array.IndexOf(bitmapSizes, size, 0, i);
            bitmaps[i] = same >= 0 ? bitmaps[same] : new WriteableBitmap(size.Item1, size.Item2, 96, 96, PixelFormats.Bgr32, null);
            bitmapSizes[i] = size;
        }
        shownTargets = bitmaps;
        Position = start;
        lock (gate)
        {
            timeline = tl;
            targets = bitmaps;
            targetSizes = bitmapSizes;
            generation++;
            pendingSeek = start;
            pendingStep = 0;
        }
        SetPlaying(false);
        signal.Set();
    }

    public void Unload()
    {
        shownTargets = null;
        lock (gate)
        {
            timeline = null;
            targets = null;
            generation++;
        }
        SetPlaying(false);
        signal.Set();
    }

    public void Seek(double t)
    {
        lock (gate) { pendingSeek = t; pendingStep = 0; reanchor = true; }
        signal.Set();
    }

    public void Step(int frames)
    {
        SetPlaying(false);
        lock (gate) pendingStep += frames;
        signal.Set();
    }

    public void Play() => SetPlaying(true);
    public void Pause() => SetPlaying(false);
    public void Toggle() => SetPlaying(!IsPlaying);

    public void SetSpeed(double value)
    {
        lock (gate) { speed = value; reanchor = true; }
        signal.Set();
    }

    public void SetMuted(bool value)
    {
        lock (gate) { muted = value; reanchor = true; }
        signal.Set();
    }

    void SetPlaying(bool value)
    {
        bool changed;
        lock (gate)
        {
            if (value && timeline == null) value = false;
            changed = playing != value;
            playing = value;
            reanchor = true;
        }
        signal.Set();
        if (changed) RaiseOnUi(() => PlayingChanged?.Invoke(value));
    }

    void Run()
    {
        while (!disposed)
        {
            Timeline tl;
            WriteableBitmap[] bmps;
            (int W, int H)[] bmpSizes;
            int gen, step;
            double? seek;
            bool play, anchor, mute;
            double spd;
            lock (gate)
            {
                tl = timeline; bmps = targets; bmpSizes = targetSizes; gen = generation;
                seek = pendingSeek; pendingSeek = null;
                step = pendingStep; pendingStep = 0;
                play = playing; spd = speed;
                anchor = reanchor; reanchor = false;
                mute = muted;
            }

            if (gen != workerGeneration)
            {
                StopAudio();
                CloseDecoder();
                workerGeneration = gen;
                held = false;
                currentTime = 0;
                sizes = bmpSizes; // the frame buffer follows the open file's size (OpenDecoder)
            }

            if (tl == null || bmps == null)
            {
                StopAudio();
                signal.WaitOne();
                continue;
            }

            try
            {
                if (seek.HasValue || step != 0) StopAudio();

                if (seek.HasValue)
                {
                    SeekTo(tl, seek.Value);
                    if (NextFrame(tl)) Present(bmps);
                    ResetAnchor();
                    continue;
                }

                if (step > 0)
                {
                    for (int i = 0; i < step && NextFrame(tl); i++)
                    {
                        if (i == step - 1) Present(bmps);
                        else Consume();
                    }
                    continue;
                }

                if (step < 0)
                {
                    SeekTo(tl, Math.Max(0, currentTime + step * tl.FrameDuration));
                    if (NextFrame(tl)) Present(bmps);
                    continue;
                }

                if (play)
                {
                    if (anchor)
                    {
                        StopAudio();
                        ResetAnchor();
                    }
                    if (!audioTried)
                    {
                        audioTried = true;
                        if (!mute && tl.Segments.All(s => s.Media.HasAudio))
                        {
                            audio = new AudioSession(tl, currentTime, spd);
                            audioStartWall = Now;
                        }
                    }
                    CheckAudio();
                    if (!NextFrame(tl))
                    {
                        SetPlaying(false);
                        continue;
                    }

                    double now = Now;
                    double mediaNow = audio != null ? audio.Clock : anchorMedia + (now - anchorWall) * spd;
                    double wait = (heldTime - mediaNow) / spd;
                    if (wait > 0.002)
                    {
                        signal.WaitOne(TimeSpan.FromSeconds(Math.Min(wait, 0.05)));
                        continue;
                    }
                    if (-wait * spd > 0.5)
                    {
                        // Decoding can't keep up (e.g. 4K at 2x on a slow machine).
                        if (audio != null)
                        {
                            // Sound keeps going, so jump the picture ahead to meet it.
                            SeekTo(tl, mediaNow + 0.3 * spd);
                            continue;
                        }
                        anchorWall = now;
                        anchorMedia = heldTime;
                    }
                    if (-wait > 2 * tl.FrameDuration && now - lastPresentWall < 0.1)
                        Consume(); // drop a late frame, but still refresh the picture regularly
                    else
                        Present(bmps);
                    continue;
                }

                StopAudio();
                signal.WaitOne();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Preview error: " + ex);
                StopAudio();
                CloseDecoder();
                SetPlaying(false);
                signal.WaitOne(200);
            }
        }
        StopAudio();
        CloseDecoder();
    }

    /// <summary>Falls back to the stopwatch clock if sound fails, never starts, or runs out.</summary>
    void CheckAudio()
    {
        if (audio == null) return;
        string reason = audio.Failed ? "預覽聲音無法播放：" + audio.Error
            : !audio.Started && Now - audioStartWall > 2 ? "預覽聲音啟動逾時，改為無聲播放"
            : null;
        if (reason == null && !audio.Ended) return;
        anchorWall = Now;
        anchorMedia = audio.Started && !audio.Failed ? audio.Clock : currentTime;
        StopAudio(keepTried: true);
        if (reason != null) RaiseOnUi(() => Log?.Invoke(reason));
    }

    void StopAudio(bool keepTried = false)
    {
        audioTried = keepTried;
        var a = audio;
        audio = null;
        a?.Dispose();
    }

    double Now => clock.Elapsed.TotalSeconds;

    void ResetAnchor()
    {
        anchorWall = Now;
        anchorMedia = currentTime;
    }

    void SeekTo(Timeline tl, double t)
    {
        t = Math.Max(0, Math.Min(t, tl.Duration - 0.5 * tl.FrameDuration));
        tl.Locate(t, out var seg, out var local);
        OpenDecoder(tl, seg, local);
        held = false;

        // A seek that lands past the last decodable frame yields nothing; back off a little.
        if (!NextFrame(tl))
        {
            OpenDecoder(tl, seg, Math.Max(0, local - 1));
            NextFrame(tl);
        }
    }

    /// <summary>Makes sure an unpresented frame sits in the buffer, rolling over into the next file at EOF.</summary>
    bool NextFrame(Timeline tl)
    {
        if (held) return true;
        while (decoder != null)
        {
            if (decoder.Read(buffer))
            {
                held = true;
                heldTime = tl.Segments[decoderSegment].Offset + decoder.LastFrameTime;
                return true;
            }
            CloseDecoder();
            if (decoderSegment + 1 < tl.Segments.Count)
                OpenDecoder(tl, decoderSegment + 1, 0);
        }
        return false;
    }

    void Consume()
    {
        held = false;
        currentTime = heldTime;
    }

    void Present(WriteableBitmap[] set)
    {
        Consume();
        lastPresentWall = Now;
        double t = currentTime;
        var bmp = set[decoderSegment];
        var buf = buffer;
        int w = width, h = height;
        try
        {
            ui.Invoke(() =>
            {
                if (!ReferenceEquals(set, shownTargets)) return; // a different timeline was loaded meanwhile
                if (!ReferenceEquals(bmp, Bitmap))
                {
                    Bitmap = bmp;
                    BitmapChanged?.Invoke();
                }
                bmp.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
                Position = t;
                PositionChanged?.Invoke(t);
            }, DispatcherPriority.Render);
        }
        catch (OperationCanceledException) { }
    }

    void OpenDecoder(Timeline tl, int seg, double local)
    {
        CloseDecoder();
        decoderSegment = seg;
        var (w, h) = sizes[seg];
        if (buffer == null || w != width || h != height)
        {
            width = w;
            height = h;
            buffer = new byte[w * h * 4];
        }
        var m = tl.Segments[seg].Media;
        decoder = new FrameDecoder(m.Path, local, width, height, m.Fps);
    }

    void CloseDecoder()
    {
        var d = decoder;
        decoder = null;
        d?.Dispose();
    }

    void RaiseOnUi(Action a)
    {
        if (ui.CheckAccess()) a();
        else ui.BeginInvoke(a);
    }

    public void Dispose()
    {
        disposed = true;
        decoder?.Dispose();
        signal.Set();
    }
}

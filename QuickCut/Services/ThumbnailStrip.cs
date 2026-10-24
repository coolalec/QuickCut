using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace QuickCut.Services;

/// <summary>
/// The filmstrip thumbnails of one file, made on demand: the timeline says which times it is showing
/// (<see cref="Want"/>) and draws whatever is closest (<see cref="Nearest"/>) while the rest are decoded one by one.
/// The visible tiles are fetched coarse to fine, so a long file shows a rough strip at once and fills in after.
/// </summary>
public sealed class ThumbnailStrip : IDisposable
{
    const int ThumbHeight = 120; // the filmstrip is at most 90 DIP tall, so this stays sharp up to 133% scaling
    const int MaxThumbs = 1000;  // ~100 MB per file at worst; the ones farthest from the view go first
    const double MinSlot = 1;   // keyframes rarely come closer together than this

    // Shared by every file, so loading many files doesn't start a crowd of FFmpeg processes.
    static readonly int Parallel = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
    static readonly SemaphoreSlim Gate = new SemaphoreSlim(Parallel);

    readonly MediaInfo media;
    readonly CancellationTokenSource cts = new CancellationTokenSource();
    readonly object sync = new object();
    readonly SortedList<long, BitmapSource> done = new SortedList<long, BitmapSource>(); // keyed by time in ms
    readonly HashSet<long> attempted = new HashSet<long>(); // in flight or failed, so they aren't asked for again
    readonly Queue<long> queue = new Queue<long>();
    long[] lastWanted = new long[0];
    double viewFrom, viewTo;
    int workers;

    public ThumbnailStrip(MediaInfo media) => this.media = media;

    /// <summary>Raised on a worker thread each time a thumbnail is ready.</summary>
    public event Action Changed;

    /// <summary>The thumbnail closest to <paramref name="t"/> (seconds into the file), or null if there is none yet.</summary>
    public BitmapSource Nearest(double t)
    {
        lock (sync)
        {
            int i = NearestIndex(Ms(t));
            return i < 0 ? null : done.Values[i];
        }
    }

    /// <summary>
    /// Sets what is on screen now: tile centres <paramref name="times"/> (seconds into the file, in order), each tile
    /// spanning <paramref name="tileSeconds"/>. Replaces whatever was queued before, so scrolled-away tiles are dropped.
    /// </summary>
    public void Want(IReadOnlyList<double> times, double tileSeconds)
    {
        double slot = MinSlot;
        while (slot * 2 <= tileSeconds) slot *= 2;
        double last = Math.Max(0, media.Duration - 0.05);

        var keys = new List<long>(times.Count);
        foreach (var t in times)
        {
            long k = Ms(Math.Min(last, (Math.Floor(t / slot) + 0.5) * slot));
            if (keys.Count == 0 || keys[keys.Count - 1] != k) keys.Add(k);
        }

        lock (sync)
        {
            if (cts.IsCancellationRequested || SameAs(keys)) return;
            lastWanted = keys.ToArray();
            viewFrom = times.Count > 0 ? times[0] : 0;
            viewTo = times.Count > 0 ? times[times.Count - 1] : 0;

            queue.Clear();
            long half = Ms(slot / 2);
            foreach (int i in CoarseToFine(keys.Count))
            {
                long k = keys[i];
                int near = NearestIndex(k);
                if (attempted.Contains(k) || (near >= 0 && Math.Abs(done.Keys[near] - k) < half)) continue;
                queue.Enqueue(k);
            }
            // Off the UI thread: this runs while the timeline renders, and starting a process takes a while.
            while (workers < queue.Count && workers < Parallel)
            {
                workers++;
                Task.Run(WorkAsync);
            }
        }
    }

    async Task WorkAsync()
    {
        var ct = cts.Token;
        try
        {
            while (true)
            {
                await Gate.WaitAsync(ct).ConfigureAwait(false);
                long k;
                BitmapSource bmp = null;
                try
                {
                    // Take the job only once a slot is free, so it is the most wanted one at that moment.
                    lock (sync)
                    {
                        if (queue.Count == 0) { workers--; return; }
                        k = queue.Dequeue();
                        attempted.Add(k);
                    }
                    try { bmp = await MediaAnalyzer.ThumbnailAtAsync(media, k / 1000.0, ThumbHeight, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { }
                }
                finally { Gate.Release(); }

                if (bmp == null) continue; // stays in attempted: don't retry a time that can't be decoded
                lock (sync)
                {
                    attempted.Remove(k);
                    done[k] = bmp;
                    Trim();
                }
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync) workers--;
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        lock (sync) queue.Clear();
    }

    void Trim()
    {
        while (done.Count > MaxThumbs)
        {
            int worst = 0;
            double worstDist = -1;
            for (int i = 0; i < done.Count; i++)
            {
                double t = done.Keys[i] / 1000.0;
                double d = t < viewFrom ? viewFrom - t : t > viewTo ? t - viewTo : 0;
                if (d > worstDist) { worstDist = d; worst = i; }
            }
            done.RemoveAt(worst);
        }
    }

    bool SameAs(List<long> keys)
    {
        if (keys.Count != lastWanted.Length) return false;
        for (int i = 0; i < keys.Count; i++)
            if (keys[i] != lastWanted[i]) return false;
        return true;
    }

    int NearestIndex(long k)
    {
        var keys = done.Keys;
        if (keys.Count == 0) return -1;
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] < k) lo = mid + 1; else hi = mid;
        }
        return lo > 0 && k - keys[lo - 1] < keys[lo] - k ? lo - 1 : lo;
    }

    /// <summary>0..n-1 ordered middle first, then the middles of each half, and so on.</summary>
    static IEnumerable<int> CoarseToFine(int n)
    {
        var ranges = new Queue<(int Lo, int Hi)>();
        ranges.Enqueue((0, n));
        while (ranges.Count > 0)
        {
            var (lo, hi) = ranges.Dequeue();
            if (lo >= hi) continue;
            int mid = (lo + hi) / 2;
            yield return mid;
            ranges.Enqueue((lo, mid));
            ranges.Enqueue((mid + 1, hi));
        }
    }

    static long Ms(double seconds) => (long)Math.Round(seconds * 1000);
}

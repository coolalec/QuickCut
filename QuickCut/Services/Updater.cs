using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace QuickCut.Services;

/// <summary>
/// Downloads a release zip and swaps it in next to the running exe. Windows lets a running exe and its loaded
/// DLLs be renamed, so each file being replaced is renamed to *.old first and the new one copied in its place;
/// the *.old files are deleted on the next start (<see cref="CleanUp"/>).
/// </summary>
public static class Updater
{
    const string OldSuffix = ".old";
    static readonly string[] Keep = { "QuickCut.settings.xml", "QuickCut.crash.log" };

    static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    static string TempDir => Path.Combine(Path.GetTempPath(), "QuickCut-update");

    /// <summary>Only the portable layout from the release zip (ffmpeg\bin next to the exe) is updated in place.</summary>
    public static bool CanSelfUpdate => Directory.Exists(Path.Combine(AppDir, "ffmpeg", "bin"));

    /// <summary>
    /// Downloads <see cref="UpdateResult.AssetUrl"/>, checks its SHA256 and unpacks it.
    /// Returns the unpacked QuickCut folder. <paramref name="progress"/> gets 0–1.
    /// </summary>
    public static Task<string> DownloadAsync(UpdateResult release, IProgress<double> progress) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(release?.AssetUrl)) throw new InvalidOperationException("這個版本沒有可下載的檔案");
        if (string.IsNullOrEmpty(release.AssetSha256)) throw new InvalidOperationException("這個版本沒有提供 SHA256，無法確認下載的檔案");

        DeleteDirectory(TempDir);
        Directory.CreateDirectory(TempDir);
        var zip = Path.Combine(TempDir, Path.GetFileName(new Uri(release.AssetUrl).LocalPath));

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var req = (HttpWebRequest)WebRequest.Create(release.AssetUrl);
        req.UserAgent = "QuickCut/" + UpdateChecker.DisplayVersion;
        req.Timeout = req.ReadWriteTimeout = 30_000;

        string hash;
        long total;
        using (var resp = req.GetResponse())
        using (var src = resp.GetResponseStream())
        using (var dst = File.Create(zip))
        using (var sha = SHA256.Create())
        {
            long size = resp.ContentLength > 0 ? resp.ContentLength : release.AssetSize;
            var buf = new byte[81920];
            int n, lastPercent = -1;
            total = 0;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                sha.TransformBlock(buf, 0, n, null, 0);
                total += n;
                if (size <= 0) continue;
                int percent = (int)(total * 100 / size);
                if (percent != lastPercent) progress?.Report(Math.Min(1, (double)total / size));
                lastPercent = percent;
            }
            sha.TransformFinalBlock(buf, 0, 0);
            hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
        if (release.AssetSize > 0 && total != release.AssetSize)
            throw new InvalidDataException($"下載不完整（{total:N0} / {release.AssetSize:N0} 位元組）");
        if (!string.Equals(hash, release.AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下載的檔案 SHA256 不符，已停止更新");

        var unpacked = Path.Combine(TempDir, "package");
        ZipFile.ExtractToDirectory(zip, unpacked);
        var newDir = Path.Combine(unpacked, "QuickCut");
        if (!File.Exists(Path.Combine(newDir, "QuickCut.exe"))) throw new InvalidDataException("下載的檔案裡沒有 QuickCut.exe");
        return newDir;
    });

    /// <summary>
    /// Replaces every file of the new package plus everything under the old ffmpeg folder; the settings file,
    /// crash log and anything else in <paramref name="appDir"/> stay. On failure everything is put back and the error rethrown.
    /// </summary>
    public static void Apply(string newDir, string appDir)
    {
        var incoming = Directory.EnumerateFiles(newDir, "*", SearchOption.AllDirectories)
            .Select(f => f.Substring(newDir.Length).TrimStart('\\'))
            .Where(rel => !Keep.Contains(rel, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var oldFfmpeg = Path.Combine(appDir, "ffmpeg");
        var replaced = (Directory.Exists(oldFfmpeg)
                ? Directory.EnumerateFiles(oldFfmpeg, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Substring(appDir.Length).TrimStart('\\'))
                : Enumerable.Empty<string>())
            .Concat(incoming.Where(rel => File.Exists(Path.Combine(appDir, rel))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var renamed = new List<string>();
        var copied = new List<string>();
        var createdDirs = new List<string>();
        try
        {
            foreach (var rel in replaced)
            {
                var path = Path.Combine(appDir, rel);
                var old = path + OldSuffix;
                if (File.Exists(old)) DeleteFile(old); // left over from an update whose clean-up couldn't finish
                File.Move(path, old);
                renamed.Add(path);
            }
            foreach (var rel in incoming)
            {
                var path = Path.Combine(appDir, rel);
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    createdDirs.Add(dir);
                }
                File.Copy(Path.Combine(newDir, rel), path);
                copied.Add(path);
            }
        }
        catch
        {
            foreach (var path in copied) try { File.Delete(path); } catch { }
            foreach (var dir in Enumerable.Reverse(createdDirs)) try { Directory.Delete(dir); } catch { }
            foreach (var path in renamed) try { File.Move(path + OldSuffix, path); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Deletes what an update left behind: the download folder and *.old files (only those next to a file of the
    /// same name, and any under ffmpeg). Files still in use are skipped; returns false if any remain.
    /// </summary>
    public static bool CleanUp()
    {
        bool clean = DeleteDirectory(TempDir);
        try
        {
            var appDir = AppDir;
            var ffmpeg = Path.Combine(appDir, "ffmpeg");
            var olds = Directory.EnumerateFiles(appDir, "*" + OldSuffix)
                .Where(f => File.Exists(f.Substring(0, f.Length - OldSuffix.Length)));
            if (Directory.Exists(ffmpeg))
                olds = olds.Concat(Directory.EnumerateFiles(ffmpeg, "*" + OldSuffix, SearchOption.AllDirectories));
            // "*.old" also matches e.g. "x.older" through 8.3 short names
            foreach (var f in olds.Where(f => f.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase)).ToList())
                try { DeleteFile(f); } catch { clean = false; }
        }
        catch { clean = false; }
        return clean;
    }

    static void DeleteFile(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    static bool DeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            return true;
        }
        catch { return false; }
    }
}

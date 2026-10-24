using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QuickCut.Services;

public enum UpdateState { UpToDate, Available, Unknown }

public sealed class UpdateResult
{
    public UpdateState State;
    public string LatestTag;   // e.g. "v1.2.0"
    public string ReleaseUrl;  // release page on GitHub
    public string AssetUrl;    // QuickCut-<ver>-win64.zip, null if the release has none
    public long AssetSize;
    public string AssetSha256; // lowercase hex, null if GitHub and the release notes give none
    public string Error;
}

/// <summary>Asks GitHub for the latest release and compares it with this build. Never throws.</summary>
public static class UpdateChecker
{
    public const string RepoUrl = "https://github.com/coolalec/QuickCut";
    const string LatestApi = "https://api.github.com/repos/coolalec/QuickCut/releases/latest";

    /// <summary>The version shown to people: InformationalVersion (e.g. "1.2.0" or "1.2.0.17"), else AssemblyVersion.</summary>
    public static string DisplayVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrEmpty(info) ? Current.ToString(3) : info;
        }
    }

    static Version Current => Assembly.GetExecutingAssembly().GetName().Version;

    public static Task<UpdateResult> CheckAsync() => Task.Run(() =>
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(LatestApi);
            req.UserAgent = "QuickCut/" + Current.ToString(3);
            req.Accept = "application/vnd.github+json";
            req.Timeout = req.ReadWriteTimeout = 10_000;

            string json;
            using (var resp = req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
                json = reader.ReadToEnd();

            var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var tag = obj.TryGetValue("tag_name", out var t) ? t as string : null;
            var url = obj.TryGetValue("html_url", out var u) ? u as string : null;
            var result = Compare(tag, url, Current);
            ReadAsset(obj, result);
            return result;
        }
        catch (Exception ex)
        {
            return new UpdateResult { State = UpdateState.Unknown, Error = ex.Message };
        }
    });

    /// <summary>Picks the QuickCut-*-win64.zip asset; its SHA256 comes from GitHub's digest, else from the release notes.</summary>
    static void ReadAsset(Dictionary<string, object> release, UpdateResult result)
    {
        if (!release.TryGetValue("assets", out var a) || !(a is IEnumerable assets)) return;
        foreach (var item in assets)
        {
            if (!(item is Dictionary<string, object> asset)) continue;
            var name = asset.TryGetValue("name", out var n) ? n as string : null;
            if (name == null || !Regex.IsMatch(name, @"^QuickCut-.+-win64\.zip$", RegexOptions.IgnoreCase)) continue;
            result.AssetUrl = asset.TryGetValue("browser_download_url", out var d) ? d as string : null;
            result.AssetSize = asset.TryGetValue("size", out var s) && s != null ? Convert.ToInt64(s) : 0;
            var digest = asset.TryGetValue("digest", out var g) ? g as string : null; // "sha256:5caa…"
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                result.AssetSha256 = digest.Substring(7);
            break;
        }
        if (result.AssetUrl != null && result.AssetSha256 == null && release.TryGetValue("body", out var b) && b is string body)
        {
            // release-notes.ps1 writes "- SHA256：`5caa…`"
            var m = Regex.Match(body, @"SHA256\s*[:：]\s*`?([0-9a-fA-F]{64})");
            if (m.Success) result.AssetSha256 = m.Groups[1].Value;
        }
        if (result.AssetSha256 != null && !Regex.IsMatch(result.AssetSha256, "^[0-9a-fA-F]{64}$")) result.AssetSha256 = null;
        result.AssetSha256 = result.AssetSha256?.ToLowerInvariant();
    }

    /// <summary>Compares a release tag ("v1.2.0") with the first three parts of <paramref name="current"/>.</summary>
    public static UpdateResult Compare(string tag, string url, Version current)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text, out var latest))
            return new UpdateResult { State = UpdateState.Unknown, Error = "無法解讀版本標籤: " + tag };
        var mine = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        var theirs = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        return new UpdateResult
        {
            State = theirs > mine ? UpdateState.Available : UpdateState.UpToDate,
            LatestTag = "v" + theirs.ToString(3),
            ReleaseUrl = string.IsNullOrEmpty(url) ? RepoUrl + "/releases/latest" : url,
        };
    }
}

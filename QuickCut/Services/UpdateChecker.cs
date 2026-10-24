using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QuickCut.Services;

public enum UpdateState { UpToDate, Available, Unknown }

public sealed class UpdateResult
{
    public UpdateState State;
    public string LatestTag;   // e.g. "v1.2.0"
    public string ReleaseUrl;  // release page on GitHub
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
            return Compare(tag, url, Current);
        }
        catch (Exception ex)
        {
            return new UpdateResult { State = UpdateState.Unknown, Error = ex.Message };
        }
    });

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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace QuickCut.Services;

/// <summary>
/// Orders files for the timeline: by the recording time stored in the video when every file has one,
/// otherwise by file name in natural order (clip_2 before clip_10), the way File Explorer sorts.
/// </summary>
public static class FileOrder
{
    public static bool ByCreationTime(IReadOnlyCollection<MediaInfo> medias) =>
        medias.Count > 0 && medias.All(m => m.CreationTime.HasValue);

    public static List<MediaInfo> Sort(IEnumerable<MediaInfo> medias)
    {
        var list = medias.ToList();
        var byName = Comparer<MediaInfo>.Create((a, b) => CompareNatural(Path.GetFileName(a.Path), Path.GetFileName(b.Path)));
        if (ByCreationTime(list))
            return list.OrderBy(m => m.CreationTime.Value).ThenBy(m => m, byName).ToList();
        return list.OrderBy(m => m, byName).ToList();
    }

    public static int CompareNatural(string a, string b) => StrCmpLogicalW(a ?? "", b ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);
}

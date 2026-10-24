using System;
using System.IO;
using System.Xml.Serialization;

namespace QuickCut.Services;

public enum ThemeMode { System, Light, Dark }

/// <summary>User preferences, stored next to the executable as QuickCut.settings.xml.</summary>
public sealed class Settings
{
    public double LimitMB = 30;
    public bool RemoveAudio;
    public int TargetHeight = 1080;
    public bool Lossless;
    public string OutputDir = "";
    public string EncoderId = "";
    public double InspectorWidth = 310;
    public double LowerHeight = 250;
    public bool PreviewMuted;
    public ThemeMode Theme = ThemeMode.System;
    public bool RailVisible = true;

    static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuickCut.settings.xml");

    /// <summary>Set when the last save failed, e.g. the program sits in a read-only folder.</summary>
    [XmlIgnore] public string SaveError;

    public static Settings Load()
    {
        Settings s = null;
        try
        {
            if (File.Exists(FilePath))
                using (var fs = File.OpenRead(FilePath))
                    s = (Settings)new XmlSerializer(typeof(Settings)).Deserialize(fs);
        }
        catch { }
        return s ?? new Settings();
    }

    public bool Save()
    {
        try
        {
            using (var fs = File.Create(FilePath))
                new XmlSerializer(typeof(Settings)).Serialize(fs, this);
            SaveError = null;
            return true;
        }
        catch (Exception ex)
        {
            SaveError = ex.Message;
            return false;
        }
    }
}

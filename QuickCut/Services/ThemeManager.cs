using System;
using System.Windows;
using Microsoft.Win32;

namespace QuickCut.Services;

/// <summary>
/// Swaps the palette dictionary (index 0 of the application's merged dictionaries) between
/// Themes/Dark.xaml and Themes/Light.xaml, and follows Windows' app theme in System mode.
/// </summary>
public static class ThemeManager
{
    static ThemeMode mode = ThemeMode.Dark;
    static bool? applied;

    static ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (mode == ThemeMode.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(new Action(Update));
        };
    }

    /// <summary>Raised after the palette changes, for controls that draw colours in code.</summary>
    public static event Action Changed;

    public static bool IsLight => applied == true;

    public static void Apply(ThemeMode value)
    {
        mode = value;
        Update();
    }

    static void Update()
    {
        bool light = mode == ThemeMode.Light || (mode == ThemeMode.System && SystemUsesLightTheme());
        if (applied == light) return;
        applied = light;
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/QuickCut;component/Themes/{(light ? "Light" : "Dark")}.xaml"),
        };
        Changed?.Invoke();
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }
}

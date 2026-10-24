#if DEBUG
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickCut;

/// <summary>
/// Debug builds only: when DC_SNAPSHOT names a folder, renders the window to a PNG every
/// two seconds (shot_N.png) so the UI can be checked without screen capture.
/// </summary>
static class DebugSnapshots
{
    public static void Attach(Window w)
    {
        // DC_THEME_TEST=Light|Dark: switch theme 4 s after start, the way the theme menu does.
        var themeTest = Environment.GetEnvironmentVariable("DC_THEME_TEST");
        if (!string.IsNullOrEmpty(themeTest))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Trace("theme test: " + themeTest);
                ((MainWindow)w).SetThemeForTest(themeTest);
                Trace("theme test: done");
            };
            t.Start();
        }

        var dir = Environment.GetEnvironmentVariable("DC_SNAPSHOT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        int n = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (!(w.Content is FrameworkElement root) || root.ActualWidth < 1) return;
            Trace("snapshot " + n);
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, $"shot_{n++ % 50}.png"));
            enc.Save(fs);
        };
        timer.Start();
    }

    public static void Trace(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuickCut.debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); }
        catch { }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using QuickCut.Services;
using Microsoft.Win32;
using Timeline = QuickCut.Services.Timeline;

namespace QuickCut;

public partial class MainWindow : Window
{
    static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".ts", ".m4v", ".3gp", ".mts" };

    readonly Settings settings = Settings.Load();
    readonly PreviewPlayer player;
    readonly ObservableCollection<ChipItem> chips = new ObservableCollection<ChipItem>();
    readonly StringBuilder log = new StringBuilder();
    readonly ObservableCollection<MessageItem> messages = new ObservableCollection<MessageItem>();
    readonly List<TimeRegion> regions = new List<TimeRegion>(); // shared with TimelineView, kept in timeline order
    readonly ObservableCollection<RegionItem> regionItems = new ObservableCollection<RegionItem>();

    Timeline timeline;
    double inPoint, outPoint;
    List<EncoderChoice> encoders = new List<EncoderChoice>();
    bool gpuReady;
    bool loadingUi = true; // controls raise change events while InitializeComponent builds them
    int currentSegment = -1;
    int sourceIndex;      // file whose details the inspector shows
    string sourceOrder;   // how the files were put in order, for the inspector
    CancellationTokenSource exportCts;
    CancellationTokenSource analysisCts;
    string lastOutput;
    bool previewFailureShown;

    public MainWindow()
    {
        ThemeManager.Apply(settings.Theme);
        InitializeComponent();
        ThemeManager.Changed += OnThemeChanged;

        player = new PreviewPlayer(Dispatcher);
        player.PositionChanged += OnPositionChanged;
        player.PlayingChanged += playing => PlayGlyph.Text = playing ? "" : "";
        player.Log += AppendLog;
        player.Failed += message =>
        {
            AppendLog(message);
            if (previewFailureShown) return; // the preview retries on every seek; once is enough
            previewFailureShown = true;
            ShowResult(false, "預覽無法播放", message);
        };
        player.BitmapChanged += () => PreviewImage.Source = player.Bitmap;
        player.SetMuted(settings.PreviewMuted);

        TimelineView.SeekRequested += t => player.Seek(t);
        TimelineView.ViewChanged += _ => UpdateTimelineNav();
        TimelineView.RangeChanged += (a, b) =>
        {
            inPoint = a;
            outPoint = b;
            UpdateRangeUi();
        };
        TimelineView.Regions = regions;
        TimelineView.RegionsChanged += () => OnRegionsChanged(false);
        TimelineView.NewRegionRequested += OnNewRegionRequested;

        FileChips.ItemsSource = chips;
        MessageList.ItemsSource = messages;
        RegionList.ItemsSource = regionItems;
        ApplySettingsToUi();
        UpdateEstimate();

        SourceInitialized += (_, _) =>
        {
            WindowEffects.FixHitTestOverflow(this);
            WindowEffects.Apply(this);
            OnStateChanged(); // starts maximized, which doesn't raise StateChanged
        };
#if DEBUG
        DebugSnapshots.Attach(this);
#endif
        StateChanged += (_, _) => OnStateChanged();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        // A lone Alt (held for Alt+drag on the timeline) would otherwise put the window in menu mode and eat the next key.
        PreviewKeyUp += (_, e) => { if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) e.Handled = true; };
        PreviewMouseDown += OnPreviewMouseDown;
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    // ───────────────────────── Startup / shutdown ─────────────────────────

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AboutVersion.Text = "v" + UpdateChecker.DisplayVersion;
        CheckForUpdates(); // runs on the thread pool; GPU detection below doesn't wait for it

        if (!FfmpegLocator.Locate())
        {
            EmptyTitle.Text = "找不到 FFmpeg";
            EmptyBody.Text = "請把 ffmpeg.exe 與 ffprobe.exe 放在程式旁的 ffmpeg\\bin 資料夾，然後重新啟動。";
            EmptyOpenButton.Visibility = Visibility.Collapsed;
            GpuText.Text = "FFmpeg 未就緒";
            GpuDot.Fill = (Brush)FindResource("OutBrush");
            return;
        }
        AppendLog("FFmpeg: " + FfmpegLocator.FfmpegPath);

        var startupFiles = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
        if (startupFiles.Length > 0) _ = OpenFilesAsync(startupFiles);

        try
        {
            var (adapters, found) = await GpuDetector.DetectAsync();
            encoders = found;
            foreach (var a in adapters) AppendLog("顯示卡: " + a);
            AppendLog("可用編碼器: " + string.Join(", ", encoders.Select(x => $"{x.DisplayName} (H.264 {(x.H264Ok ? "✓" : "✗")}, H.265 {(x.HevcOk ? "✓" : "✗")})")));
        }
        catch (Exception ex)
        {
            AppendLog("GPU 偵測失敗: " + ex.Message);
            encoders = new List<EncoderChoice> { new EncoderChoice { Id = "cpu", DisplayName = "CPU 軟體編碼", H264 = "libx264", Hevc = "libx265", H264Ok = true, HevcOk = true } };
        }
        gpuReady = true;

        UpdateEncoderBadge();
        UpdateExportEnabled();
    }

    void OnClosing(object sender, CancelEventArgs e)
    {
        if (exportCts != null)
        {
            if (MessageBox.Show(this, "正在輸出影片，確定要中止並關閉嗎？", "QuickCut", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            exportCts.Cancel();
        }
        analysisCts?.Cancel();
        DisposeThumbnails();
        player.Dispose();
        settings.InspectorWidth = InspectorCol.ActualWidth;
        settings.LowerHeight = LowerRow.ActualHeight;
        ThemeManager.Changed -= OnThemeChanged;
        SaveSettings();
    }

    void SaveSettings()
    {
        settings.Save();
        SaveWarning.Visibility = settings.SaveError == null ? Visibility.Collapsed : Visibility.Visible;
        if (settings.SaveError != null) AppendLog("設定無法儲存: " + settings.SaveError);
    }

    void ApplySettingsToUi()
    {
        loadingUi = true;
        if (settings.LimitMB < 1) settings.LimitMB = 100;
        CodecH264.IsChecked = true;
        (settings.TargetHeight == 720 ? Res720 : settings.TargetHeight == 1080 ? Res1080 : Res0).IsChecked = true;
        (settings.RemoveAudio ? AudioRemove : AudioKeep).IsChecked = true;
        (settings.TimelapseAudio == TimelapseAudio.Fast ? TlAudioFast : TlAudioMute).IsChecked = true;
        LimitSlider.Value = Clamp(settings.LimitMB, LimitSlider.Minimum, LimitSlider.Maximum);
        LimitBox.Text = settings.LimitMB.ToString("0.#", CultureInfo.InvariantCulture);
        LosslessSwitch.IsChecked = settings.Lossless;
        InspectorCol.Width = new GridLength(Clamp(settings.InspectorWidth, InspectorCol.MinWidth, InspectorCol.MaxWidth));
        LowerRow.Height = new GridLength(Math.Min(LowerRow.MaxHeight, Math.Max(LowerRow.MinHeight, settings.LowerHeight)));
        ApplyLosslessUi();
        SourceToggle.IsChecked = !settings.SourceCollapsed;
        OutputToggle.IsChecked = !settings.OutputCollapsed;
        EncodeToggle.IsChecked = !settings.EncodeCollapsed;
        UpdateOutputDirText();
        UpdateMuteUi();
        UpdateThemeUi();
        ApplyRail();
        loadingUi = false;
    }

    void SectionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        settings.SourceCollapsed = SourceToggle.IsChecked != true;
        settings.OutputCollapsed = OutputToggle.IsChecked != true;
        settings.EncodeCollapsed = EncodeToggle.IsChecked != true;
        SaveSettings();
    }

    /// <summary>What a folded section shows next to its title. The output section's is set by <see cref="UpdateEstimate"/>.</summary>
    void UpdateSectionSummaries()
    {
        if (timeline == null) SourceToggle.Tag = "—";
        else
        {
            var seg = timeline.Segments[Math.Min(sourceIndex, timeline.Segments.Count - 1)];
            SourceToggle.Tag = $"{seg.Name} · {TimeFormat.FormatShort(seg.Duration)}";
        }

        string audio = RemoveAudio ? "移除聲音" : "保留聲音";
        if (regions.Any(r => r.Kind == RegionKind.Speed)) audio += settings.TimelapseAudio == TimelapseAudio.Fast ? " · 縮時加速" : " · 縮時靜音";
        EncodeToggle.Tag = Lossless ? $"無損 · {audio}"
            : $"{(SelectedCodec == "hevc" ? "H.265" : "H.264")} · {(SelectedHeight == 0 ? "原始" : SelectedHeight + "p")} · {audio}";
    }

    void RailToggle_Click(object sender, RoutedEventArgs e)
    {
        settings.RailVisible = !settings.RailVisible;
        ApplyRail();
        SaveSettings();
    }

    void ApplyRail()
    {
        RailCol.Width = new GridLength(settings.RailVisible ? 60 : 0);
        Rail.Visibility = settings.RailVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── Theme ─────────────────────────

    void ThemeButton_Click(object sender, RoutedEventArgs e) => ThemePopup.IsOpen = !ThemePopup.IsOpen;

    void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        ThemePopup.IsOpen = false;
        settings.Theme = (ThemeMode)Enum.Parse(typeof(ThemeMode), (string)((FrameworkElement)sender).Tag);
        ThemeManager.Apply(settings.Theme);
        UpdateThemeUi();
        SaveSettings();
    }

#if DEBUG
    internal void SetThemeForTest(string mode)
    {
        settings.Theme = (ThemeMode)Enum.Parse(typeof(ThemeMode), mode);
        DebugSnapshots.Trace("apply");
        ThemeManager.Apply(settings.Theme);
        DebugSnapshots.Trace("ui");
        UpdateThemeUi();
        DebugSnapshots.Trace("save");
        SaveSettings();
    }
#endif

    void UpdateThemeUi()
    {
        ThemeCheckLight.Visibility = settings.Theme == ThemeMode.Light ? Visibility.Visible : Visibility.Hidden;
        ThemeCheckDark.Visibility = settings.Theme == ThemeMode.Dark ? Visibility.Visible : Visibility.Hidden;
        ThemeCheckSystem.Visibility = settings.Theme == ThemeMode.System ? Visibility.Visible : Visibility.Hidden;
        ThemeButton.Content = settings.Theme switch { ThemeMode.Light => "\uE706", ThemeMode.Dark => "\uE708", _ => "\uE770" };
        ThemeButton.ToolTip = "佈景主題：" + settings.Theme switch { ThemeMode.Light => "淺色", ThemeMode.Dark => "深色", _ => "依系統設定" };
    }

    void OnThemeChanged()
    {
        TimelineView.InvalidateVisual();
        WindowEffects.Apply(this);
    }

    // ───────────────────────── Opening files ─────────────────────────

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇影片（可多選）",
            Multiselect = true,
            Filter = "影片檔|*.mp4;*.mov;*.avi;*.mkv;*.ts;*.m4v;*.3gp;*.mts|所有檔案|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames);
    }

    void AddVideos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = timeline == null ? "選擇影片（可多選）" : "選擇要加入時間軸的影片（可多選）",
            Multiselect = true,
            Filter = "影片檔|*.mp4;*.mov;*.avi;*.mkv;*.ts;*.m4v;*.3gp;*.mts|所有檔案|*.*",
        };
        if (dlg.ShowDialog(this) == true) _ = OpenFilesAsync(dlg.FileNames, append: true);
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p) : new[] { p }).ToArray();
            _ = OpenFilesAsync(files);
        }
    }

    /// <param name="append">Add to the current timeline instead of replacing it; the cut range and position stay on the same footage.</param>
    async Task OpenFilesAsync(string[] paths, bool append = false)
    {
        if (!FfmpegLocator.Locate())
        {
            ShowResult(false, "找不到 FFmpeg，無法開啟影片",
                "請把 ffmpeg 資料夾（含 ffmpeg.exe 與 ffprobe.exe）放回程式旁的 ffmpeg\\bin，或重新解壓 QuickCut 的 zip，然後重新啟動。");
            return;
        }
        var old = append && timeline != null ? timeline.Segments.ToList() : new List<Segment>();
        if (old.Count == 0) manualOrder = false;
        var files = paths
            .Where(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Where(p => !old.Any(s => string.Equals(s.Media.Path, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            if (old.Count > 0) ShowResult(false, "沒有新的影片可加入", "選擇的檔案已經在時間軸上，或不是支援的影片格式。", warn: true);
            else ShowResult(false, "沒有可用的影片檔", "支援 MP4、MOV、AVI、MKV、TS 等格式。");
            return;
        }

        BusyText.Text = files.Length > 1 ? $"讀取 {files.Length} 個影片…" : "讀取影片資訊…";
        BusyOverlay.Visibility = Visibility.Visible;
        MediaInfo[] infos;
        try
        {
            infos = await Task.WhenAll(files.Select(f => MediaProbe.ProbeAsync(f)));
        }
        catch (Exception ex)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            ShowResult(false, "無法讀取影片", ex.Message);
            return;
        }
        BusyOverlay.Visibility = Visibility.Collapsed;

        // Remember where the cut and playhead sit as (file, time in file), so adding files keeps them in place.
        var anchors = old.Count > 0 ? new[] { inPoint, outPoint, player.Position }.Select(t => Anchor(timeline, t)).ToArray() : null;

        // After the tabs were reordered by hand that order stays, and new files go at the end.
        var medias = manualOrder
            ? old.Select(s => s.Media).Concat(FileOrder.Sort(infos)).ToList()
            : FileOrder.Sort(old.Select(s => s.Media).Concat(infos));
        ApplyTimeline(medias, old, anchors == null ? null : tl => new[] { Unanchor(tl, anchors[0]), Unanchor(tl, anchors[1]), Unanchor(tl, anchors[2]) });
        int count = timeline.Segments.Count;

        // Default the output codec to what the camera recorded.
        if (old.Count == 0)
        {
            loadingUi = true;
            (timeline.Segments[0].Media.VideoCodec == "hevc" ? CodecHevc : CodecH264).IsChecked = true;
            loadingUi = false;
        }

        AppendLog(old.Count > 0
            ? $"加入 {files.Length} 個檔案，共 {count} 個，總長 {TimeFormat.Format(timeline.Duration)}"
            : $"載入 {files.Length} 個檔案，總長 {TimeFormat.Format(timeline.Duration)}");
        _ = AnalyzeAsync(timeline);
    }

    /// <summary>
    /// Rebuilds the timeline from <paramref name="medias"/> (keeping waveforms and thumbnails from <paramref name="old"/>)
    /// and refreshes everything that shows it. <paramref name="place"/> returns in, out and playhead on the new timeline;
    /// null selects everything from the start.
    /// </summary>
    void ApplyTimeline(List<MediaInfo> medias, List<Segment> old, Func<Timeline, double[]> place)
    {
        player.Pause();
        var previous = timeline;
        timeline = new Timeline(medias);
        foreach (var s in timeline.Segments)
        {
            var prev = old.FirstOrDefault(o => o.Media == s.Media);
            if (prev == null)
            {
                s.Thumbs = new ThumbnailStrip(s.Media);
                s.Thumbs.Changed += OnThumbnailReady;
                continue;
            }
            s.Peaks = prev.Peaks;
            s.PeakInterval = prev.PeakInterval;
            s.Thumbs = prev.Thumbs;
        }
        foreach (var o in old.Where(o => !medias.Contains(o.Media)))
            o.Thumbs?.Dispose();
        var p = place?.Invoke(timeline);
        int lostRegions = 0;
        if (p == null) regions.Clear();
        else lostRegions = RemapRegions(previous, timeline);
        RebuildRegionItems();
        double playhead = 0;
        if (p != null)
        {
            inPoint = Clamp(p[0], 0, timeline.Duration);
            outPoint = Clamp(Math.Max(inPoint + 0.1, p[1]), 0, timeline.Duration);
            playhead = Clamp(p[2], 0, timeline.Duration);
        }
        else
        {
            inPoint = 0;
            outPoint = timeline.Duration;
        }
        // Loaded straight at the playhead: the first frame shown is the right one (the picture switches via BitmapChanged).
        player.Load(timeline, playhead);
        PreviewHost.Cursor = Cursors.Hand;
        TimelineView.Timeline = timeline;
        TimelineView.Position = playhead;
        TimelineView.SetRange(inPoint, outPoint);

        // The tab of the playhead's file is marked at once, not when the first frame arrives.
        currentSegment = timeline.IndexAt(playhead);
        chips.Clear();
        foreach (var s in timeline.Segments)
            chips.Add(new ChipItem { Name = s.Name, Length = TimeFormat.FormatShort(s.Duration), Offset = s.Offset, Media = s.Media,
                IsCurrent = s == timeline.Segments[currentSegment] });

        sourceOrder = manualOrder ? "手動排列" : FileOrder.ByCreationTime(medias) ? "依影片建立時間" : "依檔名";
        ShowSourceInfo(currentSegment);
        FramePill.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        TimeTotal.Text = " / " + TimeFormat.Format(timeline.Duration);

        UpdateTimelineNav();
        UpdateRangeUi();
        UpdateOutputDirText();
        UpdateExportEnabled();
        UpdateRegionPill(playhead);
        if (lostRegions > 0)
            ShowResult(false, $"已移除 {lostRegions} 個縮時或刪除區段", "檔案變動後，這些區段的範圍不再是連續的同一段影片。", warn: true);
    }

    /// <summary>
    /// Moves the regions onto a rebuilt timeline by (file, time in file), the way the cut is kept in place.
    /// Ones that no longer cover one unbroken stretch of the same footage are dropped; returns how many.
    /// </summary>
    int RemapRegions(Timeline from, Timeline to)
    {
        if (from == null || regions.Count == 0) return 0;
        var kept = new List<TimeRegion>();
        foreach (var r in regions)
        {
            // The end is anchored just inside the region, so a region ending on a file boundary stays with its own file.
            var a = Anchor(from, r.Start);
            var b = Anchor(from, r.End - 0.001);
            if (!to.Segments.Any(s => s.Media == a.Media) || !to.Segments.Any(s => s.Media == b.Media)) continue;
            double start = Unanchor(to, a), end = Unanchor(to, b) + 0.001;
            if (Math.Abs(end - start - r.Length) > 0.01) continue;
            r.Start = start;
            r.End = end;
            kept.Add(r);
        }
        int lost = regions.Count - kept.Count;
        regions.Clear();
        regions.AddRange(kept.OrderBy(r => r.Start));
        return lost;
    }

    void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't let the click reach the tab underneath (which would seek)
        if (timeline == null || exportCts != null) return;
        var media = ((ChipItem)((FrameworkElement)sender).Tag).Media;
        var old = timeline.Segments.ToList();
        int removed = old.FindIndex(s => s.Media == media);
        if (removed < 0) return;
        var remaining = old.Where(s => s.Media != media).Select(s => s.Media).ToList();
        var following = old.Skip(removed + 1).Select(s => s.Media).FirstOrDefault();
        AppendLog("移除 " + Path.GetFileName(media.Path));
        if (remaining.Count == 0)
        {
            CloseAll();
            return;
        }

        var anchors = new[] { inPoint, outPoint, player.Position }.Select(t => Anchor(timeline, t)).ToArray();
        ApplyTimeline(remaining, old, tl =>
        {
            // Points inside the removed file move to where it used to be; a range that disappears resets to everything.
            double gap = following != null ? tl.Segments.First(s => s.Media == following).Offset : tl.Duration;
            double Place((MediaInfo Media, double Local) a) => a.Media == media ? gap : Unanchor(tl, a);
            double a0 = Place(anchors[0]), a1 = Place(anchors[1]);
            if (a1 - a0 < 0.1) { a0 = 0; a1 = tl.Duration; }
            return new[] { a0, a1, Place(anchors[2]) };
        });
        _ = AnalyzeAsync(timeline);
    }

    /// <summary>Back to the empty "drop a video" state.</summary>
    void CloseAll()
    {
        analysisCts?.Cancel();
        DisposeThumbnails();
        player.Unload();
        timeline = null;
        manualOrder = false;
        chips.Clear();
        TimelineView.Timeline = null;
        PreviewImage.Source = null;
        PreviewHost.Cursor = null;
        EmptyState.Visibility = Visibility.Visible;
        FramePill.Visibility = Visibility.Collapsed;
        SrcName.Text = SrcVideo.Text = SrcAudio.Text = SrcLength.Text = "—";
        SrcName.ToolTip = null;
        SrcPager.Visibility = Visibility.Collapsed;
        sourceIndex = 0;
        TimeMain.Text = "00:00";
        TimeMs.Text = ".000";
        TimeTotal.Text = " / 00:00";
        inPoint = outPoint = 0;
        currentSegment = -1;
        regions.Clear();
        RebuildRegionItems();
        RegionPill.Visibility = Visibility.Collapsed;
        UpdateTimelineNav();
        UpdateRangeUi();
        UpdateExportEnabled();
    }

    // ── Source details, one file at a time ──

    void SrcPrev_Click(object sender, RoutedEventArgs e) => ShowSourceInfo(sourceIndex - 1);
    void SrcNext_Click(object sender, RoutedEventArgs e) => ShowSourceInfo(sourceIndex + 1);

    void ShowSourceInfo(int index)
    {
        if (timeline == null) return;
        int count = timeline.Segments.Count;
        sourceIndex = Math.Max(0, Math.Min(count - 1, index));
        var seg = timeline.Segments[sourceIndex];
        var m = seg.Media;
        SrcPager.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SrcPagerText.Text = $"{sourceIndex + 1} / {count} · 總長 {TimeFormat.FormatShort(timeline.Duration)} · {sourceOrder}";
        SrcPrev.IsEnabled = sourceIndex > 0;
        SrcNext.IsEnabled = sourceIndex < count - 1;
        SrcName.Text = seg.Name;
        SrcName.ToolTip = m.Path;
        // Thin spaces around the dots keep a 4K portrait clip's details on one line in the default inspector width.
        SrcVideo.Text = string.Join(" · ", m.CodecDisplay, $"{m.Width}×{m.Height}", $"{m.Fps:0.##}fps", $"{m.VideoBitrate / 1e6:0.0}Mbps");
        SrcAudio.Text = m.AudioDisplay;
        SrcLength.Text = TimeFormat.Format(m.Duration);
        UpdateSectionSummaries();
    }

    // ── Tab strip scrolling ──

    void ChipsLeft_Click(object sender, RoutedEventArgs e) => ChipsScroll.ScrollToHorizontalOffset(ChipsScroll.HorizontalOffset - ChipsScroll.ViewportWidth * 0.8);
    void ChipsRight_Click(object sender, RoutedEventArgs e) => ChipsScroll.ScrollToHorizontalOffset(ChipsScroll.HorizontalOffset + ChipsScroll.ViewportWidth * 0.8);

    void ChipsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChipsScroll.ScrollToHorizontalOffset(ChipsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    void RegionScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        RegionScroll.ScrollToHorizontalOffset(RegionScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    void ChipsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool overflow = ChipsScroll.ExtentWidth > ChipsScroll.ViewportWidth + 1;
        ChipsLeft.Visibility = ChipsRight.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        ChipsLeft.IsEnabled = ChipsScroll.HorizontalOffset > 0.5;
        ChipsRight.IsEnabled = ChipsScroll.HorizontalOffset < ChipsScroll.ScrollableWidth - 0.5;
    }

    // ── Dragging tabs to reorder files ──

    ChipItem dragChip;  // tab under the pressed mouse button
    Point dragStart;
    double grabX;       // where in the tab it was grabbed
    bool chipDragging;
    ChipGhost ghost;    // floating copy of the dragged tab
    bool manualOrder;   // files were reordered by hand; adding or removing files keeps that order

    FrameworkElement ChipContainer(ChipItem c) => FileChips.ItemContainerGenerator.ContainerFromItem(c) as FrameworkElement;

    /// <summary>The tab's button; its container also holds the gap after it.</summary>
    static FrameworkElement ChipButtonOf(FrameworkElement container) =>
        container != null && VisualTreeHelper.GetChildrenCount(container) > 0 ? VisualTreeHelper.GetChild(container, 0) as FrameworkElement : null;

    static double SlotX(FrameworkElement el) => LayoutInformation.GetLayoutSlot(el).X;

    /// <summary>The current tab's tint over its normal background (ChipButton's IsCurrent trigger in Theme.xaml).</summary>
    static readonly Brush CurrentTabTint = Frozen(Color.FromArgb(0x1F, 0x5B, 0x8C, 0xFF));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    void FileChips_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragChip = null;
        if (timeline == null || exportCts != null || chips.Count < 2) return;
        var close = (Style)FindResource("ChipCloseButton");
        for (var d = e.OriginalSource as DependencyObject; d != null && d != FileChips; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (!(d is Button b && b.Tag is ChipItem c)) continue;
            if (b.Style == close) return; // the ✕ removes the file
            dragChip = c;
            dragStart = e.GetPosition(FileChips);
            grabX = e.GetPosition(b).X;
            return;
        }
    }

    void FileChips_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (dragChip == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!chipDragging)
        {
            if (Math.Abs(e.GetPosition(FileChips).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            var button = ChipButtonOf(ChipContainer(dragChip));
            if (button == null) return;
            // Everything is set up before capturing: CaptureMouse raises a mouse move of its own, which re-enters here.
            chipDragging = true;
            // Picking a tab up also makes its file the current one, so the copy carries the blue "current" look.
            foreach (var c in chips) c.IsCurrent = c == dragChip;
            SeekTo(dragChip.Offset);
            ghost = ChipGhost.Show(ChipsRow, button, (Brush)FindResource("AccentBrush"),
                (Brush)FindResource("PopupBrush"), (Brush)FindResource("ControlBrush"), CurrentTabTint);
            dragChip.IsDragging = true; // after copying it: its own place turns into an empty gap
            FileChips.Cursor = Cursors.SizeAll;
            if (!FileChips.CaptureMouse()) // the tab's button never sees the release, so it won't seek
            {
                chipDragging = false;
                ghost.Remove();
                ghost = null;
                dragChip.IsDragging = false;
                dragChip = null;
                FileChips.ClearValue(CursorProperty);
                return;
            }
        }

        // The copy follows the mouse; the slot moves past every tab whose middle the copy's middle has passed.
        ghost.X = e.GetPosition(ChipsRow).X - grabX;
        double middle = ghost.X + ghost.TabWidth / 2;
        var panelOrigin = ChipContainer(chips[0]) is FrameworkElement first && VisualTreeHelper.GetParent(first) is Visual panel
            ? panel.TransformToVisual(ChipsRow).Transform(new Point()) : new Point();
        int from = chips.IndexOf(dragChip), to = 0;
        for (int i = 0; i < chips.Count; i++)
        {
            if (i == from || !(ChipContainer(chips[i]) is FrameworkElement other)) continue;
            double width = ChipButtonOf(other)?.ActualWidth ?? other.ActualWidth;
            if (panelOrigin.X + SlotX(other) + width / 2 < middle) to++;
        }
        if (to != from) MoveChip(from, to);

        double x = e.GetPosition(ChipsScroll).X;
        if (x < 24) ChipsScroll.ScrollToHorizontalOffset(ChipsScroll.HorizontalOffset - 12);
        else if (x > ChipsScroll.ActualWidth - 24) ChipsScroll.ScrollToHorizontalOffset(ChipsScroll.HorizontalOffset + 12);
        e.Handled = true;
    }

    /// <summary>Moves a tab in the strip; the tabs that make room slide over instead of jumping.</summary>
    void MoveChip(int from, int to)
    {
        var before = chips.Where(c => c != dragChip).Select(c => (Chip: c, X: ChipContainer(c) is FrameworkElement el ? SlotX(el) : double.NaN)).ToList();
        chips.Move(from, to);
        FileChips.UpdateLayout();
        foreach (var (chip, oldX) in before)
        {
            if (!(ChipContainer(chip) is FrameworkElement el) || double.IsNaN(oldX)) continue;
            var slide = el.RenderTransform as TranslateTransform;
            double from0 = (slide?.X ?? 0) + oldX - SlotX(el);
            if (Math.Abs(from0) < 0.5) continue;
            if (slide == null || slide.IsFrozen) el.RenderTransform = slide = new TranslateTransform();
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from0, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    void FileChips_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!chipDragging)
        {
            dragChip = null;
            return;
        }
        e.Handled = true;
        FileChips.ReleaseMouseCapture(); // finishes in FileChips_LostMouseCapture
    }

    void FileChips_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Also bubbles up when a tab's button loses capture to FileChips; only FileChips' own release ends the drag.
        if (e.OriginalSource != FileChips || !chipDragging) return;
        chipDragging = false;
        FileChips.ClearValue(CursorProperty);
        var chip = dragChip;
        dragChip = null;
        var order = chips.Select(c => c.Media).ToList();
        bool changed = timeline != null && !order.SequenceEqual(timeline.Segments.Select(s => s.Media));

        // The copy settles into the slot, then the tab comes back and the new order is applied (which rebuilds the tabs).
        var g = ghost;
        ghost = null;
        var slot = ChipButtonOf(ChipContainer(chip));
        double target = slot != null ? slot.TransformToVisual(ChipsRow).Transform(new Point()).X : g.X;
        g.SlideTo(target, () =>
        {
            g.Remove();
            chip.IsDragging = false;
            if (changed && timeline != null) ReorderFiles(order, chip.Media);
        });
    }

    /// <summary>
    /// Puts the files in a new order; the cut stays on the same footage and the playhead goes to the start of
    /// <paramref name="focus"/> (the dragged file, already sought to when the drag began but maybe not shown yet).
    /// </summary>
    void ReorderFiles(List<MediaInfo> order, MediaInfo focus)
    {
        manualOrder = true;
        var old = timeline.Segments.ToList();
        bool all = inPoint <= 0.001 && outPoint >= timeline.Duration - 0.001;
        var anchors = new[] { inPoint, outPoint }.Select(t => Anchor(timeline, t)).Append((Media: focus, Local: 0.0)).ToArray();
        ApplyTimeline(order, old, tl =>
        {
            double a0 = Unanchor(tl, anchors[0]), a1 = Unanchor(tl, anchors[1]);
            if (all || a1 - a0 < 0.1) { a0 = 0; a1 = tl.Duration; } // a range that no longer runs forward selects everything
            return new[] { a0, a1, Unanchor(tl, anchors[2]) };
        });
        AppendLog("調整檔案順序：" + string.Join("、", order.Select(m => Path.GetFileName(m.Path))));
    }

    // ── Timeline zoom ──

    bool syncingScroll;

    void ShowAll_Click(object sender, RoutedEventArgs e) => TimelineView.ShowAll();

    void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        // At the limit a zoom would only re-centre on the playhead, which looks like the view jumping.
        if (TimelineView.CanZoomIn) TimelineView.ZoomBy(2, player.Position);
    }

    void ZoomOut_Click(object sender, RoutedEventArgs e) => TimelineView.ZoomBy(0.5, player.Position);

    void TimelineScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingScroll) TimelineView.ScrollTo(e.NewValue);
    }

    void UpdateTimelineNav()
    {
        if (ZoomText == null) return;
        bool zoomed = TimelineView.IsZoomed;
        bool canZoomIn = TimelineView.CanZoomIn;
        ZoomInButton.IsEnabled = canZoomIn;
        ZoomInButton.ToolTip = canZoomIn || timeline == null ? "放大時間軸（+ 鍵 或 Ctrl＋滾輪）" : "已放到最大（畫面顯示 1 秒）";
        ZoomOutButton.IsEnabled = zoomed;
        // Not IsEnabled: a disabled button would dim the "全部" label it carries.
        ShowAllButton.IsHitTestVisible = zoomed;
        double factor = TimelineView.TimelineDuration / TimelineView.ViewLength;
        ZoomText.Text = !zoomed ? "全部" : factor.ToString(factor < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "×";

        TimelineScroll.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        if (zoomed)
        {
            syncingScroll = true;
            TimelineScroll.Minimum = 0;
            TimelineScroll.Maximum = TimelineView.TimelineDuration - TimelineView.ViewLength;
            TimelineScroll.ViewportSize = TimelineView.ViewLength;
            TimelineScroll.LargeChange = TimelineView.ViewLength * 0.9;
            TimelineScroll.SmallChange = TimelineView.ViewLength * 0.1;
            TimelineScroll.Value = TimelineView.ViewStart;
            syncingScroll = false;
        }
    }

    static (MediaInfo Media, double Local) Anchor(Timeline tl, double t)
    {
        tl.Locate(t, out var i, out var local);
        return (tl.Segments[i].Media, local);
    }

    static double Unanchor(Timeline tl, (MediaInfo Media, double Local) a)
    {
        var seg = tl.Segments.First(s => s.Media == a.Media);
        return Math.Min(tl.Duration, seg.Offset + a.Local);
    }

    async Task AnalyzeAsync(Timeline tl)
    {
        analysisCts?.Cancel();
        var cts = analysisCts = new CancellationTokenSource();
        try
        {
            // Thumbnails aren't made here: the timeline asks each segment's ThumbnailStrip for the ones it shows.
            foreach (var s in tl.Segments.Where(x => x.Peaks == null))
            {
                var peaks = await Task.Run(() => MediaAnalyzer.WaveformAsync(s.Media, cts.Token));
                s.PeakInterval = MediaAnalyzer.PeakInterval;
                s.Peaks = peaks;
                TimelineView.InvalidateVisual();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("波形產生失敗: " + ex.Message); }
    }

    int thumbRedrawQueued;

    /// <summary>Redraws the timeline once for however many thumbnails arrived since the last redraw.</summary>
    void OnThumbnailReady()
    {
        if (Interlocked.Exchange(ref thumbRedrawQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            thumbRedrawQueued = 0;
            TimelineView.InvalidateVisual();
        }));
    }

    void DisposeThumbnails()
    {
        if (timeline == null) return;
        foreach (var s in timeline.Segments) s.Thumbs?.Dispose();
    }

    // ───────────────────────── Playback ─────────────────────────

    void OnPositionChanged(double t)
    {
        var text = TimeFormat.Format(t);
        int dot = text.LastIndexOf('.');
        TimeMain.Text = text.Substring(0, dot);
        TimeMs.Text = text.Substring(dot);
        TimelineView.Position = t;
        // Not while dragging on the timeline: moving the view under the mouse would make the dragged handle jump.
        if (timeline != null && TimelineView.IsZoomed && !TimelineView.IsDragging && (t < TimelineView.ViewStart || t > TimelineView.ViewEnd))
            TimelineView.EnsureVisible(t);
        UpdateRegionPill(t);
        if (timeline != null)
        {
            FrameText.Text = "F " + (long)Math.Round(t * timeline.Fps);
            int seg = timeline.IndexAt(t);
            if (seg != currentSegment)
            {
                currentSegment = seg;
                ShowSourceInfo(seg); // the inspector follows the playhead into another file
                var media = timeline.Segments[seg].Media;
                foreach (var c in chips) c.IsCurrent = c.Media == media;
                (FileChips.ItemContainerGenerator.ContainerFromItem(chips.FirstOrDefault(c => c.IsCurrent)) as FrameworkElement)?.BringIntoView();
            }
        }
    }

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void PreviewHost_Click(object sender, MouseButtonEventArgs e)
    {
        if (timeline == null || EmptyState.Visibility == Visibility.Visible || BusyOverlay.Visibility == Visibility.Visible) return;
        TogglePlay();
    }

    async void SaveFrame_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null) return;
        player.Pause();
        timeline.Locate(player.Position, out var seg, out var local);
        var m = timeline.Segments[seg].Media;
        var at = TimeSpan.FromSeconds(local);
        var dir = OutputDir(m.Path);
        var name = $"{Path.GetFileNameWithoutExtension(m.Path)}_{(int)at.TotalMinutes:00}m{at.Seconds:00}.{at.Milliseconds:000}s";
        var path = Path.Combine(dir, name + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}).png");

        SnapshotButton.IsEnabled = false;
        try
        {
            await FrameGrabber.SaveAsync(m, local, path);
            lastOutput = path;
            ShowResult(true, "已儲存圖片", $"{Path.GetFileName(path)}\n{m.Width}×{m.Height} · {TimeFormat.Format(player.Position)}", path: path);
        }
        catch (Exception ex)
        {
            ShowResult(false, "儲存圖片失敗", ex.Message);
        }
        finally
        {
            SnapshotButton.IsEnabled = true;
        }
    }

    void TogglePlay()
    {
        if (timeline == null) return;
        if (!player.IsPlaying && player.Position >= timeline.Duration - 2 * timeline.FrameDuration)
            player.Seek(0);
        player.Toggle();
    }

    void PrevFrame_Click(object sender, RoutedEventArgs e) => player.Step(-1);
    void NextFrame_Click(object sender, RoutedEventArgs e) => player.Step(1);
    void JumpIn_Click(object sender, RoutedEventArgs e) => SeekTo(inPoint);
    void JumpOut_Click(object sender, RoutedEventArgs e) => SeekTo(outPoint);

    void SeekTo(double t)
    {
        if (timeline == null) return;
        player.Seek(Clamp(t, 0, timeline.Duration));
    }

    void SeekBy(double seconds) => SeekTo(player.Position + seconds);

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        settings.PreviewMuted = !settings.PreviewMuted;
        player.SetMuted(settings.PreviewMuted);
        UpdateMuteUi();
    }

    void UpdateMuteUi()
    {
        bool m = settings.PreviewMuted;
        MuteButton.Content = m ? "" : "";
        MuteButton.ToolTip = m ? "預覽聲音：靜音（點擊開啟）" : "預覽聲音：開（點擊靜音）";
        if (m) MuteButton.Foreground = (Brush)FindResource("OutBrush");
        else MuteButton.SetResourceReference(ForegroundProperty, "Text2Brush");
    }

    static readonly double[] Speeds = { 0.25, 0.5, 1, 1.5, 2 };
    double speed = 1;

    void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        // Built on first open; the check mark follows the current speed.
        if (SpeedMenu.Children.Count == 1)
        {
            foreach (var s in Speeds)
            {
                var check = new TextBlock { Text = "", FontSize = 12, Margin = new Thickness(14, 0, 0, 0), Tag = s };
                check.Style = (Style)FindResource("Icon");
                check.Foreground = (Brush)FindResource("AccentBrush");
                DockPanel.SetDock(check, Dock.Right);
                var label = new TextBlock { Text = s.ToString("0.##", CultureInfo.InvariantCulture) + "x", FontFamily = (FontFamily)FindResource("MonoFont") };
                var item = new Button { Style = (Style)FindResource("MenuItemButton"), Tag = s, Content = new DockPanel { Children = { check, label } } };
                System.Windows.Automation.AutomationProperties.SetName(item, label.Text);
                item.Click += SpeedItem_Click;
                SpeedMenu.Children.Add(item);
            }
        }
        foreach (var item in SpeedMenu.Children.OfType<Button>())
            ((DockPanel)item.Content).Children[0].Visibility = (double)item.Tag == speed ? Visibility.Visible : Visibility.Hidden;
        SpeedPopup.IsOpen = !SpeedPopup.IsOpen;
    }

    void SpeedItem_Click(object sender, RoutedEventArgs e)
    {
        SpeedPopup.IsOpen = false;
        speed = (double)((FrameworkElement)sender).Tag;
        SpeedText.Text = speed.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        player.SetSpeed(speed);
    }

    void Chip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChipItem chip) SeekTo(chip.Offset);
    }

    /// <summary>Clicking outside a text box commits it and hands the keyboard back to the shortcuts.</summary>
    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox) return;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBox) return;
        Keyboard.ClearFocus();
        Focus();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // With a Chinese IME (e.g. Bopomofo) switched on, letter and -/= keys arrive as ImeProcessed.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        switch (key)
        {
            case Key.O when ctrl: Open_Click(this, null); break;
            case Key.S when ctrl: if (ExportButton.IsEnabled) Export_Click(this, null); break;
            case Key.S: SaveFrame_Click(this, null); break;
            case Key.Space: TogglePlay(); break;
            case Key.Left when ctrl: SeekBy(-10); break;
            case Key.Right when ctrl: SeekBy(10); break;
            case Key.Left when shift: SeekBy(-1); break;
            case Key.Right when shift: SeekBy(1); break;
            case Key.Left: player.Step(-1); break;
            case Key.Right: player.Step(1); break;
            case Key.I: SetIn_Click(this, null); break;
            case Key.O: SetOut_Click(this, null); break;
            case Key.OemOpenBrackets: SeekTo(inPoint); break;
            case Key.OemCloseBrackets: SeekTo(outPoint); break;
            case Key.M: Mute_Click(this, null); break;
            case Key.T: AddRegionAtPlayhead(RegionKind.Speed); break;
            case Key.D: AddRegionAtPlayhead(RegionKind.Cut); break;
            case Key.OemPlus or Key.Add: ZoomIn_Click(this, null); break;
            case Key.OemMinus or Key.Subtract when timeline != null: ZoomOut_Click(this, null); break;
            case Key.Oem5 or Key.OemBackslash when TimelineView.IsZoomed: ShowAll_Click(this, null); break;
            case Key.Home: SeekTo(0); break;
            case Key.End: SeekTo(timeline?.Duration ?? 0); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────────────────────── In / out range ─────────────────────────

    void SetIn_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null) return;
        SetRange(player.Position, Math.Max(outPoint, player.Position + 0.1));
    }

    void SetOut_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null) return;
        // Include the frame on screen: the cut ends after it, not at its start.
        double end = Math.Min(timeline.Duration, player.Position + timeline.FrameDuration);
        SetRange(Math.Min(inPoint, end - 0.1), end);
    }

    void SetRange(double a, double b)
    {
        if (timeline == null) return;
        inPoint = Clamp(a, 0, timeline.Duration);
        outPoint = Clamp(b, inPoint + 0.1, timeline.Duration);
        TimelineView.SetRange(inPoint, outPoint);
        UpdateRangeUi();
    }

    void UpdateRangeUi()
    {
        if (timeline == null)
        {
            InBox.Text = OutBox.Text = "";
        }
        else
        {
            InBox.Text = TimeFormat.Format(inPoint);
            OutBox.Text = TimeFormat.Format(outPoint);
        }
        foreach (var item in regionItems) item.Refresh(inPoint, outPoint);
        ApplyLosslessUi();
        UpdateEstimate();
    }

    static string LengthString(double seconds) => seconds < 60 ? $"{seconds:0.0} 秒" : TimeFormat.FormatShort(seconds);

    void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            if (e.Key == Key.Escape) UpdateRangeUi(); // also puts the region cards' texts back
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }

    void InBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TimeFormat.TryParse(InBox.Text, out var t)) SetRange(t, Math.Max(outPoint, t + 0.1));
        else UpdateRangeUi();
    }

    void OutBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TimeFormat.TryParse(OutBox.Text, out var t)) SetRange(Math.Min(inPoint, t - 0.1), t);
        else UpdateRangeUi();
    }

    // ───────────────────────── Output options ─────────────────────────

    string SelectedCodec => CodecHevc.IsChecked == true ? "hevc" : "h264";
    int SelectedHeight => Res720.IsChecked == true ? 720 : Res1080.IsChecked == true ? 1080 : 0;
    bool RemoveAudio => AudioRemove.IsChecked == true;
    /// <summary>
    /// Lossless as the user chose it, unless the timeline can't be cut without re-encoding.
    /// Read from the setting: while blocked the switch is shown off without changing the choice.
    /// </summary>
    bool Lossless => settings.Lossless && LosslessBlocker == null;

    /// <summary>Why the current cut needs a re-encode, or null when a stream copy works.</summary>
    string LosslessBlocker
    {
        get
        {
            if (timeline == null) return null;
            if (regions.Any(r => r.Kind == RegionKind.Speed && r.End > inPoint && r.Start < outPoint))
                return "縮時需要重新編碼，無法使用無損快速剪輯。";
            // Stream copy can only join files whose streams match. Any mismatch on the timeline blocks it,
            // so adding a different file later switches lossless off straight away.
            if (timeline.Segments.Count > 1 && !Timeline.SameFormat(timeline.Segments.Select(s => s.Media).ToList()))
                return "時間軸上的檔案格式不同（編碼、解析度、方向或聲音），無法使用無損快速剪輯。";
            return null;
        }
    }

    void Codec_Checked(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        UpdateEstimate();
    }

    void Res_Checked(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        settings.TargetHeight = SelectedHeight;
        UpdateEstimate();
    }

    void Audio_Checked(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        settings.RemoveAudio = RemoveAudio;
        UpdateEstimate();
    }

    void Lossless_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        settings.Lossless = LosslessSwitch.IsChecked == true;
        ApplyLosslessUi();
        UpdateEstimate();
        UpdateExportEnabled();
    }

    void ApplyLosslessUi()
    {
        var blocker = LosslessBlocker;
        LosslessRow.IsEnabled = blocker == null;
        LosslessRow.Opacity = blocker == null ? 1 : 0.4;
        LosslessBlockedNote.Text = blocker;
        LosslessBlockedNote.Visibility = blocker == null ? Visibility.Collapsed : Visibility.Visible;
        bool on = Lossless;
        if (LosslessSwitch.IsChecked != on)
        {
            bool wasLoading = loadingUi;
            loadingUi = true; // showing "off" while blocked isn't the user switching it off
            LosslessSwitch.IsChecked = on;
            loadingUi = wasLoading;
        }
        EncodeOptions.IsEnabled = !on;
        EncodeOptions.Opacity = on ? 0.4 : 1;
        // Lossless has no size limit: the limit and the bar against it go away rather than sit there disabled.
        LimitOptions.Visibility = SizeBarPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LosslessSizeNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        LosslessNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // Show "原始" while lossless, without overwriting the user's saved choice.
        bool prev = loadingUi;
        loadingUi = true;
        (on || settings.TargetHeight == 0 ? Res0 : settings.TargetHeight == 720 ? Res720 : Res1080).IsChecked = true;
        loadingUi = prev;
    }

    // ───────────────────────── Sped-up and cut regions ─────────────────────────

    /// <summary>After any region edit; <paramref name="rebuild"/> when regions were added, removed or changed kind.</summary>
    void OnRegionsChanged(bool rebuild)
    {
        if (rebuild) RebuildRegionItems();
        TimelineView.InvalidateVisual();
        UpdateRangeUi();
        UpdateRegionPill(player.Position);
    }

    void RebuildRegionItems()
    {
        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        regionItems.Clear();
        foreach (var r in regions) regionItems.Add(new RegionItem(r));
        RegionScroll.Visibility = regions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimelapseAudioPanel.Visibility = regions.Any(r => r.Kind == RegionKind.Speed) ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateRegionPill(double t)
    {
        var r = timeline == null ? null : regions.FirstOrDefault(x => x.Contains(t));
        RegionPill.Visibility = r == null ? Visibility.Collapsed : Visibility.Visible;
        if (r == null) return;
        bool cut = r.Kind == RegionKind.Cut;
        RegionPillGlyph.Text = cut ? "\uE8C6" : "\uEB9D";
        RegionPillText.Text = cut ? "刪除區段（不輸出）" : "縮時 ×" + TimeRegion.FormatSpeed(r.Speed);
    }

    /// <summary>About a tenth of the length, but always at least <see cref="TimeRegion.MinSpeed"/>× faster.</summary>
    static double DefaultTarget(double length) =>
        Math.Max(TimeRegion.MinTarget, Math.Min(Math.Round(length / 10), length / TimeRegion.MinSpeed));

    /// <summary>From the playhead to the next region or the out point, at most five minutes.</summary>
    void AddRegionAtPlayhead(RegionKind kind)
    {
        if (timeline == null) return;
        double t = player.Position;
        if (!TimeRegion.Gap(regions, t, timeline.Duration, out _, out var hi))
        {
            ShowResult(false, "這裡已經有區段", "播放頭在現有的區段裡。先移到空白的地方，或直接修改那個區段。", warn: true);
            return;
        }
        double end = Math.Min(hi, t + 300);
        if (outPoint >= t + TimeRegion.MinLength) end = Math.Min(end, outPoint);
        if (end - t < TimeRegion.MinLength)
        {
            ShowResult(false, "這裡放不下區段", "從播放頭到下一個區段或影片結尾不到 2 秒。", warn: true);
            return;
        }
        AddRegion(kind, t, end);
    }

    void AddRegion(RegionKind kind, double start, double end)
    {
        var r = new TimeRegion { Kind = kind, Start = start, End = end };
        if (kind == RegionKind.Speed) r.Target = DefaultTarget(r.Length);
        regions.Add(r);
        OnRegionsChanged(true);
    }

    static TimeRegion RegionOf(object sender) => ((RegionItem)((FrameworkElement)sender).Tag).Region;

    // Typed or "set to playhead" edges stay clear of the neighbours and keep the region long enough for its target.
    void MoveRegionStart(TimeRegion r, double t)
    {
        TimeRegion.Bounds(regions, r, timeline.Duration, out var lo, out _);
        r.Start = Math.Max(lo, Math.Min(t, r.End - r.MinLengthFor));
    }

    void MoveRegionEnd(TimeRegion r, double t)
    {
        TimeRegion.Bounds(regions, r, timeline.Duration, out _, out var hi);
        r.End = Math.Min(hi, Math.Max(t, r.Start + r.MinLengthFor));
    }

    void RegionStart_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        var r = RegionOf(sender);
        if (timeline == null || !regions.Contains(r)) return; // focus also leaves when the card is removed
        if (TimeFormat.TryParse(((TextBox)sender).Text, out var t)) MoveRegionStart(r, t);
        OnRegionsChanged(false);
    }

    void RegionEnd_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        var r = RegionOf(sender);
        if (timeline == null || !regions.Contains(r)) return;
        if (TimeFormat.TryParse(((TextBox)sender).Text, out var t)) MoveRegionEnd(r, t);
        OnRegionsChanged(false);
    }

    void RegionTarget_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        var r = RegionOf(sender);
        if (timeline == null || !regions.Contains(r)) return;
        if (TimeFormat.TryParse(((TextBox)sender).Text, out var v) && Math.Abs(v - r.Target) > 0.0005)
        {
            if (v * TimeRegion.MinSpeed > r.Length)
                ShowResult(false, "縮成長度要比區段短", $"這段長 {TimeFormat.FormatShort(r.Length)}，最多只能縮成 {TimeFormat.FormatShort(r.Length / TimeRegion.MinSpeed)}。", warn: true);
            else
                r.Target = Math.Max(TimeRegion.MinTarget, v);
        }
        OnRegionsChanged(false);
    }

    void RegionSetStart_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null) return;
        MoveRegionStart(RegionOf(sender), player.Position);
        OnRegionsChanged(false);
    }

    void RegionSetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null) return;
        // Like the out point: the region ends after the frame on screen.
        MoveRegionEnd(RegionOf(sender), Math.Min(timeline.Duration, player.Position + timeline.FrameDuration));
        OnRegionsChanged(false);
    }

    void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        regions.Remove(RegionOf(sender));
        OnRegionsChanged(true);
    }

    void TimelapseAudio_Checked(object sender, RoutedEventArgs e)
    {
        if (loadingUi) return;
        settings.TimelapseAudio = TlAudioFast.IsChecked == true ? TimelapseAudio.Fast : TimelapseAudio.Mute;
        UpdateSectionSummaries();
    }

    // ── Range drawn on the timeline ──

    double newRegionStart, newRegionEnd;

    void OnNewRegionRequested(double a, double b)
    {
        newRegionStart = a;
        newRegionEnd = b;
        NewRegionTitle.Text = $"{TimeFormat.FormatShort(a)}–{TimeFormat.FormatShort(b)}（{TimeFormat.FormatShort(b - a)}）";
        NewRegionPopup.IsOpen = true;
    }

    void NewRegionItem_Click(object sender, RoutedEventArgs e)
    {
        NewRegionPopup.IsOpen = false;
        AddRegion((RegionKind)Enum.Parse(typeof(RegionKind), (string)((FrameworkElement)sender).Tag), newRegionStart, newRegionEnd);
    }

    void NewRegionCancel_Click(object sender, RoutedEventArgs e) => NewRegionPopup.IsOpen = false;

    void NewRegionPopup_Closed(object sender, EventArgs e) => TimelineView.ClearPending();

    // ───────────────────────── Encoder (title-bar badge) ─────────────────────────

    /// <summary>The user's pick, else the first working hardware encoder, else CPU.</summary>
    EncoderChoice SelectedEncoder =>
        encoders.FirstOrDefault(x => x.Id == settings.EncoderId)
        ?? encoders.FirstOrDefault(x => x.IsHardware)
        ?? encoders.LastOrDefault();

    void GpuBadge_Click(object sender, RoutedEventArgs e)
    {
        if (gpuReady && encoders.Count > 0) EncoderPopup.IsOpen = !EncoderPopup.IsOpen;
    }

    void EncoderItem_Click(object sender, RoutedEventArgs e)
    {
        EncoderPopup.IsOpen = false;
        settings.EncoderId = ((EncoderMenuItem)((FrameworkElement)sender).Tag).Choice.Id;
        UpdateEncoderBadge();
        SaveSettings();
    }

    void UpdateEncoderBadge()
    {
        var enc = SelectedEncoder;
        if (enc == null) return;
        bool hw = enc.IsHardware;
        GpuText.Text = hw ? $"{enc.DisplayName} 硬體加速" : "CPU 軟體編碼";
        GpuText.SetResourceReference(TextBlock.ForegroundProperty, hw ? "OkTextBrush" : "WarnTextBrush");
        GpuDot.Fill = (Brush)FindResource(hw ? "InBrush" : "WarnBrush");
        GpuBadge.Background = new SolidColorBrush(hw ? Color.FromArgb(0x1F, 0x34, 0xD3, 0x99) : Color.FromArgb(0x1F, 0xFB, 0xBF, 0x24));
        GpuBadge.BorderBrush = new SolidColorBrush(hw ? Color.FromArgb(0x4D, 0x34, 0xD3, 0x99) : Color.FromArgb(0x4D, 0xFB, 0xBF, 0x24));
        EncoderList.ItemsSource = encoders.Select(x => new EncoderMenuItem
        {
            Choice = x,
            Name = x.IsHardware ? $"{x.DisplayName} 硬體加速" : "CPU 軟體編碼",
            Detail = x.IsHardware
                ? "速度快；支援 " + string.Join("、", new[] { x.H264Ok ? "H.264" : null, x.HevcOk ? "H.265" : null }.Where(c => c != null))
                : "不需顯示卡，速度較慢",
            IsHardware = x.IsHardware,
            IsSelected = x == enc,
        }).ToList();
    }

    void LimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loadingUi || LimitBox == null) return;
        settings.LimitMB = Math.Round(LimitSlider.Value);
        LimitBox.Text = settings.LimitMB.ToString("0", CultureInfo.InvariantCulture);
        UpdateEstimate();
    }

    void LimitBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }

    void LimitBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (double.TryParse(LimitBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) && mb >= 1 && mb <= 4000)
        {
            settings.LimitMB = mb;
            loadingUi = true;
            LimitSlider.Value = Clamp(mb, LimitSlider.Minimum, LimitSlider.Maximum);
            loadingUi = false;
        }
        LimitBox.Text = settings.LimitMB.ToString("0.#", CultureInfo.InvariantCulture);
        UpdateEstimate();
    }

    void UpdateEstimate()
    {
        if (SizeBar == null) return;
        UpdateSectionSummaries();
        OutputToggle.Tag = "—";
        OutBreakdown.Visibility = Visibility.Collapsed;
        if (timeline == null || outPoint - inPoint <= 0)
        {
            SetSizeBar(double.NaN);
            OutLengthText.Text = "—";
            EstText.Text = "—";
            RateText.Text = "載入影片後計算";
            AudioNote.Text = "";
            HintBox.Visibility = Visibility.Collapsed;
            return;
        }

        var pieces = timeline.Slice(inPoint, outPoint, regions);
        ShowOutputLength();
        if (pieces.Count == 0)
        {
            SetSizeBar(double.NaN);
            EstText.Text = "—";
            RateText.Text = "剪輯範圍都被刪除了";
            AudioNote.Text = "";
            HintBox.Visibility = Visibility.Collapsed;
            return;
        }
        double dur = pieces.Sum(p => p.OutDuration);
        var first = timeline.Segments[pieces[0].Segment].Media;
        var audio = EncodePlanner.PlanAudio(timeline, pieces, RemoveAudio, Lossless);
        EncodePlanner.OutputSize(first, Lossless ? 0 : SelectedHeight, out int w, out int h);
        double pixelRatio = (double)w * h / Math.Max(1, first.Width * first.Height);
        double srcKbps = EncodePlanner.SourceKbps(timeline, pieces);
        var plan = EncodePlanner.ComputeSize(settings.LimitMB, dur, audio.Kbps, srcKbps, pixelRatio, first.VideoCodec, SelectedCodec, Lossless);

        SetSizeBar(plan.EstimatedMB / settings.LimitMB);
        EstText.Text = plan.EstimatedMB.ToString("0.0");
        OutputToggle.Tag = $"{OutLengthText.Text} · " + (Lossless ? "無損" : $"{plan.EstimatedMB:0.0} MB");
        RateText.Text = $"影片 {h}p · {plan.VideoKbps / 1000:0.0} Mbps"
                        + (audio.Mode == AudioMode.None ? "" : $"\n聲音 {audio.Kbps:0} kbps");
        AudioNote.Text = audio.Describe();

        string hint = null;
        if (!Lossless && plan.LowBitrate && h > 720) hint = "流量偏低，畫面可能模糊。建議降低解析度或縮短剪輯範圍。";
        else if (!Lossless && plan.LowBitrate) hint = "剪輯範圍較長，流量偏低，畫面可能模糊。";
        HintText.Text = hint;
        HintBox.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;

        if (Lossless) _ = UpdateKeyframeNoteAsync(pieces[0], regions.Any(r => r.Kind == RegionKind.Cut && r.End > inPoint && r.Start < outPoint));
    }

    static readonly Brush OverLimitBrush = Frozen(Color.FromRgb(0xFB, 0x71, 0x85));

    /// <summary>Fills the bar to the estimate's share of the limit (red past it); NaN leaves it empty.</summary>
    void SetSizeBar(double fraction)
    {
        double f = double.IsNaN(fraction) ? 0 : Math.Max(0, Math.Min(1, fraction));
        SizeBar.ColumnDefinitions[0].Width = new GridLength(f, GridUnitType.Star);
        SizeBar.ColumnDefinitions[1].Width = new GridLength(1 - f, GridUnitType.Star);
        bool over = fraction > 1;
        if (over) SizeBarFill.Background = OverLimitBrush;
        else SizeBarFill.SetResourceReference(Border.BackgroundProperty, "ProgressGradient");
        SizeBarText.Text = double.IsNaN(fraction) ? $"上限 {settings.LimitMB:0.#} MB"
            : over ? $"超過上限 {settings.LimitMB:0.#} MB" : $"{fraction * 100:0}% · 上限 {settings.LimitMB:0.#} MB";
        if (over) SizeBarText.Foreground = OverLimitBrush;
        else SizeBarText.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
    }

    /// <summary>Output length, plus how the regions in the cut got it there: "原 06:00 · 縮時 −1:40 · 刪除 −0:30".</summary>
    void ShowOutputLength()
    {
        double length = TimeRegion.OutputDuration(inPoint, outPoint, regions);
        OutLengthText.Text = length > 0.001 ? LengthString(length) : "—";
        TimeRegion.Savings(inPoint, outPoint, regions, out var sped, out var cut);
        if (sped < 0.05 && cut < 0.05) return;
        var parts = new List<string> { "原 " + LengthString(outPoint - inPoint) };
        if (sped >= 0.05) parts.Add("縮時 −" + LengthString(sped));
        if (cut >= 0.05) parts.Add("刪除 −" + LengthString(cut));
        OutBreakdown.Text = string.Join(" · ", parts);
        OutBreakdown.Visibility = Visibility.Visible;
    }

    int keyframeQuery;

    /// <summary>Shows where a stream-copy cut will really start, since it can only begin on a keyframe.</summary>
    /// <param name="cuts">Cut regions split the range; every piece after one also starts on a keyframe.</param>
    async Task UpdateKeyframeNoteAsync(Piece first, bool cuts)
    {
        int id = ++keyframeQuery;
        var seg = timeline.Segments[first.Segment];
        LosslessNote.Text = "不重新編碼，幾秒完成。正在確認實際起點…";
        await Task.Delay(250); // let in-point dragging settle
        if (id != keyframeQuery) return;
        double key;
        try { key = await MediaProbe.KeyframeAtOrBeforeAsync(seg.Media.Path, first.Start); }
        catch { return; }
        if (id != keyframeQuery) return;
        double early = first.Start - key;
        LosslessNote.Text = (early < 0.05
            ? "不重新編碼，幾秒完成。起點剛好在關鍵影格上，不會提前。"
            : $"不重新編碼，幾秒完成。起點只能從關鍵影格開始，實際會從 {TimeFormat.Format(seg.Offset + key)} 開始（提早 {early:0.0} 秒）。")
            + (cuts ? "刪除區段之後的每一段也會從前一個關鍵影格開始。" : "");
    }

    void UpdateExportEnabled()
    {
        if (ExportButton == null) return;
        ExportButton.IsEnabled = timeline != null && exportCts == null && (gpuReady || Lossless);
        FileChips.IsEnabled = exportCts == null;
    }

    // ───────────────────────── Export ─────────────────────────

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (timeline == null || exportCts != null) return;
        if (outPoint - inPoint < 0.1) return;
        player.Pause();

        var pieces = timeline.Slice(inPoint, outPoint, regions);
        if (pieces.Count == 0)
        {
            ShowResult(false, "沒有可輸出的內容", "剪輯範圍內的影片都在刪除區段裡。", warn: true);
            return;
        }
        double outLength = pieces.Sum(p => p.OutDuration);
        var req = new ExportRequest
        {
            Timeline = timeline,
            In = inPoint,
            Out = outPoint,
            Regions = regions.Select(r => r.Clone()).ToList(),
            TimelapseAudio = settings.TimelapseAudio,
            Codec = SelectedCodec,
            Encoder = SelectedEncoder,
            TargetHeight = SelectedHeight,
            RemoveAudio = RemoveAudio,
            Lossless = Lossless,
            LimitMB = settings.LimitMB,
            OutputPath = BuildOutputPath(pieces, outLength),
        };
        if (req.Encoder == null && !req.Lossless) return;

        // Some cards can't encode H.265; fall back to the CPU for this export rather than failing.
        string encoderNote = null;
        if (!req.Lossless && !req.Encoder.Supports(req.Codec))
        {
            encoderNote = $"{req.Encoder.DisplayName} 不支援 {(req.Codec == "hevc" ? "H.265" : "H.264")}，這次改用 CPU 軟體編碼。";
            req.Encoder = encoders.First(x => !x.IsHardware);
        }

        SaveSettings();
        exportCts = new CancellationTokenSource();
        UpdateExportEnabled();
        ProgressPanel.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ProgressText.Text = "準備中…";

        var job = new ExportJob();
        job.Progress += (f, speed) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ExportProgress.Value = f;
            ProgressText.Text = $"{(req.Lossless ? "複製中" : "編碼中")} {f * 100:0}%" + (string.IsNullOrEmpty(speed) ? "" : $" · {speed.TrimEnd('x')}× 即時");
        }));
        job.Log += line => Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));

        var sw = Stopwatch.StartNew();
        ExportResult result;
        try
        {
            result = await job.RunAsync(req, exportCts.Token);
        }
        catch (Exception ex)
        {
            result = new ExportResult { Error = ex.Message };
        }
        sw.Stop();

        exportCts.Dispose();
        exportCts = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        UpdateExportEnabled();

        if (result.Canceled)
        {
            ShowResult(false, "已取消輸出", "未完成的檔案已刪除。", warn: true);
        }
        else if (!result.Success)
        {
            ShowResult(false, "輸出失敗", result.Error + "\n可開啟左下角的 FFmpeg 記錄查看詳細資訊。");
        }
        else
        {
            lastOutput = result.OutputPath;
            var mb = result.Bytes / 1048576.0;
            var body = $"{Path.GetFileName(result.OutputPath)}\n{mb:0.0} MB · {LengthString(outLength)} · 花費 {sw.Elapsed.TotalSeconds:0} 秒";
            if (result.Attempts > 1) body += "\n第一次超過上限，已自動降低流量重新編碼。";
            if (encoderNote != null) body += "\n" + encoderNote;
            if (result.OverLimit && !req.Lossless)
                ShowResult(true, $"已輸出，但超過 {settings.LimitMB:0.#} MB 上限", body, warn: true, path: result.OutputPath);
            else
                ShowResult(true, "輸出完成", body, path: result.OutputPath);
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => exportCts?.Cancel();

    /// <param name="length">Length of the output, after speeding up and leaving out regions.</param>
    string BuildOutputPath(List<Piece> pieces, double length)
    {
        var p = pieces[0];
        var seg = timeline.Segments[p.Segment];
        var dir = OutputDir(seg.Media.Path);
        var start = TimeSpan.FromSeconds(Math.Floor(p.Start));
        var name = $"{Path.GetFileNameWithoutExtension(seg.Media.Path)}_{(int)start.TotalMinutes:00}m{start.Seconds:00}s_{length:0}s";
        var path = Path.Combine(dir, name + ".mp4");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}).mp4");
        return path;
    }

    string OutputDir(string sourcePath) =>
        !string.IsNullOrEmpty(settings.OutputDir) && Directory.Exists(settings.OutputDir)
            ? settings.OutputDir
            : Path.GetDirectoryName(sourcePath);

    void UpdateOutputDirText()
    {
        if (OutputDirText == null) return;
        OutputDirText.Text = !string.IsNullOrEmpty(settings.OutputDir) ? settings.OutputDir : "輸出到來源影片所在資料夾";
        OutputDirText.ToolTip = OutputDirText.Text;
        ResetOutputDirButton.Visibility = string.IsNullOrEmpty(settings.OutputDir) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ChangeOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var current = timeline != null ? OutputDir(timeline.Segments[0].Media.Path) : settings.OutputDir;
        var dir = FolderPicker.Pick(this, "選擇輸出資料夾", current);
        if (dir == null) return;
        settings.OutputDir = dir;
        SaveSettings();
        UpdateOutputDirText();
    }

    void ResetOutputDir_Click(object sender, RoutedEventArgs e)
    {
        settings.OutputDir = "";
        SaveSettings();
        UpdateOutputDirText();
    }

    void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (lastOutput != null && File.Exists(lastOutput)) { RevealOutput_Click(sender, e); return; }
        var dir = timeline != null ? OutputDir(timeline.Segments[0].Media.Path) : settings.OutputDir;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Process.Start("explorer.exe", ProcessUtil.Quote(dir));
    }

    static void Reveal(string path)
    {
        if (path != null && File.Exists(path))
            Process.Start("explorer.exe", "/select," + ProcessUtil.Quote(path));
    }

    void RevealOutput_Click(object sender, RoutedEventArgs e) => Reveal(lastOutput);

    // ───────────────────────── Messages ─────────────────────────

    void MessageReveal_Click(object sender, RoutedEventArgs e) => Reveal(((MessageItem)((FrameworkElement)sender).Tag).Path);

    void MessageOpen_Click(object sender, RoutedEventArgs e)
    {
        var path = ((MessageItem)((FrameworkElement)sender).Tag).Path;
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void RemoveMessage_Click(object sender, RoutedEventArgs e)
    {
        messages.Remove((MessageItem)((FrameworkElement)sender).Tag);
        UpdateMessagesUi();
    }

    void ClearMessages_Click(object sender, RoutedEventArgs e)
    {
        messages.Clear();
        UpdateMessagesUi();
    }

    void UpdateMessagesUi()
    {
        var v = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MessagesHeader.Visibility = MessagesScroll.Visibility = v;
        MessagesTitle.Text = $"訊息 ({messages.Count})";
    }

    /// <param name="path">File the message is about; success messages get "open folder" / "open" buttons for it.</param>
    void ShowResult(bool success, string title, string body, bool warn = false, string path = null)
    {
        var color = success && !warn ? Color.FromRgb(0x34, 0xD3, 0x99) : warn ? Color.FromRgb(0xFB, 0xBF, 0x24) : Color.FromRgb(0xFB, 0x71, 0x85);
        messages.Insert(0, new MessageItem
        {
            Title = title,
            Body = body,
            Path = path,
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Glyph = success && !warn ? "\uE73E" : warn ? "\uE7BA" : "\uE783",
            Accent = new SolidColorBrush(color),
            Back = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B)),
            Line = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
            ActionsVisibility = path != null ? Visibility.Visible : Visibility.Collapsed,
        });
        MessagesScroll.ScrollToTop();
        UpdateMessagesUi();
    }


    // ───────────────────────── Log ─────────────────────────

    void AppendLog(string line)
    {
        log.AppendLine(line);
        if (log.Length > 200_000) log.Remove(0, log.Length - 150_000);
        if (LogPanel.Visibility == Visibility.Visible)
        {
            LogBox.Text = log.ToString();
            LogBox.ScrollToEnd();
        }
    }

    void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogButton.SetResourceReference(ForegroundProperty, show ? "TextBrush" : "Text3Brush");
        if (show)
        {
            LogBox.Text = log.ToString();
            LogBox.ScrollToEnd();
        }
    }

    void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        log.Clear();
        LogBox.Text = "";
    }

    // ───────────────────────── About / updates ─────────────────────────

    bool checkingUpdate;
    bool ffmpegVersionRead;
    UpdateResult latestRelease;
    bool updating;
    bool selfUpdateFailed; // after a failed install the button only opens the release page

    void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutPopup.IsOpen = !AboutPopup.IsOpen;
        if (AboutPopup.IsOpen) ReadFfmpegVersion();
    }

    async void ReadFfmpegVersion()
    {
        if (ffmpegVersionRead) return;
        if (FfmpegLocator.FfmpegPath == null)
        {
            AboutFfmpeg.Text = "找不到 FFmpeg";
            return;
        }
        ffmpegVersionRead = true;
        try
        {
            var r = await ProcessUtil.RunAsync(FfmpegLocator.FfmpegPath, "-version");
            // "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024 the FFmpeg developers"
            var first = (r.StdOut ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            var v = first.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase) ? first.Substring(15) : first;
            int cut = v.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) v = v.Substring(0, cut);
            AboutFfmpeg.Text = v.Length > 0 ? v : "未知";
            AboutFfmpeg.ToolTip = first.Length > 0 ? first : null;
        }
        catch (Exception ex)
        {
            AboutFfmpeg.Text = "無法讀取版本";
            AppendLog("FFmpeg -version 失敗: " + ex.Message);
        }
    }

    async void CheckForUpdates()
    {
        if (checkingUpdate || updating) return;
        checkingUpdate = true;
        RecheckButton.IsEnabled = false;
        ShowUpdateState(null);

        var r = await UpdateChecker.CheckAsync();
        checkingUpdate = false;
        RecheckButton.IsEnabled = !updating;
        ShowUpdateState(r);
        if (r.State == UpdateState.Available) AppendLog($"有新版本 {r.LatestTag}（目前 v{UpdateChecker.DisplayVersion}）: {r.ReleaseUrl}");
        else if (r.State == UpdateState.Unknown) AppendLog("無法檢查更新: " + r.Error);
    }

    bool CanInstall(UpdateResult r) =>
        !selfUpdateFailed && Updater.CanSelfUpdate && r?.AssetUrl != null && r.AssetSha256 != null;

    void ShowUpdateState(UpdateResult r)
    {
        latestRelease = r;
        bool available = r?.State == UpdateState.Available;
        UpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SetDownloadButton(CanInstall(r));
        AboutButton.ToolTip = available ? $"關於 QuickCut（有新版本 {r.LatestTag}）" : "關於 QuickCut";
        UpdateText.ToolTip = r?.State == UpdateState.Unknown ? r.Error : null;

        switch (r?.State)
        {
            case null:
                UpdateGlyph.Text = "";
                UpdateText.Text = "正在檢查更新…";
                UpdateGlyph.SetResourceReference(ForegroundProperty, "Text3Brush");
                UpdateText.SetResourceReference(ForegroundProperty, "Text3Brush");
                break;
            case UpdateState.UpToDate:
                UpdateGlyph.Text = "";
                UpdateText.Text = "已是最新版本";
                UpdateGlyph.SetResourceReference(ForegroundProperty, "InBrush");
                UpdateText.SetResourceReference(ForegroundProperty, "Text2Brush");
                break;
            case UpdateState.Available:
                UpdateGlyph.Text = "";
                UpdateText.Text = "有新版本 " + r.LatestTag;
                UpdateGlyph.SetResourceReference(ForegroundProperty, "AccentBrush");
                UpdateText.SetResourceReference(ForegroundProperty, "TextBrush");
                break;
            default:
                UpdateGlyph.Text = "";
                UpdateText.Text = "無法檢查更新";
                UpdateGlyph.SetResourceReference(ForegroundProperty, "Text3Brush");
                UpdateText.SetResourceReference(ForegroundProperty, "Text3Brush");
                break;
        }
    }

    void RecheckUpdate_Click(object sender, RoutedEventArgs e) => CheckForUpdates();

    void AboutGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateChecker.RepoUrl);

    void SetDownloadButton(bool install)
    {
        DownloadUpdateButton.Content = install ? "立即更新" : "前往下載";
        AutomationProperties.SetName(DownloadUpdateButton, install ? "下載並安裝新版本" : "前往下載新版本");
    }

    async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        var release = latestRelease;
        if (!CanInstall(release))
        {
            OpenUrl(release?.ReleaseUrl ?? UpdateChecker.RepoUrl + "/releases/latest");
            return;
        }
        if (exportCts != null)
        {
            ShowResult(false, "正在輸出影片", "請等輸出完成後再更新。", warn: true);
            return;
        }

        updating = true;
        DownloadUpdateButton.IsEnabled = RecheckButton.IsEnabled = false;
        UpdateText.ToolTip = null;
        UpdateText.Text = "正在下載 " + release.LatestTag + "…";
        AppendLog($"下載更新 {release.LatestTag}: {release.AssetUrl}");
        try
        {
            var progress = new Progress<double>(p => UpdateText.Text = $"正在下載 {release.LatestTag}… {p:0%}");
            var newDir = await Updater.DownloadAsync(release, progress);
            UpdateText.Text = "正在安裝…";
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            await Task.Run(() => Updater.Apply(newDir, appDir));
            AppendLog("已安裝 " + release.LatestTag);

            if (exportCts != null) // started while downloading; restarting would cut it off
            {
                UpdateText.Text = "已安裝 " + release.LatestTag + "，重新啟動後生效";
                DownloadUpdateButton.Visibility = Visibility.Collapsed;
                return;
            }
            // Reopen the same videos in the new version once this window has saved its settings and closed.
            var args = string.Join(" ", (timeline?.Segments ?? Enumerable.Empty<Segment>()).Select(s => "\"" + s.Media.Path + "\""));
            var exe = Path.Combine(appDir, "QuickCut.exe");
            Closed += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = appDir }); }
                catch (Exception ex) { MessageBox.Show("已更新到 " + release.LatestTag + "，但無法重新啟動：" + ex.Message, "QuickCut"); }
            };
            Close();
        }
        catch (Exception ex)
        {
            AppendLog("更新失敗: " + ex.Message);
            selfUpdateFailed = true;
            UpdateGlyph.Text = "";
            UpdateText.Text = "更新失敗";
            UpdateText.ToolTip = ex.Message;
            UpdateGlyph.SetResourceReference(ForegroundProperty, "Text3Brush");
            UpdateText.SetResourceReference(ForegroundProperty, "Text3Brush");
            SetDownloadButton(false);
        }
        finally
        {
            updating = false;
            DownloadUpdateButton.IsEnabled = RecheckButton.IsEnabled = true;
        }
    }

    void OpenUrl(string url)
    {
        AboutPopup.IsOpen = false;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog("無法開啟瀏覽器: " + ex.Message); }
    }

    // ───────────────────────── Window chrome ─────────────────────────

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void OnStateChanged()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";
        // A chromeless maximized window overhangs the screen by the resize frame.
        var f = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(f.Left + 4, f.Top + 4, f.Right + 4, f.Bottom + 4) : new Thickness(0);
    }

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PreviewHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 12, 12);

    static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}

public sealed class MessageItem
{
    public string Title { get; set; }
    public string Body { get; set; }
    public string Path { get; set; }
    public string Time { get; set; }
    public string Glyph { get; set; }
    public Brush Accent { get; set; }
    public Brush Back { get; set; }
    public Brush Line { get; set; }
    public Visibility ActionsVisibility { get; set; }
}

/// <summary>One region's card in the inspector; <see cref="Refresh"/> re-reads everything after an edit.</summary>
public sealed class RegionItem : INotifyPropertyChanged
{
    double inPoint, outPoint;

    public RegionItem(TimeRegion region) => Region = region;

    public TimeRegion Region { get; }
    public bool IsCut => Region.Kind == RegionKind.Cut;
    public string Glyph => IsCut ? "\uE8C6" : "\uEB9D";
    public string Title => IsCut ? "刪除 " + TimeFormat.FormatShort(Region.Length) : "縮時 ×" + TimeRegion.FormatSpeed(Region.Speed);
    public string StartText => TimeFormat.Format(Region.Start);
    public string EndText => TimeFormat.Format(Region.End);
    public string TargetText => TimeFormat.Format(Region.Target);
    public Visibility TargetVisibility => IsCut ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoteVisibility => Note == null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>How the in/out range trims this region, if it does.</summary>
    public string Note
    {
        get
        {
            double a = Math.Max(Region.Start, inPoint), b = Math.Min(Region.End, outPoint);
            if (b - a <= 0.001) return "不在剪輯範圍內，不會用到。";
            if (a - Region.Start < 0.001 && Region.End - b < 0.001) return null;
            return IsCut ? "只有一部分在剪輯範圍內。" : $"只有一部分在剪輯範圍內，輸出 {TimeFormat.FormatShort((b - a) / Region.Speed)}。";
        }
    }

    public void Refresh(double inPoint, double outPoint)
    {
        this.inPoint = inPoint;
        this.outPoint = outPoint;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

public sealed class EncoderMenuItem
{
    public EncoderChoice Choice { get; set; }
    public string Name { get; set; }
    public string Detail { get; set; }
    public bool IsHardware { get; set; }
    public bool IsSelected { get; set; }
}

public sealed class ChipItem : INotifyPropertyChanged
{
    bool isCurrent, isDragging;
    public string Name { get; set; }
    public string Length { get; set; }
    public double Offset { get; set; }
    public MediaInfo Media { get; set; }

    public bool IsCurrent
    {
        get => isCurrent;
        set
        {
            if (isCurrent == value) return;
            isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public bool IsDragging
    {
        get => isDragging;
        set
        {
            if (isDragging == value) return;
            isDragging = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragging)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>Win11 rounded corners and border colour for the chromeless window; silently ignored on older Windows.</summary>
static class WindowEffects
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    const int WM_NCHITTEST = 0x84;

    /// <summary>
    /// WindowChrome reads WM_NCHITTEST's lParam with a checked ToInt32, which throws OverflowException when a
    /// 64-bit lParam arrives zero-extended (seen with the mouse on a monitor left of or above the main one).
    /// This hook runs before WindowChrome's (it is added later) and resends such messages with the same
    /// 32-bit value sign-extended, so the coordinates are unchanged and WindowChrome handles them as usual.
    /// </summary>
    public static void FixHitTestOverflow(Window w)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            long v = lParam.ToInt64();
            if (msg != WM_NCHITTEST || (v >= int.MinValue && v <= int.MaxValue)) return IntPtr.Zero;
            handled = true;
            return SendMessage(hwnd, msg, wParam, new IntPtr(unchecked((int)v)));
        });
    }

    /// <summary>Rounded corners plus a border colour and dark/light frame matching the current theme.</summary>
    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var c = w.TryFindResource("WindowBorderColor") is Color col ? col : Color.FromRgb(0x23, 0x28, 0x38);
        int round = 2;                               // DWMWCP_ROUND
        int border = c.R | (c.G << 8) | (c.B << 16); // COLORREF 0x00BBGGRR
        int dark = ThemeManager.IsLight ? 0 : 1;
        try
        {
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }
}

/// <summary>
/// The floating copy of a file tab that follows the mouse while tabs are reordered. It lives in the window's
/// adorner layer, above the tab strip's scroll viewer, so nothing clips it, and only moves sideways.
/// It redraws the tab (background, 1px frame and its texts) rather than snapshotting it: rendering a live
/// element into a bitmap can come out blank and leave the element stale on screen. Drawn on whole device
/// pixels so the frame stays crisp at any display scaling.
/// </summary>
sealed class ChipGhost : Adorner
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double), typeof(ChipGhost),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly List<(FormattedText Text, Point At)> texts;
    readonly Size size;
    readonly double top;
    readonly Brush frame;
    readonly Brush[] backs; // painted bottom to top

    ChipGhost(UIElement row, List<(FormattedText, Point)> texts, Size size, double x, double top, Brush frame, Brush[] backs) : base(row)
    {
        this.texts = texts;
        this.size = size;
        this.top = top;
        this.frame = frame;
        this.backs = backs;
        X = x;
        IsHitTestVisible = false;
        Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.25, BlurRadius = 14, ShadowDepth = 3, Direction = 270 };
    }

    /// <summary>Left edge, in the row's coordinates.</summary>
    public double X
    {
        get => (double)GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public double TabWidth => size.Width;

    /// <summary>
    /// Copies <paramref name="tab"/>'s texts (font, colour, opacity and position) and floats the copy over it,
    /// with a 1px <paramref name="frame"/> over <paramref name="backs"/> (the first one should be opaque).
    /// </summary>
    public static ChipGhost Show(FrameworkElement row, FrameworkElement tab, Brush frame, params Brush[] backs)
    {
        double ppd = VisualTreeHelper.GetDpi(tab).PixelsPerDip;
        var texts = new List<(FormattedText, Point)>();
        void Collect(DependencyObject d, double opacity)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is UIElement u && u.Visibility != Visibility.Visible) continue;
                double o = opacity * (child is UIElement e ? e.Opacity : 1);
                if (child is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    var brush = tb.Foreground.Clone();
                    brush.Opacity *= o;
                    brush.Freeze();
                    var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                        new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, brush, ppd);
                    texts.Add((ft, tb.TransformToVisual(tab).Transform(new Point())));
                }
                else Collect(child, o);
            }
        }
        Collect(tab, 1);
        var at = tab.TransformToVisual(row).Transform(new Point());
        var ghost = new ChipGhost(row, texts, new Size(tab.ActualWidth, tab.ActualHeight), at.X, at.Y, frame, backs);
        AdornerLayer.GetAdornerLayer(row).Add(ghost);
        return ghost;
    }

    public void SlideTo(double x, Action done)
    {
        var slide = new DoubleAnimation(x, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        slide.Completed += (_, _) => done();
        BeginAnimation(XProperty, slide);
    }

    public void Remove() => AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        static double Snap(double v, double scale) => Math.Round(v * scale) / scale;
        var r = new Rect(Snap(X, dpi.DpiScaleX), Snap(top, dpi.DpiScaleY), size.Width, size.Height);
        dc.PushGuidelineSet(new GuidelineSet(new[] { r.Left, r.Right }, new[] { r.Top, r.Bottom }));
        foreach (var back in backs)
            dc.DrawRoundedRectangle(back, null, r, 8, 8);
        dc.DrawRoundedRectangle(null, new Pen(frame, 1), new Rect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1), 7.5, 7.5);
        dc.Pop();
        foreach (var (text, at) in texts)
            dc.DrawText(text, new Point(r.X + at.X, r.Y + at.Y));
    }
}

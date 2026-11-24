using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Notch;

public partial class MainWindow : Window
{
    const double FullW = 360, FullH = 170;
    static readonly string EdgeFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch", "edge.txt");
    static readonly CultureInfo PtBr = new("pt-BR");
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

    string _edge = "Top";
    bool _open, _syncingVolume;
    string? _coverKey;
    GlobalSystemMediaTransportControlsSessionManager? _media;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        try { _edge = File.ReadAllText(EdgeFile).Trim(); } catch { }
        ApplyEdge();
        _timer.Tick += async (_, _) => await Tick();
        Loaded += async (_, _) =>
        {
            try { _media = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); } catch { }
            await Tick();
            _timer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        // WS_EX_TOOLWINDOW: fora do Alt+Tab. WS_EX_NOACTIVATE: clicar no notch não rouba o foco da janela atual.
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000);
    }

    // ---------- posição ----------

    void ApplyEdge()
    {
        if (_edge != "Left" && _edge != "Right") _edge = "Top";
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Mini.Orientation = _edge == "Top" ? Orientation.Horizontal : Orientation.Vertical;
        (Left, Top, Pill.HorizontalAlignment, Pill.VerticalAlignment, Pill.CornerRadius) = _edge switch
        {
            "Left" => (0.0, (sh - FullH) / 2, HorizontalAlignment.Left, VerticalAlignment.Center, new CornerRadius(0, 16, 16, 0)),
            "Right" => (sw - FullW, (sh - FullH) / 2, HorizontalAlignment.Right, VerticalAlignment.Center, new CornerRadius(16, 0, 0, 16)),
            _ => ((sw - FullW) / 2, 0.0, HorizontalAlignment.Center, VerticalAlignment.Top, new CornerRadius(0, 0, 16, 16)),
        };
        Resize(false, animate: false);
    }

    void Resize(bool open, bool animate = true)
    {
        _open = open;
        bool side = _edge != "Top";
        double w = open ? FullW : side ? 44 : 120, h = open ? FullH : side ? 78 : 30;
        Mini.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Full.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        var d = TimeSpan.FromMilliseconds(animate ? 180 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
    }

    void Pill_MouseEnter(object s, MouseEventArgs e) { Resize(true); _ = UpdateMedia(); }
    void Pill_MouseLeave(object s, MouseEventArgs e) { if (!Pill.ContextMenu.IsOpen) Resize(false); }
    void Menu_Closed(object s, RoutedEventArgs e) { if (!Pill.IsMouseOver) Resize(false); }

    void Edge_Click(object s, RoutedEventArgs e)
    {
        _edge = (string)((MenuItem)s).Tag;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(EdgeFile)!);
            File.WriteAllText(EdgeFile, _edge);
        }
        catch { }
        ApplyEdge();
    }

    void Exit_Click(object s, RoutedEventArgs e) => Close();

    // ---------- relógio + bateria ----------

    async Task Tick()
    {
        var now = DateTime.Now;
        ClockMini.Text = ClockFull.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("ddd dd MMM yyyy", PtBr).Replace(".", "");
        UpdateBattery();
        if (_open) await UpdateMedia();
    }

    void UpdateBattery()
    {
        GetSystemPowerStatus(out var p);
        bool none = p.Percent > 100; // 255 = PC sem bateria
        double pct = none ? 1 : p.Percent / 100.0;
        var geo = RingGeometry(pct);
        RingMini.Data = RingFull.Data = geo;
        RingMini.Stroke = RingFull.Stroke = none ? Brushes.Gray : pct <= 0.2 ? Red : Green;
        BatteryText.Text = none ? "" : $"{p.Percent}%{(p.AC == 1 ? " ⚡" : "")}";
    }

    // Arco no sentido horário a partir do topo, centro (8,8), raio 6.5 (anel de 16px).
    static Geometry RingGeometry(double pct)
    {
        const double c = 8, r = 6.5;
        if (pct >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        double a = pct * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)),
            new Size(r, r), 0, pct > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    // ---------- mídia (Spotify via controles de mídia do Windows) ----------

    async Task UpdateMedia()
    {
        UpdateVolumeSlider();
        try
        {
            _session = _media?.GetSessions().FirstOrDefault(x =>
                x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) ?? _media?.GetCurrentSession();
            if (_session == null) { ShowNothing(); return; }

            var props = await _session.TryGetMediaPropertiesAsync();
            var tl = _session.GetTimelineProperties();
            bool playing = _session.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Nada tocando" : props.Title;
            ArtistText.Text = props.Artist;
            PlayBtn.Content = playing ? "" : "";

            var len = tl.EndTime - tl.StartTime;
            var pos = tl.Position + (playing ? DateTimeOffset.Now - tl.LastUpdatedTime : TimeSpan.Zero);
            double ratio = len > TimeSpan.Zero ? Math.Clamp(pos / len, 0, 1) : 0;
            Elapsed.Text = Fmt(len * ratio);
            Remaining.Text = "-" + Fmt(len * (1 - ratio));
            ProgressFill.Width = ProgressTrack.ActualWidth * ratio;

            string key = props.Title + "|" + props.Artist;
            if (key != _coverKey) { _coverKey = key; await LoadCover(props.Thumbnail); }
        }
        catch { ShowNothing(); }
    }

    static string Fmt(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    void ShowNothing()
    {
        TitleText.Text = "Nada tocando";
        ArtistText.Text = "";
        Elapsed.Text = "0:00";
        Remaining.Text = "-0:00";
        ProgressFill.Width = 0;
        PlayBtn.Content = "";
        CoverBox.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        CoverIcon.Visibility = Visibility.Visible;
        _coverKey = null;
    }

    async Task LoadCover(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        CoverBox.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        CoverIcon.Visibility = Visibility.Visible;
        if (thumb == null) return;
        using var ras = await thumb.OpenReadAsync();
        var ms = new MemoryStream();
        await ras.AsStreamForRead().CopyToAsync(ms);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        CoverBox.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        CoverIcon.Visibility = Visibility.Collapsed;
    }

    async void Media_Click(object s, RoutedEventArgs e)
    {
        if (_session == null) return;
        try
        {
            await ((string)((Button)s).Tag switch
            {
                "prev" => _session.TrySkipPreviousAsync(),
                "next" => _session.TrySkipNextAsync(),
                _ => _session.TryTogglePlayPauseAsync(),
            });
            await Task.Delay(300);
            await UpdateMedia();
        }
        catch { }
    }

    // ---------- volume ----------

    // Lê/ajusta o volume do Spotify no mixer do Windows; sem Spotify tocando som, usa o volume geral.
    static float Volume(float? set = null)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = dev.AudioSessionManager.Sessions;
        float? found = null;
        for (int i = 0; i < sessions.Count; i++)
        {
            var sess = sessions[i];
            if (!IsSpotify(sess.GetProcessID)) continue;
            if (set is float v) sess.SimpleAudioVolume.Volume = v;
            found = sess.SimpleAudioVolume.Volume;
        }
        if (found is float f) return f;
        if (set is float m) dev.AudioEndpointVolume.MasterVolumeLevelScalar = m;
        return dev.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    static bool IsSpotify(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    void UpdateVolumeSlider()
    {
        if (VolSlider.IsMouseCaptureWithin) return;
        try { _syncingVolume = true; VolSlider.Value = Volume(); }
        catch { }
        finally { _syncingVolume = false; }
    }

    void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolume || !IsLoaded) return;
        try { Volume((float)e.NewValue); } catch { }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, FullLife; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}

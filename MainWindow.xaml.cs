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

namespace Orbi;

public partial class MainWindow : Window
{
    const double FullW = 420, FullH = 200;
    // Folga em volta do notch aberto para a animação com "mola" passar um pouco do tamanho final sem cortar.
    const double WinW = FullW + 24, WinH = FullH + 16;
    static readonly string EdgeFile = System.IO.Path.Combine(App.Dir, "edge.txt");
    static readonly CultureInfo PtBr = new("pt-BR");
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

    bool _open, _syncingVolume;
    string? _coverKey;
    GlobalSystemMediaTransportControlsSessionManager? _media;
    GlobalSystemMediaTransportControlsSession? _session;
    MMDevice? _audioDevice;
    readonly List<SimpleAudioVolume> _spotifyVolumes = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public string Edge { get; private set; } = "Top";

    public MainWindow()
    {
        InitializeComponent();
        string edge = "Top";
        try { edge = File.ReadAllText(EdgeFile).Trim(); } catch { }
        SetEdge(edge, save: false);
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

    public void SetEdge(string edge, bool save = true)
    {
        Edge = edge is "Left" or "Right" ? edge : "Top";
        if (save)
        {
            try { File.WriteAllText(EdgeFile, Edge); } catch { }
        }
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Width = WinW;
        Height = WinH;
        Mini.Orientation = Edge == "Top" ? Orientation.Horizontal : Orientation.Vertical;
        (Left, Top, Pill.HorizontalAlignment, Pill.VerticalAlignment, Pill.CornerRadius) = Edge switch
        {
            "Left" => (0.0, (sh - WinH) / 2, HorizontalAlignment.Left, VerticalAlignment.Center, new CornerRadius(0, 20, 20, 0)),
            "Right" => (sw - WinW, (sh - WinH) / 2, HorizontalAlignment.Right, VerticalAlignment.Center, new CornerRadius(20, 0, 0, 20)),
            _ => ((sw - WinW) / 2, 0.0, HorizontalAlignment.Center, VerticalAlignment.Top, new CornerRadius(0, 0, 20, 20)),
        };
        Resize(false, animate: false);
    }

    void Resize(bool open, bool animate = true)
    {
        _open = open;
        bool side = Edge != "Top";
        double w = open ? FullW : side ? 44 : 120, h = open ? FullH : side ? 80 : 30;
        var d = TimeSpan.FromMilliseconds(animate ? (open ? 340 : 220) : 0);
        // Abrir com um leve "pulo" (estilo ilha dinâmica); fechar direto.
        IEasingFunction ease = open
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
        Fade(Full, open ? 1 : 0, animate ? (open ? 220 : 90) : 0, open && animate ? 100 : 0);
        Fade(Mini, open ? 0 : 1, animate ? 140 : 0, !open && animate ? 120 : 0);
        Full.IsHitTestVisible = open;
        if (open) Avatar.Resume(); else Avatar.Pause();
    }

    static void Fade(UIElement el, double to, double ms, double delayMs) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    void Pill_MouseEnter(object s, MouseEventArgs e)
    {
        if (_open) return;
        Resize(true);
        _ = UpdateMedia();
    }

    void Pill_MouseLeave(object s, MouseEventArgs e)
    {
        if (!Pill.ContextMenu.IsOpen && !VolSlider.IsMouseCaptureWithin) Resize(false);
    }

    void Menu_Closed(object s, RoutedEventArgs e)
    {
        if (!Pill.IsMouseOver) Resize(false);
    }

    void Settings_Click(object s, RoutedEventArgs e) => App.OpenSettings();
    void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

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
        bool charging = !none && p.AC == 1;
        double pct = none ? 1 : p.Percent / 100.0;
        var brush = none ? Brushes.Gray : pct <= 0.2 && !charging ? Red : Green;
        RingMini.Data = RingFull.Data = RingGeometry(pct);
        RingMini.Stroke = RingFull.Stroke = BoltMini.Foreground = BoltFull.Foreground = brush;
        BoltMini.Visibility = BoltFull.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        BatteryText.Text = none ? "" : $"{p.Percent}%";
    }

    // Arco no sentido horário a partir do topo, centro (9,9), raio 7.5 (anel de 18px).
    static Geometry RingGeometry(double pct)
    {
        const double c = 9, r = 7.5;
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
            Avatar.SetState(playing ? "humming" : "idle");

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
        Avatar.SetState("idle");
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
    // As sessões de áudio do Spotify são procuradas só a cada segundo (no Tick). Arrastar a barra
    // apenas grava o valor nelas, sem varrer processos a cada movimento (era isso que travava).

    void RefreshAudio()
    {
        _audioDevice?.Dispose();
        _spotifyVolumes.Clear();
        using var en = new MMDeviceEnumerator();
        _audioDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = _audioDevice.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
            if (IsSpotify(sessions[i].GetProcessID)) _spotifyVolumes.Add(sessions[i].SimpleAudioVolume);
    }

    static bool IsSpotify(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    void UpdateVolumeSlider()
    {
        if (VolSlider.IsMouseCaptureWithin) return;
        try
        {
            RefreshAudio();
            _syncingVolume = true;
            // Spotify tocando som: volume dele no mixer. Senão: volume geral.
            VolSlider.Value = _spotifyVolumes.Count > 0
                ? _spotifyVolumes[0].Volume
                : _audioDevice!.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch { }
        finally { _syncingVolume = false; }
    }

    void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolume || _audioDevice == null) return;
        float v = (float)e.NewValue;
        try
        {
            if (_spotifyVolumes.Count > 0) foreach (var sv in _spotifyVolumes) sv.Volume = v;
            else _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
        }
        catch { }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, FullLife; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}

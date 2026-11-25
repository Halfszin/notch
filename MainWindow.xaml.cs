using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
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
using NAudio.Wave;
using Windows.Media.Control;

namespace Orbi;

public partial class MainWindow : Window
{
    // Tamanho do notch aberto (Configurações → Notch → Tamanho).
    static bool Big => Config.Current.NotchSize != "compacto";
    static double FullW => Big ? 700 : 544;
    static double FullH => Big ? 330 : 232;
    // Folga em volta do notch aberto para a animação com "mola" passar um pouco do tamanho final sem cortar.
    static double WinW => FullW + 24;
    static double WinH => FullH + 16;
    static readonly string EdgeFile = System.IO.Path.Combine(App.Dir, "edge.txt");
    static readonly CultureInfo PtBr = new("pt-BR");
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

    bool _open, _syncingVolume, _playing;
    string? _coverKey;
    GlobalSystemMediaTransportControlsSessionManager? _media;
    GlobalSystemMediaTransportControlsSession? _session;
    MMDevice? _audioDevice;
    readonly List<SimpleAudioVolume> _spotifyVolumes = new();
    readonly List<AudioMeterInformation> _spotifyMeters = new();
    readonly Dictionary<uint, bool> _spotifyPids = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _beat = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(7) };
    BeatDetector _beats = new();
    long _lastBounceMs;
    WasapiLoopbackCapture? _capture;
    SpectrumAnalyzer? _spectrum;
    readonly double[] _bands = new double[SpectrumAnalyzer.BandCount];
    readonly double[] _barLevel = new double[SpectrumAnalyzer.BandCount];
    double _bassSmooth;
    System.Windows.Shapes.Rectangle[] _barRects = null!;
    Point? _dragFrom;
    bool _dragging, _toasting;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    readonly MMDeviceEnumerator _deviceEvents = new();
    readonly AudioWatcher _audioWatcher = new();
    int _lowWarnAt = 20;
    readonly NotificationWatcher _notifications = new();
    public readonly Dictation Dictation = new();
    public readonly AgentHub Agents = new();
    public AgentServer? AgentServer { get; private set; }
    readonly DispatcherTimer _look = new() { Interval = TimeSpan.FromMilliseconds(80) };
    DateTime _weatherAt = DateTime.MinValue;
    int _tickCount;

    public string Edge { get; private set; } = "Top";
    /// <summary>Posição ao longo da borda: 0 = começo (esquerda/cima), 1 = fim.</summary>
    public double EdgePos { get; private set; } = 0.5;

    public MainWindow()
    {
        InitializeComponent();
        _barRects = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        ApplyTabs();
        // edge.txt: "Top 0.5" (borda e posição ao longo dela). Arquivo antigo tinha só a borda.
        string[] saved = { "Top" };
        try { saved = File.ReadAllText(EdgeFile).Split(' ', StringSplitOptions.RemoveEmptyEntries); } catch { }
        double pos = saved.Length > 1 && double.TryParse(saved[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.5;
        SetEdge(saved.FirstOrDefault() ?? "Top", pos, save: false);
        _timer.Tick += async (_, _) => await Tick();
        _beat.Tick += (_, _) => Beat();
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); if (!Pill.IsMouseOver) Resize(false); };
        CalendarView.Finished += TimerFinished;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toasting = false; if (!_open) Resize(false); };
        _audioWatcher.DefaultOutputChanged += id => Dispatcher.BeginInvoke(() => OutputChanged(id));
        try { _deviceEvents.RegisterEndpointNotificationCallback(_audioWatcher); } catch { }
        // Notas: a janela do notch normalmente não pega o teclado (para não roubar o foco); ao clicar
        // nas notas ela passa a aceitar, e volta ao normal quando você clica fora.
        Pill.PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (var el = e.OriginalSource as DependencyObject; el != null; el = VisualTreeHelper.GetParent(el))
                if (el is TextBox) { AllowKeyboard(true); return; }
        };
        AiView.Thinking += on => { if (on) React("thinking", 120); else React("happy", 2); };
        StartAgents();
        StartDictation();
        AiView.Hub = Agents;
        // Olhos do avatar seguem o mouse enquanto o notch está aberto.
        _look.Tick += (_, _) => LookAtCursor();
        _notifications.Received += (app, title, body) =>
        {
            var color = new SolidColorBrush(app == "WhatsApp" ? Color.FromRgb(0x25, 0xD3, 0x66) : Color.FromRgb(0x58, 0x65, 0xF2));
            Toast("\uE8BD", body.Length > 0 ? $"{title}: {body}" : title, color, 5);
            React("notifying", 2);
        };
        Deactivated += (_, _) =>
        {
            KeepOnTop();
        UpdateIdleVisibility();
            AllowKeyboard(false);
            if (!Pill.IsMouseOver) Resize(false);
        };
        Updater.Changed += () => Dispatcher.Invoke(() =>
            UpdateDot.Visibility = Updater.ReadyFile != null ? Visibility.Visible : Visibility.Collapsed);
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
        // Avisos de "algo foi copiado" para o histórico da área de transferência.
        AddClipboardFormatListener(h);
        ApplyHotkeys();
        HwndSource.FromHwnd(h)!.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x031D) ClipView.Capture(); // WM_CLIPBOARDUPDATE
            if (msg == 0x0312) OnHotkey(w.ToInt32()); // WM_HOTKEY
            return IntPtr.Zero;
        });
    }

    // O Windows às vezes tira o "sempre por cima" (Alt+Tab, tela cheia, Explorer reiniciando) e o
    // notch fica atrás das janelas. Reafirma a cada segundo, sem ativar a janela nem roubar o foco.
    void KeepOnTop()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST; NOSIZE|NOMOVE|NOACTIVATE
    }

    // ---------- atalhos de teclado de qualquer lugar ----------

    public string? HotkeyProblem { get; private set; }

    /// <summary>Ctrl+Alt+Espaço abre/fecha o notch; Ctrl+Alt+C abre o chat.</summary>
    public void ApplyHotkeys()
    {
        var h = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(h, 1);
        UnregisterHotKey(h, 2);
        HotkeyProblem = null;
        if (!Config.Current.Hotkeys) return;
        const uint ctrlAlt = 0x0002 | 0x0001 | 0x4000; // CONTROL | ALT | NOREPEAT
        var failed = new List<string>();
        if (!RegisterHotKey(h, 1, ctrlAlt, 0x20)) failed.Add("Ctrl+Alt+Espaço");
        if (!RegisterHotKey(h, 2, ctrlAlt, 0x43)) failed.Add("Ctrl+Alt+C");
        if (failed.Count > 0) HotkeyProblem = string.Join(" e ", failed) + " já é usado por outro programa.";
    }

    void OnHotkey(int id)
    {
        if (id == 1) { Resize(!_open); return; }
        if (id != 2) return;
        if (!_open) Resize(true);
        TabAi.IsChecked = true;
        AiView.FocusChat();
        AllowKeyboard(true);
    }

    // ---------- esconder quando parado ----------

    void UpdateIdleVisibility()
    {
        bool idle = Config.Current.AutoHide && !_open && !_toasting && !_playing && !_dragging
                    && !Agents.Busy && Agents.Pending.Count == 0 && CalendarView.RunningText == null;
        // Quase invisível (não zero): assim ainda dá para passar o mouse e abrir.
        Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(idle ? 0.01 : 1, TimeSpan.FromMilliseconds(250)));
    }

    void AllowKeyboard(bool on)
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, -20);
        SetWindowLong(h, -20, on ? ex & ~0x08000000 : ex | 0x08000000);
        if (on) Activate();
    }

    // ---------- avisos rápidos no notch fechado ----------

    public void Toast(string glyph, string text, Brush? color = null, double seconds = 3.5)
    {
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        ToastIcon.Text = glyph;
        ToastIcon.Foreground = color ?? Brushes.White;
        ToastText.Text = text;
        _toasting = true;
        _toastTimer.Stop();
        _toastTimer.Start();
        if (!_open) Resize(false);
        UpdateIdleVisibility();
    }

    void OutputChanged(string deviceId)
    {
        string name = "nova saída de som";
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDevice(deviceId);
            name = dev.FriendlyName;
        }
        catch { }
        Toast("\uE7F6", name);
        React("happy", 2);
        // O visualizador passa a escutar a saída nova.
        if (_playing) { StopCapture(); StartCapture(); }
    }

    // ---------- bandeja: soltar coisas no notch ----------

    void Pill_DragEnter(object s, DragEventArgs e)
    {
        if (ShelfView.Dragging) return;
        if (!_open) Resize(true);
        TabShelf.IsChecked = true;
    }

    void Pill_DragOver(object s, DragEventArgs e)
    {
        e.Effects = ShelfView.Dragging ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    void Pill_Drop(object s, DragEventArgs e)
    {
        if (ShelfView.Dragging) return;
        if (ShelfView.Accept(e.Data))
        {
            React("happy", 2);
            Avatar.Poke("bounce");
        }
        _autoClose.Start();
    }

    // ---------- posição ----------

    public void SetEdge(string edge, double pos = 0.5, bool save = true)
    {
        Edge = edge is "Left" or "Right" ? edge : "Top";
        EdgePos = double.IsFinite(pos) ? Math.Clamp(pos, 0, 1) : 0.5;
        if (save)
        {
            try { File.WriteAllText(EdgeFile, $"{Edge} {EdgePos.ToString(CultureInfo.InvariantCulture)}"); } catch { }
        }
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Width = WinW;
        Height = WinH;
        Full.Width = FullW;
        Full.Height = FullH;
        // Avatar acompanha o tamanho do notch (no Grande ficava pequeno no meio do cartão).
        double avatar = Big ? 150 : 104;
        AvatarButton.Width = Avatar.Width = Avatar.Height = avatar;
        Glow.Width = Glow.Height = avatar - 4;
        // shortcut: a janela (do tamanho do notch aberto) não sai da tela, então o notch para a ~230 px
        // dos cantos; para encostar no canto, deslocar o notch dentro da janela.
        double x = Math.Clamp(EdgePos * sw - WinW / 2, 0, sw - WinW), y = Math.Clamp(EdgePos * sh - WinH / 2, 0, sh - WinH);
        Mini.Orientation = Edge == "Top" ? Orientation.Horizontal : Orientation.Vertical;
        (Left, Top, Pill.HorizontalAlignment, Pill.VerticalAlignment, Pill.CornerRadius, Pill.RenderTransformOrigin) = Edge switch
        {
            "Left" => (0.0, y, HorizontalAlignment.Left, VerticalAlignment.Center, new CornerRadius(0, 16, 16, 0), new Point(0, 0.5)),
            "Right" => (sw - WinW, y, HorizontalAlignment.Right, VerticalAlignment.Center, new CornerRadius(16, 0, 0, 16), new Point(1, 0.5)),
            _ => (x, 0.0, HorizontalAlignment.Center, VerticalAlignment.Top, new CornerRadius(0, 0, 28, 28), new Point(0.5, 0)),
        };
        ShowMiniClock();
        Resize(false, animate: false);
    }

    // Nas laterais a hora fica em pé ("19" em cima de "54"): o notch fica fino.
    void ShowMiniClock()
    {
        string t = CalendarView.RunningText ?? DateTime.Now.ToString("HH:mm");
        ClockMini.Text = Edge == "Top" ? t : t.Replace(":", "\n");
    }

    // ---------- arrastar o notch pelas bordas ----------
    // Segura e arrasta (pelo fundo do notch aberto): ele corre pela borda e pula para a borda
    // (topo, esquerda, direita) mais perto do cursor.

    void Pill_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        _dragFrom = CursorDip();
    }

    void Pill_MouseMove(object s, MouseEventArgs e)
    {
        if (_dragFrom is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = CursorDip();
        if (!_dragging && (p - from).Length < 8) return; // clique normal não arrasta
        if (!_dragging)
        {
            _dragging = true;
            Pill.CaptureMouse();
            Resize(false);
        }
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        string edge = p.Y < Math.Min(p.X, sw - p.X) ? "Top" : p.X < sw - p.X ? "Left" : "Right";
        SetEdge(edge, edge == "Top" ? p.X / sw : p.Y / sh, save: false);
    }

    void Pill_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        Pill.ReleaseMouseCapture();
        _dragFrom = null;
        if (!_dragging) return;
        _dragging = false;
        SetEdge(Edge, EdgePos); // salva onde soltou
    }

    Point CursorDip()
    {
        GetCursorPos(out var pt);
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return toDip.Transform(new Point(pt.X, pt.Y));
    }

    void Resize(bool open, bool animate = true)
    {
        _open = open;
        bool side = Edge != "Top";
        bool toast = _toasting && !open;
        double w = open ? FullW : toast ? 350 : side ? 32 : 120, h = open ? FullH : toast ? 36 : side ? 100 : 30;
        var d = TimeSpan.FromMilliseconds(animate ? (open ? 340 : 220) : 0);
        // Abrir com um leve "pulo" (estilo ilha dinâmica); fechar direto.
        IEasingFunction ease = open
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        Pill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
        Fade(Full, open ? 1 : 0, animate ? (open ? 220 : 90) : 0, open && animate ? 100 : 0);
        Fade(Mini, open || toast ? 0 : 1, animate ? 140 : 0, !open && !toast && animate ? 120 : 0);
        Fade(ToastPanel, toast ? 1 : 0, animate ? 160 : 0, toast && animate ? 120 : 0);
        if (!open) AllowKeyboard(false);
        Full.IsHitTestVisible = open;
        if (!open && _event == "done") HideEvent();
        if (open) Pill.BeginAnimation(OpacityProperty, null); // aparece inteiro ao abrir, mesmo se estava escondido
        if (open) { Avatar.Resume(); _look.Start(); }
        else { Avatar.Pause(); _look.Stop(); Avatar.LookAt(null, null); }
    }

    static void Fade(UIElement el, double to, double ms, double delayMs) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    void Pill_MouseEnter(object s, MouseEventArgs e)
    {
        _autoClose.Stop();
        if (_open || _dragging) return;
        bool wasAsleep = DateTime.Now - _lastSeen > SleepAfter;
        _lastSeen = DateTime.Now;
        Resize(true);
        if (wasAsleep) React("waking", 2.5);
        else Avatar.Poke("nod");
        _ = UpdateMedia();
    }

    // ---------- agentes (Claude Code) ----------

    void StartAgents()
    {
        var cfg = Config.Current;
        // Os eventos chegam numa thread do servidor; o estado mora na thread da interface.
        AgentServer = AgentServer.Start(cfg.AgentPort, cfg.AgentToken,
            (evt, payload) => Dispatcher.InvokeAsync(() => Agents.Handle(evt, payload)).Task.Unwrap());
        bool portMoved = AgentServer != null && AgentServer.Port != cfg.AgentPort;
        if (portMoved) cfg.AgentPort = AgentServer!.Port;
        try { cfg.Save(); } catch { } // guarda o código secreto gerado na primeira vez
        if (portMoved && HookInstaller.IsInstalled())
            Dispatcher.BeginInvoke(() => Toast("\uE7BA", "Agentes: a porta mudou, reconecte em Configurações", null, 8));
        Agents.PermissionAsked += _ => ShowPermission();
        Agents.Changed += () =>
        {
            if (Agents.Pending.Count > 0) ShowPermission();
            else if (_event == "permission") HideEvent();
            int working = Agents.Sessions.Values.Count(x => x.Busy);
            // Compacto para caber ao lado das 9 abas; o texto completo fica na dica.
            AgentsCount.Text = $"● {working}";
            AgentsCount.ToolTip = working == 1 ? "1 agente em andamento" : $"{working} agentes em andamento";
            AgentsCount.Visibility = working > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateBadge();
        };
        Agents.Finished += (project, message) =>
        {
            Sounds.Play("terminou", SystemSounds.Asterisk);
            React("celebrate", 4);
            if (Agents.Pending.Count > 0) { Toast("\uE73E", $"{project}: o Claude terminou", Green, 5); return; } // permissão tem prioridade
            ShowEvent("done", project, "Claude Code terminou", null, message.Length > 0 ? message : "Pronto!");
            if (!_open) Resize(true);
            _autoClose.Start(); // fecha sozinho se você não estiver com o mouse no notch
        };
        Agents.NeedsYou += (project, message) =>
        {
            Toast("\uE7E7", $"{project}: {message}", new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), 6);
            React("notifying", 3);
        };
    }

    // ---------- ditado ----------

    void StartDictation()
    {
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        Dictation.Listening += on => Dispatcher.BeginInvoke(() =>
        {
            if (on) { Toast("\uE720", "Ouvindo… solte o Ctrl direito para colar", red, 600); React("listening", 600); }
            else HideToast();
        });
        Dictation.Working += on => Dispatcher.BeginInvoke(() =>
        {
            if (on) { Toast("\uE70F", "Escrevendo…", null, 600); React("dictating", 120); }
            else { HideToast(); React("happy", 1.5); }
        });
        Dictation.Failed += message => Dispatcher.BeginInvoke(() => Toast("\uE7BA", message, red, 6));
        Dictation.Enable(Config.Current.Dictation);
    }

    void HideToast()
    {
        _toastTimer.Stop();
        _toasting = false;
        if (!_open) Resize(false);
    }

    // ---------- evento ao lado do avatar (estilo coucou): permissão ou "terminou" ----------

    string? _event; // "permission", "done" ou null
    static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A), GreenColor = Color.FromRgb(0x30, 0xD1, 0x58), Blue = Color.FromRgb(0x0A, 0x84, 0xFF);

    void ShowEvent(string kind, string project, string what, string? code, string? message)
    {
        _event = kind;
        var color = kind == "permission" ? Orange : GreenColor;
        TabHome.IsChecked = true;
        MediaPanel.Visibility = Visibility.Collapsed;
        EventPanel.Visibility = Visibility.Visible;
        EventDot.Fill = new SolidColorBrush(color);
        EventProject.Text = project;
        EventWhat.Text = what;
        EventCode.Text = code ?? "";
        EventCodeBox.Visibility = code != null ? Visibility.Visible : Visibility.Collapsed;
        EventMessage.Text = message ?? "";
        EventMessage.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;
        PermButtons.Visibility = kind == "permission" ? Visibility.Visible : Visibility.Collapsed;
        DoneButtons.Visibility = kind == "done" ? Visibility.Visible : Visibility.Collapsed;
        PermQueue.Visibility = kind == "permission" ? Visibility.Visible : Visibility.Collapsed;
        EventGlowColor.Color = Color.FromArgb(0x55, color.R, color.G, color.B);
        EventGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        UpdateBadge();
    }

    void HideEvent()
    {
        _event = null;
        EventPanel.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Visible;
        EventGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        UpdateBadge();
    }

    // Selo no canto do avatar: ! = pedindo permissão, ✓ = terminou, … = algum agente trabalhando.
    void UpdateBadge()
    {
        (string? text, Color color) = _event == "permission" ? ("!", Orange)
            : _event == "done" ? ("✓", GreenColor)
            : Agents.Busy ? ("…", Blue) : (null, Blue);
        AvatarBadge.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        AvatarBadge.Background = new SolidColorBrush(color);
        AvatarBadgeText.Text = text ?? "";
    }

    void ShowPermission()
    {
        if (Agents.Pending.FirstOrDefault() is not { } perm) return;
        bool first = _event != "permission";
        ShowEvent("permission", perm.Project, perm.What, perm.Code, null);
        PermAlways.Visibility = perm.Suggestions is System.Text.Json.Nodes.JsonArray { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PermQueue.Text = Agents.Pending.Count > 1 ? $"+{Agents.Pending.Count - 1} na fila" : "Sem resposta em 2 min, vai para o terminal";
        if (!first) return;
        Sounds.Play("permissao", SystemSounds.Exclamation);
        _autoClose.Stop();
        if (!_open) Resize(true);
        React("alerting", 3);
    }

    void Permission_Click(object s, RoutedEventArgs e)
    {
        if (Agents.Pending.FirstOrDefault() is not { } perm) return;
        AgentHub.Decide(perm, (string)((Button)s).Tag);
        React((string)((Button)s).Tag == "deny" ? "surprised" : "approving", 2);
    }

    void EventOk_Click(object s, RoutedEventArgs e) => HideEvent();

    void LookAtCursor()
    {
        if (!_open || AvatarButton.ActualWidth == 0) return;
        var center = AvatarButton.PointToScreen(new Point(AvatarButton.ActualWidth / 2, AvatarButton.ActualHeight / 2));
        GetCursorPos(out var pt);
        // Distância em pixels → -1..1 (a ~250 px já olha para o lado todo); y positivo = para cima.
        Avatar.LookAt(Math.Clamp((pt.X - center.X) / 250, -1, 1), Math.Clamp((center.Y - pt.Y) / 250, -1, 1));
    }

    void Pill_MouseLeave(object s, MouseEventArgs e)
    {
        _lastSeen = DateTime.Now;
        // Digitando nas notas o notch fica aberto até você clicar fora.
        // Com pedido de permissão aberto o notch espera a sua resposta.
        if (!Pill.ContextMenu.IsOpen && !VolSlider.IsMouseCaptureWithin && !(Pill.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox)
            && Agents.Pending.Count == 0) Resize(false);
    }

    void Menu_Closed(object s, RoutedEventArgs e)
    {
        if (!Pill.IsMouseOver) Resize(false);
    }

    void Tab_Checked(object s, RoutedEventArgs e)
    {
        if (AiView == null) return; // o XAML marca "Início" antes de criar as páginas
        string tag = (string)((RadioButton)s).Tag;
        foreach (var (name, page) in new (string, UIElement)[]
                 { ("home", HomePage), ("calendar", CalendarView), ("shelf", ShelfView), ("clip", ClipView), ("notes", NotesView),
                   ("system", SystemView), ("todo", TodoView), ("shortcuts", ShortcutsView), ("ai", AiView) })
            page.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
    }

    void Settings_Click(object s, RoutedEventArgs e) => App.OpenSettings();

    public void ApplySize() => SetEdge(Edge, EdgePos, save: false);

    /// <summary>Esconde as abas que você tirou do acesso rápido (a Início fica sempre).</summary>
    public void ApplyTabs()
    {
        foreach (var tab in TabBar.Children.OfType<RadioButton>())
        {
            bool hidden = (string)tab.Tag != "home" && Config.Current.HiddenTabs.Contains((string)tab.Tag);
            tab.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            if (hidden && tab.IsChecked == true) TabHome.IsChecked = true;
        }
    }
    void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------- humor do avatar ----------
    // Estado de fundo (música, bateria, sono) + reações curtas que passam por cima por alguns segundos.

    static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);
    DateTime _lastSeen = DateTime.Now, _reactionUntil;
    string? _reaction;
    readonly Queue<DateTime> _clicks = new();
    bool? _wasCharging;

    void React(string state, double seconds)
    {
        _reaction = state;
        _reactionUntil = DateTime.Now.AddSeconds(seconds);
        ApplyMood();
    }

    void ApplyMood()
    {
        if (_reaction != null && DateTime.Now < _reactionUntil) { Avatar.SetState(_reaction); return; }
        _reaction = null;
        GetSystemPowerStatus(out var p);
        bool lowBattery = p.Percent <= 15 && p.AC != 1;
        Avatar.SetState(
            _playing ? "humming" :
            Agents.Busy ? "coding" :
            lowBattery ? "drowsy" :
            !_open && DateTime.Now - _lastSeen > SleepAfter ? "sleeping" :
            "idle");
    }

    // Cutucar: 1-2 cliques = feliz, vários seguidos = surpreso, muitos = confuso, exagero = bravo.
    void Avatar_Click(object s, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _clicks.Enqueue(now);
        while (_clicks.Peek() < now.AddSeconds(-3)) _clicks.Dequeue();
        int n = _clicks.Count;
        if (n >= 12) React("angry", 4);
        else if (n >= 7) React("confused", 6);
        else if (n >= 4) { React("surprised", 2); Avatar.Poke("spin"); }
        else { React("happy", 2); Avatar.Poke("bounce"); }
    }

    void TimerFinished()
    {
        Sounds.Play("timer", SystemSounds.Asterisk);
        Resize(true);
        React("celebrate", 6);
        _autoClose.Start();
    }

    // ---------- relógio + bateria ----------

    async Task Tick()
    {
        var now = DateTime.Now;
        ClockFull.Text = now.ToString("HH:mm");
        // Com o timer correndo, o notch fechado mostra o tempo que falta no lugar da hora.
        ShowMiniClock();
        DateText.Text = now.ToString("ddd dd MMM yyyy", PtBr).Replace(".", "");
        CalendarView.Refresh();
        SystemView.Update(SafeDeviceName());
        foreach (var due in TodoView.TakeDue(now))
        {
            Sounds.Play("lembrete", SystemSounds.Exclamation);
            Toast("\uE787", "Lembrete: " + due.Title, new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), 10);
            React("notifying", 6);
        }
        if (Config.Current.AppNotifications && ++_tickCount % 2 == 0) _notifications.Poll();
        if (now - _weatherAt > TimeSpan.FromMinutes(30)) _ = RefreshWeather();
        UpdateBattery();
        KeepOnTop();
        await UpdateMedia();
        ApplyMood();
    }

    void UpdateBattery()
    {
        GetSystemPowerStatus(out var p);
        bool none = p.Percent > 100; // 255 = PC sem bateria
        bool charging = !none && p.AC == 1;
        double pct = none ? 1 : p.Percent / 100.0;
        var brush = none ? Brushes.Gray : pct <= 0.2 && !charging ? Red : Green;
        RingMini.Data = RingFull.Data = RingGeometry(pct);
        RingMini.Stroke = RingFull.Stroke = BoltMini.Fill = BoltFull.Fill = brush;
        BoltMini.Visibility = BoltFull.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        BatteryText.Text = none ? "" : $"{p.Percent}%";
        // Ligou o carregador: fica animado e avisa. Tirou: leva um susto e avisa.
        if (!none && _wasCharging is bool was && was != charging)
        {
            React(charging ? "excited" : "surprised", 3);
            Toast(charging ? "\uE945" : "\uE83F", charging ? $"Carregando · {p.Percent}%" : $"Na bateria · {p.Percent}%", brush);
        }
        if (!none) _wasCharging = charging;
        // Bateria fraca: avisa em 20% e de novo em 10% (rearma quando carregar).
        if (charging) _lowWarnAt = 20;
        else if (!none && p.Percent <= _lowWarnAt)
        {
            Toast("\uE83F", $"Bateria fraca · {p.Percent}%", Red);
            React("drowsy", 4);
            _lowWarnAt = p.Percent <= 10 ? -1 : 10;
        }
    }

    // Arco no sentido horário a partir do topo, centro (9,9), raio 7.5 (anel de 18px).
    // c = centro, r = raio do meio do traço. Padrão: anel de 18px com traço 2.5 (9 - 2.5/2 = 7.75), alinhado
    // ao trilho cinza, que o Windows desenha por dentro da caixa. A aba Sistema usa anéis maiores.
    internal static Geometry RingGeometry(double pct, double c = 9, double r = 7.75)
    {
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
            SetPlaying(_session.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            TitleText.Text = string.IsNullOrEmpty(props.Title) ? "Nada tocando" : props.Title;
            ArtistText.Text = props.Artist;
            PlayBtn.Content = _playing ? "" : "";
            var playback = _session.GetPlaybackInfo();
            var accent = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            if (playback.IsShuffleActive == true) ShuffleBtn.Foreground = accent; else ShuffleBtn.ClearValue(ForegroundProperty);
            RepeatBtn.Content = playback.AutoRepeatMode == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
            if (playback.AutoRepeatMode is Windows.Media.MediaPlaybackAutoRepeatMode.List or Windows.Media.MediaPlaybackAutoRepeatMode.Track) RepeatBtn.Foreground = accent;
            else RepeatBtn.ClearValue(ForegroundProperty);
            _timeline = (tl.StartTime, tl.EndTime);

            var len = tl.EndTime - tl.StartTime;
            var pos = tl.Position + (_playing ? DateTimeOffset.Now - tl.LastUpdatedTime : TimeSpan.Zero);
            double ratio = len > TimeSpan.Zero ? Math.Clamp(pos / len, 0, 1) : 0;
            Elapsed.Text = Fmt(len * ratio);
            Remaining.Text = "-" + Fmt(len * (1 - ratio));
            ProgressFill.Width = ProgressTrack.ActualWidth * ratio;

            string key = props.Title + "|" + props.Artist;
            if (key != _coverKey)
            {
                // Música nova (não a primeira leitura): o avatar se anima.
                if (_coverKey != null && _playing) React("excited", 2.5);
                _coverKey = key;
                await LoadCover(props.Thumbnail);
            }
        }
        catch { ShowNothing(); }
    }

    (TimeSpan Start, TimeSpan End) _timeline;

    async void Seek_Click(object s, MouseButtonEventArgs e)
    {
        if (_session == null || ProgressTrack.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
        var target = _timeline.Start + (_timeline.End - _timeline.Start) * ratio;
        try
        {
            await _session.TryChangePlaybackPositionAsync(target.Ticks);
            await Task.Delay(250);
            await UpdateMedia();
        }
        catch { }
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
        CoverBox.SetResourceReference(Border.BackgroundProperty, "N.Field");
        CoverIcon.Visibility = Visibility.Visible;
        SetPlaying(false);
        _coverKey = null;
    }

    async Task LoadCover(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        CoverBox.SetResourceReference(Border.BackgroundProperty, "N.Field");
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
            var info = _session.GetPlaybackInfo();
            await ((string)((Button)s).Tag switch
            {
                "prev" => _session.TrySkipPreviousAsync(),
                "next" => _session.TrySkipNextAsync(),
                "shuffle" => _session.TryChangeShuffleActiveAsync(!(info.IsShuffleActive ?? false)),
                // Repetir: desligado → tudo → uma música → desligado
                "repeat" => _session.TryChangeAutoRepeatModeAsync(info.AutoRepeatMode switch
                {
                    Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                    Windows.Media.MediaPlaybackAutoRepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.None,
                    _ => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                }),
                _ => _session.TryTogglePlayPauseAsync(),
            });
            await Task.Delay(300);
            await UpdateMedia();
        }
        catch { }
    }

    // ---------- reagir à música ----------
    // ~30 vezes por segundo, enquanto toca: lê o nível do som do Spotify e faz o avatar, o brilho
    // e o notch fechado pulsarem com ele.

    void SetPlaying(bool playing)
    {
        if (playing == _playing) return;
        _playing = playing;
        Bars.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (playing)
        {
            StartCapture();
            _beat.Start();
        }
        else
        {
            _beat.Stop();
            StopCapture();
            _beats = new BeatDetector();
            _bassSmooth = 0;
            Array.Clear(_barLevel);
            ApplyLevel(0);
        }
    }

    // Escuta o som que sai do PC (loopback do WASAPI), como o Wallpaper Engine. Só roda enquanto toca.
    void StartCapture()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            var fmt = capture.WaveFormat;
            var spectrum = new SpectrumAnalyzer(fmt.SampleRate);
            int channels = fmt.Channels, bytes = fmt.BitsPerSample / 8;
            var mono = new float[4096];
            capture.DataAvailable += (_, a) =>
            {
                // shortcut: só lê 32 bits (float, o padrão do Windows) e 16 bits; outros formatos ficam sem visualizador.
                if (bytes != 4 && bytes != 2) return;
                int frames = a.BytesRecorded / (bytes * channels);
                if (mono.Length < frames) mono = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int at = (f * channels + c) * bytes;
                        sum += bytes == 4 ? BitConverter.ToSingle(a.Buffer, at) : BitConverter.ToInt16(a.Buffer, at) / 32768f;
                    }
                    mono[f] = sum / channels;
                }
                spectrum.Add(mono.AsSpan(0, frames));
            };
            capture.StartRecording();
            _capture = capture;
            _spectrum = spectrum;
        }
        catch { StopCapture(); } // sem loopback: Beat() volta a usar o medidor de volume
    }

    void StopCapture()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
        _spectrum = null;
    }

    void Beat()
    {
        double bass;
        if (_spectrum is { } spectrum)
        {
            bass = spectrum.ReadBass();
            spectrum.ReadBands(_bands);
        }
        else
        {
            float peak = 0;
            try
            {
                peak = _spotifyMeters.Count > 0
                    ? _spotifyMeters.Max(m => m.MasterPeakValue)
                    : _audioDevice?.AudioMeterInformation.MasterPeakValue ?? 0;
            }
            catch { }
            bass = peak;
            Array.Fill(_bands, peak);
        }
        long now = Environment.TickCount64;
        double pulse = _beats.Process(bass, now);
        // Batida forte com o notch aberto: o avatar dá um pulinho (no máximo ~2 por segundo).
        if (_beats.LastWasBeat && pulse > 0.85 && _open && now - _lastBounceMs > 450)
        {
            _lastBounceMs = now;
            Avatar.Poke("bounce");
        }
        // Sobe rápido e desce suave, como os visualizadores do Wallpaper Engine.
        _bassSmooth = bass > _bassSmooth ? _bassSmooth + (bass - _bassSmooth) * 0.6 : _bassSmooth * 0.9;
        for (int i = 0; i < _barLevel.Length; i++)
            _barLevel[i] = _bands[i] > _barLevel[i] ? _barLevel[i] + (_bands[i] - _barLevel[i]) * 0.6 : _barLevel[i] * 0.85;
        ApplyLevel(pulse);
    }

    // Graves (contínuo) + batida (salto): o avatar acompanha o grave o tempo todo e dá um tranco no bumbo.
    void ApplyLevel(double pulse)
    {
        double bass = _bassSmooth;
        AvatarScale.ScaleX = AvatarScale.ScaleY = 1 + 0.10 * bass + 0.08 * pulse;
        GlowScale.ScaleX = GlowScale.ScaleY = 1 + 0.30 * bass + 0.25 * pulse;
        Glow.Opacity = 0.4 + 0.6 * Math.Max(bass, pulse);
        // Notch fechado pulsa; aberto fica parado para não atrapalhar os botões.
        PillScale.ScaleX = PillScale.ScaleY = _open ? 1 : 1 + 0.04 * bass + 0.05 * pulse;
        for (int i = 0; i < _barRects.Length; i++) _barRects[i].Height = 3 + 11 * _barLevel[i];
    }

    // ---------- volume ----------
    // As sessões de áudio do Spotify são procuradas só a cada segundo (no Tick). Arrastar a barra
    // apenas grava o valor nelas, sem varrer processos a cada movimento (era isso que travava).

    void RefreshAudio()
    {
        _audioDevice?.Dispose();
        _spotifyVolumes.Clear();
        _spotifyMeters.Clear();
        using var en = new MMDeviceEnumerator();
        _audioDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = _audioDevice.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (!IsSpotify(sessions[i].GetProcessID)) continue;
            _spotifyVolumes.Add(sessions[i].SimpleAudioVolume);
            _spotifyMeters.Add(sessions[i].AudioMeterInformation);
        }
    }

    bool IsSpotify(uint pid)
    {
        if (_spotifyPids.TryGetValue(pid, out bool known)) return known;
        bool spotify;
        try { spotify = Process.GetProcessById((int)pid).ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }
        catch { spotify = false; }
        if (_spotifyPids.Count > 500) _spotifyPids.Clear(); // PIDs são reaproveitados pelo Windows; não deixa crescer para sempre
        return _spotifyPids[pid] = spotify;
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

    public async Task RefreshWeather()
    {
        _weatherAt = DateTime.Now;
        var cfg = Config.Current;
        if (cfg.Latitude is not double lat || cfg.Longitude is not double lon)
        {
            WeatherText.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var (temp, code) = await Weather.Now(lat, lon);
            var (symbol, text) = Weather.Describe(code);
            WeatherText.Text = $"{symbol} {Math.Round(temp)}°";
            WeatherText.ToolTip = $"{text} · {cfg.City}";
            WeatherText.Visibility = Visibility.Visible;
        }
        catch { _weatherAt = DateTime.Now.AddMinutes(-25); } // sem internet: tenta de novo em ~5 min
    }

    string? SafeDeviceName()
    {
        try { return _audioDevice?.FriendlyName; } catch { return null; }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, FullLife; }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Orbi;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        (App.OrbiWindow.Edge switch { "Left" => EdgeLeft, "Right" => EdgeRight, _ => EdgeTop }).IsChecked = true;
        StartupSwitch.IsChecked = App.StartsWithWindows;
        Banner.SizeChanged += (_, _) => DrawStars();
        BannerAvatar.Reloaded += ShowAvatarStatus;
        ShowAvatarStatus();
        CityInput.Text = Config.Current.City ?? "";
        CityStatus.Text = Config.Current.City == null ? "Escreva sua cidade para ver o clima no notch." : $"Mostrando o clima de {Config.Current.City}.";
        ShowKeyStatus();
        NotifSwitch.IsChecked = Config.Current.AppNotifications;
        (Config.Current.NotchSize == "compacto" ? SizeSmall : SizeBig).IsChecked = true;
        (Config.Current.Theme == "claro" ? ThemeLight : ThemeDark).IsChecked = true;
        AutoHideSwitch.IsChecked = Config.Current.AutoHide;
        HotkeySwitch.IsChecked = Config.Current.Hotkeys;
        SoundSwitch.IsChecked = Config.Current.Sounds;
        ShowHotkeyStatus();
        DictSwitch.IsChecked = Config.Current.Dictation;
        (Config.Current.VoiceModel == "small" ? ModelSmall : ModelBase).IsChecked = true;
        DictWordsBox.Text = Config.Current.DictWords;
        DictSwapsBox.Text = Config.Current.DictReplacements;
        DictSnippetsBox.Text = Config.Current.DictSnippets;
        ShowModelStatus();
        ShowDictStats();
        Loaded += (_, _) => BuildNav();
        foreach (var (tag, name) in new[] { ("calendar", "Calendário"), ("shelf", "Bandeja"), ("clip", "Área de transferência"), ("notes", "Notas"),
                                            ("system", "Sistema"), ("todo", "Tarefas"), ("shortcuts", "Atalhos"), ("ai", "IA") })
        {
            var chip = new CheckBox { Content = name, Tag = tag, Style = (Style)FindResource("Chip"), IsChecked = !Config.Current.HiddenTabs.Contains(tag) };
            chip.Click += Tab_Click;
            TabChips.Children.Add(chip);
        }
        ShowAgentsStatus();
        VersionText.Text = $"Orbi {Updater.Current}";
        Updater.Changed += ShowUpdate;
        Closed += (_, _) => Updater.Changed -= ShowUpdate;
        ShowUpdate();
    }

    void ShowUpdate() => Dispatcher.Invoke(() =>
    {
        UpdateStatus.Text = Updater.Status;
        InstallBtn.Visibility = Updater.ReadyFile != null ? Visibility.Visible : Visibility.Collapsed;
    });

    async void CheckUpdate_Click(object s, RoutedEventArgs e) => await Updater.Check();
    void InstallUpdate_Click(object s, RoutedEventArgs e) => Updater.Install();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1; // barra de título escura (Windows 10 20H1+ / 11)
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    void DrawStars()
    {
        Stars.Children.Clear();
        var rnd = new Random(7);
        for (int i = 0; i < 45; i++)
        {
            double size = rnd.NextDouble() < 0.85 ? 1.5 : 2.5;
            var star = new Ellipse { Width = size, Height = size, Fill = Brushes.White, Opacity = 0.15 + rnd.NextDouble() * 0.5 };
            Canvas.SetLeft(star, rnd.NextDouble() * Banner.ActualWidth);
            Canvas.SetTop(star, rnd.NextDouble() * Banner.ActualHeight);
            Stars.Children.Add(star);
        }
    }

    void Edge_Checked(object s, RoutedEventArgs e)
    {
        if (IsLoaded) App.OrbiWindow.SetEdge((string)((RadioButton)s).Tag);
    }

    void Startup_Click(object s, RoutedEventArgs e)
    {
        try { App.StartsWithWindows = StartupSwitch.IsChecked == true; }
        catch { StartupSwitch.IsChecked = App.StartsWithWindows; }
    }

    async void PickAvatar_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Escolher avatar", Filter = "Avatar (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            string json = File.ReadAllText(dlg.FileName);
            JsonDocument.Parse(json).Dispose(); // recusa o que não for JSON antes de trocar o avatar atual
            File.WriteAllText(AvatarView.AvatarFile, json);
        }
        catch (Exception ex)
        {
            AvatarStatus.Text = "Não deu para ler esse arquivo: " + ex.Message;
            return;
        }
        await ReloadAvatars();
    }

    async void DefaultAvatar_Click(object s, RoutedEventArgs e)
    {
        try { File.Delete(AvatarView.AvatarFile); } catch { }
        await ReloadAvatars();
    }

    void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    async void SaveCity_Click(object s, RoutedEventArgs e)
    {
        var cfg = Config.Current;
        if (string.IsNullOrWhiteSpace(CityInput.Text))
        {
            (cfg.City, cfg.Latitude, cfg.Longitude) = (null, null, null);
            SaveConfig("Clima desligado.", CityStatus);
            await App.OrbiWindow.RefreshWeather();
            return;
        }
        CityStatus.Text = "Procurando…";
        try
        {
            if (await Weather.FindCity(CityInput.Text) is not { } found) { CityStatus.Text = "Não achei essa cidade. Tente só o nome, ex.: Campinas."; return; }
            (cfg.City, cfg.Latitude, cfg.Longitude) = (found.Name, found.Lat, found.Lon);
            CityInput.Text = found.Name;
            SaveConfig($"Mostrando o clima de {found.Name}.", CityStatus);
            await App.OrbiWindow.RefreshWeather();
        }
        catch (Exception ex) { CityStatus.Text = "Sem conexão para procurar a cidade: " + ex.Message; }
    }

    void SaveKey_Click(object s, RoutedEventArgs e)
    {
        string key = KeyInput.Password.Trim();
        if (key.Length > 0 && !key.StartsWith("sk-ant-")) { KeyStatus.Text = "Essa não parece uma chave da Anthropic (começa com sk-ant-)."; return; }
        Config.Current.ApiKey = key.Length == 0 ? null : key;
        KeyInput.Clear();
        SaveConfig(null, KeyStatus);
        ShowKeyStatus();
    }

    // ---------- barra lateral e busca (como no coucou) ----------

    static readonly Dictionary<string, string> NavIcons = new()
    {
        ["NOTCH"] = "\uE7F4", ["APARÊNCIA"] = "\uE790", ["ATALHOS DE TECLADO"] = "\uE765", ["SONS"] = "\uE767", ["AVATAR"] = "\uE77B",
        ["CLIMA"] = "\uE706", ["INTELIGÊNCIA ARTIFICIAL"] = "\uE99A", ["AGENTES (CLAUDE CODE)"] = "\uE756", ["NOTIFICAÇÕES"] = "\uEA8F",
        ["ATUALIZAÇÕES"] = "\uE895", ["APP"] = "\uE7E8", ["DITADO (VOZ)"] = "\uE720", ["ESTATÍSTICAS DO DITADO"] = "\uE9D2",
    };

    // Cada seção = o título (TextBlock com o estilo Label) + o que vem depois até o próximo título.
    List<(TextBlock Label, List<UIElement> Items, Border Nav)> _sections = new();

    void BuildNav()
    {
        var label = (Style)FindResource("Label");
        _sections.Clear();
        NavItems.Children.Clear();
        foreach (UIElement child in Sections.Children)
        {
            if (child is TextBlock tb && tb.Style == label)
            {
                var nav = NavItem(tb.Text);
                _sections.Add((tb, new List<UIElement>(), nav));
                NavItems.Children.Add(nav);
            }
            else if (_sections.Count > 0) _sections[^1].Items.Add(child);
        }
        if (_sections.Count > 0) Select(_sections[0].Nav);
    }

    Border NavItem(string section)
    {
        string title = System.Globalization.CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(section.ToLowerInvariant()).Replace(" De ", " de ").Replace("(Claude Code)", "");
        var item = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        item.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = NavIcons.GetValueOrDefault(section, "\uE713"), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray },
                new TextBlock { Text = title.Trim(), FontSize = 13.5, Margin = new Thickness(12, 0, 0, 0) },
            },
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            var target = _sections.First(x => x.Nav == item).Label;
            ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset + target.TranslatePoint(new Point(0, 0), ContentScroll).Y - 8);
            Select(item);
        };
        return item;
    }

    void Select(Border nav)
    {
        foreach (var x in _sections) x.Nav.Background = x.Nav == nav ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F)) : Brushes.Transparent;
    }

    void Search_TextChanged(object s, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Banner.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var cmp = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        bool Match(string text) => cmp.IndexOf(text, q, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
        foreach (var (label, items, nav) in _sections)
        {
            bool show = q.Length == 0 || Match(label.Text) || items.Any(i => Texts(i).Any(Match));
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = nav.Visibility = v;
            foreach (var i in items) i.Visibility = v;
        }
    }

    // Todos os textos visíveis de um pedaço da tela (títulos, explicações, botões).
    static IEnumerable<string> Texts(object node)
    {
        if (node is TextBlock tb) yield return tb.Text;
        if (node is ContentControl { Content: string s }) yield return s;
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                foreach (var t in Texts(child)) yield return t;
    }

    // ---------- aparência, atalhos, sons ----------

    void Theme_Checked(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Config.Current.Theme = (string)((RadioButton)s).Tag;
        SaveConfig(null, null);
        Theme.Apply(Config.Current.Theme);
    }

    void AutoHide_Click(object s, RoutedEventArgs e)
    {
        Config.Current.AutoHide = AutoHideSwitch.IsChecked == true;
        SaveConfig(null, null);
    }

    void Hotkeys_Click(object s, RoutedEventArgs e)
    {
        Config.Current.Hotkeys = HotkeySwitch.IsChecked == true;
        SaveConfig(null, null);
        App.OrbiWindow.ApplyHotkeys();
        ShowHotkeyStatus();
    }

    void ShowHotkeyStatus() => HotkeyStatus.Text = "Ctrl+Alt+Espaço abre e fecha o notch · Ctrl+Alt+C abre o chat."
        + (App.OrbiWindow.HotkeyProblem is { } p ? " " + p : "");

    void Sound_Click(object s, RoutedEventArgs e)
    {
        Config.Current.Sounds = SoundSwitch.IsChecked == true;
        SaveConfig(null, null);
    }

    void OpenSounds_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Sounds.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Sounds.Folder) { UseShellExecute = true });
    }

    // ---------- ditado ----------

    CancellationTokenSource? _download;

    void Dict_Click(object s, RoutedEventArgs e)
    {
        Config.Current.Dictation = DictSwitch.IsChecked == true;
        SaveConfig(null, null);
        App.OrbiWindow.Dictation.Enable(Config.Current.Dictation);
        ShowModelStatus();
    }

    void Model_Checked(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Config.Current.VoiceModel = (string)((RadioButton)s).Tag;
        SaveConfig(null, null);
        ShowModelStatus();
    }

    void ShowModelStatus()
    {
        var m = VoiceModels.All.First(x => x.Id == Config.Current.VoiceModel);
        bool has = VoiceModels.Has(m.Id);
        ModelBtn.Visibility = has || _download != null ? Visibility.Collapsed : Visibility.Visible;
        ModelBtn.Content = $"Baixar ({m.ApproxMb} MB)";
        if (_download == null)
            ModelStatus.Text = has
                ? $"Modelo {m.Name.ToLowerInvariant()} pronto." + (Config.Current.Dictation ? " Segure Ctrl direito para ditar." : " Ligue o ditado acima.")
                : $"Baixe uma vez ({m.ApproxMb} MB). \"Preciso\" erra menos, mas demora um pouco mais para escrever.";
    }

    async void DownloadModel_Click(object s, RoutedEventArgs e)
    {
        string id = Config.Current.VoiceModel;
        _download = new CancellationTokenSource();
        ModelBtn.Visibility = Visibility.Collapsed;
        try
        {
            await VoiceModels.Download(id, new Progress<double>(p => ModelStatus.Text = $"Baixando o modelo de voz… {p:P0}"), _download.Token);
            _download = null;
            ShowModelStatus();
        }
        catch (Exception ex)
        {
            _download = null;
            ShowModelStatus();
            ModelStatus.Text = "Não baixou: " + ex.Message;
        }
    }

    void DictText_LostFocus(object s, RoutedEventArgs e)
    {
        Config.Current.DictWords = DictWordsBox.Text;
        Config.Current.DictReplacements = DictSwapsBox.Text;
        Config.Current.DictSnippets = DictSnippetsBox.Text;
        SaveConfig(null, null);
    }

    void ShowDictStats()
    {
        var st = DictationStats.Load();
        int today = st.WordsByDay.GetValueOrDefault(DateTime.Today.ToString("yyyy-MM-dd"));
        int streak = DictationText.Streak(st.WordsByDay.Keys.Select(k => DateTime.TryParse(k, out var d) ? d : DateTime.MinValue), DateTime.Today);
        var saved = TimeSpan.FromSeconds(st.SavedSeconds);
        DictStats.Text = st.TotalWords == 0 ? "Você ainda não ditou nada."
            : $"{today} palavras hoje · {st.TotalWords} no total · {(saved.TotalHours >= 1 ? $"{(int)saved.TotalHours}h{saved.Minutes:00}" : $"{saved.Minutes} min")} economizados · {streak} {(streak == 1 ? "dia seguido" : "dias seguidos")}";
        var recent = st.Recent.Where(r => r.When.Date == DateTime.Today).Take(5).Select(r => $"{r.When:HH:mm}  {(r.Text.Length > 90 ? r.Text[..90] + "…" : r.Text)}");
        DictRecent.Text = string.Join("\n", recent);
    }

    void Size_Checked(object s, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Config.Current.NotchSize = (string)((RadioButton)s).Tag;
        SaveConfig(null, null);
        App.OrbiWindow.ApplySize();
    }

    void Tab_Click(object s, RoutedEventArgs e)
    {
        var chip = (CheckBox)s;
        string tag = (string)chip.Tag;
        Config.Current.HiddenTabs.Remove(tag);
        if (chip.IsChecked != true) Config.Current.HiddenTabs.Add(tag);
        SaveConfig(null, null);
        App.OrbiWindow.ApplyTabs();
    }

    void ShowAgentsStatus(string? extra = null)
    {
        bool on = HookInstaller.IsInstalled();
        AgentsOn.Content = on ? "Reconectar" : "Conectar";
        AgentsOff.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        AgentsStatus.Text = (on
            ? "Conectado. Veja na aba IA → Agentes; pedidos de permissão aparecem no notch com Permitir / Sempre / Negar."
            : "Mostra o que cada sessão do Claude Code está fazendo, deixa você aprovar comandos pelo notch e avisa quando ele termina.")
            + (extra != null ? "\n" + extra : "");
    }

    void AgentsOn_Click(object s, RoutedEventArgs e)
    {
        var server = App.OrbiWindow.AgentServer;
        if (server == null) { ShowAgentsStatus("Não deu para abrir a porta local dos agentes. Feche e abra o Orbi."); return; }
        var ok = MessageBox.Show(this,
            $"O Orbi vai adicionar os hooks dele ao arquivo do Claude Code:\n{HookInstaller.SettingsPath}\n\n" +
            "• Antes, uma cópia de segurança do arquivo é salva ao lado dele.\n" +
            "• Suas configurações e hooks de outros programas não são mexidos.\n" +
            "• Se você não tiver uma status line, o Orbi coloca a dele (mostra os limites do plano).\n" +
            "• Com o Orbi fechado, o Claude Code continua funcionando normalmente.\n\n" +
            "Sessões do Claude Code já abertas só passam a aparecer depois de reiniciadas. Continuar?",
            "Conectar ao Claude Code", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            var (backup, statusLine) = HookInstaller.Install(server.Port, Config.Current.AgentToken);
            ShowAgentsStatus((backup != null ? $"Cópia de segurança: {backup}" : "Arquivo criado.")
                + (statusLine ? "" : " Você já tinha uma status line, então ela ficou como estava (sem os limites do plano no Orbi)."));
        }
        catch (Exception ex) { ShowAgentsStatus("Não conectou: " + ex.Message); }
    }

    void AgentsOff_Click(object s, RoutedEventArgs e)
    {
        try
        {
            string? backup = HookInstaller.Uninstall();
            ShowAgentsStatus("Desconectado: só as entradas do Orbi saíram." + (backup != null ? $" Cópia de segurança: {backup}" : ""));
        }
        catch (Exception ex) { ShowAgentsStatus("Não desconectou: " + ex.Message); }
    }

    void ShowKeyStatus() => KeyStatus.Text = Config.Current.ApiKey is { } k
        ? $"Chave salva (…{k[^4..]}), só usada se o Claude Code não estiver instalado (é cobrada por uso). Para apagar, salve vazio."
        : "Opcional e cobrada por uso. Com o Claude Code instalado, o chat usa a sua assinatura e esta chave nem é usada.";

    void Notif_Click(object s, RoutedEventArgs e)
    {
        Config.Current.AppNotifications = NotifSwitch.IsChecked == true;
        SaveConfig(null, null);
    }

    static void SaveConfig(string? ok, TextBlock? status)
    {
        try
        {
            Config.Current.Save();
            if (ok != null && status != null) status.Text = ok;
        }
        catch (Exception ex) { if (status != null) status.Text = "Não salvou: " + ex.Message; }
    }

    void Link_RequestNavigate(object s, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    async Task ReloadAvatars()
    {
        await BannerAvatar.Reload();
        await App.OrbiWindow.Avatar.Reload();
    }

    void ShowAvatarStatus()
    {
        bool custom = File.Exists(AvatarView.AvatarFile);
        DefaultAvatarBtn.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        string result = BannerAvatar.LastResult;
        AvatarStatus.Text = result is "ok" or "none"
            ? custom ? "Usando o avatar que você escolheu." : "Usando o avatar padrão do Orbi."
            : "O avatar não carregou: " + result;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}

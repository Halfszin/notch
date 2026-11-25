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

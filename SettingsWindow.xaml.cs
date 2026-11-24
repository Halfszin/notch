using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Notch;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        (App.Notch.Edge switch { "Left" => EdgeLeft, "Right" => EdgeRight, _ => EdgeTop }).IsChecked = true;
        StartupSwitch.IsChecked = App.StartsWithWindows;
        Banner.SizeChanged += (_, _) => DrawStars();
        BannerAvatar.Reloaded += ShowAvatarStatus;
        ShowAvatarStatus();
    }

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
        if (IsLoaded) App.Notch.SetEdge((string)((RadioButton)s).Tag);
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

    async Task ReloadAvatars()
    {
        await BannerAvatar.Reload();
        await App.Notch.Avatar.Reload();
    }

    void ShowAvatarStatus()
    {
        bool custom = File.Exists(AvatarView.AvatarFile);
        DefaultAvatarBtn.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        string result = BannerAvatar.LastResult;
        AvatarStatus.Text = result is "ok" or "none"
            ? custom ? "Usando o avatar que você escolheu." : "Usando o avatar padrão (New avatar)."
            : "O avatar não carregou: " + result;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}

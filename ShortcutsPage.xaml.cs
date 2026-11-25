using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Orbi;

// Atalhos fixos: apps, arquivos, pastas e sites, abertos com um clique. Salvos em atalhos.json.
public partial class ShortcutsPage : UserControl
{
    public sealed class Shortcut
    {
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
    }

    static readonly string FilePath = Path.Combine(App.Dir, "atalhos.json");
    readonly List<Shortcut> _items;

    public ShortcutsPage()
    {
        InitializeComponent();
        try { _items = JsonSerializer.Deserialize<List<Shortcut>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { _items = new(); }
        Draw();
        Theme.Changed += Draw;
    }

    static bool IsUrl(string target) => target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    void Draw()
    {
        Items.Children.Clear();
        // Ferramentas que já vêm prontas: usam a Ferramenta de Captura do próprio Windows.
        Items.Children.Add(BuiltIn("\uE714", "Gravar tela", "Grava a tela pela Ferramenta de Captura do Windows (Win+Shift+R)", RecordScreen));
        Items.Children.Add(BuiltIn("\uE722", "Print", "Tira um print de uma parte da tela (Win+Shift+S)", Snip));
        foreach (var s in _items) Items.Children.Add(Tile(s));
        var add = Tile(null);
        Items.Children.Add(add);
    }

    FrameworkElement Tile(Shortcut? s)
    {
        var mdl2 = new FontFamily("Segoe MDL2 Assets");
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Theme.Res("N.Field"), HorizontalAlignment = HorizontalAlignment.Center };
        if (s == null) icon.Child = new TextBlock { Text = "", FontFamily = mdl2, FontSize = 16, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else if (IsUrl(s.Target)) icon.Child = new TextBlock { Text = "", FontFamily = mdl2, FontSize = 18, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else if (Shell.FileIcon(s.Target) is { } img) icon.Child = new Image { Source = img, Width = 26, Height = 26 };

        var tile = new StackPanel { Width = 62, Margin = new Thickness(2, 2, 2, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = s?.Target ?? "Adicionar atalho" };
        tile.Children.Add(icon);
        tile.Children.Add(new TextBlock { Text = s?.Name ?? "Adicionar", FontSize = 10.5, Foreground = Theme.Res("N.Sub"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 60, Margin = new Thickness(0, 4, 0, 0) });
        AutomationProperties.SetName(tile, s?.Name ?? "Adicionar atalho");

        if (s == null)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("App ou arquivo…", AddFile));
            menu.Items.Add(Item("Pasta…", AddFolder));
            menu.Items.Add(Item("Site…", () => { UrlBox.Visibility = Visibility.Visible; UrlInput.Focus(); }));
            tile.MouseLeftButtonUp += (_, _) => { menu.PlacementTarget = tile; menu.IsOpen = true; };
        }
        else
        {
            tile.MouseLeftButtonUp += (_, _) => Open(s);
            var menu = new ContextMenu();
            menu.Items.Add(Item("Remover", () => { _items.Remove(s); Save(); Draw(); }));
            tile.ContextMenu = menu;
        }
        return tile;
    }

    FrameworkElement BuiltIn(string glyph, string name, string tip, Action run)
    {
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Theme.Res("N.Field"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var tile = new StackPanel { Width = 62, Margin = new Thickness(2, 2, 2, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
        tile.Children.Add(icon);
        tile.Children.Add(new TextBlock { Text = name, FontSize = 10.5, Foreground = Theme.Res("N.Sub"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        AutomationProperties.SetName(tile, name);
        tile.MouseLeftButtonUp += (_, _) => run();
        return tile;
    }

    static void Snip()
    {
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch { Shell.PressKeys(0x5B, 0x10, 0x53); } // Win+Shift+S
    }

    // shortcut: usa a gravação da Ferramenta de Captura do Windows 11 (Win+Shift+R); um gravador próprio
    // exigiria capturar e codificar vídeo dentro do Orbi.
    static void RecordScreen() => Shell.PressKeys(0x5B, 0x10, 0x52);

    static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    static void Open(Shortcut s)
    {
        try { Process.Start(new ProcessStartInfo(s.Target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Não deu para abrir \"{s.Name}\":\n{ex.Message}", "Orbi"); }
    }

    void AddFile()
    {
        var dlg = new OpenFileDialog { Title = "Escolher app ou arquivo" };
        if (dlg.ShowDialog() == true) Add(Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
    }

    void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Escolher pasta" };
        if (dlg.ShowDialog() == true) Add(Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName, dlg.FolderName);
    }

    void UrlInput_TextChanged(object s, TextChangedEventArgs e) => UrlHint.Visibility = UrlInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UrlInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { UrlInput.Clear(); UrlBox.Visibility = Visibility.Collapsed; return; }
        if (e.Key != Key.Enter) return;
        string text = UrlInput.Text.Trim();
        if (!IsUrl(text)) text = "https://" + text;
        // Só endereços web de verdade: nada de "file:" ou comandos escondidos num "site".
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !uri.Host.Contains('.'))
        {
            UrlHint.Text = "Esse endereço não parece um site. Ex.: youtube.com";
            UrlInput.Clear();
            return;
        }
        Add(uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host, uri.ToString());
        UrlInput.Clear();
        UrlBox.Visibility = Visibility.Collapsed;
    }

    void Add(string name, string target)
    {
        _items.Add(new Shortcut { Name = name, Target = target });
        Save();
        Draw();
    }

    void Save()
    {
        try
        {
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(_items));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { } // shortcut: falha ao salvar só perde o atalho novo quando o Orbi reiniciar
    }
}

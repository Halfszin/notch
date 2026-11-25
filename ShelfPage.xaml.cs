using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbi;

// Bandeja: o que você solta no notch fica "flutuando" aqui até ser arrastado para outro lugar.
// shortcut: guarda só enquanto o Orbi está aberto; para sobreviver a reinícios, salvar a lista em App.Dir.
public partial class ShelfPage : UserControl
{
    record Item(string? File, string? Text);

    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Orbi", "bandeja");
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    readonly List<Item> _items = new();
    Point? _pressAt;

    /// <summary>Verdadeiro enquanto um item está sendo arrastado para fora (para o notch não recebê-lo de volta).</summary>
    public bool Dragging { get; private set; }

    public ShelfPage()
    {
        InitializeComponent();
        Theme.Changed += Draw;
    }

    /// <summary>Recebe o que foi solto no notch. Devolve true se havia algo que dá para guardar.</summary>
    public bool Accept(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) _items.Add(new Item(f, null));
            else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                _items.Add(new Item(SavePng(bmp), null));
            else if (data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
                _items.Add(new Item(null, text));
            else return false;
        }
        catch { return false; }
        Draw();
        return true;
    }

    static string SavePng(BitmapSource bmp)
    {
        Directory.CreateDirectory(TempDir);
        string file = Path.Combine(TempDir, $"imagem-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
        return file;
    }

    void Clear_Click(object s, MouseButtonEventArgs e)
    {
        _items.Clear();
        Draw();
    }

    void Draw()
    {
        Items.Children.Clear();
        foreach (var item in _items) Items.Children.Add(Tile(item));
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = _items.Count == 1 ? "1 item" : $"{_items.Count} itens";
    }

    FrameworkElement Tile(Item item)
    {
        string label = item.File != null ? Path.GetFileName(item.File) : item.Text!.Replace('\n', ' ');
        var thumb = new Border { Width = 54, Height = 44, CornerRadius = new CornerRadius(8), Background = Theme.Res("N.Field") };
        if (item.File != null && ImageExt.Contains(Path.GetExtension(item.File)) && Thumb(item.File) is { } img)
            thumb.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        else if (item.File != null && Shell.FileIcon(item.File) is { } icon)
            thumb.Child = new Image { Source = icon, Width = 28, Height = 28 };
        else
            thumb.Child = new TextBlock { Text = item.Text?.Length > 40 ? item.Text[..40] : item.Text, FontSize = 8.5, Foreground = Theme.Res("N.Sub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };

        var remove = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Foreground = Theme.Res("N.Fg"), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0), Visibility = Visibility.Hidden };
        remove.MouseLeftButtonDown += (_, e) => { e.Handled = true; _items.Remove(item); Draw(); };

        var tile = new Grid { Width = 66, Margin = new Thickness(2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = item.File ?? item.Text };
        tile.Children.Add(new StackPanel
        {
            Children =
            {
                thumb,
                new TextBlock { Text = label, FontSize = 10, Foreground = Theme.Res("N.Sub"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 64 },
            },
        });
        tile.Children.Add(remove);
        tile.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        tile.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        tile.PreviewMouseLeftButtonDown += (_, e) => { _pressAt = e.GetPosition(this); e.Handled = true; }; // não deixa o notch começar a ser arrastado
        tile.MouseMove += (_, e) =>
        {
            if (_pressAt is not Point p || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - p).Length < 6) return;
            _pressAt = null;
            var data = new DataObject();
            if (item.File != null) data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.File });
            else data.SetText(item.Text!);
            // Mover (ex.: soltar numa pasta do Explorer) tira da bandeja; copiar deixa.
            Dragging = true;
            DragDropEffects result;
            try { result = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move); }
            finally { Dragging = false; }
            if (result == DragDropEffects.Move)
            {
                _items.Remove(item);
                Draw();
            }
        };
        tile.MouseLeftButtonUp += (_, _) => _pressAt = null;
        return tile;
    }

    static BitmapSource? Thumb(string file)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(file);
            b.DecodePixelWidth = 120;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

}

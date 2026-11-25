using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbi;

// Histórico da área de transferência: últimos 20 textos/prints, só na memória (nada vai para o disco).
public partial class ClipboardPage : UserControl
{
    const int Max = 20;
    record Entry(string? Text, BitmapSource? Image);
    readonly List<Entry> _entries = new();
    bool _ignoreNext;

    public ClipboardPage()
    {
        InitializeComponent();
        Theme.Changed += Draw;
    }

    /// <summary>Chamado quando o Windows avisa que a área de transferência mudou.</summary>
    public async void Capture()
    {
        if (_ignoreNext) { _ignoreNext = false; return; }
        // A área de transferência pode estar ocupada por quem acabou de copiar: tenta algumas vezes.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                // Gerenciadores de senha marcam a cópia para ficar fora de históricos: respeita.
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (Clipboard.ContainsText()) Add(new Entry(Clipboard.GetText(), null));
                else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) { img.Freeze(); Add(new Entry(null, img)); }
                return;
            }
            catch { await Task.Delay(80); }
        }
    }

    void Add(Entry e)
    {
        if (e.Text != null)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            _entries.RemoveAll(x => x.Text == e.Text); // repetido sobe para o topo
        }
        _entries.Insert(0, e);
        if (_entries.Count > Max) _entries.RemoveAt(Max);
        Draw();
    }

    void Clear_Click(object s, MouseButtonEventArgs e)
    {
        _entries.Clear();
        Draw();
    }

    void Draw()
    {
        Items.Children.Clear();
        foreach (var entry in _entries) Items.Children.Add(Row(entry));
        Empty.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = _entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    FrameworkElement Row(Entry entry)
    {
        var hover = Theme.Res("N.Hover");
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 4, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.Child = entry.Image != null
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Border { Width = 64, Height = 36, CornerRadius = new CornerRadius(5), Background = new ImageBrush(entry.Image) { Stretch = Stretch.UniformToFill } },
                    new TextBlock { Text = $"Print {entry.Image.PixelWidth}×{entry.Image.PixelHeight}", FontSize = 12, Foreground = Theme.Res("N.Sub"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            }
            : new TextBlock { Text = entry.Text!.Trim(), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, TextWrapping = TextWrapping.Wrap };
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => CopyBack(entry);
        return row;
    }

    void CopyBack(Entry entry)
    {
        try
        {
            _ignoreNext = true; // a nossa própria cópia não vira item novo
            if (entry.Text != null) Clipboard.SetText(entry.Text);
            else Clipboard.SetImage(entry.Image);
            Hint.Text = "Copiado!";
        }
        catch
        {
            _ignoreNext = false;
            Hint.Text = "Não deu para copiar, tente de novo";
        }
    }
}

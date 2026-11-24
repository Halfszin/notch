using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Orbi;

// Bloco de notas rápido: salva em %AppData%\Orbi\notas.txt meio segundo depois de parar de digitar.
public partial class NotesPage : UserControl
{
    static readonly string NotesFile = Path.Combine(App.Dir, "notas.txt");
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _loading;

    public NotesPage()
    {
        InitializeComponent();
        _loading = true;
        try { if (File.Exists(NotesFile)) Box.Text = File.ReadAllText(NotesFile); }
        catch { Status.Text = "Não deu para abrir as notas salvas"; }
        _loading = false;
        Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _save.Tick += (_, _) => Save();
        // Fechar o Orbi com nota pendente não perde o que foi digitado.
        Application.Current.Exit += (_, _) => { if (_save.IsEnabled) Save(); };
    }

    void Box_TextChanged(object s, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        _save.Stop();
        _save.Start();
    }

    void Save()
    {
        _save.Stop();
        try
        {
            // Grava num .tmp e troca: se der erro no meio, a nota anterior continua inteira.
            File.WriteAllText(NotesFile + ".tmp", Box.Text);
            File.Move(NotesFile + ".tmp", NotesFile, true);
            Status.Text = $"salvo às {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) { Status.Text = "Não salvou: " + ex.Message; }
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbi;

// Lista de tarefas; as que têm hora viram lembrete (o notch avisa na hora). Salva em tarefas.json.
public partial class TodoPage : UserControl
{
    public sealed class TodoItem
    {
        public string Title { get; set; } = "";
        public DateTime? Due { get; set; }
        public bool Done { get; set; }
        public bool Notified { get; set; }
    }

    static readonly string FilePath = Path.Combine(App.Dir, "tarefas.json");
    readonly List<TodoItem> _items;

    public TodoPage()
    {
        InitializeComponent();
        try { _items = JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { _items = new(); }
        Draw();
        Theme.Changed += Draw;
    }

    /// <summary>Lembretes que venceram agora (cada um avisa uma vez só).</summary>
    public List<TodoItem> TakeDue(DateTime now)
    {
        var due = _items.Where(i => !i.Done && !i.Notified && i.Due <= now).ToList();
        if (due.Count == 0) return due;
        foreach (var i in due) i.Notified = true;
        Save();
        Draw();
        return due;
    }

    void Input_TextChanged(object s, TextChangedEventArgs e) => Hint.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Input_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(Input.Text)) return;
        var (title, due) = TaskParser.Parse(Input.Text, DateTime.Now);
        _items.Insert(0, new TodoItem { Title = title.Length > 0 ? title : Input.Text.Trim(), Due = due });
        Input.Clear();
        Save();
        Draw();
    }

    void ClearDone_Click(object s, MouseButtonEventArgs e)
    {
        _items.RemoveAll(i => i.Done);
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
        catch (Exception ex) { CountText.Text = "Não salvou: " + ex.Message; }
    }

    void Draw()
    {
        Items.Children.Clear();
        // Pendentes primeiro (as com hora mais perto no topo), concluídas no fim.
        foreach (var item in _items.OrderBy(i => i.Done).ThenBy(i => i.Due ?? DateTime.MaxValue)) Items.Children.Add(Row(item));
        int open = _items.Count(i => !i.Done);
        CountText.Text = _items.Count == 0 ? "Sem tarefas" : open == 1 ? "1 pendente" : $"{open} pendentes";
    }

    FrameworkElement Row(TodoItem item)
    {
        var mdl2 = new FontFamily("Segoe MDL2 Assets");
        var check = new TextBlock
        {
            Text = item.Done ? "" : "", FontFamily = mdl2, FontSize = 14, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Foreground = item.Done ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : Brushes.Gray, Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(check, item.Done ? "Desmarcar" : "Concluir");
        check.MouseLeftButtonUp += (_, _) => { item.Done = !item.Done; Save(); Draw(); };

        var remove = new TextBlock { Text = "", FontFamily = mdl2, FontSize = 10, Foreground = Brushes.Gray, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden, Margin = new Thickness(6, 0, 0, 0) };
        remove.MouseLeftButtonUp += (_, _) => { _items.Remove(item); Save(); Draw(); };

        var row = new DockPanel { Margin = new Thickness(2, 2, 6, 2), Background = Brushes.Transparent };
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(remove);
        if (item.Due is DateTime due)
        {
            bool late = !item.Done && due <= DateTime.Now;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(late ? Color.FromRgb(0x4A, 0x1F, 0x1F) : ((SolidColorBrush)Theme.Res("N.Field")).Color),
                Child = new TextBlock { Text = When(due), FontSize = 10.5, Foreground = late ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) : Theme.Res("N.Sub") },
            };
            DockPanel.SetDock(chip, Dock.Right);
            row.Children.Add(chip);
        }
        row.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = item.Done ? Brushes.Gray : Theme.Res("N.Fg"), TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
        });
        row.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        return row;
    }

    static string When(DateTime due) =>
        due.Date == DateTime.Today ? due.ToString("HH:mm")
        : due.Date == DateTime.Today.AddDays(1) ? "amanhã " + due.ToString("HH:mm")
        : due.ToString("dd/MM HH:mm");
}

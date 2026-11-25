using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Orbi;

public partial class CalendarPage : UserControl
{
    static readonly CultureInfo PtBr = new("pt-BR");
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime _today = DateTime.Today;
    TimeSpan _length = TimeSpan.FromMinutes(25), _left = TimeSpan.FromMinutes(25);
    DateTime? _endsAt; // preenchido só enquanto o timer corre

    /// <summary>O timer chegou a zero.</summary>
    public event Action? Finished;

    /// <summary>Tempo restante enquanto o timer corre (para o notch fechado mostrar); senão null.</summary>
    public string? RunningText => _endsAt == null ? null : Fmt(_left);

    public CalendarPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => TickTimer();
        Theme.Changed += DrawMonth;
        DrawMonth();
    }

    // ---------- calendário ----------

    void Prev_Click(object s, RoutedEventArgs e) { _month = _month.AddMonths(-1); DrawMonth(); }
    void Next_Click(object s, RoutedEventArgs e) { _month = _month.AddMonths(1); DrawMonth(); }

    /// <summary>Chamado a cada segundo pelo notch: vira o dia sozinho à meia-noite.</summary>
    public void Refresh()
    {
        if (DateTime.Today == _today) return;
        _today = DateTime.Today;
        DrawMonth();
    }

    void DrawMonth()
    {
        MonthText.Text = _month.ToString("MMMM yyyy", PtBr);
        Days.Children.Clear();
        foreach (var d in new[] { "D", "S", "T", "Q", "Q", "S", "S" })
            Days.Children.Add(new TextBlock { Text = d, FontSize = 10, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        // Começa no domingo da semana do dia 1; 6 semanas cobrem qualquer mês.
        var first = _month.AddDays(-(int)_month.DayOfWeek);
        for (int i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            bool today = day == _today, inMonth = day.Month == _month.Month;
            Days.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1),
                Background = today ? Theme.Res("N.Fg") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(),
                    FontSize = 10.5,
                    FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = today ? Theme.Res("N.Card") : inMonth ? Theme.Res("N.Fg") : Theme.Res("N.Track"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    // ---------- timer ----------

    void Preset_Checked(object s, RoutedEventArgs e)
    {
        _length = TimeSpan.FromMinutes(int.Parse((string)((RadioButton)s).Tag));
        if (IsLoaded) Reset();
    }

    void Start_Click(object s, RoutedEventArgs e)
    {
        if (_endsAt == null)
        {
            if (_left <= TimeSpan.Zero) _left = _length;
            _endsAt = DateTime.Now + _left;
            _tick.Start();
        }
        else
        {
            _left = _endsAt.Value - DateTime.Now;
            _endsAt = null;
            _tick.Stop();
        }
        Show();
    }

    void Reset_Click(object s, RoutedEventArgs e) => Reset();

    void Reset()
    {
        _tick.Stop();
        _endsAt = null;
        _left = _length;
        Show();
    }

    void TickTimer()
    {
        _left = _endsAt!.Value - DateTime.Now;
        if (_left <= TimeSpan.Zero)
        {
            _left = TimeSpan.Zero;
            _endsAt = null;
            _tick.Stop();
            Show();
            Finished?.Invoke();
            return;
        }
        Show();
    }

    void Show()
    {
        TimeText.Text = Fmt(_left);
        StartText.Text = _endsAt != null ? "Pausar" : _left < _length && _left > TimeSpan.Zero ? "Continuar" : "Iniciar";
        EndsText.Text = _endsAt != null ? $"termina às {_endsAt:HH:mm}" : _left <= TimeSpan.Zero ? "acabou!" : "timer";
    }

    // Arredonda para cima: com 24:59,4 restantes mostra 25:00, e só mostra 0:00 quando acabou de verdade.
    static string Fmt(TimeSpan t)
    {
        int secs = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        return $"{secs / 60}:{secs % 60:00}";
    }
}

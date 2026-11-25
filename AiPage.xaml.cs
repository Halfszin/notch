using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbi;

public partial class AiPage : UserControl
{
    readonly List<(bool User, string Text)> _history = new();
    CancellationTokenSource? _pending;
    long _todayTokens;
    int _todayQuestions;
    DateTime _countDay = DateTime.Today;

    AgentHub? _hub;
    public AgentHub? Hub
    {
        get => _hub;
        set
        {
            _hub = value;
            if (value != null) value.Changed += () => { if (AgentsPanel.Visibility == Visibility.Visible) DrawAgents(); };
        }
    }

    /// <summary>true enquanto espera a resposta (o avatar fica "pensando").</summary>
    public event Action<bool>? Thinking;

    public AiPage()
    {
        InitializeComponent();
        ShowWelcome();
    }

    void Mode_Checked(object s, RoutedEventArgs e)
    {
        if (UsagePanel == null) return; // o XAML marca "Chat" antes de criar os painéis
        bool usage = UsageMode.IsChecked == true, agents = AgentsMode.IsChecked == true;
        ChatPanel.Visibility = usage || agents ? Visibility.Collapsed : Visibility.Visible;
        UsagePanel.Visibility = usage ? Visibility.Visible : Visibility.Collapsed;
        AgentsPanel.Visibility = agents ? Visibility.Visible : Visibility.Collapsed;
        NewChat.Visibility = usage || agents ? Visibility.Collapsed : Visibility.Visible;
        if (usage) _ = LoadUsage();
        if (agents) DrawAgents();
    }

    // ---------- chat ----------

    void ShowWelcome()
    {
        Messages.Children.Clear();
        Bubble(ClaudeCli.Find() != null ? "Pergunte qualquer coisa. Respondo pela sua assinatura do Claude (Claude Code), sem custo extra, com busca na web."
            : Config.Current.ApiKey != null ? "Pergunte qualquer coisa. (Sem o Claude Code instalado, estou usando a sua chave da API, que é cobrada por uso.)"
            : "Para perguntar sem pagar nada a mais, instale o Claude Code: no PowerShell rode  irm https://claude.ai/install.ps1 | iex  e depois  claude  para entrar com a sua conta do Claude (Pro ou Max). Ele precisa do Git para Windows (git-scm.com).", false, muted: true);
    }

    public void FocusChat()
    {
        ChatMode.IsChecked = true;
        Dispatcher.BeginInvoke(() => AskInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void NewChat_Click(object s, MouseButtonEventArgs e)
    {
        _pending?.Cancel();
        _history.Clear();
        ShowWelcome();
    }

    void AskInput_TextChanged(object s, TextChangedEventArgs e) => AskHint.Visibility = AskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    async void AskInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await Send();
    }

    async void Send_Click(object s, RoutedEventArgs e) => await Send();

    async Task Send()
    {
        if (string.IsNullOrWhiteSpace(AskInput.Text) || _pending != null) return;
        string question = AskInput.Text.Trim();
        // A assinatura (Claude Code) vem primeiro: a chave da API é cobrada por uso e fica só de reserva.
        string? cli = ClaudeCli.Find();
        string? key = cli == null ? Config.Current.ApiKey : null;
        if (_history.Count == 0) Messages.Children.Clear();
        AskInput.Clear();
        Bubble(question, true);
        if (key == null && cli == null)
        {
            Bubble("Não achei o Claude Code neste PC. No PowerShell rode  irm https://claude.ai/install.ps1 | iex  e depois  claude  para entrar com a sua conta. Aí é só perguntar de novo aqui.", false, muted: true);
            return;
        }
        var waiting = Bubble("Pensando…", false, muted: true);
        _pending = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Thinking?.Invoke(true);
        try
        {
            // Só as últimas 3 perguntas e respostas vão junto: o chat é para coisas rápidas.
            var history = _history.TakeLast(6).ToList();
            // Com chave: API do Claude. Sem chave: a sua assinatura, pelo Claude Code instalado.
            var reply = cli != null
                ? new AiChat.Reply(await ClaudeCli.Ask(cli, ClaudeCli.Prompt(history, question), AiChat.Instructions, _pending.Token), 0, 0)
                : await AiChat.Ask(key!, history, question, _pending.Token);
            Messages.Children.Remove(waiting);
            Bubble(reply.Text, false);
            _history.Add((true, question));
            _history.Add((false, reply.Text));
            if (_countDay != DateTime.Today) { _countDay = DateTime.Today; _todayTokens = 0; _todayQuestions = 0; }
            _todayTokens += reply.InputTokens + reply.OutputTokens;
            _todayQuestions++;
        }
        catch (OperationCanceledException)
        {
            Messages.Children.Remove(waiting);
        }
        catch (Exception ex)
        {
            Messages.Children.Remove(waiting);
            Bubble(Friendly(ex), false, muted: true);
        }
        finally
        {
            _pending = null;
            Thinking?.Invoke(false);
        }
    }

    static string Friendly(Exception ex)
    {
        string m = ex.Message;
        if (m.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || m.Contains("/login"))
            return "O Claude Code ainda não está logado. Abra o PowerShell, rode  claude , digite  /login  e escolha a conta do Claude (assinatura). Depois é só perguntar de novo aqui.";
        if (m.Contains("401") || m.Contains("authentication", StringComparison.OrdinalIgnoreCase)) return "A chave da API não foi aceita. Confira em Configurações.";
        if (m.Contains("429") || m.Contains("rate", StringComparison.OrdinalIgnoreCase)) return "Muitas perguntas seguidas. Espere um pouco e tente de novo.";
        if (m.Contains("credit", StringComparison.OrdinalIgnoreCase) || m.Contains("billing", StringComparison.OrdinalIgnoreCase)) return "Sua conta da API está sem créditos.";
        return "Não deu para responder agora: " + m;
    }

    FrameworkElement Bubble(string text, bool user, bool muted = false)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
            FontSize = 13, Padding = new Thickness(0), SelectionBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
        };
        var bubble = new Border
        {
            Child = box, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(user ? 80 : 0, 3, user ? 4 : 60, 3),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        // Cores pelo tema (mudam na hora se você trocar entre escuro e claro).
        bubble.SetResourceReference(Border.BackgroundProperty, user ? "N.Button" : "N.Field");
        box.SetResourceReference(TextBox.ForegroundProperty, muted ? "N.Sub" : "N.Fg");
        Messages.Children.Add(bubble);
        ChatScroll.ScrollToEnd();
        return bubble;
    }

    // ---------- agentes ----------

    void DrawAgents()
    {
        AgentRows.Children.Clear();
        if (_hub == null) return;
        if (_hub.Plan is { } plan)
        {
            Header("Plano do Claude", AgentRows);
            if (plan.FiveHour is { } f) Row("Últimas 5 horas", $"{f.UsedPct:0}% · renova às {f.ResetsAt.ToLocalTime():HH:mm}", target: AgentRows);
            if (plan.SevenDay is { } w) Row("Semana", $"{w.UsedPct:0}% · renova {w.ResetsAt.ToLocalTime():ddd HH:mm}", target: AgentRows);
        }
        Header("Sessões do Claude Code", AgentRows);
        if (_hub.Sessions.Count == 0 && !HookInstaller.IsInstalled())
            Row("Ainda não conectado", "Configurações → Agentes", muted: true, target: AgentRows);
        else if (_hub.Sessions.Count == 0)
            Row("Nenhuma sessão agora", "abra o Claude Code no terminal", muted: true, target: AgentRows);
        foreach (var s in _hub.Sessions.Values.OrderByDescending(x => x.Updated))
            Row((s.Busy ? "● " : "○ ") + s.Project, s.Status, target: AgentRows);
    }

    // ---------- consumo ----------

    async Task LoadUsage()
    {
        UsageRows.Children.Clear();
        Row("Lendo os registros do Claude Code…", "", muted: true);
        var now = DateTime.Now;
        UsageLog.Summary? sum = null;
        string? error = null;
        await Task.Run(() =>
        {
            try { sum = UsageLog.Summarize(UsageLog.RecentFiles(now).SelectMany(ReadShared), now); }
            catch (Exception ex) { error = ex.Message; }
        });
        UsageRows.Children.Clear();
        Header("Claude Code neste PC");
        if (sum == null) Row("Não deu para ler os registros", error ?? "", muted: true);
        else if (sum.WeekMessages == 0) Row("Nenhum uso nos últimos 7 dias", "(sessões na nuvem não ficam neste PC)", muted: true);
        else
        {
            Row("Hoje", $"{Fmt(sum.Today.Total)} tokens · {sum.TodayMessages} respostas");
            Row("Últimos 7 dias", $"{Fmt(sum.Week.Total)} tokens · {sum.WeekMessages} respostas");
            Row("  entrada / saída", $"{Fmt(sum.Week.Input)} / {Fmt(sum.Week.Output)}");
            Row("  cache (lido / gravado)", $"{Fmt(sum.Week.CacheRead)} / {Fmt(sum.Week.CacheWrite)}");
            foreach (var (model, tokens) in sum.WeekByModel.OrderByDescending(kv => kv.Value).Take(3))
                Row("  " + model, Fmt(tokens));
        }
        Header("Chat do Orbi hoje");
        Row("Perguntas", _countDay == DateTime.Today ? $"{_todayQuestions} · {Fmt(_todayTokens)} tokens" : "0");
    }

    // O Claude Code pode estar escrevendo o arquivo agora: abre sem bloquear.
    static IEnumerable<string> ReadShared(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
        using var reader = new System.IO.StreamReader(fs);
        while (reader.ReadLine() is { } line) yield return line;
    }

    static string Fmt(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0} mi" : n >= 1_000 ? $"{n / 1_000.0:0.0} mil" : n.ToString();

    void Header(string text, Panel? target = null) => (target ?? (AgentsPanel.Visibility == Visibility.Visible ? AgentRows : UsageRows)).Children.Add(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 3) });

    void Row(string label, string value, bool muted = false, Panel? target = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var v = new TextBlock { Text = value, FontSize = 12, Foreground = Theme.Res("N.Sub"), Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = muted ? Brushes.Gray : Theme.Res("N.Fg"), TextTrimming = TextTrimming.CharacterEllipsis });
        (target ?? UsageRows).Children.Add(row);
    }
}

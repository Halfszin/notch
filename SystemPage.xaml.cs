using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Orbi;

// CPU, memória e bateria em anéis (como o anel da bateria do notch), atualizados a cada segundo.
public partial class SystemPage : UserControl
{
    const double Size = 74, Thick = 7;
    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Blue = Color.FromRgb(0x0A, 0x84, 0xFF), Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    readonly Gauge _cpu, _ram, _bat;
    readonly TextBlock _audio = new() { FontSize = 11.5, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    long _idle, _kernel, _user;

    sealed record Gauge(Path Arc, TextBlock Value, TextBlock Detail);

    public SystemPage()
    {
        InitializeComponent();
        var row = new UniformGrid { Columns = 3 };
        _cpu = AddGauge(row, "CPU");
        _ram = AddGauge(row, "Memória");
        _bat = AddGauge(row, "Bateria");
        Rows.Children.Add(row);
        var audio = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        audio.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 1, 6, 0) });
        audio.Children.Add(_audio);
        Rows.Children.Add(audio);
    }

    static Gauge AddGauge(Panel parent, string name)
    {
        var arc = new Path { StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var value = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Grid { Width = Size, Height = Size };
        var track = new Ellipse { StrokeThickness = Thick }; // o Windows desenha o contorno por dentro: centro do traço = raio do arco
        track.SetResourceReference(Shape.StrokeProperty, "N.Track");
        ring.Children.Add(track);
        ring.Children.Add(arc);
        ring.Children.Add(value);
        var detail = new TextBlock { FontSize = 10.5, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center };
        parent.Children.Add(new StackPanel
        {
            Children =
            {
                ring,
                new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
                detail,
            },
        });
        return new Gauge(arc, value, detail);
    }

    static void Set(Gauge g, double ratio, string value, string detail, Color color)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        g.Arc.Data = MainWindow.RingGeometry(ratio, Size / 2, Size / 2 - Thick / 2);
        g.Arc.Visibility = ratio < 0.01 ? Visibility.Hidden : Visibility.Visible; // 0%: sem a bolinha da ponta arredondada
        g.Arc.Stroke = new SolidColorBrush(color);
        g.Value.Text = value;
        g.Detail.Text = detail;
    }

    public void Update(string? audioDevice)
    {
        // CPU: fração do tempo não ocioso desde a leitura anterior.
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long total = (kernel - _kernel) + (user - _user); // o tempo de kernel já inclui o ocioso
            if (_kernel != 0 && total > 0)
            {
                double cpu = 1 - (double)(idle - _idle) / total;
                Set(_cpu, cpu, $"{cpu * 100:0}%", $"{Environment.ProcessorCount} núcleos", cpu > 0.85 ? Red : Green);
            }
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double used = mem.TotalPhys - mem.AvailPhys, gb = 1L << 30;
            Set(_ram, used / mem.TotalPhys, $"{mem.MemoryLoad}%", $"{used / gb:0.0} de {mem.TotalPhys / gb:0} GB", mem.MemoryLoad > 90 ? Red : Blue);
        }

        GetSystemPowerStatus(out var p);
        if (p.Percent > 100) Set(_bat, 0, "—", "sem bateria", Green);
        else
        {
            string detail = p.AC == 1 ? "carregando" : p.Life > 0 ? $"~{p.Life / 3600}h{p.Life % 3600 / 60:00} restantes" : "na bateria";
            Set(_bat, p.Percent / 100.0, $"{p.Percent}%", detail, p.Percent <= 20 && p.AC != 1 ? Red : Green);
        }

        _audio.Text = audioDevice ?? "Saída de áudio desconhecida";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AC, Flag, Percent, Saver; public int Life, FullLife; }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus m);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);
}

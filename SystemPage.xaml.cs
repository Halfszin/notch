using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Orbi;

// CPU, RAM, bateria e saída de áudio, atualizados a cada segundo pelo notch.
public partial class SystemPage : UserControl
{
    readonly (TextBlock Value, Border Fill, Border Track) _cpu, _ram, _bat;
    readonly TextBlock _audio = new() { FontSize = 12, Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis };
    long _idle, _kernel, _user;

    public SystemPage()
    {
        InitializeComponent();
        _cpu = Row("", "CPU");
        _ram = Row("", "Memória");
        _bat = Row("", "Bateria");
        var audioRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        audioRow.Children.Add(Glyph(""));
        audioRow.Children.Add(_audio);
        Rows.Children.Add(audioRow);
    }

    (TextBlock, Border, Border) Row(string glyph, string name)
    {
        var value = new TextBlock { FontSize = 12, Width = 120, TextAlignment = TextAlignment.Right, Foreground = Brushes.LightGray };
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E)), VerticalAlignment = VerticalAlignment.Center, Child = fill, Margin = new Thickness(10, 0, 10, 0) };
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        row.Children.Add(Glyph(glyph));
        var label = new TextBlock { Text = name, FontSize = 12, Width = 62, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(track);
        Rows.Children.Add(row);
        return (value, fill, track);
    }

    static TextBlock Glyph(string g)
    {
        var t = new TextBlock { Text = g, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Width = 24, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(t, Dock.Left);
        return t;
    }

    static void Set((TextBlock Value, Border Fill, Border Track) row, double ratio, string text, Color? color = null)
    {
        row.Value.Text = text;
        row.Fill.Width = Math.Max(0, row.Track.ActualWidth * Math.Clamp(ratio, 0, 1));
        if (color is Color c) row.Fill.Background = new SolidColorBrush(c);
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
                Set(_cpu, cpu, $"{cpu:P0}", cpu > 0.85 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
            }
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            double used = mem.TotalPhys - mem.AvailPhys;
            Set(_ram, used / mem.TotalPhys, $"{used / (1L << 30):0.0} de {mem.TotalPhys / (double)(1L << 30):0} GB",
                mem.MemoryLoad > 90 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x0A, 0x84, 0xFF));
        }

        GetSystemPowerStatus(out var p);
        if (p.Percent > 100) Set(_bat, 0, "sem bateria");
        else
        {
            string extra = p.AC == 1 ? " · carregando" : p.Life > 0 ? $" · ~{p.Life / 3600}h{p.Life % 3600 / 60:00}" : "";
            Set(_bat, p.Percent / 100.0, $"{p.Percent}%{extra}", p.Percent <= 20 && p.AC != 1 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
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

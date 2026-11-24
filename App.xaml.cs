using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Notch;

public partial class App : Application
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notch");
    public static MainWindow Notch = null!;
    static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(Dir);
        _single = new Mutex(true, "Notch.SingleInstance", out bool first);
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Notch.ShowSettings");
        if (!first)
        {
            // Já está rodando: abrir o app de novo só abre as configurações da instância que já existe.
            showSettings.Set();
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Notch = new MainWindow();
        Notch.Show();
        if (!e.Args.Contains("--startup")) OpenSettings();
        ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => Dispatcher.Invoke(OpenSettings), null, -1, false);
    }

    public static void OpenSettings()
    {
        var w = Current.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? new SettingsWindow();
        w.Show();
        w.WindowState = WindowState.Normal;
        w.Activate();
    }

    // ---------- iniciar com o Windows ----------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Notch") != null;
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Notch", $"\"{Environment.ProcessPath}\" --startup");
            else k.DeleteValue("Notch", false);
        }
    }
}

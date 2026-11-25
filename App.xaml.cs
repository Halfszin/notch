using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Orbi;

public partial class App : Application
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbi");
    public static MainWindow OrbiWindow = null!;
    static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(Dir);
        // Diagnóstico: "Orbi.exe --testar-ditado modelo.bin audio.wav" transcreve o áudio e grava em teste-ditado.txt.
        if (e.Args.Length == 3 && e.Args[0] == "--testar-ditado")
        {
            string result;
            try { result = Task.Run(() => TestDictation(e.Args[1], e.Args[2])).GetAwaiter().GetResult(); }
            catch (Exception ex) { result = "ERRO: " + ex; }
            File.WriteAllText(Path.Combine(Dir, "teste-ditado.txt"), result);
            Shutdown();
            return;
        }
        Theme.Apply(Config.Current.Theme);
        // Um erro inesperado vira aviso + linha em erros.log, em vez de fechar o app sem explicação.
        DispatcherUnhandledException += (_, ev) => { ev.Handled = true; Report(ev.Exception); };
        TaskScheduler.UnobservedTaskException += (_, ev) => { ev.SetObserved(); Log(ev.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log(ev.ExceptionObject);
        _single = new Mutex(true, "Orbi.SingleInstance", out bool first);
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "Orbi.ShowSettings");
        if (!first)
        {
            // Já está rodando: abrir o app de novo só abre as configurações da instância que já existe.
            showSettings.Set();
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        OrbiWindow = new MainWindow();
        OrbiWindow.Show();
        if (!e.Args.Contains("--startup")) OpenSettings();
        Updater.Start();
        ThreadPool.RegisterWaitForSingleObject(showSettings, (_, _) => Dispatcher.Invoke(OpenSettings), null, -1, false);
    }

    public static string ErrorLog => Path.Combine(Dir, "erros.log");
    static bool _reported;

    static void Log(object error)
    {
        try { File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\n\n"); } catch { }
    }

    static void Report(Exception ex)
    {
        Log(ex);
        if (_reported) return; // um aviso só, para não virar uma cascata de janelas
        _reported = true;
        MessageBox.Show($"O Orbi encontrou um erro e continuou aberto.\n\n{ex.Message}\n\nDetalhes em:\n{ErrorLog}",
            "Orbi", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static async Task<string> TestDictation(string model, string wav)
    {
        using var reader = new NAudio.Wave.WaveFileReader(wav);
        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
        var samples = new List<float>();
        var buffer = new float[16000];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.Take(n));
        using var transcriber = new Transcriber(model);
        return await transcriber.Transcribe(samples.ToArray(), "en", null);
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
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Orbi") != null;
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Orbi", $"\"{Environment.ProcessPath}\" --startup");
            else k.DeleteValue("Orbi", false);
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using NAudio.Wave;

namespace Orbi;

// Ditado: segure Ctrl direito, fale, solte → o texto é colado onde o cursor está, em qualquer programa.
// Outra tecla junto com o Ctrl direito (ex.: Ctrl+C) cancela, para não atrapalhar atalhos. Esc também cancela.
public sealed class Dictation : IDisposable
{
    const int VkRControl = 0xA3, VkEscape = 0x1B;
    readonly LowLevelKeyboardProc _proc; // guardado: o Windows chama de volta enquanto o gancho existir
    IntPtr _hook;
    WaveInEvent? _mic;
    readonly List<float> _samples = new();
    bool _holding, _cancelled;
    DateTime _startedAt;
    Transcriber? _transcriber;
    string? _transcriberModel;

    public event Action<bool>? Listening;      // true = gravando
    public event Action<bool>? Working;         // true = transcrevendo
    public event Action<string>? Done;          // texto colado
    public event Action<string>? Failed;        // mensagem para a pessoa

    public Dictation() => _proc = HookProc;

    public void Enable(bool on)
    {
        if (on && _hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
        else if (!on && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Cancel();
        }
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is 0x0100 or 0x0104, up = msg is 0x0101 or 0x0105;
            if (down && vk == VkRControl && !_holding) { _holding = true; _cancelled = false; Start(); }
            else if (down && _holding && vk != VkRControl) { _cancelled = true; Cancel(); } // atalho (Ctrl+C...) ou Esc
            else if (up && vk == VkRControl && _holding) { _holding = false; if (!_cancelled) _ = Finish(); }
        }
        return CallNextHookEx(_hook, code, wParam, lParam); // a tecla segue normal para os outros programas
    }

    void Start()
    {
        if (!VoiceModels.Has(Config.Current.VoiceModel)) { Failed?.Invoke("Baixe o modelo de voz em Configurações → Ditado."); _cancelled = true; return; }
        try
        {
            _samples.Clear();
            _mic = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            _mic.DataAvailable += (_, a) =>
            {
                lock (_samples)
                    for (int i = 0; i + 1 < a.BytesRecorded; i += 2)
                        _samples.Add(BitConverter.ToInt16(a.Buffer, i) / 32768f);
            };
            _mic.StartRecording();
            _startedAt = DateTime.Now;
            Listening?.Invoke(true);
        }
        catch (Exception ex)
        {
            _cancelled = true;
            StopMic();
            Failed?.Invoke("Não deu para usar o microfone: " + ex.Message + ". Veja em Configurações do Windows → Privacidade → Microfone.");
        }
    }

    void StopMic()
    {
        try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
        _mic = null;
    }

    void Cancel()
    {
        if (_mic == null) return;
        StopMic();
        Listening?.Invoke(false);
    }

    async Task Finish()
    {
        if (_mic == null) return;
        StopMic();
        Listening?.Invoke(false);
        var spoken = DateTime.Now - _startedAt;
        float[] audio;
        lock (_samples) audio = _samples.ToArray();
        if (spoken < TimeSpan.FromMilliseconds(400) || audio.Length < 16000 / 3) return; // toque rápido no Ctrl: não é ditado

        Working?.Invoke(true);
        try
        {
            var cfg = Config.Current;
            if (_transcriber == null || _transcriberModel != cfg.VoiceModel)
            {
                _transcriber?.Dispose();
                string model = VoiceModels.PathOf(cfg.VoiceModel);
                _transcriber = await Task.Run(() => new Transcriber(model));
                _transcriberModel = cfg.VoiceModel;
            }
            string prompt = string.Join(", ", cfg.DictWords.Split('\n').Select(w => w.Trim()).Where(w => w.Length > 0));
            string heard = await Task.Run(() => _transcriber.Transcribe(audio, "pt", prompt));
            string text = DictationText.Apply(heard, DictationText.ParsePairs(cfg.DictSnippets), DictationText.ParsePairs(cfg.DictReplacements));
            if (text.Length == 0) return;
            await Paste(text);
            DictationStats.Record(text, spoken);
            Done?.Invoke(text);
        }
        catch (Exception ex) { Failed?.Invoke("O ditado falhou: " + ex.Message); }
        finally { Working?.Invoke(false); }
    }

    // Cola pelo Ctrl+V e devolve à área de transferência o que estava lá antes.
    static async Task Paste(string text)
    {
        string? before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
        for (int i = 0; i < 4; i++)
        {
            try { Clipboard.SetText(text); break; }
            catch { await Task.Delay(60); } // área de transferência ocupada por outro programa
        }
        Shell.PressKeys(0x11, 0x56); // Ctrl+V
        await Task.Delay(400);
        if (before != null) try { Clipboard.SetText(before); } catch { }
    }

    public void Dispose()
    {
        Enable(false);
        _transcriber?.Dispose();
    }

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}

// Estatísticas do ditado, só no seu PC: palavras por dia, tempo poupado e os ditados de hoje.
public sealed class DictationStats
{
    static readonly string FilePath = Path.Combine(App.Dir, "ditado.json");
    public Dictionary<string, int> WordsByDay { get; set; } = new();
    public long TotalWords { get; set; }
    public double SavedSeconds { get; set; }
    public List<Entry> Recent { get; set; } = new();
    public sealed record Entry(DateTime When, string Text);

    public static DictationStats Load()
    {
        try { return JsonSerializer.Deserialize<DictationStats>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public static void Record(string text, TimeSpan spoken)
    {
        var s = Load();
        int words = DictationText.Words(text);
        string day = DateTime.Today.ToString("yyyy-MM-dd");
        s.WordsByDay[day] = s.WordsByDay.GetValueOrDefault(day) + words;
        s.TotalWords += words;
        s.SavedSeconds += DictationText.Saved(words, spoken).TotalSeconds;
        s.Recent.Insert(0, new Entry(DateTime.Now, text));
        if (s.Recent.Count > 30) s.Recent.RemoveRange(30, s.Recent.Count - 30);
        try
        {
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { } // estatística perdida não atrapalha o ditado
    }
}

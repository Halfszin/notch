using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Orbi;

// Igual ao Prumo: procura versão nova no GitHub ao abrir e a cada 4 h, baixa o instalador sozinho
// e deixa o botão "Reiniciar e atualizar" pronto.
public static class Updater
{
    const string LatestRelease = "https://api.github.com/repos/Halfszin/notch/releases/latest";
    static readonly HttpClient Http = new();
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromHours(4) };
    static bool _checking;

    public static string Current { get; } = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    public static string Status { get; private set; } = "";
    public static string? ReadyFile { get; private set; }
    public static string? ReadyVersion { get; private set; }
    public static event Action? Changed;

    public static void Start()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Orbi/{Current}");
        Timer.Tick += async (_, _) => await Check();
        Timer.Start();
        _ = Check();
    }

    public static async Task Check()
    {
        if (_checking || ReadyFile != null) return;
        _checking = true;
        try
        {
            Set("Procurando versão nova…");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestRelease));
            string tag = doc.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v');
            if (Version.Parse(tag) <= Version.Parse(Current))
            {
                Set($"Você está na versão mais nova ({Current}).");
                return;
            }
            string url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .First(a => a.GetProperty("name").GetString() == "OrbiSetup.exe")
                .GetProperty("browser_download_url").GetString()!;
            string file = Path.Combine(Path.GetTempPath(), $"OrbiSetup-{tag}.exe");
            if (!File.Exists(file))
            {
                Set($"Baixando a versão {tag}…");
                // Baixa em .part e só renomeia no fim: um download interrompido nunca vira instalador "pronto".
                await using (var src = await Http.GetStreamAsync(url))
                await using (var dst = File.Create(file + ".part"))
                    await src.CopyToAsync(dst);
                File.Move(file + ".part", file, true);
            }
            ReadyFile = file;
            ReadyVersion = tag;
            Set($"Versão {tag} baixada e pronta para instalar.");
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            Set("Não deu para procurar: o repositório do Orbi no GitHub está privado (ou ainda não tem versão publicada).");
        }
        catch (Exception e)
        {
            Set("Não deu para procurar versão nova: " + e.Message);
        }
        finally { _checking = false; }
    }

    public static void Install()
    {
        if (ReadyFile == null) return;
        // O instalador fecha o Orbi, instala em silêncio e abre a versão nova (ver [Run] no installer.iss).
        Process.Start(ReadyFile, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
        Application.Current.Shutdown();
    }

    static void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}

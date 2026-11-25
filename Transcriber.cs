using System.IO;
using System.Net.Http;
using System.Text;
using Whisper.net;

namespace Orbi;

// Reconhecimento de voz no próprio PC (Whisper), sem mandar áudio para lugar nenhum.
public sealed class Transcriber : IDisposable
{
    readonly WhisperFactory _factory;

    public Transcriber(string modelPath) => _factory = WhisperFactory.FromPath(modelPath);

    /// <summary>Áudio mono 16 kHz (−1 a 1) → texto. "prompt" ajuda a escrever certo nomes do seu dicionário.</summary>
    public async Task<string> Transcribe(float[] samples, string language, string? prompt, CancellationToken ct = default)
    {
        var builder = _factory.CreateBuilder().WithLanguage(language).WithNoContext();
        if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
        await using var processor = builder.Build();
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct)) sb.Append(segment.Text);
        return sb.ToString().Trim();
    }

    public void Dispose() => _factory.Dispose();
}

// Os modelos de voz ficam em %LocalAppData%\Orbi\modelos e são baixados uma vez, quando você pede.
public static class VoiceModels
{
    public static readonly (string Id, string Name, long ApproxMb)[] All =
    {
        ("base", "Rápido", 148),
        ("small", "Preciso", 488),
    };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbi", "modelos");
    public static string PathOf(string id) => Path.Combine(Dir, $"ggml-{id}.bin");
    public static bool Has(string id) => File.Exists(PathOf(id));

    public static async Task Download(string id, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{id}.bin";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        string part = PathOf(id) + ".part";
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
            // Download cortado no meio não pode virar "modelo pronto".
            if (total > 0 && done != total) throw new IOException("O download do modelo veio incompleto. Tente de novo.");
        }
        File.Move(part, PathOf(id), true);
    }
}

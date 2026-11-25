// Teste do BeatDetector: dotnet run --project tests
using Orbi;

var rnd = new Random(1);
double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
int Run(Func<int, double> signal, int frames = 600, int skip = 60)
{
    var d = new BeatDetector();
    int beats = 0;
    for (int i = 0; i < frames; i++)
    {
        d.Process(Math.Clamp(signal(i), 0, 1), i * 16L);
        if (i >= skip && d.LastWasBeat) beats++;
    }
    return beats;
}
bool Kick(int i) => i % 30 < 2; // ~120 BPM a 60 leituras/s; 18 bumbos depois do aquecimento

var r = new Dictionary<string, int>
{
    // Música alta com bumbo bem marcado.
    ["bumbo"] = Run(i => (Kick(i) ? 0.95 : 0.6) + Gauss() * 0.03),
    // O caso real do Spotify: comprimida, sempre perto do máximo, bumbo sobe pouco.
    ["comprimida"] = Run(i => (Kick(i) ? 0.97 : 0.86) + Gauss() * 0.02),
    // Baixinha.
    ["baixinha"] = Run(i => (Kick(i) ? 0.3 : 0.08) + Gauss() * 0.01),
    // Volume constante só com chiado: quase nada.
    ["reta"] = Run(i => 0.6 + Gauss() * 0.03),
    // Subida lenta (fade-in): não é batida.
    ["fade"] = Run(i => i / 600.0 + Gauss() * 0.005),
    // Degrau: a música fica bem mais alta de repente; a régua tem que acompanhar e não disparar sem parar.
    ["degrau"] = Run(i => (i < 300 ? 0.3 : 0.8) + Gauss() * 0.02),
};
// ---- Áudio de verdade (como no PC): FFT nos graves + detector, lendo a cada 16 ms ----
int RunAudio(Func<double, double> wave, double seconds = 10)
{
    const int rate = 48000;
    var spec = new SpectrumAnalyzer(rate);
    var det = new BeatDetector();
    var chunk = new float[rate / 1000]; // 1 ms por vez
    int beats = 0;
    for (int ms = 0; ms < seconds * 1000; ms++)
    {
        for (int i = 0; i < chunk.Length; i++) chunk[i] = (float)wave((ms * chunk.Length + i) / (double)rate);
        spec.Add(chunk);
        if (ms % 16 == 0)
        {
            det.Process(spec.ReadBass(), ms);
            if (ms >= 1000 && det.LastWasBeat) beats++;
        }
    }
    return beats;
}
double KickWave(double t, double period = 0.5) // bumbo: 55 Hz com decaimento rápido
{
    double u = t % period;
    return Math.Sin(2 * Math.PI * 55 * u) * Math.Exp(-u / 0.08);
}
double Noise() => (rnd.NextDouble() - 0.5);
// Música com bumbo a 120 BPM, voz/sintetizador (1 kHz) e chiado: 9 s depois do aquecimento = ~18 bumbos.
r["audio-bumbo"] = RunAudio(t => 0.6 * KickWave(t) + 0.3 * Math.Sin(2 * Math.PI * 1000 * t) + 0.05 * Noise());
// Comprimida: baixo contínuo forte (80 Hz) por baixo do bumbo; o bumbo ainda tem que aparecer.
r["audio-comprimida"] = RunAudio(t => 0.5 * KickWave(t) + 0.35 * Math.Sin(2 * Math.PI * 80 * t) + 0.3 * Math.Sin(2 * Math.PI * 1000 * t) + 0.05 * Noise());
// Só chimbal (agudo) batendo e graves parados: não é batida de bumbo.
r["audio-chimbal"] = RunAudio(t => 0.3 * Math.Sin(2 * Math.PI * 70 * t) + (t % 0.25 < 0.03 ? 0.5 * Noise() : 0.02 * Noise()));

Console.WriteLine(string.Join(" ", r.Select(kv => $"{kv.Key}={kv.Value}")));
bool ok = r["bumbo"] is >= 16 and <= 20 && r["comprimida"] is >= 16 and <= 20 && r["baixinha"] is >= 16 and <= 20
          && r["reta"] <= 3 && r["fade"] <= 3 && r["degrau"] <= 4
          && r["audio-bumbo"] is >= 16 and <= 19 && r["audio-comprimida"] is >= 16 and <= 19 && r["audio-chimbal"] <= 3;

// ---- Tarefas com hora ----
var now = new DateTime(2026, 11, 24, 14, 0, 0);
var failures = new List<string>();
void Check(string input, string title, DateTime? due)
{
    var (t, d) = TaskParser.Parse(input, now);
    if (t != title || d != due) failures.Add($"'{input}' -> '{t}' {d:dd/MM HH:mm} (esperado '{title}' {due:dd/MM HH:mm})");
}
Check("Ligar pro João às 15:30", "Ligar pro João", now.Date.AddHours(15.5));
Check("reunião 9h", "reunião", now.Date.AddDays(1).AddHours(9));            // 9h já passou hoje -> amanhã
Check("reunião amanhã 9h30", "reunião", now.Date.AddDays(1).AddHours(9.5));
Check("academia 18h", "academia", now.Date.AddHours(18));
Check("tirar o bolo em 20 min", "tirar o bolo", now.AddMinutes(20));
Check("pagar boleto amanhã", "pagar boleto", now.Date.AddDays(1).AddHours(9));
Check("comprar pão", "comprar pão", null);
Check("ver episódio 12", "ver episódio 12", null);                      // número solto não é hora
Check("código 99:99", "código 99:99", null);
// ---- Texto das notificações ----
var (nt, nb) = ToastText.Parse("<toast><visual><binding template='ToastGeneric'><text>Maria</text><text>Chego em 10 min</text></binding></visual></toast>");
if (nt != "Maria" || nb != "Chego em 10 min") failures.Add($"toast: '{nt}' / '{nb}'");
if (ToastText.Parse("<isso não é xml").Title != "") failures.Add("toast inválido");
// ---- Consumo do Claude Code ----
string L(string ts, string id, int inp, int outp, string model = "claude-opus-5-5") =>
    $"{{\"type\":\"assistant\",\"timestamp\":\"{ts}\",\"requestId\":\"r{id}\",\"message\":{{\"id\":\"m{id}\",\"model\":\"{model}\",\"usage\":{{\"input_tokens\":{inp},\"output_tokens\":{outp},\"cache_read_input_tokens\":100}}}}}}";
var todayUtc = now.ToUniversalTime();
var lines = new[]
{
    L(todayUtc.ToString("o"), "1", 10, 20),
    L(todayUtc.ToString("o"), "1", 10, 20),                         // mesma resposta repetida: conta uma vez
    L(todayUtc.AddDays(-3).ToString("o"), "2", 5, 5, "claude-sonnet-5-5"),
    L(todayUtc.AddDays(-30).ToString("o"), "3", 999, 999),          // fora da semana
    "{\"type\":\"user\",\"message\":{\"role\":\"user\"}}",
    "{\"cortada\": \"usage\"",                                  // linha pela metade
};
var sum = UsageLog.Summarize(lines, now);
if (sum.TodayMessages != 1 || sum.Today.Total != 130 || sum.WeekMessages != 2 || sum.Week.Total != 240 || sum.WeekByModel.Count != 2)
    failures.Add($"consumo: hoje {sum.TodayMessages}/{sum.Today.Total} semana {sum.WeekMessages}/{sum.Week.Total} modelos {sum.WeekByModel.Count}");

// ---- Hooks do Claude Code (mexe no settings.json da pessoa: tem que preservar tudo) ----
var original = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
    "{\"model\":\"opus\",\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":\"meu-status.sh\"}," +
    "\"hooks\":{\"PreToolUse\":[{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"outro-app.exe\"}]}]}}")!;
var once = HookInstaller.Merge(original, 47821, "tok", out bool sl1);
var twice = HookInstaller.Merge(once, 47821, "tok", out _);
var pre = twice["hooks"]!["PreToolUse"]!.AsArray();
if (pre.Count != 2) failures.Add($"hooks: PreToolUse com {pre.Count} entradas (esperado: a sua + a do Orbi, sem duplicar)");
if (twice["model"]!.GetValue<string>() != "opus" || twice["theme"]!.GetValue<string>() != "dark") failures.Add("hooks: perdeu configurações da pessoa");
if (sl1 || !twice["statusLine"]!.ToJsonString().Contains("meu-status.sh")) failures.Add("hooks: trocou a status line que já existia");
if (twice["hooks"]!.AsObject().Count != HookInstaller.Events.Length) failures.Add("hooks: faltou evento");
var removed = HookInstaller.Unmerge(twice);
if (removed.ToJsonString() != original.ToJsonString()) failures.Add("hooks: desligar não voltou ao original: " + removed.ToJsonString());
var fresh = HookInstaller.Merge(null, 1, "t", out bool sl2);
if (!sl2 || HookInstaller.Unmerge(fresh).ToJsonString() != "{}") failures.Add("hooks: instalar/desinstalar num arquivo novo");
try { HookInstaller.Merge((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("{\"hooks\":[1]}")!, 1, "t", out _); failures.Add("hooks: aceitou formato estranho"); }
catch (InvalidDataException) { }
// ---- Agentes ----
if (AgentHub.Describe("Edit", System.Text.Json.Nodes.JsonNode.Parse("{\"file_path\":\"C:/proj/src/app.ts\"}")) != "Editando app.ts") failures.Add("descrição Edit");
if (!AgentHub.Describe("Bash", System.Text.Json.Nodes.JsonNode.Parse("{\"command\":\"npm test\"}")).Contains("npm test")) failures.Add("descrição Bash");
var allow = AgentHub.PermissionResponse("always", System.Text.Json.Nodes.JsonNode.Parse("[{\"type\":\"addRules\"}]"));
if (!allow.Contains("\"behavior\":\"allow\"") || !allow.Contains("updatedPermissions") || !allow.Contains("PermissionRequest")) failures.Add("resposta sempre: " + allow);
if (!AgentHub.PermissionResponse("deny", null).Contains("\"behavior\":\"deny\"")) failures.Add("resposta negar");
long soon = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
var plan = AgentHub.ParsePlan((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse($"{{\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":23.5,\"resets_at\":{soon}}},\"seven_day\":{{\"used_percentage\":250,\"resets_at\":{soon}}}}}}}")!);
if (plan?.FiveHour?.UsedPct != 23.5 || plan.SevenDay != null) failures.Add("plano: leitura dos limites");
if (AgentHub.ParsePlan((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse($"{{\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":5,\"resets_at\":{soon * 1000}}}}}}}")!) != null) failures.Add("plano: aceitou milissegundos");

// ---- Receptor dos hooks, com o mesmo curl que o Claude Code roda ----
var hub = new AgentHub();
hub.PermissionAsked += perm => AgentHub.Decide(perm, "allow"); // como se você clicasse Permitir
var server = AgentServer.Start(47900, "segredo123", hub.Handle)!;
string Curl(string url, string body)
{
    // O mesmo curl dos hooks; chamado direto porque no Windows do CI o "bash" não é o Git Bash.
    var psi = new System.Diagnostics.ProcessStartInfo("curl", new[] { "-s", "-m", "10", "--data-binary", "@-", url })
        { RedirectStandardInput = true, RedirectStandardOutput = true };
    using var proc = System.Diagnostics.Process.Start(psi)!;
    proc.StandardInput.Write(body);
    proc.StandardInput.Close();
    string output = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    return output;
}
string baseUrl = $"http://127.0.0.1:{server.Port}/segredo123";
Curl($"{baseUrl}/PreToolUse", "{\"session_id\":\"s1\",\"cwd\":\"C:/projetos/loja\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:/projetos/loja/app.ts\"}}");
if (!hub.Sessions.TryGetValue("s1", out var sess) || sess.Project != "loja" || sess.Status != "Editando app.ts" || !sess.Busy) failures.Add("servidor: evento PreToolUse");
string decision = Curl($"{baseUrl}/PermissionRequest", "{\"session_id\":\"s1\",\"cwd\":\"C:/projetos/loja\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"npm test\"}}");
if (!decision.Contains("\"behavior\":\"allow\"")) failures.Add("servidor: permissão não voltou: " + decision);
if (Curl($"http://127.0.0.1:{server.Port}/errado/Stop", "{}") != "") failures.Add("servidor: aceitou código errado");
string big = "{\"session_id\":\"s2\",\"cwd\":\"/x/y\",\"tool_name\":\"Write\",\"tool_input\":{\"file_path\":\"a.txt\",\"content\":\"" + new string('x', 200_000) + "\"}}";
Curl($"{baseUrl}/PreToolUse", big);
if (!hub.Sessions.ContainsKey("s2")) failures.Add("servidor: corpo grande (Expect: 100-continue)");
long nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
string status = Curl($"{baseUrl}/statusline", $"{{\"model\":{{\"display_name\":\"Opus\"}},\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":42,\"resets_at\":{nowS}}}}}}}");
if (status != "Opus · 5h 42%") failures.Add("servidor: status line: " + status);
Curl($"{baseUrl}/Stop", "{\"session_id\":\"s1\",\"cwd\":\"C:/projetos/loja\"}");
if (hub.Sessions["s1"].Busy || hub.Sessions["s1"].Status != "Terminou") failures.Add("servidor: Stop");

// ---- Última mensagem do Claude (aviso de "terminou") ----
string transcript = Path.GetTempFileName();
File.WriteAllLines(transcript, new[]
{
    "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"rode os testes\"}}",
    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Rodando agora.\"}]}}",
    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Migração aplicada, 14 testes passaram.\"}]}}",
    "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"te",   // linha pela metade no fim
});
if (AgentHub.LastAssistantText(transcript) != "Migração aplicada, 14 testes passaram.") failures.Add("última mensagem: " + AgentHub.LastAssistantText(transcript));
if (AgentHub.LastAssistantText("/nao/existe.jsonl") != null) failures.Add("última mensagem: arquivo que não existe");
File.Delete(transcript);

// ---- Ditado: atalhos de texto, trocas, estatísticas ----
var snippets = DictationText.ParsePairs("meu email => rafa@exemplo.com\n# comentário\nassinatura => Abraços,\\nRafael\nlinha sem seta");
var swaps = DictationText.ParsePairs("cloud code => Claude Code\norbe => Orbi");
if (snippets.Count != 2) failures.Add($"ditado: {snippets.Count} atalhos lidos");
if (DictationText.Apply(" Meu e-mail. ", snippets, swaps) != "rafa@exemplo.com") failures.Add("ditado: atalho com hífen (o reconhecimento escreve e-mail)");
if (DictationText.Apply("Meu email.", snippets, swaps) != "rafa@exemplo.com") failures.Add("ditado: atalho de texto");
if (DictationText.Apply("Assinatura!", snippets, swaps) != "Abraços,\nRafael") failures.Add("ditado: atalho com quebra de linha");
if (DictationText.Apply("abre o cloud code e o Orbe", snippets, swaps) != "abre o Claude Code e o Orbi") failures.Add("ditado: trocas: " + DictationText.Apply("abre o cloud code e o Orbe", snippets, swaps));
if (DictationText.Apply("orbes", snippets, swaps) != "orbes") failures.Add("ditado: troca pegou pedaço de palavra");
if (DictationText.Words("Olá, mundo! Tudo bem? 123") != 5) failures.Add("ditado: contagem de palavras");
if (DictationText.Saved(80, TimeSpan.FromSeconds(30)) != TimeSpan.FromSeconds(90)) failures.Add("ditado: tempo poupado");
var d0 = new DateTime(2026, 11, 25);
if (DictationText.Streak(new[] { d0, d0.AddDays(-1), d0.AddDays(-2), d0.AddDays(-4) }, d0) != 3) failures.Add("ditado: dias seguidos");
if (DictationText.Streak(new[] { d0.AddDays(-1), d0.AddDays(-2) }, d0) != 2) failures.Add("ditado: dias seguidos sem hoje");

// ---- Reconhecimento de voz (só quando o CI fornece modelo e áudio: ORBI_WHISPER_MODEL e ORBI_WHISPER_WAV) ----
if (Environment.GetEnvironmentVariable("ORBI_WHISPER_MODEL") is { Length: > 0 } model && Environment.GetEnvironmentVariable("ORBI_WHISPER_WAV") is { Length: > 0 } wavPath)
{
    using var reader = new NAudio.Wave.WaveFileReader(wavPath);
    var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
    var samples = new List<float>();
    var buf = new float[reader.WaveFormat.SampleRate];
    int read;
    while ((read = provider.Read(buf, 0, buf.Length)) > 0) samples.AddRange(buf.Take(read));
    using var tr = new Transcriber(model);
    string heard = await tr.Transcribe(samples.ToArray(), "en", null);
    Console.WriteLine("transcrito: " + heard);
    var lower = heard.ToLowerInvariant();
    if (!(lower.Contains("weather") && lower.Contains("today"))) failures.Add("voz: transcrição inesperada: " + heard);
}

foreach (var f in failures) Console.WriteLine("FALHA " + f);
ok &= failures.Count == 0;
Console.WriteLine(ok ? "OK" : "FALHOU");
return ok ? 0 : 1;

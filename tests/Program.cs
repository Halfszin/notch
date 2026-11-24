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
Console.WriteLine(ok ? "OK" : "FALHOU");
return ok ? 0 : 1;

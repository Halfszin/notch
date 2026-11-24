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
Console.WriteLine(string.Join(" ", r.Select(kv => $"{kv.Key}={kv.Value}")));
bool ok = r["bumbo"] is >= 16 and <= 20 && r["comprimida"] is >= 16 and <= 20 && r["baixinha"] is >= 16 and <= 20
          && r["reta"] <= 3 && r["fade"] <= 3 && r["degrau"] <= 4;
Console.WriteLine(ok ? "OK" : "FALHOU");
return ok ? 0 : 1;

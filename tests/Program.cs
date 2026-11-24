// Teste do BeatDetector: dotnet run --project tests
using Orbi;

var rnd = new Random(1);
int Run(Func<int, double> signal, int frames, int skip = 60)
{
    var d = new BeatDetector();
    int beats = 0;
    for (int i = 0; i < frames; i++)
    {
        d.Process(signal(i), i * 16L);
        if (i >= skip && d.LastWasBeat) beats++;
    }
    return beats;
}
double Noise() => (rnd.NextDouble() - 0.5) * 0.1;

// Música alta (0,6) com bumbo a cada 30 quadros (~120 BPM) por 10 s: ~20 batidas depois do aquecimento.
int kicks = Run(i => (i % 30 < 2 ? 0.95 : 0.6) + Noise(), 600);
// Mesmo volume, sem bumbo: quase nenhuma.
int flat = Run(i => 0.6 + Noise(), 600);
// Música baixinha (0,08) com bumbo fraco (0,3): também precisa achar.
int quiet = Run(i => (i % 30 < 2 ? 0.3 : 0.08) + Noise() * 0.3, 600);
// Subida lenta de volume (fade-in): não é batida.
int fade = Run(i => i / 600.0 + Noise() * 0.3, 600);

Console.WriteLine($"bumbo={kicks} reto={flat} baixinho={quiet} fade={fade}");
bool ok = kicks is >= 17 and <= 20 && flat <= 2 && quiet is >= 17 and <= 20 && fade <= 2;
Console.WriteLine(ok ? "OK" : "FALHOU");
return ok ? 0 : 1;

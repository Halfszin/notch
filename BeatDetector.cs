namespace Orbi;

// Acha as batidas no nível de pico do som (lido ~60x por segundo). Música vem comprimida: fica sempre
// alta e o bumbo sobe só um pouco acima do resto. Por isso a régua é a própria música: uma batida é um
// pico bem acima da média recente, medido em desvios-padrão dessa música (não num valor fixo).
public sealed class BeatDetector
{
    double _avg, _var, _pulse;
    int _samples;
    long _lastBeatMs = long.MinValue / 2;

    public bool LastWasBeat { get; private set; }

    /// <summary>Recebe o pico atual (0–1) e devolve um pulso 0–1: salta na batida e cai rápido.</summary>
    public double Process(double peak, long nowMs)
    {
        if (_samples++ == 0) _avg = peak; // começa já no nível da música, não no zero
        double dev = peak - _avg;
        double std = Math.Max(Math.Sqrt(_var), 0.008);
        double score = dev / std;
        // Picos fora da curva quase não entram na média: senão o próprio bumbo levanta a régua e some.
        // (Entram um pouco, para a régua acompanhar quando a música inteira fica mais alta.)
        // No primeiro meio segundo aprende rápido o "normal" desta música, sem marcar batidas.
        bool warmingUp = _samples < 30;
        double rate = warmingUp ? 0.15 : score > 3.0 ? 0.006 : 0.04;
        _avg += dev * rate;
        _var += (dev * dev - _var) * rate;
        LastWasBeat = !warmingUp && score > 3.4 && dev > 0.02 && nowMs - _lastBeatMs > 160; // no máximo ~6 batidas/s
        if (LastWasBeat)
        {
            _pulse = Math.Min(1, 0.6 + 0.1 * (score - 3.4));
            _lastBeatMs = nowMs;
        }
        else _pulse *= 0.82; // some em ~150 ms
        return _pulse;
    }
}

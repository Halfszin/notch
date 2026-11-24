namespace Orbi;

// Acha as batidas no nível de pico do som (lido ~60x por segundo). Música tocando fica quase sempre
// alta, então seguir o volume mal mexe; uma batida é um pico bem acima da média recente.
public sealed class BeatDetector
{
    double _avg, _pulse;
    long _lastBeatMs = long.MinValue / 2;

    public bool LastWasBeat { get; private set; }

    /// <summary>Recebe o pico atual (0–1) e devolve um pulso 0–1: salta na batida e cai rápido.</summary>
    public double Process(double peak, long nowMs)
    {
        double rise = peak - _avg;
        _avg += (peak - _avg) * 0.05; // média de ~0,3 s
        LastWasBeat = rise > 0.05 + _avg * 0.22 && nowMs - _lastBeatMs > 140; // no máximo ~7 batidas/s
        if (LastWasBeat)
        {
            _pulse = Math.Min(1, 0.6 + rise / Math.Max(_avg, 0.08));
            _lastBeatMs = nowMs;
        }
        else _pulse *= 0.82; // some em ~150 ms
        return _pulse;
    }
}

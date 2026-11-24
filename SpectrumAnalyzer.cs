using NAudio.Dsp;

namespace Orbi;

// Como o Wallpaper Engine: recebe o som que sai do PC, faz FFT e separa em faixas (graves → agudos).
// A batida sai dos graves (bumbo), que pulam muito mais que o volume geral da música.
public sealed class SpectrumAnalyzer
{
    const int Size = 1024, Log2Size = 10;     // janela de ~21 ms a 48 kHz, analisada a cada meia janela
    static readonly double[] BandHz = { 40, 150, 400, 1500, 5000, 16000 };
    public const int BandCount = 5;           // faixa 0 = graves (40–150 Hz)

    readonly float[] _ring = new float[Size];
    readonly Complex[] _fft = new Complex[Size];
    readonly int[] _bin = new int[BandCount + 1];
    readonly double[] _max = new double[BandCount];
    readonly double[] _bands = new double[BandCount];
    readonly object _lock = new();
    int _pos, _sinceFft;
    double _bassPeak, _bassLast;
    bool _fresh;

    public SpectrumAnalyzer(int sampleRate)
    {
        for (int i = 0; i <= BandCount; i++)
            _bin[i] = Math.Clamp((int)Math.Round(BandHz[i] * Size / sampleRate), 1, Size / 2);
    }

    /// <summary>Amostras mono (−1 a 1), na ordem em que chegam. Chamado pela thread de captura.</summary>
    public void Add(ReadOnlySpan<float> mono)
    {
        foreach (float s in mono)
        {
            _ring[_pos] = s;
            _pos = (_pos + 1) % Size;
            if (++_sinceFft >= Size / 2) { _sinceFft = 0; Analyze(); }
        }
    }

    void Analyze()
    {
        for (int i = 0; i < Size; i++)
        {
            _fft[i].X = (float)(_ring[(_pos + i) % Size] * FastFourierTransform.HannWindow(i, Size));
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, Log2Size, _fft);
        lock (_lock)
        {
            for (int b = 0; b < BandCount; b++)
            {
                double e = 0;
                for (int k = _bin[b]; k < Math.Max(_bin[b + 1], _bin[b] + 1); k++)
                    e += Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                // Ganho automático por faixa: o mais alto dos últimos segundos vale 1.
                _max[b] = Math.Max(e, _max[b] * 0.999);
                _bands[b] = e / Math.Max(_max[b], 1e-9);
            }
            _bassPeak = Math.Max(_bassPeak, _bands[0]);
            _fresh = true;
        }
    }

    /// <summary>Maior nível de graves (0–1) desde a última leitura; se não chegou som novo, repete o último.</summary>
    public double ReadBass()
    {
        lock (_lock)
        {
            if (_fresh) { _bassLast = _bassPeak; _bassPeak = 0; _fresh = false; }
            return _bassLast;
        }
    }

    public void ReadBands(double[] into)
    {
        lock (_lock) Array.Copy(_bands, into, BandCount);
    }
}

// Visualizador: FFT do som que está saindo e a força de 5 faixas (graves → agudos), como no Wallpaper Engine.
// Mesmas faixas, ganho e detector de batida do Orbi em C# (SpectrumAnalyzer.cs e BeatDetector.cs).

/// Amostras por análise (~21 ms a 48 kHz).
pub const N: usize = 1024;

/// FFT radix-2 no lugar (tamanho potência de 2).
fn fft(re: &mut [f32], im: &mut [f32]) {
    let n = re.len();
    let mut j = 0;
    for i in 1..n {
        let mut bit = n >> 1;
        while j & bit != 0 { j ^= bit; bit >>= 1; }
        j |= bit;
        if i < j { re.swap(i, j); im.swap(i, j); }
    }
    let mut len = 2;
    while len <= n {
        let ang = -2.0 * std::f32::consts::PI / len as f32;
        for start in (0..n).step_by(len) {
            for k in 0..len / 2 {
                let (s, c) = (ang * k as f32).sin_cos();
                let (a, b) = (start + k, start + k + len / 2);
                let (tr, ti) = (re[b] * c - im[b] * s, re[b] * s + im[b] * c);
                re[b] = re[a] - tr;
                im[b] = im[a] - ti;
                re[a] += tr;
                im[a] += ti;
            }
        }
        len <<= 1;
    }
}

pub const BANDS: usize = 5;
/// Limites das faixas em Hz (faixa 0 = graves, onde está o bumbo).
const EDGES: [f32; BANDS + 1] = [40.0, 150.0, 400.0, 1500.0, 5000.0, 16000.0];

/// Amplitude média de cada faixa (sem normalizar).
pub fn bands(samples: &[f32], rate: f32) -> [f32; BANDS] {
    let n = samples.len();
    let mut re: Vec<f32> = samples.iter().enumerate()
        .map(|(i, &x)| x * (0.5 - 0.5 * (2.0 * std::f32::consts::PI * i as f32 / (n - 1) as f32).cos())) // janela de Hann
        .collect();
    let mut im = vec![0.0; n];
    fft(&mut re, &mut im);
    let hz = rate / n as f32;
    let mut out = [0.0; BANDS];
    for (b, o) in out.iter_mut().enumerate() {
        let lo = ((EDGES[b] / hz).ceil() as usize).max(1);
        let hi = ((EDGES[b + 1] / hz) as usize).min(n / 2);
        if hi >= lo {
            let sum: f32 = (lo..=hi).map(|k| (re[k] * re[k] + im[k] * im[k]).sqrt()).sum();
            *o = sum / (hi - lo + 1) as f32 * 2.0 / n as f32;
        }
    }
    out
}

/// Ganho automático por faixa: o mais alto dos últimos segundos vale 1.
pub struct Normalizer { peak: [f32; BANDS] }

impl Normalizer {
    pub fn new() -> Self { Normalizer { peak: [0.0; BANDS] } }

    pub fn apply(&mut self, raw: [f32; BANDS]) -> [f32; BANDS] {
        let mut out = [0.0; BANDS];
        for i in 0..BANDS {
            self.peak[i] = (self.peak[i] * 0.999).max(raw[i]);
            // Piso: silêncio e chiado ficam em zero em vez de virar barra cheia.
            out[i] = if self.peak[i] < 1e-4 { 0.0 } else { raw[i] / self.peak[i] };
        }
        out
    }
}

/// Acha as batidas nos graves: um pico bem acima da média recente, medido em desvios-padrão da própria
/// música (música vem comprimida, então um valor fixo não serve). Chamado ~60 vezes por segundo.
pub struct BeatDetector { avg: f32, var: f32, pulse: f32, samples: u32, last_beat_ms: i64 }

impl BeatDetector {
    pub fn new() -> Self { BeatDetector { avg: 0.0, var: 0.0, pulse: 0.0, samples: 0, last_beat_ms: i64::MIN / 2 } }

    /// Recebe o grave atual (0 a 1) e devolve o pulso (0 a 1): salta na batida e cai em ~150 ms.
    pub fn process(&mut self, peak: f32, now_ms: i64) -> f32 {
        if self.samples == 0 { self.avg = peak; } // começa já no nível da música
        self.samples += 1;
        let dev = peak - self.avg;
        let std = self.var.sqrt().max(0.008);
        let score = dev / std;
        // Picos quase não entram na média (senão o próprio bumbo levanta a régua); no começo aprende rápido.
        let warming = self.samples < 30;
        let rate = if warming { 0.15 } else if score > 3.0 { 0.006 } else { 0.04 };
        self.avg += dev * rate;
        self.var += (dev * dev - self.var) * rate;
        if !warming && score > 3.4 && dev > 0.02 && now_ms - self.last_beat_ms > 160 {
            self.pulse = (0.6 + 0.1 * (score - 3.4)).min(1.0);
            self.last_beat_ms = now_ms;
        } else {
            self.pulse *= 0.82;
        }
        self.pulse
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sine(freq: f32) -> Vec<f32> {
        (0..N).map(|i| (2.0 * std::f32::consts::PI * freq * i as f32 / 48000.0).sin() * 0.5).collect()
    }

    fn loudest(b: [f32; BANDS]) -> usize {
        (0..BANDS).max_by(|&x, &y| b[x].total_cmp(&b[y])).unwrap()
    }

    #[test]
    fn cada_som_cai_na_faixa_certa() {
        assert_eq!(loudest(bands(&sine(90.0), 48000.0)), 0);
        assert_eq!(loudest(bands(&sine(250.0), 48000.0)), 1);
        assert_eq!(loudest(bands(&sine(800.0), 48000.0)), 2);
        assert_eq!(loudest(bands(&sine(3000.0), 48000.0)), 3);
        assert_eq!(loudest(bands(&sine(9000.0), 48000.0)), 4);
    }

    #[test]
    fn silencio_fica_em_zero() {
        let mut n = Normalizer::new();
        assert_eq!(n.apply(bands(&vec![0.0; N], 48000.0)), [0.0; BANDS]);
    }

    #[test]
    fn batida_forte_acende_o_pulso() {
        let mut d = BeatDetector::new();
        let mut t = 0;
        for i in 0..120 { t += 16; d.process(0.3 + 0.01 * ((i % 3) as f32), t); } // música "normal"
        assert!(d.process(0.3, t + 16) < 0.2);
        assert!(d.process(0.9, t + 32) >= 0.6); // bumbo
    }
}

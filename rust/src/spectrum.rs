// Visualizador: FFT do som que está saindo e a força de 4 faixas (graves → agudos), como no Wallpaper Engine.

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

/// Limites das faixas em Hz: graves, médio-graves, médios, agudos.
const EDGES: [f32; 5] = [40.0, 160.0, 600.0, 2500.0, 10000.0];

/// Amplitude média de cada faixa (sem normalizar).
pub fn bands(samples: &[f32], rate: f32) -> [f32; 4] {
    let n = samples.len();
    let mut re: Vec<f32> = samples.iter().enumerate()
        .map(|(i, &x)| x * (0.5 - 0.5 * (2.0 * std::f32::consts::PI * i as f32 / (n - 1) as f32).cos())) // janela de Hann
        .collect();
    let mut im = vec![0.0; n];
    fft(&mut re, &mut im);
    let hz = rate / n as f32;
    let mut out = [0.0; 4];
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

/// Ganho automático por faixa: divide pelo pico recente, que cai devagar (música baixa ou alta mexe igual).
pub struct Normalizer { peak: [f32; 4] }

impl Normalizer {
    pub fn new() -> Self { Normalizer { peak: [0.01; 4] } }

    pub fn apply(&mut self, raw: [f32; 4]) -> [f32; 4] {
        let mut out = [0.0; 4];
        for i in 0..4 {
            // Piso: silêncio e chiado ficam em zero em vez de virar barra cheia.
            self.peak[i] = (self.peak[i] * 0.997).max(raw[i]).max(0.002);
            out[i] = (raw[i] / self.peak[i]).clamp(0.0, 1.0).powf(1.4);
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sine(freq: f32) -> Vec<f32> {
        (0..N).map(|i| (2.0 * std::f32::consts::PI * freq * i as f32 / 48000.0).sin() * 0.5).collect()
    }

    fn loudest(b: [f32; 4]) -> usize {
        (0..4).max_by(|&x, &y| b[x].total_cmp(&b[y])).unwrap()
    }

    #[test]
    fn cada_som_cai_na_faixa_certa() {
        assert_eq!(loudest(bands(&sine(90.0), 48000.0)), 0);
        assert_eq!(loudest(bands(&sine(350.0), 48000.0)), 1);
        assert_eq!(loudest(bands(&sine(1200.0), 48000.0)), 2);
        assert_eq!(loudest(bands(&sine(5000.0), 48000.0)), 3);
    }

    #[test]
    fn silencio_fica_em_zero() {
        let mut n = Normalizer::new();
        assert_eq!(n.apply(bands(&vec![0.0; N], 48000.0)), [0.0; 4]);
    }
}

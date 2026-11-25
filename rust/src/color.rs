// Cor do degradê a partir da média da capa: mais saturada e escura, para o texto branco continuar legível.
pub fn accent(r: u64, g: u64, b: u64) -> slint::Color {
    let (r, g, b) = boost(r as f32 / 255.0, g as f32 / 255.0, b as f32 / 255.0);
    slint::Color::from_rgb_u8((r * 255.0) as u8, (g * 255.0) as u8, (b * 255.0) as u8)
}

/// Puxa a cor para longe do cinza (×1.6) e limita o brilho a 55%.
pub fn boost(r: f32, g: f32, b: f32) -> (f32, f32, f32) {
    let gray = (r + g + b) / 3.0;
    let push = |c: f32| (gray + (c - gray) * 1.6).clamp(0.0, 1.0);
    let (r, g, b) = (push(r), push(g), push(b));
    let max = r.max(g).max(b).max(0.0001);
    let k = (0.55 / max).min(1.0);
    (r * k, g * k, b * k)
}

#[cfg(test)]
mod tests {
    use super::boost;

    #[test]
    fn cor_escura_e_saturada() {
        let (r, g, b) = boost(0.9, 0.9, 1.0); // capa quase branca e azulada
        assert!(r.max(g).max(b) <= 0.551, "brilho limitado");
        assert!(b > r && b > g, "mantém o tom azul");
        let (r, g, b) = boost(0.5, 0.5, 0.5); // cinza continua cinza
        assert!((r - g).abs() < 1e-6 && (g - b).abs() < 1e-6);
    }
}

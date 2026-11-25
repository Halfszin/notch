// Letra sincronizada (formato LRC: "[01:23.45] linha"), buscada no LRCLIB (grátis, sem cadastro).
// Só o nome da música, o artista e a duração são enviados.

#[derive(Debug, Clone, PartialEq)]
pub struct Line {
    pub at: f64,
    pub text: String,
}

pub fn parse_lrc(lrc: &str) -> Vec<Line> {
    let mut out = Vec::new();
    for raw in lrc.lines() {
        // Uma linha pode ter várias marcas: "[00:10.00][00:50.00] refrão".
        let mut rest = raw.trim();
        let mut times = Vec::new();
        while let Some(stripped) = rest.strip_prefix('[') {
            let Some(end) = stripped.find(']') else { break };
            if let Some(t) = time(&stripped[..end]) { times.push(t) } else { break } // [ar:Artista] e afins: ignora
            rest = stripped[end + 1..].trim_start();
        }
        for t in times {
            out.push(Line { at: t, text: rest.trim().to_string() });
        }
    }
    out.sort_by(|a, b| a.at.total_cmp(&b.at));
    out
}

fn time(tag: &str) -> Option<f64> {
    let (m, s) = tag.split_once(':')?;
    let m: f64 = m.trim().parse().ok()?;
    let s: f64 = s.trim().replace(',', ".").parse().ok()?;
    (m >= 0.0 && (0.0..60.0).contains(&s)).then_some(m * 60.0 + s)
}

/// Linha que está sendo cantada no segundo `t` (None antes da primeira).
pub fn index_at(lines: &[Line], t: f64) -> Option<usize> {
    let n = lines.partition_point(|l| l.at <= t);
    n.checked_sub(1)
}

#[cfg(windows)]
pub fn fetch(artist: &str, title: &str, duration: f64) -> Option<Vec<Line>> {
    let mut req = ureq::get("https://lrclib.net/api/get")
        .query("artist_name", artist)
        .query("track_name", title)
        .header("User-Agent", "Orbi (github.com/Halfszin/notch)");
    if duration > 0.0 {
        req = req.query("duration", &format!("{}", duration.round() as u64));
    }
    let body = req.call().ok()?.body_mut().read_to_string().ok()?;
    let json: serde_json::Value = serde_json::from_str(&body).ok()?;
    let lines = parse_lrc(json.get("syncedLyrics")?.as_str()?);
    (!lines.is_empty()).then_some(lines)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn le_letra_e_acha_a_linha_atual() {
        let lrc = "[ar:Artista]\n[00:12.50] primeira\n[00:15.00][01:00.00] refrão\n[00:20,25]   terceira  \nsem marca\n[xx:yy] lixo";
        let l = parse_lrc(lrc);
        assert_eq!(l.iter().map(|x| x.text.as_str()).collect::<Vec<_>>(), ["primeira", "refrão", "terceira", "refrão"]);
        assert_eq!(l[2].at, 20.25);
        assert_eq!(index_at(&l, 5.0), None);
        assert_eq!(index_at(&l, 12.5), Some(0));
        assert_eq!(index_at(&l, 17.0), Some(1));
        assert_eq!(index_at(&l, 61.0), Some(3));
    }
}

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

/// Cliente HTTPS com o TLS do Windows (schannel): o ureq tenta Rustls por padrão e ele não está compilado.
#[cfg(windows)]
pub fn http() -> ureq::Agent {
    use ureq::tls::{TlsConfig, TlsProvider};
    ureq::Agent::config_builder().tls_config(TlsConfig::builder().provider(TlsProvider::NativeTls).build()).build().new_agent()
}

/// Letra sincronizada no LRCLIB. O /api/get só responde com artista, música, álbum e duração exatos;
/// sem álbum ou com outra duração, procura no /api/search e fica com a de duração mais perto.
#[cfg(windows)]
pub fn fetch(artist: &str, title: &str, album: &str, duration: f64) -> Option<Vec<Line>> {
    let agent = http();
    let get = |path: &str, q: &[(&str, String)]| -> Option<serde_json::Value> {
        let mut req = agent.get(format!("https://lrclib.net/api/{path}")).header("User-Agent", "Orbi (github.com/Halfszin/notch)");
        for (k, v) in q { req = req.query(*k, v); }
        serde_json::from_str(&req.call().ok()?.body_mut().read_to_string().ok()?).ok()
    };
    let synced = |v: &serde_json::Value| v.get("syncedLyrics").and_then(|s| s.as_str()).map(parse_lrc).filter(|l| !l.is_empty());
    if !album.is_empty() && duration > 0.0 {
        let q = [("artist_name", artist.to_string()), ("track_name", title.to_string()), ("album_name", album.to_string()), ("duration", format!("{}", duration.round() as u64))];
        if let Some(lines) = get("get", &q).as_ref().and_then(synced) { return Some(lines); }
    }
    let found = get("search", &[("artist_name", artist.to_string()), ("track_name", title.to_string())])?;
    pick(found.as_array()?, duration).and_then(synced)
}

/// Diagnóstico do CI: o que o LRCLIB responde (ou o erro) a uma busca simples.
#[cfg(windows)]
pub fn probe() -> String {
    let r = http().get("https://lrclib.net/api/search").query("track_name", "Yellow").query("artist_name", "Coldplay")
        .header("User-Agent", "Orbi (github.com/Halfszin/notch)").call();
    match r {
        Ok(mut res) => {
            let status = res.status();
            match res.body_mut().read_to_string() {
                Ok(b) => format!("status={status} tamanho={} começo={}", b.len(), b.chars().take(120).collect::<String>()),
                Err(e) => format!("status={status} erro_ao_ler={e}"),
            }
        }
        Err(e) => format!("erro={e}"),
    }
}

/// Entre os resultados da busca com letra sincronizada, o de duração mais perto da música tocando.
pub fn pick(items: &[serde_json::Value], duration: f64) -> Option<&serde_json::Value> {
    items.iter()
        .filter(|v| v.get("syncedLyrics").and_then(|s| s.as_str()).is_some_and(|s| !s.is_empty()))
        .min_by(|a, b| {
            let d = |v: &serde_json::Value| if duration > 0.0 { (v.get("duration").and_then(|d| d.as_f64()).unwrap_or(0.0) - duration).abs() } else { 0.0 };
            d(a).total_cmp(&d(b))
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escolhe_a_busca_de_duracao_mais_perto() {
        let items: Vec<serde_json::Value> = serde_json::from_str(r#"[
            {"id":1,"duration":200,"syncedLyrics":null},
            {"id":2,"duration":250,"syncedLyrics":"[00:01.00] a"},
            {"id":3,"duration":389,"syncedLyrics":"[00:01.00] b"}]"#).unwrap();
        assert_eq!(pick(&items, 390.0).unwrap()["id"], 3);
        assert_eq!(pick(&items, 0.0).unwrap()["id"], 2);
        assert!(pick(&items[..1], 200.0).is_none());
    }

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

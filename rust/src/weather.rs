// Clima pelo Open-Meteo (grátis, sem cadastro), como no Orbi em C#. Só a cidade escolhida é enviada.

/// Código WMO do Open-Meteo → símbolo (fonte Segoe UI Symbol, monocromática) e descrição.
pub fn describe(code: i64) -> (&'static str, &'static str) {
    match code {
        0 => ("☀", "Céu limpo"),
        1 | 2 => ("⛅", "Parcialmente nublado"),
        3 => ("☁", "Nublado"),
        45 | 48 => ("≋", "Neblina"),
        51..=67 | 80..=82 => ("☂", "Chuva"),
        71..=77 | 85 | 86 => ("❄", "Neve"),
        95.. => ("⚡", "Tempestade"),
        _ => ("☁", "Tempo"),
    }
}

/// Nome legível do resultado do geocoding ("Campinas, São Paulo, Brasil"), sem repetir partes.
pub fn place_name(r: &serde_json::Value) -> String {
    let mut parts: Vec<&str> = Vec::new();
    for k in ["name", "admin1", "country"] {
        if let Some(v) = r.get(k).and_then(|v| v.as_str()).filter(|v| !v.is_empty()) {
            if !parts.contains(&v) { parts.push(v); }
        }
    }
    parts.join(", ")
}

/// Procura a cidade: (latitude, longitude, nome).
#[cfg(windows)]
pub fn find_city(query: &str) -> Result<Option<(f64, f64, String)>, String> {
    let body = crate::lyrics::http().get("https://geocoding-api.open-meteo.com/v1/search")
        .query("count", "1").query("language", "pt").query("name", query.trim())
        .call().map_err(|e| e.to_string())?.body_mut().read_to_string().map_err(|e| e.to_string())?;
    let json: serde_json::Value = serde_json::from_str(&body).map_err(|e| e.to_string())?;
    let Some(r) = json.get("results").and_then(|r| r.get(0)) else { return Ok(None) };
    let (Some(lat), Some(lon)) = (r.get("latitude").and_then(|v| v.as_f64()), r.get("longitude").and_then(|v| v.as_f64())) else { return Ok(None) };
    Ok(Some((lat, lon, place_name(r))))
}

/// Temperatura e código do tempo agora.
#[cfg(windows)]
pub fn now(lat: f64, lon: f64) -> Option<(f64, i64)> {
    let body = crate::lyrics::http().get("https://api.open-meteo.com/v1/forecast")
        .query("latitude", &lat.to_string()).query("longitude", &lon.to_string()).query("current", "temperature_2m,weather_code")
        .call().ok()?.body_mut().read_to_string().ok()?;
    let json: serde_json::Value = serde_json::from_str(&body).ok()?;
    let cur = json.get("current")?;
    Some((cur.get("temperature_2m")?.as_f64()?, cur.get("weather_code")?.as_i64()?))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn simbolos_e_nome() {
        assert_eq!(describe(0).0, "☀");
        assert_eq!(describe(63).1, "Chuva");
        assert_eq!(describe(99).1, "Tempestade");
        let r = serde_json::json!({ "name": "São Paulo", "admin1": "São Paulo", "country": "Brasil" });
        assert_eq!(place_name(&r), "São Paulo, Brasil");
    }
}

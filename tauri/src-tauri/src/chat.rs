// Chat pela sua assinatura do Claude (Pro/Max), sem chave de API: usa o Claude Code instalado no PC
// ("claude -p"), já logado na sua conta, como no Orbi em C# (e no coucou). Só libera a busca na web.

/// Conversa anterior + pergunta nova num texto só (o "claude -p" recebe um pedido por vez).
pub fn prompt(history: &[(bool, String)], question: &str) -> String {
    if history.is_empty() { return question.to_string(); }
    let mut s = String::from("A conversa até agora:\n<conversa>\n");
    for (user, text) in history {
        let tag = if *user { "usuario" } else { "assistente" };
        s.push_str(&format!("<{tag}>\n{text}\n</{tag}>\n"));
    }
    s.push_str("</conversa>\n\nA nova mensagem:\n");
    s.push_str(question);
    s
}

/// Frase de boas-vindas da conversa vazia, pela hora (como o Claude faz), escolhida por `pick`.
pub fn greeting(hour: u32, pick: usize) -> &'static str {
    const MADRUGADA: [&str; 4] = ["Pensamentos da madrugada?", "Ainda acordado? Bora conversar.", "Insônia criativa?", "O que está tirando seu sono?"];
    const MANHA: [&str; 4] = ["Bom dia! Por onde começamos?", "Café na mão? Manda a primeira.", "Que tal planejar o dia?", "Manhã boa pra uma ideia nova."];
    const TARDE: [&str; 4] = ["Boa tarde! No que posso ajudar?", "Pausa pro café e uma pergunta?", "O que está na sua cabeça agora?", "Tarde produtiva? Vamos nessa."];
    const NOITE: [&str; 4] = ["Pensamentos noturnos?", "Boa noite! Sobre o que vamos falar?", "Fim de dia, hora de pensar alto.", "Uma última ideia antes de dormir?"];
    let list = match hour { 0..=4 => &MADRUGADA, 5..=11 => &MANHA, 12..=17 => &TARDE, _ => &NOITE };
    list[pick % list.len()]
}

/// Resposta do "claude -p --output-format json": o texto, ou o erro que ele contou.
pub fn parse_answer(stdout: &str) -> Result<String, String> {
    let json: serde_json::Value = serde_json::from_str(stdout.trim()).map_err(|_| String::new())?;
    let result = json.get("result").and_then(|r| r.as_str()).unwrap_or("").trim().to_string();
    let error = json.get("is_error").and_then(|e| e.as_bool()).unwrap_or(false);
    if error || result.is_empty() { Err(if result.is_empty() { "O Claude Code não respondeu.".into() } else { result }) } else { Ok(result) }
}

/// Corpo do pedido para a API do Claude (Messages): conversa inteira, respostas curtas.
pub fn api_body(history: &[(bool, String)], question: &str) -> serde_json::Value {
    let mut messages: Vec<serde_json::Value> = history.iter()
        .map(|(user, text)| serde_json::json!({ "role": if *user { "user" } else { "assistant" }, "content": text }))
        .collect();
    messages.push(serde_json::json!({ "role": "user", "content": question }));
    serde_json::json!({
        "model": "claude-opus-5-5",
        "max_tokens": 16000,
        "system": SYSTEM,
        // Conversa do notch: respostas rápidas e curtas.
        "output_config": { "effort": "low" },
        // Se o filtro de segurança recusar, a API tenta de novo num modelo de reserva na mesma chamada.
        "fallbacks": "default",
        "messages": messages,
    })
}

/// Texto da resposta da API (ou o motivo de não ter resposta).
pub fn api_answer(json: &serde_json::Value) -> Result<String, String> {
    if json.get("stop_reason").and_then(|s| s.as_str()) == Some("refusal") {
        return Err("O Claude não quis responder a esse pedido.".into());
    }
    let text: String = json.get("content").and_then(|c| c.as_array()).into_iter().flatten()
        .filter(|b| b.get("type").and_then(|t| t.as_str()) == Some("text"))
        .filter_map(|b| b.get("text").and_then(|t| t.as_str()))
        .collect::<Vec<_>>().join("\n").trim().to_string();
    if text.is_empty() { Err("A API não mandou texto.".into()) } else { Ok(text) }
}

pub const SYSTEM: &str = "Você é o assistente do Orbi, um notch no topo da tela do Windows. Responda em português do Brasil, \
    curto e direto (a resposta aparece numa área pequena), sem markdown pesado.";

#[cfg(windows)]
pub use win::{ask, ask_api, find};

#[cfg(windows)]
mod win {
    use std::io::Write;
    use std::os::windows::process::CommandExt;
    use std::path::PathBuf;
    use std::process::{Command, Stdio};

    use super::SYSTEM;

    /// Pergunta pela API do Claude com a sua chave (quando não há Claude Code ou ele não respondeu).
    pub fn ask_api(key: &str, history: &[(bool, String)], question: &str) -> Result<String, String> {
        let res = crate::lyrics::http().post("https://api.anthropic.com/v1/messages")
            .header("x-api-key", key)
            .header("anthropic-version", "2026-10-21")
            .header("anthropic-beta", "server-side-fallback-2026-07-01")
            .header("content-type", "application/json")
            .send(super::api_body(history, question).to_string());
        let mut res = match res {
            Ok(r) => r,
            Err(ureq::Error::StatusCode(401)) => return Err("A chave de API foi recusada. Confira nas configurações.".into()),
            Err(ureq::Error::StatusCode(429)) => return Err("Limite da API atingido agora. Tente de novo daqui a pouco.".into()),
            Err(ureq::Error::StatusCode(c)) if c >= 500 => return Err("A API do Claude está com problema agora. Tente de novo.".into()),
            Err(e) => return Err(format!("Não deu para falar com a API: {e}")),
        };
        let body = res.body_mut().read_to_string().map_err(|e| e.to_string())?;
        let json: serde_json::Value = serde_json::from_str(&body).map_err(|e| e.to_string())?;
        super::api_answer(&json)
    }

    /// Claude Code instalado: instalador oficial (.local\bin) e npm primeiro (o PATH deste processo é o de quando o
    /// Orbi abriu, então um Claude Code instalado depois não estaria nele), depois o PATH.
    pub fn find() -> Option<PathBuf> {
        let home = PathBuf::from(std::env::var_os("USERPROFILE")?);
        let mut dirs = vec![home.join(".local").join("bin")];
        if let Some(app) = std::env::var_os("APPDATA") { dirs.push(PathBuf::from(app).join("npm")); }
        if let Some(path) = std::env::var_os("PATH") { dirs.extend(std::env::split_paths(&path)); }
        ["claude.exe", "claude.cmd"].iter().find_map(|name| dirs.iter().map(|d| d.join(name)).find(|p| p.is_file()))
    }

    /// Pergunta ao Claude Code. A pergunta vai pela entrada padrão, nunca na linha de comando.
    pub fn ask(exe: &std::path::Path, prompt: &str) -> Result<String, String> {
        // "--setting-sources ''": não carrega suas configurações, então os hooks do Orbi não disparam para o próprio chat.
        let args = ["-p", "--output-format", "json", "--system-prompt", SYSTEM, "--tools", "WebSearch,WebFetch",
            "--allowedTools", "WebSearch,WebFetch", "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands"];
        let mut cmd = if exe.extension().is_some_and(|e| e.eq_ignore_ascii_case("cmd")) {
            // O claude.cmd (instalação pelo npm) só roda pelo cmd.
            let mut c = Command::new(std::env::var_os("ComSpec").unwrap_or("cmd.exe".into()));
            let quoted: Vec<String> = std::iter::once(exe.display().to_string()).chain(args.iter().map(|a| a.to_string()))
                .map(|a| format!("\"{}\"", a.replace('"', "\\\""))).collect();
            c.raw_arg(format!("/d /s /c \"{}\"", quoted.join(" ")));
            c
        } else {
            let mut c = Command::new(exe);
            c.args(args);
            c
        };
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        let mut child = cmd.creation_flags(CREATE_NO_WINDOW).stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped())
            .spawn().map_err(|e| format!("Não deu para abrir o Claude Code: {e}"))?;
        child.stdin.take().map(|mut s| s.write_all(prompt.as_bytes()));
        let out = child.wait_with_output().map_err(|e| e.to_string())?;
        super::parse_answer(&String::from_utf8_lossy(&out.stdout)).map_err(|e| {
            if !e.is_empty() { return e; }
            let why = String::from_utf8_lossy(&out.stderr).trim().lines().last().unwrap_or("").to_string();
            if why.is_empty() { "O Claude Code não respondeu. Ele está logado? Rode \"claude\" uma vez no terminal e faça /login.".into() }
            else { format!("O Claude Code não respondeu: {why}") }
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn junta_a_conversa() {
        assert_eq!(prompt(&[], "oi"), "oi");
        let p = prompt(&[(true, "oi".into()), (false, "olá!".into())], "tudo bem?");
        assert!(p.contains("<usuario>\noi\n</usuario>") && p.contains("<assistente>\nolá!\n</assistente>") && p.ends_with("tudo bem?"));
    }

    #[test]
    fn frase_pela_hora() {
        assert_eq!(greeting(23, 0), "Pensamentos noturnos?");
        assert_eq!(greeting(2, 0), "Pensamentos da madrugada?");
        assert_eq!(greeting(9, 5), "Café na mão? Manda a primeira.");
    }

    #[test]
    fn pedido_e_resposta_da_api() {
        let body = api_body(&[(true, "oi".into()), (false, "olá".into())], "tudo bem?");
        assert_eq!(body["model"], "claude-opus-5-5");
        assert_eq!(body["messages"].as_array().unwrap().len(), 3);
        assert_eq!(body["messages"][1]["role"], "assistant");
        let ok = serde_json::json!({ "stop_reason": "end_turn", "content": [{ "type": "thinking", "thinking": "" }, { "type": "text", "text": "Tudo!" }] });
        assert_eq!(api_answer(&ok), Ok("Tudo!".into()));
        assert!(api_answer(&serde_json::json!({ "stop_reason": "refusal", "content": [] })).is_err());
    }

    #[test]
    fn le_a_resposta() {
        assert_eq!(parse_answer(r#"{"result":" Oi! ","is_error":false}"#), Ok("Oi!".into()));
        assert_eq!(parse_answer(r#"{"result":"limite atingido","is_error":true}"#), Err("limite atingido".into()));
        assert_eq!(parse_answer("lixo"), Err(String::new()));
    }
}

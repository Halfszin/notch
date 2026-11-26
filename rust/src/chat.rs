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

#[cfg(windows)]
pub use win::{ask, find};

#[cfg(windows)]
mod win {
    use std::io::Write;
    use std::os::windows::process::CommandExt;
    use std::path::PathBuf;
    use std::process::{Command, Stdio};

    const SYSTEM: &str = "Você é o assistente do Orbi, um notch no topo da tela do Windows. Responda em português do Brasil, \
        curto e direto (a resposta aparece numa área pequena), sem markdown pesado.";

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
    fn le_a_resposta() {
        assert_eq!(parse_answer(r#"{"result":" Oi! ","is_error":false}"#), Ok("Oi!".into()));
        assert_eq!(parse_answer(r#"{"result":"limite atingido","is_error":true}"#), Err("limite atingido".into()));
        assert_eq!(parse_answer("lixo"), Err(String::new()));
    }
}

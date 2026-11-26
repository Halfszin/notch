// Agentes do Claude Code no notch (ideia do coucou, porte do Orbi em C#): o Claude Code roda um curl a
// cada evento (hooks) e manda para um servidor só do próprio PC, com um código secreto no endereço.
// O notch mostra as sessões, os limites do plano e os pedidos de permissão (Permitir / Negar / Sempre).
use serde_json::{json, Map, Value};

/// Marca das entradas do Orbi no settings.json do Claude Code (a mesma do Orbi em C#: uma troca a outra).
pub const MARKER: &str = "# orbi-hook";

/// Eventos ouvidos e o tempo máximo de cada hook (PermissionRequest espera você decidir).
pub const EVENTS: [(&str, u64); 7] = [
    ("SessionStart", 10), ("SessionEnd", 10), ("UserPromptSubmit", 10), ("PreToolUse", 10),
    ("PermissionRequest", 120), ("Notification", 10), ("Stop", 10),
];

/// No Windows o Claude Code roda hooks pelo Git Bash: um curl simples. "|| true": com o Orbi fechado, segue normal.
pub fn command(port: u16, token: &str, evt: &str, max_secs: u64) -> String {
    format!("curl -s -m {max_secs} --data-binary @- http://127.0.0.1:{port}/{token}/{evt} || true {MARKER}")
}

fn is_ours_command(v: &Value) -> bool {
    v.get("command").and_then(|c| c.as_str()).is_some_and(|c| c.contains(MARKER))
}

fn is_ours_entry(v: &Value) -> bool {
    v.get("hooks").and_then(|h| h.as_array()).is_some_and(|hs| hs.iter().any(is_ours_command))
}

/// Põe os hooks do Orbi no settings.json sem mexer no resto. Devolve (novo json, se a status line entrou).
/// A status line (limites do plano) só entra se você ainda não tem uma, ou se a que existe já é do Orbi.
pub fn merge(existing: Option<Value>, port: u16, token: &str) -> Result<(Value, bool), String> {
    let mut root = existing.unwrap_or_else(|| json!({}));
    let obj = root.as_object_mut().ok_or("o settings.json do Claude Code não é um objeto JSON; nada foi alterado")?;
    let hooks = obj.entry("hooks").or_insert_with(|| json!({})).as_object_mut()
        .ok_or("o \"hooks\" do settings.json está num formato desconhecido; nada foi alterado")?;
    for (evt, timeout) in EVENTS {
        let list = hooks.entry(evt).or_insert_with(|| json!([])).as_array_mut()
            .ok_or(format!("o hook \"{evt}\" do settings.json está num formato desconhecido; nada foi alterado"))?;
        list.retain(|e| !is_ours_entry(e));
        list.push(json!({ "hooks": [{ "type": "command", "command": command(port, token, evt, timeout - 5), "timeout": timeout }] }));
    }
    let status = obj.get("statusLine").is_none_or(is_ours_command);
    if status {
        obj.insert("statusLine".into(), json!({ "type": "command", "command": command(port, token, "statusline", 2) }));
    }
    Ok((root, status))
}

/// Tira só o que é do Orbi.
pub fn unmerge(mut root: Value) -> Value {
    if let Some(obj) = root.as_object_mut() {
        if let Some(hooks) = obj.get_mut("hooks").and_then(|h| h.as_object_mut()) {
            let keys: Vec<String> = hooks.keys().cloned().collect();
            for k in keys {
                let Some(list) = hooks.get_mut(&k).and_then(|l| l.as_array_mut()) else { continue };
                let before = list.len();
                list.retain(|e| !is_ours_entry(e));
                if before != list.len() && list.is_empty() { hooks.remove(&k); }
            }
            if hooks.is_empty() { obj.remove("hooks"); }
        }
        if obj.get("statusLine").is_some_and(is_ours_command) { obj.remove("statusLine"); }
    }
    root
}

pub fn is_installed(root: &Value) -> bool {
    root.get("hooks").and_then(|h| h.as_object()).is_some_and(|h| h.values().any(|l| l.as_array().is_some_and(|a| a.iter().any(is_ours_entry))))
}

fn str_of<'a>(v: &'a Value, key: &str) -> Option<&'a str> { v.get(key).and_then(|s| s.as_str()) }

fn short(s: &str) -> String {
    let s = s.replace('\n', " ");
    let s = s.trim();
    if s.chars().count() > 40 { format!("{}…", s.chars().take(40).collect::<String>()) } else { s.to_string() }
}

/// O que o agente está fazendo, em português.
pub fn describe(tool: &str, input: &Value) -> String {
    let file = str_of(input, "file_path").or(str_of(input, "notebook_path"));
    let name = file.map(|f| f.rsplit(['/', '\\']).next().unwrap_or(f)).unwrap_or("");
    match tool {
        "Edit" | "MultiEdit" | "NotebookEdit" => format!("Editando {name}"),
        "Write" => format!("Criando {name}"),
        "Read" => format!("Lendo {name}"),
        "Bash" | "PowerShell" if str_of(input, "command").is_some() => format!("Rodando: {}", short(str_of(input, "command").unwrap())),
        "Grep" | "Glob" => "Procurando no código".into(),
        "WebSearch" => "Pesquisando na web".into(),
        "WebFetch" => "Lendo uma página da web".into(),
        "Task" | "Agent" => "Chamando um subagente".into(),
        "TodoWrite" => "Organizando a lista de tarefas".into(),
        "" => "Trabalhando".into(),
        _ => format!("Usando {tool}"),
    }
}

/// Frase do pedido de permissão e o que ele quer rodar/abrir.
pub fn permission_text(tool: &str, input: &Value) -> (String, String) {
    let what = match tool {
        "Bash" | "PowerShell" => "Claude Code quer rodar um comando".to_string(),
        "Edit" | "MultiEdit" | "Write" | "NotebookEdit" => "Claude Code quer mexer num arquivo".to_string(),
        "WebFetch" | "WebSearch" => "Claude Code quer acessar a web".to_string(),
        _ => format!("Claude Code quer usar {tool}"),
    };
    let code = ["command", "file_path", "url", "query"].iter().find_map(|k| str_of(input, k)).unwrap_or(tool).to_string();
    (what, code)
}

/// Resposta do hook PermissionRequest: allow / deny / always (allow + as regras sugeridas pelo Claude Code).
pub fn permission_response(choice: &str, suggestions: &Value) -> String {
    let mut decision = Map::new();
    decision.insert("behavior".into(), json!(if choice == "deny" { "deny" } else { "allow" }));
    if choice == "deny" { decision.insert("message".into(), json!("Negado pelo Orbi")); }
    if choice == "always" && suggestions.as_array().is_some_and(|a| !a.is_empty()) { decision.insert("updatedPermissions".into(), suggestions.clone()); }
    json!({ "hookSpecificOutput": { "hookEventName": "PermissionRequest", "decision": decision } }).to_string()
}

/// Limites do plano (status line do Claude Code, campo rate_limits): (uso %, quando zera em segundos unix).
pub fn plan_window(payload: &Value, key: &str, now: f64) -> Option<(f64, f64)> {
    let w = payload.get("rate_limits")?.get(key)?;
    let pct = w.get("used_percentage")?.as_f64()?;
    let resets = w.get("resets_at")?.as_f64()?;
    // Mais de 400 dias no futuro: provavelmente veio em milissegundos; melhor ignorar do que mostrar errado.
    if !(0.0..=200.0).contains(&pct) || resets <= 0.0 || resets > now + 400.0 * 86400.0 { return None; }
    Some((pct.min(100.0), resets))
}

/// Nome do projeto: a pasta onde o Claude Code está.
pub fn project_of(payload: &Value) -> String {
    str_of(payload, "cwd").map(|c| c.trim_end_matches(['/', '\\'])).and_then(|c| c.rsplit(['/', '\\']).next())
        .filter(|s| !s.is_empty()).unwrap_or("Claude Code").to_string()
}

#[cfg(windows)]
pub use win::{port, setup};

#[cfg(windows)]
mod win {
    use super::*;
    use crate::{AgentRow, Notch};
    use slint::{ComponentHandle, ModelRc, VecModel};
    use std::cell::RefCell;
    use std::collections::HashMap;
    use std::io::{Read, Write};
    use std::net::{TcpListener, TcpStream};
    use std::sync::mpsc;
    use std::time::{Duration, Instant};

    struct Session { project: String, status: String, busy: bool, updated: Instant }
    struct Pending { id: u64, project: String, what: String, code: String, suggestions: Value, reply: mpsc::Sender<String> }

    #[derive(Default)]
    struct Hub { sessions: HashMap<String, Session>, pending: Vec<Pending>, next_id: u64, five: Option<(f64, f64)>, week: Option<(f64, f64)> }

    thread_local! {
        static HUB: RefCell<Hub> = RefCell::new(Hub::default());
        static PORT: std::cell::Cell<u16> = const { std::cell::Cell::new(0) };
    }

    /// Porta em que o servidor ficou (0 = não subiu).
    pub fn port() -> u16 { PORT.with(|p| p.get()) }

    pub fn setup(notch: &Notch, preferred: u16, token: String) {
        let weak = notch.as_weak();
        notch.on_perm_decide(move |choice| decide(&weak, &choice));
        // Servidor só do próprio PC; tenta a porta salva e as 9 seguintes. Pula porta onde outro programa já
        // atende: no Windows dá para abrir 127.0.0.1 numa porta que outro programa ouve em todas as redes, e aí
        // o Orbi roubaria as conexões dele (derrubou a máquina do teste do GitHub).
        let busy = |p: u16| TcpStream::connect_timeout(&std::net::SocketAddr::from(([127, 0, 0, 1], p)), Duration::from_millis(300)).is_ok();
        let Some((listener, port)) = (preferred..preferred + 10).filter(|&p| !busy(p)).find_map(|p| TcpListener::bind(("127.0.0.1", p)).ok().map(|l| (l, p))) else { return };
        PORT.with(|p| p.set(port));
        let weak = notch.as_weak();
        std::thread::spawn(move || {
            for conn in listener.incoming().flatten() {
                let (weak, token) = (weak.clone(), token.clone());
                std::thread::spawn(move || { let _ = serve(conn, &token, weak); });
            }
        });
    }

    fn serve(mut conn: TcpStream, token: &str, weak: slint::Weak<Notch>) -> std::io::Result<()> {
        conn.set_read_timeout(Some(Duration::from_secs(10)))?;
        let (path, body) = read_request(&mut conn)?;
        let parts: Vec<&str> = path.trim_matches('/').split('/').collect();
        // Código secreto comparado sem atalho (tempo igual): um site ou outro programa não adivinha nem aprova nada.
        let ok = parts.len() == 2 && parts[0].len() == token.len() && parts[0].bytes().zip(token.bytes()).fold(0u8, |a, (x, y)| a | (x ^ y)) == 0;
        if !ok { return reply(&mut conn, 403, ""); }
        let Ok(payload) = serde_json::from_slice::<Value>(&body) else { return reply(&mut conn, 400, "") };
        let evt = parts[1].to_string();
        let (tx, rx) = mpsc::channel();
        let wait = if evt == "PermissionRequest" { 115 } else { 5 };
        log(&format!("recebido {evt}"));
        let sent = slint::invoke_from_event_loop(move || handle(&weak, &evt, payload, tx));
        if let Err(e) = sent { log(&format!("não chegou na interface: {e}")); }
        // Sem resposta a tempo: vazio, e o Claude Code pergunta no terminal como sempre.
        let answer = rx.recv_timeout(Duration::from_secs(wait)).unwrap_or_default();
        reply(&mut conn, 200, &answer)
    }

    fn read_request(conn: &mut TcpStream) -> std::io::Result<(String, Vec<u8>)> {
        let bad = |m: &str| std::io::Error::new(std::io::ErrorKind::InvalidData, m.to_string());
        let mut buf = Vec::new();
        let mut chunk = [0u8; 8192];
        let head_end = loop {
            let n = conn.read(&mut chunk)?;
            if n == 0 || buf.len() > 32 * 1024 { return Err(bad("cabeçalho inválido")); }
            buf.extend_from_slice(&chunk[..n]);
            if let Some(i) = buf.windows(4).position(|w| w == b"\r\n\r\n") { break i; }
        };
        let head = String::from_utf8_lossy(&buf[..head_end]).to_string();
        let mut lines = head.split("\r\n");
        let path = lines.next().and_then(|l| l.split(' ').nth(1)).unwrap_or("/").to_string();
        let mut len = 0usize;
        let mut expect = false;
        for l in lines {
            let Some((k, v)) = l.split_once(':') else { continue };
            match k.trim().to_ascii_lowercase().as_str() {
                "content-length" => len = v.trim().parse().map_err(|_| bad("tamanho"))?,
                "expect" => expect = v.to_ascii_lowercase().contains("100-continue"),
                _ => {}
            }
        }
        if len > 1 << 20 { return Err(bad("corpo grande demais")); }
        // O curl pede licença antes de mandar corpos maiores.
        if expect { conn.write_all(b"HTTP/1.1 100 Continue\r\n\r\n")?; }
        let mut body = buf[head_end + 4..].to_vec();
        while body.len() < len {
            let n = conn.read(&mut chunk)?;
            if n == 0 { return Err(bad("corpo incompleto")); }
            body.extend_from_slice(&chunk[..n]);
        }
        body.truncate(len);
        Ok((path, body))
    }

    fn reply(conn: &mut TcpStream, status: u16, body: &str) -> std::io::Result<()> {
        let head = format!("HTTP/1.1 {status} {}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", if status == 200 { "OK" } else { "Erro" }, body.len());
        conn.write_all(head.as_bytes())?;
        conn.write_all(body.as_bytes())
    }

    fn now_unix() -> f64 { std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs_f64()).unwrap_or(0.0) }

    /// Um evento chegou (na thread da interface).
    /// Registro do caminho dos eventos (%AppData%\Orbi\rust-agentes.log), para achar onde um evento para.
    fn log(line: &str) {
        use std::io::Write;
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(crate::store::dir().join("rust-agentes.log")) {
            let _ = writeln!(f, "{line}");
        }
    }

    fn handle(weak: &slint::Weak<Notch>, evt: &str, p: Value, tx: mpsc::Sender<String>) {
        log(&format!("na interface {evt}"));
        let Some(n) = weak.upgrade() else { log("notch sumiu"); return };
        if evt == "statusline" {
            let now = now_unix();
            let model = p.get("model").and_then(|m| str_of(m, "display_name")).unwrap_or("Claude").to_string();
            let text = HUB.with(|h| {
                let mut h = h.borrow_mut();
                // Cada sessão manda sua cópia; uma cópia velha não pode fazer o número voltar.
                let newest = |cur: Option<(f64, f64)>, inc: Option<(f64, f64)>| match (cur, inc) {
                    (Some(c), Some(i)) => Some(if i.1 > c.1 || (i.1 == c.1 && i.0 >= c.0) { i } else { c }),
                    (c, i) => i.or(c),
                };
                h.five = newest(h.five, plan_window(&p, "five_hour", now));
                h.week = newest(h.week, plan_window(&p, "seven_day", now));
                let mut parts = vec![model];
                if let Some(f) = h.five { parts.push(format!("5h {:.0}%", f.0)); }
                if let Some(w) = h.week { parts.push(format!("semana {:.0}%", w.0)); }
                parts.join(" · ")
            });
            let _ = tx.send(text);
            render(&n);
            return;
        }
        let id = str_of(&p, "session_id").unwrap_or("?").to_string();
        let project = project_of(&p);
        let mut answer_now = true;
        HUB.with(|h| {
            let mut h = h.borrow_mut();
            let s = h.sessions.entry(id.clone()).or_insert_with(|| Session { project: String::new(), status: String::new(), busy: false, updated: Instant::now() });
            s.project = project.clone();
            s.updated = Instant::now();
            match evt {
                "SessionStart" => { s.status = "Começou".into(); s.busy = false; }
                "UserPromptSubmit" => { s.status = "Pensando…".into(); s.busy = true; }
                "PreToolUse" => { s.status = describe(str_of(&p, "tool_name").unwrap_or(""), p.get("tool_input").unwrap_or(&Value::Null)); s.busy = true; }
                "Notification" => { s.status = "Esperando você".into(); s.busy = false; }
                "Stop" => { s.status = "Terminou".into(); s.busy = false; }
                "SessionEnd" => { h.sessions.remove(&id); }
                "PermissionRequest" => {
                    s.status = "Pedindo permissão".into();
                    s.busy = false;
                    let tool = str_of(&p, "tool_name").unwrap_or("");
                    let (what, code) = permission_text(tool, p.get("tool_input").unwrap_or(&Value::Null));
                    let pid = h.next_id;
                    h.next_id += 1;
                    h.pending.push(Pending { id: pid, project: project.clone(), what, code, suggestions: p.get("permission_suggestions").cloned().unwrap_or(Value::Null), reply: tx.clone() });
                    answer_now = false;
                    // Sem decisão em 110 s: sai da tela (o servidor já devolveu vazio para o Claude Code).
                    let weak = weak.clone();
                    slint::Timer::single_shot(Duration::from_secs(110), move || {
                        HUB.with(|h| h.borrow_mut().pending.retain(|x| x.id != pid));
                        if let Some(n) = weak.upgrade() { render(&n); }
                    });
                }
                _ => {}
            }
            // Sessões paradas há muito tempo somem da lista.
            h.sessions.retain(|_, s| s.busy || s.updated.elapsed() < Duration::from_secs(30 * 60));
        });
        if answer_now { let _ = tx.send("{}".into()); }
        log(&format!("tratado {evt}"));
        match evt {
            "PermissionRequest" => {
                crate::features::toast(&n, "\u{E72E}", &format!("{project}: pedindo permissão"), slint::Color::from_rgb_u8(0xFF, 0x9F, 0x0A), 6);
                crate::features::sound("permissao");
            }
            "Stop" => {
                crate::features::toast(&n, "\u{E73E}", &format!("{project}: terminou"), slint::Color::from_rgb_u8(0x30, 0xD1, 0x58), 5);
                crate::features::sound("terminou");
            }
            "Notification" => {
                let msg = str_of(&p, "message").unwrap_or("O Claude precisa de você");
                crate::features::toast(&n, "\u{E7E7}", &format!("{project}: {msg}"), slint::Color::from_rgb_u8(0x0A, 0x84, 0xFF), 6);
            }
            _ => {}
        }
        render(&n);
    }

    fn decide(weak: &slint::Weak<Notch>, choice: &str) {
        let first = HUB.with(|h| { let mut h = h.borrow_mut(); (!h.pending.is_empty()).then(|| h.pending.remove(0)) });
        if let Some(p) = first {
            let _ = p.reply.send(permission_response(choice, &p.suggestions));
            HUB.with(|h| {
                for s in h.borrow_mut().sessions.values_mut().filter(|s| s.project == p.project && s.status == "Pedindo permissão") {
                    s.status = if choice == "deny" { "Permissão negada".into() } else { "Trabalhando".into() };
                    s.busy = choice != "deny";
                }
            });
        }
        if let Some(n) = weak.upgrade() { render(&n); }
    }

    fn render(n: &Notch) {
        HUB.with(|h| {
            let h = h.borrow();
            let mut rows: Vec<&Session> = h.sessions.values().collect();
            rows.sort_by_key(|s| std::cmp::Reverse(s.updated));
            let rows: Vec<AgentRow> = rows.iter().map(|s| AgentRow { project: s.project.clone().into(), status: s.status.clone().into(), busy: s.busy }).collect();
            n.set_agent_rows(ModelRc::new(VecModel::from(rows)));
            n.set_agents_busy(h.sessions.values().any(|s| s.busy));
            match h.pending.first() {
                Some(p) => {
                    // Pedido novo: vai para a página dos agentes (o notch abre sozinho).
                    if !n.get_perm_visible() {
                        use slint::Model;
                        let order = n.get_order();
                        if let Some(i) = (0..order.row_count()).find(|&i| order.row_data(i) == Some(8)) { n.set_pos(i as i32); }
                    }
                    n.set_perm_project(p.project.clone().into());
                    n.set_perm_what(p.what.clone().into());
                    n.set_perm_code(p.code.clone().into());
                    n.set_perm_count(h.pending.len() as i32);
                    n.set_perm_visible(true);
                }
                None => n.set_perm_visible(false),
            }
            let now = now_unix();
            let fmt = |w: Option<(f64, f64)>| match w {
                Some((pct, resets)) => {
                    let mins = ((resets - now) / 60.0).max(0.0) as i64;
                    let left = if mins >= 1440 { format!("{}d", mins / 1440) } else if mins >= 60 { format!("{}h{:02}", mins / 60, mins % 60) } else { format!("{mins} min") };
                    ((pct / 100.0) as f32, format!("{pct:.0}% · zera em {left}"))
                }
                None => (-1.0, String::new()),
            };
            let (f, ft) = fmt(h.five);
            let (w, wt) = fmt(h.week);
            n.set_plan_five(f);
            n.set_plan_five_text(ft.into());
            n.set_plan_week(w);
            n.set_plan_week_text(wt.into());
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn liga_e_desliga_sem_mexer_no_resto() {
        let mine = json!({ "model": "opus", "hooks": { "Stop": [{ "hooks": [{ "type": "command", "command": "echo meu" }] }] }, "statusLine": { "type": "command", "command": "minha" } });
        let (merged, status) = merge(Some(mine.clone()), 47831, "abc").unwrap();
        assert!(!status, "não troca a status line que já é sua");
        assert!(is_installed(&merged));
        assert_eq!(merged["hooks"]["Stop"].as_array().unwrap().len(), 2);
        assert_eq!(merged["hooks"]["PermissionRequest"][0]["hooks"][0]["timeout"], 120);
        // Ligar de novo não duplica.
        let (again, _) = merge(Some(merged.clone()), 47831, "abc").unwrap();
        assert_eq!(again["hooks"]["Stop"].as_array().unwrap().len(), 2);
        // Desligar volta ao que era.
        assert_eq!(unmerge(again), mine);
        // Sem arquivo: entra a status line também.
        let (fresh, status) = merge(None, 1, "t").unwrap();
        assert!(status && fresh["statusLine"]["command"].as_str().unwrap().contains(MARKER));
        assert!(merge(Some(json!({ "hooks": [] })), 1, "t").is_err());
    }

    #[test]
    fn respostas_de_permissao() {
        let sug = json!([{ "type": "addRules" }]);
        let allow: Value = serde_json::from_str(&permission_response("allow", &sug)).unwrap();
        assert_eq!(allow["hookSpecificOutput"]["decision"]["behavior"], "allow");
        assert!(allow["hookSpecificOutput"]["decision"].get("updatedPermissions").is_none());
        let always: Value = serde_json::from_str(&permission_response("always", &sug)).unwrap();
        assert_eq!(always["hookSpecificOutput"]["decision"]["updatedPermissions"], sug);
        let deny: Value = serde_json::from_str(&permission_response("deny", &sug)).unwrap();
        assert_eq!(deny["hookSpecificOutput"]["decision"]["behavior"], "deny");
    }

    #[test]
    fn textos_e_limites() {
        assert_eq!(describe("Edit", &json!({ "file_path": "C:\\proj\\main.rs" })), "Editando main.rs");
        assert_eq!(project_of(&json!({ "cwd": "/home/eu/notch/" })), "notch");
        let p = json!({ "rate_limits": { "five_hour": { "used_percentage": 42.0, "resets_at": 1000.0 }, "seven_day": { "used_percentage": 5.0, "resets_at": 1e15 } } });
        assert_eq!(plan_window(&p, "five_hour", 900.0), Some((42.0, 1000.0)));
        assert_eq!(plan_window(&p, "seven_day", 900.0), None); // milissegundos: ignora
    }
}

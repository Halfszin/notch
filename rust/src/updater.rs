// Atualização da versão em Rust: compara o commit desta cópia com o versao.txt da prévia no GitHub,
// baixa o Orbi-rust.exe novo e troca o arquivo (o Windows deixa renomear um .exe aberto).
use std::fs;
use std::io::Read;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering::Relaxed};
use std::sync::OnceLock;
use std::time::Duration;

const BASE: &str = "https://github.com/Halfszin/notch/releases/download/rust-preview/";
/// Commit desta cópia (o CI passa ORBI_BUILD ao compilar); "dev" = compilada fora do GitHub.
const BUILD: &str = match option_env!("ORBI_BUILD") { Some(b) => b, None => "dev" };

pub static AUTO: AtomicBool = AtomicBool::new(true);
static BUSY: AtomicBool = AtomicBool::new(false);
static READY: AtomicBool = AtomicBool::new(false);
static EXE: OnceLock<PathBuf> = OnceLock::new();

/// Caminho do .exe guardado ao abrir (depois da troca, o arquivo aberto vira .old).
pub fn exe() -> PathBuf {
    EXE.get_or_init(|| std::env::current_exe().unwrap_or_default()).clone()
}

pub fn version() -> String {
    format!("{} ({})", env!("CARGO_PKG_VERSION"), &BUILD[..BUILD.len().min(7)])
}

pub fn start() {
    exe();
    std::thread::spawn(|| {
        std::thread::sleep(Duration::from_secs(20));
        // Sobra da atualização anterior (a cópia velha já fechou a essa altura).
        let _ = fs::remove_file(exe().with_extension("exe.old"));
        loop {
            if AUTO.load(Relaxed) { check(); }
            std::thread::sleep(Duration::from_secs(4 * 3600));
        }
    });
}

pub fn check_now() {
    std::thread::spawn(check);
}

/// Abre a versão nova e fecha esta.
pub fn restart() {
    if std::process::Command::new(exe()).spawn().is_ok() {
        let _ = slint::quit_event_loop();
    }
}

fn report(text: String, ready: bool) {
    let _ = slint::invoke_from_event_loop(move || crate::settings::on_update(text, ready));
}

fn check() {
    if READY.load(Relaxed) || BUSY.swap(true, Relaxed) { return; }
    match download() {
        Ok(true) => {
            READY.store(true, Relaxed);
            report("Versão nova baixada. Clique em \"Reiniciar e atualizar\".".into(), true);
        }
        Ok(false) => report(format!("Você está na versão mais nova ({}).", version()), false),
        Err(e) => report(format!("Não deu para atualizar: {e}"), false),
    }
    BUSY.store(false, Relaxed);
}

/// true = versão nova já no lugar do .exe, falta reiniciar.
fn download() -> Result<bool, String> {
    if BUILD == "dev" { return Err("esta cópia foi compilada fora do GitHub.".into()); }
    report("Procurando versão nova…".into(), false);
    let get = |name: &str| ureq::get(format!("{BASE}{name}")).header("User-Agent", "Orbi").call().map_err(|e| e.to_string());
    let latest = get("versao.txt")?.body_mut().read_to_string().map_err(|e| e.to_string())?.trim().to_string();
    if latest.len() < 7 || !latest.bytes().all(|b| b.is_ascii_hexdigit()) { return Err("resposta estranha do GitHub.".into()); }
    if latest == BUILD { return Ok(false); }

    report("Baixando a versão nova…".into(), false);
    let exe = exe();
    let part = exe.with_extension("exe.part");
    let old = exe.with_extension("exe.old");
    let io = |e: std::io::Error| e.to_string();
    {
        let mut res = get("Orbi-rust.exe")?;
        let mut file = fs::File::create(&part).map_err(io)?;
        std::io::copy(&mut res.body_mut().as_reader(), &mut file).map_err(io)?;
    }
    // Download cortado nunca vira o .exe: precisa ter tamanho de app e começar com "MZ".
    let mut head = [0u8; 2];
    let ok = fs::metadata(&part).map(|m| m.len() > 1_000_000).unwrap_or(false)
        && fs::File::open(&part).and_then(|mut f| f.read_exact(&mut head)).is_ok()
        && &head == b"MZ";
    if !ok {
        let _ = fs::remove_file(&part);
        return Err("o arquivo baixado veio incompleto.".into());
    }
    let _ = fs::remove_file(&old);
    fs::rename(&exe, &old).map_err(io)?;
    if let Err(e) = fs::rename(&part, &exe) {
        let _ = fs::rename(&old, &exe);
        return Err(e.to_string());
    }
    Ok(true)
}

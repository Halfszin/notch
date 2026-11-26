// Orbi em Rust: o notch do Windows. A interface fica em ui/notch.slint.
#![windows_subsystem = "windows"]

slint::include_modules!();

mod color;
mod lyrics;
mod pages;
mod notifications;
mod spectrum;
mod store;
mod taskparse;
#[cfg(windows)]
mod media;
#[cfg(windows)]
mod system;
#[cfg(windows)]
mod winapi;
#[cfg(windows)]
mod features;
#[cfg(windows)]
mod settings;
#[cfg(windows)]
mod updater;

/// Erros vão para %AppData%\Orbi\rust-erro.log (o app não tem console para mostrar).
fn log_error(msg: &str) {
    if let Some(dir) = std::env::var_os("APPDATA").map(|d| std::path::PathBuf::from(d).join("Orbi")) {
        let _ = std::fs::create_dir_all(&dir);
        let _ = std::fs::write(dir.join("rust-erro.log"), msg);
    }
}

fn main() {
    std::panic::set_hook(Box::new(|info| log_error(&format!("pane: {info}"))));
    if let Err(e) = run() {
        log_error(&format!("erro: {e}"));
    }
}

fn run() -> Result<(), slint::PlatformError> {
    // Teste do CI: busca uma letra de verdade no LRCLIB (com álbum e sem álbum) e grava o resultado.
    #[cfg(windows)]
    if std::env::args().any(|a| a == "--testar-letra") {
        let count = |l: Option<Vec<lyrics::Line>>| l.map_or(0, |l| l.len());
        let text = format!(
            "com_album={}\nsem_album={}\nlrclib: {}\nicone_do_explorer={}\nsom_do_windows={}\n",
            count(lyrics::fetch("Coldplay", "Yellow", "Parachutes", 269.0)),
            count(lyrics::fetch("Coldplay", "Yellow", "", 0.0)),
            lyrics::probe(),
            {
                unsafe { let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_APARTMENTTHREADED); }
                winapi::app_icon("Microsoft.Windows.Explorer", 48).map(|(w, h, px)| format!("{w}x{h} visiveis={}", px.chunks(4).filter(|p| p[3] > 0).count())).unwrap_or("nenhum".into())
            },
            std::path::Path::new(&(std::env::var("WINDIR").unwrap_or_default() + "\\Media\\Windows Notify Messaging.wav")).exists(),
        );
        let _ = std::fs::create_dir_all(store::dir());
        let _ = std::fs::write(store::dir().join("rust-letra.txt"), text);
        return Ok(());
    }
    #[cfg(windows)]
    if !settings::claim_instance() { return Ok(()); }
    let notch = Notch::new()?;
    #[cfg(windows)]
    {
        system::setup(&notch);
        media::start(&notch);
        pages::setup(&notch);
        features::setup(&notch);
        settings::setup(&notch);
    }
    notch.run()
}

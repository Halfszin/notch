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

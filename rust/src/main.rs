// Orbi em Rust: o notch do Windows. A interface fica em ui/notch.slint.
#![windows_subsystem = "windows"]

slint::include_modules!();

mod color;
mod lyrics;
mod pages;
mod store;
#[cfg(windows)]
mod media;
#[cfg(windows)]
mod system;

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
    let notch = Notch::new()?;
    #[cfg(windows)]
    {
        system::setup(&notch);
        media::start(&notch);
        pages::setup(&notch);
    }
    notch.run()
}

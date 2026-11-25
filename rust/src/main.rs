// Orbi em Rust: o notch do Windows. A interface fica em ui/notch.slint.
#![windows_subsystem = "windows"]

slint::include_modules!();

mod color;
#[cfg(windows)]
mod media;
#[cfg(windows)]
mod system;

fn main() -> Result<(), slint::PlatformError> {
    let notch = Notch::new()?;
    #[cfg(windows)]
    {
        system::setup(&notch);
        media::start(&notch);
    }
    notch.run()
}

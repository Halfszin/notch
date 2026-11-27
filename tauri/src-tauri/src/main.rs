// Orbi em Tauri: o Rust cuida da janela (transparente, sempre por cima, clique passando quando fechado),
// do mouse e da bateria; a tela (ui/) é HTML/CSS/JS no WebView2 do Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::Serialize;
use std::time::{Duration, Instant};
use tauri::{Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewWindow};

/// Medidas lógicas, iguais às do Orbi em Slint: janela fixa do tamanho do notch aberto.
const WIN_W: f64 = 660.0;
const WIN_H: f64 = 300.0;
const CLOSED: (f64, f64) = (120.0, 30.0);
const OPEN: (f64, f64) = (620.0, 262.0);

#[derive(Serialize, Clone)]
struct Battery { pct: u8, charging: bool, has: bool }

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let win = app.get_webview_window("notch").expect("janela do notch");
            place(&win)?;
            // Fechado, a janela deixa o mouse passar; quem abre é a posição do mouse, lida no Rust.
            win.set_ignore_cursor_events(true)?;
            std::thread::spawn(move || watch(win));
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("erro ao abrir o Orbi");
}

/// Topo da tela, centralizado.
fn place(win: &WebviewWindow) -> tauri::Result<()> {
    let Some(m) = win.current_monitor()? else { return Ok(()) };
    let s = m.scale_factor();
    let (w, h) = ((WIN_W * s) as u32, (WIN_H * s) as u32);
    win.set_size(PhysicalSize::new(w, h))?;
    win.set_position(PhysicalPosition::new(m.position().x + (m.size().width as i32 - w as i32) / 2, m.position().y))?;
    Ok(())
}

/// O notch (centralizado no topo da janela) contém o ponto? Margem de 2 px acima: encostar na borda da tela abre.
pub fn inside(cursor: (f64, f64), win_pos: (f64, f64), win_w: f64, scale: f64, open: bool) -> bool {
    let (pw, ph) = if open { OPEN } else { CLOSED };
    let (pw, ph) = (pw * scale, ph * scale);
    let left = win_pos.0 + (win_w - pw) / 2.0;
    cursor.0 >= left && cursor.0 <= left + pw && cursor.1 >= win_pos.1 - 2.0 && cursor.1 <= win_pos.1 + ph
}

/// A cada 30 ms: mouse em cima abre (e a janela passa a aceitar clique); a cada segundo: bateria e "sempre por cima".
fn watch(win: WebviewWindow) {
    let mut open = false;
    let mut last_slow = Instant::now() - Duration::from_secs(2);
    loop {
        if let (Some(c), Ok(pos), Ok(size), Ok(scale)) = (cursor(), win.outer_position(), win.outer_size(), win.scale_factor()) {
            let now_open = inside(c, (pos.x as f64, pos.y as f64), size.width as f64, scale, open);
            if now_open != open {
                open = now_open;
                let _ = win.set_ignore_cursor_events(!open);
                let _ = win.emit("notch", open);
            }
        }
        if last_slow.elapsed() >= Duration::from_secs(1) {
            last_slow = Instant::now();
            let _ = win.emit("battery", battery());
            let _ = win.set_always_on_top(true);
        }
        std::thread::sleep(Duration::from_millis(30));
    }
}

#[cfg(windows)]
fn cursor() -> Option<(f64, f64)> {
    let mut p = windows::Win32::Foundation::POINT::default();
    unsafe { windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut p) }.ok()?;
    Some((p.x as f64, p.y as f64))
}

#[cfg(windows)]
fn battery() -> Battery {
    let mut s = windows::Win32::System::Power::SYSTEM_POWER_STATUS::default();
    if unsafe { windows::Win32::System::Power::GetSystemPowerStatus(&mut s) }.is_err() || s.BatteryLifePercent > 100 {
        return Battery { pct: 100, charging: false, has: false }; // 255 = PC sem bateria
    }
    Battery { pct: s.BatteryLifePercent, charging: s.ACLineStatus == 1, has: true }
}

#[cfg(not(windows))]
fn cursor() -> Option<(f64, f64)> { None }
#[cfg(not(windows))]
fn battery() -> Battery { Battery { pct: 100, charging: false, has: false } }

#[cfg(test)]
mod tests {
    use super::inside;

    #[test]
    fn area_do_notch() {
        // Janela de 660 px em x=630 (tela de 1920): notch fechado de 120 px vai de 900 a 1020.
        assert!(inside((960.0, 10.0), (630.0, 0.0), 660.0, 1.0, false));
        assert!(!inside((880.0, 10.0), (630.0, 0.0), 660.0, 1.0, false));
        assert!(!inside((960.0, 40.0), (630.0, 0.0), 660.0, 1.0, false));
        // Aberto, a área cresce para 620 x 262.
        assert!(inside((700.0, 200.0), (630.0, 0.0), 660.0, 1.0, true));
        // Escala 150%: tudo multiplica.
        assert!(inside((1440.0, 40.0), (945.0, 0.0), 990.0, 1.5, false));
        assert!(!inside((1440.0, 50.0), (945.0, 0.0), 990.0, 1.5, false));
    }
}

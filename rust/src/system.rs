// Janela, hora, bateria e mouse: o que o Windows sabe e o notch mostra.
use slint::winit_030::{winit, WinitWindowAccessor};
use slint::{ComponentHandle, Timer, TimerMode};
use std::time::Duration;
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
use windows::Win32::UI::WindowsAndMessaging::{
    GetCursorPos, GetSystemMetrics, GetWindowLongPtrW, SetWindowLongPtrW, SetWindowPos, GWL_EXSTYLE, HWND_TOPMOST,
    SM_CXSCREEN, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
};

use crate::Notch;

pub fn setup(notch: &Notch) {
    // Fora da barra de tarefas e do Alt+Tab, sem roubar o foco ao clicar; centralizado no topo.
    let weak = notch.as_weak();
    slint::invoke_from_event_loop(move || {
        let Some(n) = weak.upgrade() else { return };
        n.window().with_winit_window(|w: &winit::window::Window| {
            if let Some(hwnd) = hwnd_of(w) {
                unsafe {
                    let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
                    SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW.0 as isize | WS_EX_NOACTIVATE.0 as isize);
                }
            }
            let screen_w = unsafe { GetSystemMetrics(SM_CXSCREEN) };
            let size = w.outer_size();
            w.set_outer_position(winit::dpi::PhysicalPosition::new((screen_w - size.width as i32) / 2, 0));
        });
    })
    .ok();

    // A cada 30 ms: o clique só vale em cima do notch (o resto da janela deixa passar) e os olhos seguem o mouse.
    let weak = notch.as_weak();
    let fast = Timer::default();
    fast.start(TimerMode::Repeated, Duration::from_millis(30), move || {
        let Some(n) = weak.upgrade() else { return };
        n.window().with_winit_window(|w: &winit::window::Window| {
            let scale = w.scale_factor();
            let Ok(pos) = w.outer_position() else { return };
            let size = w.outer_size();
            let mut cursor = POINT::default();
            if unsafe { GetCursorPos(&mut cursor) }.is_err() { return; }
            let (pw, ph) = if n.get_open() {
                (n.get_open_width() as f64 * scale, n.get_open_height() as f64 * scale)
            } else {
                (n.get_closed_width() as f64 * scale, n.get_closed_height() as f64 * scale)
            };
            let left = pos.x as f64 + (size.width as f64 - pw) / 2.0;
            let (cx, cy) = (cursor.x as f64, cursor.y as f64);
            let inside = cx >= left && cx <= left + pw && cy >= pos.y as f64 && cy <= pos.y as f64 + ph;
            let _ = w.set_cursor_hittest(inside);
            // Centro aproximado do avatar (lado esquerdo do notch aberto).
            let ax = left + 18.0 * scale + 75.0 * scale;
            let ay = pos.y as f64 + 120.0 * scale;
            n.set_look_x(((cx - ax) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
            n.set_look_y(((ay - cy) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
        });
    });
    std::mem::forget(fast); // o timer vive enquanto o app viver

    // A cada segundo: hora, data, bateria e "sempre por cima" (o Windows às vezes tira, ex.: Alt+Tab).
    let weak = notch.as_weak();
    let slow = Timer::default();
    let tick = move || {
        let Some(n) = weak.upgrade() else { return };
        let (clock, date) = now_text();
        n.set_clock(clock.into());
        n.set_date_text(date.into());
        let mut p = SYSTEM_POWER_STATUS::default();
        if unsafe { GetSystemPowerStatus(&mut p) }.is_ok() {
            let has = p.BatteryLifePercent <= 100; // 255 = PC sem bateria
            n.set_has_battery(has);
            n.set_battery(if has { p.BatteryLifePercent as f32 / 100.0 } else { 1.0 });
            n.set_charging(has && p.ACLineStatus == 1);
        }
        n.window().with_winit_window(|w: &winit::window::Window| {
            if let Some(hwnd) = hwnd_of(w) {
                unsafe { let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            }
        });
    };
    tick();
    slow.start(TimerMode::Repeated, Duration::from_secs(1), tick);
    std::mem::forget(slow);
}

fn hwnd_of(w: &winit::window::Window) -> Option<HWND> {
    use winit::raw_window_handle::{HasWindowHandle, RawWindowHandle};
    match w.window_handle().ok()?.as_raw() {
        RawWindowHandle::Win32(h) => Some(HWND(h.hwnd.get() as *mut _)),
        _ => None,
    }
}

const WEEKDAYS: [&str; 7] = ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];
const MONTHS: [&str; 12] = ["jan", "fev", "mar", "abr", "mai", "jun", "jul", "ago", "set", "out", "nov", "dez"];

fn now_text() -> (String, String) {
    let t = unsafe { windows::Win32::System::SystemInformation::GetLocalTime() };
    (
        format!("{:02}:{:02}", t.wHour, t.wMinute),
        format!("{} {} {} {}", WEEKDAYS[t.wDayOfWeek as usize % 7], t.wDay, MONTHS[(t.wMonth as usize + 11) % 12], t.wYear),
    )
}

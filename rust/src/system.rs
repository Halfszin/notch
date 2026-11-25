// Janela, hora, bateria e mouse: o que o Windows sabe e o notch mostra.
use slint::winit_030::{winit, WinitWindowAccessor};
use winit::platform::windows::WindowExtWindows;
use std::sync::atomic::{AtomicU32, Ordering::Relaxed};
use slint::{ComponentHandle, Timer, TimerMode};
use std::time::Duration;
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
use windows::Win32::UI::WindowsAndMessaging::{
    GetCursorPos, GetSystemMetrics, GetWindowLongPtrW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, GWL_EXSTYLE, HWND_TOPMOST,
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
            w.set_skip_taskbar(true);
            let screen_w = unsafe { GetSystemMetrics(SM_CXSCREEN) };
            let size = w.outer_size();
            w.set_outer_position(winit::dpi::PhysicalPosition::new((screen_w - size.width as i32) / 2, 0));
        });
    })
    .ok();

    notch.on_quit(|| { let _ = slint::quit_event_loop(); });
    // Fechar pela barra de tarefas/Alt+F4 não derruba o notch; sair é pelo X do notch ou nas configurações.
    notch.window().on_close_requested(|| slint::CloseRequestResponse::KeepWindowShown);

    // Diagnóstico (rust-diag.txt): escala, tela e onde a janela ficou — para entender PCs onde o notch não abre.
    let weak = notch.as_weak();
    let diag = Timer::default();
    diag.start(TimerMode::SingleShot, Duration::from_secs(3), move || {
        let Some(n) = weak.upgrade() else { return };
        n.window().with_winit_window(|w: &winit::window::Window| {
            let screen = unsafe { (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_CYSCREEN)) };
            let text = format!(
                "escala={}\ntela={}x{}\njanela_pos={:?}\njanela_tam={:?}\nnotch_fechado={}x{}\n",
                w.scale_factor(), screen.0, screen.1, w.outer_position(), w.outer_size(),
                n.get_closed_width(), n.get_closed_height()
            );
            let _ = std::fs::create_dir_all(crate::store::dir());
            let _ = std::fs::write(crate::store::dir().join("rust-diag.txt"), text);
        });
    });
    std::mem::forget(diag);

    // Digitar uma tarefa: a janela passa a aceitar teclado só enquanto você digita (normalmente não rouba o foco).
    let weak = notch.as_weak();
    notch.on_want_keyboard(move || {
        let Some(n) = weak.upgrade() else { return };
        n.window().with_winit_window(|w: &winit::window::Window| {
            if let Some(hwnd) = hwnd_of(w) {
                set_noactivate(hwnd, false);
                unsafe { let _ = SetForegroundWindow(hwnd); }
            }
        });
    });

    // A cada 30 ms: abre quando o mouse está sobre o notch; a janela cresce ao abrir e encolhe
    // (depois da animação de fechar) ao fechar. Os olhos seguem o mouse.
    let weak = notch.as_weak();
    let big = std::rc::Rc::new(std::cell::Cell::new(true));
    let size_now = std::rc::Rc::new(std::cell::Cell::new((n_big_w(notch), n_big_h(notch))));
    let close_at: std::rc::Rc<std::cell::Cell<Option<std::time::Instant>>> = Default::default();
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
            // Margem de 2 px no topo da tela: encostar o mouse na borda também abre.
            let inside = cx >= left && cx <= left + pw && cy >= pos.y as f64 - 2.0 && cy <= pos.y as f64 + ph;
            n.set_hovered(inside);

            let open = n.get_open();
            let (lw, lh) = if open || close_at.get().is_some_and(|t| std::time::Instant::now() < t) {
                (n.get_big_width(), n.get_big_height())
            } else if n.get_toasting() {
                (n.get_toast_width(), n.get_toast_height())
            } else {
                (n.get_closed_width(), n.get_closed_height())
            };
            if open {
                close_at.set(None);
            } else if big.get() && close_at.get().is_none() {
                close_at.set(Some(std::time::Instant::now() + Duration::from_millis(450))); // deixa a animação de fechar terminar
            }
            let want_big = lw >= n.get_big_width();
            let changed = (lw, lh) != size_now.get();
            if changed {
                size_now.set((lw, lh));
                big.set(want_big);
                if !want_big { close_at.set(None); }
                let screen_w = unsafe { GetSystemMetrics(SM_CXSCREEN) } as f64;
                let _ = w.request_inner_size(winit::dpi::LogicalSize::new(lw as f64, lh as f64));
                w.set_outer_position(winit::dpi::PhysicalPosition::new(((screen_w - lw as f64 * scale) / 2.0) as i32, 0));
            }

            // Notas abertas: a janela aceita teclado (clicar no texto já deixa digitar).
            // Notch fechou: volta a não roubar o foco.
            if let Some(hwnd) = hwnd_of(w) {
                if open && n.get_page() == 4 { set_noactivate(hwnd, false); }
                else if !open { set_noactivate(hwnd, true); }
            }
            // Centro aproximado do avatar (lado esquerdo do notch aberto).
            let ax = left + 46.0 * scale + 70.0 * scale;
            let ay = pos.y as f64 + 120.0 * scale;
            n.set_look_x(((cx - ax) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
            n.set_look_y(((ay - cy) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
        });
    });
    std::mem::forget(fast); // o timer vive enquanto o app viver

    // Volume do som agora: as barrinhas do notch fechado e o avatar pulam com a música.
    static PEAK: AtomicU32 = AtomicU32::new(0);
    std::thread::spawn(|| {
        let mut meter = None;
        for i in 0u64.. {
            if i % 100 == 0 { meter = crate::winapi::peak_meter(); } // a cada 3 s: pega a saída de som atual
            let v = meter.as_ref().and_then(|m| unsafe { m.GetPeakValue() }.ok()).unwrap_or(0.0);
            PEAK.store(v.to_bits(), Relaxed);
            std::thread::sleep(Duration::from_millis(30));
        }
    });
    let weak = notch.as_weak();
    let level = std::rc::Rc::new(std::cell::Cell::new(0f32));
    let beat = Timer::default();
    beat.start(TimerMode::Repeated, Duration::from_millis(30), move || {
        let Some(n) = weak.upgrade() else { return };
        let target = (f32::from_bits(PEAK.load(Relaxed)) * 1.4).min(1.0);
        let now = level.get();
        // Sobe rápido e desce suave (como no Orbi em C#).
        let next = if target > now { now + (target - now) * 0.6 } else { now * 0.85 };
        level.set(next);
        let shown = if n.get_playing() { next } else { 0.0 };
        if (shown - n.get_level()).abs() > 0.005 { n.set_level(shown); }
    });
    std::mem::forget(beat);

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
            // O botão na barra de tarefas às vezes volta (ex.: quando outra janela do Orbi abre).
            w.set_skip_taskbar(true);
        });
    };
    tick();
    slow.start(TimerMode::Repeated, Duration::from_secs(1), tick);
    std::mem::forget(slow);
}

fn n_big_w(n: &Notch) -> f32 { n.get_big_width() }
fn n_big_h(n: &Notch) -> f32 { n.get_big_height() }

fn set_noactivate(hwnd: HWND, on: bool) {
    unsafe {
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let flag = WS_EX_NOACTIVATE.0 as isize;
        let want = if on { ex | flag } else { ex & !flag };
        if want != ex { SetWindowLongPtrW(hwnd, GWL_EXSTYLE, want); }
    }
}

/// Agora, no relógio do PC (para lembretes e tarefas com hora).
pub fn now() -> crate::taskparse::When {
    let t = unsafe { windows::Win32::System::SystemInformation::GetLocalTime() };
    crate::taskparse::When { year: t.wYear as i32, month: t.wMonth as u32, day: t.wDay as u32, hour: t.wHour as u32, minute: t.wMinute as u32 }
}

pub fn hwnd_of(w: &winit::window::Window) -> Option<HWND> {
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

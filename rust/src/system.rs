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
    // Arrastar pelas bordas (como no Orbi em C#): segurou o fundo do notch aberto e moveu mais de 8 px,
    // ele fecha e corre pela borda mais perto do cursor; ao soltar, guarda onde ficou.
    let drag_from: std::rc::Rc<std::cell::Cell<Option<(i32, i32)>>> = Default::default();
    let d = drag_from.clone();
    notch.on_drag_start(move || {
        let mut c = POINT::default();
        if unsafe { GetCursorPos(&mut c) }.is_ok() { d.set(Some((c.x, c.y))); }
    });
    let (d, weak) = (drag_from.clone(), notch.as_weak());
    notch.on_drag_move(move || {
        let (Some(n), Some((fx, fy))) = (weak.upgrade(), d.get()) else { return };
        let mut c = POINT::default();
        if unsafe { GetCursorPos(&mut c) }.is_err() { return; }
        if !n.get_dragging() && ((c.x - fx).pow(2) + (c.y - fy).pow(2)) < 64 { return; } // clique normal não arrasta
        n.set_dragging(true);
        let (sw, sh) = unsafe { (GetSystemMetrics(SM_CXSCREEN) as f32, GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_CYSCREEN) as f32) };
        let (edge, pos) = nearest_edge(c.x as f32, c.y as f32, sw, sh);
        n.set_edge(edge);
        n.set_edge_pos(pos);
    });
    let (d, weak) = (drag_from, notch.as_weak());
    notch.on_drag_end(move || {
        d.set(None);
        let Some(n) = weak.upgrade() else { return };
        if n.get_dragging() {
            n.set_dragging(false);
            crate::settings::save_edge(n.get_edge(), n.get_edge_pos());
        }
    });

    let weak = notch.as_weak();
    let hit = std::rc::Rc::new(std::cell::Cell::new(true));
    let placed: std::rc::Rc<std::cell::Cell<Option<(i32, f32)>>> = Default::default();
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
            let edge = n.get_edge();
            // Onde o notch está dentro da janela (encostado na borda dela).
            let (sw_win, sh_win) = (size.width as f64, size.height as f64);
            let left = pos.x as f64 + match edge { 1 => 0.0, 2 => sw_win - pw, _ => (sw_win - pw) / 2.0 };
            let top = pos.y as f64 + if edge == 0 { 0.0 } else { (sh_win - ph) / 2.0 };
            let (cx, cy) = (cursor.x as f64, cursor.y as f64);
            // Margem de 2 px do lado da borda da tela: encostar o mouse na borda também abre.
            let (ml, mr, mt) = match edge { 1 => (2.0, 0.0, 0.0), 2 => (0.0, 2.0, 0.0), _ => (0.0, 0.0, 2.0) };
            let inside = cx >= left - ml && cx <= left + pw + mr && cy >= top - mt && cy <= top + ph;
            n.set_hovered(inside);

            let open = n.get_open();
            // A janela fica sempre do tamanho do notch aberto (mudar o tamanho dela no meio da animação
            // piscava e fazia o notch escorregar). Fechado, ela deixa o mouse passar para o que está embaixo;
            // quem abre é a posição do mouse, lida aqui, então não precisa de eventos da janela.
            let clickable = open || n.get_dragging();
            if clickable != hit.get() {
                hit.set(clickable);
                let _ = w.set_cursor_hittest(clickable);
            }
            let now_state = (edge, n.get_edge_pos());
            if Some(now_state) != placed.get() {
                placed.set(Some(now_state));
                let (sw, sh) = unsafe { (GetSystemMetrics(SM_CXSCREEN) as f64, GetSystemMetrics(windows::Win32::UI::WindowsAndMessaging::SM_CYSCREEN) as f64) };
                let (bw, bh) = (n.get_big_width() as f64 * scale, n.get_big_height() as f64 * scale);
                let _ = w.request_inner_size(winit::dpi::LogicalSize::new(n.get_big_width() as f64, n.get_big_height() as f64));
                let (x, y) = window_pos(edge, n.get_edge_pos() as f64, bw, bh, sw, sh);
                w.set_outer_position(winit::dpi::PhysicalPosition::new(x as i32, y as i32));
            }

            // Notas abertas: a janela aceita teclado (clicar no texto já deixa digitar).
            // Notch fechou: volta a não roubar o foco.
            if let Some(hwnd) = hwnd_of(w) {
                if open && n.get_page() == 4 { set_noactivate(hwnd, false); }
                else if !open { set_noactivate(hwnd, true); }
            }
            // Centro aproximado do avatar (lado esquerdo do notch aberto).
            let ax = left + 46.0 * scale + 70.0 * scale;
            let ay = top + 120.0 * scale;
            n.set_look_x(((cx - ax) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
            n.set_look_y(((ay - cy) / (250.0 * scale)).clamp(-1.0, 1.0) as f32);
        });
    });
    std::mem::forget(fast); // o timer vive enquanto o app viver

    // Visualizador (como o Wallpaper Engine): 4 faixas do som que está saindo; barrinhas do notch fechado,
    // pulso do notch e do avatar no grave. Sem captura (formato estranho, sem saída): medidor de volume.
    static BANDS: [AtomicU32; crate::spectrum::BANDS] = [const { AtomicU32::new(0) }; crate::spectrum::BANDS];
    std::thread::spawn(|| loop {
        let mut ring: std::collections::VecDeque<f32> = std::collections::VecDeque::with_capacity(crate::spectrum::N * 2);
        let mut norm = crate::spectrum::Normalizer::new();
        let mut quiet = std::time::Instant::now();
        let ok = crate::winapi::loopback(|samples, rate| {
            if samples.is_empty() {
                // Nada tocando: o Windows não manda pacotes; completa com silêncio para as barras caírem.
                if quiet.elapsed().as_millis() < 100 { return; }
                ring.extend(std::iter::repeat_n(0.0, (rate * 0.015) as usize));
            } else {
                quiet = std::time::Instant::now();
                ring.extend(samples);
            }
            while ring.len() > crate::spectrum::N { ring.pop_front(); }
            if ring.len() == crate::spectrum::N {
                let b = norm.apply(crate::spectrum::bands(ring.make_contiguous(), rate));
                for (slot, v) in BANDS.iter().zip(b) { slot.store(v.to_bits(), Relaxed); }
            }
        });
        if ok.is_err() {
            let meter = crate::winapi::peak_meter();
            for _ in 0..100 {
                let v = meter.as_ref().and_then(|m| unsafe { m.GetPeakValue() }.ok()).unwrap_or(0.0);
                for slot in &BANDS { slot.store((v * 1.4).min(1.0).to_bits(), Relaxed); }
                std::thread::sleep(Duration::from_millis(30));
            }
        }
    });
    let weak = notch.as_weak();
    let shown = std::rc::Rc::new(std::cell::Cell::new([0f32; crate::spectrum::BANDS]));
    let detector = std::rc::Rc::new(std::cell::RefCell::new(crate::spectrum::BeatDetector::new()));
    let started = std::time::Instant::now();
    let beat = Timer::default();
    // ~60 vezes por segundo, como no Orbi em C# (o detector de batida foi calibrado nesse ritmo).
    beat.start(TimerMode::Repeated, Duration::from_millis(16), move || {
        let Some(n) = weak.upgrade() else { return };
        if !n.get_has_media() { return; }
        let playing = n.get_playing();
        let mut cur = shown.get();
        for (c, slot) in cur.iter_mut().zip(&BANDS) {
            let target = if playing { f32::from_bits(slot.load(Relaxed)) } else { 0.0 };
            // Sobe rápido e desce suave, como os visualizadores do Wallpaper Engine.
            *c = if target > *c { *c + (target - *c) * 0.6 } else { *c * 0.85 };
        }
        shown.set(cur);
        let bass = if playing { f32::from_bits(BANDS[0].load(Relaxed)) } else { 0.0 };
        let pulse = detector.borrow_mut().process(bass, started.elapsed().as_millis() as i64);
        n.set_bands(slint::ModelRc::new(slint::VecModel::from(cur.to_vec())));
        n.set_level(cur[0]);
        n.set_pulse(if playing { pulse } else { 0.0 });
    });
    std::mem::forget(beat);

    // A cada segundo: hora, data, bateria e "sempre por cima" (o Windows às vezes tira, ex.: Alt+Tab).
    let weak = notch.as_weak();
    let slow = Timer::default();
    let tick = move || {
        let Some(n) = weak.upgrade() else { return };
        let (clock, date) = now_text();
        n.set_clock(clock.into());
        // Nas laterais a hora fica em pé ("19" em cima de "54").
        n.set_side_text(n.get_closed_text().replace(':', "\n").into());
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


fn set_noactivate(hwnd: HWND, on: bool) {
    unsafe {
        let ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        let flag = WS_EX_NOACTIVATE.0 as isize;
        let want = if on { ex | flag } else { ex & !flag };
        if want != ex { SetWindowLongPtrW(hwnd, GWL_EXSTYLE, want); }
    }
}

/// Borda mais perto do cursor (0 = topo, 1 = esquerda, 2 = direita) e onde nela (0 a 1), como no Orbi em C#.
pub fn nearest_edge(x: f32, y: f32, sw: f32, sh: f32) -> (i32, f32) {
    if y < x.min(sw - x) { (0, (x / sw).clamp(0.0, 1.0)) }
    else if x < sw - x { (1, (y / sh).clamp(0.0, 1.0)) }
    else { (2, (y / sh).clamp(0.0, 1.0)) }
}

/// Canto da janela para o notch ficar encostado na borda, centrado em `pos`, sem sair da tela.
pub fn window_pos(edge: i32, pos: f64, w: f64, h: f64, sw: f64, sh: f64) -> (f64, f64) {
    let along = |p: f64, size: f64, screen: f64| (p * screen - size / 2.0).clamp(0.0, (screen - size).max(0.0));
    match edge {
        1 => (0.0, along(pos, h, sh)),
        2 => (sw - w, along(pos, h, sh)),
        _ => (along(pos, w, sw), 0.0),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn borda_mais_perto() {
        assert_eq!(nearest_edge(960.0, 5.0, 1920.0, 1080.0).0, 0);
        assert_eq!(nearest_edge(10.0, 500.0, 1920.0, 1080.0), (1, 500.0 / 1080.0));
        assert_eq!(nearest_edge(1915.0, 540.0, 1920.0, 1080.0), (2, 0.5));
    }

    #[test]
    fn janela_nao_sai_da_tela() {
        assert_eq!(window_pos(0, 0.5, 200.0, 40.0, 1920.0, 1080.0), (860.0, 0.0));
        assert_eq!(window_pos(0, 0.0, 200.0, 40.0, 1920.0, 1080.0), (0.0, 0.0));
        assert_eq!(window_pos(2, 1.0, 40.0, 120.0, 1920.0, 1080.0), (1880.0, 960.0));
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

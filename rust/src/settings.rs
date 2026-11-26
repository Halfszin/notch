// Preferências da versão em Rust (%AppData%\Orbi\rust-config.json; o config.json é do Orbi em C#)
// e a janela de Configurações, aberta pela engrenagem do notch.
use serde::{Deserialize, Serialize};
use slint::winit_030::WinitWindowAccessor;
use slint::{ComponentHandle, Weak};
use std::cell::RefCell;
use std::sync::atomic::Ordering;
use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{GetLastError, ERROR_ALREADY_EXISTS, HANDLE};
use windows::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject, INFINITE};
use windows::Win32::System::Registry::{RegDeleteKeyValueW, RegGetValueW, RegSetKeyValueW, HKEY_CURRENT_USER, REG_SZ, RRF_RT_REG_SZ};

use crate::{updater, Notch, Settings};

#[derive(Serialize, Deserialize, Clone)]
#[serde(default)]
pub struct Prefs {
    pub battery_alerts: bool,
    pub audio_alerts: bool,
    pub reminders: bool,
    pub lyrics: bool,
    pub gradient: bool,
    pub auto_update: bool,
    pub compact: bool,
    /// Borda (0 = topo, 1 = esquerda, 2 = direita) e onde nela (0 a 1), como no Orbi em C#.
    pub edge: i32,
    pub edge_pos: f32,
    /// Modo jogo (ligado pelo botão do próprio notch).
    pub game_mode: bool,
    /// WhatsApp e Discord no notch, e sons dos avisos.
    pub app_notifications: bool,
    pub sounds: bool,
    /// Páginas na ordem das bolinhas (id = posição na lista PAGES) e se aparecem.
    pub pages: Vec<PageCfg>,
}

#[derive(Serialize, Deserialize, Clone, PartialEq, Debug)]
pub struct PageCfg {
    pub id: usize,
    pub visible: bool,
}

/// Nomes das páginas, na ordem dos ids da interface (titles do notch.slint).
const PAGES: [&str; 8] = ["Tocando", "Tarefas", "Foco", "Calendário", "Notas", "Copiados", "Sistema", "Atalhos"];

impl Default for Prefs {
    fn default() -> Self {
        Prefs {
            battery_alerts: true, audio_alerts: true, reminders: true, lyrics: true, gradient: true, auto_update: true, compact: false, edge: 0, edge_pos: 0.5, game_mode: false, app_notifications: true, sounds: true,
            pages: (0..PAGES.len()).map(|id| PageCfg { id, visible: true }).collect(),
        }
    }
}

/// Arquivo editado à mão ou de versão antiga: tira ids repetidos/inválidos, põe os que faltam no fim e
/// deixa a página da música sempre visível (o notch nunca fica sem página).
pub fn fix_pages(pages: &[PageCfg]) -> Vec<PageCfg> {
    let mut out: Vec<PageCfg> = Vec::new();
    for p in pages {
        if p.id < PAGES.len() && !out.iter().any(|o| o.id == p.id) { out.push(PageCfg { id: p.id, visible: p.visible || p.id == 0 }); }
    }
    for id in 0..PAGES.len() {
        if !out.iter().any(|o| o.id == id) { out.push(PageCfg { id, visible: true }); }
    }
    out
}

const FILE: &str = "rust-config.json";

thread_local! {
    static PREFS: RefCell<Prefs> = RefCell::new({
        let mut p: Prefs = crate::store::load(FILE);
        p.pages = fix_pages(&p.pages);
        p
    });
    static NOTCH: RefCell<Option<Weak<Notch>>> = const { RefCell::new(None) };
    static WINDOW: RefCell<Option<Settings>> = const { RefCell::new(None) };
    static STATUS: RefCell<(String, bool)> = RefCell::new((String::new(), false));
}

/// Preferências atuais (só na thread da interface).
pub fn get() -> Prefs {
    PREFS.with(|p| p.borrow().clone())
}

pub fn setup(notch: &Notch) {
    let prefs = get();
    notch.set_gradient_enabled(prefs.gradient);
    notch.set_compact(prefs.compact);
    notch.set_edge(prefs.edge.clamp(0, 2));
    notch.set_edge_pos(prefs.edge_pos.clamp(0.0, 1.0));
    notch.set_game_mode(prefs.game_mode);
    notch.set_lyrics_on(prefs.lyrics);
    let weak = notch.as_weak();
    notch.on_lyrics_toggled(move || {
        let Some(n) = weak.upgrade() else { return };
        let mut prefs = get();
        prefs.lyrics = n.get_lyrics_on();
        if !prefs.lyrics { n.set_has_lyrics(false); }
        crate::store::save(FILE, &prefs);
        WINDOW.with(|w| if let Some(w) = &*w.borrow() { w.set_lyrics(prefs.lyrics); });
        PREFS.with(|p| *p.borrow_mut() = prefs);
    });
    let weak = notch.as_weak();
    notch.on_game_toggled(move || {
        let Some(n) = weak.upgrade() else { return };
        let mut prefs = get();
        prefs.game_mode = n.get_game_mode();
        crate::store::save(FILE, &prefs);
        PREFS.with(|p| *p.borrow_mut() = prefs);
    });
    apply_pages(notch, &prefs.pages);
    updater::AUTO.store(prefs.auto_update, Ordering::Relaxed);
    NOTCH.with(|n| *n.borrow_mut() = Some(notch.as_weak()));
    notch.on_open_settings(show);
    updater::start();
    // Como no Orbi em C#: abrir pelo ícone mostra as configurações; ao ligar o PC (--startup), só o notch.
    if !std::env::args().any(|a| a == "--startup") {
        let _ = slint::invoke_from_event_loop(show);
    }
}

/// Soltou o notch depois de arrastar: guarda a borda e o lugar.
pub fn save_edge(edge: i32, pos: f32) {
    let mut prefs = get();
    prefs.edge = edge;
    prefs.edge_pos = pos;
    crate::store::save(FILE, &prefs);
    WINDOW.with(|w| if let Some(w) = &*w.borrow() { w.set_edge(edge); });
    PREFS.with(|p| *p.borrow_mut() = prefs);
}

/// O atualizador conta o que está fazendo; quando a versão nova fica pronta, o notch avisa.
pub fn on_update(text: String, ready: bool) {
    STATUS.with(|s| *s.borrow_mut() = (text.clone(), ready));
    WINDOW.with(|w| {
        if let Some(w) = &*w.borrow() {
            w.set_update_status(text.into());
            w.set_update_ready(ready);
        }
    });
    if ready {
        if let Some(n) = NOTCH.with(|n| n.borrow().as_ref().and_then(|w| w.upgrade())) {
            crate::features::toast(&n, "\u{E895}", "Orbi atualizado · reinicie nas configurações", slint::Color::from_rgb_u8(0x30, 0xD1, 0x58), 8);
        }
    }
}

/// Uma cópia só, como no Orbi em C#: abrir o app de novo só mostra as configurações da que já está aberta.
/// Devolve false quando outra cópia já está rodando (e já foi avisada).
pub fn claim_instance() -> bool {
    let after_update = std::env::args().any(|a| a == "--after-update");
    // Depois de atualizar, a cópia velha ainda está fechando: espera ela soltar o nome (até 10 s).
    for _ in 0..if after_update { 100 } else { 1 } {
        let Ok(h) = (unsafe { CreateEventW(None, false, false, w!("Local\\OrbiRust.ShowSettings")) }) else { return true };
        if unsafe { GetLastError() } != ERROR_ALREADY_EXISTS {
            let raw = h.0 as isize; // HANDLE não é Send; o número é
            std::thread::spawn(move || loop {
                unsafe { WaitForSingleObject(HANDLE(raw as *mut _), INFINITE) };
                let _ = slint::invoke_from_event_loop(show);
            });
            return true;
        }
        if !after_update {
            // Esta cópia acabou de ser aberta (tem o foco): deixa a outra trazer as configurações para a frente.
            unsafe {
                let _ = windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(windows::Win32::UI::WindowsAndMessaging::ASFW_ANY);
                let _ = SetEvent(h);
            }
        }
        unsafe { let _ = windows::Win32::Foundation::CloseHandle(h); }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
    false
}

pub fn show() {
    WINDOW.with(|cell| {
        let mut cell = cell.borrow_mut();
        // Fechada pelo X: cria de novo, como na primeira vez (reabrir a mesma janela escondida falhava).
        if cell.as_ref().is_some_and(|w| !w.window().is_visible()) { *cell = None; }
        if cell.is_none() {
            let Ok(w) = Settings::new() else { return };
            let weak = w.as_weak();
            w.on_changed(move || {
                let Some(w) = weak.upgrade() else { return };
                let prefs = Prefs {
                    battery_alerts: w.get_battery_alerts(),
                    audio_alerts: w.get_audio_alerts(),
                    reminders: w.get_reminders(),
                    lyrics: w.get_lyrics(),
                    gradient: w.get_gradient(),
                    auto_update: w.get_auto_update(),
                    compact: w.get_compact(),
                    // Trocou de borda nas configurações: começa no meio dela.
                    edge: w.get_edge(),
                    edge_pos: if w.get_edge() == get().edge { get().edge_pos } else { 0.5 },
                    game_mode: get().game_mode,
                    app_notifications: w.get_app_notifications(),
                    sounds: w.get_sounds(),
                    pages: get().pages,
                };
                crate::store::save(FILE, &prefs);
                updater::AUTO.store(prefs.auto_update, Ordering::Relaxed);
                if let Some(n) = NOTCH.with(|n| n.borrow().as_ref().and_then(|w| w.upgrade())) {
                    n.set_gradient_enabled(prefs.gradient);
                    n.set_compact(prefs.compact);
                    n.set_edge(prefs.edge);
                    n.set_edge_pos(prefs.edge_pos);
                    n.set_lyrics_on(prefs.lyrics);
                    if !prefs.lyrics { n.set_has_lyrics(false); }
                }
                PREFS.with(|p| *p.borrow_mut() = prefs);
                if w.get_start_with_windows() != autostart() {
                    set_autostart(w.get_start_with_windows());
                    w.set_start_with_windows(autostart());
                }
            });
            let weak = w.as_weak();
            w.on_page_toggle(move |i| edit_pages(&weak, |p| {
                if let Some(x) = p.get_mut(i as usize) { if x.id != 0 { x.visible = !x.visible; } }
            }));
            let weak = w.as_weak();
            w.on_page_move(move |i, dir| edit_pages(&weak, |p| {
                let (a, b) = (i as usize, (i + dir) as usize);
                if a < p.len() && b < p.len() { p.swap(a, b); }
            }));
            w.on_check_update(updater::check_now);
            w.on_restart_update(updater::restart);
            w.on_quit(|| { let _ = slint::quit_event_loop(); });
            w.on_open_folder(|| {
                let _ = std::fs::create_dir_all(crate::store::dir());
                crate::winapi::open(&crate::store::dir().to_string_lossy());
            });
            w.set_version(updater::version().into());
            *cell = Some(w);
        }
        let Some(w) = &*cell else { return };
        let p = get();
        w.set_battery_alerts(p.battery_alerts);
        w.set_audio_alerts(p.audio_alerts);
        w.set_reminders(p.reminders);
        w.set_lyrics(p.lyrics);
        w.set_gradient(p.gradient);
        w.set_auto_update(p.auto_update);
        w.set_compact(p.compact);
        w.set_edge(p.edge);
        w.set_app_notifications(p.app_notifications);
        w.set_sounds(p.sounds);
        set_page_list(w, &p.pages);
        w.set_start_with_windows(autostart());
        STATUS.with(|s| {
            let s = s.borrow();
            w.set_update_status(if s.0.is_empty() { "Ainda não procurou versão nova.".into() } else { s.0.clone().into() });
            w.set_update_ready(s.1);
        });
        let _ = w.show();
        // Traz para a frente: aberta pelo notch (que não pega foco) ela podia ficar atrás das outras janelas.
        w.window().with_winit_window(|ww: &slint::winit_030::winit::window::Window| {
            ww.set_minimized(false);
            ww.focus_window();
            if let Some(h) = crate::system::hwnd_of(ww) {
                unsafe { let _ = windows::Win32::UI::WindowsAndMessaging::SetForegroundWindow(h); }
            }
        });
    });
}

// ---------- páginas do notch ----------

fn set_page_list(w: &Settings, pages: &[PageCfg]) {
    let list: Vec<crate::PageEntry> = pages.iter()
        .map(|p| crate::PageEntry { name: PAGES[p.id].into(), visible: p.visible, fixed: p.id == 0 })
        .collect();
    w.set_page_list(slint::ModelRc::new(slint::VecModel::from(list)));
}

/// Manda a ordem para o notch, ficando na mesma página se ela ainda aparece.
fn apply_pages(n: &Notch, pages: &[PageCfg]) {
    let current = n.get_page();
    let order: Vec<i32> = pages.iter().filter(|p| p.visible).map(|p| p.id as i32).collect();
    n.set_pos(order.iter().position(|&id| id == current).unwrap_or(0) as i32);
    n.set_order(slint::ModelRc::new(slint::VecModel::from(order)));
}

fn edit_pages(weak: &Weak<Settings>, change: impl FnOnce(&mut Vec<PageCfg>)) {
    let Some(w) = weak.upgrade() else { return };
    let mut prefs = get();
    change(&mut prefs.pages);
    crate::store::save(FILE, &prefs);
    set_page_list(&w, &prefs.pages);
    if let Some(n) = NOTCH.with(|n| n.borrow().as_ref().and_then(|w| w.upgrade())) { apply_pages(&n, &prefs.pages); }
    PREFS.with(|p| *p.borrow_mut() = prefs);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fix_pages_repara_lista() {
        let bad = vec![PageCfg { id: 3, visible: false }, PageCfg { id: 3, visible: true }, PageCfg { id: 0, visible: false }, PageCfg { id: 99, visible: true }];
        let fixed = fix_pages(&bad);
        assert_eq!(fixed.len(), 8);
        assert_eq!(fixed[0], PageCfg { id: 3, visible: false });
        assert_eq!(fixed[1], PageCfg { id: 0, visible: true }); // música sempre visível
        assert_eq!(fixed.iter().map(|p| p.id).collect::<std::collections::BTreeSet<_>>().len(), 8);
    }
}

// ---------- iniciar com o Windows (HKCU\...\Run, valor próprio para não mexer no Orbi em C#) ----------

const RUN: PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const VALUE: PCWSTR = w!("OrbiRust");

fn autostart() -> bool {
    unsafe { RegGetValueW(HKEY_CURRENT_USER, RUN, VALUE, RRF_RT_REG_SZ, None, None, None) }.is_ok()
}

fn set_autostart(on: bool) {
    unsafe {
        if on {
            let cmd: Vec<u16> = format!("\"{}\" --startup", updater::exe().display()).encode_utf16().chain(Some(0)).collect();
            let _ = RegSetKeyValueW(HKEY_CURRENT_USER, RUN, VALUE, REG_SZ.0, Some(cmd.as_ptr().cast()), (cmd.len() * 2) as u32);
        } else {
            let _ = RegDeleteKeyValueW(HKEY_CURRENT_USER, RUN, VALUE);
        }
    }
}

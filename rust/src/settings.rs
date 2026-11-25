// Preferências da versão em Rust (%AppData%\Orbi\rust-config.json; o config.json é do Orbi em C#)
// e a janela de Configurações, aberta pela engrenagem do notch.
use serde::{Deserialize, Serialize};
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
}

impl Default for Prefs {
    fn default() -> Self {
        Prefs { battery_alerts: true, audio_alerts: true, reminders: true, lyrics: true, gradient: true, auto_update: true, compact: false }
    }
}

const FILE: &str = "rust-config.json";

thread_local! {
    static PREFS: RefCell<Prefs> = RefCell::new(crate::store::load(FILE));
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
    updater::AUTO.store(prefs.auto_update, Ordering::Relaxed);
    NOTCH.with(|n| *n.borrow_mut() = Some(notch.as_weak()));
    notch.on_open_settings(show);
    updater::start();
    // Como no Orbi em C#: abrir pelo ícone mostra as configurações; ao ligar o PC (--startup), só o notch.
    if !std::env::args().any(|a| a == "--startup") {
        let _ = slint::invoke_from_event_loop(show);
    }
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
        if !after_update { unsafe { let _ = SetEvent(h); } }
        unsafe { let _ = windows::Win32::Foundation::CloseHandle(h); }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
    false
}

pub fn show() {
    WINDOW.with(|cell| {
        let mut cell = cell.borrow_mut();
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
                };
                crate::store::save(FILE, &prefs);
                updater::AUTO.store(prefs.auto_update, Ordering::Relaxed);
                if let Some(n) = NOTCH.with(|n| n.borrow().as_ref().and_then(|w| w.upgrade())) {
                    n.set_gradient_enabled(prefs.gradient);
                    n.set_compact(prefs.compact);
                    if !prefs.lyrics { n.set_has_lyrics(false); }
                }
                PREFS.with(|p| *p.borrow_mut() = prefs);
                if w.get_start_with_windows() != autostart() {
                    set_autostart(w.get_start_with_windows());
                    w.set_start_with_windows(autostart());
                }
            });
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
        w.set_start_with_windows(autostart());
        STATUS.with(|s| {
            let s = s.borrow();
            w.set_update_status(if s.0.is_empty() { "Ainda não procurou versão nova.".into() } else { s.0.clone().into() });
            w.set_update_ready(s.1);
        });
        let _ = w.show();
    });
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

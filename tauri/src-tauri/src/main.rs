// Orbi: o notch do Windows. O motor é Rust (estes módulos) e a tela é HTML/CSS/JS (pasta ui/), no WebView2,
// pelo Tauri. compat.rs liga os dois: propriedades viram eventos "ui" e os botões chamam "ui_call".
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod agents;
mod chat;
mod color;
mod compat;
mod features;
mod lyrics;
mod media;
mod notifications;
mod pages;
mod settings;
mod spectrum;
mod store;
mod system;
mod taskparse;
mod updater;
mod weather;
mod winapi;

pub use compat::{AgentRow, ChatMsg, DayCell, Notch, PageEntry, Settings, ShortcutItem, TaskRow};
use serde_json::Value;
use tauri::Manager;

/// Erros vão para %AppData%\Orbi\rust-erro.log (o app não tem console para mostrar).
fn log_error(msg: &str) {
    let _ = std::fs::create_dir_all(store::dir());
    let _ = std::fs::write(store::dir().join("rust-erro.log"), msg);
}

/// A tela chamou um botão/ação do notch ou das configurações (ex.: "notch.play_pause").
#[tauri::command]
fn ui_call(name: String, args: Vec<Value>) {
    let _ = compat::invoke_from_event_loop(move || compat::fire(&name, &args));
}

/// A tela mudou um valor que o motor também lê (página, chat aberto, foco no texto, volume...).
#[tauri::command]
fn ui_set(key: String, value: Value) {
    compat::put_quiet(&key, value);
}

/// Estado completo, pedido pela tela ao carregar.
#[tauri::command]
fn ui_state() -> Value {
    compat::snapshot()
}

fn main() {
    std::panic::set_hook(Box::new(|info| log_error(&format!("pane: {info}"))));
    // Teste do CI: busca uma letra de verdade no LRCLIB (com álbum e sem álbum) e grava o resultado.
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
        return;
    }
    if !settings::claim_instance() { return; }
    let result = tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![ui_call, ui_set, ui_state])
        .on_window_event(|window, event| {
            // Fechar o notch pelo Alt+F4 não derruba o Orbi; sair é pelo X do notch ou nas configurações.
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                if window.label() == "notch" { api.prevent_close(); }
            }
        })
        .setup(|app| {
            compat::set_app(app.handle().clone());
            if let Some(w) = app.get_webview_window("notch") { let _ = w.set_ignore_cursor_events(true); }
            compat::start_core(|| {
                let notch = Notch;
                notch.init();
                system::setup(&notch);
                media::start(&notch);
                pages::setup(&notch);
                features::setup(&notch);
                settings::setup(&notch);
                let p = settings::get();
                // ORBI_SEM_AGENTES=1: abre sem o servidor dos agentes (diagnóstico do CI).
                if std::env::var_os("ORBI_SEM_AGENTES").is_none() { agents::setup(&notch, p.agent_port, p.agent_token); }
                settings::upgrade_agents();
                settings::refresh_agents(&notch);
                features::chat(&notch);
                features::weather(&notch);
            });
            Ok(())
        })
        .run(tauri::generate_context!());
    if let Err(e) = result { log_error(&format!("erro: {e}")); }
}

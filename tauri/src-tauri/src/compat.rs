// Ponte entre os módulos do Orbi (escritos para o Slint) e a tela em HTML do Tauri.
// Imita só o que eles usavam do Slint: timers e "invoke" numa thread só (o núcleo, como o event loop do
// Slint), propriedades do notch e das configurações (guardadas aqui e mandadas para a tela como eventos
// "ui") e os callbacks que a tela chama (comando "ui_call").
#![allow(dead_code)]

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::rc::Rc;
use std::sync::{mpsc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, Manager};

pub type SharedString = String;
/// No Slint era uma referência fraca; aqui o notch e as configurações são globais.
pub type Weak<T> = T;
pub trait ComponentHandle {}
pub trait Model {
    fn row_count(&self) -> usize;
    fn row_data(&self, i: usize) -> Option<i32>;
}
impl Model for Vec<i32> {
    fn row_count(&self) -> usize { self.len() }
    fn row_data(&self, i: usize) -> Option<i32> { self.get(i).copied() }
}
pub struct ModelRc;
impl ModelRc { pub fn new<T>(v: Vec<T>) -> Vec<T> { v } }
pub struct VecModel;
impl VecModel { pub fn from<T>(v: Vec<T>) -> Vec<T> { v } }

// ---------- núcleo: fila de tarefas e timers numa thread só ----------

type Job = Box<dyn FnOnce() + Send>;
static QUEUE: OnceLock<mpsc::Sender<Job>> = OnceLock::new();

pub fn invoke_from_event_loop(f: impl FnOnce() + Send + 'static) -> Result<(), String> {
    QUEUE.get().ok_or("o núcleo não começou")?.send(Box::new(f)).map_err(|e| e.to_string())
}

type Callback = Rc<RefCell<Box<dyn FnMut()>>>;
struct Entry { due: Instant, every: Option<Duration>, f: Callback, alive: Rc<Cell<bool>> }
thread_local! { static TIMERS: RefCell<Vec<Entry>> = const { RefCell::new(Vec::new()) }; }

#[derive(Clone, Copy)]
pub enum TimerMode { SingleShot, Repeated }

/// Timer como o do Slint: parado ao ser descartado (os módulos usam mem::forget para os eternos).
#[derive(Default)]
pub struct Timer { alive: RefCell<Option<Rc<Cell<bool>>>>, last: RefCell<Option<(TimerMode, Duration, Callback)>> }

impl Timer {
    pub fn start(&self, mode: TimerMode, dur: Duration, f: impl FnMut() + 'static) {
        let f: Callback = Rc::new(RefCell::new(Box::new(f)));
        self.schedule(mode, dur, f.clone());
        *self.last.borrow_mut() = Some((mode, dur, f));
    }
    fn schedule(&self, mode: TimerMode, dur: Duration, f: Callback) {
        self.stop();
        let alive = Rc::new(Cell::new(true));
        let every = matches!(mode, TimerMode::Repeated).then_some(dur);
        TIMERS.with(|t| t.borrow_mut().push(Entry { due: Instant::now() + dur, every, f, alive: alive.clone() }));
        *self.alive.borrow_mut() = Some(alive);
    }
    pub fn stop(&self) { if let Some(a) = self.alive.borrow_mut().take() { a.set(false); } }
    pub fn restart(&self) {
        let last = self.last.borrow().clone();
        if let Some((m, d, f)) = last { self.schedule(m, d, f); }
    }
    pub fn single_shot(dur: Duration, f: impl FnOnce() + 'static) {
        let mut f = Some(f);
        let cb: Callback = Rc::new(RefCell::new(Box::new(move || { if let Some(f) = f.take() { f() } })));
        TIMERS.with(|t| t.borrow_mut().push(Entry { due: Instant::now() + dur, every: None, f: cb, alive: Rc::new(Cell::new(true)) }));
    }
}
impl Drop for Timer { fn drop(&mut self) { self.stop() } }

fn run_due_timers() {
    let now = Instant::now();
    let due: Vec<Callback> = TIMERS.with(|t| {
        let mut t = t.borrow_mut();
        t.retain(|e| e.alive.get());
        t.iter_mut().filter(|e| e.due <= now).map(|e| {
            match e.every { Some(d) => e.due = now + d, None => e.alive.set(false) }
            e.f.clone()
        }).collect()
    });
    for f in due { if let Ok(mut f) = f.try_borrow_mut() { f() } }
}

/// Liga o núcleo: `setup` roda nele, e depois ele fica atendendo a fila e os timers.
pub fn start_core(setup: impl FnOnce() + Send + 'static) {
    let (tx, rx) = mpsc::channel::<Job>();
    let _ = QUEUE.set(tx);
    std::thread::spawn(move || {
        setup();
        loop {
            let next = TIMERS.with(|t| t.borrow().iter().filter(|e| e.alive.get()).map(|e| e.due).min());
            let wait = next.map(|d| d.saturating_duration_since(Instant::now())).unwrap_or(Duration::from_millis(500));
            match rx.recv_timeout(wait) {
                Ok(job) => job(),
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }
            while let Ok(job) = rx.try_recv() { job() }
            run_due_timers();
        }
    });
}

pub fn quit_event_loop() -> Result<(), String> {
    if let Some(app) = APP.get() { app.exit(0); }
    Ok(())
}

// ---------- propriedades (Rust → tela) e callbacks (tela → Rust) ----------

static APP: OnceLock<AppHandle> = OnceLock::new();
static STATE: Mutex<Option<Map<String, Value>>> = Mutex::new(None);

pub fn set_app(app: AppHandle) { let _ = APP.set(app); }
pub fn app() -> Option<&'static AppHandle> { APP.get() }

/// Guarda e, se mudou, avisa a tela.
pub fn put(key: &str, v: Value) {
    let changed = {
        let mut s = STATE.lock().unwrap();
        s.get_or_insert_with(Map::new).insert(key.to_string(), v.clone()) != Some(v.clone())
    };
    if changed { if let Some(app) = APP.get() { let _ = app.emit("ui", json!({ "k": key, "v": v })); } }
}

/// Valor que a tela mudou (ex.: página, chat aberto): guarda sem mandar de volta.
pub fn put_quiet(key: &str, v: Value) { STATE.lock().unwrap().get_or_insert_with(Map::new).insert(key.to_string(), v); }

pub fn get_value(key: &str) -> Value { STATE.lock().unwrap().as_ref().and_then(|s| s.get(key).cloned()).unwrap_or(Value::Null) }

/// Tudo de uma vez (a tela pede ao carregar).
pub fn snapshot() -> Value { Value::Object(STATE.lock().unwrap().clone().unwrap_or_default()) }

type Handler = Rc<dyn Fn(&[Value])>;
thread_local! { static HANDLERS: RefCell<HashMap<String, Handler>> = RefCell::new(HashMap::new()); }

pub fn register(name: &str, f: Handler) { HANDLERS.with(|h| h.borrow_mut().insert(name.to_string(), f)); }

/// A tela chamou um callback (roda no núcleo).
pub fn fire(name: &str, args: &[Value]) {
    let h = HANDLERS.with(|h| h.borrow().get(name).cloned());
    if let Some(h) = h { h(args) }
}

fn arg<T: serde::de::DeserializeOwned + Default>(args: &[Value], i: usize) -> T {
    args.get(i).cloned().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
}

macro_rules! props {
    ($ty:ident, $scope:literal, { $($name:ident : $t:ty),* $(,)? }) => { paste::paste! {
        impl $ty { $(
            pub fn [<set_ $name>](&self, v: $t) { put(concat!($scope, ".", stringify!($name)), serde_json::to_value(v).unwrap_or(Value::Null)) }
            pub fn [<get_ $name>](&self) -> $t { serde_json::from_value(get_value(concat!($scope, ".", stringify!($name)))).unwrap_or_default() }
        )* }
    } };
}

macro_rules! callbacks {
    ($ty:ident, $scope:literal, { $($name:ident ( $($t:ty),* )),* $(,)? }) => { paste::paste! {
        impl $ty { $(
            #[allow(unused_mut, unused_variables, unused_assignments)]
            pub fn [<on_ $name>](&self, f: impl Fn($($t),*) + 'static) {
                register(concat!($scope, ".", stringify!($name)), Rc::new(move |args: &[Value]| {
                    let mut i = 0usize;
                    f($({ let v: $t = arg(args, i); i += 1; v }),*)
                }));
            }
        )* }
    } };
}

// ---------- tipos que a tela recebe ----------

/// Cor (vai para a tela como "#rrggbb").
#[derive(Clone, Copy, Default, PartialEq)]
pub struct Color(pub u32);
impl Color {
    pub const fn from_rgb_u8(r: u8, g: u8, b: u8) -> Self { Color((r as u32) << 16 | (g as u32) << 8 | b as u32) }
    pub fn hex(&self) -> String { format!("#{:06x}", self.0) }
}
impl Serialize for Color {
    fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> { s.serialize_str(&self.hex()) }
}
impl<'de> Deserialize<'de> for Color {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        let s = String::deserialize(d)?;
        Ok(Color(u32::from_str_radix(s.trim_start_matches('#'), 16).unwrap_or(0xFFFFFF)))
    }
}

pub struct Rgba8Pixel;
/// Pixels RGBA (como o SharedPixelBuffer<Rgba8Pixel> do Slint).
pub struct SharedPixelBuffer { px: Vec<u8>, w: u32, h: u32 }
impl SharedPixelBuffer {
    pub fn clone_from_slice(px: &[u8], w: u32, h: u32) -> Self { SharedPixelBuffer { px: px.to_vec(), w, h } }
}

/// Imagem como data URL PNG (a tela usa direto no <img>).
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct Image(pub String);
impl Image {
    pub fn from_rgba8(b: SharedPixelBuffer) -> Self {
        let mut png = Vec::new();
        let ok = image::RgbaImage::from_raw(b.w, b.h, b.px)
            .map(|img| img.write_to(&mut std::io::Cursor::new(&mut png), image::ImageFormat::Png).is_ok())
            .unwrap_or(false);
        if ok { Image(format!("data:image/png;base64,{}", base64(&png))) } else { Image::default() }
    }
}

pub fn base64(bytes: &[u8]) -> String {
    const T: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for c in bytes.chunks(3) {
        let n = (c[0] as u32) << 16 | (*c.get(1).unwrap_or(&0) as u32) << 8 | *c.get(2).unwrap_or(&0) as u32;
        for i in 0..4 {
            if i <= c.len() { out.push(T[(n >> (18 - 6 * i) & 63) as usize] as char) } else { out.push('=') }
        }
    }
    out
}

#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct TaskRow { pub title: String, pub done: bool, pub due: String }
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct DayCell { pub day: i32, pub in_month: bool, pub today: bool }
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct ShortcutItem { pub name: String, pub glyph: String, pub tint: Color }
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct ChatMsg { pub user: bool, pub text: String }
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct AgentRow { pub project: String, pub status: String, pub busy: bool }
#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct PageEntry { pub name: String, pub visible: bool, pub fixed: bool }

// ---------- o notch ----------

#[derive(Clone, Copy, Default)]
pub struct Notch;
impl ComponentHandle for Notch {}
impl Notch {
    pub fn as_weak(&self) -> Notch { Notch }
    pub fn upgrade(&self) -> Option<Notch> { Some(Notch) }
}

props!(Notch, "notch", {
    accent: Color, adding: bool, agent_rows: Vec<AgentRow>, agents_busy: bool, agents_connected: bool, artist: String,
    audio_device: String, bands: Vec<f32>, battery: f32, battery_detail: String, charging: bool, chat_busy: bool,
    chat_greeting: String, chat_messages: Vec<ChatMsg>, chat_open: bool, chat_focused: bool, clip_hint: String, clips: Vec<String>,
    clock: String, closed_text: String, compact: bool, cover: Image, cpu: f32, cpu_text: String, date_text: String,
    days: Vec<DayCell>, dragging: bool, edge: i32, edge_pos: f32, elapsed: String, focus_minutes: i32,
    focus_running: bool, focus_text: String, game_mode: bool, gradient_enabled: bool, has_battery: bool, has_cover: bool,
    has_lyrics: bool, has_media: bool, has_toast_icon: bool, hovered: bool, level: f32, look_x: f32, look_y: f32,
    lyric_next: String, lyric_next2: String, lyric_now: String, lyric_prev: String, lyrics_on: bool, month_name: String,
    notes: String, notes_focused: bool, order: Vec<i32>, perm_code: String, perm_count: i32, perm_project: String,
    perm_visible: bool, perm_what: String, plan_five: f32, plan_five_text: String, plan_week: f32, plan_week_text: String,
    playing: bool, pos: i32, progress: f32, pulse: f32, ram: f32, ram_detail: String, ram_text: String, remaining: String,
    sc_adding: bool, shortcuts: Vec<ShortcutItem>, side_text: String, song: String, tasks: Vec<TaskRow>, tasks_done: i32,
    tasks_total: i32, temp: f32, toast_app: String, toast_badge: bool, toast_color: Color, toast_glyph: String,
    toast_icon: Image, toast_text: String, toasting: bool, volume: f32, volume_held: bool, weather_glyph: String,
    weather_text: String,
});

callbacks!(Notch, "notch", {
    add_shortcut_file(), add_shortcut_folder(), add_shortcut_site(String), add_task(String), chat_clear(), chat_opened(),
    chat_send(String), clear_clips(), clear_done(), copy_clip(i32), drag_end(), drag_move(), drag_start(),
    focus_preset(i32), focus_reset(), focus_toggle(), game_toggled(), lyrics_toggled(), month_next(), month_prev(),
    next(), notes_edited(String), open_settings(), open_shortcut(i32), perm_decide(String), play_pause(), previous(),
    quit(), remove_shortcut(i32), toggle_task(i32), volume_changed(f32), want_keyboard(),
});

/// Valores que no Slint eram calculados na própria interface.
impl Notch {
    pub fn get_open(&self) -> bool {
        !self.get_dragging() && (self.get_hovered() || self.get_adding() || self.get_notes_focused() || self.get_chat_focused()
            || (self.get_perm_visible() && !self.get_game_mode()))
    }
    pub fn get_page(&self) -> i32 {
        let order = self.get_order();
        if order.is_empty() { return 0; }
        order[(self.get_pos().max(0) as usize).min(order.len() - 1)]
    }
    fn side(&self) -> bool { self.get_edge() != 0 }
    pub fn get_closed_width(&self) -> f32 {
        if self.side() { if self.get_game_mode() { 6.0 } else { 34.0 } } else if self.get_game_mode() { 70.0 } else { 120.0 }
    }
    pub fn get_closed_height(&self) -> f32 {
        if self.get_game_mode() { if self.side() { 70.0 } else { 6.0 } } else if !self.side() { 30.0 } else if self.get_playing() { 116.0 } else { 92.0 }
    }
    pub fn get_open_width(&self) -> f32 { if self.get_compact() { 544.0 } else { 620.0 } }
    pub fn get_open_height(&self) -> f32 { if self.get_compact() { 232.0 } else { 262.0 } }
    pub fn get_big_width(&self) -> f32 { 660.0 }
    pub fn get_big_height(&self) -> f32 { 300.0 }

    /// Valores de partida (no Slint eram os padrões das propriedades).
    pub fn init(&self) {
        self.set_song("Nada tocando".into());
        self.set_volume(0.5);
        self.set_edge_pos(0.5);
        self.set_temp(-1.0);
        self.set_plan_five(-1.0);
        self.set_plan_week(-1.0);
        self.set_gradient_enabled(true);
        self.set_lyrics_on(true);
        self.set_has_battery(true);
        self.set_battery(1.0);
        self.set_order((0..9).collect());
        self.set_bands(vec![0.0; 5]);
        self.set_accent(Color::from_rgb_u8(0x1A, 0x2A, 0x5C));
        self.set_toast_color(Color::from_rgb_u8(0xFF, 0xFF, 0xFF));
        self.set_elapsed("0:00".into());
        self.set_remaining("-0:00".into());
    }
}

// ---------- as configurações (janela separada) ----------

#[derive(Clone, Copy, Default)]
pub struct Settings;
impl ComponentHandle for Settings {}
impl Settings {
    pub fn as_weak(&self) -> Settings { Settings }
    pub fn upgrade(&self) -> Option<Settings> { Some(Settings) }
    /// Abre (ou traz para a frente) a janela de configurações.
    pub fn new() -> Result<Settings, String> {
        let app = APP.get().ok_or("sem app")?;
        if app.get_webview_window("settings").is_none() {
            tauri::WebviewWindowBuilder::new(app, "settings", tauri::WebviewUrl::App("settings.html".into()))
                .title("Orbi").inner_size(900.0, 600.0).min_inner_size(760.0, 480.0).center()
                .build().map_err(|e| e.to_string())?;
        }
        Ok(Settings)
    }
    pub fn show(&self) -> Result<(), String> {
        if let Some(w) = APP.get().and_then(|a| a.get_webview_window("settings")) {
            let _ = w.unminimize();
            let _ = w.show();
            let _ = w.set_focus();
        }
        Ok(())
    }
    pub fn is_open() -> bool { APP.get().and_then(|a| a.get_webview_window("settings")).is_some() }
}

props!(Settings, "settings", {
    agents_connected: bool, agents_status: String, api_key_input: String, api_key_status: String, app_notifications: bool,
    audio_alerts: bool, auto_update: bool, battery_alerts: bool, city: String, city_status: String, compact: bool, edge: i32,
    gradient: bool, lyrics: bool, page_list: Vec<PageEntry>, reminders: bool, sounds: bool, start_with_windows: bool,
    update_ready: bool, update_status: String, version: String,
});

callbacks!(Settings, "settings", {
    agents_connect(), agents_disconnect(), changed(), check_update(), open_folder(), page_move(i32, i32), page_toggle(i32),
    quit(), restart_update(), save_api_key(String), save_city(String),
});

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_igual_ao_padrao() {
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"f"), "Zg==");
        assert_eq!(base64(b"fo"), "Zm8=");
        assert_eq!(base64(b"foo"), "Zm9v");
        assert_eq!(base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn cor_em_hexadecimal() {
        assert_eq!(Color::from_rgb_u8(0xD9, 0x77, 0x57).hex(), "#d97757");
        assert_eq!(serde_json::to_value(Color::from_rgb_u8(1, 2, 3)).unwrap(), "#010203");
    }
}

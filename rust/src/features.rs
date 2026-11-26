// Leva 1 das funções da versão em C#: avisos rápidos, bateria/fone, calendário, notas,
// área de transferência, sistema, atalhos e lembretes das tarefas.
use crate::{store, winapi, DayCell, Notch, ShortcutItem};
use serde::Deserialize;
use slint::{Color, ComponentHandle, ModelRc, SharedString, Timer, TimerMode, VecModel};
use std::cell::RefCell;
use std::rc::Rc;
use std::time::Duration;

const GREEN: Color = Color::from_rgb_u8(0x30, 0xD1, 0x58);
const RED: Color = Color::from_rgb_u8(0xFF, 0x45, 0x3A);
const ORANGE: Color = Color::from_rgb_u8(0xFF, 0x9F, 0x0A);
const WHITE: Color = Color::from_rgb_u8(0xFF, 0xFF, 0xFF);

thread_local! {
    static TOAST_TIMER: Timer = Timer::default();
}

/// Aviso rápido no notch fechado por alguns segundos.
pub fn toast(n: &Notch, glyph: &str, text: &str, color: Color, secs: u64) {
    n.set_toast_badge(false);
    n.set_has_toast_icon(false);
    n.set_toast_app("".into());
    n.set_toast_glyph(glyph.into());
    n.set_toast_text(text.into());
    n.set_toast_color(color);
    n.set_toasting(true);
    let weak = n.as_weak();
    TOAST_TIMER.with(|t| t.start(TimerMode::SingleShot, Duration::from_secs(secs), move || {
        if let Some(n) = weak.upgrade() { n.set_toasting(false); }
    }));
}

/// Aviso de app (WhatsApp, Discord, email): o ícone de verdade do app (sem ele, um quadradinho da cor do app) e som.
pub fn toast_app(n: &Notch, app: &str, icon: Option<slint::Image>, text: &str) {
    let (color, glyph) = match app {
        "WhatsApp" => (Color::from_rgb_u8(0x25, 0xD3, 0x66), "\u{E8BD}"),
        "Discord" => (Color::from_rgb_u8(0x58, 0x65, 0xF2), "\u{E8F2}"),
        "Gmail" => (Color::from_rgb_u8(0xEA, 0x43, 0x35), "\u{E715}"),
        _ => (Color::from_rgb_u8(0x0A, 0x84, 0xFF), "\u{E715}"), // email
    };
    toast(n, glyph, text, color, 5);
    n.set_toast_app(app.into()); // WhatsApp, Discord, Gmail e Outlook: logo próprio; os outros, o ícone do Windows
    n.set_toast_badge(true);
    if let Some(icon) = icon {
        n.set_toast_icon(icon);
        n.set_has_toast_icon(true);
    }
    sound("aviso");
}

/// Som de um aviso, se os sons estiverem ligados nas configurações.
pub fn sound(name: &str) {
    if crate::settings::get().sounds { crate::winapi::play_sound(name); }
}

pub fn setup(notch: &Notch) {
    app_notifications(notch);
    battery_and_audio(notch);
    calendar(notch);
    notes(notch);
    clipboard(notch);
    system(notch);
    shortcuts(notch);
}

// ---------- chat com o Claude (pela assinatura, via Claude Code) ----------

pub fn chat(notch: &Notch) {
    let history: Rc<RefCell<Vec<(bool, String)>>> = Default::default();
    let render = {
        let (weak, history) = (notch.as_weak(), history.clone());
        Rc::new(move || {
            let Some(n) = weak.upgrade() else { return };
            let rows: Vec<_> = history.borrow().iter().map(|(user, text)| crate::ChatMsg { user: *user, text: text.into() }).collect();
            n.set_chat_messages(ModelRc::new(VecModel::from(rows)));
        })
    };
    let (weak, h, r) = (notch.as_weak(), history.clone(), render.clone());
    notch.on_chat_send(move |text| {
        let Some(n) = weak.upgrade() else { return };
        let question = text.trim().to_string();
        if question.is_empty() || n.get_chat_busy() { return; }
        let prompt = crate::chat::prompt(&h.borrow(), &question);
        let past = h.borrow().clone();
        let key = crate::settings::api_key();
        let q = question.clone();
        h.borrow_mut().push((true, question));
        n.set_chat_busy(true);
        r();
        let weak = weak.clone();
        std::thread::spawn(move || {
            // Assinatura primeiro (Claude Code); sem ele, ou se ele falhar, a chave de API (se houver).
            let cli = match crate::chat::find() {
                Some(exe) => crate::chat::ask(&exe, &prompt),
                None => Err("Não achei o Claude Code neste PC. Instale e faça /login uma vez no terminal (comando \"claude\"), ou coloque uma chave de API nas configurações.".into()),
            };
            let answer = match (cli, key) {
                (Err(_), Some(key)) => crate::chat::ask_api(&key, &past, &q),
                (result, _) => result,
            };
            let _ = slint::invoke_from_event_loop(move || {
                let Some(n) = weak.upgrade() else { return };
                CHAT_REPLY.with(|c| if let Some(f) = &*c.borrow() { f(answer.unwrap_or_else(|e| format!("⚠ {e}"))); });
                n.set_chat_busy(false);
            });
        });
    });
    let (h, r) = (history.clone(), render.clone());
    CHAT_REPLY.with(|c| *c.borrow_mut() = Some(Box::new(move |text| { h.borrow_mut().push((false, text)); r(); })));
    let (h, r) = (history, render);
    notch.on_chat_clear(move || { h.borrow_mut().clear(); r(); });
    // Frase nova cada vez que o chat abre (a hora decide o tom; os nanossegundos, qual frase).
    let weak = notch.as_weak();
    let greet = move || {
        let Some(n) = weak.upgrade() else { return };
        let pick = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.subsec_nanos() as usize).unwrap_or(0);
        n.set_chat_greeting(crate::chat::greeting(crate::system::now().hour, pick).into());
    };
    greet();
    notch.on_chat_opened(greet);
}

thread_local! {
    static CHAT_REPLY: RefCell<Option<Box<dyn Fn(String)>>> = RefCell::new(None);
}

// ---------- clima (Open-Meteo, da cidade escolhida nas configurações) ----------

pub fn weather(notch: &Notch) {
    let weak = notch.as_weak();
    let timer = Timer::default();
    let tick: Rc<dyn Fn()> = Rc::new(move || {
        let Some((lat, lon)) = crate::settings::get().city_coords() else {
            if let Some(n) = weak.upgrade() { n.set_weather_text("".into()); }
            return;
        };
        let weak = weak.clone();
        std::thread::spawn(move || {
            let now = crate::weather::now(lat, lon);
            let _ = slint::invoke_from_event_loop(move || {
                let (Some(n), Some((temp, code))) = (weak.upgrade(), now) else { return };
                n.set_weather_glyph(crate::weather::describe(code).0.into());
                n.set_weather_text(format!("{}°", temp.round()).into());
            });
        });
    });
    tick();
    let t = tick.clone();
    timer.start(TimerMode::Repeated, Duration::from_secs(30 * 60), move || t());
    std::mem::forget(timer);
    WEATHER_TICK.with(|w| *w.borrow_mut() = Some(tick));
}

thread_local! { static WEATHER_TICK: RefCell<Option<Rc<dyn Fn()>>> = RefCell::new(None); }

/// Pede o clima de novo agora (depois de escolher a cidade).
pub fn weather_now() {
    if let Some(t) = WEATHER_TICK.with(|w| w.borrow().clone()) { t(); }
}

// ---------- WhatsApp e Discord ----------

fn app_notifications(notch: &Notch) {
    let weak = notch.as_weak();
    std::thread::spawn(move || {
        // COM em modo de janela: o ícone do app vem do Shell.
        unsafe { let _ = windows::Win32::System::Com::CoInitializeEx(None, windows::Win32::System::Com::COINIT_APARTMENTTHREADED); }
        let mut watcher = crate::notifications::Watcher::new();
        let mut icons: std::collections::HashMap<String, Option<(u32, u32, Vec<u8>)>> = Default::default();
        loop {
            for (app, handler, title, body) in watcher.poll() {
                let icon = icons.entry(handler.clone()).or_insert_with(|| crate::winapi::app_icon(&handler, 48)).clone();
                let weak = weak.clone();
                let _ = slint::invoke_from_event_loop(move || {
                    if !crate::settings::get().app_notifications { return; }
                    let Some(n) = weak.upgrade() else { return };
                    let icon = icon.map(|(w, h, px)| slint::Image::from_rgba8(slint::SharedPixelBuffer::clone_from_slice(&px, w, h)));
                    toast_app(&n, app, icon, &if body.is_empty() { title } else { format!("{title}: {body}") });
                });
            }
            std::thread::sleep(Duration::from_secs(2));
        }
    });
}

// ---------- bateria (carregador, bateria fraca) e fone ----------

fn battery_and_audio(notch: &Notch) {
    let weak = notch.as_weak();
    let was_charging: Rc<RefCell<Option<bool>>> = Default::default();
    let warn_at = Rc::new(RefCell::new(20u32));
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_secs(2), move || {
        let Some(n) = weak.upgrade() else { return };
        if !n.get_has_battery() { return; }
        let pct = (n.get_battery() * 100.0).round() as u32;
        let charging = n.get_charging();
        let mut was = was_charging.borrow_mut();
        if let Some(w) = *was {
            if w != charging && crate::settings::get().battery_alerts {
                if charging { toast(&n, "\u{E945}", &format!("Carregando · {pct}%"), GREEN, 4); }
                else { toast(&n, "\u{E83F}", &format!("Na bateria · {pct}%"), if pct <= 20 { RED } else { GREEN }, 4); }
            }
        }
        *was = Some(charging);
        // Bateria fraca: avisa em 20% e de novo em 10% (rearma quando carregar).
        let mut at = warn_at.borrow_mut();
        if charging { *at = 20; } else if pct <= *at {
            if crate::settings::get().battery_alerts { toast(&n, "\u{E83F}", &format!("Bateria fraca · {pct}%"), RED, 6); }
            *at = if pct <= 10 { 0 } else { 10 };
        }
    });
    std::mem::forget(timer);

    // Saída de som (ex.: fone Bluetooth conectou): lida numa thread, a cada 2 s.
    let weak = notch.as_weak();
    std::thread::spawn(move || {
        let mut last: Option<String> = None;
        loop {
            let name = winapi::audio_output_name();
            if name != last {
                let changed = last.is_some();
                last = name.clone();
                let weak = weak.clone();
                let _ = slint::invoke_from_event_loop(move || {
                    let Some(n) = weak.upgrade() else { return };
                    let label = name.unwrap_or_else(|| "Sem saída de som".into());
                    n.set_audio_device(label.clone().into());
                    if changed && crate::settings::get().audio_alerts { toast(&n, "\u{E7F6}", &label, WHITE, 4); }
                });
            }
            std::thread::sleep(Duration::from_secs(2));
        }
    });
}

// ---------- calendário ----------

const MONTHS: [&str; 12] = ["janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro"];

/// 42 casas (6 semanas) começando no domingo da semana do dia 1.
pub fn month_grid(year: i32, month: u32, today: (i32, u32, u32)) -> Vec<(u32, bool, bool)> {
    let dim = |y: i32, m: u32| match m { 1 | 3 | 5 | 7 | 8 | 10 | 12 => 31, 4 | 6 | 9 | 11 => 30, _ if (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 => 29, _ => 28 };
    // Dia da semana do dia 1 (Zeller adaptado: 0 = domingo).
    let (y, m) = if month < 3 { (year - 1, month + 12) } else { (year, month) };
    let k = y % 100;
    let j = y / 100;
    let h = (1 + 13 * (m as i32 + 1) / 5 + k + k / 4 + j / 4 + 5 * j) % 7; // 0 = sábado
    let first = ((h + 6) % 7) as u32;
    let (py, pm) = if month == 1 { (year - 1, 12) } else { (year, month - 1) };
    let prev_days = dim(py, pm);
    (0..42u32).map(|i| {
        if i < first { (prev_days - first + 1 + i, false, false) }
        else if i - first < dim(year, month) {
            let d = i - first + 1;
            (d, true, (year, month, d) == today)
        } else { (i - first - dim(year, month) + 1, false, false) }
    }).collect()
}

fn calendar(notch: &Notch) {
    let t = unsafe { windows::Win32::System::SystemInformation::GetLocalTime() };
    let today = (t.wYear as i32, t.wMonth as u32, t.wDay as u32);
    let shown = Rc::new(RefCell::new((today.0, today.1)));
    let draw = {
        let (weak, shown) = (notch.as_weak(), shown.clone());
        Rc::new(move || {
            let Some(n) = weak.upgrade() else { return };
            let (y, m) = *shown.borrow();
            n.set_month_name(format!("{} {}", MONTHS[(m - 1) as usize], y).into());
            let cells: Vec<DayCell> = month_grid(y, m, today).into_iter()
                .map(|(day, in_month, is_today)| DayCell { day: day as i32, in_month, today: is_today }).collect();
            n.set_days(ModelRc::new(VecModel::from(cells)));
        })
    };
    draw();
    {
        let (shown, draw) = (shown.clone(), draw.clone());
        notch.on_month_prev(move || { { let mut s = shown.borrow_mut(); *s = if s.1 == 1 { (s.0 - 1, 12) } else { (s.0, s.1 - 1) }; } draw(); });
    }
    notch.on_month_next(move || { { let mut s = shown.borrow_mut(); *s = if s.1 == 12 { (s.0 + 1, 1) } else { (s.0, s.1 + 1) }; } draw(); });
}

// ---------- notas (mesmo notas.txt do Orbi em C#) ----------

fn notes(notch: &Notch) {
    let path = store::dir().join("notas.txt");
    notch.set_notes(std::fs::read_to_string(&path).unwrap_or_default().into());
    let pending: Rc<RefCell<Option<SharedString>>> = Default::default();
    let timer = Rc::new(Timer::default());
    let p = pending.clone();
    let t = timer.clone();
    notch.on_notes_edited(move |text| {
        *p.borrow_mut() = Some(text);
        // Salva meio segundo depois de parar de digitar (num .tmp e troca: nada se perde no meio).
        let (p2, path) = (p.clone(), path.clone());
        t.start(TimerMode::SingleShot, Duration::from_millis(500), move || {
            if let Some(text) = p2.borrow_mut().take() {
                let _ = std::fs::create_dir_all(store::dir());
                let tmp = path.with_extension("tmp");
                if std::fs::write(&tmp, text.as_str()).is_ok() { let _ = std::fs::rename(&tmp, &path); }
            }
        });
    });
    std::mem::forget(timer);
}

// ---------- área de transferência (últimos 20 textos, só na memória) ----------

fn clipboard(notch: &Notch) {
    let items: Rc<RefCell<Vec<String>>> = Default::default();
    let ignore_seq: Rc<RefCell<u32>> = Default::default();
    let show = {
        let (weak, items) = (notch.as_weak(), items.clone());
        Rc::new(move || {
            let Some(n) = weak.upgrade() else { return };
            let v: Vec<SharedString> = items.borrow().iter().map(|s| s.replace(['\n', '\r'], " ").chars().take(200).collect::<String>().into()).collect();
            n.set_clips(ModelRc::new(VecModel::from(v)));
        })
    };
    let last_seq = Rc::new(RefCell::new(winapi::clipboard_seq()));
    {
        let (items, show, ignore) = (items.clone(), show.clone(), ignore_seq.clone());
        let timer = Timer::default();
        timer.start(TimerMode::Repeated, Duration::from_millis(500), move || {
            let seq = winapi::clipboard_seq();
            if seq == *last_seq.borrow() { return; }
            *last_seq.borrow_mut() = seq;
            if seq == *ignore.borrow() { return; } // a nossa própria cópia
            let Some(text) = winapi::clipboard_text() else { return };
            if text.trim().is_empty() { return; }
            let mut v = items.borrow_mut();
            v.retain(|x| x != &text); // repetido sobe para o topo
            v.insert(0, text);
            v.truncate(20);
            drop(v);
            show();
        });
        std::mem::forget(timer);
    }
    {
        let (items, weak, ignore) = (items.clone(), notch.as_weak(), ignore_seq.clone());
        notch.on_copy_clip(move |i| {
            let Some(text) = items.borrow().get(i as usize).cloned() else { return };
            let ok = winapi::set_clipboard_text(&text);
            if ok { *ignore.borrow_mut() = winapi::clipboard_seq(); }
            if let Some(n) = weak.upgrade() { n.set_clip_hint(if ok { "Copiado!" } else { "Não deu para copiar, tente de novo" }.into()); }
        });
    }
    notch.on_clear_clips(move || { items.borrow_mut().clear(); show(); });
}

// ---------- sistema: CPU, memória, bateria ----------

fn system(notch: &Notch) {
    let weak = notch.as_weak();
    let mut cpu = winapi::Cpu::new();
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_secs(1), move || {
        let Some(n) = weak.upgrade() else { return };
        if let Some(c) = cpu.read() {
            n.set_cpu(c);
            n.set_cpu_text(format!("{}%", (c * 100.0).round()).into());
        }
        if let Some((load, used, total)) = winapi::memory() {
            n.set_ram(load);
            n.set_ram_text(format!("{}%", (load * 100.0).round()).into());
            n.set_ram_detail(format!("{used:.1} de {total:.0} GB").into());
        }
        n.set_battery_detail(if !n.get_has_battery() { "sem bateria".into() } else if n.get_charging() { "carregando".into() } else { "na bateria".into() });
    });
    std::mem::forget(timer);
}

// ---------- atalhos (mesmo atalhos.json do Orbi em C#) ----------

#[derive(Deserialize, serde::Serialize, Clone)]
#[serde(rename_all = "PascalCase")]
struct Shortcut { name: String, target: String }

fn shortcuts(notch: &Notch) {
    // Lista do atalhos.json (a mesma do Orbi em C#); os dois primeiros são fixos e o último é o "Adicionar".
    let user: Rc<RefCell<Vec<Shortcut>>> = Rc::new(RefCell::new(store::load("atalhos.json")));
    let draw = {
        let (weak, user) = (notch.as_weak(), user.clone());
        Rc::new(move || {
            let Some(n) = weak.upgrade() else { return };
            let mut items = vec![
                ShortcutItem { name: "Gravar tela".into(), glyph: "\u{E714}".into(), tint: RED },
                ShortcutItem { name: "Print".into(), glyph: "\u{E722}".into(), tint: RED },
            ];
            for s in user.borrow().iter() {
                let is_url = s.target.starts_with("http://") || s.target.starts_with("https://");
                let is_dir = std::path::Path::new(&s.target).is_dir();
                items.push(ShortcutItem {
                    name: s.name.clone().into(),
                    glyph: if is_url { "\u{E774}" } else if is_dir { "\u{E8B7}" } else { "\u{E7AC}" }.into(),
                    tint: if is_url { Color::from_rgb_u8(0x0A, 0x84, 0xFF) } else if is_dir { ORANGE } else { WHITE },
                });
            }
            items.push(ShortcutItem { name: "Adicionar".into(), glyph: "\u{E710}".into(), tint: Color::from_rgb_u8(0x8E, 0x8E, 0x93) });
            n.set_shortcuts(ModelRc::new(VecModel::from(items)));
        })
    };
    draw();
    let add = {
        let (user, draw) = (user.clone(), draw.clone());
        Rc::new(move |name: String, target: String| {
            user.borrow_mut().push(Shortcut { name, target });
            store::save("atalhos.json", &*user.borrow());
            draw();
        })
    };
    let (weak, u) = (notch.as_weak(), user.clone());
    notch.on_open_shortcut(move |i| {
        let count = u.borrow().len() as i32;
        match i {
            0 => winapi::press_keys(&[0x5B, 0x10, 0x52]), // Win+Shift+R: gravação da Ferramenta de Captura
            1 => winapi::open("ms-screenclip:"),
            _ if i == count + 2 => { if let Some(n) = weak.upgrade() { n.set_sc_adding(true); } }
            _ => if let Some(s) = u.borrow().get(i as usize - 2) { winapi::open(&s.target) },
        }
    });
    let (u, d) = (user.clone(), draw.clone());
    notch.on_remove_shortcut(move |i| {
        let k = i as usize;
        if k < 2 || k - 2 >= u.borrow().len() { return; } // os fixos e o "Adicionar" não saem
        u.borrow_mut().remove(k - 2);
        store::save("atalhos.json", &*u.borrow());
        d();
    });
    for folder in [false, true] {
        let (weak, add) = (notch.as_weak(), add.clone());
        let pick = move || {
            if let Some(n) = weak.upgrade() { n.set_sc_adding(false); }
            let Some(path) = winapi::pick_path(folder) else { return };
            let p = std::path::Path::new(&path);
            let name = p.file_stem().or(p.file_name()).map(|s| s.to_string_lossy().into_owned()).unwrap_or(path.clone());
            add(name, path);
        };
        if folder { notch.on_add_shortcut_folder(pick); } else { notch.on_add_shortcut_file(pick); }
    }
    let (weak, add) = (notch.as_weak(), add.clone());
    notch.on_add_shortcut_site(move |text| {
        if let Some(n) = weak.upgrade() { n.set_sc_adding(false); n.set_adding(false); }
        if let Some((name, url)) = site(&text) { add(name, url); }
    });
}

/// Endereço digitado → (nome curto, endereço completo). "github.com/x" vira ("github.com", "https://github.com/x").
pub fn site(text: &str) -> Option<(String, String)> {
    let t = text.trim();
    if t.is_empty() || t.contains(' ') || !t.contains('.') { return None; }
    let url = if t.starts_with("http://") || t.starts_with("https://") { t.to_string() } else { format!("https://{t}") };
    let host = url.split("://").nth(1)?.split(['/', '?', '#']).next()?.to_string();
    Some((host.strip_prefix("www.").unwrap_or(&host).to_string(), url))
}

#[cfg(test)]
mod tests {
    use super::{month_grid, site};

    #[test]
    fn endereco_vira_atalho() {
        assert_eq!(site("www.youtube.com/watch?v=1"), Some(("youtube.com".into(), "https://www.youtube.com/watch?v=1".into())));
        assert_eq!(site(" http://exemplo.com.br "), Some(("exemplo.com.br".into(), "http://exemplo.com.br".into())));
        assert_eq!(site("não é site"), None);
    }

    #[test]
    fn calendario_de_novembro_2026() {
        // 1º de novembro de 2026 é domingo.
        let g = month_grid(2026, 11, (2026, 11, 25));
        assert_eq!(g[0], (1, true, false));
        assert_eq!(g[24], (25, true, true));
        assert_eq!(g[29], (30, true, false));
        assert_eq!(g[30], (1, false, false)); // dezembro
        // Fevereiro de 2024 (bissexto) começa na quinta: casas anteriores são de janeiro (28..31).
        let f = month_grid(2024, 2, (0, 0, 0));
        assert_eq!(f[0], (28, false, false));
        assert_eq!(f[4], (1, true, false));
        assert_eq!(f[32], (29, true, false));
    }
}

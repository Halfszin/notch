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
    n.set_toast_glyph(glyph.into());
    n.set_toast_text(text.into());
    n.set_toast_color(color);
    n.set_toasting(true);
    let weak = n.as_weak();
    TOAST_TIMER.with(|t| t.start(TimerMode::SingleShot, Duration::from_secs(secs), move || {
        if let Some(n) = weak.upgrade() { n.set_toasting(false); }
    }));
}

pub fn setup(notch: &Notch) {
    battery_and_audio(notch);
    calendar(notch);
    notes(notch);
    clipboard(notch);
    system(notch);
    shortcuts(notch);
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
            if w != charging {
                if charging { toast(&n, "\u{E945}", &format!("Carregando · {pct}%"), GREEN, 4); }
                else { toast(&n, "\u{E83F}", &format!("Na bateria · {pct}%"), if pct <= 20 { RED } else { GREEN }, 4); }
            }
        }
        *was = Some(charging);
        // Bateria fraca: avisa em 20% e de novo em 10% (rearma quando carregar).
        let mut at = warn_at.borrow_mut();
        if charging { *at = 20; } else if pct <= *at {
            toast(&n, "\u{E83F}", &format!("Bateria fraca · {pct}%"), RED, 6);
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
                    if changed { toast(&n, "\u{E7F6}", &label, WHITE, 4); }
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

#[derive(Deserialize, Clone)]
#[serde(rename_all = "PascalCase")]
struct Shortcut { name: String, target: String }

fn shortcuts(notch: &Notch) {
    let user: Vec<Shortcut> = store::load("atalhos.json");
    let mut items = vec![
        ShortcutItem { name: "Gravar tela".into(), glyph: "\u{E714}".into(), tint: RED },
        ShortcutItem { name: "Print".into(), glyph: "\u{E722}".into(), tint: RED },
    ];
    for s in &user {
        let is_url = s.target.starts_with("http://") || s.target.starts_with("https://");
        let is_dir = std::path::Path::new(&s.target).is_dir();
        items.push(ShortcutItem {
            name: s.name.clone().into(),
            glyph: if is_url { "\u{E774}" } else if is_dir { "\u{E8B7}" } else { "\u{E7AC}" }.into(),
            tint: if is_url { Color::from_rgb_u8(0x0A, 0x84, 0xFF) } else if is_dir { ORANGE } else { WHITE },
        });
    }
    notch.set_shortcuts(ModelRc::new(VecModel::from(items)));
    notch.on_open_shortcut(move |i| match i {
        0 => winapi::press_keys(&[0x5B, 0x10, 0x52]), // Win+Shift+R: gravação da Ferramenta de Captura
        1 => winapi::open("ms-screenclip:"),
        _ => if let Some(s) = user.get(i as usize - 2) { winapi::open(&s.target) },
    });
}

#[cfg(test)]
mod tests {
    use super::month_grid;

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

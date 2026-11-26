// Páginas do notch: tarefas e foco.
use crate::store::{self, Task};
use crate::{Notch, TaskRow};
use slint::{ComponentHandle, ModelRc, Timer, TimerMode, VecModel};
use std::cell::RefCell;
use std::rc::Rc;
use std::time::{Duration, Instant};

const TASKS: &str = "tarefas.json";

pub fn setup(notch: &Notch) {
    // ---- tarefas ----
    let tasks: Rc<RefCell<Vec<Task>>> = Rc::new(RefCell::new(store::load(TASKS)));
    let show = {
        let weak = notch.as_weak();
        let tasks = tasks.clone();
        move || {
            let Some(n) = weak.upgrade() else { return };
            let t = tasks.borrow();
            // Pendentes primeiro, feitas no fim (como no Nusa).
            let mut rows: Vec<TaskRow> = t.iter().filter(|x| !x.done).chain(t.iter().filter(|x| x.done))
                .map(|x| TaskRow { title: x.title.clone().into(), done: x.done, due: due_text(x.due.as_deref()).into() })
                .collect();
            rows.truncate(50);
            n.set_tasks(ModelRc::new(VecModel::from(rows)));
            n.set_tasks_done(t.iter().filter(|x| x.done).count() as i32);
            n.set_tasks_total(t.len() as i32);
        }
    };
    show();
    let show = Rc::new(show);
    {
        let (tasks, show) = (tasks.clone(), show.clone());
        notch.on_toggle_task(move |i| {
            {
                let mut t = tasks.borrow_mut();
                // O índice é o da lista mostrada (pendentes antes das feitas).
                let order: Vec<usize> = (0..t.len()).filter(|&k| !t[k].done).chain((0..t.len()).filter(|&k| t[k].done)).collect();
                if let Some(&k) = order.get(i as usize) { t[k].done = !t[k].done; }
                store::save(TASKS, &*t);
            }
            show();
        });
    }
    {
        let (tasks, show) = (tasks.clone(), show.clone());
        notch.on_add_task(move |input| {
            if input.trim().is_empty() { return; }
            // "Ligar pro João às 15:30" vira tarefa com lembrete (mesma regra do Orbi em C#).
            let (title, due) = crate::taskparse::parse(&input, crate::system::now());
            let title = if title.is_empty() { input.trim().to_string() } else { title };
            tasks.borrow_mut().insert(0, Task { title, due: due.map(|d| d.iso()), ..Default::default() });
            store::save(TASKS, &*tasks.borrow());
            show();
        });
    }
    {
        let (tasks, show) = (tasks.clone(), show.clone());
        notch.on_clear_done(move || {
            tasks.borrow_mut().retain(|t| !t.done);
            store::save(TASKS, &*tasks.borrow());
            show();
        });
    }

    // ---- lembretes: na hora marcada, aviso no notch (cada um uma vez só) ----
    {
        let (tasks, show, weak) = (tasks.clone(), show.clone(), notch.as_weak());
        let timer = Timer::default();
        timer.start(TimerMode::Repeated, Duration::from_secs(1), move || {
            let now = crate::system::now().iso();
            let mut fired = Vec::new();
            {
                let mut t = tasks.borrow_mut();
                for task in t.iter_mut().filter(|x| !x.done && !x.notified) {
                    // Datas no formato aaaa-mm-ddThh:mm:ss: comparar o texto já compara a data.
                    if task.due.as_deref().is_some_and(|d| d <= now.as_str()) { task.notified = true; fired.push(task.title.clone()); }
                }
                if !fired.is_empty() { store::save(TASKS, &*t); }
            }
            if let (Some(n), Some(title), true) = (weak.upgrade(), fired.first(), crate::settings::get().reminders) {
                crate::features::toast(&n, "\u{E787}", &format!("Lembrete: {title}"), slint::Color::from_rgb_u8(0xFF, 0x9F, 0x0A), 10);
                crate::features::sound("lembrete");
                show();
            }
        });
        std::mem::forget(timer);
    }

    // ---- foco ----
    struct Focus { minutes: u64, left: Duration, ends: Option<Instant> }
    let focus = Rc::new(RefCell::new(Focus { minutes: 25, left: Duration::from_secs(25 * 60), ends: None }));
    let render = {
        let (weak, focus) = (notch.as_weak(), focus.clone());
        Rc::new(move || {
            let Some(n) = weak.upgrade() else { return };
            let mut f = focus.borrow_mut();
            if let Some(end) = f.ends {
                f.left = end.saturating_duration_since(Instant::now());
                if f.left.is_zero() {
                    f.ends = None;
                    crate::features::toast(&n, "\u{E916}", "Foco terminou", slint::Color::from_rgb_u8(0xFF, 0x9F, 0x0A), 6);
                    crate::features::sound("timer");
                }
            }
            let secs = f.left.as_secs() + u64::from(f.left.subsec_nanos() > 0); // arredonda para cima
            let text = format!("{}:{:02}", secs / 60, secs % 60);
            n.set_focus_text(text.clone().into());
            n.set_focus_running(f.ends.is_some());
            n.set_focus_minutes(f.minutes as i32);
            n.set_closed_text(if f.ends.is_some() { text.into() } else { n.get_clock() });
        })
    };
    {
        let (focus, render) = (focus.clone(), render.clone());
        notch.on_focus_toggle(move || {
            {
                let mut f = focus.borrow_mut();
                f.ends = match f.ends {
                    Some(_) => None, // pausa: "left" já guarda o que falta
                    None => {
                        if f.left.is_zero() { f.left = Duration::from_secs(f.minutes * 60); }
                        Some(Instant::now() + f.left)
                    }
                };
            }
            render();
        });
    }
    {
        let (focus, render) = (focus.clone(), render.clone());
        notch.on_focus_reset(move || {
            { let mut f = focus.borrow_mut(); f.ends = None; f.left = Duration::from_secs(f.minutes * 60); }
            render();
        });
    }
    {
        let (focus, render) = (focus.clone(), render.clone());
        notch.on_focus_preset(move |m| {
            { let mut f = focus.borrow_mut(); f.minutes = m.max(1) as u64; f.ends = None; f.left = Duration::from_secs(f.minutes * 60); }
            render();
        });
    }
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_millis(250), move || render());
    std::mem::forget(timer);
}

/// "2026-11-25T15:30:00" → "15:30" (só a hora; a data fica no Orbi em C# por enquanto).
pub fn due_text(due: Option<&str>) -> String {
    due.and_then(|d| d.split('T').nth(1)).map(|t| t.chars().take(5).collect()).unwrap_or_default()
}

#[cfg(test)]
mod tests {
    #[test]
    fn hora_da_tarefa() {
        assert_eq!(super::due_text(Some("2026-11-25T15:30:00")), "15:30");
        assert_eq!(super::due_text(None), "");
    }
}

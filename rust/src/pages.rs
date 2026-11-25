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
        notch.on_add_task(move |title| {
            let title = title.trim().to_string();
            if title.is_empty() { return; }
            tasks.borrow_mut().insert(0, Task { title, ..Default::default() });
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
                if f.left.is_zero() { f.ends = None; }
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

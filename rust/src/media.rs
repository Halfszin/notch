// Música: o que está tocando no Windows (Spotify primeiro), com capa e a cor da capa para o degradê.
use slint::{ComponentHandle, Image, Rgba8Pixel, SharedPixelBuffer};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSession as Session, GlobalSystemMediaTransportControlsSessionManager as Manager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
};
use windows::Storage::Streams::{Buffer, InputStreamOptions};

use crate::Notch;

enum Command { PlayPause, Next, Previous }

pub fn start(notch: &Notch) {
    let (tx, rx) = mpsc::channel::<Command>();
    let t = tx.clone();
    notch.on_play_pause(move || { let _ = t.send(Command::PlayPause); });
    let t = tx.clone();
    notch.on_next(move || { let _ = t.send(Command::Next); });
    notch.on_previous(move || { let _ = tx.send(Command::Previous); });

    let weak = notch.as_weak();
    std::thread::spawn(move || {
        let Ok(manager) = Manager::RequestAsync().and_then(|op| op.join()) else { return };
        let mut last_key = String::new();
        // Letra da música atual (buscada em outra thread quando a música muda).
        let lyrics: Arc<Mutex<(String, Option<Vec<crate::lyrics::Line>>)>> = Arc::new(Mutex::new((String::new(), None)));
        loop {
            let session = current(&manager);
            if let Some(s) = &session {
                while let Ok(cmd) = rx.try_recv() {
                    let _ = match cmd {
                        Command::PlayPause => s.TryTogglePlayPauseAsync().and_then(|o| o.join()),
                        Command::Next => s.TrySkipNextAsync().and_then(|o| o.join()),
                        Command::Previous => s.TrySkipPreviousAsync().and_then(|o| o.join()),
                    };
                }
            }
            let info = session.as_ref().and_then(read);
            let key = info.as_ref().map(|i| format!("{}|{}", i.title, i.artist)).unwrap_or_default();
            // Capa só quando a música muda (ler a imagem a cada segundo seria desperdício).
            let cover = if key != last_key { session.as_ref().and_then(cover_of) } else { None };
            let changed = key != last_key;
            if changed {
                if let Some(i) = &info {
                    let (lyrics, key, artist, title, len) = (lyrics.clone(), key.clone(), i.artist.clone(), i.title.clone(), i.len);
                    *lyrics.lock().unwrap() = (key.clone(), None);
                    std::thread::spawn(move || {
                        let found = crate::lyrics::fetch(&artist, &title, len);
                        let mut l = lyrics.lock().unwrap();
                        if l.0 == key { l.1 = found; } // ainda é a mesma música
                    });
                }
            }
            last_key = key;
            // Linhas da letra em volta do ponto atual.
            let lines: Option<[String; 4]> = info.as_ref().and_then(|i| {
                let l = lyrics.lock().unwrap();
                let all = l.1.as_ref()?;
                let at = crate::lyrics::index_at(all, i.pos + 0.3); // um pouco adiantado: a linha acende quando começa a ser cantada
                let get = |k: Option<usize>| k.and_then(|k| all.get(k)).map(|x| x.text.clone()).unwrap_or_default();
                Some([get(at.and_then(|a| a.checked_sub(1))), get(at), get(Some(at.map_or(0, |a| a + 1))), get(Some(at.map_or(1, |a| a + 2)))])
            });
            let weak = weak.clone();
            let _ = slint::invoke_from_event_loop(move || {
                let Some(n) = weak.upgrade() else { return };
                match info {
                    Some(i) => {
                        n.set_song(i.title.into());
                        n.set_artist(i.artist.into());
                        n.set_playing(i.playing);
                        n.set_progress(i.progress);
                        n.set_elapsed(i.elapsed.into());
                        n.set_remaining(i.remaining.into());
                    }
                    None => {
                        n.set_song("Nada tocando".into());
                        n.set_artist("".into());
                        n.set_playing(false);
                        n.set_progress(0.0);
                    }
                }
                n.set_has_lyrics(lines.is_some() && crate::settings::get().lyrics);
                if let Some([prev, now, next, next2]) = lines {
                    n.set_lyric_prev(prev.into());
                    n.set_lyric_now(if now.is_empty() { "♪".into() } else { now.into() });
                    n.set_lyric_next(next.into());
                    n.set_lyric_next2(next2.into());
                }
                if changed {
                    match cover {
                        Some((buffer, accent)) => {
                            n.set_cover(Image::from_rgba8(buffer));
                            n.set_accent(accent);
                            n.set_has_cover(true);
                        }
                        None => n.set_has_cover(false),
                    }
                }
            });
            std::thread::sleep(Duration::from_millis(700));
        }
    });
}

fn current(manager: &Manager) -> Option<Session> {
    let sessions = manager.GetSessions().ok()?;
    for s in sessions {
        if s.SourceAppUserModelId().map(|id| id.to_string().to_lowercase().contains("spotify")).unwrap_or(false) {
            return Some(s);
        }
    }
    manager.GetCurrentSession().ok()
}

struct Info { title: String, artist: String, playing: bool, progress: f32, elapsed: String, remaining: String, pos: f64, len: f64 }

fn read(s: &Session) -> Option<Info> {
    let props = s.TryGetMediaPropertiesAsync().ok()?.join().ok()?;
    let title = props.Title().ok()?.to_string();
    if title.is_empty() { return None; }
    let playing = s.GetPlaybackInfo().ok()?.PlaybackStatus().ok()? == Status::Playing;
    let tl = s.GetTimelineProperties().ok()?;
    let ticks = |d: windows::Foundation::TimeSpan| d.Duration as f64 / 10_000_000.0;
    let len = ticks(tl.EndTime().ok()?) - ticks(tl.StartTime().ok()?);
    let mut pos = ticks(tl.Position().ok()?);
    if playing {
        // A posição vem de quando o app avisou pela última vez: soma o tempo que passou desde então.
        let updated = tl.LastUpdatedTime().ok()?.UniversalTime;
        let now = file_time_now();
        pos += ((now - updated) as f64 / 10_000_000.0).max(0.0);
    }
    let ratio = if len > 0.0 { (pos / len).clamp(0.0, 1.0) } else { 0.0 };
    Some(Info {
        title,
        artist: props.Artist().map(|a| a.to_string()).unwrap_or_default(),
        playing,
        progress: ratio as f32,
        elapsed: fmt(len * ratio),
        remaining: format!("-{}", fmt(len * (1.0 - ratio))),
        pos,
        len,
    })
}

fn file_time_now() -> i64 {
    // DateTime do WinRT = intervalos de 100 ns desde 1601 (mesmo relógio do FILETIME).
    let ft = unsafe { windows::Win32::System::SystemInformation::GetSystemTimeAsFileTime() };
    ((ft.dwHighDateTime as i64) << 32) | ft.dwLowDateTime as i64
}

fn fmt(secs: f64) -> String {
    let s = secs.max(0.0).round() as u64;
    if s >= 3600 { format!("{}:{:02}:{:02}", s / 3600, s % 3600 / 60, s % 60) } else { format!("{}:{:02}", s / 60, s % 60) }
}

/// Capa da música + a cor dela (média dos pixels, um pouco mais escura) para o degradê do notch.
fn cover_of(s: &Session) -> Option<(SharedPixelBuffer<Rgba8Pixel>, slint::Color)> {
    let props = s.TryGetMediaPropertiesAsync().ok()?.join().ok()?;
    let stream = props.Thumbnail().ok()?.OpenReadAsync().ok()?.join().ok()?;
    let size = stream.Size().ok()? as u32;
    if size == 0 || size > 8 * 1024 * 1024 { return None; }
    let buffer = Buffer::Create(size).ok()?;
    let read = stream.ReadAsync(&buffer, size, InputStreamOptions::None).ok()?.join().ok()?;
    let reader = windows::Storage::Streams::DataReader::FromBuffer(&read).ok()?;
    let mut bytes = vec![0u8; read.Length().ok()? as usize];
    reader.ReadBytes(&mut bytes).ok()?;
    let img = image::load_from_memory(&bytes).ok()?.to_rgba8();
    let (r, g, b, n) = img.pixels().step_by(7).fold((0u64, 0u64, 0u64, 0u64), |(r, g, b, n), p| (r + p[0] as u64, g + p[1] as u64, b + p[2] as u64, n + 1));
    let accent = crate::color::accent(r / n.max(1), g / n.max(1), b / n.max(1));
    let buf = SharedPixelBuffer::<Rgba8Pixel>::clone_from_slice(img.as_raw(), img.width(), img.height());
    Some((buf, accent))
}

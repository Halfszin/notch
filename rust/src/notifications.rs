// Notificações do WhatsApp e do Discord no notch, como no Orbi em C#: lê, só para leitura, a base onde o
// próprio Windows guarda as notificações (a mesma da Central de Notificações). Apps sem pacote da Loja
// não têm outra API. O SQLite usado é o que já vem no Windows (winsqlite3.dll).

/// Apps acompanhados: (trecho do id do app no Windows, nome mostrado).
pub const APPS: [(&str, &str); 2] = [("whatsapp", "WhatsApp"), ("discord", "Discord")];

/// Título e texto do XML de uma notificação (<toast><visual><binding><text>…).
pub fn parse_toast(xml: &str) -> (String, String) {
    let re = regex::Regex::new(r"(?s)<text\b[^>]*>(.*?)</text>").unwrap();
    let texts: Vec<String> = re.captures_iter(xml)
        .map(|c| unescape(c[1].trim()))
        .filter(|t| !t.is_empty())
        .collect();
    (texts.first().cloned().unwrap_or_default(), texts.iter().skip(1).cloned().collect::<Vec<_>>().join(" "))
}

fn unescape(s: &str) -> String {
    s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'").replace("&#39;", "'").replace("&amp;", "&")
}

/// Qual dos apps acompanhados mandou (pelo id do app no Windows).
pub fn app_of(handler: &str) -> Option<&'static str> {
    let h = handler.to_lowercase();
    APPS.iter().find(|(m, _)| h.contains(m)).map(|(_, name)| *name)
}

#[cfg(windows)]
pub use db::Watcher;

#[cfg(windows)]
mod db {
    use std::ffi::{c_char, c_int, c_void, CStr};

    type Open = unsafe extern "system" fn(*const c_char, *mut *mut c_void, c_int, *const c_char) -> c_int;
    type Prepare = unsafe extern "system" fn(*mut c_void, *const c_char, c_int, *mut *mut c_void, *mut *const c_char) -> c_int;
    type Step = unsafe extern "system" fn(*mut c_void) -> c_int;
    type Bind64 = unsafe extern "system" fn(*mut c_void, c_int, i64) -> c_int;
    type Col64 = unsafe extern "system" fn(*mut c_void, c_int) -> i64;
    type ColPtr = unsafe extern "system" fn(*mut c_void, c_int) -> *const c_void;
    type ColInt = unsafe extern "system" fn(*mut c_void, c_int) -> c_int;
    type Close = unsafe extern "system" fn(*mut c_void) -> c_int;

    struct Sqlite { open: Open, prepare: Prepare, step: Step, bind64: Bind64, col64: Col64, text: ColPtr, blob: ColPtr, bytes: ColInt, finalize: Close, close: Close }

    fn load() -> Option<Sqlite> {
        use windows::core::{s, w};
        use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
        unsafe {
            let lib = LoadLibraryW(w!("winsqlite3.dll")).ok()?;
            macro_rules! f { ($name:expr) => { std::mem::transmute(GetProcAddress(lib, $name)?) } }
            Some(Sqlite {
                open: f!(s!("sqlite3_open_v2")), prepare: f!(s!("sqlite3_prepare_v2")), step: f!(s!("sqlite3_step")),
                bind64: f!(s!("sqlite3_bind_int64")), col64: f!(s!("sqlite3_column_int64")), text: f!(s!("sqlite3_column_text")),
                blob: f!(s!("sqlite3_column_blob")), bytes: f!(s!("sqlite3_column_bytes")), finalize: f!(s!("sqlite3_finalize")),
                close: f!(s!("sqlite3_close")),
            })
        }
    }

    const READONLY: c_int = 0x1;
    const ROW: c_int = 100;

    /// Lê as notificações novas desde a última leitura.
    pub struct Watcher { sql: Option<Sqlite>, path: String, last_id: i64 }

    impl Watcher {
        pub fn new() -> Self {
            let path = std::env::var("LOCALAPPDATA").unwrap_or_default() + "\\Microsoft\\Windows\\Notifications\\wpndatabase.db";
            Watcher { sql: load(), path, last_id: -1 }
        }

        /// (app, id do app no Windows, título, texto) de cada notificação nova dos apps acompanhados.
        pub fn poll(&mut self) -> Vec<(&'static str, String, String, String)> {
            let mut out = Vec::new();
            let Some(sql) = &self.sql else { return out };
            let Ok(path) = std::ffi::CString::new(self.path.clone()) else { return out };
            unsafe {
                let mut db = std::ptr::null_mut();
                if (sql.open)(path.as_ptr(), &mut db, READONLY, std::ptr::null()) != 0 {
                    (sql.close)(db);
                    return out; // base ocupada ou ausente: tenta de novo na próxima
                }
                let query = |q: &CStr, id: Option<i64>, row: &mut dyn FnMut(*mut c_void)| {
                    let mut st = std::ptr::null_mut();
                    if (sql.prepare)(db, q.as_ptr(), -1, &mut st, std::ptr::null_mut()) != 0 { return; }
                    if let Some(id) = id { (sql.bind64)(st, 1, id); }
                    while (sql.step)(st) == ROW { row(st); }
                    (sql.finalize)(st);
                };
                if self.last_id < 0 {
                    // Primeira leitura: marca onde está, para não repetir notificações antigas.
                    let mut max = 0;
                    query(c"SELECT IFNULL(MAX(Id), 0) FROM Notification", None, &mut |st| max = (sql.col64)(st, 0));
                    self.last_id = max;
                } else {
                    let mut last = self.last_id;
                    query(
                        c"SELECT n.Id, h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Id > ?1 AND n.Type = 'toast' ORDER BY n.Id",
                        Some(self.last_id),
                        &mut |st| {
                            last = (sql.col64)(st, 0);
                            let handler = (sql.text)(st, 1);
                            if handler.is_null() { return; }
                            let handler = CStr::from_ptr(handler as *const c_char).to_string_lossy().into_owned();
                            let Some(app) = super::app_of(&handler) else { return };
                            let (p, n) = ((sql.blob)(st, 2), (sql.bytes)(st, 2));
                            if p.is_null() || n <= 0 { return; }
                            let xml = String::from_utf8_lossy(std::slice::from_raw_parts(p as *const u8, n as usize));
                            let (title, body) = super::parse_toast(&xml);
                            if !title.is_empty() || !body.is_empty() { out.push((app, handler, title, body)); }
                        },
                    );
                    self.last_id = last;
                }
                (sql.close)(db);
            }
            out
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn le_titulo_e_texto() {
        let xml = r#"<toast launch="x"><visual><binding template="ToastGeneric"><text hint-maxLines="1">Maria</text><text>Bora &amp; vamos? &lt;3</text><text></text></binding></visual></toast>"#;
        assert_eq!(parse_toast(xml), ("Maria".into(), "Bora & vamos? <3".into()));
        assert_eq!(parse_toast("lixo"), (String::new(), String::new()));
    }

    #[test]
    fn reconhece_o_app() {
        assert_eq!(app_of("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App"), Some("WhatsApp"));
        assert_eq!(app_of("com.squirrel.Discord.Discord"), Some("Discord"));
        assert_eq!(app_of("Microsoft.Windows.Explorer"), None);
    }
}

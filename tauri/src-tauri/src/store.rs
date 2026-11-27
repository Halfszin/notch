// Dados salvos em %AppData%\Orbi, nos mesmos arquivos do Orbi em C# (as tarefas aparecem nas duas versões).
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use std::path::PathBuf;

pub fn dir() -> PathBuf {
    std::env::var_os("APPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir).join("Orbi")
}

pub fn load<T: DeserializeOwned + Default>(name: &str) -> T {
    std::fs::read_to_string(dir().join(name)).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default()
}

/// Grava num .tmp e troca: se der erro no meio, o arquivo anterior continua inteiro.
pub fn save<T: Serialize>(name: &str, value: &T) {
    let _ = std::fs::create_dir_all(dir());
    let path = dir().join(name);
    let tmp = path.with_extension("tmp");
    if let Ok(json) = serde_json::to_string_pretty(value) {
        if std::fs::write(&tmp, json).is_ok() {
            let _ = std::fs::rename(&tmp, &path);
        }
    }
}

// Mesmo formato do tarefas.json do Orbi em C# (nomes com maiúscula).
#[derive(Serialize, Deserialize, Clone, Default, Debug, PartialEq)]
#[serde(rename_all = "PascalCase")]
pub struct Task {
    pub title: String,
    #[serde(default)]
    pub due: Option<String>,
    #[serde(default)]
    pub done: bool,
    #[serde(default)]
    pub notified: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn le_tarefas_do_orbi_em_csharp() {
        let json = r#"[{"Title":"Ligar pro João","Due":"2026-11-25T15:30:00","Done":false,"Notified":false},{"Title":"Pão","Due":null,"Done":true,"Notified":false}]"#;
        let tasks: Vec<Task> = serde_json::from_str(json).unwrap();
        assert_eq!(tasks.len(), 2);
        assert_eq!(tasks[0].due.as_deref(), Some("2026-11-25T15:30:00"));
        assert!(tasks[1].done);
        // E grava de volta no mesmo formato.
        let back = serde_json::to_string(&tasks[0]).unwrap();
        assert!(back.contains("\"Title\":\"Ligar pro João\"") && back.contains("\"Done\":false"));
    }
}

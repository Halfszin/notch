// Entende a hora escrita na tarefa (igual ao Orbi em C#): "Ligar pro João às 15:30", "reunião amanhã 9h",
// "tirar o bolo em 20 min". Sem hora: só uma tarefa, sem lembrete.
use regex::Regex;
use std::sync::OnceLock;

/// Data e hora local simples (o suficiente para lembretes), com conta de minutos.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct When { pub year: i32, pub month: u32, pub day: u32, pub hour: u32, pub minute: u32 }

impl When {
    pub fn iso(&self) -> String {
        format!("{:04}-{:02}-{:02}T{:02}:{:02}:00", self.year, self.month, self.day, self.hour, self.minute)
    }
    fn days_in_month(y: i32, m: u32) -> u32 {
        match m { 1 | 3 | 5 | 7 | 8 | 10 | 12 => 31, 4 | 6 | 9 | 11 => 30, _ if (y % 4 == 0 && y % 100 != 0) || y % 400 == 0 => 29, _ => 28 }
    }
    pub fn add_days(mut self, n: u32) -> When {
        for _ in 0..n {
            self.day += 1;
            if self.day > Self::days_in_month(self.year, self.month) { self.day = 1; self.month += 1; }
            if self.month > 12 { self.month = 1; self.year += 1; }
        }
        self
    }
    pub fn add_minutes(self, mins: u32) -> When {
        let total = self.hour * 60 + self.minute + mins;
        let mut w = self.add_days(total / (24 * 60));
        w.hour = total % (24 * 60) / 60;
        w.minute = total % 60;
        w
    }
    fn at(self, hour: u32, minute: u32) -> When { When { hour, minute, ..self } }
}

fn re(cell: &'static OnceLock<Regex>, pattern: &str) -> &'static Regex {
    cell.get_or_init(|| Regex::new(pattern).unwrap())
}

pub fn parse(input: &str, now: When) -> (String, Option<When>) {
    static IN: OnceLock<Regex> = OnceLock::new();
    static AT: OnceLock<Regex> = OnceLock::new();
    static TOMORROW: OnceLock<Regex> = OnceLock::new();
    let text = input.trim();
    if let Some(c) = re(&IN, r"(?i)\s*\b(?:em|daqui(?: a)?)\s+(\d{1,3})\s*(min(?:utos?)?|h(?:oras?)?)\s*$").captures(text) {
        let n: u32 = c[1].parse().unwrap_or(0);
        let mins = if c[2].to_lowercase().starts_with('h') { n * 60 } else { n };
        return (clean(&text[..c.get(0).unwrap().start()]), Some(now.add_minutes(mins)));
    }
    let tomorrow_re = re(&TOMORROW, r"(?i)\s*\bamanh[ãa]\b\s*");
    let tomorrow = tomorrow_re.is_match(text);
    let text = tomorrow_re.replace_all(text, " ").trim().to_string();
    let Some(c) = re(&AT, r"(?i)\s*(?:\b(?:às|as)\s+)?\b(\d{1,2})(?::(\d{2})|h(\d{2})?)\s*$").captures(&text) else {
        // "amanhã" sem hora: 9h.
        return (clean(&text), tomorrow.then(|| now.add_days(1).at(9, 0)));
    };
    let hour: u32 = c[1].parse().unwrap_or(99);
    let minute: u32 = c.get(2).or(c.get(3)).map(|m| m.as_str().parse().unwrap_or(99)).unwrap_or(0);
    if hour > 23 || minute > 59 { return (clean(input), None); } // "99:99" não é hora
    let mut when = now.at(hour, minute);
    if tomorrow || when <= now { when = when.add_days(1); } // hora que já passou hoje = amanhã
    (clean(&text[..c.get(0).unwrap().start()]), Some(when))
}

fn clean(s: &str) -> String {
    s.split_whitespace().collect::<Vec<_>>().join(" ").trim_end_matches([',', '-', ' ']).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    const NOW: When = When { year: 2026, month: 11, day: 24, hour: 14, minute: 0 };
    fn at(d: u32, h: u32, m: u32) -> Option<When> { Some(When { year: 2026, month: 11, day: d, hour: h, minute: m }) }

    #[test]
    fn mesmos_casos_do_orbi_em_csharp() {
        let cases: [(&str, &str, Option<When>); 9] = [
            ("Ligar pro João às 15:30", "Ligar pro João", at(24, 15, 30)),
            ("reunião 9h", "reunião", at(25, 9, 0)),
            ("reunião amanhã 9h30", "reunião", at(25, 9, 30)),
            ("academia 18h", "academia", at(24, 18, 0)),
            ("tirar o bolo em 20 min", "tirar o bolo", at(24, 14, 20)),
            ("pagar boleto amanhã", "pagar boleto", at(25, 9, 0)),
            ("comprar pão", "comprar pão", None),
            ("ver episódio 12", "ver episódio 12", None),
            ("código 99:99", "código 99:99", None),
        ];
        for (input, title, due) in cases {
            assert_eq!(parse(input, NOW), (title.to_string(), due), "{input}");
        }
        assert_eq!(When { year: 2026, month: 12, day: 31, hour: 23, minute: 50 }.add_minutes(20).iso(), "2027-01-01T00:10:00");
    }
}

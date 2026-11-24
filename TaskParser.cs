using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbi;

// Entende a hora escrita na própria tarefa: "Ligar pro João às 15:30", "reunião amanhã 9h",
// "tirar o bolo em 20 min". Sem hora: é só uma tarefa, sem lembrete.
public static class TaskParser
{
    static readonly Regex In = new(@"\s*\b(?:em|daqui(?: a)?)\s+(\d{1,3})\s*(min(?:utos?)?|h(?:oras?)?)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex At = new(@"\s*(?:\b(?:às|as|as)\s+)?\b(\d{1,2})(?::(\d{2})|h(\d{2})?)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex Tomorrow = new(@"\s*\bamanh[ãa]\b\s*", RegexOptions.IgnoreCase);

    public static (string Title, DateTime? Due) Parse(string input, DateTime now)
    {
        string text = input.Trim();
        var m = In.Match(text);
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var due = m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? now.AddHours(n) : now.AddMinutes(n);
            return (Clean(text[..m.Index]), due);
        }

        bool tomorrow = Tomorrow.IsMatch(text);
        text = Tomorrow.Replace(text, " ").Trim();
        m = At.Match(text);
        if (!m.Success) return (Clean(text), tomorrow ? now.Date.AddDays(1).AddHours(9) : null); // "amanhã" sem hora: 9h

        int hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string minText = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : "0";
        int minute = int.Parse(minText, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59) return (Clean(input), null); // "99:99" não é hora
        var when = now.Date.AddHours(hour).AddMinutes(minute);
        if (tomorrow) when = when.AddDays(1);
        else if (when <= now) when = when.AddDays(1); // hora que já passou hoje = amanhã
        return (Clean(text[..m.Index]), when);
    }

    static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(',', '-', ' ');
}

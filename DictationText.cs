using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbi;

// O que acontece com o texto ditado antes de ser colado: atalhos de texto (frase curta → texto inteiro),
// trocas de palavras e as contas das estatísticas.
public static class DictationText
{
    /// <summary>Linhas "de => para" (comentários com # e linhas vazias são ignorados).</summary>
    public static List<(string From, string To)> ParsePairs(string lines) =>
        lines.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split("=>", 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => (p[0].Trim(), p[1].Trim().Replace("\\n", "\n")))
            .ToList();

    public static string Apply(string text, IEnumerable<(string From, string To)> snippets, IEnumerable<(string From, string To)> replacements)
    {
        string spoken = text.Trim();
        // Atalho de texto: a fala inteira é a frase-chave (sem ligar para maiúsculas, acentos e pontuação no fim).
        foreach (var (from, to) in snippets)
            if (Key(spoken) == Key(from)) return to;
        foreach (var (from, to) in replacements)
            spoken = Regex.Replace(spoken, $@"(?<!\w){Regex.Escape(from)}(?!\w)", to.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return spoken;
    }

    static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(c) || c == ' ')) sb.Append(char.ToLowerInvariant(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static int Words(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+").Count;

    // Digitando a ~40 palavras por minuto: o que a fala poupou.
    public static TimeSpan Saved(int words, TimeSpan spoken) => TimeSpan.FromMinutes(words / 40.0) - spoken is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero;

    /// <summary>Dias seguidos com ditado, terminando hoje (ou ontem, se hoje ainda não ditou).</summary>
    public static int Streak(IEnumerable<DateTime> days, DateTime today)
    {
        var set = days.Select(d => d.Date).ToHashSet();
        var day = set.Contains(today.Date) ? today.Date : today.Date.AddDays(-1);
        int n = 0;
        while (set.Contains(day)) { n++; day = day.AddDays(-1); }
        return n;
    }
}

using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Orbi;

// Soma o consumo de tokens dos agentes do Claude Code a partir dos registros que ele grava no PC
// (~/.claude/projects/**/*.jsonl: uma linha por mensagem, com "usage" nas respostas do modelo).
public static class UsageLog
{
    public sealed record Tokens(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public long Total => Input + Output + CacheRead + CacheWrite;
        public static Tokens operator +(Tokens a, Tokens b) => new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
        public static readonly Tokens Zero = new(0, 0, 0, 0);
    }

    public sealed record Summary(Tokens Today, Tokens Week, int TodayMessages, int WeekMessages, IReadOnlyDictionary<string, long> WeekByModel);

    public static Summary Summarize(IEnumerable<string> lines, DateTime now)
    {
        var seen = new HashSet<string>();
        Tokens today = Tokens.Zero, week = Tokens.Zero;
        int todayN = 0, weekN = 0;
        var byModel = new Dictionary<string, long>();
        foreach (var line in lines)
        {
            if (!line.Contains("\"usage\"")) continue; // pula rápido as linhas sem consumo
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u)) continue;
                if (!root.TryGetProperty("timestamp", out var ts) || !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
                var local = utc.ToLocalTime();
                if (local < now.Date.AddDays(-6) || local > now.AddMinutes(5)) continue;
                // A mesma resposta aparece em várias linhas (uma por bloco): conta uma vez só.
                string id = (msg.TryGetProperty("id", out var mid) ? mid.GetString() : null) + "|" + (root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null);
                if (id != "|" && !seen.Add(id)) continue;
                var t = new Tokens(Num(u, "input_tokens"), Num(u, "output_tokens"), Num(u, "cache_read_input_tokens"), Num(u, "cache_creation_input_tokens"));
                week += t;
                weekN++;
                if (local.Date == now.Date) { today += t; todayN++; }
                string model = msg.TryGetProperty("model", out var mo) ? mo.GetString() ?? "?" : "?";
                byModel[model] = byModel.GetValueOrDefault(model) + t.Total;
            }
            catch (JsonException) { } // linha cortada (o Claude Code pode estar escrevendo agora)
        }
        return new Summary(today, week, todayN, weekN, byModel);
    }

    static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>Arquivos de registro mexidos nos últimos 7 dias (o Claude Code guarda tudo em ~/.claude, ou em CLAUDE_CONFIG_DIR).</summary>
    public static IEnumerable<string> RecentFiles(DateTime now)
    {
        string root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        string projects = Path.Combine(root, "projects");
        if (!Directory.Exists(projects)) return [];
        return Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories)
            .Where(f => File.GetLastWriteTime(f) >= now.Date.AddDays(-6));
    }
}

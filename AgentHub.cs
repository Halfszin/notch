using System.IO;
using System.Text.Json.Nodes;

namespace Orbi;

// Estado das sessões do Claude Code a partir dos eventos dos hooks, e as decisões de permissão.
public sealed class AgentHub
{
    public sealed class Session
    {
        public string Id = "", Project = "", Status = "";
        public bool Busy;
        public DateTime Updated;
    }

    public sealed class Permission
    {
        public string Project = "", Tool = "", Detail = "", What = "", Code = "";
        public JsonNode? Suggestions;
        public readonly TaskCompletionSource<string> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed record PlanWindow(double UsedPct, DateTimeOffset ResetsAt);
    public sealed record PlanUsage(PlanWindow? FiveHour, PlanWindow? SevenDay);

    public readonly Dictionary<string, Session> Sessions = new();
    public readonly List<Permission> Pending = new();
    public PlanUsage? Plan { get; private set; }
    public bool Busy => Sessions.Values.Any(s => s.Busy);

    public event Action? Changed;
    public event Action<string, string>? Finished;         // projeto, última mensagem do Claude
    public event Action<string, string>? NeedsYou;         // projeto, mensagem
    public event Action<Permission>? PermissionAsked;

    public static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(110);

    /// <summary>Um evento chegou (na thread da interface). Devolve o texto de resposta para o hook.</summary>
    public async Task<string> Handle(string evt, JsonObject p)
    {
        if (evt == "statusline")
        {
            if (ParsePlan(p) is { } plan) Plan = Merge(Plan, plan);
            Changed?.Invoke();
            return StatusLine(p, Plan);
        }

        string id = Str(p, "session_id") ?? "?";
        string project = Path.GetFileName(Str(p, "cwd")?.TrimEnd('/', '\\') ?? "") is { Length: > 0 } name ? name : "Claude Code";
        if (!Sessions.TryGetValue(id, out var s)) Sessions[id] = s = new Session { Id = id };
        s.Project = project;
        s.Updated = DateTime.Now;
        string? answer = null;
        switch (evt)
        {
            case "SessionStart": s.Status = "Começou"; s.Busy = false; break;
            case "UserPromptSubmit": s.Status = "Pensando…"; s.Busy = true; break;
            case "PreToolUse": s.Status = Describe(Str(p, "tool_name") ?? "", p["tool_input"]); s.Busy = true; break;
            case "Notification":
                s.Status = "Esperando você"; s.Busy = false;
                NeedsYou?.Invoke(project, Str(p, "message") ?? "O Claude precisa de você");
                break;
            case "Stop":
                s.Status = "Terminou"; s.Busy = false;
                Finished?.Invoke(project, Str(p, "last_assistant_message") ?? LastAssistantText(Str(p, "transcript_path")) ?? "");
                break;
            case "SessionEnd": Sessions.Remove(id); break;
            case "PermissionRequest":
                s.Status = "Pedindo permissão"; s.Busy = false;
                string tool = Str(p, "tool_name") ?? "";
                var input = p["tool_input"];
                var perm = new Permission
                {
                    Project = project, Tool = tool, Detail = Describe(tool, input, full: true), Suggestions = p["permission_suggestions"]?.DeepClone(),
                    What = tool switch
                    {
                        "Bash" or "PowerShell" => "Claude Code quer rodar um comando",
                        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "Claude Code quer mexer num arquivo",
                        "WebFetch" or "WebSearch" => "Claude Code quer acessar a web",
                        _ => $"Claude Code quer usar {tool}",
                    },
                    Code = Str(input, "command") ?? Str(input, "file_path") ?? Str(input, "url") ?? Str(input, "query") ?? tool,
                };
                Pending.Add(perm);
                Changed?.Invoke();
                PermissionAsked?.Invoke(perm);
                // Sem resposta a tempo: devolve vazio e o Claude Code pergunta no terminal, como sempre.
                var done = await Task.WhenAny(perm.Answer.Task, Task.Delay(DecisionTimeout));
                Pending.Remove(perm);
                answer = done == perm.Answer.Task ? perm.Answer.Task.Result : "";
                if (Sessions.TryGetValue(id, out var still)) { still.Status = answer.Contains("deny") ? "Permissão negada" : "Trabalhando"; still.Busy = !answer.Contains("deny"); }
                break;
        }
        // Sessões paradas há muito tempo somem da lista.
        foreach (var old in Sessions.Values.Where(x => !x.Busy && DateTime.Now - x.Updated > TimeSpan.FromMinutes(30)).ToList()) Sessions.Remove(old.Id);
        Changed?.Invoke();
        return answer ?? "{}";
    }

    public static void Decide(Permission perm, string choice) => perm.Answer.TrySetResult(PermissionResponse(choice, perm.Suggestions));

    public static string PermissionResponse(string choice, JsonNode? suggestions)
    {
        var decision = new JsonObject { ["behavior"] = choice == "deny" ? "deny" : "allow" };
        if (choice == "deny") decision["message"] = "Negado pelo Orbi";
        if (choice == "always" && suggestions is JsonArray { Count: > 0 }) decision["updatedPermissions"] = suggestions.DeepClone();
        return new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision } }.ToJsonString();
    }

    public static string Describe(string tool, JsonNode? input, bool full = false)
    {
        string? file = Str(input, "file_path") ?? Str(input, "notebook_path");
        string name = file != null ? Path.GetFileName(file) : "";
        string? cmd = Str(input, "command");
        return tool switch
        {
            "Edit" or "MultiEdit" or "NotebookEdit" => $"Editando {name}",
            "Write" => $"Criando {name}",
            "Read" => $"Lendo {name}",
            "Bash" or "PowerShell" when cmd != null => full ? $"Rodar o comando:\n{cmd}" : $"Rodando: {Short(cmd)}",
            "Grep" or "Glob" => "Procurando no código",
            "WebSearch" => full ? $"Pesquisar na web: {Str(input, "query")}" : "Pesquisando na web",
            "WebFetch" => full ? $"Abrir a página: {Str(input, "url")}" : "Lendo uma página da web",
            "Task" or "Agent" => "Chamando um subagente",
            "TodoWrite" => "Organizando a lista de tarefas",
            "" => "Trabalhando",
            _ => $"Usando {tool}",
        };
    }

    /// <summary>Último texto que o Claude escreveu, lido do fim do registro da sessão (para o aviso de "terminou").</summary>
    public static string? LastAssistantText(string? transcript)
    {
        try
        {
            if (transcript == null || !File.Exists(transcript)) return null;
            using var fs = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, fs.Length - 256 * 1024), SeekOrigin.Begin); // só o fim: o registro pode ser enorme
            var lines = new StreamReader(fs).ReadToEnd().Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"assistant\"")) continue;
                try
                {
                    if (JsonNode.Parse(lines[i]) is not JsonObject row || Str(row, "type") != "assistant" || row["message"]?["content"] is not JsonArray content) continue;
                    var text = string.Join(" ", content.OfType<JsonObject>().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text"))).Trim();
                    if (text.Length > 0) return text.Length > 200 ? text[..200] + "…" : text;
                }
                catch (System.Text.Json.JsonException) { } // linha pela metade
            }
        }
        catch (IOException) { }
        return null;
    }

    static string Short(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // ---------- limites do plano (vêm na status line do Claude Code, campo rate_limits) ----------

    public static PlanUsage? ParsePlan(JsonObject payload)
    {
        if (payload["rate_limits"] is not JsonObject limits) return null;
        var five = Window(limits["five_hour"]);
        var week = Window(limits["seven_day"]);
        return five == null && week == null ? null : new PlanUsage(five, week);
    }

    static PlanWindow? Window(JsonNode? node)
    {
        if (node is not JsonObject o || o["used_percentage"] is not JsonValue pv || !pv.TryGetValue(out double pct)) return null;
        if (pct < 0 || pct > 200) return null;
        if (o["resets_at"] is not JsonValue rv || !rv.TryGetValue(out double epoch) || epoch <= 0) return null;
        // Mais de 400 dias no futuro: provavelmente veio em milissegundos; melhor ignorar do que mostrar errado.
        if (epoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 400L * 86400) return null;
        return new PlanWindow(Math.Min(100, pct), DateTimeOffset.FromUnixTimeSeconds((long)epoch));
    }

    // Cada sessão manda a sua cópia; uma cópia velha não pode fazer o número voltar.
    static PlanUsage Merge(PlanUsage? cur, PlanUsage inc) => new(Latest(cur?.FiveHour, inc.FiveHour), Latest(cur?.SevenDay, inc.SevenDay));

    static PlanWindow? Latest(PlanWindow? cur, PlanWindow? inc) =>
        cur == null ? inc : inc == null ? cur
        : inc.ResetsAt > cur.ResetsAt ? inc
        : inc.ResetsAt < cur.ResetsAt ? cur
        : inc.UsedPct >= cur.UsedPct ? inc : cur;

    static string StatusLine(JsonObject p, PlanUsage? plan)
    {
        string model = p["model"] is JsonObject m && Str(m, "display_name") is { } d ? d : "Claude";
        var parts = new List<string> { model };
        if (plan?.FiveHour is { } f) parts.Add($"5h {f.UsedPct:0}%");
        if (plan?.SevenDay is { } w) parts.Add($"semana {w.UsedPct:0}%");
        return string.Join(" · ", parts);
    }
}

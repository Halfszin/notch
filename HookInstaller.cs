using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbi;

// Liga o Claude Code ao Orbi pelos "hooks" (como o coucou faz): a cada evento o Claude Code roda um
// curl que manda o evento para o Orbi. Regras: backup antes de gravar, só mexe nas entradas do Orbi
// (marcadas com "# orbi-hook"), nunca troca uma status line que já é sua, e desligar tira só o que é nosso.
public static class HookInstaller
{
    public const string Marker = "# orbi-hook";

    // PermissionRequest espera você decidir, por isso o tempo maior.
    public static readonly (string Event, int Timeout)[] Events =
    {
        ("SessionStart", 10), ("SessionEnd", 10), ("UserPromptSubmit", 10), ("PreToolUse", 10),
        ("PermissionRequest", 120), ("Notification", 10), ("Stop", 10),
    };

    public static string ClaudeDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string SettingsPath => Path.Combine(ClaudeDir, "settings.json");

    // No Windows o Claude Code roda hooks pelo Git Bash: um curl simples funciona e é rápido.
    // "|| true": com o Orbi fechado o Claude Code segue normal, sem erro.
    public static string Command(int port, string token, string evt, int maxSeconds) =>
        $"curl -s -m {maxSeconds} --data-binary @- http://127.0.0.1:{port}/{token}/{evt} || true {Marker}";

    static bool IsOursCommand(JsonNode? node) =>
        node is JsonObject o && o["command"] is JsonValue v && v.TryGetValue(out string? c) && c.Contains(Marker);

    static bool IsOursEntry(JsonNode? entry) =>
        entry is JsonObject o && o["hooks"] is JsonArray hs && hs.Any(IsOursCommand);

    public static JsonObject Merge(JsonObject? existing, int port, string token, out bool statusLineAdded)
    {
        var root = existing?.DeepClone().AsObject() ?? new JsonObject();
        JsonObject hooks = root["hooks"] switch
        {
            null => (JsonObject)(root["hooks"] = new JsonObject()),
            JsonObject h => h,
            _ => throw new InvalidDataException("O \"hooks\" do settings.json está num formato que o Orbi não conhece; nada foi alterado."),
        };
        foreach (var (evt, timeout) in Events)
        {
            JsonArray list = hooks[evt] switch
            {
                null => (JsonArray)(hooks[evt] = new JsonArray()),
                JsonArray a => a,
                _ => throw new InvalidDataException($"O hook \"{evt}\" do settings.json está num formato que o Orbi não conhece; nada foi alterado."),
            };
            for (int i = list.Count - 1; i >= 0; i--) if (IsOursEntry(list[i])) list.RemoveAt(i);
            list.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Command(port, token, evt, timeout - 5),
                    ["timeout"] = timeout,
                }),
            });
        }
        // Status line (limites do plano): só se você ainda não tem uma, ou se a que existe já é do Orbi.
        statusLineAdded = root["statusLine"] is null || IsOursCommand(root["statusLine"]);
        if (statusLineAdded)
            root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(port, token, "statusline", 2) };
        return root;
    }

    public static JsonObject Unmerge(JsonObject existing)
    {
        var root = existing.DeepClone().AsObject();
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var key in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[key] is not JsonArray list) continue;
                bool hadOurs = false;
                for (int i = list.Count - 1; i >= 0; i--) if (IsOursEntry(list[i])) { list.RemoveAt(i); hadOurs = true; }
                if (hadOurs && list.Count == 0) hooks.Remove(key);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }
        if (IsOursCommand(root["statusLine"])) root.Remove("statusLine");
        return root;
    }

    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    static JsonObject? Read()
    {
        if (!File.Exists(SettingsPath)) return null;
        string text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
               ?? throw new InvalidDataException("O settings.json do Claude Code não é um objeto JSON; nada foi alterado.");
    }

    public static bool IsInstalled()
    {
        try { return Read()?["hooks"] is JsonObject h && h.Any(kv => kv.Value is JsonArray a && a.Any(IsOursEntry)); }
        catch { return false; }
    }

    /// <summary>Grava os hooks. Devolve o caminho do backup (null se não havia arquivo) e se a status line entrou.</summary>
    public static (string? Backup, bool StatusLine) Install(int port, string token)
    {
        var current = Read();
        var merged = Merge(current, port, token, out bool statusLine);
        return (Write(merged, current != null), statusLine);
    }

    public static string? Uninstall()
    {
        var current = Read();
        return current == null ? null : Write(Unmerge(current), true);
    }

    static string? Write(JsonObject root, bool backup)
    {
        Directory.CreateDirectory(ClaudeDir);
        string? backupPath = null;
        if (backup)
        {
            backupPath = SettingsPath + $".orbi-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(SettingsPath, backupPath, false);
        }
        File.WriteAllText(SettingsPath + ".orbi-tmp", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(SettingsPath + ".orbi-tmp", SettingsPath, true);
        return backupPath;
    }
}

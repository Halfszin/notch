using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Orbi;

// Chat pela sua assinatura do Claude (Pro/Max), sem chave da API: usa o Claude Code instalado no PC
// ("claude -p"), já logado na sua conta, como o coucou faz. Só libera a busca na web.
public static class ClaudeCli
{
    public static string? Find()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Instalador oficial (.local\bin) e npm (%AppData%\npm) vêm primeiro: o PATH deste processo é o de quando
        // o Orbi abriu, então um Claude Code instalado depois não estaria nele.
        var dirs = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
            foreach (var dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { } // pedaço estranho no PATH
            }
        return null;
    }

    public static async Task<string> Ask(string exe, string prompt, string system, CancellationToken ct)
    {
        // "--setting-sources ''": não carrega suas configurações, então os hooks do Orbi não disparam para o próprio chat.
        string[] args = { "-p", "--output-format", "json", "--system-prompt", system, "--tools", "WebSearch,WebFetch",
            "--allowedTools", "WebSearch,WebFetch", "--setting-sources", "", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands" };
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // O claude.cmd (instalação pelo npm) só roda pelo cmd. A pergunta vai pela entrada padrão, nunca na linha de comando.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"" + Quote(exe) + " " + string.Join(" ", args.Select(Quote)) + "\"";
        }
        else
        {
            psi.FileName = exe;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Não deu para abrir o Claude Code.");
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        string output = await stdout;
        try
        {
            using var doc = JsonDocument.Parse(output);
            string result = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString()?.Trim() ?? "" : "";
            bool error = doc.RootElement.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            if (error || result.Length == 0) throw new InvalidOperationException(result.Length > 0 ? result : "O Claude Code não respondeu.");
            return result;
        }
        catch (JsonException)
        {
            string why = (await stderr).Trim().Split('\n').LastOrDefault() ?? "";
            throw new InvalidOperationException("O Claude Code não respondeu" + (why.Length > 0 ? $": {why}" : ". Ele está logado? Rode \"claude\" uma vez no terminal."));
        }
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>Conversa anterior + pergunta nova num texto só (o "claude -p" recebe um prompt por vez).</summary>
    public static string Prompt(IReadOnlyList<(bool User, string Text)> history, string question)
    {
        if (history.Count == 0) return question;
        var sb = new StringBuilder("A conversa até agora:\n<conversa>\n");
        foreach (var (user, text) in history) sb.Append(user ? "<usuario>\n" : "<assistente>\n").Append(text).Append(user ? "\n</usuario>\n" : "\n</assistente>\n");
        return sb.Append("</conversa>\n\nA nova mensagem:\n").Append(question).ToString();
    }
}

using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Orbi;

// Pergunta rápida ao Claude, com busca na web, sem abrir o navegador.
public static class AiChat
{
    const string Model = "claude-opus-5-5";
    public const string Instructions =
        "Você responde perguntas rápidas dentro de um notch pequeno no Windows. Responda em português do Brasil, " +
        "direto ao ponto, em no máximo 4 frases curtas, sem títulos nem tabelas. Use a busca na web quando a resposta " +
        "depender de algo atual (notícias, preços, placares, horários) e diga de qual site veio.";

    public sealed record Reply(string Text, long InputTokens, long OutputTokens);

    public static async Task<Reply> Ask(string apiKey, IReadOnlyList<(bool User, string Text)> history, string question, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var messages = new List<BetaMessageParam>();
        foreach (var (user, text) in history)
            messages.Add(user ? new BetaUserMessageParam { Content = text } : new BetaAssistantMessageParam { Content = text });
        messages.Add(new BetaUserMessageParam { Content = question });

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = Instructions,
            // Pergunta rápida: esforço baixo responde mais depressa.
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            Tools = [new BetaWebSearchTool20260209 { MaxUses = 3 }],
            // Se a resposta for recusada por segurança, o próprio servidor tenta de novo com outro modelo.
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            Messages = messages,
        }, ct);

        if (response.StopReason == "refusal")
            return new Reply("Não posso responder isso.", response.Usage.InputTokens, response.Usage.OutputTokens);
        string answer = string.Join("", response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        if (response.StopReason == "pause_turn" || answer.Length == 0) answer += answer.Length == 0 ? "Não achei uma resposta, tente perguntar de outro jeito." : " (resposta incompleta)";
        return new Reply(answer.Trim(), response.Usage.InputTokens, response.Usage.OutputTokens);
    }
}

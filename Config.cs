using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbi;

// Preferências salvas em %AppData%\Orbi\config.json. A chave da API fica cifrada com a conta do
// Windows (DPAPI): o arquivo copiado para outro PC ou usuário não revela a chave.
public sealed class Config
{
    static readonly string FilePath = Path.Combine(App.Dir, "config.json");
    public static Config Current { get; } = Load();

    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool AppNotifications { get; set; } = true;
    public string? ApiKeyProtected { get; set; }
    public int AgentPort { get; set; } = 47821;
    public string NotchSize { get; set; } = "grande";
    public string Theme { get; set; } = "escuro";              // "escuro" ou "claro"
    public bool AutoHide { get; set; }
    public bool Hotkeys { get; set; } = true;
    public bool Sounds { get; set; } = true;
    public bool Dictation { get; set; }
    public string VoiceModel { get; set; } = "base";            // "base" (rápido) ou "small" (preciso)
    public string DictWords { get; set; } = "Orbi\nClaude Code";    // dicionário: um termo por linha
    public string DictReplacements { get; set; } = "";              // "de => para", uma por linha
    public string DictSnippets { get; set; } = "";                  // "frase => texto", uma por linha           // "grande" ou "compacto"
    public List<string> HiddenTabs { get; set; } = new();       // abas tiradas do acesso rápido (tags)
    // Código secreto que vai no endereço dos hooks: sem ele o Orbi recusa o evento.
    public string AgentToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            if (ApiKeyProtected == null) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), null, DataProtectionScope.CurrentUser)); }
            catch { return null; } // cifrada por outro usuário/PC: pede de novo
        }
        set => ApiKeyProtected = string.IsNullOrWhiteSpace(value) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    }

    static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}

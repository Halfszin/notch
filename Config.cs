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

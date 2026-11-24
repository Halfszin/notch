using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Orbi;

// Notificações do WhatsApp e do Discord: lê, só para leitura, a base onde o próprio Windows guarda as
// notificações (a mesma da Central de Notificações). Apps comuns (sem pacote da Loja) não têm outra API.
public sealed class NotificationWatcher
{
    static readonly string Db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");
    static readonly (string Match, string Name)[] Apps = { ("WhatsApp", "WhatsApp"), ("Discord", "Discord") };
    long _lastId = -1;
    bool _broken;

    public event Action<string, string, string>? Received; // app, título, texto

    public void Poll()
    {
        if (_broken || !File.Exists(Db)) return;
        try
        {
            using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Mode = SqliteOpenMode.ReadOnly }.ToString());
            con.Open();
            if (_lastId < 0)
            {
                // Primeira leitura: marca onde está, para não repetir notificações antigas.
                using var max = con.CreateCommand();
                max.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Notification";
                _lastId = Convert.ToInt64(max.ExecuteScalar());
                return;
            }
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT n.Id, h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                              "WHERE n.Id > $id AND n.Type = 'toast' ORDER BY n.Id";
            cmd.Parameters.AddWithValue("$id", _lastId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                _lastId = r.GetInt64(0);
                string handler = r.IsDBNull(1) ? "" : r.GetString(1);
                var app = Apps.FirstOrDefault(a => handler.Contains(a.Match, StringComparison.OrdinalIgnoreCase));
                if (app.Name == null || r.IsDBNull(2)) continue;
                string xml = r.GetValue(2) is byte[] bytes ? Encoding.UTF8.GetString(bytes) : r.GetString(2);
                var (title, body) = ToastText.Parse(xml);
                if (title.Length + body.Length > 0) Received?.Invoke(app.Name, title, body);
            }
        }
        catch (SqliteException) { } // base ocupada nesta hora: tenta de novo na próxima
        catch (Exception) { _broken = true; } // formato diferente nesta versão do Windows: desliga sem atrapalhar
    }
}

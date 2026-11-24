using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Orbi;

// Recebe os eventos dos hooks do Claude Code (curl → http://127.0.0.1:porta/código/evento).
// Só aceita conexões do próprio PC e exige o código secreto no endereço: um site aberto no navegador
// ou outro programa não consegue mandar eventos falsos nem aprovar nada.
public sealed class AgentServer
{
    const int MaxBody = 1 << 20;
    readonly TcpListener _listener;
    readonly byte[] _token;
    readonly Func<string, JsonObject, Task<string>> _handle;

    public int Port { get; }

    AgentServer(TcpListener listener, string token, Func<string, JsonObject, Task<string>> handle)
    {
        _listener = listener;
        _token = Encoding.ASCII.GetBytes(token);
        _handle = handle;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Tenta a porta salva e as seguintes; null se nenhuma estiver livre.</summary>
    public static AgentServer? Start(int preferredPort, string token, Func<string, JsonObject, Task<string>> handle)
    {
        for (int port = preferredPort; port < preferredPort + 10; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                var server = new AgentServer(l, token, handle);
                _ = server.AcceptLoop();
                return server;
            }
            catch (SocketException) { }
        }
        return null;
    }

    async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Serve(client);
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var (path, headers, body) = await ReadRequest(stream);
            string[] parts = path.Trim('/').Split('/');
            if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), _token))
            {
                await Reply(stream, 403, "");
                return;
            }
            if (JsonNode.Parse(body) is not JsonObject payload) { await Reply(stream, 400, ""); return; }
            string answer = await _handle(parts[1], payload);
            await Reply(stream, 200, answer);
        }
        catch { } // conexão cortada ou pedido estranho: ignora
    }

    static async Task<(string Path, Dictionary<string, string> Headers, string Body)> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0 || buffer.Length > 32 * 1024) throw new IOException("cabeçalho inválido");
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        var raw = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
        var lines = head.Split("\r\n");
        string path = lines[0].Split(' ') is { Length: >= 2 } rl ? rl[1] : "/";
        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int v) ? v : 0;
        if (length < 0 || length > MaxBody) throw new IOException("corpo grande demais");
        // O curl pede licença antes de mandar corpos maiores ("Expect: 100-continue").
        if (headers.TryGetValue("expect", out var ex) && ex.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());
        var body = new byte[length];
        int have = Math.Min(length, (int)buffer.Length - (headerEnd + 4));
        Array.Copy(raw, headerEnd + 4, body, 0, have);
        while (have < length)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, length - have));
            if (n == 0) throw new IOException("corpo incompleto");
            have += n;
        }
        return (path, headers, Encoding.UTF8.GetString(body));
    }

    static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern) => data.AsSpan(0, length).IndexOf(pattern);

    static async Task Reply(NetworkStream stream, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Erro")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }
}

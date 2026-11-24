using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Orbi;

// Mostra o avatar do Avatar Lab (@bible-strong/avatar-web) dentro de um WebView2 transparente.
public class AvatarView : ContentControl
{
    public static string AvatarFile => Path.Combine(App.Dir, "avatar.avatar.json");
    static Task<CoreWebView2Environment>? _env;

    readonly WebView2CompositionControl _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    bool _started;
    string _state = "idle";

    /// <summary>"ok", "none" (sem arquivo) ou a mensagem de erro do último carregamento.</summary>
    public string LastResult { get; private set; } = "none";

    public event Action? Reloaded;

    public AvatarView()
    {
        Content = _web;
        IsHitTestVisible = false;
        Loaded += async (_, _) => await Start();
    }

    async Task Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(App.Dir, "WebView2"));
            await _web.EnsureCoreWebView2Async(await _env);
            _web.NavigationCompleted += async (_, _) => await Reload();
            _web.NavigateToString(Html());
        }
        catch (Exception ex)
        {
            // Sem o WebView2 Runtime o notch segue funcionando, só sem avatar.
            LastResult = "WebView2 indisponível: " + ex.Message;
            Visibility = Visibility.Collapsed;
        }
    }

    public async Task Reload()
    {
        if (_web.CoreWebView2 == null) return;
        // Avatar escolhido nas configurações; se não houver, o "New avatar" que vem dentro do app.
        string json;
        try { json = File.Exists(AvatarFile) ? File.ReadAllText(AvatarFile) : Resource("default.avatar.json"); }
        catch { json = Resource("default.avatar.json"); }
        // Passa como texto e o JSON.parse faz a leitura: o conteúdo do arquivo nunca roda como código.
        try
        {
            var r = await _web.ExecuteScriptAsync($"load({JsonSerializer.Serialize(json)},{JsonSerializer.Serialize(_state)})");
            LastResult = JsonSerializer.Deserialize<string>(r) ?? "none";
        }
        catch (Exception ex) { LastResult = ex.Message; }
        Visibility = LastResult == "ok" ? Visibility.Visible : Visibility.Collapsed;
        Reloaded?.Invoke();
    }

    public void SetState(string state)
    {
        if (state == _state) return;
        _state = state;
        Run($"av&&av.setState({JsonSerializer.Serialize(state)})");
    }

    /// <summary>Gesto por cima do que está tocando: "nod", "bounce" ou "spin".</summary>
    public void Poke(string gesture) => Run($"av&&av.poke({JsonSerializer.Serialize(gesture)})");

    /// <summary>Para onde o avatar olha: x para a direita, y para cima, de -1 a 1; null = olhar livre.</summary>
    public void LookAt(double? x, double? y) =>
        Run(x is double lx && y is double ly
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"av&&av.setLookAt([{lx:0.00},{ly:0.00}])")
            : "av&&av.setLookAt(null)");

    public void Pause() => Run("av&&av.pause()");
    public void Resume() => Run("av&&av.resume()");

    void Run(string js)
    {
        if (_web.CoreWebView2 != null) _ = _web.ExecuteScriptAsync(js);
    }

    static string Resource(string name)
    {
        using var s = typeof(AvatarView).Assembly.GetManifestResourceStream(name)!;
        return new StreamReader(s).ReadToEnd();
    }

    static string Html()
    {
        string bundle = Resource("avatar.bundle.js");
        return "<!doctype html><html><body style=\"margin:0;height:100vh;display:flex;align-items:center;" +
               "justify-content:center;background:transparent;overflow:hidden\"><div id=\"a\"></div><script>" + bundle +
               "</script><script>var av=null;function load(t,st){try{if(av){av.destroy();av=null}" +
               "var c=JSON.parse(t);if(!c)return 'none';av=AvatarWeb.createAvatar('#a',{character:c,state:st,size:'68vmin'});" +
               "return 'ok'}catch(e){return String(e&&e.message||e)}}</script></body></html>";
    }
}

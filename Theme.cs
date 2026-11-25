using System.Windows;
using System.Windows.Media;

namespace Orbi;

// Cores do notch em dois temas. Os elementos usam {DynamicResource N.*}: trocar o tema troca tudo na hora.
public static class Theme
{
    static readonly Dictionary<string, (string Dark, string Light)> Colors = new()
    {
        ["N.Bg"] = ("#000000", "#F2F2F5"),     // corpo do notch
        ["N.Card"] = ("#161619", "#FFFFFF"),   // cartão interno
        ["N.Fg"] = ("#FFFFFF", "#1C1C1E"),     // texto principal
        ["N.Sub"] = ("#8E8E93", "#6E6E73"),    // texto secundário e ícones
        ["N.Field"] = ("#1E1E22", "#EEEEF1"),  // campos, pílula do comando, capa vazia
        ["N.Hover"] = ("#232327", "#E4E4E9"),  // aba selecionada / passar o mouse
        ["N.Button"] = ("#2A2A2F", "#E6E6EB"), // botões secundários
        ["N.Track"] = ("#333336", "#D6D6DB"),  // trilhos (barra de música, anel, volume)
        ["N.Strong"] = ("#FFFFFF", "#1C1C1E"), // botão principal (Permitir, OK, enviar)
        ["N.OnStrong"] = ("#000000", "#FFFFFF"),
    };

    public static string Current { get; private set; } = "escuro";
    public static event Action? Changed;

    /// <summary>Pincel do tema atual (para elementos montados por código; redesenhe no evento Changed).</summary>
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static void Apply(string theme)
    {
        Current = theme == "claro" ? "claro" : "escuro";
        foreach (var (key, (dark, light)) in Colors)
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Current == "claro" ? light : dark));
        Changed?.Invoke();
    }
}

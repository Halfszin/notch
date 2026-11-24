using System.Xml.Linq;

namespace Orbi;

// Tira título e texto do XML de uma notificação do Windows (<toast><visual><binding><text>…).
public static class ToastText
{
    public static (string Title, string Body) Parse(string xml)
    {
        try
        {
            var texts = XDocument.Parse(xml).Descendants("text").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            return (texts.FirstOrDefault() ?? "", string.Join(" ", texts.Skip(1)));
        }
        catch { return ("", ""); } // XML estranho: não mostra nada em vez de quebrar
    }
}

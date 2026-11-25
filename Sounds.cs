using System.IO;
using System.Media;

namespace Orbi;

// Sons dos avisos. Um .wav com o nome do aviso em %AppData%\Orbi\sons substitui o som do Windows.
public static class Sounds
{
    public static string Folder => Path.Combine(App.Dir, "sons");

    public static void Play(string name, SystemSound fallback)
    {
        if (!Config.Current.Sounds) return;
        string file = Path.Combine(Folder, name + ".wav");
        try
        {
            if (File.Exists(file)) { new SoundPlayer(file).Play(); return; }
        }
        catch { } // arquivo que não é .wav de verdade: cai no som do Windows
        fallback.Play();
    }
}

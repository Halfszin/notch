using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbi;

static class Shell
{
    /// <summary>Aperta e solta uma combinação de teclas (ex.: Win+Shift+R), como se fosse no teclado.</summary>
    public static void PressKeys(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, 0x0002, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static ImageSource? FileIcon(string file)
    {
        // Ícone que o Explorer mostra para o arquivo (ou pasta).
        var info = new FileInfoShell();
        if (SHGetFileInfo(file, 0, ref info, (uint)Marshal.SizeOf<FileInfoShell>(), 0x100) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try { return Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
        catch { return null; }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfoShell
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfoShell info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}

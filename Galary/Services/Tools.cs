using System.IO;
using System.Runtime.InteropServices;

namespace Galary.Services;

/// <summary>Small Windows shell helpers: wallpaper and Recycle Bin restore.</summary>
public static class Tools
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(int action, int param, string value, int flags);

    public static bool Wallpaper(string path) => SystemParametersInfo(20 /*SPI_SETDESKWALLPAPER*/, 0, path, 3 /*update + broadcast*/);

    /// <summary>Restores files from the Recycle Bin by original path. Returns how many were restored.</summary>
    public static int Restore(IEnumerable<string> paths)
    {
        var want = paths.ToList();
        int n = 0;
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic bin = sh.NameSpace(10); // CSIDL_BITBUCKET
            dynamic items = bin.Items();
            int count = items.Count;
            for (int i = 0; i < count && want.Count > 0; i++)
            {
                dynamic item = items.Item(i);
                string dir = ((string)bin.GetDetailsOf(item, 1)).Trim('‎', '‏');
                string name = (string)item.Name;
                string full = Path.Combine(dir, name);
                // Explorer may hide the extension in the bin's Name column
                var hit = want.FirstOrDefault(w => string.Equals(w, full, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.Combine(Path.GetDirectoryName(w) ?? "", Path.GetFileNameWithoutExtension(w)), full, StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                item.InvokeVerb("undelete");
                want.Remove(hit); n++;
            }
        }
        catch { }
        return n;
    }
}

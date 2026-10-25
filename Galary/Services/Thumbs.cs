using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Galary.Services;

/// <summary>Shell thumbnails (video frames, album art, pdf/office previews, photos) through WinRT, with a small LRU and a disk cache.</summary>
public static class Thumbs
{
    const int Cap = 800;
    const uint DiskMax = 700; // bigger previews (viewer) are not worth the disk space
    static readonly SemaphoreSlim Gate = new(6);
    static readonly Dictionary<string, ImageSource> Cache = new();
    static readonly Queue<string> Order = new();
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galary", "thumbs");

    // keyed on path + size + mtime + length so an edited file never shows a stale picture
    static string DiskPath(string path, uint size)
    {
        long t = 0, l = 0;
        try { var fi = new FileInfo(path); t = fi.LastWriteTimeUtc.Ticks; l = fi.Length; } catch { }
        return Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{size}|{t}|{l}"))) + ".img");
    }

    static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static void Remember(string key, ImageSource img)
    {
        lock (Cache)
        {
            Cache[key] = img; Order.Enqueue(key);
            while (Order.Count > Cap) Cache.Remove(Order.Dequeue());
        }
    }

    public static async Task<ImageSource?> Get(string path, uint size = 220, Func<bool>? wanted = null)
    {
        var key = path + "|" + size;
        lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;

        string? dp = size <= DiskMax ? DiskPath(path, size) : null;
        if (dp != null && File.Exists(dp))
        {
            try
            {
                var bi = Decode(await File.ReadAllBytesAsync(dp));
                if (bi != null) { Remember(key, bi); return bi; }
            }
            catch { }
        }

        byte[]? bytes = null;
        await Gate.WaitAsync();
        try
        {
            if (wanted != null && !wanted()) return null; // tile scrolled away while queued
            var f = await StorageFile.GetFileFromPathAsync(path);
            using var t = await f.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.None);
            if (t != null && t.Type == ThumbnailType.Image) // else generic icon: tile draws its own glyph
            {
                using var s = t.AsStreamForRead();
                using var buf = new MemoryStream();
                await s.CopyToAsync(buf);
                bytes = buf.ToArray();
            }
        }
        catch { }
        finally { Gate.Release(); }

        // shell had no picture for this video: grab a frame ourselves (outside the shell gate, it is slow)
        if (bytes == null && VideoExts.Contains(Path.GetExtension(path)) && size <= DiskMax)
        {
            lock (NoPoster) if (NoPoster.Contains(path)) return null;
            bytes = await Posters.Capture(path, wanted);
            if (bytes == null && (wanted == null || wanted())) lock (NoPoster) NoPoster.Add(path); // don't retry a file libvlc can't open
        }
        if (bytes == null) return null;
        var img = Decode(bytes);
        if (img == null) return null;
        if (dp != null) try { Directory.CreateDirectory(Dir); await File.WriteAllBytesAsync(dp, bytes); } catch { }
        Remember(key, img);
        return img;
    }

    static readonly HashSet<string> NoPoster = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".3gp", ".ogv", ".vob" };

    /// <summary>Deletes the on-disk previews and returns the bytes freed.</summary>
    public static long ClearDisk()
    {
        long freed = 0;
        lock (Cache) { Cache.Clear(); Order.Clear(); }
        try
        {
            foreach (var f in Directory.EnumerateFiles(Dir, "*.img"))
                try { freed += new FileInfo(f).Length; File.Delete(f); } catch { }
        }
        catch { }
        return freed;
    }
}

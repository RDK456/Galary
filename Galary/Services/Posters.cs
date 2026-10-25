using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Galary.Services;

/// <summary>
/// Poster frame for videos the Windows shell can't thumbnail (mkv, hevc without codecs, ...).
/// Plays the file headless through libvlc's memory renderer, seeks to 10 % and keeps one frame. One at a time.
/// </summary>
public static class Posters
{
    static LibVLC? _vlc;
    static readonly SemaphoreSlim Gate = new(1);

    public static async Task<byte[]?> Capture(string path, Func<bool>? wanted = null)
    {
        await Gate.WaitAsync();
        try
        {
            if (wanted != null && !wanted()) return null;
            return await Task.Run(() => Grab(path));
        }
        catch { return null; }
        finally { Gate.Release(); }
    }

    static byte[]? Grab(string path)
    {
        _vlc ??= new LibVLC("--no-audio", "--quiet");
        using var media = new Media(_vlc, path, FromType.FromPath);
        try { media.Parse(MediaParseOptions.ParseLocal).Wait(4000); } catch { }

        int vw = 16, vh = 9;
        try
        {
            foreach (var t in media.Tracks)
                if (t.TrackType == TrackType.Video && t.Data.Video.Width > 0 && t.Data.Video.Height > 0) { vw = (int)t.Data.Video.Width; vh = (int)t.Data.Video.Height; break; }
        }
        catch { }

        uint w = 320, h = (uint)Math.Clamp((int)Math.Round(320.0 * vh / vw / 2) * 2, 2, 480), pitch = w * 4;
        var buf = Marshal.AllocHGlobal((int)(pitch * h));
        int frames = 0;
        using var mp = new MediaPlayer(media);
        try
        {
            mp.SetVideoFormat("RV32", w, h, pitch);
            mp.SetVideoCallbacks(
                (opaque, planes) => { Marshal.WriteIntPtr(planes, buf); return IntPtr.Zero; },
                (opaque, picture, planes) => { },
                (opaque, picture) => { Interlocked.Increment(ref frames); });
            mp.Mute = true;
            mp.Play();

            for (int i = 0; i < 80 && !(mp.IsPlaying && mp.Length > 0); i++) Thread.Sleep(50);
            var px = new byte[pitch * h];
            // 10 % in; if that frame is nearly black (fade-in, intro card) try 35 % and 60 %
            foreach (double at in new[] { 0.10, 0.35, 0.60 })
            {
                if (mp.Length > 0 && mp.IsSeekable) { mp.Time = (long)(mp.Length * at); Interlocked.Exchange(ref frames, 0); }
                for (int i = 0; i < 60 && Volatile.Read(ref frames) < 3; i++) Thread.Sleep(50);
                if (Volatile.Read(ref frames) < 1) return null;
                Marshal.Copy(buf, px, 0, px.Length);
                long sum = 0;
                for (int i = 0; i < px.Length; i += 16) sum += px[i] + px[i + 1] + px[i + 2];
                if (sum / (px.Length / 16) / 3 > 12 || !mp.IsSeekable) break;
            }
            var bmp = BitmapSource.Create((int)w, (int)h, 96, 96, PixelFormats.Bgr32, null, px, (int)pitch);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream(); enc.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            try { mp.Stop(); } catch { }
            Thread.Sleep(100); // let in-flight callbacks drain before the buffer goes away
            Marshal.FreeHGlobal(buf);
        }
    }
}

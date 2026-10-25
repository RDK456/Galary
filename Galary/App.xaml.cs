using System.IO;
using System.Windows;
using Galary.Models;
using Galary.Services;
using LibVLCSharp.Shared;

namespace Galary;

public partial class App : Application
{
    public static string? ThemeOverride; // --theme light|dark|auto, for screenshots; never saved

    protected override void OnStartup(StartupEventArgs e)
    {
        int ti = Array.IndexOf(e.Args, "--theme");
        if (ti >= 0 && ti + 1 < e.Args.Length) ThemeOverride = e.Args[ti + 1];
        try { Core.Initialize(); } catch { /* libvlc missing: viewer shows an error when media is opened */ }
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest()); return; }
        int pi = Array.IndexOf(e.Args, "--poster"); // debug: --poster <video> <out.png>
        if (pi >= 0 && pi + 2 < e.Args.Length)
        {
            var vid = e.Args[pi + 1]; var bytes = Task.Run(() => Posters.Capture(vid)).Result; // off the UI thread: the await inside must not wait on this one
            if (bytes != null) File.WriteAllBytes(e.Args[pi + 2], bytes);
            Shutdown(bytes != null ? 0 : 1); return;
        }
        base.OnStartup(e);
        _ = Updater.CheckAsync();
    }

    // Scan + sort + dedupe + libvlc audio playback against a throwaway folder.
    static int SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "galary-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub")); Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            File.WriteAllText(Path.Combine(root, "a.mp4"), "x");
            File.WriteAllBytes(Path.Combine(root, "img10.jpg"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(root, "img2.jpg"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(root, "tiny.png"), new byte[100]);
            File.WriteAllText(Path.Combine(root, "d.pdf"), "x");
            File.WriteAllText(Path.Combine(root, "skip.tmp"), "x");
            File.WriteAllBytes(Path.Combine(root, "node_modules", "n.jpg"), new byte[20000]);
            File.WriteAllBytes(Path.Combine(root, "sub", "img2.jpg"), new byte[20000]);
            var wav = Path.Combine(root, "sub", "tone.wav"); WriteWav(wav);

            Library.Dir = Path.Combine(root, "data"); // never touch the real library.db
            var lib = new Library();
            lib.S.SmartHide = false; // the throwaway folder sits under %TEMP%, which the smart filter hides on purpose
            lib.ScanAsync(new[] { root }, () => { }, CancellationToken.None).Wait();
            Check(lib.Count == 6, $"count {lib.Count} (want a.mp4, img10, img2, sub/img2, d.pdf, tone.wav)");

            lib.SaveCache();
            var lib2 = new Library(); lib2.S.SmartHide = false; lib2.LoadCache();
            Check(lib2.Count == 6, $"sqlite index round-trip: {lib2.Count} of 6");

            lib.S.Sort = "name"; lib.S.Desc = false;
            var names = lib.Build(_ => true, false).SelectMany(g => g.Items).Select(i => i.Name).ToList();
            Check(names.IndexOf("img2.jpg") < names.IndexOf("img10.jpg"), "natural sort: img2 before img10");

            lib.S.HideDupes = true;
            Check(lib.Build(_ => true, false).SelectMany(g => g.Items).Count(i => i.Name == "img2.jpg") == 1, "dedupe img2.jpg");
            Check(lib.Build(_ => true, true).Count == 2, "albums: root + sub");
            var dups = lib.FindDuplicates(CancellationToken.None);
            Check(dups.Count == 2 && dups.Any(g => g.Items.Length == 3), $"duplicates: want the 3 zero-filled jpgs plus the 1-byte mp4/pdf pair, got {dups.Count} sets");

            using var vlc = new LibVLC();
            using var mp = new MediaPlayer(vlc);
            using var media = new Media(vlc, wav, FromType.FromPath);
            mp.Play(media);
            for (int i = 0; i < 30 && mp.State is not (VLCState.Playing or VLCState.Ended or VLCState.Error); i++) Thread.Sleep(100);
            Check(mp.State is VLCState.Playing or VLCState.Ended, "libvlc plays wav, state " + mp.State);
            mp.Stop();
            Console.WriteLine("SELFTEST OK");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "galary-selftest.txt"), ex.ToString()); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void Check(bool ok, string what)
    {
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "galary-selftest.txt"), (ok ? "PASS " : "FAIL ") + what + "\n");
        if (!ok) throw new Exception("FAIL " + what);
    }

    static void WriteWav(string path)
    {
        const int rate = 22050, n = rate * 2;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
    }
}

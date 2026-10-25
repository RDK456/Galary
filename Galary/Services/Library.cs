using System.IO;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Galary.Models;

namespace Galary.Services;

public sealed class Settings
{
    public List<string> Folders { get; set; } = new();
    public string Sort { get; set; } = "date";
    public bool Desc { get; set; } = true;
    public bool HideDupes { get; set; }
    public int Tile { get; set; } = 170;
    public string Background { get; set; } = "";
    public double BgDim { get; set; } = 0.72; // tint opacity over the background image
    public bool SmartHide { get; set; } = true; // hide app/game/system media
    public List<string> ExcludeWords { get; set; } = new() { "cache", "thumbnail", "cutscene", "content" }; // folder-name fragments to leave out
    public string Theme { get; set; } = "dark";     // dark | light | auto (follow Windows)
    public bool Compact { get; set; }              // smaller tiles
    public string Accent { get; set; } = "";       // "" = built-in amber
    public double SlideSec { get; set; } = 3.5;    // slideshow seconds per photo
    public List<string> Searches { get; set; } = new();
    public Dictionary<string, long> Resume { get; set; } = new(StringComparer.OrdinalIgnoreCase); // video/audio resume positions (ms)
    public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);     // single files
    public HashSet<string> HiddenDirs { get; set; } = new(StringComparer.OrdinalIgnoreCase); // whole folders
    public HashSet<string> Favs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record Group(string Head, string Sub, MediaItem[] Items);
record CacheEntry(string P, long S, long T, int W = 0, int H = 0);

public sealed class Library
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    public static int Natural(string a, string b) => StrCmpLogicalW(a, b);

    public static string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galary");
    static string SettingsFile => Path.Combine(Dir, "settings.json");
    static string CacheFile => Path.Combine(Dir, "cache.json");
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "AppData", "venv", "site-packages", "__pycache__", "WinSxS" };
    static readonly HashSet<string> SkipAtRoot = new(StringComparer.OrdinalIgnoreCase) { "Windows", "ProgramData" };

    readonly Dictionary<string, MediaItem> _items = new(StringComparer.OrdinalIgnoreCase);
    public Settings S { get; private set; } = new();
    public int Count { get { lock (_items) return _items.Count; } }

    public static Library Load()
    {
        var l = new Library();
        try { l.S = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); } catch { }
        l.S.Favs = new HashSet<string>(l.S.Favs, StringComparer.OrdinalIgnoreCase);
        l.S.Resume = new Dictionary<string, long>(l.S.Resume ?? new(), StringComparer.OrdinalIgnoreCase);
        l.S.Searches ??= new();
        l.S.Hidden = new HashSet<string>(l.S.Hidden, StringComparer.OrdinalIgnoreCase);
        l.S.HiddenDirs = new HashSet<string>(l.S.HiddenDirs, StringComparer.OrdinalIgnoreCase);
        return l;
    }

    public void SaveSettings()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(S)); } catch { }
    }

    // ---------- index: SQLite, saved incrementally (only rows that changed) ----------
    static string DbFile => Path.Combine(Dir, "library.db");
    readonly Dictionary<string, (long S, long T, int W, int H)> _persisted = new(StringComparer.OrdinalIgnoreCase);

    static SqliteConnection OpenDb()
    {
        Directory.CreateDirectory(Dir);
        var c = new SqliteConnection($"Data Source={DbFile}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS items(path TEXT PRIMARY KEY, size INTEGER, mtime INTEGER, w INTEGER, h INTEGER) WITHOUT ROWID;";
        cmd.ExecuteNonQuery();
        return c;
    }

    void AddCached(string p, long size, long ticks, int w, int h)
    {
        if (MediaItem.From(p, size, new DateTime(ticks)) is not { } m) return;
        m.Fav = S.Favs.Contains(m.Path); m.W = w; m.H = h; m.Probed = w > 0;
        lock (_items) _items[m.Path] = m;
    }

    public void LoadCache()
    {
        try
        {
            using var c = OpenDb();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path,size,mtime,w,h FROM items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string p = r.GetString(0); long s = r.GetInt64(1), t = r.GetInt64(2); int w = r.GetInt32(3), h = r.GetInt32(4);
                _persisted[p] = (s, t, w, h);
                AddCached(p, s, t, w, h);
            }
        }
        catch { }
        // one-time import of the old cache.json
        try
        {
            if (!File.Exists(CacheFile)) return;
            var list = JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(CacheFile)) ?? new();
            foreach (var c in list) AddCached(c.P, c.S, c.T, c.W, c.H);
            File.Move(CacheFile, CacheFile + ".migrated", true);
            SaveCache();
        }
        catch { }
    }

    public void SaveCache()
    {
        try
        {
            Dictionary<string, (long S, long T, int W, int H)> now;
            lock (_items) now = _items.Values.ToDictionary(i => i.Path, i => (i.Size, i.Modified.Ticks, i.W, i.H), StringComparer.OrdinalIgnoreCase);
            var up = now.Where(kv => !_persisted.TryGetValue(kv.Key, out var o) || o != kv.Value).ToList();
            var gone = _persisted.Keys.Where(k => !now.ContainsKey(k)).ToList();
            if (up.Count == 0 && gone.Count == 0) return;

            using var c = OpenDb();
            using var tx = c.BeginTransaction();
            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO items VALUES($p,$s,$t,$w,$h)";
                var p = ins.Parameters.Add("$p", SqliteType.Text); var s = ins.Parameters.Add("$s", SqliteType.Integer);
                var t = ins.Parameters.Add("$t", SqliteType.Integer); var w = ins.Parameters.Add("$w", SqliteType.Integer); var h = ins.Parameters.Add("$h", SqliteType.Integer);
                foreach (var (path, v) in up) { p.Value = path; s.Value = v.S; t.Value = v.T; w.Value = v.W; h.Value = v.H; ins.ExecuteNonQuery(); }
            }
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM items WHERE path=$p";
                var p = del.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in gone) { p.Value = path; del.ExecuteNonQuery(); }
            }
            tx.Commit();
            _persisted.Clear();
            foreach (var kv in now) _persisted[kv.Key] = kv.Value;
        }
        catch { }
    }

    public IEnumerable<string> Roots()
    {
        // every local, ready drive; user-added folders on top (network drives are left out on purpose)
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .Select(d => d.RootDirectory.FullName);
        return drives.Concat(S.Folders).Where(d => d.Length > 0 && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static bool Skip(DirectoryInfo d)
    {
        if (d.Name.StartsWith('$') || d.Name.StartsWith('.') || SkipDirs.Contains(d.Name)) return true;
        if (d.Parent?.Parent == null && SkipAtRoot.Contains(d.Name)) return true;
        return (d.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0; // junction loops, system dirs
    }

    /// <summary>Walks roots on a worker thread. onBatch fires every ~500 files; items missing after a full pass are dropped.</summary>
    public Task ScanAsync(IEnumerable<string> roots, Action onBatch, CancellationToken ct) => Task.Run(() =>
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(roots);
        int n = 0;
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos().ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                try
                {
                    if (e is DirectoryInfo di) { if (!Skip(di) && !Excluded(di.Name)) stack.Push(di.FullName); continue; }
                    var fi = (FileInfo)e;
                    var m = MediaItem.From(fi.FullName, fi.Length, fi.LastWriteTime);
                    if (m == null) continue;
                    seen.Add(m.Path);
                    m.Fav = S.Favs.Contains(m.Path);
                    lock (_items)
                    {
                        if (_items.TryGetValue(m.Path, out var old) && old.Size == m.Size && old.Modified == m.Modified)
                        { m.W = old.W; m.H = old.H; m.Probed = old.Probed; m.Animated = old.Animated; }
                        _items[m.Path] = m;
                    }
                    if (++n % 500 == 0) onBatch();
                }
                catch { }
            }
        }
        if (ct.IsCancellationRequested) return;
        lock (_items)
            foreach (var gone in _items.Keys.Where(k => !seen.Contains(k)).ToList()) _items.Remove(gone);
    }, ct);

    // folders whose media is app/game/system content rather than the user's own
    static readonly string[] NoiseSegs =
    {
        "\\program files", "\\programdata\\", "\\windows\\", "\\appdata\\", "\\assets\\", "\\streamingassets\\", "\\resources\\",
        "\\packages\\", "\\steamapps\\", "\\unity", "\\unreal", "\\sdk", "\\plugins\\", "\\addons\\", "\\themes\\", "\\skins\\",
        "\\icons\\", "\\sprites\\", "\\textures\\", "\\samples\\", "\\sample\\", "\\examples\\", "\\build\\", "\\dist\\",
        "\\obj\\", "\\bin\\", "\\cache\\", "\\temp\\", "\\tmp\\", "\\library\\packagecache", "\\extensions\\",
        "\\shaders\\", "\\shade", "\\vendor\\", "\\third_party", "\\thirdparty", "\\external\\", "\\deps\\", "\\res\\", "\\drawable", "\\mipmap",
        "\\emoji", "\\locale", "\\wwwroot", "\\static\\", "\\public\\", "\\fixtures", "\\testdata", "\\microsoft\\", "\\copilot", "\\nvidia",
        "\\windowsapps", "\\electron", "\\chrome\\", "\\edge\\", "\\cordova", "\\ionic", "\\android\\", "\\ios\\", "\\platforms\\", "\\images\\icons",
    };
    static readonly string[] UserDirs = new[]
    {
        Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
    }.Select(Environment.GetFolderPath).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
     .Where(d => d.Length > 0).ToArray();

    /// <summary>True when any folder in the path has a name containing one of the user's exclude words.</summary>
    public bool Excluded(string folderPath)
    {
        if (S.ExcludeWords.Count == 0) return false;
        foreach (var part in folderPath.Split('\\', '/'))
            foreach (var w in S.ExcludeWords)
                if (part.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool IsHidden(MediaItem i)
    {
        if (Excluded(i.Folder)) return true;
        if (S.Hidden.Contains(i.Path) || S.HiddenDirs.Any(d => (i.Folder + "\\").StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return true;
        if (!S.SmartHide || S.Folders.Any(f => i.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase))) return false;
        var p = i.Path.ToLowerInvariant();
        if (NoiseSegs.Any(p.Contains)) return true;
        if (NoiseName.IsMatch(Path.GetFileNameWithoutExtension(i.Name))) return true;
        if (i.Kind == Kind.Image && i.W > 0)
        {
            int mx = Math.Max(i.W, i.H), mn = Math.Min(i.W, i.H);
            if (mx < 400 || mn < 200) return true;                                  // thumbnails, glyphs, icons
            if (i.W == i.H && mx <= 1024 && i.Size < 200 * 1024) return true;       // square + small = app icon / avatar asset
        }
        if (UserDirs.Any(d => i.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return false;
        // outside the user's own folders, tiny files are almost always UI assets
        const long KB = 1024;
        return i.Kind switch { Kind.Image => i.Size < 60 * KB, Kind.Video => i.Size < 500 * KB, Kind.Audio => i.Size < 150 * KB, _ => i.Size < 10 * KB };
    }

    // asset-style file names: icons, sprites, textures, retina variants
    static readonly Regex NoiseName = new(@"icon|sprite|texture|placeholder|favicon|emoji|badge|@[2-4]x|(^|[_-])ic_|[_-]thumb|^thumb|splash_?screen", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads pixel sizes of images that don't have one yet (header only, 4 at a time). Drives real tile shapes and the icon filter.</summary>
    public Task ProbeAsync(CancellationToken ct) => Task.Run(() =>
    {
        MediaItem[] todo;
        lock (_items) todo = _items.Values.Where(i => i.Kind == Kind.Image && i.W == 0 && !i.Probed).ToArray();
        try
        {
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, i =>
            {
                i.Probed = true;
                try
                {
                    var f = BitmapDecoder.Create(new Uri(i.Path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                    i.W = f.PixelWidth; i.H = f.PixelHeight;
                }
                catch { } // heic without codec, corrupt file: stays unknown and is kept
            });
        }
        catch (OperationCanceledException) { }
    }, ct);

    public void Remove(string path) { lock (_items) _items.Remove(path); }

    public void Rename(string oldPath, string newPath)
    {
        Remove(oldPath);
        if (S.Favs.Remove(oldPath)) S.Favs.Add(newPath);
        Upsert(newPath);
    }

    // ---------- live updates ----------
    readonly List<FileSystemWatcher> _watchers = new();
    /// <summary>Raised (on a worker thread) when the watcher added or removed something.</summary>
    public event Action? Changed;

    bool SkippedPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var segs = dir.Split('\\');
        if (segs.Length > 1 && SkipAtRoot.Contains(segs[1])) return true;
        foreach (var s in segs)
            if (s.StartsWith('$') || (s.Length > 1 && s.StartsWith('.')) || SkipDirs.Contains(s)) return true;
        return Excluded(dir);
    }

    /// <summary>Adds or refreshes one file (used by the watcher, rename and undo-delete).</summary>
    public void Upsert(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || SkippedPath(path)) return;
            var m = MediaItem.From(path, fi.Length, fi.LastWriteTime);
            if (m == null) return;
            m.Fav = S.Favs.Contains(path);
            lock (_items)
            {
                if (_items.TryGetValue(path, out var old))
                {
                    if (old.Size == m.Size && old.Modified == m.Modified) return;
                    if (old.Size == m.Size) { m.W = old.W; m.H = old.H; m.Probed = old.Probed; }
                }
                _items[path] = m;
            }
            Changed?.Invoke();
        }
        catch { }
    }

    public void Watch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var r in Roots())
        {
            try
            {
                var w = new FileSystemWatcher(r)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                bool Gone(string p) { lock (_items) return _items.Remove(p); }
                w.Created += (_, e) => Upsert(e.FullPath);
                w.Changed += (_, e) => Upsert(e.FullPath);
                w.Deleted += (_, e) => { if (Gone(e.FullPath)) Changed?.Invoke(); };
                w.Renamed += (_, e) => { Gone(e.OldFullPath); Upsert(e.FullPath); Changed?.Invoke(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { } // drive went away or no permission: skip it
        }
    }

    // ---------- duplicates ----------
    // ponytail: same size + same first/last 64 KB is treated as identical; full hashing only if false positives show up
    static string? QuickHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[131072];
            int n = fs.Read(buf, 0, 65536);
            if (fs.Length > 131072) { fs.Seek(-65536, SeekOrigin.End); n += fs.Read(buf, n, 65536); }
            return Convert.ToHexString(SHA1.HashData(buf.AsSpan(0, n)));
        }
        catch { return null; }
    }

    public List<Group> FindDuplicates(CancellationToken ct)
    {
        MediaItem[] snap;
        lock (_items) snap = _items.Values.Where(i => !IsHidden(i) && i.Size > 0).ToArray();
        var res = new List<Group>();
        foreach (var bySize in snap.GroupBy(i => i.Size).Where(g => g.Count() > 1))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var same in bySize.GroupBy(i => QuickHash(i.Path)).Where(g => g.Key != null && g.Count() > 1))
            {
                var items = same.OrderBy(i => i.Modified).ToArray(); // oldest first = the one to keep
                res.Add(new Group(items[0].Name, $"{items[0].Size / 1048576.0:0.#} MB each", items));
            }
        }
        return res.OrderByDescending(g => g.Items[0].Size * (g.Items.Length - 1)).ToList();
    }

    // ---------- exclude suggestions ----------
    /// <summary>Folder names that mostly hold junk-looking media (tiny images, or things the dummy filter already hides).</summary>
    public List<string> SuggestExcludes()
    {
        MediaItem[] snap;
        lock (_items) snap = _items.Values.ToArray();
        return snap.Where(i => !Excluded(i.Folder))
            .GroupBy(i => Path.GetFileName(i.Folder).ToLowerInvariant())
            .Where(g => g.Key.Length > 2 && g.Count() >= 8 &&
                (g.Count(IsHidden) * 10 >= g.Count() * 7 ||
                 (g.Average(i => (double)i.Size) < 120 * 1024 && g.Count(i => i.Kind == Kind.Image) * 10 >= g.Count() * 8)))
            .OrderByDescending(g => g.Count()).Take(8).Select(g => g.Key).ToList();
    }

    public List<Group> Build(Func<MediaItem, bool> pred, bool albums)
    {
        MediaItem[] snap;
        lock (_items) snap = _items.Values.ToArray();
        IEnumerable<MediaItem> q = snap.Where(i => !IsHidden(i) && pred(i));
        // ponytail: duplicate = same size + same name; hash compare if false positives matter
        if (S.HideDupes) q = q.GroupBy(i => (i.Size, i.Name.ToLowerInvariant())).Select(g => g.OrderBy(i => i.Path.Length).First());
        var list = q.ToList();

        int dir = S.Desc ? -1 : 1;
        Comparison<MediaItem> cmp = S.Sort switch
        {
            "name" => (a, b) => Natural(a.Name, b.Name),
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "type" => (a, b) => { int c = a.Kind.CompareTo(b.Kind); return c != 0 ? c : Natural(a.Ext, b.Ext); },
            _ => (a, b) => a.Modified.CompareTo(b.Modified),
        };
        list.Sort((a, b) =>
        {
            if (albums) { int f = Natural(a.Folder, b.Folder); if (f != 0) return f; }
            int c = dir * cmp(a, b);
            return c != 0 ? c : Natural(a.Name, b.Name);
        });

        var groups = new List<Group>();
        string? curKey = null; string head = "", sub = ""; var buf = new List<MediaItem>();
        void Flush() { if (buf.Count > 0) groups.Add(new Group(head, sub, buf.ToArray())); buf = new(); }
        foreach (var i in list)
        {
            string key, h, s;
            if (albums)
            {
                key = i.Folder; h = Path.GetFileName(i.Folder); if (h.Length == 0) h = i.Folder;
                s = Path.GetDirectoryName(i.Folder) ?? "";
            }
            else { key = h = Label(i); s = ""; }
            if (key != curKey) { Flush(); curKey = key; head = h; sub = s; }
            buf.Add(i);
        }
        Flush();
        return groups;
    }

    string Label(MediaItem i)
    {
        switch (S.Sort)
        {
            case "name": return char.IsLetter(i.Name[0]) ? char.ToUpperInvariant(i.Name[0]).ToString() : "#";
            case "size":
                const long MB = 1024 * 1024;
                return i.Size >= 1024 * MB ? "Over 1 GB" : i.Size >= 100 * MB ? "100 MB – 1 GB" : i.Size >= 10 * MB ? "10 – 100 MB" : i.Size >= MB ? "1 – 10 MB" : "Under 1 MB";
            case "type": return i.Kind switch { Kind.Image => "Pictures", Kind.Video => "Videos", Kind.Audio => "Music", _ => "Documents" };
            default:
                var d = i.Modified.Date; var days = (DateTime.Today - d).Days;
                return days <= 0 ? "Today" : days == 1 ? "Yesterday" : d.Year == DateTime.Today.Year ? d.ToString("ddd, MMM d") : d.ToString("MMM d, yyyy");
        }
    }
}

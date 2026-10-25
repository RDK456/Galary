using System.ComponentModel;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace Galary.Models;

public enum Kind { Image, Video, Audio, Doc }

public sealed class MediaItem : INotifyPropertyChanged
{
    static readonly Dictionary<string, Kind> Map = BuildMap();

    static Dictionary<string, Kind> BuildMap()
    {
        var d = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        void Add(Kind k, string list) { foreach (var e in list.Split(' ')) d["." + e] = k; }
        // no "ts": it is TypeScript far more often than MPEG-TS; "mts"/"m2ts" cover camera files
        Add(Kind.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg m2ts mts 3gp ogv vob");
        Add(Kind.Audio, "mp3 flac wav aac m4a ogg opus wma aiff alac mka");
        Add(Kind.Image, "jpg jpeg png gif bmp webp tif tiff heic heif avif");
        Add(Kind.Doc, "pdf doc docx xls xlsx ppt pptx odt epub");
        return d;
    }

    // ponytail: images under 16 KB are skipped as icons/emoji; make configurable if it hides real photos
    public static MediaItem? From(string path, long size, DateTime modified)
    {
        if (!Map.TryGetValue(IOPath.GetExtension(path), out var k)) return null;
        if (k == Kind.Image && size < 16 * 1024) return null;
        return new MediaItem
        {
            Path = path, Name = IOPath.GetFileName(path), Ext = IOPath.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            Folder = IOPath.GetDirectoryName(path) ?? "", Kind = k, Size = size, Modified = modified,
        };
    }

    public static MediaItem Url(string url) => new() { Path = url, Name = url, Ext = "URL", Kind = Kind.Video, IsUrl = true };

    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Ext { get; init; } = "";
    public string Folder { get; init; } = "";
    public Kind Kind { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public bool IsUrl { get; init; }

    public bool IsVideo => Kind == Kind.Video;
    public bool IsCaption => Kind is Kind.Audio or Kind.Doc;
    public bool IsMedia => !IsCaption;

    bool _sel; double _chk;
    public bool Selected { get => _sel; set { _sel = value; Raise(nameof(Selected)); } }
    public double CheckOpacity { get => _chk; set { _chk = value; Raise(nameof(CheckOpacity)); } } // selection circle: 1 on hover or while picking

    // laid-out size in the justified grid; set by MainWindow.Layout
    double _w, _h;
    public double TileW { get => _w; set { _w = value; Raise(nameof(TileW)); } }
    public double TileH { get => _h; set { _h = value; Raise(nameof(TileH)); } }
    public string Glyph => Kind switch { Kind.Video => "", Kind.Audio => "", Kind.Doc => "", _ => "" };

    public int W { get; set; }       // image pixel size, read in the background after a scan (0 = unknown)
    public int H { get; set; }
    public bool Probed { get; set; }
    public bool Realized { get; set; }
    public bool Animated { get; set; } // entrance already played: rebuilds during a scan must not replay it

    bool _fav;
    public bool Fav { get => _fav; set { _fav = value; Raise(nameof(Fav)); } }

    ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

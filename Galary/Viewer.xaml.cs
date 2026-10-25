using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Galary.Models;
using Galary.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Galary;

public partial class Viewer : System.Windows.Controls.UserControl
{
    static readonly float[] Speeds = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
    static readonly string[] WebExts = { "PDF", "TXT", "CSV" };

    readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(260) }; // single click waits to see if it becomes a double click
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };
    LibVLC? _vlc;
    MediaPlayer? _mp;
    List<MediaItem> _list = new();
    int _idx, _speed = 2, _gen;
    bool _open, _prog, _full, _webReady, _panning;
    Point _panFrom, _panStart;
    WindowState _prevState;

    public Settings? Cfg { get; set; }       // resume positions, slideshow speed
    public Action? Persist { get; set; }     // save settings
    MediaItem? _playing;                     // item whose position is saved on leave
    long _resumeTo, _a = -1, _b = -1;        // resume target, A-B loop points
    bool _mini;
    List<MediaItem>? _orig;                  // original order while shuffled
    WindowState _miniState;
    Rect _miniRect;
    (double W, double H) _miniMin;
    static readonly double[] SlideSteps = { 2, 3.5, 5, 8, 12 };

    public Action<MediaItem>? ToggleFav { get; set; }
    public Func<MediaItem, bool>? DeleteItem { get; set; } // returns true when the file was really removed
    readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromSeconds(3.5) };
    double _rot;
    bool _moved;
    public bool IsOpen => _open;

    public Viewer()
    {
        InitializeComponent();
_slide.Tick += (_, _) => { if (_idx + 1 < _list.Count) NextItem(+1); else { _slide.Stop(); UpdateCounter(); } };
        _hide.Tick += (_, _) => { _hide.Stop(); if (_mp?.IsPlaying == true && !Bar.IsMouseOver) SetChrome(false); };
        _click.Tick += (_, _) => { _click.Stop(); Play_Click(this, new RoutedEventArgs()); };
    }

    MediaItem Cur => _list[_idx];

    // ---------- open / close ----------
    public void Open(List<MediaItem> items, int index)
    {
        if (items.Count == 0 || index < 0 || index >= items.Count) return;
        _list = new List<MediaItem>(items); _idx = index; _open = true; _orig = null; // own copy: deleting here must not reshuffle the gallery's list
        Visibility = Visibility.Visible;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Show();
        Focus();
    }

    void Close()
    {
        if (!_open) return;
        _open = false; _gen++; _hide.Stop(); _slide.Stop(); InfoCard.Visibility = Visibility.Collapsed;
        if (_mini) Mini_Click(this, new RoutedEventArgs());
        if (_full) Full_Click(this, new RoutedEventArgs());
        SavePos(); Persist?.Invoke();
        var mp = _mp; if (mp != null) Task.Run(() => { try { mp.Stop(); } catch { } });
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        // hide the VideoView while the viewer is still visible: its overlay window only follows a visible->hidden change of its own
        a.Completed += (_, _) => { if (!_open) { HideAll(); Pic.Source = null; Visibility = Visibility.Collapsed; } };
        Root.BeginAnimation(OpacityProperty, a);
    }

    public void Dispose()
    {
        _hide.Stop();
        try { _mp?.Dispose(); _vlc?.Dispose(); } catch { }
        try { Web.Dispose(); } catch { }
    }

    // ---------- load current item ----------
    void HideAll()
    {
        PicHost.Visibility = Web.Visibility = DocPanel.Visibility = Video.Visibility = MsgS.Visibility = Visibility.Collapsed;
        Art.Visibility = MsgV.Visibility = Film.Visibility = Visibility.Collapsed;
        Over.Visibility = Visibility.Collapsed; // VideoView's overlay window outlives its host's Visibility: empty it explicitly
    }

    // remember where a long video/audio stopped; near the start or end it is forgotten
    void SavePos()
    {
        var p = _playing; _playing = null;
        if (p == null || p.IsUrl || _mp == null || Cfg == null) return;
        long len = _mp.Length, t = _mp.Time;
        if (len > 60000 && t > 10000 && t < len - 15000) Cfg.Resume[p.Path] = t; else Cfg.Resume.Remove(p.Path);
    }

    async void Show()
    {
        int gen = ++_gen;
        var it = Cur;
        SavePos();
        _a = _b = -1; LoopBtn.Foreground = Brushes.White;
        _mp?.Stop();
        HideAll();
        SetChrome(true);
        TitleText.Text = it.IsUrl ? it.Path : it.Name;
        UpdateCounter();
        InfoCard.Visibility = Visibility.Collapsed;
        FavBtn.Content = it.Fav ? "" : "";
        FavBtn.Visibility = OpenBtn.Visibility = it.IsUrl ? Visibility.Collapsed : Visibility.Visible;

        if (!it.IsUrl && !File.Exists(it.Path)) { Msg("This file is no longer available."); return; }

        switch (it.Kind)
        {
            case Kind.Image: await ShowImage(it, gen); break;
            case Kind.Doc: await ShowDoc(it, gen); break;
            default: PlayMedia(it, gen); break;
        }
    }

    void UpdateCounter() => Counter.Text = (_slide.IsEnabled ? "Slideshow  ·  " : "") + (_list.Count > 1 ? $"{_idx + 1} / {_list.Count}" : "");

    void Msg(string text) { MsgS.Text = text; MsgS.Visibility = Visibility.Visible; }

    async Task ShowImage(MediaItem it, int gen)
    {
        PZ.ScaleX = PZ.ScaleY = 1; PT.X = PT.Y = 0; _rot = 0; PR.BeginAnimation(RotateTransform.AngleProperty, null); PR.Angle = 0;
        ImageSource? src;
        try
        {
            src = await Task.Run(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(it.Path);
                bi.DecodePixelWidth = 3840; // cap decode size so huge photos stay snappy
                bi.EndInit(); bi.Freeze();
                return (ImageSource)bi;
            });
        }
        catch { src = await Thumbs.Get(it.Path, 1600); } // heic/avif etc: let the shell decode it
        if (gen != _gen) return;
        if (src == null) { Msg("Can't display this image."); return; }
        Pic.Source = src;
        PicHost.Visibility = Visibility.Visible;
        PicHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        BuildFilm();
    }

    async Task ShowDoc(MediaItem it, int gen)
    {
        if (WebExts.Contains(it.Ext) && await EnsureWeb())
        {
            if (gen != _gen) return;
            Web.Visibility = Visibility.Visible;
            Web.CoreWebView2.Navigate(new Uri(it.Path).AbsoluteUri);
            return;
        }
        if (gen != _gen) return;
        DocName.Text = it.Name;
        DocMeta.Text = $"{it.Ext} · {Fmt(it.Size)}";
        DocThumb.ImageSource = null;
        DocPanel.Visibility = Visibility.Visible;
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        DocScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        DocScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
        var t = await Thumbs.Get(it.Path, 480);
        if (gen == _gen) DocThumb.ImageSource = t;
    }

    async Task<bool> EnsureWeb()
    {
        if (_webReady) return true;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Galary", "wv2"));
            await Web.EnsureCoreWebView2Async(env);
            return _webReady = true;
        }
        catch { return false; } // no WebView2 runtime: caller falls back to the "Open" card
    }

    void PlayMedia(MediaItem it, int gen)
    {
        if (_vlc == null)
        {
            try { _vlc = new LibVLC(); _mp = new MediaPlayer(_vlc); }
            catch { Video.Visibility = Visibility.Visible; Over.Visibility = Visibility.Visible; ShowV("VLC engine not found."); return; }
            _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (!_open) return;
                if (_playing != null) Cfg?.Resume.Remove(_playing.Path); // finished: next time starts from the top
                _playing = null;
                NextItem(+1, true);
            });
            _mp.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RefreshTracks();
                if (_resumeTo > 0 && _mp != null) { _mp.Time = _resumeTo; Flash($"Resumed at {Clock(_resumeTo)}"); _resumeTo = 0; }
            });
            _mp.TimeChanged += (_, e) => Dispatcher.BeginInvoke(() => Tick(e.Time));
            _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => ShowV("Can't play this file."));
            Video.MediaPlayer = _mp;
            _mp.Volume = (int)VolSlider.Value;
        }
        Video.Visibility = Visibility.Visible;
        Over.Visibility = Visibility.Visible;
        MsgV.Visibility = Visibility.Collapsed;
        bool audio = it.Kind == Kind.Audio;
        Art.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        if (audio) _ = LoadArt(it, gen);

        Seek.Value = 0; CurT.Text = "0:00"; TotT.Text = "0:00";
        SpeedBtn.Content = SpeedText();
        using var media = it.IsUrl ? new Media(_vlc, new Uri(it.Path)) : new Media(_vlc, it.Path, FromType.FromPath);
        _playing = it;
        _resumeTo = !it.IsUrl && Cfg != null && Cfg.Resume.TryGetValue(it.Path, out var rp) ? rp : 0;
        _mp!.Play(media);
        _mp.SetRate(Speeds[_speed]);
        PlayBtn.Content = "";
        Over.Focus();
    }

    string SpeedText() => Speeds[_speed] == 1 ? "1×" : Speeds[_speed] + "×";

    void ShowV(string text) { MsgV.Text = text; MsgV.Visibility = Visibility.Visible; Art.Visibility = Visibility.Collapsed; }

    async Task LoadArt(MediaItem it, int gen)
    {
        ArtTitle.Text = it.Name;
        ArtBrush.ImageSource = null; ArtBg.Source = null;
        var t = await Thumbs.Get(it.Path, 600);
        if (gen != _gen) return;
        ArtBrush.ImageSource = t; ArtBg.Source = t;
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }

    // ---------- playback state ----------
    void Tick(long ms)
    {
        if (_mp == null || !_open) return;
        if (_b > 0 && ms >= _b) { _mp.Time = _a; return; } // A-B loop
        long len = _mp.Length;
        _prog = true;
        if (len > 0) Seek.Value = Math.Clamp(ms * 1000.0 / len, 0, 1000);
        _prog = false;
        CurT.Text = Clock(ms); TotT.Text = Clock(len);
        PlayBtn.Content = _mp.IsPlaying ? "" : "";
    }

    static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    static string Fmt(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):0} KB",
        _ => $"{b} B",
    };

    void RefreshTracks()
    {
        if (_mp == null) return;
        AudioBtn.Visibility = _mp.AudioTrackCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        SubBtn.Visibility = Cur.Kind == Kind.Video ? Visibility.Visible : Visibility.Collapsed; // always there: subtitle files can be added
    }

    void SetChrome(bool on)
    {
        Bar.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        Bar.IsHitTestVisible = on;
        Over.Cursor = on ? Cursors.Arrow : Cursors.None;
        _hide.Stop();
        if (on && _mp?.IsPlaying == true) _hide.Start();
    }

    // ---------- navigation ----------
    void NextItem(int dir, bool ended = false)
    {
        int n = _idx + dir;
        if (n < 0 || n >= _list.Count) { if (ended) PlayBtn.Content = ""; return; }
        _idx = n; Show();
    }

    void Back_Click(object s, RoutedEventArgs e) => Close();
    void Prev_Click(object s, RoutedEventArgs e) => NextItem(-1);
    void Next_Click(object s, RoutedEventArgs e) => NextItem(+1);

    void Fav_Click(object s, RoutedEventArgs e)
    {
        ToggleFav?.Invoke(Cur);
        FavBtn.Content = Cur.Fav ? "" : "";
    }

    void OpenExt_Click(object s, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Cur.Path) { UseShellExecute = true }); } catch { }
    }

    // ---------- image zoom / pan ----------
    void Pic_Wheel(object s, MouseWheelEventArgs e)
    {
        double z = Math.Clamp(PZ.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 1, 12);
        PZ.ScaleX = PZ.ScaleY = z;
        if (z == 1) PT.X = PT.Y = 0;
        e.Handled = true;
    }

    void Pic_Down(object s, MouseButtonEventArgs e)
    {
        _moved = false;
        if (e.ClickCount == 2) { PZ.ScaleX = PZ.ScaleY = PZ.ScaleX > 1 ? 1 : 2.5; PT.X = PT.Y = 0; return; }
        if (PZ.ScaleX <= 1) return;
        _panning = true; _panFrom = e.GetPosition(PicHost); _panStart = new Point(PT.X, PT.Y);
        PicHost.CaptureMouse();
    }

    void Pic_Move(object s, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(PicHost);
        if ((p - _panFrom).Length > 3) _moved = true;
        PT.X = _panStart.X + p.X - _panFrom.X; PT.Y = _panStart.Y + p.Y - _panFrom.Y;
    }

    void Pic_Up(object s, MouseButtonEventArgs e) { _panning = false; PicHost.ReleaseMouseCapture(); }

    void Stage_Up(object s, MouseButtonEventArgs e)
    {
        if (_moved) { _moved = false; return; }
        if (e.OriginalSource == Stage || e.OriginalSource == PicHost) Close();
    }

    // ---------- gallery-app extras ----------
    void ToggleSlide()
    {
        if (_slide.IsEnabled) _slide.Stop();
        else if (_list.Count > 1) { _slide.Interval = TimeSpan.FromSeconds(Cfg?.SlideSec ?? 3.5); _slide.Start(); }
        UpdateCounter();
    }

    void Rotate()
    {
        if (PicHost.Visibility != Visibility.Visible) return;
        double to = _rot + 90;
        PR.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(_rot, to, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _rot = to;
    }

    void ToggleInfo()
    {
        if (InfoCard.Visibility == Visibility.Visible) { InfoCard.Visibility = Visibility.Collapsed; return; }
        var it = Cur;
        var lines = new List<string> { it.Name, "", $"Type  {it.Ext}", $"Size  {Fmt(it.Size)}" };
        if (!it.IsUrl) lines.Add($"Modified  {it.Modified:d MMM yyyy, HH:mm}");
        if (Pic.Source is BitmapSource bs && PicHost.Visibility == Visibility.Visible) lines.Add($"Dimensions  {(it.W > 0 ? it.W : bs.PixelWidth)} × {(it.H > 0 ? it.H : bs.PixelHeight)}");
        else if (_mp != null && _mp.Length > 0 && Video.Visibility == Visibility.Visible) lines.Add($"Length  {Clock(_mp.Length)}");
        if (PicHost.Visibility == Visibility.Visible && !it.IsUrl) lines.AddRange(Exif(it.Path));
        lines.Add(""); lines.Add(it.IsUrl ? it.Path : it.Folder);
        InfoText.Text = string.Join("\n", lines);
        InfoCard.Visibility = Visibility.Visible;
    }

    void DeleteCurrent()
    {
        if (Cur.IsUrl || DeleteItem?.Invoke(Cur) != true) return;
        _orig?.Remove(Cur);
        _list.RemoveAt(_idx);
        if (_list.Count == 0) { Close(); return; }
        _idx = Math.Min(_idx, _list.Count - 1);
        Show();
    }

    void CopyFile()
    {
        if (Cur.IsUrl) return;
        try { var c = new System.Collections.Specialized.StringCollection { Cur.Path }; Clipboard.SetFileDropList(c); } catch { }
    }

    // ---------- hint toast ----------
    public void Flash(string text)
    {
        HintVT.Text = HintST.Text = text;
        foreach (var b in new[] { HintV, HintS })
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(150)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1500)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1900)));
            b.BeginAnimation(OpacityProperty, a);
        }
    }

    // ---------- filmstrip ----------
    void BuildFilm()
    {
        FilmItems.Children.Clear();
        bool on = _list.Count > 1;
        Film.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PicHost.Margin = new Thickness(0, 0, 0, on ? 96 : 12);
        if (!on) return;
        int gen = _gen;
        for (int k = Math.Max(0, _idx - 7); k <= Math.Min(_list.Count - 1, _idx + 7); k++)
        {
            int idx = k; var it = _list[k];
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            var b = new Border
            {
                Width = 64, Height = 64, Margin = new Thickness(3), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
                Background = (Brush)FindResource("Surface"), BorderThickness = new Thickness(2),
                BorderBrush = k == _idx ? (Brush)FindResource("Accent") : Brushes.Transparent, Opacity = k == _idx ? 1 : 0.6,
                Child = new Border { CornerRadius = new CornerRadius(9), Background = brush },
            };
            b.MouseLeftButtonUp += (_, e) => { _idx = idx; Show(); e.Handled = true; };
            FilmItems.Children.Add(b);
            _ = Thumbs.Get(it.Path, 220).ContinueWith(t => Dispatcher.BeginInvoke(() => { if (gen == _gen && t.Result is { } src) brush.ImageSource = src; }));
        }
    }

    // ---------- photo details ----------
    static List<string> Exif(string path)
    {
        var r = new List<string>();
        try
        {
            var f = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (f.Metadata is not BitmapMetadata m) return r;
            object? Get(string q) { try { return m.GetQuery(q); } catch { return null; } }
            double Rat(object? o) => o is ulong u ? (double)(uint)u / Math.Max(1u, (uint)(u >> 32)) : 0; // EXIF rational: low 32 = numerator, high 32 = denominator
            string cam = $"{m.CameraManufacturer} {m.CameraModel}".Trim();
            if (cam.Length > 0) r.Add("Camera  " + cam);
            if (!string.IsNullOrEmpty(m.DateTaken)) r.Add("Taken  " + m.DateTaken);
            double fn = Rat(Get("/app1/ifd/exif/{ushort=33437}")); if (fn > 0) r.Add($"Aperture  f/{fn:0.#}");
            double ex = Rat(Get("/app1/ifd/exif/{ushort=33434}")); if (ex > 0) r.Add(ex < 1 ? $"Shutter  1/{Math.Round(1 / ex)} s" : $"Shutter  {ex:0.#} s");
            int? iso = Get("/app1/ifd/exif/{ushort=34855}") switch { ushort u => u, ushort[] { Length: > 0 } a => a[0], int i => i, _ => null };
            if (iso != null) r.Add($"ISO  {iso}");
            double fl = Rat(Get("/app1/ifd/exif/{ushort=37386}")); if (fl > 0) r.Add($"Focal length  {fl:0.#} mm");
            if (r.Count > 0) r.Insert(0, "");
        }
        catch { }
        return r;
    }

    // ---------- loop, snapshot, wallpaper, shuffle, zoom, mini player ----------
    void Loop_Click(object s, RoutedEventArgs e) => Loop();

    void Loop()
    {
        if (_mp == null || Video.Visibility != Visibility.Visible) return;
        if (_a < 0) { _a = _mp.Time; Flash("Loop start set. Press L again for the end"); }
        else if (_b < 0)
        {
            _b = _mp.Time;
            if (_b <= _a + 500) { _a = _b = -1; Flash("Loop cleared"); } else Flash("Looping this section. Press L to stop");
        }
        else { _a = _b = -1; Flash("Loop off"); }
        LoopBtn.Foreground = _a >= 0 ? (Brush)FindResource("Accent") : Brushes.White;
    }

    void Snap_Click(object s, RoutedEventArgs e) => Snap();

    void Snap()
    {
        if (_mp == null || Video.Visibility != Visibility.Visible || Cur.Kind != Kind.Video) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Galary Screenshots");
        try { Directory.CreateDirectory(dir); } catch { }
        string name = Cur.IsUrl ? "stream" : Path.GetFileNameWithoutExtension(Cur.Name);
        var file = Path.Combine(dir, $"{name}_{DateTime.Now:HHmmss}.png");
        Flash(_mp.TakeSnapshot(0, file, 0, 0) ? "Frame saved to Pictures\\Galary Screenshots" : "Couldn't save the frame");
    }

    void Wallpaper()
    {
        if (PicHost.Visibility != Visibility.Visible || Cur.IsUrl) return;
        Flash(Tools.Wallpaper(Cur.Path) ? "Set as wallpaper" : "Couldn't set the wallpaper");
    }

    void Shuffle()
    {
        if (_list.Count < 2) return;
        var cur = Cur;
        if (_orig == null)
        {
            _orig = _list;
            var rest = _list.Where(x => x != cur).OrderBy(_ => Random.Shared.Next()).ToList();
            rest.Insert(0, cur); _list = rest; _idx = 0;
            Flash("Shuffle on");
        }
        else { _list = _orig; _orig = null; _idx = Math.Max(0, _list.IndexOf(cur)); Flash("Shuffle off"); }
        UpdateCounter();
        if (PicHost.Visibility == Visibility.Visible) BuildFilm();
    }

    void SlideSpeed(int dir)
    {
        if (Cfg == null) return;
        int i = Array.FindIndex(SlideSteps, s => Math.Abs(s - Cfg.SlideSec) < 0.01);
        if (i < 0) i = 1;
        Cfg.SlideSec = SlideSteps[Math.Clamp(i + dir, 0, SlideSteps.Length - 1)];
        _slide.Interval = TimeSpan.FromSeconds(Cfg.SlideSec);
        Flash($"Slideshow: {Cfg.SlideSec:0.#}s per photo");
    }

    void Zoom(double f)
    {
        if (PicHost.Visibility != Visibility.Visible) return;
        double z = f == 0 ? 1 : Math.Clamp(PZ.ScaleX * f, 1, 12);
        PZ.ScaleX = PZ.ScaleY = z;
        if (z == 1) PT.X = PT.Y = 0;
    }

    // small always-on-top window in the corner; the same window shrinks and the gallery stays underneath
    void Mini_Click(object s, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w == null || _full || Video.Visibility != Visibility.Visible) return;
        _mini = !_mini;
        if (_mini)
        {
            _miniState = w.WindowState; w.WindowState = WindowState.Normal;
            _miniRect = new Rect(w.Left, w.Top, w.Width, w.Height); _miniMin = (w.MinWidth, w.MinHeight);
            w.MinWidth = 360; w.MinHeight = 240; w.Topmost = true; w.Width = 660; w.Height = 400;
            var wa = SystemParameters.WorkArea; w.Left = wa.Right - w.Width - 16; w.Top = wa.Bottom - w.Height - 16;
            Bar.Margin = new Thickness(8, 0, 8, 8);
        }
        else
        {
            w.Topmost = false; w.MinWidth = _miniMin.W; w.MinHeight = _miniMin.H;
            w.Left = _miniRect.Left; w.Top = _miniRect.Top; w.Width = _miniRect.Width; w.Height = _miniRect.Height; w.WindowState = _miniState;
            Bar.Margin = new Thickness(40, 0, 40, 26);
        }
        var vis = _mini ? Visibility.Collapsed : Visibility.Visible;
        Top.Visibility = VolSlider.Visibility = LoopBtn.Visibility = SnapBtn.Visibility = SpeedBtn.Visibility = vis;
        RefreshOverlay();
    }

    // ---------- transport ----------
    void Over_Move(object s, MouseEventArgs e) => SetChrome(true);

    void Over_Click(object s, MouseButtonEventArgs e)
    {
        if (InBar(e.OriginalSource as DependencyObject)) return;
        _click.Stop(); _click.Start();
    }

    bool InBar(DependencyObject? d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == Bar || d == TrackPop.Child) return true;
        return false;
    }

    void Play_Click(object s, RoutedEventArgs e)
    {
        if (_mp == null) return;
        if (_mp.State is VLCState.Ended or VLCState.Stopped) { _mp.Play(); return; }
        bool wasPlaying = _mp.IsPlaying;
        _mp.Pause(); // toggles; IsPlaying flips asynchronously so use the pre-toggle value
        PlayBtn.Content = wasPlaying ? "" : "";
        SetChrome(true);
    }

    void Seek_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_prog || _mp == null || _mp.Length <= 0) return;
        _mp.Time = (long)(e.NewValue / 1000.0 * _mp.Length);
    }

    void Back10_Click(object s, RoutedEventArgs e) => Jump(-10000);
    void Fwd10_Click(object s, RoutedEventArgs e) => Jump(10000);

    // frame step: libvlc only steps forward; stepping back is an approximate 40ms seek
    void Step(int dir)
    {
        if (_mp == null) return;
        if (_mp.IsPlaying) _mp.Pause();
        if (dir > 0) _mp.NextFrame(); else _mp.Time = Math.Max(0, _mp.Time - 42);
        PlayBtn.Content = "\uE768";
    }

    void Jump(long ms)
    {
        if (_mp == null || _mp.Length <= 0) return;
        _mp.Time = Math.Clamp(_mp.Time + ms, 0, _mp.Length);
    }

    void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mp != null) _mp.Volume = (int)e.NewValue;
        if (MuteBtn != null) MuteBtn.Content = e.NewValue == 0 ? "" : "";
    }

    void Mute_Click(object s, RoutedEventArgs e)
    {
        if (_mp == null) return;
        _mp.Mute = !_mp.Mute;
        MuteBtn.Content = _mp.Mute ? "" : "";
    }

    void Speed_Click(object s, RoutedEventArgs e)
    {
        _speed = (_speed + 1) % Speeds.Length;
        SpeedBtn.Content = SpeedText();
        _mp?.SetRate(Speeds[_speed]);
    }

    void Audio_Click(object s, RoutedEventArgs e)
    {
        if (_mp != null) TrackMenu("AUDIO TRACK", _mp.AudioTrackDescription, _mp.AudioTrack, id => _mp.SetAudioTrack(id), AudioBtn);
    }

    void Sub_Click(object s, RoutedEventArgs e)
    {
        if (_mp == null) return;
        TrackMenu("SUBTITLES", _mp.SpuDescription ?? Array.Empty<TrackDescription>(), _mp.Spu, id => _mp.SetSpu(id), SubBtn);
        var add = new Button { Style = (Style)FindResource("Opt"), Content = new TextBlock { Text = "Add subtitle file…", FontSize = 14, Margin = new Thickness(26, 0, 0, 0) } };
        add.Click += (_, _) =>
        {
            TrackPop.IsOpen = false;
            var d = new OpenFileDialog { Title = "Choose subtitles", Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub|All files|*.*" };
            if (d.ShowDialog() == true) Flash(_mp.AddSlave(MediaSlaveType.Subtitle, new Uri(d.FileName).AbsoluteUri, true) ? "Subtitles added" : "Couldn't load those subtitles");
        };
        TrackItems.Children.Add(add);
    }

    void TrackMenu(string head, TrackDescription[] tracks, int current, Action<int> pick, UIElement anchor)
    {
        TrackItems.Children.Clear();
        TrackItems.Children.Add(new TextBlock { Text = head, FontSize = 11, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 0, 4) });
        foreach (var t in tracks)
        {
            int id = t.Id;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = id == current ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBlock { Text = t.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis }; Grid.SetColumn(tb, 1); g.Children.Add(tb);
            var b = new Button { Style = (Style)FindResource("Opt"), Content = g };
            b.Click += (_, _) => { pick(id); TrackPop.IsOpen = false; };
            TrackItems.Children.Add(b);
        }
        TrackPop.PlacementTarget = anchor;
        TrackPop.IsOpen = true;
    }

    void Full_Click(object s, RoutedEventArgs e)
    {
        if (_mini) Mini_Click(this, new RoutedEventArgs());
        var w = Window.GetWindow(this);
        if (w == null) return;
        _full = !_full;
        if (_full) { _prevState = w.WindowState; w.WindowStyle = WindowStyle.None; w.WindowState = WindowState.Maximized; }
        else { w.WindowStyle = WindowStyle.SingleBorderWindow; w.WindowState = _prevState; }
        Top.Visibility = _full ? Visibility.Collapsed : Visibility.Visible;
        SetChrome(true);
        RefreshOverlay();
    }

    // VideoView's overlay window follows its host lazily; poke the layout after the window changes size
    void RefreshOverlay()
    {
        foreach (int ms in new[] { 60, 350 })
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var m = Video.Margin;
                Video.Margin = new Thickness(m.Left, m.Top, m.Right, m.Bottom + 1); Video.UpdateLayout();
                Video.Margin = m; Video.UpdateLayout();
            };
            t.Start();
        }
    }

    // ---------- mouse macros ----------
    // side buttons = previous/next, middle = close, right = play/pause, double-click video = fullscreen, wheel on video = volume
    bool Macro(MouseButtonEventArgs e)
    {
        bool av = Video.Visibility == Visibility.Visible && _mp != null;
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1: NextItem(-1); return true;
            case MouseButton.XButton2: NextItem(+1); return true;
            case MouseButton.Middle: Close(); return true;
            case MouseButton.Right when av: Play_Click(this, new RoutedEventArgs()); return true;
            case MouseButton.Left when av && e.ClickCount == 2 && !InBar(e.OriginalSource as DependencyObject):
                _click.Stop(); // double click: left third = back 10s, right third = forward 10s, middle = fullscreen
                double x = e.GetPosition(Over).X, w = Over.ActualWidth;
                if (x < w / 3) Jump(-10000); else if (x > w * 2 / 3) Jump(10000); else Full_Click(this, new RoutedEventArgs());
                return true;
        }
        return false;
    }

    void Root_Down(object s, MouseButtonEventArgs e) { if (_open && Macro(e)) e.Handled = true; }
    void Over_Down(object s, MouseButtonEventArgs e) { SetChrome(true); if (_open && Macro(e)) e.Handled = true; }

    void Over_Wheel(object s, MouseWheelEventArgs e)
    {
        if (_mp == null) return;
        int dir = e.Delta > 0 ? 1 : -1;
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Shift)) Jump(dir * 5000);                                  // Shift + wheel: seek 5s
        else if (m.HasFlag(ModifierKeys.Control)) { _speed = Math.Clamp(_speed + dir, 0, Speeds.Length - 1); SpeedBtn.Content = SpeedText(); _mp.SetRate(Speeds[_speed]); } // Ctrl + wheel: speed
        else if (m.HasFlag(ModifierKeys.Alt)) Step(dir);                                      // Alt + wheel: frame by frame
        else VolSlider.Value = Math.Clamp(VolSlider.Value + dir * 5, 0, 100);                  // wheel: volume
        SetChrome(true);
        e.Handled = true;
    }

    // ---------- keyboard ----------
    public bool HandleKey(KeyEventArgs e)
    {
        bool av = Video.Visibility == Visibility.Visible && _mp != null;
        var none = new RoutedEventArgs();
        if (av) SetChrome(true);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key == Key.System ? e.SystemKey : e.Key)
        {
            case Key.S when !ctrl: ToggleSlide(); return true;
            case Key.R: Rotate(); return true;
            case Key.L: if (av) { Loop(); return true; } return false;
            case Key.T: if (av) { Snap(); return true; } return false;
            case Key.Q: if (av) { Mini_Click(this, none); return true; } return false;
            case Key.W: Wallpaper(); return true;
            case Key.H: Shuffle(); return true;
            case Key.OemOpenBrackets: SlideSpeed(-1); return true;
            case Key.OemCloseBrackets: SlideSpeed(+1); return true;
            case Key.OemPlus: case Key.Add: Zoom(1.25); return true;
            case Key.OemMinus: case Key.Subtract: Zoom(0.8); return true;
            case Key.D0: case Key.NumPad0: Zoom(0); return true;
            case Key.I: ToggleInfo(); return true;
            case Key.Delete: DeleteCurrent(); return true;
            case Key.C when ctrl: CopyFile(); return true;
            case Key.OemPeriod: if (av) { Step(+1); return true; } return false;
            case Key.OemComma: if (av) { Step(-1); return true; } return false;
            case Key.Left when av && ctrl: Jump(-60000); return true;
            case Key.Right when av && ctrl: Jump(60000); return true;
            case Key.Escape: if (_full) Full_Click(this, none); else Close(); return true;
            case Key.Left: if (av) Jump(-10000); else NextItem(-1); return true;
            case Key.Right: if (av) Jump(10000); else NextItem(+1); return true;
            case Key.PageUp: case Key.P: NextItem(-1); return true;
            case Key.PageDown: case Key.N: NextItem(+1); return true;
            case Key.Space: if (av) { Play_Click(this, none); return true; } return false;
            case Key.Up: if (av) { VolSlider.Value = Math.Min(100, VolSlider.Value + 5); return true; } return false;
            case Key.Down: if (av) { VolSlider.Value = Math.Max(0, VolSlider.Value - 5); return true; } return false;
            case Key.M: if (av) { Mute_Click(this, none); return true; } return false;
            case Key.F: Full_Click(this, none); return true;
            default: return false;
        }
    }
}

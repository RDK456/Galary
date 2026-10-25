using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Galary.Models;
using Galary.Services;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Galary;

public sealed record HeaderRow(string Title, string Sub);
public sealed record TileRow(MediaItem[] Tiles);

public partial class MainWindow : Window
{
    static readonly (string Glyph, string Label, string Title)[] Navs =
    {
        ("", "All media", "Gallery"), ("", "Photos", "Pictures"), ("", "Videos", "Videos"),
        ("", "Music", "Music"), ("", "Documents", "Documents"), ("", "Favorites", "Favorites"),
    };

    readonly Library _lib = Library.Load();
    readonly List<TextBlock> _navText = new();
    readonly DispatcherTimer _deb = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _rel = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _scanTick = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    List<Group> _groups = new();
    List<MediaItem> _flat = new();
    CancellationTokenSource? _cts;
    ScrollViewer? _sv;
    int _gen, _nav;
    bool _albums, _scanning, _anim;
    double _target;
    readonly TranslateTransform _listShift = new();
    DateTime _lastBounce;
    readonly HashSet<MediaItem> _sel = new();
    MediaItem? _anchor;
    readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(450) };
    MediaItem? _holdItem;
    Point _holdAt;
    bool _held; // long-press fired: swallow the click that follows the release
    bool Picking => _sel.Count > 0;

    string[] _rowLabels = Array.Empty<string>(); // group label per list row, for the scroll bubble
    List<Group>? _dup;                           // non-null while the duplicates view is showing
    List<string> _lastDel = new();
    Action? _toastAct;
    Action<string>? _askAct;
    UIElement? _toolsBtn;
    readonly DispatcherTimer _toastT = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly DispatcherTimer _bubT = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DispatcherTimer _watchT = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    public MainWindow()
    {
        InitializeComponent();
        List.RenderTransform = _listShift;
        Head.RenderTransform = _headT;
        Nav.RenderTransform = _navT;
        MouseMove += (_, e) => { if (_chromeHidden) { var y = e.GetPosition(this).Y; if (y < 36 || y > ActualHeight - 90) HideChrome(false); } };
        Viewer.DeleteItem = TryDelete;
        BuildNav();
        SourceInitialized += (_, _) => Glass.Apply(this);
        Loaded += OnLoaded;
        Closed += (_, _) => { _lib.SaveSettings(); Viewer.Dispose(); };
        _deb.Tick += (_, _) => { _deb.Stop(); Refresh(); };
        _rel.Tick += (_, _) => { _rel.Stop(); Layout(); };
        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            if (_holdItem == null || _sel.Contains(_holdItem)) return;
            _held = true; Toggle(_holdItem, false);
        };
        PreviewMouseMove += (_, e) => { if (_hold.IsEnabled && (e.GetPosition(this) - _holdAt).Length > 6) _hold.Stop(); };
        PreviewMouseLeftButtonUp += (_, _) => _hold.Stop();
        _scanTick.Tick += (_, _) => { if (_sv != null && _sv.VerticalOffset > 80) Sub.Text = $"{_lib.Count:N0} found  ·  scanning…"; else Refresh(); };
        Viewer.ToggleFav = ToggleFav;
        Viewer.Cfg = _lib.S; Viewer.Persist = _lib.SaveSettings;
        _toastT.Tick += (_, _) => { _toastT.Stop(); HideToast(); };
        _bubT.Tick += (_, _) => { _bubT.Stop(); Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))); };
        _watchT.Tick += (_, _) => { _watchT.Stop(); if (!_scanning) Refresh(); };
        _lib.Changed += () => Dispatcher.BeginInvoke(() => { _watchT.Stop(); _watchT.Start(); });
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lib.LoadCache();
        ApplyTheme();
        UpdateSmart();
        ApplyAccent();
        Refresh();
        StartScan();
        OpenArgFile();
    }

    // "Open with Galary" / file association: open the file with the other media from its folder as neighbours
    void OpenArgFile()
    {
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (path == null) return;
        path = Path.GetFullPath(path);
        var sibs = new List<MediaItem>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
                if (MediaItem.From(f, new FileInfo(f).Length, File.GetLastWriteTime(f)) is { } m) sibs.Add(m);
        }
        catch { }
        sibs.Sort((a, b) => Library.Natural(a.Name, b.Name));
        int i = sibs.FindIndex(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Viewer.Open(sibs, i);
    }

    // ---------- nav pill ----------
    void BuildNav()
    {
        for (int i = 0; i < Navs.Length; i++)
        {
            int idx = i;
            var glyph = new TextBlock { Text = Navs[i].Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = Navs[i].Label, FontSize = 14.5, FontWeight = FontWeights.Medium, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _navText.Add(glyph); _navText.Add(label);
            var b = new Button
            {
                Style = (Style)FindResource("NavBtn"),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, label } },
            };
            b.Click += (_, _) => SetNav(idx);
            NavItems.Children.Add(b);
        }
        SetNav(0, false);
    }

    void SetNav(int i, bool refresh = true)
    {
        _nav = i; _dup = null;
        for (int k = 0; k < _navText.Count; k++) _navText[k].Foreground = k / 2 == i ? (Brush)FindResource("Ink") : (Brush)FindResource("Dim");
        IndT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(i * 44, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut } });
        Title.Text = Navs[i].Title;
        Title.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (refresh) Refresh();
    }

    Func<MediaItem, bool> NavFilter() => _nav switch
    {
        1 => i => i.Kind == Kind.Image,
        2 => i => i.Kind == Kind.Video,
        3 => i => i.Kind == Kind.Audio,
        4 => i => i.Kind == Kind.Doc,
        5 => i => i.Fav,
        _ => _ => true,
    };

    // ---------- data -> rows ----------
    async void Refresh()
    {
        int gen = ++_gen;
        var nav = NavFilter(); var qf = Query(Search.Text.Trim()); bool albums = _albums; var dup = _dup;
        var res = dup ?? await Task.Run(() => _lib.Build(i => nav(i) && qf(i), albums));
        if (gen != _gen) return;
        _groups = res;
        _flat = res.SelectMany(g => g.Items).ToList();
        Layout();
    }

    void Layout()
    {
        // justified rows: pack tiles at the target height until the row is full, then stretch the row to the exact width
        double avail = Math.Max(300, List.ActualWidth - 38 - 10), H = _lib.S.Tile * (_lib.S.Compact ? 0.7 : 1);
        const double Gap = 4; // 2px margin each side of a tile
        var rows = new List<object>();
        var labels = new List<string>();
        foreach (var g in _groups)
        {
            rows.Add(new HeaderRow(g.Head, g.Sub.Length > 0 ? $"{g.Items.Length} · {g.Sub}" : g.Items.Length.ToString()));
            labels.Add(g.Head);
            for (int i = 0; i < g.Items.Length;)
            {
                double sum = 0; int j = i;
                while (j < g.Items.Length) { sum += Aspect(g.Items[j]) * H; j++; if (sum + Gap * (j - i) >= avail) break; }
                int n = j - i;
                double f = sum + Gap * n >= avail ? (avail - Gap * n) / sum : 1; // short last row keeps its natural size
                for (int k = i; k < j; k++)
                {
                    g.Items[k].TileH = Math.Floor(H * f);
                    g.Items[k].TileW = Math.Floor(Aspect(g.Items[k]) * H * f);
                }
                rows.Add(new TileRow(g.Items[i..j])); labels.Add(g.Head);
                i = j;
            }
        }
        _rowLabels = labels.ToArray();
        _sv ??= Find<ScrollViewer>(List);
        double off = _sv?.VerticalOffset ?? 0;
        List.ItemsSource = rows;
        if (off > 0) Dispatcher.BeginInvoke(() => { _sv?.ScrollToVerticalOffset(off); _target = off; }, DispatcherPriority.Loaded);
        Empty.Visibility = rows.Count == 0 && !_scanning ? Visibility.Visible : Visibility.Collapsed;
        Sub.Text = $"{_flat.Count:N0} items" + (_scanning ? "  ·  scanning…" : "");
    }

    // ponytail: real image dimensions would need a header read per file; a stable per-file pick keeps the mosaic varied. Swap in decoded sizes if crops bother.
    static readonly double[] PhotoAspects = { 1, 4 / 3.0, 3 / 4.0, 3 / 2.0, 1, 2 / 3.0 };
    static double Aspect(MediaItem i) => i.Kind == Kind.Image && i.W > 0 && i.H > 0 ? Math.Clamp((double)i.W / i.H, 0.55, 2.2) : AspectGuess(i);

    static double AspectGuess(MediaItem i) => i.Kind switch
    {
        Kind.Video => 16 / 9.0,
        Kind.Audio => 1,
        Kind.Doc => 3 / 4.0,
        _ => PhotoAspects[i.Name.Sum(c => c) % PhotoAspects.Length],
    };

    void Rescan_Click(object s, RoutedEventArgs e) => StartScan();

    // ---------- window buttons ----------
    void WinClose_Click(object s, RoutedEventArgs e) => Close();
    void WinMin_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void WinMax_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------- header + bar slide away while scrolling down, return on scroll up or when the pointer nears an edge ----------
    readonly TranslateTransform _navT = new(), _headT = new();
    bool _chromeHidden;

    void List_Scrolled(object s, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        ShowBubble();
        if (e.VerticalChange > 0 && e.VerticalOffset > 140) HideChrome(true);
        else if (e.VerticalChange < -4 || e.VerticalOffset < 40) HideChrome(false);
    }

    void HideChrome(bool hide)
    {
        if (hide == _chromeHidden) return;
        _chromeHidden = hide;
        var ease = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut };
        // overlay, not resize: changing the list's height clamps the scroll offset and re-triggers this handler
        Head.BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(280)));
        _headT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hide ? -96 : 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        _navT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hide ? 130 : 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
    }

    // ---------- search syntax: kind:video ext:png size>10mb date:week in:holiday is:fav + plain words ----------
    static Func<MediaItem, bool> Query(string q)
    {
        var preds = new List<Func<MediaItem, bool>>();
        var words = new List<string>();
        foreach (var t in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^(kind|ext|date|size|in|is)([:<>])(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) { words.Add(t); continue; }
            string k = m.Groups[1].Value.ToLowerInvariant(), op = m.Groups[2].Value, v = m.Groups[3].Value.ToLowerInvariant();
            switch (k)
            {
                case "kind":
                    Kind? kd = v switch { "photo" or "photos" or "image" or "images" => Kind.Image, "video" or "videos" => Kind.Video, "audio" or "music" => Kind.Audio, "doc" or "docs" or "document" => Kind.Doc, _ => null };
                    preds.Add(i => kd != null && i.Kind == kd); break;
                case "ext": preds.Add(i => i.Ext.Equals(v.TrimStart('.'), StringComparison.OrdinalIgnoreCase)); break;
                case "is": preds.Add(i => v == "fav" || v == "favorite" ? i.Fav : false); break;
                case "in": preds.Add(i => i.Folder.Contains(v, StringComparison.OrdinalIgnoreCase)); break;
                case "date":
                    preds.Add(i => v switch
                    {
                        "today" => i.Modified.Date == DateTime.Today,
                        "week" => i.Modified >= DateTime.Today.AddDays(-7),
                        "month" => i.Modified >= DateTime.Today.AddMonths(-1),
                        "year" => i.Modified >= DateTime.Today.AddYears(-1),
                        _ => int.TryParse(v, out var y) && i.Modified.Year == y,
                    }); break;
                case "size":
                    var sm = System.Text.RegularExpressions.Regex.Match(v, @"^(\d+(?:\.\d+)?)(kb|mb|gb)?$");
                    if (!sm.Success || op == ":") { preds.Add(_ => false); break; }
                    double bytes = double.Parse(sm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) *
                        (sm.Groups[2].Value switch { "kb" => 1024.0, "gb" => 1073741824.0, _ => 1048576.0 });
                    preds.Add(op == ">" ? i => i.Size > bytes : i => i.Size < bytes); break;
            }
        }
        return i => preds.All(p => p(i)) && words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- toast ----------
    void Toast(string text, string? action = null, Action? act = null)
    {
        ToastText.Text = text; _toastAct = act;
        ToastBtn.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible; ToastBtnText.Text = action ?? "";
        ToastCard.Visibility = Visibility.Visible;
        ToastCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420))
            { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
        _toastT.Stop(); _toastT.Start();
    }

    void HideToast()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        a.Completed += (_, _) => { if (ToastCard.Opacity == 0) ToastCard.Visibility = Visibility.Collapsed; };
        ToastCard.BeginAnimation(OpacityProperty, a);
    }

    void Toast_Act(object s, RoutedEventArgs e) { var a = _toastAct; _toastAct = null; _toastT.Stop(); HideToast(); a?.Invoke(); }

    // ---------- undo delete ----------
    void AfterDelete(List<string> paths)
    {
        if (paths.Count == 0) return;
        _lastDel = paths;
        Toast(paths.Count == 1 ? "Moved to the Recycle Bin" : $"Moved {paths.Count} items to the Recycle Bin", "Undo", UndoDelete);
    }

    void UndoDelete()
    {
        var paths = _lastDel; _lastDel = new();
        int n = Tools.Restore(paths);
        foreach (var p in paths) _lib.Upsert(p);
        _lib.SaveCache(); Refresh();
        Toast(n > 0 ? $"Restored {n} item{(n == 1 ? "" : "s")}" : "Couldn't restore. Check the Recycle Bin");
    }

    // ---------- small input card (rename, save search) ----------
    void Ask(string title, string initial, Action<string> ok, bool selectName = false)
    {
        AskTitle.Text = title; AskBox.Text = initial; _askAct = ok;
        AskCard.Visibility = Visibility.Visible;
        AskCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        AskBox.Focus();
        int dot = initial.LastIndexOf('.');
        if (selectName && dot > 0) AskBox.Select(0, dot); else AskBox.SelectAll();
    }

    void HideAsk()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => AskCard.Visibility = Visibility.Collapsed;
        AskCard.BeginAnimation(OpacityProperty, a);
    }

    void Ask_Cancel(object s, RoutedEventArgs e) => HideAsk();

    void Ask_Ok(object s, RoutedEventArgs e)
    {
        var act = _askAct; var text = AskBox.Text.Trim();
        HideAsk();
        if (text.Length > 0) act?.Invoke(text);
    }

    void AskBox_Key(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Ask_Ok(s, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideAsk(); e.Handled = true; }
    }

    void Rename(MediaItem it) => Ask("Rename", it.Name, name =>
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Toast("That name has characters Windows doesn't allow"); return; }
        if (Path.GetExtension(name).Length == 0) name += Path.GetExtension(it.Name);
        var dest = Path.Combine(it.Folder, name);
        if (string.Equals(dest, it.Path, StringComparison.Ordinal)) return;
        if (File.Exists(dest)) { Toast("A file with that name already exists"); return; }
        try { File.Move(it.Path, dest); }
        catch (Exception ex) { Toast("Couldn't rename: " + ex.Message); return; }
        _lib.Rename(it.Path, dest); _lib.SaveSettings(); _lib.SaveCache(); Refresh();
        Toast("Renamed");
    }, true);

    // ---------- scroll bubble: shows which group you are scrolling through ----------
    void ShowBubble()
    {
        try
        {
            _sv ??= Find<ScrollViewer>(List);
            if (_sv == null || _sv.ScrollableHeight < 1500 || _rowLabels.Length == 0) return;
            var panel = Find<VirtualizingPanel>(List);
            if (panel == null) return;
            int idx = -1;
            foreach (UIElement c in panel.Children)
            {
                if (c is not FrameworkElement fe) continue;
                double bottom = fe.TransformToAncestor(List).Transform(new Point(0, fe.ActualHeight)).Y;
                if (bottom <= 100) continue;
                int k = List.ItemContainerGenerator.IndexFromContainer(fe);
                if (k >= 0 && (idx < 0 || k < idx)) idx = k;
            }
            if (idx < 0 || idx >= _rowLabels.Length) return;
            BubbleText.Text = _rowLabels[idx];
            Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            _bubT.Stop(); _bubT.Start();
        }
        catch { }
    }

    // ---------- tools menu ----------
    void Tools_Click(object s, RoutedEventArgs e) { _toolsBtn = (UIElement)s; ToolsMenu(); }

    void OpenToolsPopup()
    {
        SortPop.PlacementTarget = _toolsBtn; SortPop.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        SortPop.IsOpen = true;
    }

    void MenuHead(string text) =>
        SortItems.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 0, 4) });

    void ToolsMenu()
    {
        SortItems.Children.Clear();
        MenuHead("Tools");
        SortItems.Children.Add(Opt("Find duplicate files", false, FindDupes, false));
        if (_dup != null) SortItems.Children.Add(Opt("Select extra copies", false, SelectExtras, false));
        SortItems.Children.Add(Opt("Compact tiles", _lib.S.Compact, () => _lib.S.Compact = !_lib.S.Compact));
        SortItems.Children.Add(Opt("Theme…", false, () => Dispatcher.BeginInvoke(() => ThemeMenu()), false));
        SortItems.Children.Add(Opt("Accent color…", false, () => Dispatcher.BeginInvoke(() => AccentMenu()), false));
        SortItems.Children.Add(Opt("Saved searches…", false, () => Dispatcher.BeginInvoke(() => SearchMenu()), false));
        SortItems.Children.Add(Opt("Clear thumbnail cache", false, () => Toast($"Freed {Thumbs.ClearDisk() / 1048576.0:0.#} MB"), false));
        SortItems.Children.Add(Opt("Keyboard shortcuts", false, () => Dispatcher.BeginInvoke(() => ToggleCheat()), false));
        OpenToolsPopup();
    }

    // ---------- duplicates ----------
    CancellationTokenSource? _dupCts;

    async void FindDupes()
    {
        _dupCts?.Cancel(); var cts = _dupCts = new CancellationTokenSource();
        Toast("Looking for duplicates…");
        List<Group> res;
        try { res = await Task.Run(() => _lib.FindDuplicates(cts.Token)); }
        catch (OperationCanceledException) { return; }
        if (cts != _dupCts) return;
        if (res.Count == 0) { Toast("No duplicates found"); return; }
        _dup = res; Title.Text = "Duplicates";
        Refresh();
        Toast($"{res.Count} sets of duplicates", "Select extras", SelectExtras);
    }

    void SelectExtras()
    {
        if (_dup == null) return;
        bool was = Picking;
        foreach (var g in _dup) foreach (var it in g.Items.Skip(1)) SetSel(it, true); // oldest copy stays unselected
        UpdatePick(was);
    }

    // ---------- theme ----------
    static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Ink"] = ("#FFFFFFFF", "#FF15161B"), ["Dim"] = ("#ABFFFFFF", "#B3101218"), ["Surface"] = ("#16FFFFFF", "#11000000"), ["Hairline"] = ("#1FFFFFFF", "#1A000000"),
        ["W12"] = ("#12FFFFFF", "#0F000000"), ["W14"] = ("#14FFFFFF", "#12000000"), ["W26"] = ("#26FFFFFF", "#1F000000"),
        ["W2E"] = ("#2EFFFFFF", "#24000000"), ["W33"] = ("#33FFFFFF", "#2B000000"), ["W66"] = ("#66FFFFFF", "#66000000"),
        ["Panel"] = ("#E6181921", "#F5FFFFFF"), ["PopBg"] = ("#F01C1C24", "#FAFFFFFF"), ["SideBg"] = ("#59000000", "#99FFFFFF"),
    };

    bool Light => (App.ThemeOverride ?? _lib.S.Theme) switch { "light" => true, "auto" => WindowsUsesLight(), _ => false };

    static bool WindowsUsesLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }

    void ApplyTheme()
    {
        bool light = Light;
        var res = Application.Current.Resources;
        foreach (var (k, (d, l)) in Palette) res[k] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? l : d));
        res["FadeC0"] = (Color)ColorConverter.ConvertFromString(light ? "#00F4F5F9" : "#0008090D");
        res["FadeC1"] = (Color)ColorConverter.ConvertFromString(light ? "#E6F4F5F9" : "#E608090D");
        Glass.Apply(this, !light);
        for (int k = 0; k < _navText.Count; k++) _navText[k].Foreground = k / 2 == _nav ? (Brush)FindResource("Ink") : (Brush)FindResource("Dim");
        AlbumsBtn.Background = _albums ? (Brush)FindResource("Accent") : (Brush)FindResource("W14");
        AlbumsBtn.Foreground = _albums ? (Brush)FindResource("OnAccent") : (Brush)FindResource("Ink");
        ApplyBg();
    }

    void ThemeMenu()
    {
        SortItems.Children.Clear();
        MenuHead("Theme");
        foreach (var (key, label) in new[] { ("dark", "Dark"), ("light", "Light"), ("auto", "Match Windows") })
            SortItems.Children.Add(Opt(label, _lib.S.Theme == key, () => { _lib.S.Theme = key; ApplyTheme(); }, false));
        OpenToolsPopup();
    }

    // ---------- accent color ----------
    static readonly (string Name, string Hex)[] Accents =
    {
        ("Amber", "#F5B544"), ("Sky", "#5EB8FF"), ("Mint", "#4ADE9B"), ("Rose", "#FF6B8B"), ("Violet", "#A78BFA"),
    };

    void ApplyAccent()
    {
        if (_lib.S.Accent.Length == 0) return;
        try { ((SolidColorBrush)FindResource("Accent")).Color = (Color)ColorConverter.ConvertFromString(_lib.S.Accent); } catch { }
    }

    void SetAccent(string hex) { _lib.S.Accent = hex; ApplyAccent(); }

    void AccentMenu()
    {
        SortItems.Children.Clear();
        MenuHead("Accent color");
        foreach (var (name, hex) in Accents)
            SortItems.Children.Add(Opt(name, string.Equals(_lib.S.Accent, hex, StringComparison.OrdinalIgnoreCase) || (_lib.S.Accent.Length == 0 && name == "Amber"), () => SetAccent(name == "Amber" ? "" : hex), false));
        SortItems.Children.Add(Opt("From background image", false, AccentFromBg, false));
        OpenToolsPopup();
    }

    void AccentFromBg()
    {
        if (BgImg.Source is not BitmapSource bs) { Toast("Set a background image first"); return; }
        try
        {
            var small = new FormatConvertedBitmap(new TransformedBitmap(bs, new ScaleTransform(16.0 / bs.PixelWidth, 16.0 / bs.PixelHeight)), PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4]; small.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0; int n = w * h;
            for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
            r /= n * 255; g /= n * 255; b /= n * 255;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn, hue = 0;
            if (d > 0) hue = mx == r ? ((g - b) / d + 6) % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
            double sat = Math.Clamp(mx == 0 ? 0 : d / mx, 0.45, 0.85), val = 0.95;
            double c = val * sat, x = c * (1 - Math.Abs(hue % 2 - 1)), m = val - c;
            (double R, double G, double B) = (int)hue switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
            SetAccent($"#{(int)((R + m) * 255):X2}{(int)((G + m) * 255):X2}{(int)((B + m) * 255):X2}");
        }
        catch { Toast("Couldn't read the background colors"); }
    }

    // ---------- saved searches ----------
    void SearchMenu()
    {
        SortItems.Children.Clear();
        MenuHead("Saved searches");
        foreach (var q in _lib.S.Searches.ToList())
            SortItems.Children.Add(Opt(q, false, () => Search.Text = q, false));
        string cur = Search.Text.Trim();
        if (cur.Length > 0 && !_lib.S.Searches.Contains(cur)) SortItems.Children.Add(Opt($"Save “{cur}”", false, () => _lib.S.Searches.Add(cur), false));
        if (_lib.S.Searches.Count > 0) SortItems.Children.Add(Opt("Clear saved searches", false, () => _lib.S.Searches.Clear(), false));
        if (_lib.S.Searches.Count == 0 && cur.Length == 0) SortItems.Children.Add(new TextBlock { Text = "Type a search, then save it here", FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 4, 12, 8) });
        OpenToolsPopup();
    }

    // ---------- cheat sheet + drag and drop ----------
    void ToggleCheat()
    {
        if (Cheat.Visibility == Visibility.Visible) { HideCheat(); return; }
        Cheat.Visibility = Visibility.Visible;
        Cheat.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    void HideCheat()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        a.Completed += (_, _) => Cheat.Visibility = Visibility.Collapsed;
        Cheat.BeginAnimation(OpacityProperty, a);
    }

    void Cheat_Close(object s, MouseButtonEventArgs e) => HideCheat();

    void Win_Over(object s, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

    void Win_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        int added = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                if (!_lib.S.Folders.Contains(p, StringComparer.OrdinalIgnoreCase)) { _lib.S.Folders.Add(p); added++; }
            }
            else if (File.Exists(p) && MediaItem.From(p, new FileInfo(p).Length, File.GetLastWriteTime(p)) is { } m && paths.Length == 1)
                Viewer.Open(new List<MediaItem> { m }, 0);
        }
        if (added == 0) return;
        _lib.SaveSettings(); StartScan();
        Toast(added == 1 ? "Folder added" : $"{added} folders added");
    }

    // ---------- exclude-folder words ----------
    async void Ex_Click(object s, RoutedEventArgs e)
    {
        ExBox.Text = string.Join(", ", _lib.S.ExcludeWords);
        ExCard.Visibility = Visibility.Visible;
        ExCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ExBox.Focus(); ExBox.CaretIndex = ExBox.Text.Length;
        ExSug.Children.Clear();
        var sug = await Task.Run(() => _lib.SuggestExcludes());
        foreach (var w in sug.Where(w => !_lib.S.ExcludeWords.Contains(w, StringComparer.OrdinalIgnoreCase)))
        {
            var b = new Button { Style = (Style)FindResource("Chip"), Content = w, Margin = new Thickness(0, 6, 6, 0), Height = 30 };
            b.Click += (_, _) => { ExBox.Text = ExBox.Text.Trim().TrimEnd(',') + (ExBox.Text.Trim().Length > 0 ? ", " : "") + w; ExSug.Children.Remove(b); };
            ExSug.Children.Add(b);
        }
        if (ExSug.Children.Count == 0) ExSug.Children.Add(new TextBlock { Text = "Nothing obvious yet", Foreground = (Brush)FindResource("Dim"), FontSize = 12, Margin = new Thickness(2, 6, 0, 0) });
    }

    void HideEx()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => ExCard.Visibility = Visibility.Collapsed;
        ExCard.BeginAnimation(OpacityProperty, a);
    }

    void ExCancel_Click(object s, RoutedEventArgs e) => HideEx();

    void ExApply_Click(object s, RoutedEventArgs e)
    {
        _lib.S.ExcludeWords = ExBox.Text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _lib.SaveSettings(); HideEx(); Refresh();
    }

    void ExBox_Key(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ExApply_Click(s, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideEx(); e.Handled = true; }
    }

    void UpdateSmart() => SmartBtn.Content = "Hide dummy media: " + (_lib.S.SmartHide ? "On" : "Off");

    void Smart_Click(object s, RoutedEventArgs e)
    {
        _lib.S.SmartHide = !_lib.S.SmartHide;
        _lib.SaveSettings(); UpdateSmart(); Refresh();
    }

    // ---------- background image ----------
    void ApplyBg()
    {
        BgImg.Source = null;
        var p = _lib.S.Background;
        if (p.Length > 0 && File.Exists(p))
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(p); bi.DecodePixelWidth = 2560; bi.EndInit(); bi.Freeze();
                BgImg.Source = bi;
            }
            catch { }
        double a = BgImg.Source == null ? 0.8 : Math.Clamp(_lib.S.BgDim, 0.3, 0.95);
        BgTint.Background = new SolidColorBrush(Light ? Color.FromArgb((byte)(255 * a), 244, 245, 249) : Color.FromArgb((byte)(255 * a), 8, 9, 13));
    }

    void Bg_Click(object s, RoutedEventArgs e)
    {
        SortItems.Children.Clear();
        SortItems.Children.Add(new TextBlock { Text = "Background", FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 0, 4) });
        SortItems.Children.Add(Opt("Choose image…", false, () =>
        {
            var d = new OpenFileDialog { Title = "Choose a background", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.heic|All files|*.*" };
            if (d.ShowDialog(this) == true) { _lib.S.Background = d.FileName; ApplyBg(); }
        }, false));
        SortItems.Children.Add(Opt("Darker", false, () => { _lib.S.BgDim = Math.Min(0.95, _lib.S.BgDim + 0.08); ApplyBg(); }, false));
        SortItems.Children.Add(Opt("Lighter", false, () => { _lib.S.BgDim = Math.Max(0.3, _lib.S.BgDim - 0.08); ApplyBg(); }, false));
        SortItems.Children.Add(Opt("Remove background", false, () => { _lib.S.Background = ""; ApplyBg(); }, false));
        SortPop.PlacementTarget = (UIElement)s; SortPop.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        SortPop.IsOpen = true;
    }

    static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } r) return r;
        }
        return null;
    }

    // ---------- scanning ----------
    async void StartScan()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _scanning = true; ShowScan(true); _scanTick.Start();
        try { await _lib.ScanAsync(_lib.Roots().ToList(), () => { }, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts != _cts) return;
        await _lib.ProbeAsync(cts.Token);
        if (cts != _cts) return;
        _scanTick.Stop(); _scanning = false; ShowScan(false);
        _lib.SaveCache();
        Refresh();
        _lib.Watch();
    }

    void ShowScan(bool on)
    {
        ScanBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { ScanX.BeginAnimation(TranslateTransform.XProperty, null); return; }
        ScanX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-160, Math.Max(400, ActualWidth), TimeSpan.FromMilliseconds(1300))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    // ---------- scrolling: inertia wheel, collapsing header ----------
    void List_Wheel(object s, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            _lib.S.Tile = Math.Clamp(_lib.S.Tile + (e.Delta > 0 ? 20 : -20), 110, 320);
            _rel.Stop(); _rel.Start(); e.Handled = true; return;
        }
        _sv ??= Find<ScrollViewer>(List);
        if (_sv == null) return;
        if (!_anim) _target = _sv.VerticalOffset;
        double want = _target - e.Delta * 1.1;
        if (want < 0 && _sv.VerticalOffset <= 0.5) { Bounce(+1); e.Handled = true; return; }
        if (want > _sv.ScrollableHeight && _sv.VerticalOffset >= _sv.ScrollableHeight - 0.5) { Bounce(-1); e.Handled = true; return; }
        _target = Math.Clamp(_target - e.Delta * 1.1, 0, _sv.ScrollableHeight);
        if (!_anim) { _anim = true; CompositionTarget.Rendering += Step; }
        e.Handled = true;
    }

    void Step(object? s, EventArgs e)
    {
        var sv = _sv!;
        double d = _target - sv.VerticalOffset;
        if (Math.Abs(d) < 0.5) { sv.ScrollToVerticalOffset(_target); StopStep(); return; }
        sv.ScrollToVerticalOffset(sv.VerticalOffset + d * 0.2);
    }

    void StopStep() { if (_anim) { CompositionTarget.Rendering -= Step; _anim = false; } }

    void List_Down(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Find<System.Windows.Controls.Primitives.Thumb>(d) != null) StopStep();
        if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb) StopStep();
    }

    void List_Size(object s, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        _rel.Stop(); _rel.Start();
    }

    // ---------- tiles ----------
    async void Tile_Loaded(object s, RoutedEventArgs e)
    {
        var b = (Border)s;
        if (b.DataContext is not MediaItem it) return;
        it.Realized = true;
        int idx = 0;
        for (DependencyObject? p = b; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ItemsControl ic) { idx = Array.IndexOf((ic.DataContext as TileRow)?.Tiles ?? Array.Empty<MediaItem>(), it); break; }
        bool first = !it.Animated; it.Animated = true;
        var sc = new ScaleTransform(first ? 0.94 : 1, first ? 0.94 : 1); // template-declared transforms are frozen: swap in an animatable one
        var tt = new TranslateTransform(0, first ? 18 : 0);
        b.RenderTransform = new TransformGroup { Children = { sc, tt } };
        if (first)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(460))
                { BeginTime = TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, idx), 12) * 22), EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut } });
            var begin = TimeSpan.FromMilliseconds(Math.Min(Math.Max(0, idx), 12) * 22);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            b.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
            var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else b.Opacity = 1;
        if (it.Thumb == null)
        {
            var t = await Thumbs.Get(it.Path, 220, () => it.Realized);
            if (it.Realized) it.Thumb = t;
        }
    }

    void Tile_Unloaded(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is MediaItem it) { it.Realized = false; it.Thumb = null; }
    }

    void Tile_Enter(object s, MouseEventArgs e)
    {
        var b = (Border)s; Scale(b, 1.025, 220);
        if (b.DataContext is MediaItem it && ByTag(b, "Chk") is { } c) { it.CheckOpacity = 1; FadeCheck(c, 0, 1); }
    }

    void Tile_Leave(object s, MouseEventArgs e)
    {
        var b = (Border)s; Scale(b, 1, 220);
        if (b.DataContext is MediaItem it && ByTag(b, "Chk") is { } c) { it.CheckOpacity = Picking ? 1 : 0; if (!Picking) FadeCheck(c, 1, 0); }
    }

    // animation ends where the binding already is, so it simply releases (FillBehavior.Stop)
    static void FadeCheck(UIElement c, double from, double to) =>
        c.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });

    static FrameworkElement? ByTag(DependencyObject d, string tag)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement fe && fe.Tag as string == tag) return fe;
            if (ByTag(c, tag) is { } r) return r;
        }
        return null;
    }

    // ---------- multi-select ----------
    void SetSel(MediaItem it, bool on) { if (on) _sel.Add(it); else _sel.Remove(it); it.Selected = on; }

    void UpdatePick(bool was)
    {
        bool now = Picking;
        foreach (var i in _flat) i.CheckOpacity = now ? 1 : 0;
        SelCount.Text = $"{_sel.Count} selected";
        if (now == was) return;
        var show = now ? SelBar : Nav; var hide = now ? Nav : SelBar;
        hide.Visibility = Visibility.Collapsed; show.Visibility = Visibility.Visible;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        if (now) SelT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(34, 0, TimeSpan.FromMilliseconds(520))
            { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
    }

    void Toggle(MediaItem it, bool range)
    {
        bool was = Picking;
        int a = _anchor == null ? -1 : _flat.IndexOf(_anchor), b = _flat.IndexOf(it);
        if (range && a >= 0 && b >= 0) { for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) SetSel(_flat[i], true); }
        else SetSel(it, !_sel.Contains(it));
        _anchor = it;
        UpdatePick(was);
    }

    void ClearSel()
    {
        bool was = Picking;
        foreach (var i in _sel) i.Selected = false;
        _sel.Clear(); _anchor = null;
        UpdatePick(was);
    }

    void Tile_Press(object s, MouseButtonEventArgs e)
    {
        _held = false;
        if (((FrameworkElement)s).DataContext is not MediaItem it) return;
        _holdItem = it; _holdAt = e.GetPosition(this); _hold.Stop(); _hold.Start();
    }

    void Tile_Check(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is MediaItem it) Toggle(it, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    void SelClear_Click(object s, RoutedEventArgs e) => ClearSel();

    void SelAll_Click(object s, RoutedEventArgs e)
    {
        bool was = Picking;
        foreach (var i in _flat) SetSel(i, true);
        UpdatePick(was);
    }

    void SelFav_Click(object s, RoutedEventArgs e)
    {
        bool on = _sel.Any(i => !i.Fav);
        foreach (var i in _sel) { i.Fav = on; if (on) _lib.S.Favs.Add(i.Path); else _lib.S.Favs.Remove(i.Path); }
        _lib.SaveSettings();
        if (_nav == 5) { ClearSel(); Refresh(); }
    }

    void SelHide_Click(object s, RoutedEventArgs e)
    {
        foreach (var i in _sel) _lib.S.Hidden.Add(i.Path);
        _lib.SaveSettings(); ClearSel(); Refresh();
    }

    void SelDelete_Click(object s, RoutedEventArgs e)
    {
        var items = _sel.ToList();
        if (MessageBox.Show(this, $"Move {items.Count} item{(items.Count == 1 ? "" : "s")} to the Recycle Bin?", "Galary", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int failed = 0; var done = new List<string>();
        foreach (var it in items)
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); _lib.Remove(it.Path); done.Add(it.Path); }
            catch { failed++; }
        }
        _lib.SaveCache(); ClearSel(); Refresh(); AfterDelete(done);
        if (failed > 0) MessageBox.Show(this, $"{failed} file{(failed == 1 ? "" : "s")} couldn't be deleted (in use or no permission).", "Galary", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static void Scale(Border b, double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (b.RenderTransform is not TransformGroup { Children: [ScaleTransform sc, ..] }) return;
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Tile_Click(object s, MouseButtonEventArgs e)
    {
        if (_held) { _held = false; return; }
        if (((FrameworkElement)s).DataContext is not MediaItem it) return;
        var m = Keyboard.Modifiers;
        if (Picking || m.HasFlag(ModifierKeys.Control) || m.HasFlag(ModifierKeys.Shift)) Toggle(it, m.HasFlag(ModifierKeys.Shift));
        else Viewer.Open(_flat, _flat.IndexOf(it));
    }

    void Tile_Fav(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is MediaItem it)
        {
            ToggleFav(it);
            if (it.Fav && ((FrameworkElement)s).Parent is Grid g)
                foreach (var tb in g.Children.OfType<TextBlock>().Where(t => t.Text == "\uEB52")) Pop(tb);
        }
        e.Handled = true;
    }

    static void Pop(FrameworkElement el)
    {
        var sc = new ScaleTransform(0.3, 0.3);
        el.RenderTransformOrigin = new Point(0.5, 0.5); el.RenderTransform = sc;
        var a = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new BackEase { Amplitude = 1.8, EasingMode = EasingMode.EaseOut } };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // rubber-band at the ends of the list
    void Bounce(int dir)
    {
        if ((DateTime.UtcNow - _lastBounce).TotalMilliseconds < 500) return;
        _lastBounce = DateTime.UtcNow;
        _listShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dir * 18, 0, TimeSpan.FromMilliseconds(700))
            { EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 5 } });
    }

    static void Launch(string file, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { }
    }

    void Tile_Down(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)s).DataContext is MediaItem it) { Launch(it.Path); e.Handled = true; }
    }

    // ---------- right-click menu ----------
    void Tile_Menu(object s, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is not MediaItem it) return;
        SortItems.Children.Clear();
        SortItems.Children.Add(new TextBlock { Text = it.Name, FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 12, 4), TextTrimming = TextTrimming.CharacterEllipsis });
        SortItems.Children.Add(Opt("Open", false, () => Viewer.Open(_flat, _flat.IndexOf(it)), false));
        SortItems.Children.Add(Opt("Open with default app", false, () => Launch(it.Path), false));
        SortItems.Children.Add(Opt("Show in folder", false, () => Launch("explorer.exe", $"/select,\"{it.Path}\""), false));
        SortItems.Children.Add(Opt("Copy path", false, () => { try { Clipboard.SetText(it.Path); } catch { } }, false));
        SortItems.Children.Add(Opt("Rename…", false, () => Dispatcher.BeginInvoke(() => Rename(it)), false));
        if (it.Kind == Kind.Image) SortItems.Children.Add(Opt("Set as wallpaper", false, () => Toast(Tools.Wallpaper(it.Path) ? "Wallpaper set" : "Couldn't set the wallpaper"), false));
        SortItems.Children.Add(Opt("Favorite", it.Fav, () => ToggleFav(it), false));
        SortItems.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Surface"), Margin = new Thickness(8, 6, 8, 6) });
        SortItems.Children.Add(Opt("Hide this item", false, () => _lib.S.Hidden.Add(it.Path)));
        SortItems.Children.Add(Opt("Hide this folder", false, () => _lib.S.HiddenDirs.Add(it.Folder)));
        SortItems.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Surface"), Margin = new Thickness(8, 6, 8, 6) });
        SortItems.Children.Add(Opt("Move to Recycle Bin…", false, () => Delete(it)));
        SortPop.PlacementTarget = null; SortPop.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        SortPop.IsOpen = true;
        e.Handled = true;
    }

    void Delete(MediaItem it) => TryDelete(it);

    bool TryDelete(MediaItem it)
    {
        if (MessageBox.Show(this, $"Move \"{it.Name}\" to the Recycle Bin?", "Galary", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't delete", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        _lib.Remove(it.Path); _lib.SaveCache(); Refresh();
        AfterDelete(new List<string> { it.Path });
        return true;
    }

    void ToggleFav(MediaItem it)
    {
        it.Fav = !it.Fav;
        if (it.Fav) _lib.S.Favs.Add(it.Path); else _lib.S.Favs.Remove(it.Path);
        _lib.SaveSettings();
        if (_nav == 5 && !Viewer.IsOpen) Refresh();
    }

    // ---------- header actions ----------
    void Search_Changed(object s, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _deb.Stop(); _deb.Start();
    }

    void Albums_Click(object s, RoutedEventArgs e)
    {
        _albums = !_albums;
        AlbumsBtn.Background = _albums ? (Brush)FindResource("Accent") : (Brush)FindResource("W14");
        AlbumsBtn.Foreground = _albums ? (Brush)FindResource("OnAccent") : (Brush)FindResource("Ink");
        Refresh();
    }

    void AddFolder_Click(object s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Add a folder to scan" };
        if (d.ShowDialog(this) != true) return;
        if (!_lib.S.Folders.Contains(d.FolderName, StringComparer.OrdinalIgnoreCase)) _lib.S.Folders.Add(d.FolderName);
        _lib.SaveSettings();
        StartScan();
    }

    void Sort_Click(object s, RoutedEventArgs e)
    {
        SortItems.Children.Clear();
        SortItems.Children.Add(new TextBlock { Text = "SORT BY", FontSize = 11, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(12, 6, 0, 4) });
        foreach (var (key, label) in new[] { ("date", "Date"), ("name", "Name"), ("size", "Size"), ("type", "Type") })
            SortItems.Children.Add(Opt(label, _lib.S.Sort == key, () => _lib.S.Sort = key));
        SortItems.Children.Add(Opt("Newest / largest / Z–A first", _lib.S.Desc, () => _lib.S.Desc = !_lib.S.Desc));
        SortItems.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Surface"), Margin = new Thickness(8, 6, 8, 6) });
        SortItems.Children.Add(Opt("Hide duplicates", _lib.S.HideDupes, () => _lib.S.HideDupes = !_lib.S.HideDupes));
        SortItems.Children.Add(Opt("Hide dummy media (app, game, system)", _lib.S.SmartHide, () => { _lib.S.SmartHide = !_lib.S.SmartHide; UpdateSmart(); }));
        if (_lib.S.Hidden.Count + _lib.S.HiddenDirs.Count > 0)
            SortItems.Children.Add(Opt($"Unhide {_lib.S.Hidden.Count + _lib.S.HiddenDirs.Count} hidden", false, () => { _lib.S.Hidden.Clear(); _lib.S.HiddenDirs.Clear(); }));
        SortItems.Children.Add(Opt("Rescan folders", false, StartScan, false));
        SortItems.Children.Add(Opt("Reset added folders", false, () => { _lib.S.Folders.Clear(); StartScan(); }, false));
        SortPop.PlacementTarget = SortBtn; SortPop.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        SortPop.IsOpen = true;
    }

    Button Opt(string text, bool on, Action act, bool refresh = true)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = on ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, Foreground = (Brush)FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center });
        var t = new TextBlock { Text = text, FontSize = 14 }; Grid.SetColumn(t, 1); g.Children.Add(t);
        var b = new Button { Style = (Style)FindResource("Opt"), Content = g };
        b.Click += (_, _) => { act(); SortPop.IsOpen = false; _lib.SaveSettings(); if (refresh) Refresh(); };
        return b;
    }

    // ---------- network stream ----------
    void Url_Click(object s, RoutedEventArgs e)
    {
        UrlCard.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        UrlCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        UrlScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        UrlScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        UrlBox.Focus();
    }

    void HideUrl()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => UrlCard.Visibility = Visibility.Collapsed;
        UrlCard.BeginAnimation(OpacityProperty, a);
        UrlScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(160)));
        UrlScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(160)));
    }

    void UrlCancel_Click(object s, RoutedEventArgs e) => HideUrl();

    void UrlPlay_Click(object s, RoutedEventArgs e)
    {
        var u = UrlBox.Text.Trim();
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.IsFile) { UrlBox.Foreground = Brushes.IndianRed; return; }
        UrlBox.Foreground = (Brush)FindResource("Ink");
        HideUrl();
        Viewer.Open(new List<MediaItem> { MediaItem.Url(u) }, 0);
    }

    void UrlBox_Key(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) UrlPlay_Click(s, e);
        else if (e.Key == Key.Escape) HideUrl();
    }

    // ---------- keyboard ----------
    void Win_KeyDown(object s, KeyEventArgs e)
    {
        if (Cheat.Visibility == Visibility.Visible && e.Key == Key.Escape) { HideCheat(); e.Handled = true; return; }
        bool typing = Search.IsKeyboardFocused || ExBox.IsKeyboardFocused || AskBox.IsKeyboardFocused || UrlBox.IsKeyboardFocused;
        if (!typing && (e.Key == Key.F1 || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Shift))) { ToggleCheat(); e.Handled = true; return; }
        if (!typing && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _lastDel.Count > 0) { UndoDelete(); e.Handled = true; return; }
        if (Viewer.IsOpen) { if (Viewer.HandleKey(e)) e.Handled = true; return; }
if (e.Key == Key.Escape && Picking && !Search.IsKeyboardFocused) { ClearSel(); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && !Search.IsKeyboardFocused) { SelAll_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Search.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape && Search.IsKeyboardFocused) { Search.Clear(); List.Focus(); e.Handled = true; }
    }
}

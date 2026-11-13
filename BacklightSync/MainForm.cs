using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BacklightSync;

/// <summary>The settings window. Every control applies instantly; settings are saved a moment after the last change.</summary>
public sealed class MainForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly Settings _s;
    readonly Engine _engine;
    readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 600 };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 400 };
    readonly StatusPill _pill = new() { Dock = DockStyle.Right, Width = 330 };
    readonly ModeCard _cSync = new() { Caption = "Movie sync", Sub = "follows your screen", Icon = CardIcon.Screen, Accent = Theme.Sync };
    readonly ModeCard _cNight = new() { Caption = "Night light", Sub = "soft golden glow", Icon = CardIcon.Moon, Accent = Theme.Night };
    readonly ModeCard _cOff = new() { Caption = "Off", Sub = "lights out", Icon = CardIcon.Power, Accent = Theme.Sub };
    const int TabSync = 0, TabBars = 1, TabNight = 2;
    readonly TabBar _tabs = new() { Items = new[] { "Movie sync", "Black bars", "Night light", "Strip", "System" } };
    readonly Panel[] _pages = new Panel[5];
    readonly Label _barsInfo = new() { AutoSize = true, ForeColor = Theme.Sub, BackColor = Theme.Bg, Font = Theme.F(9f), Margin = new Padding(0, 0, 0, 6) };
    readonly List<Action> _loaders = new();
    readonly List<FlatSlider> _syncSliders = new();
    NumericUpDown _nLeft = null!, _nTop = null!, _nRight = null!;
    bool _loading;
    public event Action? ModeChangedByUser;

    public MainForm(Settings s, Engine engine)
    {
        _s = s; _engine = engine;
        Text = "Backlight Sync";
        Icon = TrayApp.MakeIcon();
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.F(9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(560, 780);
        DoubleBuffered = true;

        // ---------- header
        var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Bg };
        var title = new Label { Text = "Backlight Sync", Dock = DockStyle.Fill, Font = Theme.Semi(17f), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft };
        header.Controls.Add(title);
        header.Controls.Add(_pill);

        // ---------- mode cards
        var cards = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 3, BackColor = Theme.Bg, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        foreach (var (c, i) in new[] { (_cSync, 0), (_cNight, 1), (_cOff, 2) })
        {
            c.Dock = DockStyle.Fill; c.Margin = new Padding(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            cards.Controls.Add(c, i, 0);
        }
        _cSync.Click += (_, _) => UserMode(LightMode.Sync);
        _cNight.Click += (_, _) => UserMode(LightMode.Night);
        _cOff.Click += (_, _) => UserMode(LightMode.Off);

        // ---------- live preview
        var preview = new StripPreview(() => _s, () => _engine.Preview) { Dock = DockStyle.Top, Height = 210 };

        // ---------- tabs + pages
        _tabs.Dock = DockStyle.Top;
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(2, 14, 2, 0) };
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i] = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = Theme.Bg, Visible = i == 0 };
            body.Controls.Add(_pages[i]);
        }
        BuildSyncPage(_pages[0]);
        BuildBarsPage(_pages[1]);
        BuildNightPage(_pages[2]);
        BuildStripPage(_pages[3]);
        BuildSystemPage(_pages[4]);
        _tabs.SelectedChanged += (_, _) => ShowPage();

        // docking order: fill first (added first = lowest), then the Top ones from bottom to top
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 8, 22, 14), BackColor = Theme.Bg };
        root.Controls.Add(body);
        root.Controls.Add(_tabs);
        root.Controls.Add(Spacer(10));
        root.Controls.Add(preview);
        root.Controls.Add(Spacer(10));
        root.Controls.Add(cards);
        root.Controls.Add(header);
        Controls.Add(root);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _s.Save(); };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        LoadAll();
        ShowMode();
        _tabs.Selected = _s.Mode == LightMode.Night ? TabNight : TabSync;
        if (int.TryParse(Environment.GetEnvironmentVariable("BACKLIGHT_TAB"), out int startTab)) _tabs.Selected = Math.Clamp(startTab, 0, 4);
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            _s.Save();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;   // dark title bar (Windows 10 20H1+ / 11)
        DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
    }

    static Panel Spacer(int h) => new() { Dock = DockStyle.Top, Height = h, BackColor = Theme.Bg };

    // ------------------------------------------------------------------ pages

    void BuildSyncPage(Panel page)
    {
        var col = NewColumn(page);
        AddSlider(col, "Brightness", "overall light level", 0, 100, () => _s.Brightness, v => _s.Brightness = v, v => v + "%", Theme.Sync);
        AddSlider(col, "Colour boost", "make colours richer", 50, 250, () => _s.Saturation, v => _s.Saturation = v, v => v + "%", Theme.Sync);
        AddSlider(col, "Smoothness", "higher = calmer, lower = snappier", 0, 100, () => _s.Smoothing, v => _s.Smoothing = v, v => v == 0 ? "instant" : v + "%", Theme.Sync);
        AddSlider(col, "Depth of colour", "how far in from the edge to look", 4, 35, () => _s.SampleDepth, v => _s.SampleDepth = v, v => v + "%", Theme.Sync);
        AddSlider(col, "Contrast", "higher = darker shadows", 100, 260, () => _s.Gamma, v => _s.Gamma = v, v => (v / 100.0).ToString("0.0"), Theme.Sync);
    }

    void BuildBarsPage(Panel page)
    {
        var col = NewColumn(page);
        AddToggle(col, "Remove black bars", "ignores the black bars on movies automatically", () => _s.AutoBlackBars, v => _s.AutoBlackBars = v, Theme.Sync);
        AddSlider(col, "What counts as black", "raise it if bars aren't being detected", 2, 50, () => _s.BarThreshold, v => _s.BarThreshold = v, v => v.ToString(), Theme.Sync);
        col.Controls.Add(_barsInfo);
        col.Controls.Add(Note("Works for wide cinema bars, 4:3 side bars and both together. Subtitles inside the bars and dark scenes won't confuse it."));
    }

    void BuildNightPage(Panel page)
    {
        var col = NewColumn(page);
        AddSlider(col, "Brightness", "keep it low for the evening", 1, 100, () => _s.NightBrightness, v => _s.NightBrightness = v, v => v + "%", Theme.Night);
        AddSlider(col, "Warmth", "left = deeper gold, right = softer white", 1500, 4000, () => _s.NightKelvin, v => _s.NightKelvin = v, v => v + " K", Theme.Night);
        col.Controls.Add(Note("Tip: press  Ctrl + Alt + B  anywhere to jump between Movie sync, Night light and Off."));
    }

    void BuildStripPage(Panel page)
    {
        var col = NewColumn(page);
        col.Controls.Add(Heading("How many LEDs on each edge"));
        var counts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 8) };
        _nLeft = Num(counts, "Left", () => _s.LeftLeds, v => _s.LeftLeds = v);
        _nTop = Num(counts, "Top", () => _s.TopLeds, v => _s.TopLeds = v);
        _nRight = Num(counts, "Right", () => _s.RightLeds, v => _s.RightLeds = v);
        col.Controls.Add(counts);
        AddToggle(col, "Strip runs clockwise", "on = first LED is bottom-left, off = bottom-right", () => _s.Clockwise, v => _s.Clockwise = v, Theme.Neutral);
        var test = new FlatButton { Text = "Show test pattern (8 s)", Width = 250, Primary = true, Accent = Theme.Neutral, Margin = new Padding(0, 6, 0, 6) };
        test.Click += (_, _) => _engine.ShowTestPattern(8);
        col.Controls.Add(test);
        col.Controls.Add(Note("Test pattern: left edge red, top green, right blue, and a white LED at the bottom of each side. " +
                              "If a colour lands on the wrong edge, flip the switch above."));
    }

    void BuildSystemPage(Panel page)
    {
        var col = NewColumn(page);
        AddToggle(col, "Start with Windows", "runs quietly in the tray after you sign in", () => _s.StartWithWindows, v => { _s.StartWithWindows = v; TrayApp.SetAutostart(v); }, Theme.Neutral);
        var folder = new FlatButton { Text = "Open settings folder", Width = 250, Margin = new Padding(0, 10, 0, 6) };
        folder.Click += (_, _) => { try { Process.Start("explorer.exe", Settings.Dir); } catch { } };
        col.Controls.Add(folder);
        col.Controls.Add(Note("Closing this window keeps the app running in the tray (near the clock). Right-click the tray icon and choose Exit to quit."));
    }

    // ------------------------------------------------------------------ builders

    FlowLayoutPanel NewColumn(Panel page)
    {
        var col = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false,
            BackColor = Theme.Bg, Padding = new Padding(2, 0, 8, 0),
        };
        page.Controls.Add(col);
        // keep every row as wide as the column
        col.Resize += (_, _) => { foreach (Control c in col.Controls) if (c.Tag as string == "row") c.Width = Math.Max(100, col.ClientSize.Width - col.Padding.Horizontal - 4); };
        col.ControlAdded += (_, e) => { if (e.Control!.Tag as string == "row") e.Control.Width = Math.Max(100, col.ClientSize.Width - col.Padding.Horizontal - 4); };
        return col;
    }

    int Sc(int v) => (int)(v * DeviceDpi / 96f);

    void AddSlider(FlowLayoutPanel col, string label, string hint, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, Color accent)
    {
        var row = new Panel { Tag = "row", Height = Sc(62), Width = Sc(480), BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 2) };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(0, Sc(2)), Font = Theme.Semi(10f), ForeColor = Theme.Text, BackColor = Theme.Bg };
        var sub = new Label { Text = hint, AutoSize = true, Location = new Point(0, Sc(21)), Font = Theme.F(8.5f), ForeColor = Theme.Sub, BackColor = Theme.Bg };
        var val = new Label { AutoSize = false, Width = Sc(110), Height = Sc(22), TextAlign = ContentAlignment.MiddleRight, Font = Theme.Semi(10f), ForeColor = accent, BackColor = Theme.Bg, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var sl = new FlatSlider { Minimum = min, Maximum = max, Accent = accent, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Height = Sc(28) };
        row.Controls.AddRange(new Control[] { lbl, sub, val, sl });
        row.Resize += (_, _) =>
        {
            val.Location = new Point(row.Width - val.Width, Sc(1));
            sl.SetBounds(0, row.Height - Sc(30), row.Width, Sc(28));
        };
        row.Width = Sc(480);
        sl.ValueChanged += (_, _) =>
        {
            val.Text = fmt(sl.Value);
            if (_loading) return;
            set(sl.Value); Changed();
        };
        _loaders.Add(() => { sl.SetSilently(get()); val.Text = fmt(sl.Value); });
        if (accent == Theme.Sync) _syncSliders.Add(sl);
        col.Controls.Add(row);
    }

    void AddToggle(FlowLayoutPanel col, string label, string hint, Func<bool> get, Action<bool> set, Color accent)
    {
        var row = new Panel { Tag = "row", Height = Sc(58), Width = Sc(480), BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 4) };
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(0, Sc(6)), Font = Theme.Semi(10f), ForeColor = Theme.Text, BackColor = Theme.Bg };
        var sub = new Label { Text = hint, AutoSize = true, Location = new Point(0, Sc(26)), Font = Theme.F(8.5f), ForeColor = Theme.Sub, BackColor = Theme.Bg };
        var sw = new ToggleSwitch { Accent = accent, Size = new Size(Sc(50), Sc(30)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        row.Controls.AddRange(new Control[] { lbl, sub, sw });
        row.Resize += (_, _) => sw.Location = new Point(row.Width - sw.Width - Sc(4), Sc(10));
        sw.CheckedChanged += (_, _) => { if (_loading) return; set(sw.Checked); Changed(); };
        _loaders.Add(() => sw.SetSilently(get()));
        col.Controls.Add(row);
    }

    Label Heading(string t) => new() { Text = t, AutoSize = true, Font = Theme.Semi(10f), ForeColor = Theme.Text, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 0) };

    Label Note(string t) => new()
    {
        Text = t, AutoSize = true, MaximumSize = new Size(Sc(470), 0), Font = Theme.F(9f), ForeColor = Theme.Sub,
        BackColor = Theme.Bg, Margin = new Padding(0, 10, 0, 6),
    };

    NumericUpDown Num(FlowLayoutPanel parent, string label, Func<int> get, Action<int> set)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Sub, BackColor = Theme.Bg, Margin = new Padding(0, 6, 4, 0) });
        var n = new NumericUpDown
        {
            Minimum = 0, Maximum = 120, Width = Sc(62), Margin = new Padding(0, 2, 22, 2),
            BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
        };
        n.ValueChanged += (_, _) => { if (_loading) return; set((int)n.Value); Changed(); };
        _loaders.Add(() => n.Value = Math.Clamp(get(), 0, 120));
        parent.Controls.Add(n);
        return n;
    }

    // ------------------------------------------------------------------ behaviour

    void LoadAll()
    {
        bool was = _loading; _loading = true;
        foreach (var l in _loaders) l();
        _loading = was;
    }

    void ShowPage()
    {
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _tabs.Selected;
        _tabs.Accent = _tabs.Selected <= TabBars ? Theme.Sync : _tabs.Selected == TabNight ? Theme.Night : Theme.Neutral;
    }

    void UserMode(LightMode m)
    {
        _s.Mode = m; Changed(); ShowMode();
        if (m == LightMode.Sync) _tabs.Selected = TabSync; else if (m == LightMode.Night) _tabs.Selected = TabNight;
        ModeChangedByUser?.Invoke();
    }

    public void ShowMode()
    {
        _cSync.Selected = _s.Mode == LightMode.Sync;
        _cNight.Selected = _s.Mode == LightMode.Night;
        _cOff.Selected = _s.Mode == LightMode.Off;
    }

    void Changed()
    {
        _s.Normalize();
        _saveTimer.Stop(); _saveTimer.Start();
    }

    void RefreshStatus()
    {
        if (!Visible) return;
        ShowMode();
        var m = _s.Mode;
        string text;
        Color dot;
        if (!_engine.Connected) { text = _engine.Status; dot = Theme.Bad; }
        else if (m == LightMode.Off) { text = "Lights off"; dot = Theme.Sub; }
        else if (m == LightMode.Night) { text = "Night light on"; dot = Theme.Night; }
        else
        {
            text = _engine.Status.StartsWith("Syncing") ? $"Syncing · {_engine.Fps:0} fps" : _engine.Status;
            dot = _engine.Status.StartsWith("Syncing") ? Theme.Good : Theme.Night;
        }
        _pill.Text = text; _pill.Dot = dot; _pill.Invalidate();

        var b = _engine.Bars;
        _barsInfo.Text = !_s.AutoBlackBars ? "Black bar removal is off."
            : m != LightMode.Sync ? "Black bars are detected while Movie sync is on."
            : b.Any ? $"Black bars found — top {b.Top}, bottom {b.Bottom}, left {b.Left}, right {b.Right}. The lights follow the real picture."
            : "No black bars right now — using the whole screen.";

        // keep the numbers in step if Normalize() adjusted them
        bool was = _loading; _loading = true;
        if (_nLeft.Value != _s.LeftLeds) _nLeft.Value = _s.LeftLeds;
        if (_nTop.Value != _s.TopLeds) _nTop.Value = _s.TopLeds;
        if (_nRight.Value != _s.RightLeds) _nRight.Value = _s.RightLeds;
        _loading = was;
    }

    public void ShowAndFocus()
    {
        LoadAll();
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        RefreshStatus();
    }
}

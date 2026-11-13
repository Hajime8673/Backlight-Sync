using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BacklightSync;

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x13, 0x15, 0x1A);
    public static readonly Color Card = Color.FromArgb(0x1C, 0x1F, 0x26);
    public static readonly Color CardHi = Color.FromArgb(0x24, 0x28, 0x31);
    public static readonly Color Border = Color.FromArgb(0x2C, 0x31, 0x3B);
    public static readonly Color Text = Color.FromArgb(0xEC, 0xEE, 0xF2);
    public static readonly Color Sub = Color.FromArgb(0x8B, 0x92, 0xA3);
    public static readonly Color Sync = Color.FromArgb(0x4C, 0x9A, 0xFF);
    public static readonly Color Night = Color.FromArgb(0xFF, 0xB3, 0x47);
    public static readonly Color Neutral = Color.FromArgb(0x7C, 0x8C, 0xFF);
    public static readonly Color Good = Color.FromArgb(0x4A, 0xDE, 0x80);
    public static readonly Color Bad = Color.FromArgb(0xF2, 0x6D, 0x6D);

    public static Font F(float pt, FontStyle st = FontStyle.Regular) => new("Segoe UI", pt, st, GraphicsUnit.Point);
    public static Font Semi(float pt) => new("Segoe UI Semibold", pt, FontStyle.Regular, GraphicsUnit.Point);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Base for owner-drawn controls: double buffered, smooth, DPI aware.</summary>
public abstract class UiControl : Control
{
    protected UiControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
    }
    protected float K => DeviceDpi / 96f;
    protected void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}

public sealed class FlatSlider : UiControl
{
    int _min, _max = 100, _val;
    bool _drag, _hot;
    public Color Accent { get; set; } = Theme.Sync;
    public event EventHandler? ValueChanged;

    public FlatSlider() { SetStyle(ControlStyles.Selectable, true); TabStop = true; Height = 28; Cursor = Cursors.Hand; }

    public int Minimum { get => _min; set { _min = value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = Math.Max(value, _min + 1); Invalidate(); } }
    public int Value
    {
        get => _val;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _val) return;
            _val = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    /// <summary>Sets the value without raising ValueChanged (used when loading settings).</summary>
    public void SetSilently(int v) { _val = Math.Clamp(v, _min, _max); Invalidate(); }

    RectangleF Track => new(10 * K, Height / 2f - 2 * K, Width - 20 * K, 4 * K);
    float ThumbX => Track.X + Track.Width * (_val - _min) / (float)(_max - _min);

    void FromMouse(int x) => Value = _min + (int)Math.Round((x - Track.X) / Track.Width * (_max - _min));

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); _drag = true; Capture = true; FromMouse(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) FromMouse(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Capture = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? Math.Max(1, (_max - _min) / 10) : Math.Max(1, (_max - _min) / 100);
        if (e.KeyCode is Keys.Left or Keys.Down) Value -= step;
        else if (e.KeyCode is Keys.Right or Keys.Up) Value += step;
        else base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(BackColor);
        var t = Track;
        using (var b = new SolidBrush(Theme.Border)) using (var p = Theme.Round(t, t.Height / 2)) g.FillPath(b, p);
        float x = ThumbX;
        var fill = new RectangleF(t.X, t.Y, Math.Max(t.Height, x - t.X), t.Height);
        using (var b = new SolidBrush(Accent)) using (var p = Theme.Round(fill, t.Height / 2)) g.FillPath(b, p);
        float r = (_drag ? 9f : _hot ? 8.5f : 8f) * K;
        if (_drag || Focused)
            using (var halo = new SolidBrush(Color.FromArgb(50, Accent))) g.FillEllipse(halo, x - r - 4 * K, Height / 2f - r - 4 * K, 2 * r + 8 * K, 2 * r + 8 * K);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - r, Height / 2f - r, 2 * r, 2 * r);
        using (var pen = new Pen(Accent, 2 * K)) g.DrawEllipse(pen, x - r + K, Height / 2f - r + K, 2 * r - 2 * K, 2 * r - 2 * K);
    }
}

public sealed class ToggleSwitch : UiControl
{
    bool _on;
    public Color Accent { get; set; } = Theme.Sync;
    public event EventHandler? CheckedChanged;
    public ToggleSwitch() { Size = new Size(46, 26); Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

    public bool Checked
    {
        get => _on;
        set { if (_on == value) return; _on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void SetSilently(bool v) { _on = v; Invalidate(); }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(BackColor);
        float w = 44 * K, h = 24 * K;
        var r = new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
        using (var b = new SolidBrush(_on ? Accent : Theme.Border)) using (var p = Theme.Round(r, h / 2)) g.FillPath(b, p);
        float d = h - 6 * K;
        float x = _on ? r.Right - d - 3 * K : r.X + 3 * K;
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, x, r.Y + 3 * K, d, d);
        if (Focused) using (var pen = new Pen(Color.FromArgb(120, Accent), 2 * K)) using (var p = Theme.Round(RectangleF.Inflate(r, 2 * K, 2 * K), h / 2 + 2 * K)) g.DrawPath(pen, p);
    }
}

public enum CardIcon { Screen, Moon, Power }

public sealed class ModeCard : UiControl
{
    bool _hot, _sel;
    public CardIcon Icon { get; set; }
    public Color Accent { get; set; } = Theme.Sync;
    public string Caption { get; set; } = "";
    public string Sub { get; set; } = "";
    public bool Selected { get => _sel; set { if (_sel == value) return; _sel = value; Invalidate(); } }
    public ModeCard() { Cursor = Cursors.Hand; BackColor = Theme.Bg; }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(Theme.Bg);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var p = Theme.Round(r, 14 * K))
        {
            using var fill = new SolidBrush(_sel ? Theme.Mix(Theme.Card, Accent, 0.14f) : _hot ? Theme.CardHi : Theme.Card);
            g.FillPath(fill, p);
            using var pen = new Pen(_sel ? Accent : Theme.Border, _sel ? 2 * K : 1);
            g.DrawPath(pen, p);
        }
        var col = _sel ? Accent : Theme.Sub;
        float cx = Width / 2f, cy = Height * 0.36f, s = 12 * K;
        using (var pen = new Pen(col, 2.2f * K) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            switch (Icon)
            {
                case CardIcon.Screen:
                {
                    var outer = new RectangleF(cx - s * 1.45f, cy - s * 0.95f, s * 2.9f, s * 1.9f);
                    using (var glow = new Pen(Color.FromArgb(_sel ? 110 : 70, col), 1.6f * K)) using (var gp = Theme.Round(RectangleF.Inflate(outer, 4 * K, 4 * K), 5 * K)) g.DrawPath(glow, gp);
                    using var pp = Theme.Round(outer, 3.5f * K); g.DrawPath(pen, pp);
                    g.DrawLine(pen, cx - s * 0.5f, cy + s * 1.35f, cx + s * 0.5f, cy + s * 1.35f);
                    break;
                }
                case CardIcon.Moon:
                {
                    using var moon = new GraphicsPath(); moon.AddEllipse(cx - s, cy - s, 2 * s, 2 * s);
                    using var cut = new GraphicsPath(); cut.AddEllipse(cx - s * 0.2f, cy - s * 1.25f, 2 * s, 2 * s);
                    using var reg = new Region(moon); reg.Exclude(cut);
                    using var br = new SolidBrush(col); g.FillRegion(br, reg);
                    break;
                }
                case CardIcon.Power:
                {
                    g.DrawArc(pen, cx - s, cy - s, 2 * s, 2 * s, -55, 290);
                    g.DrawLine(pen, cx, cy - s * 1.15f, cx, cy - s * 0.1f);
                    break;
                }
            }
        }
        using var f1 = Theme.Semi(10.5f); using var f2 = Theme.F(8.5f);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        using (var b = new SolidBrush(_sel ? Theme.Text : Color.FromArgb(200, Theme.Text)))
            g.DrawString(Caption, f1, b, new RectangleF(0, Height * 0.62f, Width, 22 * K), sf);
        using (var b = new SolidBrush(Theme.Sub))
            g.DrawString(Sub, f2, b, new RectangleF(0, Height * 0.62f + 20 * K, Width, 18 * K), sf);
    }
}

public sealed class TabBar : UiControl
{
    string[] _items = Array.Empty<string>();
    int _sel, _hot = -1;
    public Color Accent { get; set; } = Theme.Sync;
    public event EventHandler? SelectedChanged;
    public TabBar() { Height = 38; Cursor = Cursors.Hand; }
    public string[] Items { get => _items; set { _items = value; Invalidate(); } }
    public int Selected { get => _sel; set { if (_sel == value) return; _sel = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); } }

    int Hit(int x) => _items.Length == 0 ? -1 : Math.Clamp(x * _items.Length / Math.Max(1, Width), 0, _items.Length - 1);
    protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.X); if (h != _hot) { _hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { Selected = Hit(e.X); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(Theme.Bg);
        using (var pen = new Pen(Theme.Border, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        float w = Width / (float)Math.Max(1, _items.Length);
        using var fn = Theme.F(10f); using var fs = Theme.Semi(10f);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < _items.Length; i++)
        {
            var r = new RectangleF(i * w, 0, w, Height - 2);
            bool sel = i == _sel;
            using (var b = new SolidBrush(sel ? Theme.Text : i == _hot ? Color.FromArgb(220, Theme.Text) : Theme.Sub))
                g.DrawString(_items[i], sel ? fs : fn, b, r, sf);
            if (sel) using (var b = new SolidBrush(Accent)) g.FillRectangle(b, i * w + w * 0.22f, Height - 3 * K, w * 0.56f, 3 * K);
        }
    }
}

public sealed class FlatButton : UiControl
{
    bool _hot, _down;
    public bool Primary { get; set; }
    public Color Accent { get; set; } = Theme.Sync;
    public FlatButton() { Height = 38; Cursor = Cursors.Hand; }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(BackColor);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        Color fill = Primary ? (_down ? Theme.Mix(Accent, Color.Black, 0.25f) : _hot ? Theme.Mix(Accent, Color.White, 0.12f) : Accent)
                             : (_down ? Theme.Card : _hot ? Theme.CardHi : Theme.Card);
        using (var p = Theme.Round(r, 10 * K))
        {
            using var b = new SolidBrush(fill); g.FillPath(b, p);
            if (!Primary) { using var pen = new Pen(Theme.Border, 1); g.DrawPath(pen, p); }
        }
        using var f = Theme.Semi(9.5f);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var tb = new SolidBrush(Primary ? Color.FromArgb(0x10, 0x12, 0x16) : Theme.Text);
        g.DrawString(Text, f, tb, ClientRectangle, sf);
    }
}

/// <summary>Little coloured dot + text, e.g. "● Syncing · 144 fps".</summary>
public sealed class StatusPill : UiControl
{
    public Color Dot { get; set; } = Theme.Sub;
    string _text = "";
    public override string Text { get => _text; set { if (_text == value) return; _text = value; Invalidate(); } }
    public StatusPill() { Height = 30; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(BackColor);
        using var f = Theme.F(9f);
        var size = g.MeasureString(_text, f);
        float w = size.Width + 34 * K, h = 26 * K;
        var r = new RectangleF(Width - w - 1, (Height - h) / 2, w, h);
        using (var p = Theme.Round(r, h / 2)) { using var b = new SolidBrush(Theme.Card); g.FillPath(b, p); using var pen = new Pen(Theme.Border); g.DrawPath(pen, p); }
        using (var b = new SolidBrush(Dot)) g.FillEllipse(b, r.X + 11 * K, r.Y + h / 2 - 4 * K, 8 * K, 8 * K);
        using (var b = new SolidBrush(Theme.Text)) g.DrawString(_text, f, b, r.X + 24 * K, r.Y + (h - size.Height) / 2 + 1);
    }
}

/// <summary>Shows the screen with the strip around it, lit with the colours the LEDs have right now.</summary>
public sealed class StripPreview : UiControl
{
    readonly Func<Settings> _settings;
    readonly Func<byte[]> _colors;
    readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };
    LedSlot[] _slots = Array.Empty<LedSlot>();
    int _key = -1;

    public StripPreview(Func<Settings> settings, Func<byte[]> colors)
    {
        _settings = settings; _colors = colors;
        Height = 200;
        _t.Tick += (_, _) => { if (Visible && FindForm()?.WindowState != FormWindowState.Minimized) Invalidate(); };
        _t.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Smooth(g); g.Clear(Theme.Bg);
        var s = _settings();
        int key = HashCode.Combine(s.LedCount, s.LeftLeds, s.TopLeds, s.RightLeds, s.Clockwise);
        if (key != _key) { _key = key; _slots = BacklightSync.Layout.Build(s); }
        var col = _colors();

        // card
        var card = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var p = Theme.Round(card, 16 * K)) { using var b = new SolidBrush(Color.FromArgb(0x10, 0x12, 0x16)); g.FillPath(b, p); using var pen = new Pen(Theme.Border); g.DrawPath(pen, p); }

        // monitor rectangle (16:9)
        float mh = card.Height * 0.66f, mw = mh * 16f / 9f;
        if (mw > card.Width * 0.72f) { mw = card.Width * 0.72f; mh = mw * 9f / 16f; }
        var mon = new RectangleF(card.X + (card.Width - mw) / 2, card.Y + card.Height * 0.2f, mw, mh);
        float gap = 11 * K;

        // soft light spill first, then the LED dots on top
        var save = g.Clip;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var sl = _slots[i];
                if (sl.Side == Side.None) continue;
                float f = (sl.Index + 0.5f) / sl.Count, x, y;
                switch (sl.Side)
                {
                    case Side.Left: x = mon.X - gap; y = mon.Bottom - f * mon.Height; break;
                    case Side.Right: x = mon.Right + gap; y = mon.Bottom - f * mon.Height; break;
                    default: x = mon.X + f * mon.Width; y = mon.Y - gap; break;
                }
                Color c = i * 3 + 2 < col.Length ? Color.FromArgb(col[i * 3], col[i * 3 + 1], col[i * 3 + 2]) : Color.Black;
                bool lit = c.R + c.G + c.B > 12;
                if (pass == 0 && lit)
                {
                    for (int k = 3; k >= 1; k--)
                    {
                        float rr = (3 + k * 4.5f) * this.K;
                        using var b = new SolidBrush(Color.FromArgb(10 + (4 - k) * 5, c));
                        g.FillEllipse(b, x - rr, y - rr, 2 * rr, 2 * rr);
                    }
                }
                else if (pass == 1)
                {
                    float rr = 2.7f * this.K;
                    using var b = new SolidBrush(lit ? c : Color.FromArgb(0x2A, 0x2E, 0x38));
                    g.FillEllipse(b, x - rr, y - rr, 2 * rr, 2 * rr);
                }
            }
        }

        // the screen itself: tinted by the average LED colour so it feels connected
        long r0 = 0, g0 = 0, b0 = 0; int n = col.Length / 3;
        for (int i = 0; i < n; i++) { r0 += col[i * 3]; g0 += col[i * 3 + 1]; b0 += col[i * 3 + 2]; }
        Color avg = n > 0 ? Color.FromArgb((int)(r0 / n), (int)(g0 / n), (int)(b0 / n)) : Color.Black;
        using (var p = Theme.Round(mon, 6 * K))
        {
            using var b = new SolidBrush(Theme.Mix(Color.FromArgb(0x1A, 0x1D, 0x24), avg, 0.18f));
            g.FillPath(b, p);
            using var pen = new Pen(Color.FromArgb(0x3A, 0x40, 0x4C), 1.5f * K); g.DrawPath(pen, p);
        }
        using var f1 = Theme.F(8.5f); using var sb = new SolidBrush(Theme.Sub);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("your screen", f1, sb, mon, sf);
    }
}

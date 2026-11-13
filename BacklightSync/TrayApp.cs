using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BacklightSync;

/// <summary>Tray icon, menu, global hotkey, power / display events. Owns the engine.</summary>
public sealed class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly Settings _s;
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly MainForm _form;
    readonly HotkeyWindow _hotkey;
    readonly ToolStripMenuItem _miSync = new("Movie sync"), _miNight = new("Night light"), _miOff = new("Off");

    public TrayApp(bool startHidden)
    {
        _s = Settings.Load();
        _engine = new Engine(_s);
        _form = new MainForm(_s, _engine);
        _form.ModeChangedByUser += RefreshMenu;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings", null, (_, _) => _form.ShowAndFocus());
        menu.Items.Add(new ToolStripSeparator());
        _miSync.Click += (_, _) => SetMode(LightMode.Sync);
        _miNight.Click += (_, _) => SetMode(LightMode.Night);
        _miOff.Click += (_, _) => SetMode(LightMode.Off);
        menu.Items.AddRange(new ToolStripItem[] { _miSync, _miNight, _miOff });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test pattern", null, (_, _) => _engine.ShowTestPattern(8));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Backlight Sync", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _form.ShowAndFocus(); };
        RefreshMenu();

        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += CycleMode;
        if (!_hotkey.Register(HotkeyWindow.MOD_CONTROL | HotkeyWindow.MOD_ALT, (int)Keys.B))
            Log.Write("could not register Ctrl+Alt+B");

        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.DisplaySettingsChanged += (_, _) => _engine.ReopenCapture();

        _engine.Start();

        if (!startHidden) _form.ShowAndFocus();
    }

    void SetMode(LightMode m)
    {
        _s.Mode = m; _s.Save();
        _form.ShowMode(); RefreshMenu();
    }

    void CycleMode()
    {
        SetMode(_s.Mode switch { LightMode.Sync => LightMode.Night, LightMode.Night => LightMode.Off, _ => LightMode.Sync });
        _tray.ShowBalloonTip(1200, "Backlight Sync", _s.Mode switch { LightMode.Sync => "Movie sync", LightMode.Night => "Night light", _ => "Lights off" }, ToolTipIcon.None);
    }

    void RefreshMenu()
    {
        _miSync.Checked = _s.Mode == LightMode.Sync;
        _miNight.Checked = _s.Mode == LightMode.Night;
        _miOff.Checked = _s.Mode == LightMode.Off;
    }

    void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _engine.Suspend(true);
        else if (e.Mode == PowerModes.Resume) { _engine.ReopenCapture(); _engine.Suspend(false); }
    }

    void OnSession(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _engine.Suspend(true);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { _engine.ReopenCapture(); _engine.Suspend(false); }
    }

    void ExitApp()
    {
        _tray.Visible = false;
        _hotkey.Dispose();
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSession;
        _s.Save();
        _engine.Dispose();   // blanks the strip
        _tray.Dispose();
        Application.Exit();
    }

    // ------------------------------------------------------------------ helpers

    public static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on) k.SetValue("BacklightSync", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("BacklightSync", false);
        }
        catch (Exception e) { Log.Write("autostart failed: " + e.Message); }
    }

    public static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var screen = new SolidBrush(Color.FromArgb(30, 30, 36));
            g.FillRectangle(screen, 7, 8, 18, 14);
            using var glow = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.DeepSkyBlue, Color.Orange, 45f);
            using var pen = new Pen(glow, 3.2f);
            g.DrawRectangle(pen, 4, 5, 24, 20);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>Invisible window that receives the global hotkey message.</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const int MOD_ALT = 1, MOD_CONTROL = 2;
    const int WM_HOTKEY = 0x0312, Id = 0xB11;
    public event Action? Pressed;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyWindow() { CreateHandle(new CreateParams()); }

    public bool Register(int mods, int vk) => RegisterHotKey(Handle, Id, mods, vk);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}

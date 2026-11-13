using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BacklightSync;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // command line tools for testing (output goes to the console and to %APPDATA%\BacklightSync\log.txt)
        if (args.Length > 0 && args[0].StartsWith("--") && args[0] != "--tray")
        {
            AttachConsole(-1);
            return Cli.Run(args);
        }

        using var mutex = new Mutex(true, @"Local\BacklightSync.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Backlight Sync is already running – look for its icon near the clock.", "Backlight Sync");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled: " + e.ExceptionObject);
        Application.ThreadException += (_, e) => Log.Write("ui exception: " + e.Exception);

        Application.Run(new TrayApp(startHidden: args.Contains("--tray")));
        return 0;
    }
}

/// <summary>Small diagnostics so each part can be tested without the window.</summary>
static class Cli
{
    static void P(string s) { Console.WriteLine(s); Log.Write("[cli] " + s); }

    public static int Run(string[] a)
    {
        var s = Settings.Load();
        switch (a[0])
        {
            case "--probe":     // talk to the strip: info + red / green / blue / off
            {
                using var l = new Backlight();
                if (!l.Open()) { P("Could not open the backlight (is SyncLight still running?)"); return 1; }
                P("opened: " + l.Info);
                foreach (var (name, r, g, b) in new[] { ("red", 255, 0, 0), ("green", 0, 255, 0), ("blue", 0, 0, 255), ("white", 160, 160, 160) })
                {
                    P("showing " + name);
                    l.SendAll((byte)r, (byte)g, (byte)b, s.LedCount);
                    Thread.Sleep(900);
                }
                l.SendAll(0, 0, 0, s.LedCount);
                return 0;
            }
            case "--off":
            {
                using var l = new Backlight();
                if (!l.Open()) { P("no device"); return 1; }
                l.SendAll(0, 0, 0, s.LedCount); P("off"); return 0;
            }
            case "--night":
            {
                using var l = new Backlight();
                if (!l.Open()) { P("no device"); return 1; }
                var (r, g, b) = WarmLight.FromKelvin(s.NightKelvin);
                var p = new ColorPipeline();
                var t = new float[s.LedCount * 3];
                for (int i = 0; i < s.LedCount; i++) { t[i * 3] = r; t[i * 3 + 1] = g; t[i * 3 + 2] = b; }
                var w = new byte[s.LedCount * 3];
                p.Step(t, s.LedCount, 0.1f, s); p.Render(s.LedCount, s, w, s.NightBrightness / 100f, 1f);
                l.SendFrame(w, s.LedCount);
                P($"night light sent, first LED = {w[0]},{w[1]},{w[2]}");
                return 0;
            }
            case "--grab":      // measure capture speed + bar detection for ~4 seconds
            {
                var cap = ScreenCapture.Create(s.DisplayDevice);
                if (cap == null) { P("capture failed"); return 1; }
                P($"display {cap.DisplayName} {cap.ScreenWidth}x{cap.ScreenHeight}, small picture {cap.Width}x{cap.Height}");
                var det = new BarDetector(); var sam = new EdgeSampler();
                var slots = Layout.Build(s); var rgb = new float[slots.Length * 3];
                var sw = Stopwatch.StartNew(); int frames = 0, polls = 0; double totalMs = 0;
                Bars bars = default;
                while (sw.ElapsedMilliseconds < 4000)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    var r = cap.Grab(30); polls++;
                    if (r == GrabResult.NewFrame)
                    {
                        bars = det.Update(cap.Pixels, cap.Width, cap.Height, s.BarThreshold);
                        sam.Sample(cap.Pixels, cap.Width, cap.Height, bars, s.SampleDepth, slots, rgb);
                        frames++;
                        totalMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    }
                    else if (r == GrabResult.Lost || r == GrabResult.Error) { P("grab result " + r); break; }
                }
                P($"{frames} new frames in 4 s ({frames / 4.0:0.#}/s, screen only changes when something moves), avg processing {(frames > 0 ? totalMs / frames : 0):0.00} ms per frame");
                P($"bars: top {bars.Top} bottom {bars.Bottom} left {bars.Left} right {bars.Right} (small-picture pixels)");
                cap.Dispose();
                return 0;
            }
            case "--selftest":  // synthetic frames for the black bar detector
            {
                const int W = 320, H = 180;
                byte[] Frame(int cx0, int cx1, int cy0, int cy1, Action<byte[]>? extra = null)
                {
                    var f = new byte[W * H * 4];
                    for (int y = cy0; y < cy1; y++)
                        for (int x = cx0; x < cx1; x++)
                        {
                            int i = (y * W + x) * 4;
                            f[i] = (byte)(60 + (x * 3 + y) % 80); f[i + 1] = (byte)(90 + (x + y * 2) % 100); f[i + 2] = (byte)(140 + (x * 2) % 90);
                        }
                    extra?.Invoke(f);
                    return f;
                }
                void Text(byte[] f, int x0, int x1, int y0, int y1)
                {   // white subtitle blob
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * W + x) * 4; f[i] = f[i + 1] = f[i + 2] = 235; }
                }
                var det = new BarDetector();
                Bars Feed(byte[] frame, int ms = 1100)
                {
                    var end = Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000;
                    Bars b = default;
                    while (Stopwatch.GetTimestamp() < end) { b = det.Update(frame, W, H, 14); Thread.Sleep(16); }
                    return b;
                }
                int fails = 0;
                void Check(string name, Bars got, Bars want, int tol = 2)
                {
                    bool ok = Math.Abs(got.Top - want.Top) <= tol && Math.Abs(got.Bottom - want.Bottom) <= tol &&
                              Math.Abs(got.Left - want.Left) <= tol && Math.Abs(got.Right - want.Right) <= tol;
                    if (!ok) fails++;
                    P($"{(ok ? "PASS" : "FAIL")}  {name}: got T{got.Top} B{got.Bottom} L{got.Left} R{got.Right}, expected T{want.Top} B{want.Bottom} L{want.Left} R{want.Right}");
                }
                Check("full 16:9 picture", Feed(Frame(0, W, 0, H)), Bars.None);
                Check("cinema 2.39:1 bars", Feed(Frame(0, W, 23, H - 23)), new Bars(23, 23, 0, 0));
                Check("cinema bars + subtitle in bottom bar", Feed(Frame(0, W, 23, H - 23, f => Text(f, 90, 230, 160, 168))), new Bars(23, 23, 0, 0));
                Check("dark scene (bottom 40 rows black) inside cinema bars -> unchanged", Feed(Frame(0, W, 23, 100)), new Bars(23, 23, 0, 0));
                Check("fade to full black holds bars", Feed(Frame(0, 0, 0, 0)), new Bars(23, 23, 0, 0));
                Check("back to full picture", Feed(Frame(0, W, 0, H)), Bars.None);
                Check("4:3 pillarbox", Feed(Frame(40, 280, 0, H)), new Bars(0, 0, 40, 40));
                Check("window-box (bars all round)", Feed(Frame(40, 280, 23, H - 23)), new Bars(23, 23, 40, 40));
                det.Reset();
                Check("night scene: only top 90 rows lit, no bars", Feed(Frame(0, W, 0, 90)), Bars.None, 0);
                P(fails == 0 ? "ALL PASSED" : fails + " FAILED");
                return fails;
            }
            case "--pattern":
            {
                using var l = new Backlight();
                if (!l.Open()) { P("no device"); return 1; }
                var e = new Engine(s); e.ShowTestPattern(6); e.Start();
                Thread.Sleep(6500); e.Dispose();
                return 0;
            }
        }
        P("unknown option. Try --probe, --grab, --pattern, --night, --off");
        return 2;
    }
}

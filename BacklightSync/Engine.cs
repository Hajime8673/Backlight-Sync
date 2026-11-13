using System.Diagnostics;

namespace BacklightSync;

/// <summary>Background loop: capture the screen, find the picture, colour the LEDs, talk to the strip.</summary>
public sealed class Engine : IDisposable
{
    readonly Settings _s;
    Thread? _thread;
    volatile bool _stop;
    volatile bool _suspended;
    long _patternUntil;      // Stopwatch ticks
    volatile bool _forceOff;
    volatile bool _reopenCapture;

    public string Status { get; private set; } = "Starting…";
    public double Fps { get; private set; }
    public double SendMs { get; private set; }
    public Bars Bars { get; private set; }
    public bool Connected { get; private set; }
    /// <summary>What the LEDs are showing right now (R,G,B per LED), for the preview in the window.</summary>
    public volatile byte[] Preview = Array.Empty<byte>();
    public int ScreenW { get; private set; }
    public int ScreenH { get; private set; }
    public bool PatternActive => Stopwatch.GetTimestamp() < Interlocked.Read(ref _patternUntil);

    public Engine(Settings s) { _s = s; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "BacklightEngine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Suspend(bool on) { _suspended = on; }
    public void ReopenCapture() { _reopenCapture = true; }

    public void ShowTestPattern(double seconds)
        => Interlocked.Exchange(ref _patternUntil, Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency));

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
    }

    // ------------------------------------------------------------------------------------------

    void Run()
    {
        using var light = new Backlight();
        ScreenCapture? cap = null;
        var bars = new BarDetector();
        var sampler = new EdgeSampler();
        var pipe = new ColorPipeline();
        var sw = Stopwatch.StartNew();

        float[] target = Array.Empty<float>();
        byte[] wire = Array.Empty<byte>(), lastWire = Array.Empty<byte>();
        LedSlot[] slots = Array.Empty<LedSlot>();
        int layoutKey = -1;
        bool blankSent = false;
        long lastSent = 0, lastFrameT = sw.ElapsedMilliseconds, fpsT = sw.ElapsedMilliseconds;
        int fpsCount = 0;
        double sendAcc = 0; int sendN = 0;
        long lastCapRetry = 0;

        while (!_stop)
        {
            try
            {
                // ---- layout bookkeeping (cheap; lets settings change live)
                int key = HashCode.Combine(_s.LedCount, _s.LeftLeds, _s.TopLeds, _s.RightLeds, _s.Clockwise);
                if (key != layoutKey || slots.Length != _s.LedCount)
                {
                    layoutKey = key; slots = Layout.Build(_s);
                    target = new float[slots.Length * 3]; wire = new byte[slots.Length * 3]; lastWire = new byte[slots.Length * 3];
                    pipe.Reset(); lastSent = 0;
                }
                int n = slots.Length;

                // ---- device
                if (!light.IsOpen)
                {
                    Connected = false;
                    Status = "Looking for the backlight…";
                    if (!light.Open()) { Thread.Sleep(800); continue; }
                    Connected = true;
                    Status = "Backlight connected" + (light.Info.Length > 0 ? " (" + light.Info + ")" : "");
                    Log.Write(Status);
                    lastSent = 0; blankSent = false; pipe.Reset();
                }

                // ---- off / paused
                if (_suspended || _s.Mode == LightMode.Off || _forceOff)
                {
                    if (!blankSent) { blankSent = light.SendAll(0, 0, 0, n) ? true : false; Array.Clear(lastWire); }
                    Preview = new byte[n * 3];
                    Status = _suspended ? "Paused (screen off / locked)" : "Lights off";
                    Fps = 0;
                    Thread.Sleep(80);
                    continue;
                }

                // ---- test pattern
                if (PatternActive)
                {
                    SendPattern(light, slots);
                    blankSent = false; lastSent = 0; pipe.Reset();
                    Status = "Test pattern";
                    Thread.Sleep(150);
                    continue;
                }

                // ---- night light: steady soft golden glow, no screen capture needed
                if (_s.Mode == LightMode.Night)
                {
                    if (cap != null) { cap.Dispose(); cap = null; }   // free the GPU while nobody is watching
                    var (nr, ng, nb) = WarmLight.FromKelvin(_s.NightKelvin);
                    for (int i = 0; i < n; i++) { target[i * 3] = nr; target[i * 3 + 1] = ng; target[i * 3 + 2] = nb; }
                    long tn = sw.ElapsedMilliseconds;
                    float dtn = Math.Clamp((tn - lastFrameT) / 1000f, 0.001f, 0.25f);
                    lastFrameT = tn;
                    pipe.Step(target, n, dtn, _s, fixedTau: 0.45f);      // gentle fade in from whatever was showing
                    pipe.Render(n, _s, wire, _s.NightBrightness / 100f, 1f);
                    Preview = (byte[])pipe.LastRgb.Clone();
                    if (!wire.AsSpan().SequenceEqual(lastWire) || tn - lastSent > 400 || blankSent)
                    {
                        if (light.SendFrame(wire, n)) { Array.Copy(wire, lastWire, wire.Length); lastSent = tn; blankSent = false; }
                        else { Connected = false; Status = "Lost the backlight, retrying…"; continue; }
                    }
                    Status = "Night light";
                    Fps = 0;
                    Thread.Sleep(25);
                    continue;
                }

                // ---- capture
                if (_reopenCapture) { cap?.Dispose(); cap = null; _reopenCapture = false; }
                if (cap == null)
                {
                    long now = sw.ElapsedMilliseconds;
                    if (now - lastCapRetry < 1000) { Thread.Sleep(100); continue; }
                    lastCapRetry = now;
                    cap = ScreenCapture.Create(_s.DisplayDevice);
                    if (cap == null) { Status = "Can't capture the screen yet (secure screen?)"; continue; }
                    ScreenW = cap.ScreenWidth; ScreenH = cap.ScreenHeight;
                    Log.Write($"capturing {cap.DisplayName} {cap.ScreenWidth}x{cap.ScreenHeight} -> {cap.Width}x{cap.Height}");
                    bars.Reset();
                }

                int interval = 1000 / Math.Clamp(_s.TargetFps, 10, 120);
                long t0 = sw.ElapsedMilliseconds;
                var res = cap.Grab(interval);
                if (res == GrabResult.Lost) { cap.Dispose(); cap = null; Status = "Screen changed, reconnecting…"; Thread.Sleep(300); continue; }
                if (res == GrabResult.Error) { Thread.Sleep(50); continue; }

                long nowMs = sw.ElapsedMilliseconds;
                float dt = Math.Clamp((nowMs - lastFrameT) / 1000f, 0.001f, 0.25f);
                lastFrameT = nowMs;

                if (res == GrabResult.NewFrame)
                {
                    var b = _s.AutoBlackBars ? bars.Update(cap.Pixels, cap.Width, cap.Height, _s.BarThreshold) : Bars.None;
                    Bars = b;
                    sampler.Sample(cap.Pixels, cap.Width, cap.Height, b, _s.SampleDepth, slots, target);
                    fpsCount++;
                }
                pipe.Step(target, n, dt, _s);
                pipe.Render(n, _s, wire);
                Preview = (byte[])pipe.LastRgb.Clone();

                // ---- send only when something changed (plus a keep-alive)
                bool changed = !wire.AsSpan().SequenceEqual(lastWire);
                if (changed || nowMs - lastSent > 400 || blankSent)
                {
                    long a = Stopwatch.GetTimestamp();
                    if (light.SendFrame(wire, n))
                    {
                        sendAcc += (Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency; sendN++;
                        Array.Copy(wire, lastWire, wire.Length);
                        lastSent = nowMs; blankSent = false;
                        Status = bars.Current.Any && _s.AutoBlackBars ? "Syncing (black bars removed)" : "Syncing";
                    }
                    else { Connected = false; Status = "Lost the backlight, retrying…"; continue; }
                }

                if (nowMs - fpsT >= 1000)
                {
                    Fps = fpsCount * 1000.0 / (nowMs - fpsT);
                    SendMs = sendN > 0 ? sendAcc / sendN : 0;
                    fpsCount = 0; fpsT = nowMs; sendAcc = 0; sendN = 0;
                }

                // pace to the target frame rate when the screen updates faster than we need
                int spent = (int)(sw.ElapsedMilliseconds - t0);
                if (spent < interval) Thread.Sleep(interval - spent);
            }
            catch (Exception e)
            {
                Log.Write("engine error: " + e);
                Status = "Error: " + e.Message;
                try { cap?.Dispose(); } catch { }
                cap = null;
                Thread.Sleep(500);
            }
        }

        try { if (light.IsOpen) light.SendAll(0, 0, 0, _s.LedCount); } catch { }
        cap?.Dispose();
    }

    void SendPattern(Backlight light, LedSlot[] slots)
    {
        var wire = new byte[slots.Length * 3];
        var prev = new byte[slots.Length * 3];
        for (int i = 0; i < slots.Length; i++)
        {
            var sl = slots[i];
            byte r = 0, g = 0, b = 0;
            switch (sl.Side)
            {
                case Side.Left: r = 160; break;     // red   = left edge
                case Side.Top: g = 160; break;      // green = top edge
                case Side.Right: b = 160; break;    // blue  = right edge
            }
            // white marks the bottom LED of each side and the left-most LED of the top
            if (sl.Side != Side.None && sl.Index == 0) { r = g = b = 200; }
            int o0 = "RGB".IndexOf(char.ToUpperInvariant(_s.ColorOrder[0]));
            int o1 = "RGB".IndexOf(char.ToUpperInvariant(_s.ColorOrder[1]));
            int o2 = "RGB".IndexOf(char.ToUpperInvariant(_s.ColorOrder[2]));
            var v = new[] { r, g, b };
            wire[i * 3] = v[o0]; wire[i * 3 + 1] = v[o1]; wire[i * 3 + 2] = v[o2];
            prev[i * 3] = r; prev[i * 3 + 1] = g; prev[i * 3 + 2] = b;
        }
        Preview = prev;
        light.SendFrame(wire, slots.Length);
    }
}

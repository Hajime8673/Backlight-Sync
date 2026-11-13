using System.Text.Json;
using System.Text.Json.Serialization;

namespace BacklightSync;

public enum LightMode { Sync, Night, Off }

/// <summary>User settings, stored in %APPDATA%\BacklightSync\settings.json.</summary>
public sealed class Settings
{
    // ---- strip layout (defaults match the 57-LED "Backlight Strip for Monitor") ----
    public int LedCount { get; set; } = 57;
    public int LeftLeds { get; set; } = 15;
    public int TopLeds { get; set; } = 27;
    public int RightLeds { get; set; } = 15;
    /// <summary>true: LED 1 is bottom-left, strip goes up the left side, over the top, down the right.
    /// false: LED 1 is bottom-right, up the right side, over the top (right to left), down the left.</summary>
    public bool Clockwise { get; set; } = false;
    public string ColorOrder { get; set; } = "RGB";

    // ---- picture ----
    public int Brightness { get; set; } = 100;      // 0..100
    public int Saturation { get; set; } = 130;      // 100 = unchanged
    public int Gamma { get; set; } = 160;           // /100, higher = darker mid-tones
    public int Smoothing { get; set; } = 35;        // 0..100, higher = calmer
    public int SampleDepth { get; set; } = 12;      // % of picture used at each edge
    public int BlackCut { get; set; } = 3;          // below this (0..255) the LED turns off

    // ---- black bars ----
    public bool AutoBlackBars { get; set; } = true;
    public int BarThreshold { get; set; } = 14;     // 0..80, how dark counts as "black"

    // ---- night light ----
    public int NightBrightness { get; set; } = 40;  // 1..100
    public int NightKelvin { get; set; } = 2300;    // 1500..4000, lower = more golden/amber

    // ---- system ----
    public int TargetFps { get; set; } = 60;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LightMode Mode { get; set; } = LightMode.Sync;
    public bool StartWithWindows { get; set; } = false;
    public string DisplayDevice { get; set; } = "";  // empty = primary display

    // ------------------------------------------------------------------
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BacklightSync");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
    }

    /// <summary>Keeps the per-side counts consistent with the LED total.</summary>
    public void Normalize()
    {
        LedCount = Math.Clamp(LedCount, 1, 250);
        LeftLeds = Math.Max(0, LeftLeds); TopLeds = Math.Max(0, TopLeds); RightLeds = Math.Max(0, RightLeds);
        if (LeftLeds + TopLeds + RightLeds > LedCount)
        {
            LeftLeds = RightLeds = Math.Min(LeftLeds, LedCount / 4);
            TopLeds = LedCount - LeftLeds - RightLeds;
        }
        Brightness = Math.Clamp(Brightness, 0, 100);
        Saturation = Math.Clamp(Saturation, 0, 300);
        Gamma = Math.Clamp(Gamma, 100, 300);
        Smoothing = Math.Clamp(Smoothing, 0, 100);
        SampleDepth = Math.Clamp(SampleDepth, 3, 40);
        BlackCut = Math.Clamp(BlackCut, 0, 40);
        BarThreshold = Math.Clamp(BarThreshold, 1, 80);
        TargetFps = Math.Clamp(TargetFps, 10, 120);
        NightBrightness = Math.Clamp(NightBrightness, 1, 100);
        NightKelvin = Math.Clamp(NightKelvin, 1500, 4000);
        if (ColorOrder.Length != 3 || ColorOrder.ToUpperInvariant().Distinct().Count() != 3 || ColorOrder.ToUpperInvariant().Any(c => "RGB".IndexOf(c) < 0))
            ColorOrder = "RGB";
    }
}

public static class Log
{
    static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                var path = Path.Combine(Settings.Dir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

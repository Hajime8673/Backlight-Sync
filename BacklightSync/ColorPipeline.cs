namespace BacklightSync;

public static class WarmLight
{
    /// <summary>Colour temperature to linear RGB (0..1). Lower Kelvin = more golden.</summary>
    public static (float r, float g, float b) FromKelvin(int kelvin)
    {
        double t = kelvin / 100.0, r, g, b;
        if (t <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(t) - 161.1195681661;
            b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = 255;
        }
        static float Lin(double v) => (float)Math.Pow(Math.Clamp(v, 0, 255) / 255.0, 2.2);
        return (Lin(r), Lin(g), Lin(b));
    }
}

/// <summary>
/// Turns sampled edge colours into the bytes the strip wants.
/// Smoothing is adaptive: calm while the picture is steady, fast on cuts and flashes,
/// so the light neither flickers nor lags behind the film.
/// </summary>
public sealed class ColorPipeline
{
    float[] _state = Array.Empty<float>();
    bool _primed;

    /// <summary>The last rendered frame as plain R,G,B per LED (before wire-order swapping). Used by the live preview.</summary>
    public byte[] LastRgb { get; private set; } = Array.Empty<byte>();

    public void Reset() => _primed = false;

    /// <param name="fixedTau">When set, fades toward the target with this time constant (used for mode changes).</param>
    public void Step(float[] target, int leds, float dtSeconds, Settings s, float? fixedTau = null)
    {
        if (_state.Length != leds * 3) { _state = new float[leds * 3]; _primed = false; }
        if (!_primed) { Array.Copy(target, _state, leds * 3); _primed = true; return; }
        if (fixedTau is float ft)
        {
            float fa = 1f - MathF.Exp(-dtSeconds / MathF.Max(ft, 0.01f));
            for (int i = 0; i < leds * 3; i++) _state[i] += (target[i] - _state[i]) * fa;
            return;
        }

        float tauSlow = s.Smoothing / 100f * 0.50f;     // up to half a second of calm
        const float tauFast = 0.025f;
        for (int i = 0; i < leds; i++)
        {
            int k = i * 3;
            float dr = target[k] - _state[k], dg = target[k + 1] - _state[k + 1], db = target[k + 2] - _state[k + 2];
            float delta = MathF.Max(MathF.Abs(dr), MathF.Max(MathF.Abs(dg), MathF.Abs(db)));
            float a;
            if (s.Smoothing == 0) a = 1f;
            else
            {
                float tau = tauSlow + (tauFast - tauSlow) * MathF.Min(1f, delta * 5f);
                a = 1f - MathF.Exp(-dtSeconds / MathF.Max(tau, 0.005f));
            }
            _state[k] += dr * a; _state[k + 1] += dg * a; _state[k + 2] += db * a;
        }
    }

    /// <summary>Writes 3 bytes per LED in the strip's wire colour order.</summary>
    public void Render(int leds, Settings s, byte[] wire, float? brightness = null, float? saturation = null)
    {
        float sat = saturation ?? s.Saturation / 100f;
        float bright = brightness ?? s.Brightness / 100f;
        float invG = 1f / (s.Gamma / 100f);
        int o0 = "RGB".IndexOf(char.ToUpperInvariant(s.ColorOrder[0]));
        int o1 = "RGB".IndexOf(char.ToUpperInvariant(s.ColorOrder[1]));
        int o2 = "RGB".IndexOf(char.ToUpperInvariant(s.ColorOrder[2]));
        Span<float> c = stackalloc float[3];
        Span<byte> v = stackalloc byte[3];
        if (LastRgb.Length != leds * 3) LastRgb = new byte[leds * 3];

        for (int i = 0; i < leds; i++)
        {
            int k = i * 3;
            float r = _state[k], g = _state[k + 1], b = _state[k + 2];
            float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = y + (r - y) * sat; g = y + (g - y) * sat; b = y + (b - y) * sat;
            c[0] = Math.Clamp(r, 0f, 1f) * bright;
            c[1] = Math.Clamp(g, 0f, 1f) * bright;
            c[2] = Math.Clamp(b, 0f, 1f) * bright;
            for (int j = 0; j < 3; j++)
                v[j] = (byte)Math.Clamp((int)(MathF.Pow(c[j], invG) * 255f + 0.5f), 0, 255);
            if (Math.Max(v[0], Math.Max(v[1], v[2])) <= s.BlackCut) { v[0] = v[1] = v[2] = 0; }
            LastRgb[k] = v[0]; LastRgb[k + 1] = v[1]; LastRgb[k + 2] = v[2];
            wire[k] = v[o0]; wire[k + 1] = v[o1]; wire[k + 2] = v[o2];
        }
    }
}

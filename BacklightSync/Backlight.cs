using System.Diagnostics;
using HidSharp;

namespace BacklightSync;

/// <summary>
/// Talks to the 57-LED backlight over USB HID (VID 1A86, PID FE07, interface 0).
/// Packet format was taken from the SyncLight app's own code:
///   "SC" | len(2, big-endian) | sid | 0x80 | { first, R, G, B, last }* | checksum
/// where first/last are 1-based LED numbers of a range that gets one colour.
/// Packets are cut into 64-byte HID reports (report id 0).
/// </summary>
public sealed class Backlight : IDisposable
{
    public const int Vid = 0x1A86, Pid = 0xFE07;

    HidDevice? _dev;
    HidStream? _stream;
    byte _sid = 1;
    int _reportLen = 65;
    readonly byte[] _scratch = new byte[2048];

    public bool IsOpen => _stream != null;
    public string Info { get; private set; } = "";
    public int LedCountFromDevice { get; private set; }

    public bool Open()
    {
        Close();
        try
        {
            var candidates = DeviceList.Local.GetHidDevices(Vid, Pid).ToList();
            // interface 0 has "mi_00" in its path; fall back to anything that can take output reports
            _dev = candidates.FirstOrDefault(d => d.DevicePath.Contains("mi_00", StringComparison.OrdinalIgnoreCase) && d.GetMaxOutputReportLength() > 0)
                   ?? candidates.FirstOrDefault(d => d.GetMaxOutputReportLength() > 0);
            if (_dev == null) return false;
            _reportLen = Math.Max(2, _dev.GetMaxOutputReportLength());
            if (!_dev.TryOpen(out _stream)) { _stream = null; return false; }
            _stream.WriteTimeout = 200;
            _stream.ReadTimeout = 300;
            ReadInfo();
            return true;
        }
        catch (Exception e)
        {
            Log.Write("open failed: " + e.Message);
            Close();
            return false;
        }
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null; _dev = null;
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------------ packets

    byte NextSid() { _sid = (byte)(_sid >= 254 ? 1 : _sid + 1); return _sid; }

    static byte Sum(byte[] b, int len) { int s = 0; for (int i = 0; i < len; i++) s += b[i]; return (byte)(s & 0xFF); }

    byte[] BuildRb(byte action, params byte[] payload)
    {
        int len = 6 + payload.Length;
        var p = new byte[len];
        p[0] = (byte)'R'; p[1] = (byte)'B'; p[2] = (byte)len; p[3] = NextSid(); p[4] = action;
        payload.CopyTo(p, 5);
        p[len - 1] = Sum(p, len - 1);
        return p;
    }

    /// <summary>rgb: 3 bytes per LED, already in the strip's wire colour order.</summary>
    byte[] BuildSync(ReadOnlySpan<byte> rgb, int leds)
    {
        // merge neighbouring LEDs with the same colour into one range -> fewer USB reports
        var tmp = _scratch;
        int o = 6;
        int i = 0;
        while (i < leds)
        {
            byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            int j = i;
            while (j + 1 < leds && rgb[(j + 1) * 3] == r && rgb[(j + 1) * 3 + 1] == g && rgb[(j + 1) * 3 + 2] == b) j++;
            tmp[o++] = (byte)(i + 1); tmp[o++] = r; tmp[o++] = g; tmp[o++] = b; tmp[o++] = (byte)(j + 1);
            i = j + 1;
        }
        int total = o + 1;
        var p = new byte[total];
        Array.Copy(tmp, p, o);
        p[0] = (byte)'S'; p[1] = (byte)'C';
        p[2] = (byte)(total >> 8); p[3] = (byte)(total & 0xFF);
        p[4] = NextSid(); p[5] = 0x80;
        p[total - 1] = Sum(p, total - 1);
        return p;
    }

    // ------------------------------------------------------------------ I/O

    /// <summary>Writes a packet as consecutive 64-byte reports. Returns false if the device is gone.</summary>
    bool Write(byte[] packet)
    {
        if (_stream == null) return false;
        var buf = new byte[_reportLen];
        try
        {
            for (int off = 0; off < packet.Length; off += 64)
            {
                Array.Clear(buf);
                int n = Math.Min(64, packet.Length - off);
                buf[0] = 0; // report id
                Array.Copy(packet, off, buf, 1, Math.Min(n, _reportLen - 1));
                _stream.Write(buf);
                if (off + 64 < packet.Length) SpinWaitMs(0.5); // the MCU drops back-to-back reports
            }
            return true;
        }
        catch (Exception e)
        {
            Log.Write("write failed: " + e.Message);
            Close();
            return false;
        }
    }

    static void SpinWaitMs(double ms)
    {
        long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(20);
    }

    /// <summary>Send one frame. rgb = 3 bytes per LED in wire order.</summary>
    public bool SendFrame(ReadOnlySpan<byte> rgb, int leds) => Write(BuildSync(rgb, leds));

    public bool SendAll(byte r, byte g, byte b, int leds)
    {
        var rgb = new byte[leds * 3];
        for (int i = 0; i < leds; i++) { rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b; }
        return SendFrame(rgb, leds);
    }

    public bool TurnOff() => Write(BuildRb(0x97));

    // ------------------------------------------------------------------ info

    void ReadInfo()
    {
        try
        {
            if (_stream == null) return;
            var junk = new byte[_reportLen + 64];
            _stream.ReadTimeout = 20;
            for (int k = 0; k < 64; k++) { try { if (_stream.Read(junk) <= 0) break; } catch (TimeoutException) { break; } }

            _stream.ReadTimeout = 400;
            if (!Write(BuildRb(0x82))) return;
            var rx = new byte[_reportLen + 64];
            int n = _stream.Read(rx);
            // The reply layout differs between firmware versions, so only report that the strip answered.
            Info = n > 0 ? "device replied" : "no reply";
        }
        catch (Exception e) { Info = "no reply (" + e.GetType().Name + ")"; }
        finally { try { if (_stream != null) _stream.ReadTimeout = 300; } catch { } }
    }
}

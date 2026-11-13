using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace BacklightSync;

public enum GrabResult { NewFrame, NoChange, Lost, Error }

/// <summary>
/// Fast screen grabber: DXGI Desktop Duplication, then the GPU shrinks the frame with a mip chain
/// so only a ~320x180 picture ever crosses to the CPU (instead of 14 MB per frame at 1440p).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    ID3D11Device _device = null!;
    ID3D11DeviceContext _ctx = null!;
    IDXGIOutputDuplication _dup = null!;
    ID3D11Texture2D? _work, _staging;
    ID3D11ShaderResourceView? _srv;
    int _srcW, _srcH, _level;
    Vortice.DXGI.Format _fmt;

    /// <summary>Small BGRA picture of the whole screen (row-major, 4 bytes/pixel, tightly packed).</summary>
    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ScreenWidth => _srcW;
    public int ScreenHeight => _srcH;
    public string DisplayName { get; private set; } = "";

    ScreenCapture() { }

    public static ScreenCapture? Create(string displayDevice)
    {
        var cap = new ScreenCapture();
        try
        {
            if (cap.Init(displayDevice)) return cap;
        }
        catch (Exception e) { Log.Write("capture init failed: " + e.Message); }
        cap.Dispose();
        return null;
    }

    bool Init(string displayDevice)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput? chosenOutput = null;

        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
            {
                var d = output.Description;
                if (!d.AttachedToDesktop) { output.Dispose(); continue; }
                bool wanted = displayDevice.Length > 0
                    ? string.Equals(d.DeviceName, displayDevice, StringComparison.OrdinalIgnoreCase)
                    : d.DesktopCoordinates.Left == 0 && d.DesktopCoordinates.Top == 0; // primary
                if (wanted && chosenOutput == null)
                {
                    chosenAdapter = adapter; chosenOutput = output;
                    DisplayName = d.DeviceName;
                }
                else output.Dispose();
            }
            if (chosenAdapter != adapter) adapter.Dispose();
        }
        if (chosenAdapter == null || chosenOutput == null)
        {
            // requested display not found: fall back to the first attached output
            if (displayDevice.Length > 0) return Init("");
            return false;
        }

        D3D11.D3D11CreateDevice(chosenAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device!, out _ctx!).CheckError();

        using var out1 = chosenOutput.QueryInterface<IDXGIOutput1>();
        _dup = out1.DuplicateOutput(_device);

        var dd = _dup.Description;
        _srcW = (int)dd.ModeDescription.Width;
        _srcH = (int)dd.ModeDescription.Height;
        _fmt = dd.ModeDescription.Format == Vortice.DXGI.Format.Unknown ? Vortice.DXGI.Format.B8G8R8A8_UNorm : dd.ModeDescription.Format;
        Log.Write("desktop format: " + _fmt);
        if (_fmt != Vortice.DXGI.Format.B8G8R8A8_UNorm)
            throw new NotSupportedException($"Screen format {_fmt} (Windows HDR?) is not supported yet. Turn HDR off in Windows display settings.");

        chosenOutput.Dispose();
        chosenAdapter.Dispose();
        BuildTextures();
        return true;
    }

    void BuildTextures()
    {
        _level = 0;
        while ((_srcW >> _level) > 400 && _level < 6) _level++;
        Width = Math.Max(1, _srcW >> _level);
        Height = Math.Max(1, _srcH >> _level);
        Pixels = new byte[Width * Height * 4];

        _work = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_srcW, Height = (uint)_srcH, MipLevels = (uint)(_level + 1), ArraySize = 1,
            Format = _fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _srv = _device.CreateShaderResourceView(_work);
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
            Format = _fmt, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    /// <summary>Waits up to timeoutMs for the screen to change and updates <see cref="Pixels"/>.</summary>
    public GrabResult Grab(int timeoutMs)
    {
        IDXGIResource? res = null;
        try
        {
            var r = _dup.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out res);
            if (r.Failure)
            {
                if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return GrabResult.NoChange;
                if (r.Code == Vortice.DXGI.ResultCode.AccessLost.Code || r.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code
                    || r.Code == Vortice.DXGI.ResultCode.DeviceReset.Code) return GrabResult.Lost;
                Log.Write("AcquireNextFrame failed: 0x" + r.Code.ToString("X"));
                return GrabResult.Error;
            }
            // Only the mouse pointer moved: nothing new to show.
            if (info.LastPresentTime == 0)
            {
                res?.Dispose(); res = null;
                _dup.ReleaseFrame();
                return GrabResult.NoChange;
            }

            using (var tex = res!.QueryInterface<ID3D11Texture2D>())
            {
                _ctx.CopySubresourceRegion(_work!, 0, 0, 0, 0, tex, 0, null);
            }
            res.Dispose(); res = null;
            _dup.ReleaseFrame();

            _ctx.GenerateMips(_srv!);
            _ctx.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _work!, (uint)_level, null);

            var map = _ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = Width * 4;
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, Pixels, y * rowBytes, rowBytes);
            }
            finally { _ctx.Unmap(_staging!, 0); }
            return GrabResult.NewFrame;
        }
        catch (Exception e)
        {
            res?.Dispose();
            Log.Write("grab exception: " + e.Message);
            return e.Message.Contains("ACCESS_LOST", StringComparison.OrdinalIgnoreCase) ? GrabResult.Lost : GrabResult.Error;
        }
    }

    public void Dispose()
    {
        _srv?.Dispose(); _work?.Dispose(); _staging?.Dispose();
        _dup?.Dispose(); _ctx?.Dispose(); _device?.Dispose();
        _srv = null; _work = null; _staging = null;
    }
}

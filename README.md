# Backlight Sync

A fast, lightweight Windows app for USB monitor backlight strips that ship with the **SyncLight** app
(generic "Backlight Strip for Monitor" kits, USB HID `1A86:FE07`). It replaces the vendor app with something
quicker, steadier and smarter about movies.

## Features

- **Movie sync** – follows the colours at the edges of your screen.
  - Screen is captured on the GPU (DXGI Desktop Duplication) and shrunk there, so only a ~320×180 picture reaches the CPU.
  - Smoothing adapts: calm while the picture is steady, fast on scene cuts.
- **Black bar detection** – letterbox, 4:3 pillarbox and both together. Lights follow the real picture, and it is
  robust against subtitles inside the bars, dark scenes and fades to black.
- **Night light** – a steady, soft golden glow with its own brightness and warmth (Kelvin) controls.
- **Dark settings window** with a live preview of what each LED is showing, a tray icon, and the global hotkey
  **Ctrl + Alt + B** to cycle Movie sync → Night light → Off.
- Pauses when the PC locks or sleeps, optional start with Windows.

## Build and run

Requires Windows 10/11 and the .NET 8 SDK (runtime to run).

```
cd BacklightSync
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ../App
```

Close the vendor SyncLight app first – two programs can't drive the strip at the same time.
Windows HDR must be off (the screen capture expects an 8-bit desktop).

Settings live in `%APPDATA%\BacklightSync\settings.json`, logs in `%APPDATA%\BacklightSync\log.txt`.

### Command line tools

`BacklightSync.exe --probe | --pattern | --night | --off | --grab | --selftest`
(`--selftest` runs the black bar detector against synthetic frames.)

## Strip layout

Defaults match a 57-LED strip on three edges: 15 left, 27 top, 15 right. Use **Strip → Show test pattern**
(left edge red, top green, right blue, white LED at the bottom of each side) and the *clockwise* switch to match
your wiring.

## Protocol notes

Frames are sent as `"SC" | length (2 bytes, big-endian) | id | 0x80 | { first LED, R, G, B, last LED }… | checksum`
(checksum = sum of all previous bytes mod 256), split into 64-byte HID reports. This follows the packet format used
by the vendor app and documented by the community project [SyncRGB](https://github.com/Tonic-Jin/SyncRGB).

This project is not affiliated with the makers of the strip or the SyncLight app.

# MonitorDeck

A lightweight Windows 11 tray application for multi-monitor brightness and contrast controls.

**Current source version: v1.0.25 — experimental / personal-project preview.** This project is under active development and is not a Microsoft Store application or a signed release.

## Features

- Detect active displays across NVIDIA, AMD and other Windows graphics adapters; show monitor names even when DDC/CI control fails.
- Adjust **hardware brightness and contrast** on monitors that respond to DDC/CI.
- Use per-monitor **software dimming** (0–100%) when hardware brightness isn't available, or optionally alongside it.
- Keep a small, always-on-top tray popup for quick adjustments, plus a full diagnostics view.
- Automatically rescan and attempt overlay recovery after display changes.
- Save per-user software-dimming values under `%LOCALAPPDATA%\MonitorDeck\`.
- Control Windows Night Light state and strength (experimental, undocumented Windows internals).
- Optional all-users startup installation with UAC approval.
- Capture-exclusion support for software overlays, subject to Windows capture behavior.

## Build on Windows

Requires **Windows 11**, **.NET 10 SDK**, and a Windows x64 environment for the supplied build script.

1. Clone or download the repository.
2. Run `Build-Release.cmd` from the repository root.
3. Launch `release\MonitorDeck.exe`.

The build produces a **self-contained Windows x64 executable**. Because it bundles .NET and WPF runtime components, it can be substantially larger than the source code.

For development, run `Run-Dev.cmd`. You can also build from a Windows terminal:

```powershell
dotnet publish .\MonitorDeck\MonitorDeck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release
```

### GitHub Actions

The workflow in `.github/workflows/windows-build.yml` compiles the project on a Windows runner and uploads an artifact named `MonitorDeck-windows-x64`. Download it from the **Actions** tab after a successful workflow run. To publish an experimental **GitHub Release**, push a tag such as `v1.0.25`. The separate `windows-release.yml` workflow builds and attaches `MonitorDeck-windows-x64.zip` to a prerelease.

## Usage

Left-click the tray icon to toggle the compact popup. Use the popup to adjust each monitor and Windows Night Light, and the full view for diagnostics.

**Clear dimming** removes every software overlay (sets it to 100%)

The global emergency shortcut **Ctrl + Alt + Shift + F12** clears software dimming without changing hardware values, provided the hotkey is registered successfully. `Reset-Dimming.cmd` is included as a recovery aid. Be careful when setting a display to **0%** software brightness because it can become completely black.

## Known limitations and caveats

- DDC/CI support depends on the **monitor, GPU, cable and adapter**. Display detection does not guarantee hardware brightness/contrast control.
- Some monitors acknowledge DDC/CI contrast writes without visibly applying them.
- Translucent overlay dimming may not cover Windows Start, taskbar, some protected video surfaces or exclusive fullscreen modes. A watchdog cannot overcome every case.
- Screenshot and OBS capture exclusion varies with Windows and capture method. Test this on your own setup.
- **Night Light uses undocumented CloudStore settings**, which may change between Windows releases. Strength changes may need the **Apply now** action to become visible.
- All-users startup changes `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` with administrator approval. It is optional.
- The Windows executable is currently **unsigned**. Smart App Control or other Windows security features may block unknown builds; do not disable system-wide protections just to run this software.

See [v1.0.25 testing notes](Docs/v1.0.25-testing-notes.md) for details of the overlay watchdog.

## Privacy

MonitorDeck is designed to run locally. The project does not intentionally transmit monitor readings, Night Light settings or user data to a remote service. Diagnostic logs and user-specific software-dimming preferences are stored under `%LOCALAPPDATA%\MonitorDeck` and are **not included in this repository**. Review code and Windows permissions before running.

## License

No software license has been selected yet. All rights are reserved by default until the repository owner adds an explicit license (for example, MIT). Publicly readable source code is not automatically open-source licensed.

# HP Victus Control

A tiny, low-overhead replacement for the fan/performance parts of Omen Gaming Hub — built
for an HP Victus 15, should also work on Omen laptops with the same BIOS interface.

Talks directly to the same BIOS WMI provider (`root\wmi`, class `hpqBIntM`) that HP's own
software uses. No kernel driver, no background service, no telemetry — just a WPF app with a
tray icon that polls the BIOS every 2 seconds.

## Features

- Live temperature + fan speed readout (tray tooltip and main window)
- Manual fan speed per fan (best-effort — see note below)
- Force max fan speed
- Performance mode switch: Balanced / Performance / Cool
- Per-game profiles: performance mode, refresh rate, resolution, FPS cap, max fan, game boost
  (CPU priority, OneDrive paused) and which GPU the game runs on — applied when the game starts,
  put back when it closes; optional Wi-Fi tuning (background scanning off) while any game runs
- Configurable global shortcuts for max fan and cycling the performance mode
- Keeps Windows' power plan in step with the selected mode
- Driver & BIOS updates from HP, NVIDIA and Intel in a sortable table, with a battery/charger
  safety check before BIOS installs and automatic cleanup of downloaded installers
- System page: laptop details, battery health, BIOS graphics switch (where the model has one),
  fan test, shader cache cleanup

## Requirements

- Windows 10/11, x64
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build
- Needs **administrator** rights for the BIOS interface — the app asks for them itself (UAC
  prompt) when you open it; "Start with Windows" gets them without a prompt via a scheduled task

## Build & run

```powershell
dotnet build "HpVictusControl.sln" -c Release
dotnet run --project "src\HpVictusControl\HpVictusControl.csproj"
```

Or open `HpVictusControl.sln` in Visual Studio and hit Run — the app relaunches itself as
administrator, so Windows shows a UAC prompt. To debug, run Visual Studio as administrator so the
app doesn't hand off to a separate process.

## How it works

HP does not publish this interface. The command layout used here (command codes, data
structure, and the `hpqBIntM`/`hpqBDataIn` WMI classes) matches what's been reverse-engineered
and documented by the community — most notably the [OmenMon](https://github.com/OmenMon/OmenMon)
project, which this app used as a reference for which BIOS command bytes correspond to which
function. The BIOS/WMI calls used here are separate from and much narrower in scope than
OmenMon (no keyboard backlight, no CPU/GPU power tables, no direct Embedded Controller access —
just fan level, max-fan, performance mode, and the single BIOS temperature sensor).

**Fan level unit**: the BIOS reports/accepts fan speed as a single byte per fan. Empirically
this tracks roughly "hundreds of RPM" (e.g. a value of 45 ≈ 4500 RPM) — the app displays it
that way, but treat it as approximate since HP hasn't documented the exact scale.

**Manual fan speed caveat**: setting a fan level is a direct, best-effort BIOS call. On some
BIOS versions the firmware's own automatic thermal curve will override a manual value again
after a few seconds; there's no publicly documented way from WMI alone to durably disable that
loop (doing so requires talking to the Embedded Controller directly through a kernel driver,
which this project intentionally avoids for simplicity and safety). If manual speed doesn't
stick on your model, "Force max fan speed" and the performance-mode switch are the reliable
controls.

**FPS cap**: the per-game frame limit is not enforced by this app — it's written into the NVIDIA
driver's own profile database through `nvapi64.dll` (setting `0x10835002`, the same "Max Frame
Rate" NVIDIA Control Panel writes), so the driver does the limiting and the cap survives with or
without this app running. It only applies to games rendering on the NVIDIA GPU. The setting id is
read back from the driver via `NvAPI_DRS_EnumAvailableSettingIds` rather than hard-coded on faith;
measured on an RTX 3050 laptop GPU, an uncapped 1900 fps test render drops to exactly 30.0 fps at
a 30 fps cap.

**No "current mode" readout**: the BIOS interface exposes a way to *set* the performance mode
but not to read back which one is currently active, so the UI's mode selector just defaults to
Balanced on launch rather than reflecting the laptop's actual state.

## Disclaimer

This adjusts firmware-level hardware behavior on a best-effort basis via an undocumented
interface. It's provided as-is, with no warranty — use at your own risk.

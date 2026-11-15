# How MSI Center M is put together

What the OEM software does on this device, so Tsume can run beside it today and
replace it later. Read from the decompiled assemblies of MSI Center M
3.0.2610.0701; nothing here was executed.

## Processes

| Process | Role | Started by |
|---|---|---|
| `MSI_Center_M_Server.exe` | Supervisor, elevated. Spawns and respawns everything below. | Scheduled task `MSI_Center_M_Server` (logon + boot) |
| `MSI_Center_M_Launcher.exe` | Listens for the hardware buttons; activates the Game Bar widget. | Server |
| `MSI_Center_M_Server_<Feature>.exe` | One host per feature (UserScenario, ControlMode, Battery, MysticLight, AiEngine, …). | Server, respawned 1 s after exit |
| `Command Center.exe` | Elevated backend for the Quick Settings widget. | Server |
| `Gamebar_Widget.exe` | The Quick Settings widget itself, hosted by Xbox Game Bar. | Game Bar |
| `MSIAPService.exe` | "MSI Foundation Service", LocalSystem. Proxies registry and WMI writes. | Service manager |

Stopping a feature host while Server is alive is pointless: it returns in a second.

## The hardware buttons

Both are detected through the WMI event `SELECT * FROM MSI_Event` (low byte of `MSIEvt`):

| Code | Press | What MSI does |
|---|---|---|
| 41 `0x29` | MSI Center M, short | Opens the main app (`msi-mcm:` protocol) |
| 49 `0x31` | MSI Center M, long | Vibrates. The action itself is done by firmware. |
| 88 `0x58` | Quick Settings, short | Activates its Game Bar widget (below) |
| 42 `0x2A` | Quick Settings, long | Vibrates. Firmware sends Win+Tab. |

The event is a broadcast — Tsume can subscribe alongside MSI.

**On this model the firmware types Win+G by itself.** MSI's Launcher checks
`IsBiosVersionSending_Win_And_G()`, which is always true for MS-1T91, and then
only tells Game Bar *which widget* to bring forward:

```
ms-gamebar://launch/activate/9426MICRO-STARINTERNATION.MSIQuickSettings_kzh8wxbdkxb8p_App_MSICommon/
```

Consequence: stopping MSI Center M does not stop Game Bar from opening on the
Quick Settings button. Only a keyboard hook that swallows Win+G does.

## Settings Tsume can use to steer it (side-by-side mode)

All under `HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M`. MSI watches these values
and reacts on its own.

| Key | Value | Effect |
|---|---|---|
| `Component\User Scenario` | `Mode` = 1 Performance · 2 Balanced · 3 Super Battery · 4 Manual · 5 AI | Applied about 2 s after the write |
| `Component\User Scenario` | `ManualPL1AC` `ManualPL2AC` `ManualPL1DC` `ManualPL2DC` | Applied about 0.5 s after the write, in Manual |
| `Component\User Scenario` | `Fan` = 1 auto · 2 cooler boost · 3 advanced | Read on the next mode change |
| `Component\User Scenario` | `AutoSwitchMode` = 0 | Stops MSI flipping the controller between gamepad and desktop mode when the foreground app changes |
| `Battery` | `BatteryLevel` = 0 (100 %) · 1 (80 %) · 2 (60 %) | Must match EC `0xD7` or MSI rewrites the EC |
| `OsdEditor` | `LightingBrightness` = 0 / 1 | LEDs off / on |

Battery charge-limit bytes MSI writes to `0xD7`: `0xE4` (100 %), `0xD0` (80 %), `0xBC` (60 %).

## What will overwrite another app's changes

MSI has no free-running timer that re-asserts EC values. It reacts to events:

| Trigger | What it rewrites |
|---|---|
| Resume from sleep | Whole scenario: shift mode, fans, power limits, Windows power mode |
| AC ↔ battery | FPS limit |
| Its registry `Mode` / `ManualPL*` changing | Shift mode, fans, power limits |
| Foreground window changing (AI Engine) | **Controller mode** — XInput ↔ Desktop. Suppressed by `AutoSwitchMode` = 0. |
| Battery host starting | EC `0xD7` forced back to `BatteryLevel` |
| Controller reporting a mode change | Stick deadzones, then a flash save |
| A display being plugged in | Forces external-only display topology |

It also caches the last power limits it wrote and skips writes that match the
cache, so it does not notice when someone else changes them.

## Taking over (milestone 6)

Clean, reversible, nothing uninstalled:

1. Disable scheduled tasks `MSI_Center_M_Server` and `MSI_Center_M_Updater`.
2. Stop `MSI_Center_M_Server.exe`; it takes its hosts and the Launcher with it.
3. Optionally stop the `MSI Foundation Service`. It is passive once running.

Server re-creates its own tasks only when it runs, so step 1 holds.

## Not yet understood

These Quick Settings tiles live in binaries not yet decompiled
(`Command Center.exe`, the widget, `MSI_Center_M_Server_OsdEditor.exe`):
refresh rate, FPS limiter, XeSS frame generation, "Embedded controller", free
up memory, end task, screenshot, record.

What is known: the 30 FPS cap MSI applies on battery in Super Battery mode uses
`intelGEDll.dll` → `setEGControlMode(2, 2)` (on) / `(0, 2)` (off) — presumably
Intel Endurance Gaming.

## A security note about the OEM service

`\\.\pipe\MSI_SERVICE_2` accepts connections from any signed-in user and will
write the registry, call firmware methods, and launch processes as SYSTEM, with
no real caller check. Tsume does not use it. It is the reason Tsume's own helper
takes typed, range-checked requests instead of raw register writes.

# Firmware interface (ACPI-WMI)

How Tsume changes performance mode, power limits, fans and battery behavior.
Everything goes through one WMI class that the BIOS exposes.

Sources, in order of trust:

1. **MSI Center M on this device**, decompiled (`MSIWMIACPI2.dll`, `API_UserScenario.dll`). The best record of what MSI does on the Claw 8 EX; what the firmware does is confirmed only by the on-device checks below.
2. **HandheldCompanion** (`ClawA1M.cs`, `ClawCG3EM.cs`) — has an explicit MS-1T91 entry and agrees with (1).
3. **Linux** `msi-wmi-platform` and the Claw patch series, **hhd**, **ClawTweaks** — agree on addresses; used for limits and warnings.

## This device

| | |
|---|---|
| Model | Claw 8 EX AI+ CG3EM |
| Board | MS-1T91 |
| BIOS | E1T91IMS.10E |
| EC firmware | 1T91EMS1.103 (2026-09-02) |
| WMI interface | version 2.9 |
| Controller firmware | 4.19 (read 2026-11-10, [controller-read-2026-11-10.txt](controller-read-2026-11-10.txt)) |

## The door: `MSI_ACPI`

```
namespace   root\WMI
instance    MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'
argument    Data : Package_32 { Bytes : uint8[32] }
```

```
request    [0] selector    [1..31] payload
response   [0] status      [1..31] data          status 0 = refused
```

**Needs administrator rights.** An unelevated call fails with "Access denied",
which is why Tsume has a helper process.

For `Get_Data` / `Set_Data` the selector is an embedded-controller register
address and the payload is one byte.

`Get_WMI` reports the interface version in data bytes 0 (major) and 1 (minor)
(DSDT `MAJR` / `MINR`; this device reads `02 09`). MSI Center M uses the
`Get_Data` / `Set_Data` register path only when the major version is 2
(`isNew_EC = WmiMajorVersion > 1`, `UserScenarioX.cs:278`). On every board
except MS-1T91 (the only one checked on a real unit), Tsume asks `Get_WMI`
before its first write and refuses all writes unless the major version is 2.
A definite answer is kept for as long as the helper runs; a failed call is
asked again on the next write. Reads are never restricted.

## Registers

| Addr | Name | Meaning |
|---|---|---|
| `0x50` | Sustained power limit | PL1 in watts |
| `0x51` | Boost power limit | PL2 in watts |
| `0x68` | CPU temperature | °C, read-only. Same value as `Get_Temperature(0)` byte 0. |
| `0x69`–`0x70` | CPU fan curve, temperature axis | mirrors `Get_Temperature(1)` |
| `0x71` | Live fan duty | read-only — *by convention on MSI ECs; read 56 while the fans turned at ~3400 rpm* |
| `0x72`–`0x77` | CPU fan curve, duties | the six points MSI rewrites. `Get_Fan(1)` mirrors `0x71`–`0x78` (byte 0 = `0x71`, byte 7 = `0x78`) |
| `0x98` | Cooler boost | bit 7 = fans at full speed |
| `0xA0`–`0xBB` | EC firmware string | `1T91EMS1.103` + build date and time |
| `0xC8`–`0xCB` | Fan tachometers | two big-endian periods; mirrors `Get_Fan(0)` |
| `0xD2` | Shift mode | see below |
| `0xD4` | Fan control | bit 7 = follow the custom curve. Reads `0x0D` in automatic. |
| `0xD7` | Charge limit | bit 7 = limit enabled, bits 0–6 = percent (60–100). Reads `0xE4` = 100 %. |

### Shift mode (`0xD2`)

```
bit 7      supported      always 1
bit 6      active         always 1
bits 5–0   mode
```

| Tsume mode | MSI name | Low bits | Register |
|---|---|---|---|
| Performance | Sport | 4 | `0xC4` |
| Balanced | Green | 1 | `0xC1` |
| Endurance | ECO ("Super Battery") | 2 | `0xC2` |
| Manual | Manual | 6 | `0xC6` — **EX only** (plus any board MSI Center M does not name, e.g. MS-1T7K, which falls into its 1T91 branch; Tsume does not support MS-1T7K yet). MS-1T41/1T42/1T52/1T8K use 4 (`0xC4`, same as Sport); 3 (`User`) is never written |
| — | Comfort | 0 | `0xC0` — never used on the Claw |

### Power limits

MSI's presets for MS-1T91, identical on AC and battery:

| Mode | Sustained | Boost |
|---|---|---|
| Endurance | 15 W | 20 W |
| Balanced | 25 W | 37 W |
| Performance | 30 W | 37 W |
| Manual | 8 – 35 W | 10 – 45 W, at least 2 W above sustained |

MSI's own floor is 8 W and its ceiling for boost is 45 W. hhd independently
lists 8–35 W sustained and 9–45 W boost for this board.

**Write order** (MSI's, and Tsume's): sustained → 8 W, then boost, then
sustained. Boost is therefore never below sustained mid-change.

Alongside the EC write, MSI also sets the Windows power mode overlay
(Best performance / Balanced / Best power efficiency). Tsume's app does the
same; it needs no elevation.

## Fans

| Call | Meaning |
|---|---|
| `Get_Fan(0)` | Live speed. Data bytes 0–1 are fan 1's tachometer period and bytes 2–3 are fan 2's, big-endian. `RPM = 480000 / period`. A period of 0 means stopped. |
| `Get_Fan(1)` / `Set_Fan(1)` | CPU fan curve — six duty points in data bytes 1–6 |
| `Get_Fan(2)` / `Set_Fan(2)` | GPU fan curve, same layout |
| `Get_Temperature(1)` / `(2)` | Temperature axis for those curves — data bytes 0 and 3–7 |
| `Get_AP(1)` | byte 0 mirrors register `0xD4` |

To enable a custom curve MSI writes the six duties, reads `Get_AP(1)[0]`, sets
bit 7 and writes the result to `0xD4`. To return to automatic it clears bit 7.

Rules for the custom curve (milestone 4):

- Duty is a raw byte, not a percentage. MSI's slider maximum is per model: 150 on MS-1T41/1T42/1T52, 130 on MS-1T8K, 100 on MS-1T91 (`UC_UserScenario*.xaml`, `uxSlider1`–`12`). 75 is the top of MSI's "Advanced" range. The physical maximum has not been measured on this device.
- Preserve data byte 0 (live duty) and byte 7 (the EC's ceiling) when writing.
- **Never write the temperature axis on the EX.** ClawTweaks reports that doing so zeroed the EX's thermal ceiling.
- This device's factory curve: temperatures `47 50 57 64 71 78` °C, duties `60 64 68 74 80 84`.

## Battery

Charge limit lives in `0xD7`: read it, keep bit 7 semantics, write
`0x80 | percent`. Accepted values are 60–100 in steps the firmware chooses.
MSI Center M offers 60, 80 and 100 (`0xBC`, `0xD0`, `0xE4`), and changes the
byte only when bit 7 already reads set — it treats bit 7 as a capability bit
and otherwise writes the byte back unchanged (`API_Battery\ComponentLoader.cs:366`). On boards
other than MS-1T91, Tsume makes the same check first: if bit 7 reads clear, it
refuses the charge limit and writes nothing. Tsume offers 80,
90 and 100; 90 (`0xDA`) was written and read back half a minute later on
2026-11-14 (0.6.2), so the firmware keeps levels MSI's software never uses.

**Written and read back on this device (2026-11-14, Tsume 0.6.1, through the
installed helper, MSI Center M paused):** `0xE4` over itself, then `0xD0`
(80 %), then `0xE4` again — each accepted and read back as written.

With the limit at 80 %, the battery full and the charger in, Windows reported
the battery neither charging nor discharging (rate 0 both ways) for the minute
it was watched, with the processor busy. So above the limit the charger runs
the machine and the battery rests; it is not drained down to the limit. There
is no separate "bypass" control known. Not yet watched: a charge stopping at
the limit on the way up, and where charging starts again (the Linux `msi-ec`
driver says ten points below the limit).

## Hardware buttons

The two right-side buttons are **not** part of the gamepad. They surface two ways:

**1. WMI event** — `SELECT * FROM MSI_Event`, property `MSIEvt`, low byte:

| Code | Button |
|---|---|
| `0x29` | MSI Center M button |
| `0x31` | MSI Center M button, long press |
| `0x58` | Quick Settings, short press |
| `0x2A` | Quick Settings, long press |
| `0x32` / `0x21` | Volume up / down — *unsourced: not in the DSDT, MSI's code or the captures from this device* |

The first four are MSI's own names (`MSI_Center_M_Launcher\App.cs:25-31`).

Only presses are reported, never releases.

Observed on this unit (2026-11-10), subscribing **without** elevation: the M
button raises `0x220029` and Quick Settings raises `0x220058`. The long-press
codes have not been seen yet.

**2. Keystrokes from the EC's PS/2 keyboard.** On current BIOSes the firmware
itself types **Win+G** for a short Quick Settings press and **Win+Tab** for a
long one. That — not anything MSI Center M does — is what opens Game Bar.
Older firmware sends F15 / F16 instead, and the EX swaps which is which.

So to own the Quick Settings button, Tsume must swallow Win+G with a low-level
keyboard hook (HandheldCompanion does this: suppress G while Win is held, then
inject a dummy key so Start and Game Bar stay shut).

## Deliberately out of scope

- **OverBoost.** A UEFI variable (`MsiDCVarData`), not an EC register, and it needs a reboot. Tsume will not write UEFI variables.
- **`Set_BIOS`, `Set_SMBUS`, `Set_Debug`, `Set_PE`, `SetPackage`.** No reason to touch them.

## Living with MSI Center M

While MSI Center M runs it re-applies its own idea of the mode on resume, on
AC/battery changes, and whenever its registry settings change. ClawTweaks also
reports direct EC writes being *refused* while MSI holds the interface.

Tsume's helper does the same job only while MSI Center M is not running: on
start-up, wake, and the screen turning on or off it compares the registers with
the chosen mode and rewrites them on a mismatch; on a board with a battery cap
(MS-1T41), also on a switch between AC and battery.

Two ways to coexist, both planned:

- **Side by side:** steer MSI Center M instead of the EC, by writing the registry values it watches (`HKLM\SOFTWARE\WOW6432Node\MSI\MSI Center M\Component\User Scenario` → `Mode`, `ManualPL1AC`, …).
- **Takeover:** stop the `MSI_Center_M_Server` scheduled task and the `MSI Foundation Service`, then write the EC directly.

## Verified on this device

The read-only probe ran twice on 2026-11-10 with MSI Center M running in Manual
mode. Full output: [probe-2026-11-10.txt](probe-2026-11-10.txt).

| Check | Probe read | Expected from MSI's own records | |
|---|---|---|---|
| WMI interface version | 2.9 | 2.9 | ✔ |
| EC firmware string | `1T91EMS1.103…` | same | ✔ |
| Shift mode `0xD2` | `0xC6` | Manual on the EX | ✔ |
| Sustained / boost `0x50` `0x51` | 33 W / 36 W | `ManualPL1AC` 33 · `ManualPL2AC` 36 | ✔ |
| Fan control `0xD4` | `0x0D`, bit 7 clear | automatic | ✔ |
| Cooler boost `0x98` | `0x06`, bit 7 clear | off | ✔ |
| Charge limit `0xD7` | `0xE4` | 100 % (`BatteryLevel` 0) | ✔ |
| `Get_AP(0)` byte 2 / `Get_AP(1)` byte 0 | `0xC6` / `0x0D` | mirror `0xD2` / `0xD4` | ✔ |
| Fan curve duties `Get_Fan(1)` | 60 64 68 74 80 84 | `Default_Fan` | ✔ |
| Fan curve temperatures `Get_Temperature(1)` | 47 50 57 64 71 78 | `Default_Temp` | ✔ |
| CPU temperature | 61 °C then 59 °C, identical by two routes | plausible | ✔ |
| Fan speed `Get_Fan(0)` | 0 on the first run; ~3450 and ~3430 rpm on the second | plausible; tach bytes at `0xC8` agree | ✔ |

So the call format, the meaning of the selector, and every address Tsume uses
are confirmed **for reading**.

### Writing

A no-op write check ran on 2026-11-10 with MSI Center M paused: each register
was rewritten with its own value and read back. Full output:
[write-check-2026-11-10.txt](write-check-2026-11-10.txt).

| Register | Value | `Set_Data` | Read back |
|---|---|---|---|
| `0xD2` shift mode | `0xC6` | accepted | `0xC6` |
| `0x50` sustained limit | 33 | accepted | 33 |
| `0x51` boost limit | 36 | accepted | 36 |
| `0x98` cooler boost | `0x06` | accepted | `0x06` |
| `0xD4` fan control | `0x0D` | accepted | `0x0D` |

So the firmware accepts `Set_Data` from Tsume on every register it uses.

### Changing a value

The mode check (`tools\mode-check.ps1`) ran on 2026-11-10 with MSI Center M
paused. It applied two of MSI's stock presets through `ClawHardware` (now `MsiDevice`), read the
registers back after eight seconds of full load, and restored the starting
state. Full output: [mode-check-2026-11-10.txt](mode-check-2026-11-10.txt).

| Step | `0xD2` | Sustained | Boost | Held | Package power under load |
|---|---|---|---|---|---|
| As found (Manual) | `0xC6` | 33 W | 36 W | | 7.2 W |
| Balanced | `0xC1` | 25 W | 37 W | ✔ | 7.6 W |
| Endurance | `0xC2` | 15 W | 20 W | ✔ | 7.3 W |
| Restored | `0xC6` | 33 W | 36 W | ✔ | 7.2 W |

So new values are accepted, stay put (nothing wrote the old ones back), and
the starting state can be restored byte for byte.

**What it did not show** is the processor obeying the limits, because the
processor never came near them: with every core busy the package drew 7–10 W,
below even Endurance's 15 W. The cause is in Windows, not the firmware — the
active power plan (Balanced) has *Processor performance boost mode* set to
**Disabled** on both AC and battery (Windows' default for the plan is
Aggressive), so the chip stays at its base clock (~1.5 GHz measured, 95 % of
nominal). Who set it is unknown: not Tsume, and nothing in the ClawLab folders
mentions it. With boost off, no limit above ~10 W can be seen to bite.

**Second run, boost on.** The owner set boost mode back to Aggressive and the
check ran again the same day. Full output:
[mode-check-2026-11-10-boost-on.txt](mode-check-2026-11-10-boost-on.txt).

| Step | Sustained | Boost | Held | Package power under load |
|---|---|---|---|---|
| As found (Manual) | 33 W | 36 W | | 32.7 W |
| Balanced | 25 W | 37 W | ✔ | 34.2 W |
| Endurance | 15 W | 20 W | ✔ | **20.0 W** |
| Restored (Manual) | 33 W | 36 W | ✔ | 35.7 W |

Endurance settles the question: the package sat on its 20 W boost ceiling to
the tenth of a watt, and went straight back to ~36 W when Manual was restored.
The limits written to `0x50` / `0x51` are the ones the processor obeys.

Balanced read 34 W, not 25 W, because each measurement is only six seconds
long and the chip is allowed to burst to the boost limit (37 W) for longer
than that. The *sustained* limit taking over after the burst window has
therefore not been watched yet; it needs a load of a minute or more.

Package power comes from Windows' energy meter
(`Win32_PerfRawData_PowerMeterCounter_EnergyMeter`, instance
`RAPL_Package0_PKG`): `Energy` is in picowatt-hours, `Time` in milliseconds,
and it can be read without elevation.

| Still open | |
|---|---|
| Does package power follow the limits? | boost limit: yes (20.0 W under Endurance). Sustained limit after the burst window: not yet watched |
| Are writes refused while MSI Center M runs? | unknown — every check ran with it paused |
| Unelevated calls refused | yes — hence the helper |

## Claw 8 AI+ (A2VM, MS-1T52) & Claw 7 AI+ / A7 (A2VM, MS-1T42 — Intel Core Ultra 7 258V / Lunar Lake)

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `MSIWMIACPI2.dll`, `UC_UserScenario_1T421T52`), official `E1T52IMS.114` BIOS ACPI AML, and embedded ENE EC firmware `1T52EMS1.109` (no `MS-1T42` BIOS image is available). MSI Center M's power, shift, fan and battery code treats `MS-1T42` and `MS-1T52` identically. They differ in the default `M2` mapping (`API_ControlMode\MSIGamePad.cs:400,436`: only `_1T52` with firmware `>= 2.3` gets `KB_LCTRL,KB_1`), `API.Is8inchModel()` (`1T52` only, `SharedData\API.cs:1056-1063`), and the OSD resolution table. That `MS-1T42` has the same WMI and EC behavior as `MS-1T52` is inferred from MSI's code, not read from its firmware.

Differences from the Claw 8 EX (`MS-1T91`):

- **Board models:** `MS-1T52` (8" 1200p) and `MS-1T42` (7" 1080p). Same `MSI_ACPI` WMI bridge (`ACPI\PNP0C14\0_0`, `Package_32`) and EC register map (`0x50`, `0x51`, `0x68`, `0x98`, `0xD2`, `0xD4`, `0xD7`) — read from the `MS-1T52` DSDT (WMI v2.8); for `MS-1T42` inferred.
- **Manual shift code (`0xD2`):** `0x04` (`0xC4`, shared with Sport/Performance), not `0x06` (`0xC6`, which is EX-only). Endurance (`0xC2`), Balanced (`0xC1`), and Performance (`0xC4`) match the EX. MSI Center M never runs `PL1 == 8` under `0xC4`: in Manual with `PL1 == 8` it switches to its Super Battery scenario, which writes `0xC2` (`UserScenarioX.cs:660-700, 1169-1192, 1385-1395`; `Shift.cs` encodes its `ECO` type as `+2`). Tsume follows it on these two boards: whenever the sustained limit it applies is 8 W (its Endurance preset, or Manual at 8 W), it writes `0xC2` instead of `0xC4` (`MsiBoard.SustainedShift`). Not yet checked on a real `MS-1T42`/`MS-1T52`.
- **Power limits (Core Ultra 7 258V / Lunar Lake):**
  - Ceiling is **8–30 W sustained (PL1)** and **9–37 W boost (PL2)**, floor `8 W`.
  - **PL2 headroom (`MinBoostHeadroom = 1`):** MSI Center M's sliders keep `PL2 >= PL1 + 1` (`m_PL1PL2Gap = 1`, `UC_UserScenario_1T421T52.xaml.cs:42,121-140`). MSI's reason is not in the source; a Lunar Lake `PL1 == PL2` throttling quirk is the assumed cause (inferred, not verified). Tsume enforces `PL2 >= PL1 + 1` in `PowerRange.Clamp` (helper) and in the Manual and Presets sliders.
  - MSI Center M stock (`UserScenarioX.cs:529-644`, same on AC and battery): Super Battery `8 W / ManualPL2AC` (default 37) on `0xC2`, Balanced `12 W / 37 W` on `0xC1`, Performance `30 W / 37 W` on `0xC4`.
  - Tsume presets: Endurance `8 W / 12 W`, Balanced `17 W / 20 W` (258V Processor Base Power sweet spot), Performance `30 W / 37 W` (full 258V PL1 / MTP), Manual default `17 W / 20 W`.
- **Hardware buttons & controller:** Same WMI event codes (`0x29`, `0x58`). Quick Settings triggers cover both `Win+G` (BIOS 10F+) and `F16` (older A2VM BIOSes). Rear-button (`M1`/`M2` at `186`/`355`) and lighting (`586`) profile offsets are the only layout in MSI Center M, which has no firmware-version check for it (`ProfileInfo.cs:83-108`); they match the EX by that code, not by read-back. A legacy layout does exist per the Linux kernel `hid-msi` driver (formerly `hid-msi-claw`), which keeps separate old/new address tables (RGB `0x01FA` old vs `0x024A` new; M1/M2 `0x007A`/`0x011F` old vs `0x00BB`/`0x0164` new). Tsume guards `2.00–2.16` (`< 0x0217`) off from writes; that cutoff is not confirmed by any source in this evidence set.

## Claw A1M (MS-1T41 — Intel Core Ultra 5 135H / Ultra 7 155H Meteor Lake)

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `UC_UserScenario.xaml`, `MSI Controller FW Updater_2608_3101.exe`). No Ultra 5 vs Ultra 7 power-limit branching exists in MSI Center M.

- **Board model & shift code:** `MS-1T41`. Same `MSI_ACPI` WMI bridge and EC register map (`0x50`, `0x51`, `0x68`, `0x98`, `0xD2`, `0xD4`, `0xD7`) — inferred: there is no `MS-1T41` BIOS image. MSI Center M writes `0x50`/`0x51` only when `WmiMajorVersion > 1` (`UserScenarioX.cs:278, 1286-1302`, `isNew_EC`); on WMI major 1 it skips them and sets the charge limit through MMIO `0xFC0008EF` instead (`API_Battery\ComponentLoader.cs:298-309, 407-428`). The A1M's WMI version is unknown. Manual shift code is `0x04` (`0xC4`).
- **Power limits:** Floor `15 W` (`LimitValue_PowerLow = 15`), PL2 driver cap `100 W`. MSI's Manual sliders are separate for AC and battery: AC `20–40 W` PL1 / `20–40 W` PL2, battery `20–35 W` / `20–35 W` (`PL2 >= PL1`, `UC_UserScenario.xaml:96-129`). MSI's presets differ on AC and battery (`UserScenarioX.cs:529-616`): Super Battery `20 / 20 W` (both), Balanced `35 / 35 W` AC, `30 / 30 W` battery, Performance `43 / 45 W` AC, `35 / 35 W` battery; it re-applies them on every AC/battery change (`UserScenarioX.cs` ~755-812). Tsume's A1M table (Endurance `20 / 20`, Balanced `30 / 35`, Performance `43 / 45`, Manual `20–43 W` PL1 / `20–45 W` PL2): Endurance and Balanced are Tsume's own, Performance is MSI's AC preset. Tsume's rule: on wall power Manual goes as far as the highest MSI itself applies on each tier, its Performance preset (`43 / 45`; MSI's own Manual sliders stop at `40`), and on battery everything is held to MSI's battery limit, the battery cap (`DeviceProfile.BatteryCap`) of `35 / 35 W`: MSI's battery sliders' end and its Performance preset on battery. While Windows reports the AC line offline (as MSI tests it, `PowerLineStatus == Offline`; unknown counts as AC), the helper lowers every tier it writes to the cap: Performance `35 / 35`, Manual at most `35 / 35`, Balanced and Endurance unchanged. The chosen limits are kept, so wall power brings them back. The panel's Manual sliders run `20–43 W` / `20–45 W` on wall power and stop at MSI's `35 W` on battery, where they show the chosen values lowered to the cap; a wall-power value changes only if that slider is moved while on battery.
- **Buttons & controller:** WMI events `0x29`/`0x58`; Quick Settings triggers `Win+G`, `F15`, and `F16` (covering older and newer A1M BIOSes). Unified `0DB0:1901/1902` 1478-byte HID controller on `1.xx` firmware (Tsume `MinFirmwareVersion = 0x0164`). Legacy offsets exist per the Linux kernel `hid-msi` driver, not per MSI Center M; Tsume's `<= 1.63` cutoff is not confirmed by any source in this evidence set.

## Claw A8 (BZ2EM, MS-1T8K — AMD Ryzen Z2 Extreme)

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `UC_UserScenario_1T8K`), official `E1T8KAMS.106` BIOS ACPI AML, embedded ITE EC firmware `1T8KIMS1.105`, and `MSI Controller FW Updater_2608_3101.exe`. **`MS-1T7K` is recognized by MSI's software but not supported by Tsume yet**; Tsume treats it as an unknown board (read-only discovery, no writes). MSI Center M names it only in its compatibility whitelist and in one OSD helper that groups it with `MS-1T8K` (`OSDMain.IsModel1T8K_AMD`, `OSDMain.cs:7510`). Its power code (`UserScenarioX.cs:282-308`) and Manual UI (`UC_UserScenario\ComponentLoader.cs:177-192`) do not name `1T7K` and fall through to the Intel `1T91` path (Manual `0xC6`, no `0x52`, no `0xC5`). No `MS-1T7K` BIOS is available; its power path, EC and name are unconfirmed and need a probe from a real unit.

Differences from the Claw 8 EX (`MS-1T91`, which covers both Core G3 Extreme and Core G3 non-Extreme with identical `MS-1T91` limits):

- **Board model & EC transport:** `MS-1T8K` with ITE EC (`1T8KIMS1.105`). Exposes the identical `root\WMI` `MSI_ACPI` (`ACPI\PNP0C14\0_0`, `Package_32`, v2.8) bridge; DSDT `WMRD`/`WMWT` transparently routes EC RAM through MMIO `0xFEDF1400 + reg` when `ECID == 'I'`.
- **AMD 3-tier TDP path & UI sliders (no RyzenAdj / SMU mailbox):** Writes directly through `MSI_ACPI.Set_Data` to `0x50` (SPL), `0x51` (SPPT), and `0x52` (FPPT). MSI's Manual sliders are SPL `7–35 W`, SPPT `9–40 W`, FPPT `11–48 W` (`UC_UserScenario_1T8K.xaml:74-90`) with monotonic coupling (`SPL <= SPPT <= FPPT`, gap 0); the driver clamps are `35 / 50 / 60 W` (`UserScenarioX.cs:1459-1478`). Performance (`28/45/55`) goes past the SPPT/FPPT slider maxima, but only AI Engine mode 8 (`35/50/60`, with STAMP on, `UserScenarioX.cs:1233-1237`) reaches the `50`/`60` clamps. The same watts apply on AC and battery. Tsume's Manual goes as far as the highest MSI itself applies on each tier without STAMP: SPL `7–35 W` (MSI's slider), SPPT `9–45 W`, FPPT `11–55 W` (MSI's Performance preset); a saved Manual above that is clamped. That allows `35 / 45 / 55`, a mix MSI's own UI does not offer but inside its driver clamps. The transient floors are `7 W` SPL and `9 W` SPPT; there is no FPPT floor write (`11 W` is only the slider minimum). Exact write order (`UserScenarioX.setPowerLimit_AMD`): `0x50 = 7` → `0x51 = 9` → `0x52 = FPPT` → `0x51 = SPPT` → `0x50 = SPL`. Tsume exposes a 3rd **Fast boost** slider in Manual mode and Performance Presets only on 3-tier AMD devices (2-tier Intel UI is unchanged).
- **AMD STAMP (`0xC5`):** After writing power limits, if bit 7 (supported) is set, Tsume writes `stamp | 0x01` (other bits as read), always, exactly as MSI Center M's `setPowerLimit_AMDSTAMP(false)` does (`UserScenarioX.cs:1337-1358`), so a STAMP boost left on by MSI Center M AI Engine Mode 8 is turned off. MSI does this only at start-up and after a mode change that follows its own STAMP-on (`UserScenarioX.cs:299-301, 1276-1279`); Tsume does it after every limit change. It is best effort: a refused read or write of `0xC5` does not fail the change, because the limits are already written. The DSDT gives `0xC5` no bit meanings.
- **Interrupted limit change:** Tsume reads SPL, SPPT and FPPT before the sequence above. If a write in it fails, Tsume tries to put the three values back in the same safe order (SPL and SPPT to their floors, then FPPT, SPPT, SPL) and then reports the failure, so the APU is not left at the 7 / 9 W floors.
- **Manual shift code (`0xD2`):** `0x04` (`0xC4`, shared with Sport/Performance). Endurance (`0xC2`) and Balanced (`0xC1`) match the EX.
- **Presets & thermals:** Endurance `15 / 28 / 33 W`, Balanced `20 / 33 / 43 W`, Performance `28 / 45 / 55 W` (matching MSI Center M stock presets). Every preset can be changed within the Manual range. AMD thermal thresholds `75 °C` warn / `85 °C` hot are Tsume's own choice, not MSI values: no MSI or BIOS source gives them, and both DSDTs set `_CRT` to 100 °C.
- **Fan, battery, buttons & controller:** Same `0x68` CPU temp, `0x98` Cooler Boost, `0xD4` Fan Control, `0xD7` Charge Limit (`0x80 | pct`), `Get_Fan(0)` tachometer (`480_000 / raw`), WMI events `0x29`/`0x58` (`Win+G`), and unified `0DB0:1901/1902` 1478-byte HID controller (`3.xx` firmware; Tsume applies no legacy-layout cutoff).

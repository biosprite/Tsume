# Firmware interface (ACPI-WMI)

How Tsume changes performance mode, power limits, fans and battery behaviour.
Everything goes through one WMI class that the BIOS exposes.

Sources, in order of trust:

1. **MSI Center M on this device**, decompiled (`MSIWMIACPI2.dll`, `API_UserScenario.dll`). Authoritative for the Claw 8 EX.
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
| Controller firmware | 4.14 |

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

## Registers

| Addr | Name | Meaning |
|---|---|---|
| `0x50` | Sustained power limit | PL1 in watts |
| `0x51` | Boost power limit | PL2 in watts |
| `0x68` | CPU temperature | °C, read-only. Same value as `Get_Temperature(0)` byte 0. |
| `0x69`–`0x70` | CPU fan curve, temperature axis | mirrors `Get_Temperature(1)` |
| `0x71` | Live fan duty | read-only — *by convention on MSI ECs; read 56 while the fans turned at ~3400 rpm* |
| `0x72`–`0x78` | CPU fan curve, duties | mirrors `Get_Fan(1)` |
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
| Manual | Manual | 6 | `0xC6` — **EX only**; older Claws use 3 |
| — | Comfort | 0 | `0xC0` — never used on the Claw |

### Power limits

MSI's presets for MS-1T91, identical on AC and battery:

| Mode | Sustained | Boost |
|---|---|---|
| Endurance | 15 W | 20 W |
| Balanced | 25 W | 37 W |
| Performance | 30 W | 37 W |
| Manual | 8 – 35 W | up to 45 W |

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

- Duty is a raw byte **0–150**, not a percentage. 75 is the top of MSI's "Advanced" range; 150 is the physical maximum.
- Preserve data byte 0 (live duty) and byte 7 (the EC's ceiling) when writing.
- **Never write the temperature axis on the EX.** ClawTweaks reports that doing so zeroed the EX's thermal ceiling.
- This device's factory curve: temperatures `47 50 57 64 71 78` °C, duties `60 64 68 74 80 84`.

## Battery

Charge limit lives in `0xD7`: read it, keep bit 7 semantics, write
`0x80 | percent`. Accepted values are 60–100 in steps the firmware chooses.
MSI Center M offers 60, 80 and 100 (`0xBC`, `0xD0`, `0xE4`). Tsume offers 80,
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
| `0x58` | Quick Settings, short press |
| `0x2A` | Quick Settings, long press |
| `0x32` / `0x21` | Volume up / down |

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

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `MSIWMIACPI2.dll`, `UC_UserScenario_1T421T52`), official `E1T52IMS.114` BIOS ACPI AML, and embedded ENE EC firmware `1T52EMS1.109`. `MS-1T42` and `MS-1T52` share the exact same WMI, EC, shift, power, fan, battery, and `2.xx` controller paths.

Differences from the Claw 8 EX (`MS-1T91`):

- **Board models:** `MS-1T52` (8" 1200p) and `MS-1T42` (7" 1080p). Same `MSI_ACPI` WMI bridge (`ACPI\PNP0C14\0_0`, `Package_32`) and EC register map (`0x50`, `0x51`, `0x68`, `0x98`, `0xD2`, `0xD4`, `0xD7`).
- **Manual shift code (`0xD2`):** `0x04` (`0xC4`, shared with Sport/Performance), not `0x06` (`0xC6`, which is EX-only). Endurance (`0xC2`), Balanced (`0xC1`), and Performance (`0xC4`) match the EX. *(Note: MSI Center M switches `0xD2` to `0xC2` when Manual `PL1 == 8`; pending on-device `mode-check` verification on `MS-1T42`/`MS-1T52`.)*
- **Power limits (Core Ultra 7 258V / Lunar Lake):**
  - Ceiling is **8–30 W sustained (PL1)** and **9–37 W boost (PL2)**, floor `8 W`.
  - **PL2 headroom (`MinBoostHeadroom = 1`):** Lunar Lake requires `PL2 >= PL1 + 1`; setting `PL1 == PL2` triggers a firmware/power-management throttling quirk. Tsume enforces `PL2 >= PL1 + 1` in `PowerRange.Clamp` (helper) and in the Manual and Presets sliders.
  - Tsume presets: Endurance `8 W / 12 W`, Balanced `17 W / 20 W` (258V Processor Base Power sweet spot), Performance `30 W / 37 W` (full 258V PL1 / MTP), Manual default `17 W / 20 W`.
- **Hardware buttons & controller:** Same WMI event codes (`0x29`, `0x58`). Quick Settings triggers cover both `Win+G` (BIOS 10F+) and `F16` (older A2VM BIOSes). Rear-button (`M1`/`M2` at `186`/`355`) and lighting (`586`) profile offsets match the EX on controller firmware `>= 2.17` (`0x0217`); early launch firmware `< 2.17` used legacy offsets and is guarded off from writes.

## Claw A1M (MS-1T41 — Intel Core Ultra 5 135H / Ultra 7 155H Meteor Lake)

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `UC_UserScenario.xaml`, `MSI Controller FW Updater_2608_3101.exe`). No Ultra 5 vs Ultra 7 power-limit branching exists in MSI Center M.

- **Board model & shift code:** `MS-1T41`. Same `MSI_ACPI` WMI bridge and EC register map (`0x50`, `0x51`, `0x68`, `0x98`, `0xD2`, `0xD4`, `0xD7`). Manual shift code is `0x04` (`0xC4`).
- **Power limits:** Floor `15 W` (`LimitValue_PowerLow = 15`). Manual range `20–40 W` PL1 / `20–45 W` PL2 (`PL2 >= PL1`), presets Endurance `20 / 20 W`, Balanced `30 / 35 W`, Performance `40 / 45 W`.
- **Buttons & controller:** WMI events `0x29`/`0x58`; Quick Settings triggers `Win+G`, `F15`, and `F16` (covering older and newer A1M BIOSes). Unified `0DB0:1901/1902` 1478-byte HID controller on `1.xx` firmware (`MinFirmwareVersion = 0x0164`; `<= 1.63` used legacy offsets).

## Claw A8 (BZ2EM, MS-1T8K) & Claw 7 AMD (MS-1T7K — AMD Ryzen Z2 Extreme)

Verified from decompiled MSI Center M v3.0.2610.0701 (`API_UserScenario.dll`, `UC_UserScenario_1T8K`), official `E1T8KAMS.106` BIOS ACPI AML, embedded ITE EC firmware `1T8KIMS1.105`, and `MSI Controller FW Updater_2608_3101.exe`. `MS-1T7K` shares the `MS-1T8K` AMD platform and `3.xx` controller firmware lineage.

Differences from the Claw 8 EX (`MS-1T91`, which covers both Core G3 Extreme and Core G3 non-Extreme with identical `MS-1T91` limits):

- **Board model & EC transport:** `MS-1T8K` (and `MS-1T7K`) with ITE EC (`1T8KIMS1.105`). Exposes the identical `root\WMI` `MSI_ACPI` (`ACPI\PNP0C14\0_0`, `Package_32`, v2.8) bridge; DSDT `WMRD`/`WMWT` transparently routes EC RAM through MMIO `0xFEDF1400 + reg` when `ECID == 'I'`.
- **AMD 3-tier TDP path & UI sliders (no RyzenAdj / SMU mailbox):** Writes directly through `MSI_ACPI.Set_Data` to `0x50` (SPL, `7–35 W`, floor `7 W`), `0x51` (SPPT, `9–50 W`, floor `9 W`), and `0x52` (FPPT, `11–60 W`, floor `11 W`) with monotonic coupling (`SPL <= SPPT <= FPPT`, gap 0). Exact write order (`UserScenarioX.setPowerLimit_AMD`): `0x50 = 7` → `0x51 = 9` → `0x52 = FPPT` → `0x51 = SPPT` → `0x50 = SPL`. Tsume exposes a 3rd **Fast boost** slider in Manual mode and Performance Presets only on 3-tier AMD devices (2-tier Intel UI is unchanged).
- **AMD STAMP (`0xC5`):** After writing power limits, if bit 7 (supported) is set, Tsume clears bit 1 and sets bit 0 (`(stamp & ~0x02) | 0x01`) so any STAMP boost left active by MSI Center M AI Engine Mode 8 is reset.
- **Manual shift code (`0xD2`):** `0x04` (`0xC4`, shared with Sport/Performance). Endurance (`0xC2`) and Balanced (`0xC1`) match the EX.
- **Presets & thermals:** Endurance `15 / 28 / 33 W`, Balanced `20 / 33 / 43 W`, Performance `28 / 45 / 55 W` (matching MSI Center M stock presets). AMD thermal thresholds: `75 °C` warn / `85 °C` hot.
- **Fan, battery, buttons & controller:** Same `0x68` CPU temp, `0x98` Cooler Boost, `0xD4` Fan Control, `0xD7` Charge Limit (`0x80 | pct`), `Get_Fan(0)` tachometer (`480_000 / raw`), WMI events `0x29`/`0x58` (`Win+G`), and unified `0DB0:1901/1902` 1478-byte HID controller (`3.xx` firmware, no legacy offset cutoff).

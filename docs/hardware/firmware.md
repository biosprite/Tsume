# Firmware interface

How Tsume sets the performance mode, power limits, fans and charge limit on
the MSI Claw family. The BIOS exposes one WMI class for this. Tsume uses no
kernel driver.

## Boards

| Board | Device | Processor | Power limits | Tested on a real unit |
|---|---|---|---|---|
| MS-1T91 | Claw 8 EX AI+ | Intel Arc G3 Extreme | sustained, boost | **Yes** |
| MS-1T91 | Claw 8 EX AI+ | Intel Arc G3 | sustained, boost | No |
| MS-1T52 | Claw 8 AI+ | Intel Core Ultra 7 258V | sustained, boost | No |
| MS-1T42 | Claw 7 AI+ | Intel Core Ultra 7 258V | sustained, boost | No |
| MS-1T41 | Claw A1M | Intel Core Ultra 5 135H / Ultra 7 155H | sustained, boost | No |
| MS-1T8K | Claw A8 | AMD Ryzen Z2 Extreme | sustained, boost, fast boost | No |

Tsume writes to no other board. Reading is allowed anywhere, so the probe can
learn a new one.

MS-1T91 is sold with two processors. MSI uses the same power limits for both,
and where a table below says MS-1T91, it means both. The board itself has been
tested, so the precautions below apply to neither.

On a board that has not been tested, Tsume takes two extra precautions:

| Precaution | Rule |
|---|---|
| Interface version | Before its first write Tsume asks the firmware for its interface version, and writes nothing unless the major version is 2. |
| Charge limit | Tsume sets a charge limit only if the firmware shows that it has one. |

## The interface

```
namespace   root\WMI
instance    MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'
argument    Data : Package_32 { Bytes : uint8[32] }
```

```
request    [0] selector    [1..31] payload
response   [0] status      [1..31] data          status 0 = refused
```

Calls need administrator rights, which is why Tsume has a helper service.

| Method | Selector | Data |
|---|---|---|
| `Get_Data` / `Set_Data` | A register address | One byte |
| `Get_WMI` | none | Interface version: major, then minor |
| `Get_EC` | none | Firmware version text |
| `Get_Fan` | 0 | Fan speed (see [Fans](#fans)) |
| `Get_Fan` / `Set_Fan` | 1, 2 | Fan 1 or fan 2 curve speeds (see [Custom curves](#custom-curves)) |
| `Get_Temperature` / `Set_Temperature` | 1, 2 | Fan 1 or fan 2 curve rising temperatures (see [Custom curves](#custom-curves)) |
| `Get_Thermal` / `Set_Thermal` | 1, 2 | Fan 1 or fan 2 falling hysteresis temperatures on Intel boards (see [Custom curves](#custom-curves)) |

## Registers

| Address | Holds | Boards | Tsume |
|---|---|---|---|
| `0x50` | Sustained power limit, watts | all | writes |
| `0x51` | Boost power limit, watts | all | writes |
| `0x52` | Fast boost power limit, watts | MS-1T8K | writes |
| `0x68` | Processor temperature, °C | all | reads |
| `0x98` | Bit 7: fans at full speed | all | writes |
| `0xC5` | Extra AMD boost. Bit 7: available. Bit 0: turn it off. | MS-1T8K | writes |
| `0xD2` | Shift mode | all | writes |
| `0xD4` | Bit 7: fans follow a custom curve | all | writes |
| `0xD7` | Charge limit | all | writes |

## Shift mode

Register `0xD2` holds the performance mode. Bits 7 and 6 are always set; the
low bits are the mode.

| Value | Mode |
|---|---|
| `0xC1` | Balanced |
| `0xC2` | Endurance |
| `0xC4` | Performance. On every board but MS-1T91, also Manual. |
| `0xC6` | Manual, on MS-1T91 only |

Tsume writes the board's **Manual** value for every mode, so that the firmware
follows the watts Tsume sets instead of its own built-in ones.

| Board | Tsume writes | Exception |
|---|---|---|
| MS-1T91 | `0xC6` | none |
| MS-1T52, MS-1T42 | `0xC4` | `0xC2` when the sustained limit is 8 W |
| MS-1T41 | `0xC4` | none |
| MS-1T8K | `0xC4` | none |

Since one value stands for several modes, the register alone cannot name the
mode. The helper reports the mode you chose as long as the registers still
hold what Tsume wrote for it.

## Power limits

Watts are shown as sustained / boost, or sustained / boost / fast boost on
MS-1T8K.

These are MSI's default limits.

### Presets

| Board | Endurance | Balanced | Performance | Manual starts at |
|---|---|---|---|---|
| MS-1T91 | 15 / 20 | 25 / 37 | 30 / 37 | 25 / 37 |
| MS-1T52, MS-1T42 | 8 / 12 | 17 / 20 | 30 / 37 | 17 / 20 |
| MS-1T41 | 20 / 20 | 35 / 35 | 43 / 45 | 30 / 35 |
| MS-1T8K | 15 / 28 / 33 | 20 / 33 / 43 | 28 / 45 / 55 | 20 / 33 / 43 |

Each preset can be changed in the panel, within the Manual range.

### Manual range

| Board | Sustained | Boost | Fast boost | Rule |
|---|---|---|---|---|
| MS-1T91 | 8–35 W | 10–45 W | n/a | boost ≥ sustained + 2 W |
| MS-1T52, MS-1T42 | 8–30 W | 9–37 W | n/a | boost ≥ sustained + 1 W |
| MS-1T41 | 20–43 W | 20–45 W | n/a | boost ≥ sustained |
| MS-1T8K | 7–35 W | 9–45 W | 11–55 W | sustained ≤ boost ≤ fast boost |

The helper clamps every request to this range.

### Write order

Tsume writes the shift mode first. It then lowers the limits to a floor before
raising them, so a higher limit is never below a lower one part-way through.

| Board | Order | Floor |
|---|---|---|
| MS-1T91, MS-1T52, MS-1T42 | sustained = floor → boost → sustained | 8 W |
| MS-1T41 | the same | 15 W |
| MS-1T8K | sustained = 7 → boost = 9 → fast boost → boost → sustained | 7 W and 9 W |

On MS-1T8K Tsume also:

- reads the three limits first and, if a write fails part-way, tries to put
  them back before reporting the failure;
- turns the extra AMD boost (`0xC5`) off after every change, when the firmware
  has it.

### On battery (MS-1T41)

This board is held to 35 / 35 W on battery, and Balanced drops to 30 / 30 W.
The chosen limits are kept, so wall power brings them back.

The presets page has separate "Plugged in" and "On battery" sliders for this
board. Battery presets go up to 35 / 35 W.

| Mode | On wall power | On battery |
|---|---|---|
| Endurance | 20 / 20 | 20 / 20 |
| Balanced | 35 / 35 | 30 / 30 |
| Performance | 43 / 45 | 35 / 35 |
| Manual | up to 43 / 45 | up to 35 / 35 |

## Fans

| Setting | What Tsume writes |
|---|---|
| Auto | Clears bit 7 of `0x98`, writes the firmware's own speeds back, then clears bit 7 of `0xD4` |
| Custom | Writes the curve to both fans, sets bit 7 of `0xD4`, then clears bit 7 of `0x98` |
| Max | Sets bit 7 of `0x98`. A custom curve stays underneath. |

Fan speed comes from `Get_Fan(0)`: data bytes 0–1 are a big-endian period, and
`RPM = 480000 / period`. A period of 0 means the fan is stopped.

### Custom curves

A curve has six points. You choose one speed for each, and for custom curves Tsume
also writes a chipset-appropriate temperature scale to the firmware (restoring the
firmware's own temperatures and hysteresis on Auto).

| Call | Data |
|---|---|
| `Get_Temperature(1\|2)` / `Set_Temperature(1\|2)` | Eight bytes per fan. Byte 2 (`0x70`/`0x88`) is the critical shutdown threshold and is never modified. Rising points sit at bytes `0, 3, 4, 5, 6, 7` on MS-1T91 (`0x69..0x6E`) and bytes `3, 4, 5, 6, 7, 1` on MS-1T52, MS-1T42, MS-1T41 and MS-1T8K (`0x6A..0x6F`, leaving byte 0 at 0). |
| `Get_Fan(1\|2)` / `Set_Fan(1\|2)` | Eight bytes per fan. Bytes 1–6 are the six speeds; bytes 0 and 7 are left as read. |
| `Get_Thermal(1\|2)` / `Set_Thermal(1\|2)` | Seven bytes per fan. On Intel boards bytes 1–6 (`0x7A..0x7F`, `0x92..0x97`) hold absolute falling hysteresis temperatures (set 6 °C below each rising point); on MS-1T8K they hold relative deltas (`7` °C) and are left untouched. |

Tsume reads each table, updates only the curve bytes, and writes all bytes back. It
writes the same curve to fan 2 when the firmware has a table for it and its own
tables are known. A fan 2 table that is refused, or reads all zeros while fan 1's
does not, counts as no table. Speeds are in the firmware's own units, and both the
top of the scale and custom temperature range differ by board/chipset:

| Board | Chipset | Top speed | Custom curve temperatures, °C | EC critical (`0x70`) |
|---|---|---|---|---|
| MS-1T91 | Intel Arc G3 / G3 Extreme | 100 | 50, 60, 70, 80, 90, 100 | 105 °C |
| MS-1T52, MS-1T42 | Intel Core Ultra 7 258V (Lunar Lake) | 150 | 45, 55, 65, 75, 85, 95 | 100 °C |
| MS-1T41 | Intel Core Ultra 5 135H / 7 155H (Meteor Lake) | 150 | 50, 60, 70, 80, 90, 100 | 110 °C |
| MS-1T8K | AMD Ryzen Z2 Extreme | 130 | 45, 55, 65, 75, 85, 95 | 102 °C |

Rules:

- Tsume writes no curve unless the read temperatures are non-decreasing within
  20–110 °C, custom temperatures sit strictly below the EC critical threshold
  (`0x70`), and every speed is within the board's top speed.
- A curve never slows down as it gets hotter. It also has floors, counted from
  the board's warning temperature: from 25 °C below it at least 30, from
  15 °C below it at least 50, and from it on at least 75. Where the firmware's
  own curve (sampled at the active temperatures) is slower, the floor drops to it.
- The firmware keeps following its tables even with bit 7 of `0xD4` clear, so
  Auto must put the firmware's own speeds, temperatures, and falling hysteresis
  back. Tsume saves them from tables it knows are untouched, and marks the
  tables on disk before its first custom write so a custom curve is never taken
  for the firmware's own.
- If the firmware's own tables are not known, MS-1T91 uses the ones below. On
  other boards Auto then only clears bit 7 of `0xD4`, and Tsume writes no
  curve it could not undo.
- Every table is read back. If a write fails or does not stick, Tsume puts
  back all modified tables, bit 7 of `0xD4` and Cooler Boost as it found them,
  then reports the failure.

On MS-1T91 the firmware's own curve is:

| Temperature, °C | 47 | 50 | 57 | 64 | 71 | 78 |
|---|---|---|---|---|---|---|
| Speed | 60 | 64 | 68 | 74 | 80 | 84 |

Fan 2's table is the same, with falling hysteresis at `48, 51, 58, 65, 72, 79` °C.
Byte 0 of `Get_Fan` is the speed the fan is running at right now, in the same units;
on this unit one unit is about 61 RPM. Byte 7 (`94` on this unit) is the high-temp
failsafe step engaged once temperature crosses the 6th point (`0x6E`) and is left
as the firmware has it.

## Charge limit

Register `0xD7` holds `0x80 + percent`. Tsume writes the byte, reads it back,
and reports a failure if the firmware kept something else.

| Limit | Byte |
|---|---|
| 80 % | `0xD0` |
| 90 % | `0xDA` |
| 100 % | `0xE4` |

Charging is expected to start again ten points below the limit. That has not
been watched yet.

## Hardware buttons

The two buttons beside the screen are not part of the gamepad. The firmware
reports them as the WMI event `MSI_Event`. Only presses are reported, and
listening needs no elevation.

| Code | Button |
|---|---|
| `0x29` | M button |
| `0x31` | M button, held |
| `0x58` | Quick Settings |
| `0x2A` | Quick Settings, held |

Tsume acts on the two short presses.

The firmware also types a key for the Quick Settings button, which is what
opens Game Bar. When you give that button another job, Tsume swallows the key
with a keyboard hook.

| Board | Keys Tsume swallows |
|---|---|
| MS-1T91, MS-1T8K | Win+G |
| MS-1T52, MS-1T42 | Win+G, F16 |
| MS-1T41 | Win+G, F15, F16 |

## Keeping the settings

The firmware forgets the mode, the charge limit and the custom fan curve
across sleep and restarts. The helper puts them back, and only when what it
finds differs from what you chose.

| When | Mode and limits | Charge limit | Fan curve |
|---|---|---|---|
| Helper start-up | checked | checked | checked |
| Wake | checked | checked | checked |
| Screen turning on or off | checked | checked | checked |
| Plugging in or unplugging | checked on MS-1T41 | not checked | not checked |

It is not known whether changing the mode makes the firmware reload its own
fan tables, so the fan curve is also checked after every mode change.

The fan curve is not put back while Max is on. Choosing Auto forgets it, even
if writing Auto fails part-way.

It stands back while MSI Center M is running. See
[msi-center-m.md](msi-center-m.md).

## Checked on MS-1T91

Done on one Claw 8 EX AI+ (BIOS E1T91IMS.10E, interface version 2.9).

| Check | Date | Result |
|---|---|---|
| Read every register | 2026-11-10 | All values as expected |
| Rewrite each register with its own value | 2026-11-10 | Accepted and read back |
| Change the mode, measure power under load, restore | 2026-11-10 | Values held and were restored exactly. Processor power followed the boost limit: 20.0 W at a 20 W limit. |
| Set the charge limit to 80 %, 90 % and 100 % | 2026-11-14 | Each kept. At 80 % with a full battery, the battery neither charged nor drained. |

An earlier mode check showed nothing about power,
because Windows had processor boost switched off that day.

Still open:

- The sustained limit taking over after a long load has not been watched.
- The mode check has not been recorded again since Tsume moved to writing the
  Manual value for every mode.
- Every check ran with MSI Center M paused.

## See also

The same interface is used by open-source projects, among them Linux's
`msi-wmi-platform` and `msi-ec` drivers, HandheldCompanion and Handheld Daemon.

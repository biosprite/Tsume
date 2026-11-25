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

MS-1T91 is sold with two processors. Tsume tells them apart by the processor's
name, and gives the Arc G3 lower limits than the Arc G3 Extreme. Everything
else is the same for both, and where a table below says only MS-1T91, it means
both. The board itself has been tested, so the precautions below apply to
neither.

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
| `0xD4` | Bit 7: fans follow a custom curve | all | clears |
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

### Presets

| Board | Endurance | Balanced | Performance | Manual starts at |
|---|---|---|---|---|
| MS-1T91, Arc G3 Extreme | 12 / 15 | 25 / 28 | 32 / 36 | 25 / 28 |
| MS-1T91, Arc G3 | 12 / 15 | 25 / 28 | 30 / 37 | 25 / 28 |
| MS-1T52, MS-1T42 | 8 / 12 | 17 / 20 | 30 / 37 | 17 / 20 |
| MS-1T41 | 20 / 20 | 30 / 35 | 43 / 45 | 30 / 35 |
| MS-1T8K | 15 / 28 / 33 | 20 / 33 / 43 | 28 / 45 / 55 | 20 / 33 / 43 |

Each preset can be changed in the panel, within the Manual range.

### Manual range

| Board | Sustained | Boost | Fast boost | Rule |
|---|---|---|---|---|
| MS-1T91, Arc G3 Extreme | 8–35 W | 8–45 W | n/a | boost ≥ sustained |
| MS-1T91, Arc G3 | 8–30 W | 8–45 W | n/a | boost ≥ sustained |
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

This board is held to 35 / 35 W on battery. The chosen limits are kept, so
wall power brings them back.

| Mode | On wall power | On battery |
|---|---|---|
| Endurance | 20 / 20 | 20 / 20 |
| Balanced | 30 / 35 | 30 / 35 |
| Performance | 43 / 45 | 35 / 35 |
| Manual | up to 43 / 45 | up to 35 / 35 |

## Fans

| Setting | What Tsume writes |
|---|---|
| Auto | Clears bit 7 of `0x98` and bit 7 of `0xD4` |
| Max | Sets bit 7 of `0x98` |

Fan speed comes from `Get_Fan(0)`: data bytes 0–1 are a big-endian period, and
`RPM = 480000 / period`. A period of 0 means the fan is stopped.

Custom fan curves are not built yet.

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

The firmware forgets the mode and the charge limit across sleep and restarts.
The helper puts them back, and only when what it finds differs from what you
chose.

| When | Mode and limits | Charge limit |
|---|---|---|
| Helper start-up | checked | checked |
| Wake | checked | checked |
| Screen turning on or off | checked | checked |
| Plugging in or unplugging | checked on MS-1T41 | not checked |

It stands back while MSI Center M is running. See
[msi-center-m.md](msi-center-m.md).

## Checked on MS-1T91

Done on one Claw 8 EX AI+ (BIOS E1T91IMS.10E, interface version 2.9).

| Check | Date | Result | Capture |
|---|---|---|---|
| Read every register | 2026-11-10 | All values as expected | [probe](probe-2026-11-10.txt) |
| Rewrite each register with its own value | 2026-11-10 | Accepted and read back | [write check](write-check-2026-11-10.txt) |
| Change the mode, measure power under load, restore | 2026-11-10 | Values held and were restored exactly. Processor power followed the boost limit: 20.0 W at a 20 W limit. | [mode check](mode-check-2026-11-10-boost-on.txt) |
| Set the charge limit to 80 %, 90 % and 100 % | 2026-11-14 | Each kept. At 80 % with a full battery, the battery neither charged nor drained. | |

An [earlier mode check](mode-check-2026-11-10.txt) showed nothing about power,
because Windows had processor boost switched off that day.

Still open:

- The sustained limit taking over after a long load has not been watched.
- The mode check has not been recorded again since Tsume moved to writing the
  Manual value for every mode.
- Every check ran with MSI Center M paused.

## See also

The same interface is used by open-source projects, among them Linux's
`msi-wmi-platform` and `msi-ec` drivers, HandheldCompanion and Handheld Daemon.

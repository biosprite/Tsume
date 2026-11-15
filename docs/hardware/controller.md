# Controller protocol (vendor HID)

How the Claw's built-in gamepad is configured: mode switching, button remaps,
RGB, vibration, gyro. Derived by reading MSI Center M's own control module
(`API_ControlMode` v1.0.2609.2901) on this device. Nothing here has been
replayed against the hardware yet — see **Status** at the bottom.

Multi-byte values are big-endian unless noted.

## Finding the device

| | |
|---|---|
| USB IDs | `0DB0:1901` (this device), `0DB0:1902` |
| Interface | the HID collection that has an **output report with ID `0x0F`** (that is MSI's only test) |
| Report size | 64 bytes each way, report ID included |
| Firmware version | USB `bcdDevice` — `0x0414` reads as "4.14" |

Zero-pad every outgoing report to 64 bytes.

## Framing

```
out   0F 00 00 3C  cmd  payload…                      (zero-padded to 64)
out   0F 00 00 3C  cmd  profile offH offL len data…   (profile-addressed)
in    ?? ?? ?? ??  cmd  payload…
```

Bytes 1–3 are constants in every packet MSI sends. Incoming bytes 0–3 are never
checked by MSI.

**Acknowledgement.** MSI runs a stop-and-wait queue: 1000 ms timeout, up to 5
resends, then it sends `ResetDevice` (0x28) on its own. A read is acknowledged
by a matching `0x05` reply (same offset and length). Anything else is
acknowledged by *any* incoming report except `0x0A`, `0x27`, `0x30` and `0xF4`.

## Commands

| Hex | Name | Direction | Payload |
|---|---|---|---|
| 04 | ReadProfile | → | `00 offH offL len` |
| 05 | ReadProfileAck | ← | `idx offH offL len data…` |
| 0A | ReadRGBStatusAck | ← | 9 × `R G B` |
| 21 | WriteProfile (RAM) | → | `01 offH offL len data…` (len ≤ 55) |
| 22 | SyncToROM | → | none — persists RAM to flash |
| 23 | RestoreFromROM | → | none — discards RAM state |
| 24 | SwitchMode | → | `mode mkeys` |
| 26 | ReadGamepadMode | → | none |
| 27 | GamepadModeAck | ← | `mode mkeys` (also sent unsolicited, by inference) |
| 28 | ResetDevice | → | none |
| 2C | SetFeatureState | → | 1 byte, **inverted** (1 = disable) |
| 2D | DisableDevice | → | 1 byte (1 = disable) |
| 2F | SetMotionStatus | → | 1 byte (1 = stream IMU) |
| 30 | MotionDataAck | ← | int16 **little-endian**: Gx Gy Gz Ax Ay Az from byte 5 |
| E0 | RGBControl | → | `play frameIndex` |
| FD | CalibrationControl | → | `0` enter · `1` commit · `2` cancel · `3` status |
| FE | CalibrationAck | ← | `[5]` in-calibration, `[6]` bits LT RT LS RS Motion |

### Never send

| Hex | Why |
|---|---|
| **F0** | Firmware update transport (JieLi DFU). A bad packet can brick the controller. |
| **F4** | Unknown. MSI only ever ignores it. |
| 24 with mode 5 or 6 | "BIOS" and "TESTING" modes. MSI never sends them. |
| 01 03 07 08 0B | Legacy commands with unknown behaviour. |

Also avoid pulsing both rumble motors at speeds `77 83 73 65 80 80` ("MSIAPP",
20 ms on / 5 ms off): MSI uses that XInput pattern as a firmware trigger.

### Use with care

- **22 SyncToROM** is a flash write. MSI issues it on almost every change; Tsume should debounce it.
- **FD 01** commits a calibration. Always send `FD 02` to back out, and on suspend.
- **2C / 2D** can leave the controller disabled.

## Modes

```
0F 00 00 3C 24  mode  mkeys
```

| `mode` | | `mkeys` | |
|---|---|---|---|
| 1 | XInput | 0 | M1/M2 run macros |
| 2 | DInput | 1 | M1/M2 are key combinations |
| 3 | MSI | 2 | M1/M2 disabled |
| 4 | Desktop (mouse + keyboard) | | |

MSI waits 20 ms and then reads the mode back with `0F 00 00 3C 26`. Tsume
does the same (`Platform/ControllerMode`), and sends back the `mkeys` it just
read so the paddles are left as they were.

## Profile memory

One 1478-byte image. Read it in chunks of up to 55 bytes (27 requests for the
whole thing); write only the bytes that changed.

```
read    0F 00 00 3C 04  00  offH offL  len
write   0F 00 00 3C 21  01  offH offL  len  data…
save    0F 00 00 3C 22
```

Reads use profile index 0 and writes use index 1. That asymmetry is MSI's, and
is unexplained.

| Offset | Len | Content |
|---|---|---|
| 0 | 32 | Custom data — opaque, **preserve** |
| 32 | 1 | Lift-to-wake (inverted) |
| 33 | 1 | Report rate |
| **34** | 2 | **Vibration strength: left, right** (default 50 / 50) |
| 36 | 5 | Audio module |
| **41** | 10 | **Gyro**: flags, switch, quick key, sensitivity, anti-deadzone, 2 × (orig, target), map selection |
| 51 | 2 | Shift module |
| 53 | 5 | Quick triggers, d-pad diagonal lock, ABXY swap, standby time |
| 58 | 16 × 8 | Button profiles: Up Down Left Right L1 R1 L3 R3 A B X Y Home View Menu Camera |
| **186** | 169 | **M1**: 8-byte button profile, step count, 32 × 5-byte macro steps |
| **355** | 169 | **M2**: same |
| **524** | 15 | **L2**: dead range at 527, edge dead range at 528 |
| **539** | 15 | **R2**: dead range at 542, edge dead range at 543 |
| **554** | 16 | **Left stick**: deadzone at 556, edge deadzone at 564 |
| **570** | 16 | **Right stick**: deadzone at 572, edge deadzone at 580 |
| **586** | 883 | **Lighting** (below) |
| 1469 | 9 | Per-button ABXY brightness + reserved — **preserve** |

**Button profile (8 bytes):** `index flag turbo map0..map4`. Index 1 = remap,
2 = macro. Flag bits: 1 dead-zone, 2 map-or-macro, 3 linear.

**Map codes:** gamepad 1–40 (Up 1 … A 9, B 10, X 11, Y 12, Home 13, View 14,
Menu 15, Camera 16, back-left 17, back-right 18, L2 19, R2 20, stick directions
21–28, diagonals 29–40), keyboard 50–157, mouse 200–206, none 255.

**Macro step (5 bytes):** `key holdH holdL delayH delayL`, milliseconds. Unused steps have key 255.

**Gyro flag bits:** 0 enable, 1 press-to-enable, 2 output as mouse, 3 flip X,
4 flip Y, 5 disable X, 6 disable Y, 7 press-to-toggle.

**Defaults MSI applies on this model:** M1 → back-left (17). M2 → back-right
(18), or Left Ctrl + 1 once controller firmware reaches 4.19.

## Lighting

Nine LEDs: left stick ring (0–3), right stick ring (4–7), ABXY (8).

```
586   active animation index
587   animation 0 ─ keyframe count (1–8)
588                 effect number, always 9
589                 speed byte = 20 − speed
590                 brightness 0–100
591                 8 keyframes × 9 LEDs × R G B
807   animation 1   (firmware-stored colour cycle, by inference)
```

The controller interpolates between keyframes: one keyframe is a steady colour,
two (colour, black) is a breathing effect, six is a colour cycle.

**Solid red at full brightness** — a single write of 32 bytes at offset 0x024A:

```
0F 00 00 3C 21 01 02 4A 20   00 01 09 03 64
FF 00 00  FF 00 00  FF 00 00  FF 00 00  FF 00 00  FF 00 00  FF 00 00  FF 00 00  FF 00 00
```

**Brightness only** — one byte at 590: `0F 00 00 3C 21 01 02 4E 01 64`.

**Lights off** — brightness 0, or one all-black keyframe.

## What MSI Center M does around it

- Logs every packet in hex to `%ProgramData%\MSI\MSI_Center_M\Log\API\GamePad\`. That log is the reference to validate Tsume's packets against.
- Stores its own copy of the profile in `%ProgramData%\MSI\MSI_Center_M\Control Mode\profile.rec`.
- Rewrites stick deadzones and saves to flash every time the controller reports a mode change. While MSI Center M runs, it will fight any other writer.

## Status

| | |
|---|---|
| Protocol read from MSI's code | done |
| Read-only handshake on this device (mode read, full profile read) | **done** 2026-11-10 — firmware 4.19, mode 1, 1478 bytes in 108 ms (`controller-read-2026-11-10.txt`) |
| Profile layout confirmed by read-back | **lighting block confirmed**: animation 0 at 586 (index), 587 count, 588 = 9, 589 speed, 590 brightness, 591 keyframes. Animations 1 and 3 sit 220 bytes apart (807, 1247); animation 2 at 1027 has a byte the layout does not explain (brightness reads 0x80). Tsume writes animation 0 only |
| Lighting write | **done** — 0x21 to working memory acknowledged in 5 ms, read back equal, original restored and read back equal (`lighting-check-2026-11-10.txt`). Not yet confirmed by eye |
| Mode switching | **done** 2026-11-11 — `24 04 00` read back as mode 4 after 40 ms, `24 01 00` as mode 1 after 34 ms (`pad-mode-check-2026-11-11.txt`). The pad **stays on the bus**: same five HID collections under `PID_1901`, same handle, XInput slot 0 still answering in mode 4. What the buttons and sticks send in mode 4 has **not been observed** (needs presses). Whether the mode survives sleep or a restart is not known. Tsume sends modes 1 and 4 only |
| Vibration, remaps | not yet |

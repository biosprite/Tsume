# Controller protocol

The Claw's built-in gamepad has its own USB channel for the controller mode,
the lighting and the back buttons. It needs no elevation, so the panel talks to
it directly.

Everything here was checked on one Claw 8 EX AI+ with controller firmware 4.19.
Other models and firmware versions are untested.

## Finding the device

| Property | Value |
|---|---|
| USB IDs | `0DB0:1901`, `0DB0:1902` |
| Interface | The HID collection that accepts output report `0x0F` |
| Report size | 64 bytes each way, report ID included |
| Firmware version | The USB device version: `0x0419` reads as 4.19 |

## Packets

```
out   0F 00 00 3C  command  payload…        (zero-padded to 64 bytes)
in    ?? ?? ?? ??  command  payload…
```

Tsume waits one second for a reply and tries three times.

| Command | Meaning | Payload |
|---|---|---|
| `04` | Read profile | `00 offsetHigh offsetLow length` |
| `05` | Reply to a read | `index offsetHigh offsetLow length data…` |
| `21` | Write profile (working memory) | `01 offsetHigh offsetLow length data…`, up to 55 bytes |
| `22` | Save working memory to flash | none |
| `24` | Switch mode | `mode mkeys` |
| `26` | Read mode | none |
| `27` | Reply to a mode read | `mode mkeys` |

These are the only commands Tsume sends. Two warnings for anyone adding more:

- **Never send `F0`.** It is the firmware update channel, and a bad packet can
  brick the controller.
- **`22` is a flash write.** Tsume sends it once, when a settings screen is
  left, not on every change.

## Modes

| `mode` | Meaning | Tsume sends it |
|---|---|---|
| 1 | Gamepad (XInput) | yes |
| 2 | DInput | no |
| 3 | MSI's own mode | no |
| 4 | Cursor: mouse and keyboard | yes |

`mkeys` says what the back buttons do: 0 uses the remaps stored in the profile.

To switch, Tsume reads the mode, sends the new mode with the `mkeys` it just
read, waits 20 ms and reads the mode back. If the pad still reports the old
mode it sends again, and gives up after five seconds.

## Profile memory

The controller keeps its settings in one 1478-byte profile. Tsume reads it in
chunks of up to 55 bytes and writes only these parts:

| Offset | Length | Content |
|---|---|---|
| 186 | 8 | M1, the left back button |
| 355 | 8 | M2, the right back button |
| 586 | 883 | Lighting |

Nothing else in the profile is ever written.

A back button's eight bytes are `01 04 turbo key1 key2 key3 00 00`: up to three
keys held together, or `FF` in the first key for no binding.

Early firmware keeps these settings at other offsets, so Tsume writes nothing
when the controller firmware is:

| Model | Firmware |
|---|---|
| Claw A1M | 1.63 or older |
| Claw 8 AI+, Claw 7 AI+ | 2.00 to 2.16 |

These cut-offs are a best guess and have not been confirmed.

## Lighting

Nine LEDs: left stick ring (0–3), right stick ring (4–7), and the A B X Y
buttons (8).

```
586   active animation
587   number of keyframes (1–8)
588   always 9
589   speed: 19 slowest … 1 fastest
590   brightness 0–100
591   8 keyframes × 9 LEDs × R G B
```

The controller fades between keyframes, so each effect is a short list of
them. Tsume writes the first animation only.

| Effect | Keyframes |
|---|---|
| Off | 1: all black |
| Steady | 1: the color |
| Breathe | 2: the color, then black |
| Spin | 4: one bright LED moving round each ring |
| Rainbow | 6: a color cycle |

## With MSI Center M running

MSI Center M writes to the same profile and may undo lighting or button
changes. The panel says so on the Lighting screen. See
[msi-center-m.md](msi-center-m.md).

## Status

| Feature | Status |
|---|---|
| Read the mode and the whole profile | Checked 2026-11-10 |
| Lighting write | Checked 2026-11-10: written, read back, restored |
| Mode switch | Checked 2026-11-11: Cursor and back, each confirmed in about 40 ms |
| Back buttons | Built, not yet checked |
| Vibration, gyro, triggers, stick settings | Not built |

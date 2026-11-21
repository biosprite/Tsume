<p align="center">
  <img src="assets/logo-transparent.png" alt="Tsume logo" width="120">
</p>

<h3 align="center">Tsume</h3>

<p align="center">
  A fast, lightweight, open source OEM software replacement for handheld gaming PCs.
</p>

<p align="center">
  <img src="assets/media/showcase.gif" alt="Tsume's panel sliding in over the desktop" width="600">
</p>

## Supported devices

| | Device | Replaces |
|---|---|---|
| ✅ | MSI Claw 8 EX AI+ (board MS-1T91) | MSI Center M and its Game Bar Quick Settings widget |

Tsume is built so that more devices can be added. On a machine it does not know
yet, the panel runs but leaves the firmware alone.

## Features

- **Performance modes:** Endurance, Balanced, Performance and Manual, set in
  the firmware. Each preset starts from defaults that depend on the device and
  can be customized in Settings. Manual has its own sliders for the sustained
  and boost limits. The chosen mode is saved.
- **Live readings:** processor temperature and fan speed, updated every second
  while the panel is open, beside the power limits in force, the battery level
  and the time.
- **Fan control:** Auto or Max.
- **Battery charge limit:** stop charging at 80 %, 90 % or 100 %, kept across
  sleep and restarts.
- **Display and sound:** screen brightness, speaker volume with mute, and a
  switch between the refresh rates the display offers.
- **Controller mode:** switch the built-in pad between gamepad and cursor
  (mouse) mode.
- **RGB lighting:** Off, Steady, Breathe, Rainbow or Spin for the stick rings
  and the A B X Y buttons, with a colour for each stick, brightness and speed.
  The choice is saved on the controller.
- **Quick actions:** touch keyboard, screenshot, Task Manager, show desktop.
- **Power menu:** hibernate, lock, restart, shut down.
- **OptiScaler (experimental):** finds games in Steam, Epic, GOG, Ubisoft
  Connect and the Xbox app, then installs, updates, repairs or removes
  OptiScaler in each one and puts the game's own files back on removal. Per
  game you choose the upscaler (XeSS, FSR 4 or FSR 3.1), frame generation and
  a frame-rate counter. Single-player games only: in an online game it can get
  an account banned, and it is not offered where an anti-cheat is found.
- **RTSS overlay (experimental):** optional Steam-style performance overlay
  (FPS, compact horizontal bar, detailed stats, or full stats with a live
  frametime graph) and global frame-rate cap driven through RivaTuner
  Statistics Server. Can be switched off to leave your own RTSS overlay alone.
  Nothing is bundled — links to Guru3D's official RTSS 7.3.7 download.
- **Themes:** eight colour schemes, or your own accent, background and text
  colours.
- **Controller, touch and mouse:** the whole panel works with any of the
  three, over a game or the desktop.

<details>
<summary><b>Install</b></summary>

1. Download `Tsume-<version>-x64.msi` from the Releases page and run it.
   It is unsigned, so Windows SmartScreen warns first: choose **More info**, then **Run anyway**.
2. Open **Tsume** from the Start menu.
3. In Settings (the gear), turn on **Start with Windows**.

On the Claw, to make the **M** button open Tsume, also turn on
**Pause MSI Center M**.

Uninstall from Windows' Installed apps. Uninstalling offers to take OptiScaler
out of your games, and lets MSI Center M run again if it was paused.

**MSI Center M still paused?** Open **Task Scheduler**, enable
`MSI_Center_M_Server` and `MSI_Center_M_Updater` in the Task Scheduler Library,
then right-click `MSI_Center_M_Server` and choose **Run**.

</details>

<details>
<summary><b>Use</b></summary>

| To open or close the panel | |
|---|---|
| Controller | hold **View + Menu** |
| The **M** button (Claw) | while MSI Center M is paused |
| Keyboard | **Win + Alt + Q** |
| Start menu | **Tsume** |

Inside: d-pad or left stick to move, **A** to press, **B** to go back. Left and
right change a slider or the performance mode.

</details>

<details>
<summary><b>Safety</b></summary>

- The panel never runs elevated. Firmware access lives in a separate helper,
  `Tsume.Helper.exe`, which runs as a Windows service.
- The helper accepts a fixed list of requests and clamps every value. It cannot
  be asked to write an arbitrary register.
- The helper only writes to boards whose firmware map has been verified. On
  anything else it refuses.
- No kernel driver: on the Claw, the firmware is reached through MSI's own WMI
  interface.
- Nothing of the manufacturer's is removed. Pausing MSI Center M only disables
  its two scheduled tasks and stops its processes; resuming reverses both.

</details>

<details>
<summary><b>Building</b></summary>

Needs the .NET 10 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File tools\run.ps1        # build, then start with the panel open
powershell -ExecutionPolicy Bypass -File tools\publish.ps1    # build the installer
```

The installer lands in `artifacts\installer\`. It holds the panel, the helper
and the .NET runtime, and is unsigned unless you pass `-Certificate <thumbprint>`.
[installer/Package.wxs](installer/Package.wxs) is the complete list of what
setup does.

Without the helper running, the panel still opens, but the performance modes
do nothing. To see what the firmware reports before trusting the helper with a
write (one permission prompt, reads only):

```powershell
powershell -ExecutionPolicy Bypass -File tools\probe.ps1
```

```
src/Tsume.Core        modes, limits, the helper protocol; no hardware, no UI
src/Tsume.Hardware    the firmware interface
src/Tsume.Helper      the elevated helper
src/Tsume.Upscaling   finds games; fetches, installs and removes OptiScaler
src/Tsume.App         the panel
docs/hardware         how each device is controlled, and how that was learned
installer             the .msi
tools                 build · run · probe · checks · publish
```

</details>

## Disclaimer

Tsume is developed with the assistance of AI.

## Licence

Copyright (C) 2026 Tsume contributors.

Tsume is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License, version 3, as published by the Free Software
Foundation. It comes with no warranty. See [LICENSE](LICENSE).

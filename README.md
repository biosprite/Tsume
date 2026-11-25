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

## Features

| Feature | What it does |
|---|---|
| **Performance modes** | Endurance, Balanced, Performance and Manual, set in the firmware. Each preset starts from defaults that depend on the device and can be customized in Settings. Manual has its own sliders for the power limits. The chosen mode is saved. |
| **Live readings** | Processor temperature and fan speed, updated every second while the panel is open, beside the power limits in force, the battery level and the time. |
| **Fan control** | Auto or Max. |
| **Battery charge limit** | Stop charging at 80 %, 90 % or 100 %, kept across sleep and restarts. |
| **Display and sound** | Screen brightness, speaker volume with mute, and a switch between the refresh rates the display offers. |
| **Controller mode** | Switch the built-in pad between gamepad and cursor (mouse) mode. |
| **Buttons** | Remap the M1 and M2 back buttons, and choose what the M and Quick Settings buttons do. |
| **RGB lighting** | Off, Steady, Breathe, Rainbow or Spin for the stick rings and the A B X Y buttons, with a color for each stick, brightness and speed. The choice is saved on the controller. |
| **Quick actions** | Touch keyboard, screenshot, Task Manager, show desktop. |
| **Power menu** | Hibernate, lock, restart, shut down. |
| **OptiScaler** (experimental) | Finds games in Steam, Epic, GOG, Ubisoft Connect, the EA app and the Xbox app, then installs, updates, repairs or removes OptiScaler in each one and puts the game's own files back on removal. Per game you choose the upscaler (XeSS, FSR 4 or FSR 3.1) and frame generation. Single-player games only: in an online game it can get an account banned, and it is not offered where an anti-cheat is found. |
| **RTSS overlay** (experimental) | Optional performance overlay using [realHfde's Essential Dark overlays](https://github.com/realHfde/Huffdee-RTSS-Overlays-and-Rainmeter-Skins) (MIT): FPS, Bar, Detail or Full. RTSS's own OverlayEditor plugin draws a single combined layout, so MSI Afterburner isn't needed, level changes (and Off) apply live in-game without restarting RTSS, and the overlay disappears when Tsume exits. Also sets a global frame-rate cap through RivaTuner Statistics Server. Can be switched off to give your own RTSS overlay back. RTSS itself isn't bundled; Tsume installs it through Windows Package Manager or links to Guru3D's official download. |
| **Themes** | Eight color schemes, or your own accent, background and text colors. |
| **Controller, touch and mouse** | The whole panel works with any of the three, over a game or the desktop. |

<details>
<summary><b>Supported devices</b></summary>

| | Device | Board |
|---|---|---|
| ✅ | MSI Claw 8 EX AI+ (Arc G3 Extreme) | MS-1T91 |
| 🧪 | MSI Claw 8 EX AI+ (Arc G3) (beta) | MS-1T91 |
| 🧪 | MSI Claw 8 AI+ (beta) | MS-1T52 |
| 🧪 | MSI Claw 7 AI+ (beta) | MS-1T42 |
| 🧪 | MSI Claw A1M (beta) | MS-1T41 |
| 🧪 | MSI Claw A8 (beta) | MS-1T8K |

On these, Tsume replaces MSI Center M and its Game Bar Quick Settings widget.

Tsume is built so that more devices can be added. On a machine it does not know
yet, the panel runs but leaves the firmware alone.

</details>

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

| To open or close the panel | How |
|---|---|
| Controller | Press **View + Menu** together |
| The **M** button (Claw) | While MSI Center M is paused |
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
- The helper only writes to boards it knows. On anything else it refuses.
- No kernel driver: on the Claw, the firmware is reached through the WMI
  interface its BIOS provides.
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

| Folder | Contents |
|---|---|
| `src/Tsume.Core` | Modes, limits, the helper protocol; no hardware, no UI |
| `src/Tsume.Hardware` | The firmware interface |
| `src/Tsume.Helper` | The elevated helper |
| `src/Tsume.Upscaling` | Finds games; fetches, installs and removes OptiScaler |
| `src/Tsume.App` | The panel |
| [`docs`](docs/README.md) | How each device is controlled |
| `installer` | The .msi |
| `tools` | Build, run, probe, checks, publish |

</details>

## Disclaimer

Tsume is developed with the assistance of AI.

## License

Copyright (C) 2026 Tsume contributors.

Tsume is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License, version 3, as published by the Free Software
Foundation. It comes with no warranty. See [LICENSE](LICENSE).

The RTSS overlay layouts and images in `src/Tsume.App/Overlays/Essential` are the
Essential overlays by realHfde (Faisal), from
[Huffdee RTSS Overlays and Rainmeter Skins](https://github.com/realHfde/Huffdee-RTSS-Overlays-and-Rainmeter-Skins),
used under the MIT License. See that folder's `LICENSE` and `README.md` for the
notice and the changes Tsume made.

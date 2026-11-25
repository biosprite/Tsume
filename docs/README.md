# Tsume documentation

For what Tsume does and how to install it, see the [project README](../README.md).

## Hardware

| Document | Covers |
|---|---|
| [hardware/firmware.md](hardware/firmware.md) | Performance modes, power limits, fans, charge limit and the hardware buttons |
| [hardware/controller.md](hardware/controller.md) | The built-in gamepad: controller mode, lighting and back buttons |
| [hardware/msi-center-m.md](hardware/msi-center-m.md) | Running alongside MSI Center M, and pausing it |

## Captures

Unedited output from checks run on a real Claw 8 EX AI+ (MS-1T91).

| File | Date | What was run |
|---|---|---|
| [hardware/probe-2026-11-10.txt](hardware/probe-2026-11-10.txt) | 2026-11-10 | Read-only firmware probe |
| [hardware/write-check-2026-11-10.txt](hardware/write-check-2026-11-10.txt) | 2026-11-10 | Each register rewritten with its own value |
| [hardware/mode-check-2026-11-10.txt](hardware/mode-check-2026-11-10.txt) | 2026-11-10 | Mode change under load, with processor boost off in Windows |
| [hardware/mode-check-2026-11-10-boost-on.txt](hardware/mode-check-2026-11-10-boost-on.txt) | 2026-11-10 | The same, with processor boost on |
| [hardware/controller-read-2026-11-10.txt](hardware/controller-read-2026-11-10.txt) | 2026-11-10 | Controller mode and full profile read |
| [hardware/lighting-check-2026-11-10.txt](hardware/lighting-check-2026-11-10.txt) | 2026-11-10 | Lighting write, then restore |
| [hardware/pad-mode-check-2026-11-11.txt](hardware/pad-mode-check-2026-11-11.txt) | 2026-11-11 | Switch to Cursor mode and back |
| [optiscaler/self-check-2026-11-11.txt](optiscaler/self-check-2026-11-11.txt) | 2026-11-11 | OptiScaler install and removal self-check |
| [optiscaler/self-check-2026-11-11-xmx.txt](optiscaler/self-check-2026-11-11-xmx.txt) | 2026-11-11 | A later run that also covers the FSR 4 XMX upscaler |

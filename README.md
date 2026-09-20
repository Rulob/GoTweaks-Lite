<div align="center">

<img src="XboxGamingBarPackage/Images/GoTweaks-1024.png" width="256" height="256" alt="GoTweaks Lite icon" />

# 🎮 GoTweaks Lite
<p align="center">
  <a href="https://github.com/Rayekkk/GoTweaks-Lite/releases/latest"><img src="https://img.shields.io/github/v/release/Rayekkk/GoTweaks-Lite?display_name=tag&label=Latest%20Release&color=7C3AED&logo=github&logoColor=white" alt="Latest Release"></a>
  <a href="https://github.com/Rayekkk/GoTweaks-Lite/releases"><img src="https://img.shields.io/github/downloads/Rayekkk/GoTweaks-Lite/total?label=Downloads&color=22C55E" alt="Downloads"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Rayekkk/GoTweaks-Lite" alt="License: MIT"></a>
</p>

**A stripped-down, Lenovo Legion Go 2–focused fork of [GoTweaks](https://github.com/corando98/GoTweaks).**

An Xbox Game Bar widget for controlling TDP, fans, RGB, controller remapping, gyro, per-game
profiles, and an OSD — rebuilt into a clean, predictable base tuned specifically for the
**Legion Go 2 (AMD Ryzen Z2 Extreme)**.

</div>

---

## 📑 Contents

- [What's different from the original](#-whats-different-from-the-original)
- [Features](#-features)
- [Installation](#-installation)
- [Uninstalling](#uninstalling)
- [Requirements](#-requirements)
- [Technology](#-technology)
- [Credits & License](#-credits)

---

## ✨ What's different from the original

This is the fork's identity — everything below is what GoTweaks Lite changes relative to
upstream, and why.

### 🎯 Focus

- **Legion Go 2 first.** The build is tuned for the Legion Go 2 (Ryzen Z2 Extreme). Other
  handhelds may still work, but they aren't a priority — decisions optimize for the Legion Go 2.

### ⚡ Rebuilt TDP control

- **One _TDP Mode_ selector** — Quiet / Balanced / Performance (Lenovo firmware presets) +
  **Custom**, replacing the old sprawling TDP UI and the duplicate Legion-tab power-profile dropdown.
- **Custom = base TDP (SPL) + SPPT / FPPT boosts** — clamped to a 50 W safety ceiling and applied
  **live** over Lenovo WMI. Mirrors Legion Space semantics and removes the old dual-write-path
  conflicts that used to snap limits back to ~15 W.
- **Removed the standalone master TDP slider** — folded into the mode selector.

### 🧹 Removed for a leaner base

- **Sticky TDP**, the **TDP Boost** toggle, **Custom TDP Presets**, and the **Device Min/Max TDP**
  panel — the single _TDP Mode_ selector replaces them all. (**AutoTDP** was also removed here, then
  brought back in PID-only form — see below.)
- The beta **Sidebar overlay** — _Focus GoTweaks Lite_ now simply opens the Game Bar.
- **Microsoft / bundled Default Game Profiles** — only your own per-game profiles remain.
- The **Advanced panel** (core parking / affinity), the **AC/DC Power Plan** selector, the debug
  **Themes** selector, and the old **ViGEm** emulation backend (**VIIPER** is now the only one) —
  niche, no-ops on the Legion Go 2, or superseded.

> **Why:** a smaller, more predictable surface with fewer background systems that can silently
> fight your settings.

### ➕ Added

- **AutoTDP (PID mode)** — an automatic power controller: pick a target FPS and a min/max wattage
  range, and it continuously adjusts TDP live to hit that FPS, backing off gracefully when the
  target is unreachable (CPU-bound games). Works from any TDP Mode — enabling it (from its card or
  the matching Quick Settings tile) automatically switches to and locks Custom mode, and restores
  your previous mode when turned off. Restored from upstream, trimmed to the rule-based PID
  controller only (no Q-Learning/SARSA machine-learning modes or per-game learned data).
- **Auto SDR** — while HDR is on, automatically matches the SDR white level to screen brightness
  so SDR content (desktop, most games) doesn't look washed out, with a full **curve editor**
  (Legion Go 2 preset or a custom, import/export-compatible curve). _(Built on the sibling Go2HDR
  project.)_
- **Brightness Gesture** — SteamOS-style brightness control: hold a configurable button and tilt a
  stick to fade the panel brightness up or down.
- **Power & Sleep card** — power-button behavior, screen-off, and a GoTweaks-owned "hibernate
  after" idle timer (input-aware, so a controller-only session isn't wrongly treated as idle),
  all restored automatically on uninstall.
- **Controller-combo tile hotkeys** — assign a 2-or-more-button controller combo (including the
  Legion back paddles) to any Quick Settings tile to activate it without touching the widget.
  _(Ported from upstream corando98/GoTweaks.)_
- **Fix Task View Bug** _(Labs, opt-in)_ — a targeted fix for the Legion Go bug where, after a
  restart with a USB hub attached, focusing the desktop pops open Task View (Win+Tab) and buzzes
  the controller. It re-enumerates the controller's USB port once per boot — the software
  equivalent of physically replugging a pad. Enable it only if you have this bug.
- **Single-file GUI installer** — `GoTweaks-Setup.exe` bundles the package + certificate and runs
  the whole install behind a small progress window (see [Installation](#-installation)).

### 🔧 Reliability fixes

- Correct **system-tray icon**, fixed **swapped CPU/GPU wattage sensors** on the Legion Go 2 so OSD
  labels match Adrenalin, more reliable **AFMF toggle**, true-CPU-Tctl **fan-curve temperature**,
  and a **Fan Full Speed** / custom fan curve that survives sleep/hibernate instead of silently
  stopping.
- **Controller vibration & lighting persistence** across restarts, a **24-hour OSD clock**, extra
  navigation / media keys in the remap pickers, and improved **Lossless Scaling** runtime
  detection, settings-sync, and reliability.
- **Kill-then-relaunch** no longer wedges the helper, **Custom TDP** holds through gameplay and
  power-source changes, and **controller status / battery** report correctly when the pads are
  detached.
- **Closing the app window no longer kills the active Game Bar widget.** GoTweaks Lite can also be
  opened as a standalone window (Start menu / taskbar) alongside the Game Bar overlay; closing that
  window used to silently kill the widget's connection — it's now minimized instead, and the widget
  keeps running.
- **Stable FPS counter.** The overlay's FPS is now derived from PresentMon's own per-frame
  timestamps instead of counting samples per refresh window, fixing brief spikes (e.g. a real 75
  FPS momentarily reading 130) caused by uneven delivery timing rather than an actual rate change.

---

## 🧩 Features

### 🗂️ Quick Settings

A customizable dashboard of quick-access tiles for your most-used settings.

- One-tap toggles for TDP Mode, Profile, Overlay, Lossless Scaling, AMD features, and more
- **Assign a controller button combo to any tile** — hold the combo (paddles included) to fire it
  hands-free
- Custom keyboard-shortcut tiles you can add and remove
- An optional built-in **brightness slider** and a live metrics row (CPU/GPU/memory, battery, fan)
- Device-specific tiles that appear when supported hardware is detected

### ⚡ Performance Control

Fine-tune power and CPU settings for performance or battery life.

**TDP management**
- **TDP Mode** — Quiet / Balanced / Performance firmware presets, plus Custom
- **Custom Power Limits** — base TDP (SPL) with independent SPPT and FPPT boosts, a 50 W ceiling,
  and live apply while dragging
- **Live readout** — current SPL / SPPT / FPPT confirmed straight from the hardware

**CPU controls**
- **CPU Boost** — enable or disable CPU boost
- **CPU EPP** — Energy Performance Preference (0–100)
- **Min / Max CPU State** — control the CPU clock-speed range

### 🎯 Per-Game Profiles

Automatically apply your preferred settings when each game launches.

- Automatic profile switching on game detection
- Saves all performance settings per game:
  - TDP (SPL / SPPT / FPPT) and CPU settings
  - AMD Radeon features
  - Lossless Scaling configuration
  - Legion Go controller settings (per-category: lighting, vibration, button mappings, gyro,
    Nintendo layout)

### 🕹️ Legion Go 2 Support

Deep support for the Legion Go 2 with automatic device detection.

**Fan & performance**
- Quiet, Balanced, Performance, and Custom modes
- Custom TDP with fine-grained control (SPL, SPPT, FPPT)
- Custom fan curve and a Fan Full Speed toggle

**Controller settings**
- **Button Remapping** — customize the M2, M3, Y1, Y2, Y3 buttons
- **Remap type** — map to keyboard shortcuts, keys, gamepad actions, or mouse buttons
- **Joystick to Mouse** — emulate a mouse with either stick
- **Desktop Controls** — map controls to mouse + left/right click, similar to the ROG Ally
- **Nintendo Layout** — swap the button layout
- **Stick Deadzones** — adjust left/right stick deadzones (0–50%)
- **Gyroscope** — enable/disable with target selection, X/Y sensitivity, axis inversion, and
  activation mode (Hold/Toggle) with a button binding
- **Brightness Gesture** — hold a trigger button and tilt a stick/D-pad to fade panel brightness
- **Vibration Mode** & **Touchpad Haptics** — configure vibration behavior and haptic feedback

**RGB lighting**
- Light mode, color, brightness, and speed control
- Power-light toggle

**Other**
- Touchpad toggle
- Battery charge limit

### 🔋 Power & Sleep

Windows power behavior, read and written directly against the matching power-plan settings and
restored on uninstall.

- **Power Button behavior** and **Turn off screen after** (AC / DC)
- **Hibernate after** — a GoTweaks-owned, input-aware idle timer (Windows has no built-in one)
- **Disable Sleep Timer** shortcut

### 🎮 Controller Emulation (VIIPER)

Present the Legion Go's controls as a different virtual gamepad — useful for games or Steam Input
profiles that expect a specific pad type, or that support native gyro aim only on certain
controllers.

- **Virtual device types** — Xbox 360, DualShock 4, DualSense, DualSense Edge, Switch Pro, Joy-Con
  pair, or Steam Controller
- **Native gyro/accel forwarding** — the Legion Go's own motion sensors drive the emulated pad's
  gyro, so games read real hardware motion instead of a stick-to-gyro conversion
- **Alternate Gyro Convention** toggle — flips gyro polarity per target if a game's aim feels
  inverted
- **Guide-button remap** — map a Legion button to Xbox Guide independent of whether full emulation
  is on
- Requires the free **usbip-win2** driver — install it once from the in-app prompt (Controller
  Emulation tab or the setup-warning banner)

### 🔴 AMD Radeon Features

GPU-based enhancements for AMD graphics.

| Category | Feature |
| --- | --- |
| **Upscaling & frame gen** | Radeon Super Resolution (RSR) with sharpness · AMD Fluid Motion Frames (AFMF) |
| **Performance** | Radeon Anti-Lag · Radeon Boost (dynamic resolution) |
| **Power saving** | Radeon Chill with min/max FPS |
| **Image quality** | Image Sharpening · display color controls (brightness, contrast, saturation, temperature) |

### 🖼️ Lossless Scaling Integration

Control Lossless Scaling directly from the widget _(the Scale tab appears when Lossless Scaling is
detected)_.

- Launch and manage Lossless Scaling
- Configure scaling type and factor
- Frame-generation modes (LSFG2, LSFG3)
- Auto-scaling with configurable delay
- Anime4K and FSR optimization options
- Per-profile configurations, with a clear "Scale" vs "Apply and Restart" distinction that lights
  up only when you have unsaved changes

### 🖥️ Graphics Settings

Display and resolution management.

- Resolution and refresh-rate control with auto-detection (clearly gated to the built-in panel
  when docked to an external monitor)
- HDR toggle (when supported)
- **Auto SDR** — match the SDR white level to screen brightness while HDR is active, with an
  editable curve

### 📊 Performance Overlay (OSD)

A real-time on-screen display powered by RivaTuner Statistics Server.

- **Detail levels** — Off, Minimal, Standard, Detailed
- **Metrics** — FPS (rendered + displayed via PresentMon) with a `[FG]` badge, frametime graph and
  frame-budget readout, CPU/GPU usage & temperatures, power draw, memory & VRAM, battery, fan speed,
  and TDP limits (SPL/SPPT/FPPT) or the current performance mode

### 🎮 Controller Navigation

Designed for full gamepad control — no mouse needed.

- D-pad navigation between all controls, with auto-focus into a tab's content
- High-visibility focus ring and automatic scroll to the focused item
- Works with Xbox, PlayStation, and other controllers

---

## 📦 Installation

1. Download **`GoTweaks-Setup.exe`** from the latest release and run it. Click **Yes** on the UAC
   prompt, then wait for the confirmation. _(Close the Game Bar overlay first if it's open —
   `Win + G` to check.)_
2. Open Xbox Game Bar (`Win + G`), open the **widget menu**, and enable **“GoTweaks Lite”**. The
   first launch shows one more UAC prompt for the elevated helper; after that, launches are silent.
3. _(For per-game profiles)_ Xbox Game Bar → **Settings** → **More Settings** → the **GoTweaks
   Lite** widget → enable **“Know which game or app is in focus”**.

Everything is built into the one `.exe`, and installing over an existing version keeps your profiles
and settings.

> [!NOTE]
> Windows may show a **"Windows protected your PC" SmartScreen warning** — normal for a freshly
> downloaded, unsigned executable. Click **More info** → **Run anyway**.

### Uninstalling

For a plain removal, **Settings → Apps → Installed apps → GoTweaks Lite → Uninstall** is enough.

For a deeper clean (stops the helper, clears any HidHide controller-hiding rules it added so a
controller is never left hidden, sweeps leftover virtual controllers, removes the scheduled task,
then removes the app package itself), grab **`scripts/Uninstall-GoTweaks.ps1`** from the repo
(not included in the release download). Shared drivers (PawnIO, ViGEmBus) are left installed by
default since other tools may use them too — pass `-RemoveDrivers` to also uninstall those.

Open **PowerShell as Administrator**, `cd` to the folder with the script, then run:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Uninstall-GoTweaks.ps1"
```

---

## ✅ Requirements

| | |
| --- | --- |
| **OS** | Windows 10 / 11 |
| **Required** | Xbox Game Bar |
| **Optional** | [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) (OSD overlay) · [PawnIO](https://github.com/SuporteTI/PawnIO) (custom fan curve / Fan Full Speed) · [usbip-win2](https://github.com/vadimgrn/usbip-win2/releases) (controller emulation / VIIPER — installable in-app) · AMD GPU (Radeon features) · Legion Go 2 / Legion Go (device features) · Lossless Scaling (scaling integration) |

> [!IMPORTANT]
> **Smart App Control** may interfere with this application. If it doesn't work correctly, you may
> need to disable Smart App Control in Windows Security. _(Other handheld software like ASUS Armoury
> Crate hits the same Smart App Control issue.)_

---

## 🛠️ Technology

Free and open source. Built with C#.

| Library | Purpose |
| --- | --- |
| **LibreHardwareMonitor** | Hardware sensors and monitoring |
| **RyzenAdj** | AMD TDP control (non-Legion fallback path) |
| **RTSSSharedMemoryNET** | Custom build with frametime-graph support, tuned for low CPU/memory use |
| **ADLX** | AMD Display Library for Radeon features |
| **PresentMon** | Rendered / displayed FPS and frame-generation metrics |
| **PawnIO** | Direct EC access for the Legion Go 2 custom fan curve |
| **libviiper** (usbip-win2) | USBIP-based virtual controller emulation with native gyro |

---

## 🙏 Credits

GoTweaks Lite builds on the work of the upstream projects:

- **[GoTweaks](https://github.com/corando98/GoTweaks)** by corando98 — the fork this project is based on.
- **[Original widget](https://github.com/namquang93)** by namquang93 — where GoTweaks itself grew from.

## 📄 License

GoTweaks Lite has a layered license, inherited from upstream:

- The **original source code** is licensed under the **MIT License** (see [`LICENSE`](LICENSE)).
- The **distributed binaries** link `libviiper.dll` (a GPL-3.0 fork of VIIPER), so the
  **combined work as distributed is conveyed under the GPL-3.0** (see [`COPYING`](COPYING)).

See [`LICENSING.md`](LICENSING.md) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for the full
details and the written offer of source.

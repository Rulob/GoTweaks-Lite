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
- The **Advanced panel** (core parking / affinity), the **AC/DC Power Plan** selector, and the
  debug **Themes** selector — niche, no-ops on the Legion Go 2, or superseded.
- **Controller Emulation** (the VIIPER virtual-gamepad feature: virtual Xbox 360 / DualShock 4 /
  DualSense / Switch Pro / Steam pads, gyro forwarding, the Sticks & Triggers shaping panel) and
  everything that only existed to support it. The Xbox Guide remap action now simply opens the
  Xbox Game Bar (Win+G).
- The **Prerequisites** installer card and the setup-warning banner: GoTweaks Lite no longer
  installs or asks for **PawnIO**, **usbip-win2** or **HidHide**. With PawnIO gone, the
  _Unlock Fan Curve Override_ (direct EC fan control) and the per-mode fan curves for Quiet /
  Balanced / Performance went with it — the Fan Curve card now edits the **Custom**-mode curve
  (applied through Lenovo WMI), and **Fan Full Speed** is unchanged. TDP is Lenovo WMI only.
- The **Display** tab (Resolution, Refresh Rate, HDR, Auto SDR, Adaptive Brightness, Color Settings
  and the AMD Radeon toggles: RSR, Fluid Motion Frames, Anti-Lag, Boost, Chill, Image Sharpening)
  and the matching Quick Settings tiles. Whatever those settings are set to now stays as it is.
- The **Scale** tab. Lossless Scaling is no longer configured from the widget — the **Lossless**
  Quick Settings tile (and any controller combo bound to it) still turns scaling on and off.
- On the Legion tab: **Per-Game Controller Profile** (controller settings are always global now,
  so the _Remap_ tile went too), **Nintendo Layout**, and the Labs cards **Disable Legion Space
  Service** and **Fix Task View Bug**. If you had either Labs option switched on, it stays as it was.

> **Why:** a smaller, more predictable surface with fewer background systems that can silently
> fight your settings.

### ➕ Added

- **AutoTDP (PID mode)** — an automatic power controller: pick a target FPS and a min/max wattage
  range, and it continuously adjusts TDP live to hit that FPS, backing off gracefully when the
  target is unreachable (CPU-bound games). Works from any TDP Mode — enabling it (from its card or
  the matching Quick Settings tile) automatically switches to and locks Custom mode, and restores
  your previous mode when turned off. While it runs it keeps your Custom-mode SPPT / FPPT boosts
  on top of the wattage it picks,
  and is saved with each profile (on/off, target FPS, min/max TDP). Restored from upstream, trimmed
  to the rule-based PID controller only (no Q-Learning/SARSA machine-learning modes or per-game
  learned data).
- **Brightness Gesture** — SteamOS-style brightness control: hold a configurable button and tilt a
  stick to fade the panel brightness up or down.
- **Power & Sleep card** — power-button behavior, screen-off, and a GoTweaks-owned "hibernate
  after" idle timer (input-aware, so a controller-only session isn't wrongly treated as idle),
  all restored automatically on uninstall.
- **Controller-combo tile hotkeys** — assign a 2-or-more-button controller combo (including the
  Legion back paddles) to any Quick Settings tile to activate it without touching the widget.
  _(Ported from upstream corando98/GoTweaks.)_
- **Controller Mode (X-Input / D-Input)** — a Legion Space style switch for the Legion Go / Go 2
  controllers, on the Legion tab and as an _Input Mode_ Quick Settings tile (see
  [Controller Mode](#-controller-mode-x-input--d-input)).
- **Single-file GUI installer** — `GoTweaks-Setup.exe` bundles the package + certificate and runs
  the whole install behind a small progress window (see [Installation](#-installation)).

### 🔧 Reliability fixes

- Correct **system-tray icon**, fixed **swapped CPU/GPU wattage sensors** on the Legion Go 2 so OSD
  labels match Adrenalin, true-CPU-Tctl **fan-curve temperature**,
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
- **Per-game profiles now actually apply.** The TDP Mode saved in a game profile (e.g. Performance)
  was silently never switched to on game start, and CPU Boost / EPP / CPU state could be re-applied
  from a stale copy of the profile kept by the background helper. Both are fixed: the saved mode is
  applied, and the widget now syncs its saved values into the helper's copy after each game-profile
  switch (including Custom power limits and the profile's AutoTDP settings).
- **Stable FPS counter.** The overlay's FPS is now derived from PresentMon's own per-frame
  timestamps instead of counting samples per refresh window, fixing brief spikes (e.g. a real 75
  FPS momentarily reading 130) caused by uneven delivery timing rather than an actual rate change.
- **Widget no longer goes deaf after sleep.** Sometimes, after the handheld woke from a long sleep,
  Game Bar would open the widget but nothing inside it responded: the old window was still drawing
  but had lost its link to Game Bar. The helper now tells the widget when the PC wakes, and the
  widget asks Game Bar to close that stale window so the next open is a clean launch (pinned
  widgets are left alone, since closing one would unpin it). Closed widget
  windows are also cleaned up properly now (they used to stay registered and swallow helper
  updates), the controller battery section repaints on every open, and the widget log records
  whether input reaches the widget so any future report is easier to diagnose.
- **Modern performance overlay.** A cleaner, calmer take on the RTSS overlay: small labels and
  units, soft colors that only change at real warning thresholds, whole-millisecond frametime,
  percentages for RAM/VRAM, one SoC temperature, and a new default order (FPS, CPU, GPU, RAM, VRAM,
  TEMP, TDP, BAT, clock). The classic look is one switch away. When PresentMon isn't attached the
  FPS line now falls back to RTSS's own frame stats instead of showing RTSS's decimal frametime.
- **Overlay polish.** The TDP mode name ("Balanced") is now the same size as the other values, the
  overlay brightness slider reaches true full brightness at 100% (numbers pure white, accents at
  full intensity) with everything scaling together below that, and the default brightness is 50%.
- **Profiles list shows the right TDP for native modes.** A profile set to Quiet, Balanced or
  Performance used to list the last Custom wattage (e.g. "Performance — 35W"). It now shows the
  mode's own wattage (8 / 15 / 25W); the saved Custom value is still kept and only shown for
  Custom. The "TDP (high → low)" sort follows the same numbers.
- **Custom TDP remembers your SPPT / FPPT boosts.** The two boost sliders used to start at their
  maximum (+10 / +15) every time Custom was selected. The widget now remembers the last boosts you
  chose and restores them when you switch to Custom; with nothing stored yet they start at +2 / +5.
- **AutoTDP reacts faster and its sliders behave.** AutoTDP now runs on its own 250 ms loop instead
  of the helper's ~1 s main loop, and takes bigger steps when FPS is far below target. The Min /
  Max TDP sliders stay inside the device's TDP range (no more 85 W), moving one no longer shifts
  the other's handle, and turning AutoTDP on no longer scrolls its settings out of view.

---

## 🧩 Features

### 🗂️ Quick Settings

A customizable dashboard of quick-access tiles for your most-used settings.

- One-tap toggles for TDP Mode, AutoTDP, Profile, Overlay, Lossless Scaling, and more
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
  - TDP Mode, TDP (SPL / SPPT / FPPT) and CPU settings
  - AutoTDP (on/off, target FPS, min/max TDP)
  - FPS limit and OS power mode

### 🕹️ Legion Go 2 Support

Deep support for the Legion Go 2 with automatic device detection.

**Fan & performance**
- Quiet, Balanced, Performance, and Custom modes
- Custom TDP with fine-grained control (SPL, SPPT, FPPT)
- Custom-mode fan curve (via Lenovo WMI) and a Fan Full Speed toggle

**Controller settings**
- **Button Remapping** — customize the M2, M3, Y1, Y2, Y3 buttons
- **Remap type** — map to keyboard shortcuts, keys, gamepad actions, or mouse buttons
- **Joystick to Mouse** — emulate a mouse with either stick
- **Desktop Controls** — map controls to mouse + left/right click, similar to the ROG Ally
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

### 🎮 Controller Mode (X-Input / D-Input)

The same switch Legion Space has, for the Legion Go / Go 2 controllers: flip them between
**X-Input** (the standard Xbox-style pad) and **D-Input** (a generic gamepad that a few games
and emulators prefer).

- **Legion tab → D-Input mode** switch, and an **Input Mode** tile for Quick Settings
- The card always shows the mode the controllers are _really_ in (read from their USB product
  ID), so a switch that doesn't take snaps back by itself
- With the controllers detached (Dual D-Input) or the right controller's FPS switch on, the
  hardware picks the mode and the switch is locked
- Switching reconnects the controllers for a few seconds. In D-Input the Xbox / Game Bar
  interface may stop reacting to the pads — use the touchscreen to switch back.

### 🖼️ Lossless Scaling
 Lossless Scaling is installed, the **Lossless** Quick Settings tile turns scaling on and off
(bind a controller combo to it for a hands-free toggle). Configure Lossless Scaling itself in its app.

### 📊 Performance Overlay (OSD)

A real-time on-screen display powered by RivaTuner Statistics Server.

- **Detail levels** — Off, Minimal, Standard, Detailed
- **Metrics** — FPS (rendered + displayed via PresentMon) with a `[FG]` badge, frametime graph and
  frame-budget readout, CPU/GPU usage & temperatures, power draw, memory & VRAM, battery, fan speed,
  and TDP limits (SPL/SPPT/FPPT) or the current performance mode
- **Modern style** (on by default, switch in the overlay's Layout options; off = the classic look) —
  small labels and units on the number's baseline, soft accent colors with values staying neutral
  until a real warning threshold, steady number widths, and a clean one-row bar:
  `FPS 138 7ms · CPU 12% · GPU 34% · RAM 42% · VRAM 18% · TEMP 53°C · TDP Balanced · BAT 78% -12.8W 4h 29m · 23:18`.
  CPU/GPU show usage only, RAM/VRAM show percentages, and a single **SoC temperature** replaces the
  separate CPU and GPU temperatures.
- **Brightness** — the overlay brightness slider (10–100%) now spans the whole range: at 100% the
  text is pure white with full-intensity accents, and everything scales down together as you lower
  it. It defaults to **50%** on a fresh install to protect OLED panels.

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

For a deeper clean (stops the helper, removes the scheduled task and the deployed helper copy,
then removes the app package itself), grab **`scripts/Uninstall-GoTweaks.ps1`** from the repo
(not included in the release download). GoTweaks Lite installs no drivers, so there is nothing
else to take out.

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
| **Optional** | [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) (OSD overlay) · Legion Go 2 / Legion Go (device features) · Lossless Scaling (Quick Settings toggle) |

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

---

## 🙏 Credits

GoTweaks Lite builds on the work of the upstream projects:

- **[GoTweaks](https://github.com/corando98/GoTweaks)** by corando98 — the fork this project is based on.
- **[Original widget](https://github.com/namquang93)** by namquang93 — where GoTweaks itself grew from.

## 📄 License

GoTweaks Lite has a layered license, inherited from upstream:

- The **original source code** is licensed under the **MIT License** (see [`LICENSE`](LICENSE)).
- Releases up to **1.6** shipped `libviiper.dll` (a GPL-3.0 fork of VIIPER) for the Controller
  Emulation feature, so those binaries were conveyed under the **GPL-3.0** (see [`COPYING`](COPYING)).
  Controller Emulation and that DLL have since been removed, and newer builds no longer link it.

See [`LICENSING.md`](LICENSING.md) and [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for the full
details and the written offer of source for the older releases.

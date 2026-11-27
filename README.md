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

- **Legion Go 2 only.** The build is tuned for, and since 1.9.7a only supports, the Legion Go 2
  (Ryzen Z2 Extreme). The GPD, Legion Go (1st gen) and Legion Go S code was removed.

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
- **GoTweaks Haptics** (per-button controller clicks) and the **Brightness Gesture** (hold a button
  + tilt a stick). For brightness, bind _Brightness Up / Down_ to a controller hotkey instead.
- **Power Source (AC / DC) profiles** and the Profiles tab's **"save to profile" checkboxes**:
  there is one profile per game plus Global, and every profile saves all performance settings.
  Existing AC / DC profiles keep their AC (plugged-in) copy. Support for **GPD** handhelds and the
  **Legion Go (1st gen) / Go S** was removed too (Legion Go 2 only).
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
  and the matching Quick Settings tiles. The display and Radeon settings keep whatever values they
  have now; Auto SDR no longer runs. The helper no longer probes the Radeon features at startup
  (ADLX is still used for GPU metrics and the AMD overlay).
- The **Scale** tab. Lossless Scaling is no longer configured from the widget (no more settings or
  per-game profile syncing) — the **Lossless** Quick Settings tile (and any controller combo bound to
  it) still turns scaling on and off, launching Lossless Scaling first if needed.
- On the Legion tab: **Per-Game Controller Profile** (controller settings are always global now,
  so the _Remap_ tile went too), **Nintendo Layout**, and the Labs cards **Disable Legion Space
  Service** and **Fix Task View Bug**. If Legion Space's service was disabled it stays disabled (the
  uninstaller still restores it); the once-per-boot Task View fix no longer runs. The helper still
  power-cycles the controller's USB port on its own when the controller stops responding.

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
- **Power & Sleep card** — power-button behavior, screen-off, and a GoTweaks-owned "hibernate
  after" idle timer (input-aware, so a controller-only session isn't wrongly treated as idle),
  all restored automatically on uninstall.
- **Controller-combo tile hotkeys** — assign a 2-or-more-button controller combo (including the
  Legion back paddles) to any Quick Settings tile to activate it without touching the widget.
  _(Ported from upstream corando98/GoTweaks.)_
- **Controller Mode (X-Input / D-Input)** — a Legion Space style switch for the Legion Go 2
  controllers, on the Legion tab and as an _Input Mode_ Quick Settings tile (see
  [Controller Mode](#-controller-mode-x-input--d-input)).
- **New look (1.9.9a).** A darker, controller-first design across every tab: icon tabs, bigger
  buttons and rounded cards. The Quick tab opens with a power card (a dial showing the watts, the
  firmware preset, or AutoTDP's live FPS; four TDP mode buttons; the Custom wattage; AutoTDP),
  then the sensors as bars, then the tiles, now two per row with a coloured icon when on (3 or 4
  per row is still in Customize). The Performance tab is now called Power, and its TDP mode and
  Windows power mode use buttons instead of dropdowns, with FPS limit presets (30/40/45/60). The
  Profiles tab opens with a Now playing card whose buttons change TDP, AutoTDP, FPS limit, the
  overlay, the Windows power mode and CPU boost directly.
- **A fixed Quick tab that fits without scrolling (1.9.9b).** The power card is compact: the four
  TDP mode buttons 2 × 2 with the dial beside them (in Custom the dial shrinks and a mini slider sets
  the watts), and one AutoTDP row with the switch on the left and a − / + target FPS on the right.
  Below it: the brightness slider, eight toggles (Power Mode, EPP, CPU Boost, FPS Limit,
  Touchscreen, Touchpad, Vibration Mode, Overlay) and one row of actions (Keyboard, End Task, Task
  Manager, Explorer). Customize, the sensor row, custom shortcut tiles and tile controller combos
  were removed from this tab.
- **Quick tab polish (1.9.9c / 1.9.9d).** Everything fits without scrolling. Every toggle, slider,
  checkbox and selected button on every tab follows the Windows accent colour, and picks up a new
  accent straight away (status colours such as battery levels keep their meaning). The dial shows
  the Custom wattage, or with AutoTDP on the wattage it is running at, placed between its Min and Max
  TDP; in Quiet / Balanced / Performance (the firmware doesn't report a preset's own limits) it
  shows the SoC's live power draw. Picking a TDP mode while AutoTDP runs
  turns AutoTDP off and switches to it. The overlay's TDP item shows the limit and then the SoC's
  live draw in brackets, e.g. `TDP 25 (17W)` (`TDP Balanced (17W)` in a preset); SPPT / FPPT were
  dropped from it. Since 1.9.9f the SoC draw is the chip's whole package power from Windows' own
  energy meter (AMD RAPL, no driver needed), unlike BAT,
  which is the whole device's battery draw (screen included). 1.9.9e fixes a 1.9.9d helper crash
  (a few seconds after the widget opened) caused by reading that value through ADLX every tick.
  1.9.9f: earlier builds read a graphics-only sensor there, so it showed too little (8 W at 26 W
  battery draw).
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
- **AutoTDP Fast response** (on by default, switch on the AutoTDP card). When FPS is far from the
  target, AutoTDP estimates the wattage it needs and jumps there in one step (up to +8 / −6 W),
  waits ¾ s for it to settle, then fine-tunes as before; it also probes for a lower wattage sooner
  once on target. Turn it off for the previous, gentler behaviour.
- **Show GoTweaks first in Game Bar** (Legion tab → Game Bar). Puts the widget in Game Bar's
  device-maker slot, left of Home, where Legion Space normally sits, so it opens straight from the
  Game Bar button. Game Bar closes once to apply it; open it again. Turning it off puts the
  previous widget (e.g. Legion Space) back, and so does the uninstaller. (Game Bar only uses that
  slot for an app with a single widget, so the old, unused "GoTweaks Lite Settings" widget and the
  gear button on the title bar were removed.)
- **Brightness hotkeys.** The Hotkeys card (View / Menu + button combos) has two new actions,
  _Brightness Up (+5%)_ and _Brightness Down (-5%)_. Each press moves the panel brightness one 5%
  step (0, 5, 10 … 100), and the brightness slider follows. Keep Menu / View held and tap the
  second button again for more steps.
- **Customizing Quick Settings no longer jumps to the top.** Showing or hiding a tile (tap it twice)
  in customize mode now updates that tile in place, and swapping two tiles keeps the scroll
  position and focus. Reordering the metrics row with its up / down arrows does the same.
- **Metrics row works without PawnIO.** CPU temperature and CPU / GPU wattage need the PawnIO
  driver, which GoTweaks Lite no longer installs, so on a fresh Windows they showed "--". The row
  now uses the overlay's values: one **SoC Temp** (CPU temperature, or the AMD GPU sensor when that
  isn't available; both are on the same chip) and **TDP** (the mode name in Quiet / Balanced /
  Performance, the TDP limit in Custom). Saved selections move over automatically. The fan-curve
  temperature uses the same fallback, and the row no longer misreads decimals on systems that use
  a comma as the decimal separator.
- **Overlay on/off is saved to the profile however it's changed.** The Quick Settings tile and the
  hotkey didn't update the profile, so the Profiles tab showed the wrong state and opening Game Bar
  could switch the overlay back. Game profile cards now show the Overlay setting too.
- **AutoTDP on the Global profile returns to your mode between games.** With AutoTDP on and no
  per-game profile, the device used to stay in Custom at AutoTDP's last wattage after a game
  closed. Now, 5 seconds after the game closes, it goes back to the mode you had before turning
  AutoTDP on (Quiet, Balanced or Performance), and it switches to Custom again when the next game
  starts. AutoTDP stays on. (After updating, turn AutoTDP off and on once so it records that mode.)
- **Focus GoTweaks opens on GoTweaks.** The _Focus GoTweaks Lite_ action (hotkeys, Legion L / R,
  scroll wheel) uses Game Bar's own link to open straight on the widget, or switch to it when Game
  Bar is already open, including in the slot left of Home; before, it always landed on Home. With
  _Show GoTweaks first_ on, _Open Game Bar_ opens on GoTweaks too, like Legion L does for Legion
  Space. If the link doesn't open Game Bar within 3 seconds, Win+G is used as before.
- **One copy of every profile.** The widget and the background helper used to keep their own
  copies of each profile and re-sync them constantly, the source of settings that changed by
  themselves (e.g. the overlay or TDP mode flipping when Game Bar opened, or the Profiles tab
  showing values that weren't applied). Now only the helper keeps profiles: it applies them when a
  game starts or closes, records every change in the active one, and the widget just shows them.
  On the first start of this version the widget hands its FPS limit, power mode, overlay and
  Custom SPPT / FPPT values over to the helper once.
- **Controller settings: one device-wide copy.** Button remaps, gyro, deadzones, triggers,
  joystick mouse, vibration and lighting are kept only by the helper, the same for every game,
  and applied at startup. Starting a game with its own profile no longer resets the gyro to its
  defaults, and deadzone / trigger changes made during a game are no longer lost.
- **Fix: controller settings stuck on their defaults** (1.9.7a / 1.9.7b). Stick deadzones read
  4%, trigger travel couldn't change and the light brightness showed 100%, because the widget
  still refused these values from the helper. The FPS limit toggle / slider, the back-button remap
  controls, vibration, gyro and lighting controls had also stopped sending their changes. All
  fixed; the helper now only answers the widget's first sync after applying its saved settings.
- **Quick Settings: TDP Mode and AutoTDP boxes.** Two boxes above the tiles, side by side, show the
  current TDP mode and AutoTDP state. Tap one to expand it: TDP Mode has Quiet / Balanced /
  Performance / Custom plus a TDP slider (usable in Custom), AutoTDP has an on/off switch and the
  target FPS slider. They replace the TDP Mode and AutoTDP tiles. The brightness slider now moves
  in 5% steps.
- **Removed:** Desktop Controls (and its Ctrl+Shift+D hotkey), Joystick as Mouse (switched off on
  both sticks at startup), the 80% Charge Limit, and the fan curve editor. The fan always follows
  the firmware curve of the TDP mode; in Custom, GoTweaks writes Lenovo's stock curve (the
  firmware doesn't expose its Balanced curve to copy).
- **Reset All Settings** (System → Maintenance). Puts GoTweaks' settings back to a fresh install:
  every setting, the Global profile, the Quick Settings and overlay layouts and the hotkeys go
  back to their defaults, and the controller gets the default buttons, deadzones, triggers,
  vibration and lighting. Game profiles are deleted unless you choose "Keep game profiles".
  Windows settings are left alone. Everything is backed up to a Desktop folder first (Import All
  Data brings it back), then the helper restarts and GoTweaks closes.
- **The background helper runs on .NET 10** (it was .NET Framework 4.8, which gets no new
  development). It ships with its own copy of the runtime, so there is still nothing to install;
  the download is larger because of that. The RTSS overlay link was rewritten in plain C# for it
  (the old one could not run on .NET 10), reading and writing RTSS's shared memory as the RTSS SDK
  documents. Errors in background tasks are now caught and logged where .NET Framework used to
  ignore them silently. For troubleshooting, `XboxGamingBarHelper.exe --self-test` checks the
  runtime, Windows APIs, WMI, HID, RTSS and the native libraries without changing anything and
  writes the result to `%TEMP%\GoTweaks-SelfTest.txt`.
- **Export All Data now saves all widget settings** (it used to save a short list) and the
  helper's real settings file (it was saving an old copy).
- **GoTweaks always takes Game Bar's first slot** (left of Home, where Legion Space sits); the
  "Show GoTweaks first" toggle was removed. Prepare for Uninstall still gives the slot back.
- **Custom TDP remembers its wattage.** Switching to Custom (Performance tab or the Quick Settings
  TDP Mode box) uses the TDP last set for the active profile instead of starting at 15 W. The
  Custom TDP slider now tops out at 35 W, the Legion Go 2's maximum.
- **Layout:** AutoTDP sits above TDP Mode on the Performance tab, the Current Profile card moved
  to the Profiles tab, and the Game Detection card was removed (its two options keep their
  current values). On the Legion tab, Button Remapping now holds every remappable button: Front
  Buttons (Legion L, Legion R, Desktop, Page) then Back Buttons (Y1-3, M1-3, Scroll, Scroll Click),
  so the Special Remapping card is gone; the Hotkeys card moved from System to right below it.
---

## 🧩 Features

### 🗂️ Quick Settings

One screen, no scrolling, built for the controller.

- TDP mode buttons with a dial (Custom watts, or with AutoTDP its live watts between Min and Max) and a mini Custom slider
- AutoTDP on/off with a − / + target FPS
- Built-in screen brightness
- Toggles: Power Mode, EPP, CPU Boost, FPS Limit, Touchscreen, Touchpad, Vibration Mode, Overlay
- Actions: on-screen keyboard, End Task, Task Manager, File Explorer

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

- Automatic profile switching on game detection, done by the background helper (it works with
  Game Bar closed)
- Every profile (Global and each game) saves all performance settings:
  - TDP Mode, TDP (SPL / SPPT / FPPT) and CPU settings
  - AutoTDP (on/off, target FPS, min/max TDP)
  - FPS limit, OS power mode and the overlay on/off

### 🕹️ Legion Go 2 Support

Deep support for the Legion Go 2 with automatic device detection.

**Fan & performance**
- Quiet, Balanced, Performance, and Custom modes
- Custom TDP with fine-grained control (SPL, SPPT, FPPT)
- Firmware fan curves in every mode (Lenovo's stock curve in Custom), a live fan RPM / CPU
  temperature readout and a Fan Full Speed toggle

**Controller settings**
- **Button Remapping** — customize the M2, M3, Y1, Y2, Y3 buttons
- **Remap type** — map to keyboard shortcuts, keys, gamepad actions, or mouse buttons
- **Stick Deadzones** — adjust left/right stick deadzones (0–50%)
- **Gyroscope** — enable/disable with target selection, X/Y sensitivity, axis inversion, and
  activation mode (Hold/Toggle) with a button binding
- **Vibration Mode** & **Touchpad Haptics** — configure vibration behavior and haptic feedback

**RGB lighting**
- Light mode, color, brightness, and speed control
- Power-light toggle

**Other**
- Touchpad toggle
- Battery charge limit
- Show GoTweaks first in Game Bar (takes Legion Space's slot left of Home)

### 🔋 Power & Sleep

Windows power behavior, read and written directly against the matching power-plan settings and
restored on uninstall.

- **Power Button behavior** and **Turn off screen after** (AC / DC)
- **Hibernate after** — a GoTweaks-owned, input-aware idle timer (Windows has no built-in one)
- **Disable Sleep Timer** shortcut

### 🎮 Controller Mode (X-Input / D-Input)

The same switch Legion Space has, for the Legion Go 2 controllers: flip them between
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
  and the TDP limit (or the performance mode) with the SoC's live power draw in brackets
- **Modern style** (on by default, switch in the overlay's Layout options; off = the classic look) —
  small labels and units on the number's baseline, soft accent colors with values staying neutral
  until a real warning threshold, steady number widths, and a clean one-row bar:
  `FPS 138 7ms · CPU 12% · GPU 34% · RAM 42% · VRAM 18% · TEMP 53°C · TDP Balanced (17W) · BAT 78% -12.8W 4h 29m · 23:18`.
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
| **Optional** | [RivaTuner Statistics Server](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) (OSD overlay) · Legion Go 2 (device features) · Lossless Scaling (Quick Settings toggle) |

> [!IMPORTANT]
> **Smart App Control** may interfere with this application. If it doesn't work correctly, you may
> need to disable Smart App Control in Windows Security. _(Other handheld software like ASUS Armoury
> Crate hits the same Smart App Control issue.)_

---

## 🛠️ Technology

Free and open source. Built with C#: the widget is a UWP Game Bar widget, the background helper
a self-contained .NET 10 app (it carries its own runtime, so nothing extra needs installing).

| Library | Purpose |
| --- | --- |
| **LibreHardwareMonitor** | Hardware sensors and monitoring |
| **RyzenAdj** | AMD TDP control (non-Legion fallback path) |
| **RTSS shared memory** | Overlay text, embedded frametime graph and per-game FPS, read and written directly (following the RTSS SDK) |
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

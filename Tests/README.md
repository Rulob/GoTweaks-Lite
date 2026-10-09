# GoTweaks unit tests

Unit tests for the **hardware-independent logic** in the `Shared` project — the
code that runs identically on both the widget and the helper and needs no Game
Bar, handheld, or AMD/Legion hardware to exercise. This is the "Tier 1" safety
net: it lets the fragile, pure-logic pieces be changed and verified on any dev
box (and in CI) instead of only on a handheld.

## Running

```
dotnet run --project Tests/Shared.Tests
```

(or build and run the produced `Shared.Tests.exe`). Uses **xUnit v3** in
Microsoft Testing Platform mode — the test project is a self-contained
executable, so no `Microsoft.NET.Test.Sdk` / VSTest is required. Exit code is
non-zero if any test fails.

The test project is **intentionally not part of `XboxGamingBar.sln`** so it
doesn't disturb the production MSIX build or an open Visual Studio instance.

## What's covered

| File | Subject | Why it matters |
|------|---------|----------------|
| `PipeMessageTests.cs` | `Shared.IPC.PipeMessage` ToJson/FromJson round-trip, numeric/bool/escaped content, malformed input | The hand-rolled (regex) JSON serializer for **all** widget↔helper IPC — the most fragile part of the wire layer |
| `LegionControllerModesTests.cs` | `Shared.Data.LegionControllerModes` USB product-ID / VID:PID → X-Input / D-Input / Dual D-Input / FPS | The Controller Mode switch trusts this mapping to report what the controllers are really in; a wrong PID here shows the wrong state or hides the switch |

## Project layout / build notes

- **TFM `net10.0-windows10.0.26100.0`** — `Shared` references the raw
  `Windows.winmd` (for `Windows.Foundation.Collections.ValueSet`), i.e. it "uses
  built-in WinRT". A non-windows TFM can't reference such an assembly
  (NETSDK1149), so the test project targets the windows TFM. The tested types
  never touch `ValueSet`, so no WinRT type is resolved at runtime.
- **`strip-winmd-deps.ps1` + the `StripWinmd*` targets** — `Shared` copy-locals
  its `Windows.winmd`, which would otherwise land in `deps.json` as a runtime
  assembly. A `.winmd` in the CoreCLR TPA makes the runtime fail to start
  ("Failed to create CoreCLR, HRESULT: 0x80070057"). The targets strip it from
  the output and the generated `deps.json`.

## Known gap: GenericProperty conflict-resolution tests

The single highest-value target — the timestamp-based conflict resolution in
`GenericProperty<T>.SetValue` (the core of widget↔helper sync) — is **not** unit
tested here. Any concrete `FunctionalProperty` subclass has
`SendMessageAsync(Windows.Foundation.Collections.ValueSet)` in its signature, so
**loading the type** forces the WinRT `ValueSet` type to resolve, and CsWinRT
throws `PlatformNotSupportedException` in a non-packaged test host — before any
test body runs. Setting `SuppressRemoteSync` / overriding `NotifyPropertyChanged`
doesn't help, because the failure is at type-load, not on a value-change path.

This is concrete motivation for the proposed Tier-2 refactor: **decouple the
pure conflict-resolution logic from `ValueSet`** (e.g. extract it into a plain
method/type in `Shared` that the WinRT-coupled `GenericProperty` calls into).
Once decoupled, those tests drop straight in here. Alternatively they can run in
a packaged/UWP test host, but that reintroduces the hardware-host friction this
suite exists to avoid.

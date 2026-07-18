# Task 5 Report: Lightweight Hardware Detection and Capability Presentation

## Status

Implemented checkpoint `hardware-capabilities`.

The app now starts a cached, read-only capability snapshot after the first status refresh without
awaiting it in the activation path. Probe startup is moved to the thread pool, all eight probes run
concurrently, and each has an independent two-second presentation-time budget. A throwing, canceled,
or timed-out probe degrades only that capability to `Unknown`; a probe that ignores cancellation no
longer holds up the aggregate result, and its late fault is observed.

## Implementation

- Added `IHardwareCapabilityProbe`, `HardwareCapabilityService`, and
  `WindowsHardwareCapabilityProbe`.
- Added partial-failure, non-cooperative timeout, concurrency, and process-cache tests.
- Reused `MonitoringService`'s bounded process runner for brightness, `nvidia-smi`, and temperature
  checks. Capability detection does not call `SampleAsync`, generate a battery report, or export a
  diagnostic package.
- Reused the existing power-status API for battery state, `EnumDisplayDevices` for an NVIDIA display
  adapter, and .NET network-interface enumeration for a WiFi adapter.
- Reused the actual tray-icon registration and all four `RegisterHotKey` results for notification and
  global-hotkey availability.
- Applied `CapabilityVisibilityPolicy` to the main telemetry cards, WiFi actions, and applicable
  Settings controls. Simple mode hides unavailable features; Professional mode keeps them visible,
  disabled, and annotated with both a ToolTip and automation help text.
- Kept the four core mode buttons outside capability presentation and unchanged.
- Reapplies capability policy after generic busy-state control restoration so unavailable actions
  cannot be accidentally re-enabled.

## Capability Evidence

| Capability | Supported | Unsupported | Unknown |
| --- | --- | --- | --- |
| Battery | `GetSystemPowerStatus` returns a known battery state/percentage | `BatteryFlag` reports no battery | API result is unavailable or indeterminate |
| Brightness | `root/WMI:WmiMonitorBrightness` returns an instance | Query succeeds with no instance | Query fails or times out |
| NVIDIA GPU | `EnumDisplayDevices` reports NVIDIA in adapter name/ID | Adapter enumeration completes without NVIDIA | Probe throws |
| `nvidia-smi` | Lightweight name query succeeds with output | Process cannot start or succeeds without GPU output | Process times out or starts but fails |
| WiFi control | A `Wireless80211` interface is enumerated | Enumeration succeeds with no wireless interface | Probe throws |
| Temperature | `MSAcpi_ThermalZoneTemperature` returns an instance | Query succeeds with no instance | Query fails or times out |
| Notifications | Existing tray icon registration succeeded | Tray icon registration failed | Probe throws |
| Global hotkeys | All four existing hotkey registrations succeeded | Any required registration failed | Probe throws |

## TDD Evidence

- Initial RED: capability test compilation failed with `CS0246` because
  `IHardwareCapabilityProbe` did not exist.
- Second RED: a non-cooperative two-second temperature probe returned `Supported` after two seconds
  instead of timing out, and a second detection created a new snapshot instead of using the cache.
- GREEN: capability/policy-focused tests passed, 9/9.

## Verification

- Focused:
  `dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~CapabilityVisibilityPolicyTests|FullyQualifiedName~HardwareCapabilityServiceTests"`
  — passed 9/9, failed 0, skipped 0.
- Full:
  `dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64`
  — passed 22/22, failed 0, skipped 0.
- Release:
  `dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 --no-restore`
  — succeeded with 0 warnings and 0 errors.
- Static review: `git diff --check`
  — clean.

## Concerns and Deferred Manual Check

- No GUI process was launched because the task forbids taking over the user's foreground. Automated
  coverage proves the aggregate returns within its budget even when a probe ignores cancellation,
  and source review confirms detection is fire-and-forget after the first status refresh.
- Some laptop firmware does not expose brightness or ACPI temperature WMI classes even when vendor
  utilities can read them. Those cases intentionally become `Unsupported` after a successful empty
  query; query failures remain `Unknown`.
- A failed hotkey registration can mean another application owns the chord rather than an OS
  limitation. It is still `Unsupported` for the current PowerMode process because the advertised
  shortcut is unavailable.
- A third-party fake probe that ignores cancellation can continue in the background after the caller
  receives `Unknown`; production process-backed probes honor cancellation and kill their child
  processes, and late task faults are observed.

## Independent Review Fixes

Review status changed from `Needs fixes` after the following six isolated RED/GREEN cycles.

### 1. Synchronous probe isolation

- **RED:** `DetectAsync_SynchronouslyBlockingProbe_DoesNotBlockOtherProbesOrCaller` blocked for about
  two seconds and returned `Supported` after a fake performed `Thread.Sleep` before returning its
  `Task`.
- **GREEN:** `SafeProbeAsync` now starts the delegate itself with `Task.Run`, then applies the
  independent wait budget externally. The focused test returned in 64 ms, preserved
  `Notifications=Supported`, and degraded only temperature to `Unknown`.
- **Files:** `HardwareCapabilityService.cs`, `HardwareCapabilityServiceTests.cs`.

### 2. Caller cancellation and cache integrity

- **RED:** `DetectAsync_CallerCancelsWait_DoesNotCacheCanceledResults` expected caller cancellation,
  but the service swallowed cancellation into an all-`Unknown` cached snapshot.
- **GREEN:** the cached detection uses the service-lifetime token; each caller independently waits
  with its own token. A 20 ms canceled caller now receives `OperationCanceledException`, while the
  same eight-probe detection finishes and a later uncanceled caller receives the supported results.
- **Files:** `HardwareCapabilityService.cs`, `HardwareCapabilityServiceTests.cs`.

### 3. WMI result classification

- **RED:** six `WindowsCapabilityProbeResultTests` cases failed to compile because the classifier
  seam did not exist.
- **GREEN:** one pure `ClassifyWmiCapabilityProbeResult` method distinguishes explicit
  `supported`, explicit `unsupported`, and all failed/ambiguous outcomes. Brightness and capability
  temperature scripts use `ErrorAction Stop`, emit an explicit marker on a reliable query, and exit
  nonzero on provider, permission, RPC, or command failure. The six cases pass.
- **Files:** `MonitoringService.cs`, `WindowsCapabilityProbeResultTests.cs`.

### 4. Window-close lifetime

- **RED:** lifecycle tests failed to compile because the hardware service was not disposable and no
  presentation lifetime gate existed.
- **GREEN:** service disposal cancels in-flight probes and rejects new detection. Main-window closing
  disposes both detection and presentation lifetimes. The callback checks the closing state before
  enqueue and again inside `TryApply`; callback exceptions remain inside that safe boundary.
- **Files:** `HardwareCapabilityService.cs`, `MainWindow.Features.cs`,
  `CapabilityPresentationLifetimeTests.cs`, `HardwareCapabilityServiceTests.cs`.

### 5. Settings brightness container

- **RED:** `SettingsWindow_BrightnessPresentation_UsesNamedContainer` found no named parent around
  the title, value, and slider.
- **GREEN:** `BrightnessSettingsPanel` now owns all three. Simple mode collapses the whole panel;
  Professional mode leaves it visible with policy ToolTip/automation help, dims the container, and
  disables the slider.
- **Files:** `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`,
  `SettingsCompatibilityTests.cs`.

### 6. Late fault observation race

- **RED:** the source regression test found
  `catch (OperationCanceledException) when (!probeTask.IsCompleted)`, which could skip observation
  if completion raced with cancellation.
- **GREEN:** every cancellation-winner path unconditionally attaches the late observer. The
  non-cooperative probe test now faults two seconds after the aggregate already returned `Unknown`;
  the late fault is observed without affecting the caller.
- **Files:** `HardwareCapabilityService.cs`, `HardwareCapabilityServiceTests.cs`.

## Independent Review Verification

- Hardware service focused:
  `dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~HardwareCapabilityServiceTests"`
  — passed 8/8, failed 0, skipped 0.
- Policy, Settings, WMI classifier, and presentation lifetime:
  `dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~CapabilityVisibilityPolicyTests|FullyQualifiedName~SettingsCompatibilityTests|FullyQualifiedName~WindowsCapabilityProbeResultTests|FullyQualifiedName~CapabilityPresentationLifetimeTests"`
  — passed 17/17, failed 0, skipped 0.
- Full:
  `dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64`
  — passed 35/35, failed 0, skipped 0.
- Release:
  `dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 --no-restore`
  — succeeded with 0 warnings and 0 errors.
- Static:
  `git diff --check`
  — clean.

No GUI process was launched during review remediation.

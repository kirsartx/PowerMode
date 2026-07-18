# Task 6 Report: Recommendation Card and Explicit One-Click Apply

## Status

Implemented the local recommendation card and explicit one-click apply flow.
Recommendations are rendered without changing the active mode. Only
`ApplyRecommendationButton_Click` enters the existing
`RunModeWithContextAsync` pipeline.

## Implementation

- Added a Fluent recommendation card above the four standard mode buttons.
  It uses theme resources, the system accent style, a polite accessibility
  live region, and no continuous animation.
- Added `RecommendationUiLogic`, a pure helper for:
  - mapping native power status without assuming AC when status is unknown;
  - building `RecommendationContext` with caller-supplied `EvaluatedAt`;
  - one semantic refresh signature that ignores `EvaluatedAt` and battery
    percentage changes that do not cross the configured threshold;
  - complete/incomplete localized presentation;
  - mapping a recommendation to trigger `recommendation`, preserving its
    reason and disabling preview.
- Recommendation input reuses the configured remote/performance process
  lists and low-battery threshold, `GetSystemPowerStatus`, current process
  names, `_temperatureProtectionActive`, and `_hardwareCapabilities`.
- Refreshes are requested after initial settings load, capability detection,
  semantic changes observed by the existing feature timer, temperature
  protection transitions, and settings application. The timer has a
  re-entry guard, and the UI is updated only when the semantic signature
  changes.
- Failed power or process-state capture is best-effort and produces an
  incomplete recommendation instead of assuming that state is known.

## TDD Evidence

- RED 1: `RecommendationUiLogicTests` failed to compile because
  `RecommendationUiLogic` and its records did not exist.
- GREEN 1: the new helper passed all 15 initial focused tests.
- RED 2: the unavailable-process-snapshot test failed because the helper did
  not yet accept `runningProcessesAvailable`.
- GREEN 2: the focused unavailable-snapshot test passed after the minimal
  completeness mapping was added.

## Verification

- Focused recommendation tests: 25 passed, 0 failed.
- Full Debug test suite: 51 passed, 0 failed, 0 skipped.
- Release x64 solution build: succeeded with 0 warnings and 0 errors.
- Static structure verification:
  - XAML parses;
  - card/button/accent/live-region structure is present;
  - no continuous animation is present;
  - refresh code does not call the switching pipeline;
  - the click handler does call `RunModeWithContextAsync`;
  - trigger and reason mapping are preserved;
  - recommendation logic adds no CLI or `powercfg` path;
  - `ModeRecommendationService` does not read the clock.
- `git diff --check`: passed (Git reported only the repository's normal
  LF-to-CRLF conversion notices).

## Deferred Runtime Check

Per the task coordination constraint, the GUI was not launched and no
foreground window was taken. Startup rendering and click behavior were
verified by tests, compilation, and static call-path checks rather than an
interactive runtime session.

## Self-Review

No critical or important issue found. The remaining risk is visual behavior
at narrow widths because no foreground GUI/DPI pass was run in this task.

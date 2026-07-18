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

## Independent Review Fixes

An independent review requested three important fixes and one related minor
refresh fix. All four were implemented:

1. `RecommendationContext.OnBattery` is now `bool?`. Unknown power stays
   `null` through native-state mapping, service evaluation, and the semantic
   refresh key. The service returns a neutral, incomplete recommendation
   instead of claiming AC or battery state. A transition from unknown to
   known AC now refreshes.
2. The apply button uses a pure state helper. It is disabled and reads
   `当前模式` / `Current mode` when the recommendation matches the existing
   successful `LastMode`; it is disabled and reads `应用中…` / `Applying…`
   while a switch is running. A dedicated interlocked gate prevents a rapid
   second click from entering the existing switch pipeline.
3. `ModeRecommendation` retains its transparent `Reason` compatibility field
   and now also carries a stable `RecommendationReasonCode`. The presentation
   helper maps that code to complete Chinese or English copy without adding a
   resource subsystem. Automation help text and the switch-history reason
   both receive the exact visible localized reason, including the incomplete
   prefix.
4. The semantic refresh key now uses nullable power state, so `null` and
   `false` are distinct.

### Review TDD Evidence

- RED: nullable-power tests failed because the model accepted only `bool`.
- GREEN: unknown-power service/context/refresh tests passed after the model
  and service preserved `null`.
- RED: reason-code and localized-presentation tests failed because no stable
  code existed.
- GREEN: all seven service reason branches and Chinese/English presentation
  tests passed after the compatibility-preserving code mapping was added.
- RED: apply-state and concurrent-gate tests failed because neither helper
  existed.
- GREEN: six current/applying/ready state cases and the concurrent second
  request test passed after the minimal state helper and gate were added.

### Review Verification

- Focused recommendation tests: 44 passed, 0 failed.
- Full Debug test suite: 69 passed, 0 failed, 0 skipped.
- Release x64 solution build: succeeded with 0 warnings and 0 errors.
- Static checks confirmed nullable power end to end, one gated handler call to
  `RunModeWithContextAsync`, visible reason reuse, no direct UI use of the raw
  Chinese reason, no new timer, no switch call from refresh, and no service
  clock read.
- `git diff --check`: passed with only normal LF-to-CRLF notices.
- The GUI was not launched.

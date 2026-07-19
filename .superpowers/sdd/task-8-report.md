# Task 8 Report: Recovery Center Window and Safe Operations

## Status

Implemented the WinUI recovery center and connected it to the existing mode,
configuration-backup, settings, capability, and recommendation pipelines.
No GUI process was launched during verification.

## Implementation

- Added a Mica-backed `RecoveryCenterWindow` with exactly three Fluent
  `PanelStyle` cards:
  - latest eligible mode switch and undo;
  - latest configuration backup distinct from the live settings and restore;
  - safety-backed reset to defaults.
- Added an always-visible main-window recovery entry outside
  `ProfessionalQuickActions`. Repeated activation reuses the existing window;
  closing it clears the retained reference.
- Added confirmation dialogs with an explicit `Root.XamlRoot`, a single
  non-closable result/progress `InfoBar`, operation-time button disabling, and
  error-to-InfoBar handling.
- Added `SwitchRequestContext.RecordHistory`. Recovery undo calls the existing
  `RunModeWithContextAsync` pipeline with `RecordHistory: false`, executes the
  original operation's `PreviousMode`, and then records exactly one linked
  `mode-undo`.
- Added `ProductionRecoveryBackend`. Its production constructor:
  - creates safety backups only through
    `SystemIntegrationService.CreateConfigurationBackupAsync` for
    `SettingsStore.FilePath`;
  - saves only `new PowerModeSettings()` through `SettingsStore.Save`;
  - never invokes the CLI, `powercfg`, or a mode-switch method.
- Configuration restore continues to use the existing atomic restore with
  `createSafetyBackup: true`, then loads and applies the restored settings
  before recording `configuration-restore`.
- Default reset runs in the required order: safety backup, save fresh defaults,
  reload/apply settings, then record `configuration-reset`. History, backups,
  and telemetry stores are not deleted or rewritten.
- Insights now formats old/default `mode-switch`, `mode-undo`,
  `configuration-restore`, `configuration-reset`, and unknown future operation
  kinds safely.
- Preserved the caller synchronization context when invoking the WinUI mode
  pipeline from recovery undo.

## TDD Checkpoints

Observed expected RED failures before production changes for:

- missing `ProductionRecoveryBackend` and backup-failure side-effect seam;
- missing exactly-one `UndoLatestAsync`;
- missing recovery operation formatter;
- missing recovery window and main-window entry;
- missing `RecordHistory: false` orchestration;
- reset/restore apply-before-record ordering;
- initially enabled recovery actions;
- lost caller synchronization context before invoking the mode pipeline.

Each checkpoint was followed by a focused GREEN run.

## Verification

- Focused recovery tests:
  - `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --filter "FullyQualifiedName~RecoveryServiceTests|FullyQualifiedName~RecoveryCenterPresentationTests" --no-restore`
  - Result: 28 passed, 0 failed.
- Full Debug suite:
  - `dotnet test PowerMode.slnx -c Debug --no-restore`
  - Result at checkpoint: 96 passed, 0 failed.
- Release x64 build:
  - `dotnet build PowerMode.slnx -c Release -p:Platform=x64 --no-restore`
  - Result: succeeded, 0 warnings, 0 errors.
- Final Release x64 full suite after the UI-context fix:
  - `dotnet test PowerMode.slnx -c Release -p:Platform=x64 --no-restore`
  - Result: 97 passed, 0 failed.
- `git diff --check`
  - Result: clean.

## Self-review

- Confirmed the Simple/Professional visibility boundary in XAML.
- Confirmed the single-instance close/reopen lifecycle.
- Confirmed restore/reset orchestration contains no active-plan operation.
- Confirmed reset does not continue to save after backup failure.
- Confirmed undo produces one linked record and suppresses the regular
  `mode-switch` record.
- Confirmed known and unknown history kinds format without exceptions.

## Remaining concern

Interactive foreground verification was intentionally not performed because the
task prohibited launching the GUI. XAML compilation, source-structure tests,
unit tests, and Release x64 build cover the required behavior without taking
focus.

## Changes-requested follow-up

Addressed all eight recovery review findings:

- Recovery mutations and availability reads now share one operation gate.
  Completed mutations retain a stable audit entry in the service session, so
  an append retry cannot repeat undo, restore, or reset side effects.
- `HistoryStore.RecordAsync` checks the stable entry ID while holding its
  per-file gate. Tests cover duplicate calls plus audit failures both before
  and after the JSONL append.
- Failed or throwing mode-pipeline execution never writes `mode-undo`.
  Recovery results separately report mutation and audit phases, including
  partial success when only the audit append fails.
- Recovery-window work is linked to both window and owner lifetime
  cancellation. Closing prevents every later presentation update, while main
  shutdown waits for the recovery gate before disposing system integration.
- Restore candidates are strictly deserialized and normalized before the live
  file is touched. The restored file is strictly reloaded/applied, and a
  post-restore validation or cancellation failure restores the safety backup
  using a non-cancelled token. Reset also strictly reloads before audit.
- The UI busy gate is acquired before showing any confirmation dialog.
  Confirmation is inside guarded `try`/`catch`/`finally` blocks, and dialog
  failures remain visible instead of being overwritten by an immediate
  availability refresh.
- Current-settings hash/read failure returns explicit unavailable state and
  never selects a fallback backup.
- Availability refresh clears retained undo/backup state before I/O, computes
  into locals, and publishes only after all reads succeed. Failure therefore
  cannot re-enable a stale Undo or Restore action.

### Follow-up TDD and verification

- Added regression coverage for concurrent undo, before/after-write audit
  failures, mutation failure without audit, reset/restore partial audit
  retries, strict settings type mismatches, restore rollback, cancellation
  during strict reload, window lifetime/busy ordering, and stale availability.
- Focused recovery/compatibility/presentation suite:
  - Result: 47 passed, 0 failed.
- Full Debug suite:
  - `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --no-restore`
  - Result: 112 passed, 0 failed.
- Full Release suite:
  - `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Release --no-restore`
  - Result: 112 passed, 0 failed.
- Release solution build:
  - `dotnet build PowerMode.slnx -c Release --no-restore`
  - Result: succeeded, 0 warnings, 0 errors.
- Final `git diff --check`:
  - Result: clean.

### Follow-up remaining concern

Interactive foreground verification remains intentionally omitted because the
task prohibits launching the GUI. The compiled WinUI project, source-structure
gates, behavioral tests, and Debug/Release suites are green.

## Blocking follow-up: pending audits and reset rollback

Resolved the two remaining recovery blockers:

- `_completedOperations` now contains only mutation-complete, audit-pending
  entries. A failed append retains the exact entry and audit ID for an
  audit-only retry; a successful or idempotent `RecordAsync` removes it.
  A later reset or restore user intent therefore creates a fresh audit ID and
  performs a fresh backup/mutation/audit. Successful undo audits are removed
  as well, after which the persisted linked undo masks the original operation.
- Reset safety backups now return a concrete `ConfigurationBackupInfo` handle.
  `ProductionRecoveryBackend` restores that handle through the existing atomic
  restore pipeline with `createSafetyBackup: false`.
- Once defaults have been saved, forward strict reload/apply runs with
  `CancellationToken.None`. If it fails, reset atomically restores the safety
  backup and strictly reloads/applies the rollback result, also with a
  non-cancelled token.
- `RecoveryActionResult.RollbackSucceeded` reports explicit reset rollback
  state. Successful rollback returns mutation/audit failure with rollback
  success and no `configuration-reset` audit. Rollback restore or strict
  reapply failure returns rollback failure with the original and rollback
  errors combined, also without an audit.
- Main-window `WaitForIdleAsync` disposal ordering and all closed-window
  presentation guards remain unchanged.

### RED/GREEN evidence

- Pending-audit RED: four repeat-intent/retry tests failed because reset and
  restore remained permanently completed after the first successful audit.
- Pending-audit GREEN: 4 passed, 0 failed after successful audit removal,
  stable pending retry IDs, and fresh IDs for later intents.
- Reset rollback RED: three tests failed because reload received the cancelled
  owner token and reset had neither a backup handle nor rollback state.
- Reset rollback GREEN: 3 passed, 0 failed after adding the minimal backend
  handle/restore contract and non-cancelled rollback flow.

### Final verification

- Focused recovery/settings/presentation suite:
  - Result: 51 passed, 0 failed.
- Full Debug suite:
  - `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --no-restore`
  - Result: 116 passed, 0 failed.
- Full Release suite:
  - `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Release --no-restore`
  - Result: 116 passed, 0 failed.
- Release solution build:
  - `dotnet build PowerMode.slnx -c Release --no-restore`
  - Result: succeeded, 0 warnings, 0 errors.
- Final `git diff --check`:
  - Result: clean.

### Remaining concern

Interactive foreground verification remains intentionally omitted because the
task prohibits launching the GUI. No GUI process was started.

# Refresh State Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent an in-flight refresh from applying a stale power state after a mode mutation begins.

**Architecture:** Add a small thread-safe `PowerStateRevisionGate` service with capture, mutation lifecycle tracking, invalidation, and applicability checks. `MainWindow` captures a revision before `ReadStateAsync`, advances the revision at every power-write boundary, blocks new refreshes while a mutation is active, and rejects stale results before any state or status presentation update.

**Tech Stack:** C#/.NET 10, WinUI 3, xUnit, existing `PowerMode.App` service and test projects.

## Global Constraints

- Preserve WinUI 3, .NET 10, Windows App SDK, x64 self-contained publishing.
- Do not add production NuGet dependencies.
- Keep `RefreshStatusAsync` read-only and preserve its timeout/error/finally cleanup behavior.
- Do not execute real power mutations in tests or desktop smoke checks.
- Use `apply_patch` for edits and keep the user-owned untracked `dsh-launcher.cmd` untouched.

---

### Task 1: Add the revision gate with behavior tests

**Files:**
- Create: `src/PowerMode.App/Services/PowerStateRevisionGate.cs`
- Create: `tests/PowerMode.App.Tests/PowerStateRevisionGateTests.cs`

**Interfaces:**
- Produces `PowerStateRevisionGate.Capture() -> long`.
- Produces `PowerStateRevisionGate.BeginMutation() -> long`.
- Produces `PowerStateRevisionGate.CanApply(long capturedRevision, bool mutationInProgress) -> bool`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void InitialRevisionCanBeApplied()
{
    var gate = new PowerStateRevisionGate();
    var revision = gate.Capture();

    Assert.True(gate.CanApply(revision, mutationInProgress: false));
}

[Fact]
public void BeginningMutationInvalidatesPreviouslyCapturedRevision()
{
    var gate = new PowerStateRevisionGate();
    var captured = gate.Capture();

    gate.BeginMutation();

    Assert.False(gate.CanApply(captured, mutationInProgress: false));
}

[Fact]
public void NewRevisionCanBeAppliedAfterMutation()
{
    var gate = new PowerStateRevisionGate();
    gate.BeginMutation();
    var captured = gate.Capture();

    Assert.True(gate.CanApply(captured, mutationInProgress: false));
}

[Fact]
public void ActiveMutationBlocksEvenTheCurrentRevision()
{
    var gate = new PowerStateRevisionGate();
    var captured = gate.Capture();

    Assert.False(gate.CanApply(captured, mutationInProgress: true));
}
```

- [ ] **Step 2: Run the focused test and verify the expected RED state**

Run:

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore --filter FullyQualifiedName~PowerStateRevisionGateTests
```

Expected: compilation/test failure because `PowerStateRevisionGate` does not exist.

- [ ] **Step 3: Implement the smallest thread-safe gate**

```csharp
namespace PowerModeWinUI;

internal sealed class PowerStateRevisionGate
{
    private long _revision;

    public long Capture() => Volatile.Read(ref _revision);

    public long BeginMutation() => Interlocked.Increment(ref _revision);

    public bool CanApply(long capturedRevision, bool mutationInProgress) =>
        !mutationInProgress && capturedRevision == Volatile.Read(ref _revision);
}
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Expected: 4/4 passed with no warnings or errors.

- [ ] **Step 5: Commit the isolated service and tests**

```powershell
git add -- src/PowerMode.App/Services/PowerStateRevisionGate.cs tests/PowerMode.App.Tests/PowerStateRevisionGateTests.cs
git commit -m "test: add power state revision gate"
```

### Task 2: Reject stale refresh results at the application boundary

**Files:**
- Modify: `src/PowerMode.App/Views/MainWindow.xaml.cs:34-645`
- Modify: `src/PowerMode.App/Views/MainWindow.AdvancedFeatures.cs:560-575`
- Modify: `tests/PowerMode.App.Tests/RefreshInteractionPresentationTests.cs`

**Interfaces:**
- `MainWindow` owns one `PowerStateRevisionGate` for the lifetime of the window.
- Refresh captures `long refreshRevision` before awaiting the backend.
- Mode transaction calls `_powerStateRevisionGate.BeginMutation()` immediately before setting `_modeSwitchInProgress = true`.
- Recovery and exit snapshot restoration call `BeginMutation()` before awaiting their write operation and `EndMutation()` in `finally`.

- [ ] **Step 1: Add a failing source contract for the stale-result boundary**

Extend `RefreshInteractionPresentationTests` with these assertions, using its existing `MethodBody` and `Minify` helpers:

```csharp
var mainSource = File.ReadAllText(FindRepositoryFile(
    "src", "PowerMode.App", "Views", "MainWindow.xaml.cs"));
var refreshMethod = Minify(MethodBody(
    mainSource,
    "private async Task RefreshStatusAsync"));
Assert.Contains("varrefreshRevision=_powerStateRevisionGate.Capture()", refreshMethod);
Assert.Contains(
    "if(!_powerStateRevisionGate.CanApply(refreshRevision,_modeSwitchInProgress))return",
    refreshMethod);

var mutationSource = File.ReadAllText(FindRepositoryFile(
    "src", "PowerMode.App", "Views", "MainWindow.AdvancedFeatures.cs"));
var mutationMethod = Minify(MethodBody(
    mutationSource,
    "private async Task<bool> RunTargetAfterStartupAsync"));
var beginMutation = mutationMethod.IndexOf(
    "_powerStateRevisionGate.BeginMutation()",
    StringComparison.Ordinal);
var setMutationFlag = mutationMethod.IndexOf(
    "_modeSwitchInProgress=true",
    StringComparison.Ordinal);
Assert.True(beginMutation >= 0 && beginMutation < setMutationFlag);
```

- [ ] **Step 2: Run the focused Refresh and revision tests**

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore --filter "FullyQualifiedName~RefreshInteractionPresentationTests|FullyQualifiedName~PowerStateRevisionGateTests"
```

Expected: the new boundary assertions fail because the window is not wired to the gate.

- [ ] **Step 3: Wire the gate into MainWindow**

Add a field:

```csharp
private readonly PowerStateRevisionGate _powerStateRevisionGate = new();
```

At the start of `RefreshStatusAsync`, after the existing duplicate gate succeeds, capture the revision:

```csharp
var refreshRevision = _powerStateRevisionGate.Capture();
```

Immediately after `ReadStateAsync` returns and before `AppendBackendDiagnostics`/state presentation, reject stale results:

```csharp
if (!_powerStateRevisionGate.CanApply(refreshRevision, _modeSwitchInProgress))
    return;
```

At the central mode mutation boundary, immediately before `_modeSwitchInProgress = true`, call:

```csharp
_powerStateRevisionGate.BeginMutation();
_modeSwitchInProgress = true;
```

End that mutation from the existing `finally` block. Wrap `RestoreBeforeStateAsync` and `AppWindow_Closing` exit snapshot restoration with the same `BeginMutation`/`EndMutation` lifecycle so refreshes cannot start while a recovery write is awaiting completion.

Capture a verification revision before `VerifyLastOperationAsync` and only apply its returned state when `CanApply` still accepts the revision after startup recovery resumes.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Expected: all focused tests pass and no existing Refresh contract regresses.

- [ ] **Step 5: Commit the integration**

```powershell
git add -- src/PowerMode.App/Views/MainWindow.xaml.cs src/PowerMode.App/Views/MainWindow.AdvancedFeatures.cs tests/PowerMode.App.Tests/RefreshInteractionPresentationTests.cs
git commit -m "fix: discard stale refresh state"
```

### Task 3: Full verification and release audit

**Files:**
- Modify: `.superpowers/sdd/progress.md` (ignored local ledger)
- Create: `.superpowers/sdd/task-15-report.md` (ignored local report)

- [ ] **Step 1: Run the full Release test suite**

```powershell
dotnet test .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore --logger "console;verbosity=minimal"
```

Expected: all tests pass, 0 failed.

- [ ] **Step 2: Run the Release build**

```powershell
dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore
```

Expected: 0 warnings and 0 errors.

- [ ] **Step 3: Run the formal portable publish**

```powershell
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1 -Root . -CreateZip
```

Expected: the internal full test gate passes, the portable directory and ZIP are produced, and no staging/backup residue remains.

- [ ] **Step 4: Audit the release artifacts**

Verify `build-info.json` points to the current HEAD, the ZIP hash equals `PowerMode-win-x64.zip.sha256`, and the portable root contains no PDB, test, `obj`, `bin`, or intermediate artifacts.

- [ ] **Step 5: Record the report and final state**

Record the focused/full test counts, build result, publish hash result, commit IDs, and the existing environment restriction that prevents safe real GUI input testing while auto-switch is enabled.

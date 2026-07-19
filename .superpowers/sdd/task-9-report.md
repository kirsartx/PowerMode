# Task 9 Report: Fluent, Accessibility, and Responsive Regression Pass

## Status

Completed the Fluent / accessibility / responsive regression pass on base
`d023ed6` without rewriting window navigation, services, or mode switching.
Checkpoint: `fluent-accessibility`.

No production packages, RGB/hex theme colors, continuous animation, new
features, or second design system were introduced. Simple/Professional
behavior, four mode buttons, recommendation card, capability presentation,
recovery safety, CLI path, portable layout, and accelerators `1–4` / `F5`
remain intact.

## Implementation summary

### Policy / non-color disabled state

- Extended `CapabilityVisibilityPolicy.Evaluate` with optional
  `bool isChinese = true`.
- Every disabled presentation now still carries a non-empty reason.
- Disabled reasons are localized for Chinese and English; callers pass
  language from the existing UI (`IsChinese` / `_zh`).
- HelpText/ToolTip presentation paths continue to surface `presentation.Reason`
  (now language-correct).

### Theme resources

- `App.xaml` `CardValueStyle` now sets
  `Foreground="{ThemeResource TextFillColorPrimaryBrush}"` so status card
  values track theme/high-contrast text brushes.
- Main recommendation/recovery/current-mode paths already use:
  - `AccentFillColorDefaultBrush`
  - `AccentTextFillColorPrimaryBrush`
  - `TextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush`
  - `SystemFillColorCriticalBrush`
  - `TextOnAccentFillColorPrimaryBrush` for selected mode buttons
- Structural tests forbid hex colors in `MainWindow.xaml`,
  `RecoveryCenterWindow.xaml`, and `App.xaml`.

### Accessibility metadata

- Experience toggle keeps localized `AutomationProperties.Name`.
- Capability-disabled controls keep `AutomationProperties.HelpText` from policy
  reasons (now bilingual).
- Recommendation reason retains `LiveSetting="Polite"` and `TextWrapping="Wrap"`.
- Current mode buttons retain localized `AutomationProperties.ItemStatus`.
- Accelerators remain `1–4` and `F5`.
- Recovery center:
  - localized `AutomationProperties.Name` on Undo / Restore / Reset and result text;
  - `ResultMessageText` now has `AutomationProperties.LiveSetting="Polite"`.

### Responsive layout (static reasoning; GUI not launched)

Preserved existing compact/header and experience-mode layout logic; no
duplicate controls added.

| Concern | Static evidence |
| --- | --- |
| Simple mode no unused log column | `ProfessionalLogPanel` collapsed; column 1 width `0`; column 0 star |
| Professional two-column layout | column 0 fixed `390`, column 1 star, spacing `16` |
| Narrow header icon-only + ToolTips | `RootGrid_SizeChanged` collapses action label texts below width `1040`; ToolTips remain set for refresh/features/insights/recovery/etc. |
| Recommendation reason wraps | `RecommendationReason TextWrapping="Wrap"` |
| Recovery action names not truncated | buttons `MinWidth="118"`, no `MaxWidth`, content text not ellipsized |
| High contrast selection/disabled | ThemeResource accent/text brushes for selection; disabled via `IsEnabled` + opacity + non-empty HelpText/ToolTip reason |

### DPI reasoning (100% / 125% / 150%)

WinUI layout is effective-pixel based with `UseLayoutRounding="True"` on main
and recovery roots. At 125% and 150% DPI the compact threshold remains
logical width `< 1040`, so toolbar compaction and simple/professional column
behavior scale consistently. Status cards already use wrapping secondary text
and ellipsis-safe values; recovery cards stack vertically in a
`ScrollViewer`, so taller effective chrome at 150% scrolls rather than
clipping action names.

## TDD evidence

### RED

Commands:

```powershell
dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --filter "FullyQualifiedName~CapabilityVisibilityPolicyTests|FullyQualifiedName~FluentAccessibilityPresentationTests" --no-restore
```

Observed RED signals before production fixes:

1. **Compile RED (policy language API)**  
   `CS1501` / `CS1739`: `Evaluate` had no 3-parameter / `isChinese` overload.
2. **Assertion RED (recovery a11y after API landed)**  
   `RecoveryCenter_ExposesAccessibleNamesLiveRegionAndThemeBrushes` failed
   because `AutomationProperties.SetName` for recovery actions was missing
   and `ResultMessageText` lacked `LiveSetting="Polite"`.
3. Matrix test for non-empty disabled reasons locked the invariant (Chinese
   paths already non-empty; English localization required the new parameter).

### GREEN

Same focused filter after minimal production/XAML changes:

- Passed: **15**, failed: **0**.

## Files changed

| Path | Change |
| --- | --- |
| `src/PowerMode.App/App.xaml` | Card value primary ThemeResource foreground |
| `src/PowerMode.App/Services/CapabilityVisibilityPolicy.cs` | Bilingual non-empty disabled reasons |
| `src/PowerMode.App/Views/MainWindow.Features.cs` | Pass `IsChinese` into policy |
| `src/PowerMode.App/Views/SettingsWindow.xaml.cs` | Pass `_zh` into policy |
| `src/PowerMode.App/Views/RecoveryCenterWindow.xaml` | Recovery result polite live region |
| `src/PowerMode.App/Views/RecoveryCenterWindow.xaml.cs` | Localized automation names for recovery actions/result |
| `tests/PowerMode.App.Tests/CapabilityVisibilityPolicyTests.cs` | Disabled-reason matrix + English reason tests |
| `tests/PowerMode.App.Tests/FluentAccessibilityPresentationTests.cs` | Structural a11y / theme / responsive source checks |

`MainWindow.xaml` / `MainWindow.xaml.cs` required no further production edits;
prior tasks already carried ThemeResource recommendation styling, accelerators,
live regions, compact header, and current-mode item status. Structural tests
now guard those contracts.

## Full verification

| Step | Command | Result |
| --- | --- | --- |
| Focused Debug | `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --filter "FullyQualifiedName~CapabilityVisibilityPolicyTests\|FullyQualifiedName~FluentAccessibilityPresentationTests" --no-restore` | 15 passed, 0 failed |
| Full Debug | `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Debug --no-restore` | **126** passed, 0 failed |
| Full Release | `dotnet test tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -c Release --no-restore` | **126** passed, 0 failed |
| Release x64 build | `dotnet build PowerMode.slnx -c Release -p:Platform=x64 --no-restore` | succeeded, **0 warnings, 0 errors** |
| Diff whitespace | `git diff --check` | clean (exit 0) |

## Self-review

- Confirmed no second design system or hard-coded RGB on the Task 9 surfaces.
- Confirmed disabled presentations always have non-empty localized reasons.
- Confirmed Simple mode still collapses the log column without duplicating
  controls.
- Confirmed recovery actions remain disabled until availability refresh and
  keep confirmation + busy gating from Task 8.
- Confirmed accelerators `1–4` and `F5` remain declared in XAML and handled in
  code-behind.

## Remaining visual risks

- Interactive Narrator / high-contrast / multi-DPI visual QA was intentionally
  not performed because the task forbids launching or controlling the
  foreground GUI. Coverage is structural XAML/XML, policy unit tests, and
  Release compilation.
- Disabled controls still use `Opacity = 0.55` in addition to `IsEnabled`;
  high-contrast may rely more on system disabled styling than opacity.
- `InsightsWindow` still uses a hard-coded temperature series color
  (`#FFF59E0B`); out of Task 9 file scope and not on recommendation/current
  mode/recovery/error paths.
- Very narrow recommendation card keeps a three-column apply button row; reason
  wraps, but extreme widths may still feel tight without runtime measurement.

## Checkpoint

`fluent-accessibility` — 0 failed tests, 0 Release build errors.

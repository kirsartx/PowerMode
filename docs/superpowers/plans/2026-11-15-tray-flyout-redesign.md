# Tray Flyout Main Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the wide dashboard main window with a 360-logical-pixel tray flyout that expands in place for Professional mode and can be pinned as a normal window.

**Architecture:** A new pure policy (`FlyoutWindowPolicy`) owns geometry (anchor, clamp, pinned placement), tray click filtering and auto-hide decisions so they are unit-testable. `MainWindow` keeps its existing control names and business logic; only layout XAML, window chrome and tray/activation wiring change. The dashboard part of `ResponsiveLayoutPolicy` is removed; its auxiliary-window projections stay.

**Tech Stack:** WinUI 3 (Windows App SDK 2.2), .NET 10, xUnit.

## Global Constraints

- Content width 360 logical px; edge margin 12 logical px; window height follows content and never exceeds the work area minus margins.
- Only system accent / theme brushes; no hard-coded colors in XAML (tests enforce `#RRGGBB` absence).
- Recommendations are never applied automatically.
- No new production dependencies, no MVVM/DI, no NavigationView.
- Keep `x:Name`s used by business logic: mode buttons + their Check/Badge/Progress parts, `RefreshButton` (F5 accelerator), `RecommendationCard/Title/Reason`, `ApplyRecommendationButton`, `StatusBar`, `StatusText`, `StatusActionButton`, `BusyProgress`, `ModeSwitchDiagnosticText`, `CopyModeSwitchDiagnosticButton`, `CpuSlider`, `CpuBox`, `RemoteCustomButton`, `VerifyButton`, `RepairButton`, `WifiOnButton`, `RemoteNoWifiButton`, `LogBox`, log toolbar buttons, `CliPathText`, `ModeValue`, `GpuValue`, `PowerValue`, `CpuValue`, `BrightnessValue`, `SleepValue`, `AutoQuickToggle`, `LiveQuickToggle`, `FeaturesButton`, `InsightsButton`, `RecoveryCenterButton`, `LanguageButton`, `MoreButton`.
- Pin state is window state, stored in `HKCU\Software\PowerModeSwitcher\Window\Pinned` (DWORD, default 1), not in `settings.json` — the Settings window saves a cloned `PowerModeSettings` and would overwrite a concurrent pin change.

---

### Task 1: FlyoutWindowPolicy

**Files:**
- Create: `src/PowerMode.App/Services/FlyoutWindowPolicy.cs`
- Test: `tests/PowerMode.App.Tests/FlyoutWindowPolicyTests.cs`

**Produces:**
- `readonly record struct PixelRect(int X, int Y, int Width, int Height)`
- `FlyoutWindowPolicy.ContentWidth = 360`, `EdgeMargin = 12`, `MinimumContentHeight = 200`
- `int ToPixels(double logical, double scale)`
- `int ClampHeight(double logicalHeight, double scale, PixelRect workArea)`
- `PixelRect AnchorBottomRight(PixelRect workArea, int width, int height, double scale)`
- `PixelRect KeepInside(PixelRect workArea, int x, int y, int width, int height)`
- `bool ShouldAutoHide(bool isPinned, bool foregroundBelongsToApp)`
- `TrayClickFilter` with `TrayClickAction OnLeftButtonUp(long ms)`, `OnDoubleClick(long ms)`, `NotifyAutoHidden(long ms)`; `enum TrayClickAction { None, Toggle, Show }`

- [ ] Write failing tests for each member (anchor math at scale 1 and 1.5, clamp to work area, keep-inside for off-screen positions, auto-hide matrix, tray filter: single click toggles, double click shows and swallows the trailing button-up, click within 400 ms of an auto-hide is ignored).
- [ ] Run `dotnet test --filter FlyoutWindowPolicyTests` → FAIL (type missing).
- [ ] Implement the policy.
- [ ] Run again → PASS. Commit `feat: add tray flyout window policy`.

### Task 2: Retire the dashboard layout policy

**Files:**
- Modify: `src/PowerMode.App/Services/ResponsiveLayoutPolicy.cs` (remove `LayoutTier`, `StatusCardId`, `ToolbarAction`, `ToolbarDisplay`, `ResponsiveLayoutState`, `CardPlacement`, `StandardModeButtonId`, `ModeButtonPlacement`, `ResponsiveDashboardProjection`, `Evaluate`, `ReflowStatusCards`, `Project`, and main-window size constants; keep `GridLengthProjection*`, `AuxiliaryLayoutProjection`, `RegionPlacement`, `MediumMinimumWidth`, `ShouldStackAuxiliaryContent`, `ProjectAuxiliaryContent`, `ProjectSettingsAuxiliaryContent`, `ProjectInsightsAuxiliaryContent`).
- Modify: `tests/PowerMode.App.Tests/ResponsiveLayoutPolicyTests.cs` (keep only auxiliary tests + `MediumMinimumWidth` constant).

- [ ] Delete the dashboard tests, keep `ShouldStackAuxiliaryContent_UsesMediumBreakpoint`.
- [ ] Remove the dashboard types (done together with Task 4 so the app keeps compiling).

### Task 3: Shared styles

**Files:** Modify `src/PowerMode.App/App.xaml`.

- `PanelStyle`: CornerRadius 8, Padding 16 (applies to all secondary windows).
- New: `FlyoutCardStyle` (Border, card brushes, CornerRadius 8, Padding 12), `SectionLabelStyle` (Caption 12 SemiBold, secondary text), `MetricLabelStyle` (11, tertiary), `MetricValueStyle` (14 SemiBold, display font), `ModeTileStyle` (Button, stretch, MinHeight 60, left/top content alignment, Padding 12,10, CornerRadius 8), `PillToggleStyle` (ToggleButton, CornerRadius 16, Padding 12,5), `HeaderIconButtonStyle` (Button, 32×32, transparent background via `SubtleFillColorTransparentBrush`, no border).

### Task 4: Main window XAML + code-behind

**Files:**
- Modify: `src/PowerMode.App/Views/MainWindow.xaml` (rewrite as single column: `HeaderGrid` / `ContentScrollViewer` > `FlyoutContent` / `FooterGrid`).
- Modify: `src/PowerMode.App/Views/MainWindow.xaml.cs` (remove responsive layout methods; add `UpdateFlyoutSize`; header `ModeValue`; hide recommendation when it equals the current mode).
- Modify: `src/PowerMode.App/Views/MainWindow.Features.cs` (`ApplyExperienceMode` drives `ProfessionalModeControls` + `ProfessionalLogPanel` visibility and the footer `ExpandButton`; tray single click via `TrayClickFilter`; Esc / deactivate hide; remove overflow toggles and `ExperienceModeButton`).
- Create: `src/PowerMode.App/Views/MainWindow.Flyout.cs` (presenter configuration for pinned / unpinned, `PinButton_Click`, `ShowFlyout`, `HideFlyout`, `UpdateFlyoutSize`, registry pin persistence, foreground-process check).
- Modify: `src/PowerMode.App/Services/MainWindowStrings.cs` (keys `Pin`, `Unpin`, `Professional`, `Collapse`, `Exit`, `RecommendHint`).

Behavior details:
- Unpinned: `OverlappedPresenter.SetBorderAndTitleBar(true,false)`, not resizable/min/max, `IsAlwaysOnTop=true`, `AppWindow.IsShownInSwitchers=false`; shown anchored bottom-right of the primary work area (or the area under the cursor when opened from the tray).
- Pinned: `SetBorderAndTitleBar(true,true)`, `ExtendsContentIntoTitleBar=true`, `SetTitleBar(HeaderDragRegion)`, minimizable, not resizable/maximizable, shown in switchers; header right padding = `AppWindow.TitleBar.RightInset / scale`; position restored from registry and kept inside the work area.
- Size: `AppWindow.ResizeClient(width = ToPixels(360), height = ClampHeight(header+content+footer+padding))` on `SizeChanged` of header, content and footer.
- Deactivated + unpinned → after 120 ms, if `ShouldAutoHide(false, ForegroundBelongsToApp())` → hide and `NotifyAutoHidden`.

### Task 5: Update presentation tests

**Files:** `FluentAccessibilityPresentationTests.cs`, `RecoveryCenterPresentationTests.cs` (only the XAML-structure assertions that referenced removed elements).

- Replace `MainWindow_DeclaresNamedAdaptiveCardsAndNarrowProfessionalOverflow` with a flyout structure test: `HeaderGrid`, `ContentScrollViewer`, `FlyoutContent`, `MetricsCard`, `FooterGrid`, `PinButton`, `ExpandButton`, `MoreButton` with `FeaturesButton`/`InsightsButton`/`RecoveryCenterButton`/`LanguageButton`/`ExitMenuItem` menu items, professional sections inside `FlyoutContent`.
- Replace `MainWindow_DeclaresBoundedAdaptiveViewportAndModeGrid` with: `ModeButtonsGrid` and `ProfessionalModeControls` and `ProfessionalLogPanel` are descendants of `ContentScrollViewer`.
- Replace the `ExperienceModeButton` accessible-name assertion with `ExpandButton` and `PinButton` accessible names.

### Task 6: Verify

- [ ] `dotnet build -c Release` with zero warnings introduced.
- [ ] `dotnet test` all green.
- [ ] Launch the Release build; UI Automation smoke: window visible, width ≈ 360 logical px, click `ExpandButton` grows the window, Feature Center opens without crash and the flyout stays open, Esc hides when unpinned.
- [ ] Commit `feat: redesign main window as tray flyout`.

# PowerMode Mature Native Experience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a default simple experience, transparent local mode recommendation, hardware-aware presentation, a recovery center, and layered documentation without replacing the existing WinUI/.NET 10 architecture.

**Architecture:** Keep the existing `MainWindow`, mode-switch pipeline, CLI, settings store, history store, monitoring, and system-integration services. Add small pure models/services for experience state, capabilities, recommendation, and recovery; let existing code-behind orchestrate them and keep all system mutations on the current execution paths.

**Tech Stack:** .NET 10, WinUI 3, Windows App SDK 2.2, x64 self-contained portable publish, System.Text.Json, xUnit test project.

## Global Constraints

- Preserve the existing WinUI 3, .NET 10, CLI, portable deployment, and code-behind/service architecture.
- Do not add production runtime packages or a new framework.
- Do not change the four existing mode definitions or force a recommendation.
- Existing settings, history JSONL, backups, diagnostics, and portable folder layout must remain compatible.
- Every new mutation must be recoverable, recorded, or protected by a safety backup.
- Use system theme resources and accent color; do not add hard-coded UI colors or continuous animation.
- The current directory is not a Git repository. Do not initialize Git without user authorization; replace commit steps with named verification checkpoints.

---

## File Structure

**Create**

- `tests/PowerMode.App.Tests/PowerMode.App.Tests.csproj` — .NET 10 xUnit test project.
- `tests/PowerMode.App.Tests/SettingsCompatibilityTests.cs` — old/new settings compatibility.
- `tests/PowerMode.App.Tests/CapabilityVisibilityPolicyTests.cs` — simple/pro capability matrix.
- `tests/PowerMode.App.Tests/ModeRecommendationServiceTests.cs` — deterministic recommendation rules.
- `tests/PowerMode.App.Tests/RecoveryServiceTests.cs` — undo selection and safe reset behavior.
- `src/PowerMode.App/Models/ExperienceMode.cs` — experience enum.
- `src/PowerMode.App/Models/HardwareCapabilities.cs` — immutable capability snapshot and support state.
- `src/PowerMode.App/Models/ModeRecommendation.cs` — recommendation input/output.
- `src/PowerMode.App/Services/CapabilityVisibilityPolicy.cs` — pure visibility decisions.
- `src/PowerMode.App/Services/HardwareCapabilityService.cs` — lightweight bounded detection.
- `src/PowerMode.App/Services/ModeRecommendationService.cs` — pure deterministic rules.
- `src/PowerMode.App/Services/RecoveryService.cs` — recovery orchestration and undo eligibility.
- `src/PowerMode.App/Views/RecoveryCenterWindow.xaml` — recovery UI.
- `src/PowerMode.App/Views/RecoveryCenterWindow.xaml.cs` — recovery UI orchestration.
- `docs/USER_GUIDE.md` — complete user guide.
- `docs/DEVELOPMENT.md` — architecture/build/maintenance guide.

**Modify**

- `PowerMode.slnx` — include the test project.
- `src/PowerMode.App/PowerMode.App.csproj` — expose internals to tests.
- `src/PowerMode.App/Models/PowerModeSettings.cs` — experience setting and compatible normalization.
- `src/PowerMode.App/Services/AutomationEngine.cs` — optional operation metadata on history entries.
- `src/PowerMode.App/Views/MainWindow.xaml` — simple/pro layout, recommendation and recovery entry.
- `src/PowerMode.App/Views/MainWindow.xaml.cs` — experience state, localized copy, responsive layout.
- `src/PowerMode.App/Views/MainWindow.Features.cs` — capability detection and feature presentation.
- `src/PowerMode.App/Views/MainWindow.AdvancedFeatures.cs` — recommendation/recovery integration and history metadata.
- `src/PowerMode.App/Views/SettingsWindow.xaml.cs` — capability-based disabled states and reasons.
- `src/PowerMode.App/Views/InsightsWindow.xaml.cs` — format old and new operation kinds.
- `README.md` — short landing page.
- `scripts/Publish-Portable.ps1` — no layout changes; only verify documentation copy expectations.

---

### Task 1: Test Harness and Experience-Mode Configuration Compatibility

**Files:**

- Create: `tests/PowerMode.App.Tests/PowerMode.App.Tests.csproj`
- Create: `tests/PowerMode.App.Tests/SettingsCompatibilityTests.cs`
- Create: `src/PowerMode.App/Models/ExperienceMode.cs`
- Modify: `PowerMode.slnx`
- Modify: `src/PowerMode.App/PowerMode.App.csproj`
- Modify: `src/PowerMode.App/Models/PowerModeSettings.cs`

**Interfaces:**

- Produces: `enum ExperienceMode { Simple, Professional }`
- Produces: `PowerModeSettings.ExperienceMode`
- Produces: `internal static PowerModeSettings SettingsStore.Normalize(PowerModeSettings? settings)`

- [ ] **Step 1: Add the test project and failing compatibility tests**

```xml
<!-- tests/PowerMode.App.Tests/PowerMode.App.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PowerMode.App\PowerMode.App.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// tests/PowerMode.App.Tests/SettingsCompatibilityTests.cs
using System.Text.Json;
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class SettingsCompatibilityTests
{
    [Fact]
    public void Normalize_OldSettingsWithoutExperienceMode_DefaultsToSimple()
    {
        var settings = JsonSerializer.Deserialize<PowerModeSettings>("""{"LastMode":"remote"}""");

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Simple, normalized.ExperienceMode);
        Assert.Equal("remote", normalized.LastMode);
    }

    [Fact]
    public void Normalize_UnknownExperienceMode_DefaultsToSimple()
    {
        var settings = new PowerModeSettings { ExperienceMode = (ExperienceMode)99 };

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Simple, normalized.ExperienceMode);
    }

    [Fact]
    public void Normalize_ProfessionalMode_IsPreserved()
    {
        var settings = new PowerModeSettings { ExperienceMode = ExperienceMode.Professional };

        var normalized = SettingsStore.Normalize(settings);

        Assert.Equal(ExperienceMode.Professional, normalized.ExperienceMode);
    }
}
```

Add to `PowerMode.slnx`:

```xml
<Project Path="tests/PowerMode.App.Tests/PowerMode.App.Tests.csproj" />
```

Add to `PowerMode.App.csproj`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="PowerMode.App.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64
```

Expected: compile failure because `ExperienceMode` and `PowerModeSettings.ExperienceMode` do not exist and `SettingsStore.Normalize` is private.

- [ ] **Step 3: Implement the minimal enum and compatible normalization**

```csharp
// src/PowerMode.App/Models/ExperienceMode.cs
namespace PowerModeWinUI;

public enum ExperienceMode
{
    Simple = 0,
    Professional = 1
}
```

Add to `PowerModeSettings`:

```csharp
public ExperienceMode ExperienceMode { get; set; } = ExperienceMode.Simple;
```

Change and extend `SettingsStore.Normalize`:

```csharp
internal static PowerModeSettings Normalize(PowerModeSettings? settings)
{
    settings ??= new();
    if (!Enum.IsDefined(settings.ExperienceMode))
        settings.ExperienceMode = ExperienceMode.Simple;
    // Preserve all existing normalization statements below this guard.
    return settings;
}
```

- [ ] **Step 4: Run the tests and verify GREEN**

Run:

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64
```

Expected: 3 passed, 0 failed.

- [ ] **Step 5: Verification checkpoint**

Run:

```powershell
dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false
```

Expected: build succeeds with 0 errors. Record checkpoint name: `settings-compatibility`.

---

### Task 2: Capability Models and Visibility Policy

**Files:**

- Create: `src/PowerMode.App/Models/HardwareCapabilities.cs`
- Create: `src/PowerMode.App/Services/CapabilityVisibilityPolicy.cs`
- Create: `tests/PowerMode.App.Tests/CapabilityVisibilityPolicyTests.cs`

**Interfaces:**

- Produces: `CapabilitySupport`
- Produces: `HardwareCapabilities`
- Produces: `CapabilityFeature`
- Produces: `FeaturePresentation`
- Produces: `CapabilityVisibilityPolicy.Evaluate(ExperienceMode, HardwareCapabilities)`

- [ ] **Step 1: Write failing policy tests**

```csharp
// tests/PowerMode.App.Tests/CapabilityVisibilityPolicyTests.cs
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class CapabilityVisibilityPolicyTests
{
    [Fact]
    public void Evaluate_SimpleMode_HidesUnsupportedProfessionalFeature()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            WifiControl = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(ExperienceMode.Simple, capabilities);

        Assert.False(result[CapabilityFeature.WifiControl].IsVisible);
    }

    [Fact]
    public void Evaluate_ProfessionalMode_DisablesUnsupportedFeatureWithReason()
    {
        var capabilities = HardwareCapabilities.Unknown with
        {
            TemperatureMonitoring = CapabilitySupport.Unsupported
        };

        var result = CapabilityVisibilityPolicy.Evaluate(ExperienceMode.Professional, capabilities);

        var temperature = result[CapabilityFeature.TemperatureProtection];
        Assert.True(temperature.IsVisible);
        Assert.False(temperature.IsEnabled);
        Assert.NotEmpty(temperature.Reason);
    }

    [Fact]
    public void Evaluate_CoreModes_AreAlwaysVisibleAndEnabled()
    {
        var result = CapabilityVisibilityPolicy.Evaluate(
            ExperienceMode.Simple,
            HardwareCapabilities.Unknown);

        Assert.True(result[CapabilityFeature.CoreModes].IsVisible);
        Assert.True(result[CapabilityFeature.CoreModes].IsEnabled);
    }
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64
```

Expected: compile failure because the capability types do not exist.

- [ ] **Step 3: Add minimal capability models**

```csharp
// src/PowerMode.App/Models/HardwareCapabilities.cs
namespace PowerModeWinUI;

public enum CapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record HardwareCapabilities(
    CapabilitySupport Battery,
    CapabilitySupport BrightnessControl,
    CapabilitySupport NvidiaGpu,
    CapabilitySupport NvidiaSmi,
    CapabilitySupport WifiControl,
    CapabilitySupport TemperatureMonitoring,
    CapabilitySupport Notifications,
    CapabilitySupport GlobalHotkeys)
{
    public static HardwareCapabilities Unknown { get; } = new(
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown,
        CapabilitySupport.Unknown);
}
```

```csharp
// src/PowerMode.App/Services/CapabilityVisibilityPolicy.cs
namespace PowerModeWinUI;

public enum CapabilityFeature
{
    CoreModes,
    BatterySettings,
    Brightness,
    GpuTelemetry,
    WifiControl,
    TemperatureProtection,
    Notifications,
    GlobalHotkeys
}

public sealed record FeaturePresentation(bool IsVisible, bool IsEnabled, string Reason);

public static class CapabilityVisibilityPolicy
{
    public static IReadOnlyDictionary<CapabilityFeature, FeaturePresentation> Evaluate(
        ExperienceMode mode,
        HardwareCapabilities capabilities)
    {
        var professional = mode == ExperienceMode.Professional;
        return new Dictionary<CapabilityFeature, FeaturePresentation>
        {
            [CapabilityFeature.CoreModes] = new(true, true, string.Empty),
            [CapabilityFeature.BatterySettings] = Present(professional, capabilities.Battery, "未检测到电池"),
            [CapabilityFeature.Brightness] = Present(true, capabilities.BrightnessControl, "设备不支持内部屏幕亮度控制"),
            [CapabilityFeature.GpuTelemetry] = Present(professional, capabilities.NvidiaSmi, "未检测到可用的 NVIDIA 遥测"),
            [CapabilityFeature.WifiControl] = Present(professional, capabilities.WifiControl, "未检测到可控制的 WiFi 适配器"),
            [CapabilityFeature.TemperatureProtection] = Present(professional, capabilities.TemperatureMonitoring, "未检测到受支持的温度传感器"),
            [CapabilityFeature.Notifications] = Present(professional, capabilities.Notifications, "系统通知不可用"),
            [CapabilityFeature.GlobalHotkeys] = Present(professional, capabilities.GlobalHotkeys, "全局快捷键注册不可用")
        };
    }

    private static FeaturePresentation Present(
        bool visibleWhenSupported,
        CapabilitySupport support,
        string unsupportedReason)
    {
        if (support == CapabilitySupport.Supported)
            return new(visibleWhenSupported, true, string.Empty);
        return new(visibleWhenSupported, false,
            support == CapabilitySupport.Unknown ? "尚未完成能力检测" : unsupportedReason);
    }
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Run the test project. Expected: all tests pass.

- [ ] **Step 5: Verification checkpoint**

Run Release build. Expected: 0 errors. Record checkpoint: `capability-policy`.

---

### Task 3: Deterministic Recommendation Service

**Files:**

- Create: `src/PowerMode.App/Models/ModeRecommendation.cs`
- Create: `src/PowerMode.App/Services/ModeRecommendationService.cs`
- Create: `tests/PowerMode.App.Tests/ModeRecommendationServiceTests.cs`

**Interfaces:**

- Consumes: `HardwareCapabilities`
- Produces: `RecommendationContext`
- Produces: `ModeRecommendation`
- Produces: `ModeRecommendationService.Recommend(RecommendationContext)`

- [ ] **Step 1: Write failing precedence tests**

```csharp
// tests/PowerMode.App.Tests/ModeRecommendationServiceTests.cs
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class ModeRecommendationServiceTests
{
    private static RecommendationContext Context() => new(
        OnBattery: false,
        BatteryPercent: 100,
        TemperatureProtectionActive: false,
        RemoteProcessNames: [],
        PerformanceProcessNames: [],
        RunningProcessNames: [],
        LowBatteryThreshold: 30,
        Capabilities: HardwareCapabilities.Unknown);

    [Fact]
    public void Recommend_TemperatureProtection_WinsOverPerformanceProcess()
    {
        var context = Context() with
        {
            TemperatureProtectionActive = true,
            PerformanceProcessNames = ["game"],
            RunningProcessNames = ["game"]
        };

        var result = ModeRecommendationService.Recommend(context);

        Assert.Equal("saver", result.Mode);
        Assert.Contains("温度", result.Reason);
    }

    [Fact]
    public void Recommend_RemoteProcess_WinsOverBattery()
    {
        var context = Context() with
        {
            OnBattery = true,
            RemoteProcessNames = ["Hermes"],
            RunningProcessNames = ["Hermes"]
        };

        Assert.Equal("remote", ModeRecommendationService.Recommend(context).Mode);
    }

    [Fact]
    public void Recommend_PerformanceProcess_ReturnsHigh()
    {
        var context = Context() with
        {
            PerformanceProcessNames = ["game"],
            RunningProcessNames = ["GAME"]
        };

        Assert.Equal("high", ModeRecommendationService.Recommend(context).Mode);
    }

    [Fact]
    public void Recommend_Battery_ReturnsSaver()
    {
        Assert.Equal("saver",
            ModeRecommendationService.Recommend(Context() with { OnBattery = true }).Mode);
    }

    [Fact]
    public void Recommend_DefaultAc_ReturnsBalanced()
    {
        Assert.Equal("balanced", ModeRecommendationService.Recommend(Context()).Mode);
    }
}
```

- [ ] **Step 2: Run tests and verify RED**

Expected: compile failure because recommendation types do not exist.

- [ ] **Step 3: Implement the minimal recommendation model and rules**

```csharp
// src/PowerMode.App/Models/ModeRecommendation.cs
namespace PowerModeWinUI;

public sealed record RecommendationContext(
    bool OnBattery,
    int? BatteryPercent,
    bool TemperatureProtectionActive,
    IReadOnlyCollection<string> RemoteProcessNames,
    IReadOnlyCollection<string> PerformanceProcessNames,
    IReadOnlyCollection<string> RunningProcessNames,
    int LowBatteryThreshold,
    HardwareCapabilities Capabilities);

public sealed record ModeRecommendation(
    string Mode,
    string Reason,
    bool IsComplete,
    DateTimeOffset GeneratedAt);
```

```csharp
// src/PowerMode.App/Services/ModeRecommendationService.cs
namespace PowerModeWinUI;

public static class ModeRecommendationService
{
    public static ModeRecommendation Recommend(RecommendationContext context)
    {
        var running = context.RunningProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (context.TemperatureProtectionActive)
            return Result("saver", "温度保护已触发，建议降低功耗", context);
        if (context.RemoteProcessNames.Any(running.Contains))
            return Result("remote", "检测到远程连接程序，建议使用远程推荐", context);
        if (context.PerformanceProcessNames.Any(running.Contains))
            return Result("high", "检测到高负载程序，建议使用高性能", context);
        if (context.OnBattery ||
            context.BatteryPercent is int percent && percent <= context.LowBatteryThreshold)
            return Result("saver", "当前使用电池供电，建议降低功耗", context);
        return Result("balanced", "当前为日常插电场景，建议使用平衡", context);
    }

    private static ModeRecommendation Result(
        string mode,
        string reason,
        RecommendationContext context) =>
        new(mode, reason,
            context.Capabilities.Battery != CapabilitySupport.Unknown,
            DateTimeOffset.Now);
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Expected: all recommendation and prior tests pass.

- [ ] **Step 5: Verification checkpoint**

Run Release build. Expected: 0 errors. Record checkpoint: `recommendation-rules`.

---

### Task 4: Simple/Professional Main-Window Presentation

**Files:**

- Modify: `src/PowerMode.App/Views/MainWindow.xaml`
- Modify: `src/PowerMode.App/Views/MainWindow.xaml.cs`
- Modify: `src/PowerMode.App/Views/MainWindow.Features.cs`
- Modify: `src/PowerMode.App/App.xaml`

**Interfaces:**

- Consumes: `PowerModeSettings.ExperienceMode`
- Produces: `ApplyExperienceMode(ExperienceMode mode)`
- Produces: `ExperienceModeButton_Click`

- [ ] **Step 1: Add a failing pure presentation assertion**

Extend `CapabilityVisibilityPolicyTests`:

```csharp
[Fact]
public void Evaluate_SimpleMode_HidesProfessionalSurface()
{
    var result = CapabilityVisibilityPolicy.Evaluate(
        ExperienceMode.Simple,
        HardwareCapabilities.Unknown);

    Assert.False(result[CapabilityFeature.ProfessionalSurface].IsVisible);
}
```

Add `ProfessionalSurface` to `CapabilityFeature` only after observing the compile failure.

- [ ] **Step 2: Run tests and verify RED**

Expected: compile failure for missing `ProfessionalSurface`.

- [ ] **Step 3: Add the minimal policy behavior**

Add:

```csharp
ProfessionalSurface
```

and dictionary entry:

```csharp
[CapabilityFeature.ProfessionalSurface] =
    new(mode == ExperienceMode.Professional, true, string.Empty),
```

Run tests and confirm GREEN before editing XAML.

- [ ] **Step 4: Group existing XAML without duplicating controls**

Add a header toggle:

```xml
<ToggleButton x:Name="ExperienceModeButton"
              Click="ExperienceModeButton_Click"
              AutomationProperties.Name="切换简单或专业模式">
    <StackPanel Orientation="Horizontal" Spacing="6">
        <FontIcon Glyph="&#xE713;"/>
        <TextBlock x:Name="ExperienceModeText"/>
    </StackPanel>
</ToggleButton>
```

Name existing areas instead of recreating them. Add
`x:Name="ProfessionalQuickActions"` to the opening tag of the existing quick-actions
`Grid`; do not replace its current row, column, spacing, or child-control attributes.

```xml
<StackPanel x:Name="ProfessionalModeControls" Spacing="10">
    <!-- move the existing custom CPU, advanced controls, and CLI path here unchanged -->
</StackPanel>
```

```xml
<Border x:Name="ProfessionalLogPanel" Grid.Column="1" Style="{StaticResource PanelStyle}">
    <!-- existing log UI unchanged -->
</Border>
```

Keep the four existing mode buttons in a shared core section visible in both experiences.

- [ ] **Step 5: Add deterministic experience application**

```csharp
private void ApplyExperienceMode(ExperienceMode mode)
{
    _featureSettings.ExperienceMode = mode;
    var professional = mode == ExperienceMode.Professional;
    ProfessionalQuickActions.Visibility = professional ? Visibility.Visible : Visibility.Collapsed;
    ProfessionalModeControls.Visibility = professional ? Visibility.Visible : Visibility.Collapsed;
    ProfessionalLogPanel.Visibility = professional ? Visibility.Visible : Visibility.Collapsed;
    MainContentGrid.ColumnDefinitions[0].Width =
        professional ? new GridLength(390) : new GridLength(1, GridUnitType.Star);
    MainContentGrid.ColumnDefinitions[1].Width =
        professional ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    ExperienceModeText.Text = professional
        ? (IsChinese ? "专业" : "Professional")
        : (IsChinese ? "简单" : "Simple");
    ExperienceModeButton.IsChecked = professional;
}

private void ExperienceModeButton_Click(object sender, RoutedEventArgs e)
{
    var mode = ExperienceModeButton.IsChecked == true
        ? ExperienceMode.Professional
        : ExperienceMode.Simple;
    ApplyExperienceMode(mode);
    SettingsStore.Save(_featureSettings);
}
```

Call `ApplyExperienceMode(_featureSettings.ExperienceMode)` after language and feature initialization.

- [ ] **Step 6: Replace hard-coded selected-mode blue**

In `UpdateActiveMode`, replace `Colors.DodgerBlue` with the current theme accent brush:

```csharp
button.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
button.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
```

Retain text labels and accessibility item status so color is not the only indicator.

- [ ] **Step 7: Verify UI states**

Run:

```powershell
dotnet test .\tests\PowerMode.App.Tests\PowerMode.App.Tests.csproj -p:Platform=x64
dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false
```

Expected: tests pass, build has 0 errors. Manually verify simple mode has no blank log column and professional mode restores every existing control. Record checkpoint: `experience-layout`.

---

### Task 5: Lightweight Hardware Detection and Capability Presentation

**Files:**

- Create: `src/PowerMode.App/Services/HardwareCapabilityService.cs`
- Create: `tests/PowerMode.App.Tests/HardwareCapabilityServiceTests.cs`
- Modify: `src/PowerMode.App/Views/MainWindow.Features.cs`
- Modify: `src/PowerMode.App/Views/SettingsWindow.xaml.cs`

**Interfaces:**

- Produces: `IHardwareCapabilityProbe`
- Produces: `HardwareCapabilityService.DetectAsync(TimeSpan, CancellationToken)`
- Consumes: `CapabilityVisibilityPolicy`

- [ ] **Step 1: Write failing partial-failure and timeout tests**

```csharp
// tests/PowerMode.App.Tests/HardwareCapabilityServiceTests.cs
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class HardwareCapabilityServiceTests
{
    [Fact]
    public async Task DetectAsync_OneProbeThrows_ReturnsOtherResults()
    {
        var service = new HardwareCapabilityService(new FakeProbe
        {
            Battery = CapabilitySupport.Supported,
            ThrowForWifi = true
        });

        var result = await service.DetectAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(CapabilitySupport.Supported, result.Battery);
        Assert.Equal(CapabilitySupport.Unknown, result.WifiControl);
    }

    [Fact]
    public async Task DetectAsync_SlowProbe_TimesOutAsUnknown()
    {
        var service = new HardwareCapabilityService(new FakeProbe { Delay = TimeSpan.FromSeconds(2) });

        var result = await service.DetectAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(CapabilitySupport.Unknown, result.TemperatureMonitoring);
    }
}
```

The test file must include a real fake implementing `IHardwareCapabilityProbe` with configurable return values, exceptions, and delay; it must not mock the service under test.

- [ ] **Step 2: Run tests and verify RED**

Expected: compile failure because detector interfaces do not exist.

- [ ] **Step 3: Implement bounded independent probes**

Define:

```csharp
public interface IHardwareCapabilityProbe
{
    Task<CapabilitySupport> HasBatteryAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsBrightnessAsync(CancellationToken token);
    Task<CapabilitySupport> HasNvidiaGpuAsync(CancellationToken token);
    Task<CapabilitySupport> HasNvidiaSmiAsync(CancellationToken token);
    Task<CapabilitySupport> HasWifiControlAsync(CancellationToken token);
    Task<CapabilitySupport> HasTemperatureAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsNotificationsAsync(CancellationToken token);
    Task<CapabilitySupport> SupportsGlobalHotkeysAsync(CancellationToken token);
}
```

`HardwareCapabilityService` must run all probes concurrently and wrap each with:

```csharp
private static async Task<CapabilitySupport> SafeProbeAsync(
    Func<CancellationToken, Task<CapabilitySupport>> probe,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    linked.CancelAfter(timeout);
    try { return await probe(linked.Token).ConfigureAwait(false); }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    { return CapabilitySupport.Unknown; }
    catch { return CapabilitySupport.Unknown; }
}
```

The production probe reuses existing Windows APIs and service methods; it must not run the full diagnostic package.

- [ ] **Step 4: Run tests and verify GREEN**

Expected: capability tests and all prior tests pass.

- [ ] **Step 5: Integrate after first activation**

Add fields:

```csharp
private HardwareCapabilities _hardwareCapabilities = HardwareCapabilities.Unknown;
private readonly HardwareCapabilityService _hardwareCapabilityService = new(new WindowsHardwareCapabilityProbe());
```

After the first status refresh:

```csharp
_ = DetectCapabilitiesAndRefreshPresentationAsync();
```

Apply the policy on the UI thread. In simple mode, collapse unsupported professional features. In professional mode, keep them visible, set `IsEnabled = false`, and set the policy reason as ToolTip and automation help text.

- [ ] **Step 6: Verify no startup blocking**

Launch the app with `nvidia-smi` unavailable or a probe forced to time out. Expected: main window and four mode buttons are usable before capability detection completes; no unhandled exception; professional entry explains the unavailable feature.

Record checkpoint: `hardware-capabilities`.

---

### Task 6: Recommendation Card and One-Click Apply

**Files:**

- Modify: `src/PowerMode.App/Views/MainWindow.xaml`
- Modify: `src/PowerMode.App/Views/MainWindow.xaml.cs`
- Modify: `src/PowerMode.App/Views/MainWindow.AdvancedFeatures.cs`

**Interfaces:**

- Consumes: `ModeRecommendationService.Recommend`
- Produces: `RefreshRecommendationAsync()`
- Produces: `ApplyRecommendationButton_Click`

- [ ] **Step 1: Add failing trigger-metadata test**

Extend recommendation tests:

```csharp
[Fact]
public void Recommendation_ResultContainsGeneratedTimeAndReason()
{
    var before = DateTimeOffset.Now;
    var result = ModeRecommendationService.Recommend(Context());

    Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    Assert.True(result.GeneratedAt >= before);
}
```

Run and confirm the test fails if either field is omitted; then retain the minimal implementation from Task 3.

- [ ] **Step 2: Add the Fluent recommendation card**

Add above the four mode buttons:

```xml
<Border x:Name="RecommendationCard"
        Style="{StaticResource PanelStyle}"
        BorderBrush="{ThemeResource AccentFillColorDefaultBrush}">
    <Grid ColumnSpacing="16">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <FontIcon Glyph="&#xE946;"
                  Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}"/>
        <StackPanel Grid.Column="1" Spacing="3">
            <TextBlock x:Name="RecommendationTitle"
                       Style="{ThemeResource BodyStrongTextBlockStyle}"/>
            <TextBlock x:Name="RecommendationReason"
                       TextWrapping="Wrap"
                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                       AutomationProperties.LiveSetting="Polite"/>
        </StackPanel>
        <Button x:Name="ApplyRecommendationButton"
                Grid.Column="2"
                Style="{StaticResource AccentButtonStyle}"
                Click="ApplyRecommendationButton_Click"/>
    </Grid>
</Border>
```

- [ ] **Step 3: Build recommendation context from existing state**

Use existing configured process lists, `GetSystemPowerStatus`, running process names, `_temperatureProtectionActive`, and `_hardwareCapabilities`. Do not start a second monitoring loop.

```csharp
private ModeRecommendation? _currentRecommendation;

private Task RefreshRecommendationAsync()
{
    var context = CreateRecommendationContext();
    _currentRecommendation = ModeRecommendationService.Recommend(context);
    RecommendationTitle.Text = IsChinese
        ? $"建议：{GetModeDisplayName(_currentRecommendation.Mode)}"
        : $"Suggested: {GetModeDisplayName(_currentRecommendation.Mode)}";
    RecommendationReason.Text = _currentRecommendation.Reason;
    ApplyRecommendationButton.Content = IsChinese ? "一键应用" : "Apply";
    return Task.CompletedTask;
}
```

- [ ] **Step 4: Apply only on explicit click**

```csharp
private async void ApplyRecommendationButton_Click(object sender, RoutedEventArgs e)
{
    var recommendation = _currentRecommendation;
    if (recommendation is null) return;
    await RunModeWithContextAsync(
        recommendation.Mode,
        new SwitchRequestContext(
            "recommendation",
            recommendation.Reason,
            AllowPreview: false));
}
```

No recommendation method may call `RunModeWithContextAsync` itself.

- [ ] **Step 5: Refresh recommendation on meaningful changes**

Refresh after:

- initial capability detection;
- power-source change detected by the existing timer;
- temperature protection transition;
- settings save that changes process lists or low-battery threshold.

Do not poll more frequently than the existing feature timer.

- [ ] **Step 6: Verify**

Run all tests and Release build. Manually verify that startup shows a reason, ignoring the card changes nothing, and clicking applies through the existing mode pipeline with trigger `recommendation`.

Record checkpoint: `mode-recommendation-ui`.

---

### Task 7: Generic Recovery Metadata and Recovery Service

**Files:**

- Modify: `src/PowerMode.App/Services/AutomationEngine.cs`
- Create: `src/PowerMode.App/Services/RecoveryService.cs`
- Create: `tests/PowerMode.App.Tests/RecoveryServiceTests.cs`

**Interfaces:**

- Produces: optional `SwitchHistoryEntry.OperationKind`
- Produces: optional `SwitchHistoryEntry.RelatedOperationId`
- Produces: `SwitchHistoryEntry.IsUndo`
- Produces: `RecoveryService.FindLatestUndoableAsync`
- Produces: `RecoveryService.ResetDefaultsAsync`

- [ ] **Step 1: Write failing undo-selection tests**

```csharp
// tests/PowerMode.App.Tests/RecoveryServiceTests.cs
using Xunit;

namespace PowerModeWinUI.Tests;

public sealed class RecoveryServiceTests
{
    [Fact]
    public async Task FindLatestUndoableAsync_SkipsFailedAndAlreadyUndoneOperations()
    {
        var first = new SwitchHistoryEntry
        {
            Id = Guid.NewGuid(), PreviousMode = "balanced", TargetMode = "high",
            Succeeded = true, OperationKind = "mode-switch"
        };
        var failed = new SwitchHistoryEntry
        {
            Id = Guid.NewGuid(), PreviousMode = "high", TargetMode = "remote",
            Succeeded = false, OperationKind = "mode-switch"
        };
        var undo = new SwitchHistoryEntry
        {
            Id = Guid.NewGuid(), Succeeded = true, OperationKind = "mode-undo",
            IsUndo = true, RelatedOperationId = first.Id
        };
        var latest = new SwitchHistoryEntry
        {
            Id = Guid.NewGuid(), PreviousMode = "remote", TargetMode = "saver",
            Succeeded = true, OperationKind = "mode-switch"
        };
        var history = new InMemoryRecoveryHistory([latest, undo, failed, first]);
        var service = new RecoveryService(history, new FakeRecoveryBackend());

        var result = await service.FindLatestUndoableAsync();

        Assert.Equal(latest.Id, result?.Id);
    }

    [Fact]
    public async Task ResetDefaultsAsync_CreatesSafetyBackupBeforeSaving()
    {
        var backend = new FakeRecoveryBackend();
        var service = new RecoveryService(new InMemoryRecoveryHistory([]), backend);

        await service.ResetDefaultsAsync();

        Assert.Equal(["backup:before-reset-defaults", "save-defaults"], backend.Calls);
    }
}
```

The in-memory history and fake backend must implement the real recovery interfaces and record call order.

- [ ] **Step 2: Run tests and verify RED**

Expected: compile failure because metadata and recovery interfaces do not exist.

- [ ] **Step 3: Add backward-compatible optional metadata**

```csharp
public string OperationKind { get; set; } = "mode-switch";
public Guid? RelatedOperationId { get; set; }
public bool IsUndo { get; set; }
```

Because old JSONL omits these fields, the initializer interprets old records as `mode-switch`.

- [ ] **Step 4: Implement recovery boundaries**

```csharp
public interface IRecoveryHistory
{
    Task<IReadOnlyList<SwitchHistoryEntry>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);
    Task RecordAsync(SwitchHistoryEntry entry, CancellationToken cancellationToken = default);
}

public interface IRecoveryBackend
{
    Task CreateSafetyBackupAsync(string reason, CancellationToken token);
    Task SaveDefaultSettingsAsync(CancellationToken token);
}
```

`FindLatestUndoableAsync` must:

1. collect successful undo entries and their `RelatedOperationId`;
2. return the newest successful standard-mode `mode-switch`;
3. reject entries whose target is not `remote`, `saver`, `balanced`, or `high`;
4. reject entries already referenced by a successful undo.

`ResetDefaultsAsync` must await `CreateSafetyBackupAsync("before-reset-defaults")` before `SaveDefaultSettingsAsync`.

- [ ] **Step 5: Run tests and verify GREEN**

Expected: all tests pass.

- [ ] **Step 6: Update history writers**

Standard mode-switch records keep the compatibility default:

```csharp
OperationKind = "mode-switch"
```

Keep trigger and reason unchanged. Undo writes:

```csharp
OperationKind = "mode-undo",
IsUndo = true,
RelatedOperationId = original.Id,
Trigger = "recovery-center"
```

Configuration restore/reset writes `configuration-restore` or `configuration-reset`.

- [ ] **Step 7: Verification checkpoint**

Run tests and Release build. Expected: old JSONL fixture without new fields deserializes with `OperationKind == "mode-switch"`.

Record checkpoint: `recovery-service`.

---

### Task 8: Recovery Center Window and Safe Operations

**Files:**

- Create: `src/PowerMode.App/Views/RecoveryCenterWindow.xaml`
- Create: `src/PowerMode.App/Views/RecoveryCenterWindow.xaml.cs`
- Modify: `src/PowerMode.App/Views/MainWindow.xaml`
- Modify: `src/PowerMode.App/Views/MainWindow.AdvancedFeatures.cs`
- Modify: `src/PowerMode.App/Views/InsightsWindow.xaml.cs`

**Interfaces:**

- Consumes: `RecoveryService`
- Consumes: existing `RestoreLatestSettingsBackupAsync`
- Consumes: existing `RunModeWithContextAsync`
- Produces: `OpenRecoveryCenterButton_Click`

- [ ] **Step 1: Add failing safety behavior test**

Add to `RecoveryServiceTests`:

```csharp
[Fact]
public async Task ResetDefaultsAsync_BackupFailure_DoesNotSaveDefaults()
{
    var backend = new FakeRecoveryBackend { FailBackup = true };
    var service = new RecoveryService(new InMemoryRecoveryHistory([]), backend);

    await Assert.ThrowsAsync<IOException>(() => service.ResetDefaultsAsync());

    Assert.DoesNotContain("save-defaults", backend.Calls);
}
```

Run and verify RED, then ensure `ResetDefaultsAsync` does not catch-and-continue past backup failure. Run again for GREEN.

- [ ] **Step 2: Create the native recovery window**

Use a Mica-backed WinUI window with three `PanelStyle` cards:

- latest mode operation and undo button;
- latest distinct configuration backup and restore button;
- default settings reset and destructive-styled button.

Each card contains title, impact text, availability reason, and one action. Add one non-closable `InfoBar` for results and progress.

- [ ] **Step 3: Wire undo to the existing mode pipeline**

The recovery window asks its owner to execute:

```csharp
internal async Task<bool> UndoModeOperationAsync(SwitchHistoryEntry original)
{
    var succeeded = await RunModeWithContextAsync(
        original.PreviousMode,
        new SwitchRequestContext(
            "recovery-center",
            $"Undo {original.Id}",
            AllowPreview: false));
    if (succeeded)
        await HistoryStore.Default.RecordAsync(new SwitchHistoryEntry
        {
            PreviousMode = original.TargetMode,
            TargetMode = original.PreviousMode,
            Trigger = "recovery-center",
            Reason = IsChinese ? "撤销最近模式切换" : "Undo latest mode switch",
            Succeeded = true,
            OperationKind = "mode-undo",
            IsUndo = true,
            RelatedOperationId = original.Id
        });
    return succeeded;
}
```

Avoid double-recording by adding a `RecordHistory` flag to `SwitchRequestContext` or by making the regular switch entry the undo entry; choose one implementation and assert exactly one undo record in a test.

- [ ] **Step 4: Wire configuration restore**

Show a confirmation, call the existing atomic restore with `createSafetyBackup: true`, reload settings, apply experience mode, capability policy, and recommendation. Do not change the active Windows plan.

- [ ] **Step 5: Wire default reset**

After confirmation:

1. backup current settings with reason `before-reset-defaults`;
2. save `new PowerModeSettings()`;
3. reload and apply settings;
4. record `configuration-reset`;
5. leave history/backups/telemetry files untouched.

- [ ] **Step 6: Add the main-window entry**

The recovery button remains visible in both simple and professional experiences. If a recovery window already exists, activate it instead of creating another.

- [ ] **Step 7: Update Insights history formatting**

Format:

- old/default kind as normal mode switch;
- `mode-undo` as undo;
- `configuration-restore` as configuration restore;
- `configuration-reset` as defaults reset.

Unknown future kinds must display their raw kind rather than throw.

- [ ] **Step 8: Verify**

Run all tests and Release build. Manually verify:

- no history disables undo;
- one successful switch enables undo;
- undo produces exactly one linked record;
- restoring a backup creates a safety backup;
- defaults reset returns to simple mode;
- current Windows plan is unchanged by configuration restore/reset.

Record checkpoint: `recovery-center`.

---

### Task 9: Fluent, Accessibility, and Responsive Regression Pass

**Files:**

- Modify: `src/PowerMode.App/App.xaml`
- Modify: `src/PowerMode.App/Views/MainWindow.xaml`
- Modify: `src/PowerMode.App/Views/MainWindow.xaml.cs`
- Modify: `src/PowerMode.App/Views/RecoveryCenterWindow.xaml`
- Modify: `src/PowerMode.App/Views/RecoveryCenterWindow.xaml.cs`

**Interfaces:**

- Consumes all presentation state from prior tasks.

- [ ] **Step 1: Add test assertions for non-color state**

Extend policy tests to assert every disabled feature has a non-empty reason. Run RED if any policy path returns an empty reason, then fix the minimal policy output.

- [ ] **Step 2: Replace remaining new hard-coded colors**

Use:

```xml
{ThemeResource AccentFillColorDefaultBrush}
{ThemeResource AccentTextFillColorPrimaryBrush}
{ThemeResource TextFillColorPrimaryBrush}
{ThemeResource TextFillColorSecondaryBrush}
{ThemeResource SystemFillColorCriticalBrush}
```

Do not add RGB values for recommendation, current mode, recovery, or error states.

- [ ] **Step 3: Add accessibility metadata**

Set:

- `AutomationProperties.Name` on experience toggle and recovery actions;
- `AutomationProperties.HelpText` to capability-disabled controls;
- `AutomationProperties.LiveSetting="Polite"` on recommendation reason and recovery result;
- item status on current mode;
- accelerator keys remain `1–4` and `F5`.

- [ ] **Step 4: Verify responsive layouts**

At 100%, 125%, and 150% DPI:

- simple mode has no unused log column;
- professional mode preserves the current two-column layout;
- narrow header uses icon-only actions with ToolTips;
- recommendation reason wraps;
- recovery cards do not truncate action names;
- high contrast retains visible selection and disabled state.

- [ ] **Step 5: Verification checkpoint**

Run tests and Release build. Expected: 0 failed tests and 0 build errors. Record checkpoint: `fluent-accessibility`.

---

### Task 10: Documentation Layering

**Files:**

- Create: `docs/USER_GUIDE.md`
- Create: `docs/DEVELOPMENT.md`
- Modify: `README.md`

**Interfaces:**

- Consumes the finalized behavior and file names.

- [ ] **Step 1: Write a documentation link check before moving content**

Run this after creating temporary empty target files:

```powershell
$readme = Get-Content -Raw .\README.md
if ($readme -notmatch 'docs/USER_GUIDE\.md') { throw 'README missing user guide link' }
if ($readme -notmatch 'docs/DEVELOPMENT\.md') { throw 'README missing development guide link' }
```

Expected before README edit: failure `README missing user guide link`.

- [ ] **Step 2: Rewrite the root README as a short landing page**

Required sections, in this order:

1. PowerMode summary;
2. download/start;
3. three-minute simple-mode quick start;
4. four-mode table;
5. recommendation and recovery safety;
6. system requirements;
7. links to full user and development docs.

Keep the root README below 220 lines and remove the dated “UI/UX 优化” changelog section.

- [ ] **Step 3: Move complete user content**

`docs/USER_GUIDE.md` must contain:

- simple and professional modes;
- status cards;
- custom CPU and profiles;
- automation and protection;
- insights and diagnostics;
- recovery center;
- shortcuts and tray;
- configuration and backups;
- complete FAQ.

- [ ] **Step 4: Move developer content**

`docs/DEVELOPMENT.md` must contain:

- architecture and services;
- project structure;
- compatibility rules;
- build prerequisites;
- Release and portable publish commands;
- tests;
- packaging layout;
- maintenance/troubleshooting.

- [ ] **Step 5: Verify links and portable README**

Run:

```powershell
$readme = Get-Content -Raw .\README.md
if ($readme -notmatch 'docs/USER_GUIDE\.md') { throw 'README missing user guide link' }
if ($readme -notmatch 'docs/DEVELOPMENT\.md') { throw 'README missing development guide link' }
(Get-Content .\README.md).Count
```

Expected: no exception and line count below 220.

Record checkpoint: `documentation-layering`.

---

### Task 11: Full Verification and Portable Release

**Files:**

- Verify: all source, tests, docs, and `scripts/Publish-Portable.ps1`
- Output: `dist/PowerMode-win-x64`
- Output: `dist/PowerMode-win-x64.zip`

**Interfaces:**

- Consumes all previous tasks.

- [ ] **Step 1: Run the complete test suite**

```powershell
dotnet test .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false
```

Expected: all tests passed, 0 failed.

- [ ] **Step 2: Run a clean Release build**

```powershell
dotnet build-server shutdown
dotnet restore .\PowerMode.slnx -p:Platform=x64
dotnet build .\PowerMode.slnx -c Release -p:Platform=x64 --no-restore -m:1 -p:UseSharedCompilation=false
```

Expected: 0 errors. Investigate and resolve warnings introduced by this work.

- [ ] **Step 3: Run behavior regression checks**

Verify:

- existing configuration loads unchanged except default simple experience;
- four mode buttons and CLI aliases retain behavior;
- recommendation never applies without click;
- unsupported hardware follows simple-hide/professional-disable policy;
- mode switches still rollback on failure;
- recovery center does not reset Windows plans;
- old history JSONL is readable;
- automatic post-switch status calibration still updates关屏/睡眠;
- English and Chinese labels remain available.

- [ ] **Step 4: Run visual checks**

Use the Windows UI inspection workflow to verify:

- simple and professional layouts;
- light/dark/high-contrast themes;
- 100%, 125%, 150% DPI;
- minimum and default window widths;
- recommendation busy/success states;
- recovery confirmations and results.

Do not bypass a locked desktop; defer visual checks if Windows cannot activate the app.

- [ ] **Step 5: Publish the portable directory and ZIP**

```powershell
$env:UseSharedCompilation = 'false'
powershell -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Publish-Portable.ps1 -Root . -CreateZip
```

Expected:

- `dist/PowerMode-win-x64/App/PowerMode.exe` exists;
- `dist/PowerMode-win-x64/00-START PowerMode.bat` exists;
- `dist/PowerMode-win-x64.zip` exists.

- [ ] **Step 6: Verify artifacts**

```powershell
$rootHash = (Get-FileHash .\README.md).Hash
$packageHash = (Get-FileHash .\dist\PowerMode-win-x64\README.md).Hash
[pscustomobject]@{
  ExeExists = Test-Path .\dist\PowerMode-win-x64\App\PowerMode.exe
  LauncherExists = Test-Path '.\dist\PowerMode-win-x64\00-START PowerMode.bat'
  ReadmeSynced = $rootHash -eq $packageHash
  ZipBytes = (Get-Item .\dist\PowerMode-win-x64.zip).Length
  ZipSha256 = (Get-FileHash .\dist\PowerMode-win-x64.zip).Hash
}
```

Expected: both booleans true, README synced true, ZIP size greater than zero, SHA-256 present.

- [ ] **Step 7: Final verification checkpoint**

Record:

- test count and failures;
- Release errors/warnings;
- capability combinations checked;
- recovery scenarios checked;
- visual DPI/theme states checked or explicitly deferred;
- portable ZIP hash.

Checkpoint name: `mature-native-experience-release`.

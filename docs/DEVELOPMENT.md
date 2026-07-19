# PowerMode 开发与构建

面向维护者与贡献者。用户使用说明见 [USER_GUIDE](USER_GUIDE.md)；仓库落地页见 [README](../README.md)。

## 技术栈与架构

| 项 | 当前选择 |
| --- | --- |
| UI | WinUI 3（`UseWinUI`） |
| 运行时 | .NET 10（`net10.0-windows10.0.26100.0`） |
| 应用 SDK | Microsoft.WindowsAppSDK **2.2.0** |
| 平台 | **x64 only**（`Platforms=x64`，`RuntimeIdentifier=win-x64`） |
| 打包 | `WindowsPackageType=None`，自包含发布（`SelfContained` + `WindowsAppSDKSelfContained`） |
| 最低 OS | `SupportedOSPlatformVersion` 10.0.19041.0（Windows 10 2004+） |
| CLI | BAT 启动器 + 内嵌 PowerShell（`src/PowerMode.Cli/PowerModeSwitcher.bat`） |
| 测试 | xUnit，`tests/PowerMode.App.Tests` |

主要程序集命名空间为 `PowerModeWinUI`；输出程序名为 `PowerMode.exe`。

### 架构分层（逻辑）

```text
Views (MainWindow / Settings / Insights / RecoveryCenter)
        │
        ├─ Experience mode + capability presentation
        ├─ Recommendation UI gate (apply only on click)
        └─ Recovery center actions
        │
Services (pure-ish policies + Windows integration)
        │
        ├─ ModeRecommendationService / RecommendationUiLogic
        ├─ CapabilityVisibilityPolicy / HardwareCapabilityService
        ├─ RecoveryService + ProductionRecoveryBackend
        ├─ AutomationEngine + HistoryStore
        ├─ MonitoringService
        └─ SystemIntegrationService (startup, backups, diagnostics, update check)
        │
Models
        ├─ PowerModeSettings / SettingsStore
        ├─ ExperienceMode
        └─ ModeRecommendation / HardwareCapabilities
        │
External
        ├─ powercfg / Windows APIs
        ├─ nvidia-smi (optional)
        └─ PowerModeSwitcher.bat (mode apply & verify)
```

GUI 通过缓存后的 CLI 脚本执行模式切换与校验，保证与命令行行为一致。单实例由 `App.xaml.cs` 中命名 Mutex 保证。

### 关键服务职责

| 服务 / 类型 | 职责 |
| --- | --- |
| `ModeRecommendationService` | 纯函数：根据上下文给出建议模式与原因码 |
| `RecommendationUiLogic` | 建议上下文构建、展示文案、应用按钮状态、点击后才构造 apply 请求 |
| `CapabilityVisibilityPolicy` | 按体验模式 + 硬件能力决定控件可见/可用及原因 |
| `HardwareCapabilityService` | 探测电池、亮度、NVIDIA、WiFi、温度、通知、热键等能力 |
| `RecoveryService` | 撤销模式切换、恢复备份、重置默认；带操作闸与审计 |
| `AutomationEngine` | 规则评估、自动切换、历史写入 |
| `MonitoringService` | 遥测采样、电池健康缓存、导出相关数据 |
| `SystemIntegrationService` | 开机启动、配置备份/恢复、诊断包、可选更新检查（**不下载不安装**） |
| `SettingsStore` | `%LOCALAPPDATA%\PowerMode\settings.json` 读写与归一化 |

## 项目结构

```text
PowerMode/
├─ PowerMode.slnx                 # 解决方案：App + Tests
├─ global.json                    # .NET 10 SDK 基线
├─ README.md                      # 简短落地页（亦复制进便携包）
├─ Build-Portable.bat             # → scripts 发布
├─ PowerModeWinUI.bat             # → scripts\Launch-PowerMode.bat
├─ PowerModeSwitcher.bat          # → src\PowerMode.Cli\...
├─ docs/
│  ├─ USER_GUIDE.md
│  ├─ DEVELOPMENT.md
│  └─ superpowers/                # 规格与实现计划（勿在功能任务中改写）
├─ scripts/
│  ├─ Publish-Portable.ps1        # 自包含便携发布权威脚本
│  ├─ Launch-PowerMode.bat
│  └─ Build-Portable.bat
├─ src/
│  ├─ PowerMode.App/
│  │  ├─ Views/
│  │  ├─ Models/
│  │  ├─ Services/
│  │  ├─ Properties/app.manifest
│  │  └─ PowerMode.App.csproj
│  └─ PowerMode.Cli/
│     └─ PowerModeSwitcher.bat
├─ tests/
│  └─ PowerMode.App.Tests/
└─ dist/                          # 生成物，通常不入库
   └─ PowerMode-win-x64/
```

日常开发关注 `src` 与 `tests`；`scripts` 负责编排；`dist` 可随时重新生成。

## 配置兼容策略

- 配置路径：`Environment.SpecialFolder.LocalApplicationData` + `PowerMode\settings.json`。
- `SettingsStore.Normalize`：
  - 未知 `ExperienceMode` → `Simple`；
  - `LastMode` 仅接受 `remote|saver|balanced|high`，否则 `balanced`；
  - 监控间隔下限 10 秒；温度/电量/备份数量等做范围夹取；
  - 规则与预设列表 null 安全处理。
- **默认体验为简单模式**（`ExperienceMode.Simple`）。
- 恢复中心「重置默认」写入 `new PowerModeSettings()`，不删除历史 JSONL 与备份目录。
- 旧字段缺失时按属性默认值反序列化；不要引入破坏性重命名而不做兼容。
- GUI CLI 缓存目录：`%LOCALAPPDATA%\PowerMode\Cache`（按源 BAT 最后写入时间/Ticks 失效，不是内容哈希；发布时 CLI 的 SHA-256 完整性校验是独立步骤）。

相关测试：`SettingsCompatibilityTests`。

## 构建前提

- Windows x64 开发机；
- **.NET 10 SDK**（`global.json` 指定 `10.0.301`，`rollForward: latestFeature`）；
- 可构建 WinUI / Windows App SDK 的工作负载与桌面开发组件；
- Windows PowerShell 5.1+（发布与启动脚本）；
- 运行测试不要求物理 NVIDIA，但部分运行时能力探测依赖真实硬件。

## Release 构建与便携发布

### 开发验证构建

```powershell
dotnet build .\PowerMode.slnx -c Release -p:Platform=x64
```

产物示例：

```text
src\PowerMode.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\PowerMode.exe
```

`dotnet build` 适合本地验证；**分发请使用自包含 publish**。

### 便携发布（推荐）

```powershell
.\scripts\Publish-Portable.ps1 -Root . -CreateZip
```

或：

```bat
.\Build-Portable.bat
```

脚本行为摘要：

1. `dotnet publish` App 项目：`Release`、`win-x64`、`--self-contained true`、`-p:Platform=x64`，输出到暂存目录 `dist\.PowerMode-win-x64.staging\App`。
2. 将 `PowerModeSwitcher.bat` 与根 `README.md` 复制到暂存根目录（并避免 App 内重复副本干扰）。
3. 校验 CLI 文件哈希与源一致。
4. 生成 `00-START PowerMode.bat`（`start "" "%~dp0App\PowerMode.exe"`）。
5. 写入 `build-info.json`（UTC 时间、`App/PowerMode.exe`、`win-x64`、`selfContained`）。
6. 原子替换 `dist\PowerMode-win-x64`（若进程正从该目录运行则失败退出）。
7. 可选生成 `dist\PowerMode-win-x64.zip`。

**完整文档（`docs/*`）随源码保留，不会额外塞进便携运行目录**；便携包仅带根 README。

### 从源码启动（开发便利）

```bat
.\PowerModeWinUI.bat
```

`scripts\Launch-PowerMode.bat` 在 `App\PowerMode.exe` 缺失或源/CLI/README 新于 `build-info.json` 时自动发布。

### 清理

```powershell
dotnet clean .\src\PowerMode.App\PowerMode.App.csproj -c Release -p:Platform=x64
dotnet build .\src\PowerMode.App\PowerMode.App.csproj -c Release -p:Platform=x64
```

## 测试

```powershell
dotnet test .\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false
```

当前测试项目覆盖（示例）：

| 测试类 | 关注点 |
| --- | --- |
| `ModeRecommendationServiceTests` | 建议优先级与原因 |
| `RecommendationUiLogicTests` | 应用闸、按钮状态、本地化原因 |
| `CapabilityVisibilityPolicyTests` | 简单/专业 + 能力显隐 |
| `CapabilityPresentationLifetimeTests` | 控件呈现生命周期 |
| `HardwareCapabilityServiceTests` | 探测结果映射 |
| `RecoveryServiceTests` | 撤销 / 恢复 / 重置与回滚 |
| `RecoveryCenterPresentationTests` | 恢复中心呈现与自动化名 |
| `SettingsCompatibilityTests` | 配置归一化与兼容 |
| `FluentAccessibilityPresentationTests` | Fluent / a11y 相关静态约定 |
| `WindowsCapabilityProbeResultTests` | 探测结果模型 |

优先把可单测逻辑放在 `Services` 纯函数与策略中；UI 代码通过 InternalsVisibleTo 暴露必要内部类型给测试。

## 便携包装布局

```text
dist\PowerMode-win-x64\
├─ 00-START PowerMode.bat
├─ PowerModeSwitcher.bat
├─ README.md
├─ build-info.json
└─ App\
   └─ PowerMode.exe   # 及自包含依赖 / WinUI 资源
```

兼容性约定：

- 根目录只放启动入口、CLI 与 README；运行库在 `App\`。
- 不要把 `App\PowerMode.exe` 单独拷走作为“绿色版”。
- 发布中若 `PowerMode` 进程路径位于输出目录下，脚本会拒绝覆盖。

## 维护与故障排查

### 配置损坏

1. 优先走 GUI 恢复中心或功能中心备份恢复。
2. 手动时先复制整个 `%LOCALAPPDATA%\PowerMode\`。
3. 删除 `settings.json` 后重启即可回到默认（简单模式等）。

### 模式切换与 CLI 不一致

- 确认 GUI 找到的 CLI 与便携包 / 源码中的 `PowerModeSwitcher.bat` 为同一代。
- 清掉 `%LOCALAPPDATA%\PowerMode\Cache` 后再试。
- 用 `.\PowerModeSwitcher.bat status` / `verify` 对照。

### 便携发布失败

- 退出正在运行的便携版 `PowerMode.exe` 后重试。
- 确认 `dotnet` 为 SDK 10.x：`dotnet --info`。
- 检查工作区路径权限；脚本拒绝修改仓库根以外路径。

### 能力相关 UI 空白或禁用

属预期：`CapabilityVisibilityPolicy` 会在不支持时禁用并给出原因。专业模式仍可能显示部分表面以便配置，但不可用控件保持禁用。

### 更新检查

`SystemIntegrationService` 仅在配置了更新 API 且用户启用时查询；**不会**自动下载或安装更新。

### 文档分层

- 根 `README.md`：几分钟可读完的中文落地页。
- `docs/USER_GUIDE.md`：完整用户材料。
- `docs/DEVELOPMENT.md`：本文件。
- 不要把 dated 功能 changelog 堆回 README 首页。

### 回归清单（发布前）

1. `dotnet test` Release x64 通过。
2. `Publish-Portable.ps1 -CreateZip` 成功。
3. 双击 `00-START PowerMode.bat` 可启动；CLI `status` 可用。
4. 简单模式默认；建议仅点击后应用；恢复重置不影响 Windows 方案批量还原。
5. 根 README 链接到 `docs/USER_GUIDE.md` 与 `docs/DEVELOPMENT.md`，行数保持精简。

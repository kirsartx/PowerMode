# PowerMode 主窗口重设计：托盘快捷浮窗

日期：2026-11-15
状态：已与用户确认方向，待实现计划

## 目标

把主窗口从“宽屏仪表盘”改成类似 Windows 快速设置的窄浮窗：平时从托盘一点即出、点别处即收；需要专业工具时原地向下展开；需要长时间停留时可以固定成普通窗口。现有功能全部保留，只重新分组和排轻重。

## 已确认的决策

| 问题 | 选择 |
| --- | --- |
| 整体方向 | C · 快捷设置浮窗 |
| 专业功能位置 | 原地向下展开（简单 = 收起，专业 = 展开） |
| 窗口行为 | 托盘浮窗，可用 📌 固定成普通窗口 |
| 配色 | 只用系统强调色，Mica 背景，跟随系统浅色/深色 |
| 范围 | 只重做主窗口；功能中心、洞察、恢复中心只统一配色/圆角/间距，不改结构 |

沿用 `2026-07-19-mature-native-experience-design.md` 的约束：不引入 NavigationView 多页、不引入 MVVM/DI 或新的生产依赖、不做复杂动画、推荐永不自动应用、保留高 DPI 与系统主题。

## 布局

窗口内容宽度固定为 360 逻辑像素（按 DPI 换算物理像素），高度随内容变化。

### 收起（简单模式）

自上而下：

1. **标题行**：左侧 `PowerMode` 标题 + 副标题（电源来源 · 上次更新时间，对应现有 `SubtitleText` / `LastUpdatedText`）。右侧三个图标按钮：
   - 刷新 ⟳（`RefreshButton`，保留 F5 加速键与刷新中的 ProgressRing）
   - 固定 📌（新增 ToggleButton，见“窗口行为”）
   - 更多 ⋯（`MoreButton`，始终可见，内容见下）
2. **推荐提示**：一行虚线描边的提示条，左侧原因文字，右侧“应用”链接按钮（复用 `RecommendationCard` / `ApplyRecommendationButton` 的逻辑）。仅当推荐模式与当前模式不同时显示；点击才切换。
3. **模式磁贴**：2×2 网格，顺序 远程 / 低功耗 / 平衡 / 高性能。每块显示名称、一行参数说明（复用 `*Description`）、右上角小号快捷键数字。当前模式用 `AccentFillColorDefaultBrush` 填充、文字反白并显示 ✓；切换中显示 ProgressRing。键盘 1–4 保持不变。磁贴最小高度 56。
4. **状态指标**：一张小卡，3 列网格：CPU 上限、亮度、电源、GPU、关屏/睡眠（后者跨两列）。取代现有 6 张大状态卡；“模式”一项由磁贴表达，不再单独显示。
5. **快捷开关**：两个药丸样式 ToggleButton：自动切换（`AutoQuickToggle`）、实时刷新（`LiveQuickToggle`）。
6. **底栏**：细分隔线上方。左侧一行状态文字（取代 `StatusBar` InfoBar；成功/警告/错误用对应的 SystemFillColor 前景与图标，`StatusActionButton` 以链接形式跟在文字后）。右侧“专业 ⌄”链接按钮。

### 展开（专业模式）

在模式磁贴、状态指标和快捷开关之后、底栏之前，依次插入：

1. **自定义远程**：小节标题 + `CpuSlider`（20–50）与 `CpuBox` 同行，下方强调色按钮 `RemoteCustomButton`（“以 N% 应用远程模式”）。
2. **工具**：2×2 按钮网格：验证、修复、打开 Wi-Fi、远程（关 Wi-Fi，危险色前景）。
3. **日志**：小节标题行显示条数，右侧自动滚动 / 复制 / 清空（文字按钮）。下方等宽字体日志框（`LogBox`，Cascadia Mono），固定高度约 160。
4. **诊断信息**：`CliPathText` 与 `ModeSwitchDiagnosticText`（切换耗时）以三级文字显示在日志下方。

底栏右侧按钮变为“收起 ⌃”。

展开/收起直接对应现有 `ExperienceMode`（Simple / Professional），继续持久化到 `PowerModeSettings.ExperienceMode`。顶部原来的 `ExperienceModeButton` 移除，由底栏按钮代替。

当内容高度超过当前显示器工作区高度时，内容区在窗口内纵向滚动（标题行与底栏固定）。

### “⋯” 菜单

功能中心、洞察、恢复中心、分隔线、语言切换、分隔线、退出。专业模式下的 Wi-Fi / 验证等不进菜单。

## 窗口行为

两种状态，由 📌 切换并持久化（新增设置项 `IsPinned`，默认 `true`，保证首次双击 exe 时看到的是普通窗口）。

### 未固定（浮窗）

- 无系统标题栏、无最小化/最大化，不在任务栏显示按钮，圆角 + 阴影。
- 托盘图标**单击**：窗口隐藏时在托盘所在显示器工作区右下角弹出（距离边缘 12 逻辑像素）；窗口可见时隐藏。
- 失去激活（`Activated` 事件 `WindowActivationState.Deactivated`）或按 Esc 时隐藏。打开功能中心等二级窗口、弹出 ⋯ 菜单或对话框导致的失焦不触发隐藏。
- 展开/收起时保持底边贴住工作区，向上生长。

### 固定（普通窗口）

- 使用 OverlappedPresenter，扩展内容到标题栏（自绘标题行即拖动区），保留最小化与关闭按钮；不可调整宽度，不可最大化。
- 显示任务栏按钮，失焦不隐藏，记住位置（多显示器下越界时回落到主显示器居中）。
- 最小化与关闭行为与现在一致：最小化隐藏到托盘，关闭退出程序（含设置窗口未保存时的确认流程）。

### 通用

- 托盘右键菜单、全局热键、单实例逻辑保持不变；热键唤起时按当前固定状态决定弹出位置。
- 托盘双击仍然显示窗口（兼容旧习惯）。

## 视觉规范

- 背景：`MicaBackdrop Kind="Base"`；浮窗模式若 Mica 不可用则回落到 `DesktopAcrylicBackdrop`。
- 唯一彩色：系统强调色（`AccentFillColorDefaultBrush` / `AccentTextFillColorPrimaryBrush`）。危险操作用 `SystemFillColorCriticalBrush`。不引入自定义模式配色。
- 卡片：`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`，圆角 8；窗口圆角 8（系统默认）。
- 间距：外边距 14，小节间距 12，网格间距 6。
- 字体：标题 Segoe UI Variable Display SemiBold 14；正文 Segoe UI Variable Text 12；小节标题 Caption 10 SemiBold 次级色；日志 Cascadia Mono 11。
- 二级窗口（功能中心 / 洞察 / 恢复中心 / 设置）：统一到同一组卡片样式、圆角 8、外边距和字体资源，结构不动。

以上样式集中放在 `App.xaml` 资源中（替换现有 `PanelStyle` / `StatusCardStyle` 的数值），各窗口引用同一组资源。

## 受影响的代码

- `Views/MainWindow.xaml`：整体重排为单列；移除 `StatusCardsGrid`、`ExperienceModeButton`、独立的 `ProfessionalLogPanel` 列布局与 `StatusBar` InfoBar。控件 `x:Name` 尽量保留，减少后台代码改动。
- `Views/MainWindow.xaml.cs` / `MainWindow.Features.cs`：托盘单击切换、浮窗定位、失焦隐藏、固定切换、Esc 处理；`ApplyExperienceMode` 改为控制展开区可见性。
- `Services/ResponsiveLayoutPolicy.cs` 与 `DpiAwareWindowSizer.cs`：宽屏三档布局不再适用，改为“固定宽度 + 工作区限高”的简单策略；对应测试同步更新。
- `Models/PowerModeSettings.cs`：新增 `IsPinned` 与固定窗口位置。
- `App.xaml`：样式资源更新。
- 二级窗口 XAML：只改样式引用。

## 测试

- 单元测试：新的尺寸策略（固定宽度、限高、浮窗锚定位置计算，含多显示器与不同 DPI）；`IsPinned` 设置的读写与默认值；展开/收起与 ExperienceMode 的映射。
- 现有测试：ResponsiveLayoutPolicy 相关断言按新策略改写；其余测试应保持通过。
- 手动验证：浅色/深色、100%/150%/200% 缩放、托盘单击开关、失焦收起、打开功能中心不误收起、固定后拖动与重启记忆、键盘 1–4 / F5 / Esc、展开超出屏幕时滚动、Feature Center 正常打开（回归之前的闪退）。

## 不做

- 不改 CLI / 引擎行为。
- 不重做二级窗口结构。
- 不加模式专属配色、动画或新依赖。

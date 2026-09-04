# Refresh 交互可靠性实施计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**目标：** 将主窗口 Refresh 做成明确、可访问、可测试的按钮/F5 双入口，同时保持刷新只读。

**设计：** 在 `MainWindow.xaml` 的 RefreshButton 上声明显式 `KeyboardAccelerator` 和 `IsTabStop`；在 `MainWindow.xaml.cs` 增加加速器处理器并设置本地化 HelpText；在 `MainWindow.Features.cs` 删除根 Grid 对 F5 的重复分支。用现有的 XAML/源码契约测试验证事件路由、单次处理和只读调用。

**验证：** 先运行聚焦测试观察失败，再实现；实现后运行聚焦测试、全量 Release 测试、Release 构建和正式便携发布审计。

## 任务 1：先写失败的 Refresh 契约测试

**文件：** `tests/PowerMode.App.Tests/RefreshInteractionPresentationTests.cs`

1. 读取 `MainWindow.xaml`，断言 RefreshButton 显式声明 `IsTabStop="True"`、`AutomationProperties.AcceleratorKey="F5"`、嵌套 `KeyboardAccelerator Key="F5"` 和 `Invoked="RefreshKeyboardAccelerator_Invoked"`。
2. 读取 `MainWindow.xaml.cs`，断言存在加速器处理器、设置 `args.Handled = true`、调用 `RefreshStatusAsync`，并为 Refresh 设置双语 `AutomationProperties.SetHelpText`。
3. 读取 `MainWindow.Features.cs`，断言根 KeyDown 仍保留数字 1–4 快捷键，但不再包含 `VirtualKey.F5` 分支。
4. 运行：

   `dotnet test .\\tests\\PowerMode.App.Tests\\PowerMode.App.Tests.csproj -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore --filter FullyQualifiedName~RefreshInteractionPresentationTests`

   预期失败原因是实现契约尚不存在。

## 任务 2：实现显式键盘与无障碍入口

**文件：** `src/PowerMode.App/Views/MainWindow.xaml`、`src/PowerMode.App/Views/MainWindow.xaml.cs`、`src/PowerMode.App/Views/MainWindow.Features.cs`

1. 为 RefreshButton 添加 `IsTabStop="True"` 与 F5 KeyboardAccelerator。
2. 添加 `RefreshKeyboardAccelerator_Invoked` 异步处理器，先设置 `args.Handled = true`，再调用已有 `RefreshStatusAsync`。
3. 在 `ApplyLanguage` 为 Refresh 设置中文/英文 HelpText，明确“读取状态”而非“切换模式”。
4. 删除根 Grid F5 分支，保持数字快捷键逻辑不变。
5. 重新运行任务 1 的聚焦测试，预期通过。

## 任务 3：文档与增量审查

**文件：** `docs/USER_GUIDE.md`、`docs/DEVELOPMENT.md`

1. 用户指南说明 Refresh 可点击或按 F5，且该操作只读取状态。
2. 开发文档说明 Refresh 交互契约测试覆盖 XAML/路由；Computer Use 的窗口激活失败属于环境层限制。
3. 运行 `git diff --check`，审查变更是否越过只读范围。
4. 提交：`feat: harden refresh keyboard accessibility`。
5. 请求一次独立代码审查，处理所有 Critical/Important 反馈。

## 任务 4：最终验证与发布

1. 运行聚焦 Refresh 契约测试。
2. 运行全量 Release 测试：

   `dotnet test .\\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore --logger "console;verbosity=minimal"`

3. 运行 Release 构建并确认 0 warning/0 error。
4. 运行正式便携发布：

   `powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\\scripts\\Publish-Portable.ps1 -Root . -CreateZip`

5. 审计发布目录、ZIP sidecar hash、版本、commit metadata 和禁止残留物。
6. 更新 `.superpowers/sdd/progress.md` 与 Task 14 报告，最后汇总结果和环境限制。

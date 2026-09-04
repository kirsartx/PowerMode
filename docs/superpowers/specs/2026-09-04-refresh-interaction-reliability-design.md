# Refresh 交互可靠性设计

日期：2026-09-04

## 背景

本轮桌面冒烟验证中，Computer Use 在点击主窗口 Refresh 前返回了 `failed to activate captured window`。应用源码中的 Refresh 按钮已有 Click 处理器，且 `RefreshStatusAsync` 只读取电源与硬件状态；因此该错误发生在窗口激活/输入注入层，不能由应用内 Click 逻辑直接修复。

仍然需要把应用自身的交互契约做得明确、可测试、可替代验证：Refresh 应同时提供可见按钮入口、显式 F5 键盘入口和完整的无障碍说明，并保证同一个 F5 只触发一次刷新。

## 目标

1. 保留现有 Refresh 的只读语义和忙碌保护。
2. 在 RefreshButton 上声明显式 `KeyboardAccelerator Key="F5"`，让键盘入口与按钮绑定在同一控件上。
3. 将 F5 的处理从根 Grid 的 KeyDown 分支移交给该显式加速器，避免双重触发。
4. 为 Refresh 设置随语言切换的 Automation HelpText，说明其读取行为和快捷键。
5. 用 XAML/源码契约测试锁定入口、单次处理和只读调用路径。
6. 在用户/开发文档中区分应用交互故障和外部窗口激活环境限制。

## 非目标

- 不改变 `RefreshStatusAsync` 的读取流程。
- 不让 Refresh 调用任何模式切换、自动化或电源写入操作。
- 不重复进行会触发当前真实自动切换设置的桌面启动测试。
- 不把 Computer Use 的窗口激活错误伪装成应用测试通过。

## 方案

采用最小增量方案：在现有按钮上添加 `IsTabStop` 和 `KeyboardAccelerator`，新增对应的异步事件处理器，并从根键盘处理器删除 F5 分支；数字 1–4 模式快捷键继续保留在根键盘处理器中。语言应用阶段同时设置 Automation Name 与 HelpText。测试直接解析现有 XAML，并检查 MainWindow 事件路由与只读刷新调用，避免启动真实 WinUI 窗口或执行电源命令。

## 验收标准

- RefreshButton 可通过可见 Click 和 F5 两条应用内入口进入 `RefreshStatusAsync`。
- F5 由单一 `KeyboardAccelerator` 处理并标记为已处理；根 Grid 不再重复处理 F5。
- 中文和英文 HelpText 均包含刷新动作与 F5 提示。
- `dotnet test .\\PowerMode.slnx -c Release -p:Platform=x64 -m:1 -p:UseSharedCompilation=false --no-restore` 全部通过。
- Release 构建、便携发布和发布物审计全部通过。
- 记录 Computer Use 激活层的环境限制，不将其作为应用层失败结论。

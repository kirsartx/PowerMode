# Refresh 状态一致性设计

日期：2026-09-06

## 问题

主窗口刷新在读取完成后直接应用 `PowerModeState`。如果用户在读取期间启动模式切换，刷新返回的旧快照可能在模式切换结果之后继续更新 `_activeModeKey` 和状态卡片，造成界面短暂或持续显示过期状态。

入口处的 `_modeSwitchInProgress` 检查只能覆盖刷新开始前的状态，无法覆盖“刷新已开始、模式切换随后开始”的时间窗口。刷新自身的 `_refreshInProgress` 只负责去重，也无法判断结果是否仍然有效。

## 目标

1. 模式切换、恢复或退出快照恢复开始后，所有此前启动的刷新结果都不能更新当前状态呈现。
2. 模式切换完成后，新的刷新可以正常应用结果。
3. 保留当前刷新去重、超时、异常和 UI 状态恢复行为。
4. 将版本判断抽成线程安全、无 UI 依赖的纯服务，提供真实行为测试。
5. 不阻塞模式切换，不修改 PowerMode 后端或电源命令语义。

## 设计

新增 `PowerStateRevisionGate`：

- `Capture()` 返回当前版本；
- `BeginMutation()` 原子递增版本并标记一个活动 mutation；
- `EndMutation()` 结束一个活动 mutation，支持嵌套生命周期；
- `CanApply(capturedRevision, mutationInProgress)` 仅在没有进行中的 mutation、没有进行中的模式切换且捕获版本仍是当前版本时返回 `true`。

`RefreshStatusAsync` 在调用 `ReadStateAsync` 前捕获版本；读取完成后，在任何 `ApplyPowerModeState` 或刷新成功/失败摘要写入前调用 `CanApply`。版本失效时直接结束本次刷新，让 `finally` 清理进度指示器和门控状态。

模式事务在设置 `_modeSwitchInProgress = true` 的同一位置调用 `BeginMutation()`，并在 `finally` 中结束 mutation；恢复中心的实际恢复、退出时的启动快照恢复也覆盖同一生命周期。这样所有经过统一模式事务入口的预设、自定义、自动化和恢复操作都会使在途刷新失效，且 mutation 等待期间不会启动新的刷新。

## 测试

- `PowerStateRevisionGateTests` 使用真实 gate 实例验证初始版本可应用、开始 mutation 会淘汰旧快照、新快照可应用，以及 mutation 标记会阻止应用。
- `PowerStateRevisionGateTests` 另外验证 mutation 生命周期、嵌套 mutation 和已在途读操作在 mutation 开始后不能应用。
- `RefreshInteractionPresentationTests` 增加源码路由契约，确保刷新捕获版本并在应用状态前检查 gate，且模式切换、恢复、退出恢复和验证后的旧快照都有边界检查；已有 F5、只读、重复刷新测试继续保留。
- 完整 Release 测试和便携发布验证继续作为完成门槛。

## 非目标

- 不启动真实 GUI 做电源写入测试。
- 不引入 MVVM、依赖注入容器或新的运行时包。
- 不把刷新排队到模式切换之后；本轮只淘汰过期读数。

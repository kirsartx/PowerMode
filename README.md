# PowerMode

PowerMode 是面向 Windows 笔记本与远程使用场景的电源模式切换工具，适合通过 Hermes、uu 远程、ToDesk、向日葵等软件长期连接电脑时使用。

当前桌面程序基于 **WinUI 3 + .NET 10 + Windows App SDK 2.2**，以 **Windows 10/11 x64 自包含便携版**发布，无需 MSIX 安装，也不要求目标电脑单独安装 .NET 或 Windows App SDK 运行库。

同时提供：

- WinUI 3 图形界面（默认 **简单模式**，可切换到专业模式）；
- 与 GUI 共用的 BAT + PowerShell 命令行（CLI 兼容入口）；
- 远程、低功耗、平衡、高性能四种预设；
- 本地建议卡片、恢复中心、自定义预设、自动化与洞察（详见用户指南）。

## 获取与启动

本仓库以**便携目录**方式分发，没有单独的安装程序下载页或强制在线安装流程。

### 使用已构建的便携包

若已有发布输出目录或 ZIP：

```text
dist\PowerMode-win-x64\
dist\PowerMode-win-x64.zip   # 可选，由 -CreateZip 生成
```

解压或复制整个 `PowerMode-win-x64` 目录后，双击根目录最前面的：

```text
00-START PowerMode.bat
```

便携包布局（由 `scripts/Publish-Portable.ps1` 生成）：

```text
PowerMode-win-x64/
├─ 00-START PowerMode.bat    # 启动 App\PowerMode.exe
├─ PowerModeSwitcher.bat     # 命令行入口
├─ README.md
├─ build-info.json
└─ App\
   └─ PowerMode.exe          # 自包含主程序与运行库
```

请保持目录结构完整，不要单独挪走 `App\PowerMode.exe`。

### 从源码仓库启动

| 入口 | 作用 |
| --- | --- |
| `PowerModeWinUI.bat` | 按需发布便携版并启动 GUI |
| `PowerModeSwitcher.bat` | 调用当前 CLI |
| `Build-Portable.bat` | 生成/刷新 `dist\PowerMode-win-x64` |

```powershell
# 构建便携版（可选打包 ZIP）
.\scripts\Publish-Portable.ps1 -Root . -CreateZip
```

命令行菜单（不传参数时交互式）：

```bat
.\PowerModeSwitcher.bat
```

## 三分钟快速开始（简单模式）

1. 双击 `00-START PowerMode.bat`（便携包）或 `PowerModeWinUI.bat`（源码树）。
2. 首次启动默认为 **简单模式**：四个模式按钮、本地建议卡片、状态卡片与恢复入口。
3. 按场景点选一种模式，或阅读建议后**显式点击「一键应用」**（不会自动改模式）。
4. 需要自定义 CPU、实时日志、功能中心、洞察与高级操作时，切换到 **专业模式**。
5. 设置异常时可打开 **恢复**；恢复/重置只动 PowerMode 应用配置，不会批量重置 Windows 电源方案。

简单模式侧重“看状态 → 选模式 → 可选应用建议”；专业能力与自动化细节见 [用户指南](docs/USER_GUIDE.md)。

## 四种预设模式

| 模式 | 默认 CPU 上限 | 亮度 | 关屏 | 睡眠/休眠 | 适用场景 |
| --- | ---: | ---: | ---: | --- | --- |
| 远程推荐 | 32% | 50% | 60 秒 | 禁用 | Hermes、uu 远程、长期待机 |
| 低功耗 | 30% | 50% | 60 秒 | 禁用 | 轻负载、省电和低温 |
| 平衡 | 100% | 100% | 保留方案现有值 | 禁用 | 日常桌面 |
| 高性能 | 100% | 100% | 保留方案现有值 | 禁用 | 游戏、渲染与高负载 |

远程与低功耗的 CPU 上限可在专业模式中按 `20–50%` 自定义。实际效果取决于 Windows 版本、固件与驱动；部分设备不支持通过 `powercfg` 调亮度。

## 建议与恢复（安全说明）

**建议卡片（本地 / 顾问式）**

- 根据供电、进程列表、低电量阈值与温度保护状态等**本地信息**给出建议模式与原因。
- **仅作提示**：不会在后台自动应用；只有点击「一键应用」后才会切换。
- 不上传遥测，也不依赖云端推荐服务。

**恢复中心**

- **撤销最近模式切换**：回到该次操作前的标准预设（远程 / 低功耗 / 平衡 / 高性能）。
- **恢复配置备份**：先安全备份当前 `settings.json`，再原子恢复最近一份内容不同的备份；**不改变**当前 Windows 活动电源方案。
- **重置为默认值**：先安全备份，再将 PowerMode 应用设置写回默认（默认体验为简单模式等）；**历史、备份与遥测数据保留**，**不**批量重置 Windows 电源方案。

关闭 WiFi 的「远程 + 关闭 WiFi」等操作仍可能断开远程会话；使用前请确认有线网络可用。

## 系统要求

- Windows 10 版本 2004（内部版本 19041）或更高、Windows 11；
- x64 处理器；
- 便携版自包含：目标机无需单独安装 .NET 或 Windows App SDK；
- 从源码开发/发布需要 **.NET 10 SDK**（见 `global.json`）；
- Windows PowerShell 5.1 或更高；
- 部分网卡启停可能需要管理员权限（UAC）；
- NVIDIA 显卡功耗等指标依赖本机可用的 `nvidia-smi`（无 NVIDIA 时相关卡片会标明不可用）。

## 完整文档

`docs/USER_GUIDE.md` 与 `docs/DEVELOPMENT.md` 仅存在于**源码树**；当前便携 ZIP / 运行目录**不包含** `docs/*`。下列链接在克隆或打开源仓库时可访问；若仅解压便携包，请到源仓库阅读完整文档。

| 文档 | 内容 |
| --- | --- |
| [用户指南](docs/USER_GUIDE.md) | 简单/专业模式、状态卡片、自定义与自动化、洞察、恢复中心、快捷键、配置位置与 FAQ |
| [开发与构建](docs/DEVELOPMENT.md) | 架构、项目结构、兼容策略、构建与便携发布、测试、维护 |

配置与历史默认位于当前用户目录（详见用户指南）：

```text
%LOCALAPPDATA%\PowerMode\settings.json
%LOCALAPPDATA%\PowerMode\switch-history.jsonl
%LOCALAPPDATA%\PowerMode\backups\
%LOCALAPPDATA%\PowerMode\Cache\
```

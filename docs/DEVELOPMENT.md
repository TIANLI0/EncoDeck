# 开发指南（DEVELOPMENT）

## 环境

| 组件 | 版本（2026-10 实测可用） |
|---|---|
| Windows | 10 19041+ / 11（开发机：Windows 11 build 26340 x64） |
| .NET SDK | 10.0.402（`global.json` 要求 10.0.100+，`latestFeature` 前滚，禁止预览版） |
| Windows App SDK | 2.5.1（稳定版） |
| Windows SDK 目标框架 | `net10.0-windows10.0.26100.0`（`10.0.28000.0` 在 SDK 10.0.402 上报 NETSDK1140） |
| H.NotifyIcon.WinUI | 2.4.1（稳定版；2.5.0 仅有预览版） |
| CommunityToolkit.Mvvm | 8.4.2 |
| xunit.v3 | 3.2.2 + xunit.runner.visualstudio 3.1.5（VSTest） |

**不需要** Visual Studio、MSIX 工具或任何 workload：`dotnet build` 即可编译 XAML。所有包版本集中在 `Directory.Packages.props`。

## 常用命令

```powershell
# 全部库 + 测试 + Probe
dotnet build EncoDeck.slnx -c Debug

# WinUI 应用（需要指定平台）
dotnet build src/EncoDeck.App -c Debug -p:Platform=x64

# 单元测试
dotnet test tests/EncoDeck.Tests -c Debug

# 发布（unpackaged，自包含）
dotnet publish src/EncoDeck.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/EncoDeck-win-x64

# 诊断工具
dotnet run --project tools/EncoDeck.Probe -- help
```

## 项目结构

```
src/EncoDeck.Core        领域模型、状态存储、能力系统、策略（net10.0）
src/EncoDeck.Protocol    帧/会话/解析/型号定义/设备控制器（net10.0）
src/EncoDeck.Bluetooth   WinRT 蓝牙：发现、SDP、RFCOMM、监督器、DeviceManager
src/EncoDeck.Platform    设置、日志、缓存、开机启动、单实例、Shell 查询
src/EncoDeck.App         WinUI 3 界面（unpackaged）
tools/EncoDeck.Probe     无界面诊断工具
tools/assets             图标与 .resw 生成脚本
tests/EncoDeck.Tests     单元测试（含 X4 实测抓包回归）
```

## 已知的坑（均已在本仓库中处理）

1. **发布必须 `EnableMsixTooling=true`**：否则 `dotnet publish` 不复制 `EncoDeck.pri`（编译后的 XAML），发布版启动即 `XamlParseException`；Debug 输出目录不受影响，容易漏掉。
2. **`TaskbarIcon.ForceCreate()` 默认开启效率模式**（EcoQoS + 空闲优先级，作用于整个进程），必须传 `false`。
3. **WinUI 下托盘图标不会自动移除**：退出前调用 `Dispose()`，并设置 `DispatcherShutdownMode = OnExplicitShutdown`，用 `Application.Exit()` 退出。
4. **原生托盘菜单（PopupMenu 模式）忽略 `Click`**，菜单项必须用 `Command`；菜单不跟随深色主题。
5. **不要固定托盘图标 GUID**：Windows 会把 GUID 与 exe 路径绑定，移动目录后注册失败。
6. **自包含 unpackaged 下 `AppNotificationManager.Register()` 抛 `0x8007007E`**（WinAppSDK 2.5.1，上游 #6774）；EncoDeck 使用托盘气泡通知代替 Toast。
7. `WMC9999 未将对象引用设置到对象的实例` 往往只是前面 C# 编译错误的连锁反应，先修 C# 错误。
8. `LibraryImport` 需要 `AllowUnsafeBlocks`。
9. x:Bind 函数绑定调用的是实例方法；静态辅助方法放在独立类型（如 `Services/Ui.cs`）中并写全名。
10. `AppWindow` 的尺寸/位置是物理像素，需要乘以 DPI 缩放。
11. `BluetoothDevice.GetDeviceSelectorFromPairingState(true)` 的结果偶尔包含 `FromIdAsync` 拒绝的条目，必须逐个容错。
12. **设置文件里缺少的 `init` 属性读成类型默认值**（`false` / `0`），不是声明里的默认值：默认开启的新开关要用 `bool?` 并在 `Normalize()` 补回（见 `ShowAboveQuickSettings`、`PopupScale`）。
13. **Windows 11 快速设置没有扩展 API**（`QuickSettingsWatcher`）：在 26H2 build 26340 上实测，它是 `ShellHost.exe` 的 `ControlCenterWindow`，窗口和工作区一样高且透明，只有窗口区域（`GetWindowRgnBox`，卡片加 12 DIP 外边距）可见、能点击；UIA 读不到内部元素。它一失去前台就开始约 300 ms 的关闭动画然后才 HIDE，所以面板在前台切走时就退场。面板必须 `WS_EX_NOACTIVATE`，否则一点它快速设置就关闭。更早的 Windows 11 版本若不是这个窗口类，功能静默不生效。面板高度会随降噪档位行出现而变化，上方放不下时改放在卡片旁边（`PopupPlacement.PlaceAboveQuickSettings`）。
14. **WinUI `RadioButtons` 在同一轮里重建 `Items` 再设 `SelectedIndex`，选中状态不显示**（`--ui-snapshot` 可复现）：设备状态驱动的单选一律用普通 `RadioButton` + `GroupName`，每次同步直接设 `IsChecked`（音效页 EQ、空间音频）。
15. **H.NotifyIcon 内置 `TrayPopup` 需要已有窗口**：EncoDeck 自己实现托盘面板，用 `Shell_NotifyIconGetRect` 定位（图标在溢出区时返回 `^` 按钮位置）。
16. **全局快捷键**（`GlobalHotkeys`）：`RegisterHotKey` 注册到 UI 线程上的 message-only 窗口，WinUI 的消息循环会派发 `WM_HOTKEY`；同一组合只能注册一次（`ERROR_HOTKEY_ALREADY_REGISTERED` = 被占用）。录制新组合时要先注销已有快捷键，否则按下已设置的组合会直接触发而不是被录到。

## 部署说明（unpackaged）

- `WindowsAppSDKSelfContained=true` + `SelfContained=true`：Windows App SDK 运行时与 .NET 运行时都放在应用目录，用户无需安装任何运行时；不需要 VC++ 运行库（已检查原生 DLL 导入表）。
- 实测发布目录约 178 MB / 479 个文件（已排除 WinAppSDK 的 AI/ML/Search/Widgets 组件），包含 `EncoDeck.exe`、`EncoDeck.pri`、`Microsoft.WindowsAppRuntime.dll`、`Microsoft.ui.xaml.dll`。
- 已在本机验证发布版：`--background` 启动 → 发现 X4 → `--exit` 正常退出（退出码 0）。**尚未**在“干净”的、从未安装过开发工具的机器上验证；发布前请在虚拟机中复测。
- 单文件发布会解压到 `%TEMP%`，不推荐；用户数据始终写入 `%LOCALAPPDATA%\EncoDeck`。
- ARM64：项目已配置 `win-arm64`，但尚未构建测试，CI 只发布 x64。
- 资源占用（实测）：安装包约 48 MB；托盘常驻空闲时 60 秒窗口 CPU ≈ 0.18% / 单核，工作集约 128 MB。

## 命令行参数

| 参数 | 作用 |
|---|---|
| `--background` | 启动后只驻留托盘（开机启动使用） |
| `--exit` | 让已运行的实例干净退出（移除托盘图标、关闭控制通道） |
| `--popup-preview <boot.mp4> <输出目录> [--theme light\|dark] [--name 名称] [--levels 80,75,60] [--at 0.3,1,2.5,4] [--offscreen]` | 诊断：不连耳机，用任意资源包视频（自动找上级 `config.json`）和示例电量显示连接弹窗，在指定秒数截图（屏幕 + PrintWindow）并写 `<前缀>.json` 后退出；不启动蓝牙、不写设置、不抢焦点，有全屏应用时自动改为屏幕外 |
| `--brand-demo <models.json> [--demo-brand 品牌] [--demo-check [--demo-exit]]` | 手动演示：不启动蓝牙，按清单（`brand`、`name`、`directory`，可选 `profileId`、`popup`、`model`、`motion`）逐型号模拟一次成功会话（`DemoDiscovery` 补齐需设备应答的功能），所有写入命令只改演示状态。`--demo-check` 在屏幕外截取每个型号的首页、音效、控制、弹窗、托盘，并把各页实际显示的功能区写入 `session/previews/results.json`；加 `--demo-exit` 则检查完直接退出、不弹出选择窗口 |
| `--updated` | 由安装程序在应用内更新完成后传入：无全屏应用时打开“关于”页，否则只发托盘通知 |
| 再次启动 | 唤醒已运行实例的主窗口（单实例） |

## 数据位置

`%LOCALAPPDATA%\EncoDeck\`：`settings.json`、`device-cache.json`（哈希键，无地址）、`logs\encodeck-yyyyMMdd.log`（JSON Lines，保留 7 天，已脱敏）、`diagnostics\`、`Resources\`（设备素材缓存，见 [架构说明](ARCHITECTURE.md#设备资源)）、`Updates\`（应用内更新下载的安装包、`last-check.txt`、`install.log`）、`Components\`（实验室组件：`LHDC\`、`A2DPWB\`，各含 `current.json`、版本目录和续传中的下载）。

## 发布流程

应用和组件分开发布：

1. **组件**（仅当固定的组件变化时）：修改 `src/EncoDeck.Platform/Components/components.json` 的版本、标签和上游提交，推送对应标签：
   - `lhdc-v<组件版本>`：CI 从固定的 LHDC-Win 提交编译并发布组件 Release；把运行摘要中的大小与 SHA-256 填回 `components.json`。
   - `a2dpwb-v<组件版本>`：CI 下载上游官方 portable 包，与 `components.json` 中已填写的大小和 SHA-256 逐字节比对后原样发布。
   组件 Release 都以 `--latest=false` 创建，仓库的“最新版本”始终是应用；两个发布脚本都会附上 `component.json`（`build/component-manifest.ps1` 生成，已发布的应用据此发现新组件）。`minApp` 填写需要的最低应用版本，接口变化时提高 `interface`。
2. **应用**：把 `CHANGELOG.md` 顶部的 `## [Unreleased]` 改成 `## [<版本>] - <日期>`（预发布用 `1.12.0-beta.2` 这类带后缀的版本；可在日期后加 ` · 中文标题 / English title`，发布名即为“EncoDeck <版本> · 标题”。每个版本先写中文的“新增 / 改进 / 修复 / 移除”，再在 `---` 分隔线下写对应的英文 Added / Improved / Fixed / Removed，都在同一个 `## [版本]` 段落里，发布说明会完整包含两者），需要时同步 `Directory.Build.props` 的 `VersionPrefix`，然后推送到 `main`。CI 发现 CHANGELOG 最新版本还没有 Release 时，在测试和打包通过后自动创建 `v<版本>` 标签并直接发布（不是草稿），说明取自 CHANGELOG 对应段落；发布前先用 `build/verify-components.ps1` 确认每个固定的组件包已发布且大小、摘要一致。带后缀的版本标记为预发布，且不提交微软商店；正式版设为“最新版本”并提交商店。版本号与 `VersionPrefix` 不一致时 CI 失败。手动推送 `v<版本>` 标签也会发布（已有同名 Release 时跳过）。

`EncoDeck.Probe bridge` 可以只读查看 A2DPWB 后台；`--instance <名称> --start <目录> --config-dir <目录>` 会以独立实例和独立设置目录启动一个测试后台，`--quit` 结束它，不影响正常安装的 A2DPWB。`EncoDeck.exe --ui-snapshot <目录> --bridge-instance <名称>` 在离屏截图中加入实验室卡片（连接到该测试后台）和首页的 Bridge 状态。

## 应用内更新

`UpdateManager`（`src/EncoDeck.Platform/Updates`）匿名查询 `https://api.github.com/repos/TIANLI0/EncoDeck/releases`：跳过草稿；未开启“接收预览版”时跳过预发布（GitHub 标记或带 `-` 后缀的标签）；按 SemVer 取最高版本中带有 `EncoDeck-Setup-<版本>-win-x64.exe` 的发布。下载时校验大小，并按 `<安装包>.sha256` / `SHA256SUMS` 资产（或 GitHub 资产的 `digest`）校验 SHA-256，完成后才原子重命名。开启“自动检查更新”时启动约 1 分钟后检查，之后每 24 小时一次（时间记录在 `Updates\last-check.txt`），发现新版本即后台下载并发一次托盘通知（全屏时延后）；只有用户点击“立即重启并更新”才会以 `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1` 启动安装程序并退出，安装完成后 `[Run]` 以 `--updated` 重新启动 EncoDeck。

## 新增设备型号

1. 用 Probe 收集证据（`services`、`query`、`watch`），见 [TESTING.md](TESTING.md)。
2. 内置型号列表由 `python -I tools/assets/make_models.py <whitelist.json> src/EncoDeck.Protocol/Oppo/Models/OppoCatalog.g.cs` 生成（只写互操作事实），解读规则在 `OppoCatalog.cs`；有真机证据的型号可以像 X4 一样在 `OppoModels.cs` 中手写定义，并用测试保证与配置生成的结果一致。
3. 把实测抓包（去除事件 06 等敏感数据）加入测试。
4. 更新 COMPATIBILITY.md 与 PROTOCOL_FINDINGS.md。

## 代码规范

- 每个源文件以 `// SPDX-License-Identifier: GPL-3.0-only` 开头。
- 协议解析器必须严格：格式不符返回 false，而不是猜测。
- 任何写命令都要：能力门控 → 确认 → 回读 → 失败回滚。
- 日志中不得出现完整蓝牙地址；涉及设备列表的帧用 `OppoRedaction`。

## 待验证事项

- 逐项真机验证已有写入功能（见 [设备兼容性](COMPATIBILITY.md#enco-x4-验证状态)），补齐多品牌自定义 EQ、手势、查找耳机等功能。
- 手动走查 UI：连接弹窗动画、托盘面板多屏 / DPI、无障碍。
- 真机走一遍安装 → 覆盖升级 → 卸载流程，并在干净环境验证自包含发布版。
- 明确不做：OTA 固件刷写；依赖手机的功能（听力增强测试、耳机专注、对话感知、AI 摘要、翻译等）。BLE 开盒弹窗需先实测广播格式（见 [PROTOCOL_FINDINGS.md](PROTOCOL_FINDINGS.md) §6）。

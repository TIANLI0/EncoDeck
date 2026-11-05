# 架构（ARCHITECTURE）

## 分层

```
┌──────────────────────────────────────────────────────────────┐
│ EncoDeck.App (WinUI 3)                                       │
│  Views: MainWindow / 各页面 / TrayPanelWindow / ConnectionPopup│
│  ViewModels: DeviceViewModel（唯一 UI 状态投影）、Settings    │
│  Services: AppHost、TrayIconService、WindowManager、Theme…    │
└───────────────┬──────────────────────────────┬───────────────┘
                │ IDeviceCommandService         │ IDeviceStateStore（只读订阅）
┌───────────────▼──────────────────────────────┴───────────────┐
│ EncoDeck.Bluetooth (WinRT 公共 API)                          │
│  DeviceManager：已配对设备 → 选择当前设备 → 监督控制通道      │
│  PairedDeviceWatcher / BluetoothLinkMonitor（事件驱动）       │
│  ControlChannelSupervisor（连接/退避重连状态机）              │
│  RfcommChannelConnector / RfcommByteChannel、SDP 解析        │
└───────────────┬──────────────────────────────────────────────┘
                │ IChannelConnector / IDuplexByteChannel
┌───────────────▼──────────────────────────────────────────────┐
│ EncoDeck.Protocol（纯 .NET，与平台无关）                      │
│  OppoFrame / OppoLink / OppoFrameDecoder（分帧、分片）        │
│  OppoSession（串行写入、请求关联、超时、取消、主动上报）      │
│  OppoDeviceController（初始化、事件→状态、确认式命令/回滚）   │
│  OppoParsers、OppoCapabilityBitmap、OppoRedaction            │
│  Models: OppoModelDefinition / OppoModels / OppoDeviceProfile │
└───────────────┬──────────────────────────────────────────────┘
┌───────────────▼──────────────┐  ┌────────────────────────────┐
│ EncoDeck.Core（纯 .NET）      │  │ EncoDeck.Platform（Win32） │
│  DeviceState / Observed<T>   │  │  设置、日志、缓存、开机启动 │
│  能力系统、命令结果、Profile  │  │  单实例、全屏检测、窗口定位 │
│  弹窗/低电量策略、重连策略    │  └────────────────────────────┘
└──────────────────────────────┘
EncoDeck.Probe：无界面诊断工具，复用 Bluetooth + Protocol 的同一套代码。
```

`Core` 与 `Protocol` 只依赖 .NET，可在任何平台单元测试；`Bluetooth`、`Platform`、`App` 依赖 Windows。

## 统一状态源

- `IDeviceStateStore` 保存当前设备的**不可变快照** `DeviceState`。所有写入都通过 `TryUpdate(deviceId, mutate)`；`deviceId` 不匹配时丢弃，防止切换设备后旧会话的迟到结果污染新设备。
- 每个值都是 `Observed<T>`：值 + 时间戳 + 来源（`DeviceNotification` / `QueryResponse` / `CommandConfirmation` / `Optimistic` / `Cache`）。UI 依据来源和年龄显示“上次读取”。
- 状态还包含：待执行命令（`PendingCommands`）、最近响应时间、最后更新来源、最近错误、单调递增 `Revision`。
- 主窗口、托盘面板、连接弹窗、托盘提示文字全部绑定同一个 `DeviceViewModel`，后者只从 store 投影；不存在各自维护的设备信息副本。

## 命令流程（DeviceCommandService）

所有改变耳机状态的操作都经过 `IDeviceCommandService`（由 `DeviceManager` 实现，转发给当前 `OppoDeviceController`）：

```
校验能力（Profile + 是否允许实验性）→ 校验能力位图
 → 写入乐观值 + PendingCommand
 → 发送 SET（固定 SEQ F0）→ 等待 0x84xx 确认（3 s）
 → 状态字节 = 0 ? 回读（0x010C / 0x010F / 0x010D）: Rejected
 → 回读与目标一致 ? Success : Rejected（并采用设备真实值）
 → 失败：仅回滚仍为 Optimistic 的字段，记录 LastError
```

结果类型：`Success / Timeout / TransportError / Rejected / Unsupported / NotConnected / Cancelled`。超时后仍会尝试回读，若设备实际已切换则视为成功。

## 连接状态机（ControlChannelSupervisor）

```
链路断开 ──► PairedNotConnected / Disconnected（等待链路事件，不轮询）
链路建立 ──► AudioConnected ─(1 s 稳定)─► ControlConnecting
ControlConnecting ─成功─► ControlReady ─通道掉线且链路仍在─► Reconnecting
                  └失败─► Reconnecting（1,2,4,8,16,30 s，最多 6 次）
重试耗尽 / 无控制服务 / 拒绝访问 ─► ControlUnavailable（等待用户重试或链路重新建立）
```

同一时间最多一个连接尝试；没有无限高速重连。系统从睡眠唤醒时（`PowerManager.SystemSuspendStatusChanged`）主动重建通道，因为睡眠前的 socket 常常“假活”。

## 设备 Profile 与能力系统

- `IDeviceProfile`（Core）：型号 ID、显示名、图片键、能力表、降噪选项、EQ 预设、控制服务 UUID、匹配评分。
- `IDeviceCapabilities`：15 项能力，每项 `Supported / Unsupported / Unknown / Experimental` + 证据说明。UI 只显示 `Supported`；`Experimental` 需在设置中开启；`Unknown` 永不提供。
- `OppoModelDefinition`（Protocol）：声明式型号数据——产品 ID、蓝牙名、UUID、降噪模式树（父/子 protocolIndex）、EQ 预设、功能开关 ID、订阅事件。新增型号只需新增一个定义，并附带证据；**MainWindow 中没有任何型号判断**。
- 安全降级：按名称匹配到型号、但产品 ID 不符时，控制器切换为只读通用配置 `oppo-generic`，不发送型号专属写命令。

## 生命周期与性能

- 空闲时没有轮询定时器：设备列表（`DeviceWatcher`）、链路状态（`ConnectionStatusChanged`）、耳机状态（订阅事件）全部由推送驱动。
- 唯一的计时器：弹窗显示期间的倒计时、缓存的延迟保存（20 s 合并写入）。
- 主窗口关闭即释放（不是隐藏）；托盘面板复用一个实例；连接弹窗每次创建、关闭后释放。
- 退出时依次：移除托盘图标 → 关闭窗口 → 释放 DeviceManager（关闭 RFCOMM、停止 watcher、释放 BluetoothDevice）→ 刷新缓存与日志。

## 设备资源

识别到耳机上报的产品 ID 和颜色后，`DeviceResourceSync` 在后台通过 [EncoAPI](https://encoapi.vercel.app) 获取对应图片、连接动画和模型，不影响蓝牙会话初始化。EncoAPI 是独立社区服务，与各耳机厂商无隶属关系；素材权利属于原权利人，不适用本项目代码 GPL，也不随安装包分发。

- **缓存**：`%LOCALAPPDATA%\EncoDeck\Resources\downloads\<产品ID>\<颜色>\`，`manifest.json` 引用经过 SHA-256 校验的文件。元数据至多每 24 小时检查一次，哈希未变不重复下载；更新失败保留已有缓存，无缓存时显示通用插图。
- **隐私**：请求只包含产品 ID 和颜色，不发送设备名称、蓝牙地址、序列号或账户信息。
- **展示兜底**：首页依次使用原厂 3D / 转盘模型、原厂产品动画、对静态产品图的本地 Composition 动效；连接弹窗独立选择原厂视频、逐帧 WebP 或 Lottie。无型号图时使用通用插画，不借用其他型号；系统关闭动画时保持静止。华为首页视频按深浅色主题分别选择，深色资源缺失时回退产品图动效。
- **设置 › 设备素材**：显示下载状态、进度与失败原因，可“重新拉取”（忽略 24 小时缓存）或打开缓存文件夹。进度由 `EncoApiResourceClient.EnsureAsync` 报告。

### 3D 模型

`model` 素材包的 `config.json` 二选一：`modelWebp`（预渲染 360° 转盘，动画 WebP）或 `model3D`（`.vfxms` 场景包）。`.vfxms` 格式：

- 40 字节大端文件头：config / glb / ibl / skybox / animation 五段的（起始，长度）；
- config：场景 JSON（`modelScale`、光照参数、按材质序号覆盖的材质参数，只应用 FLOAT/FLOAT4 类型）；
- glb：glTF 2.0 二进制模型（金属度-粗糙度 PBR）；
- ibl：KTX 1.1 立方体贴图（R11G11B10F，含 mip 预滤波与球谐系数）；skybox 不使用（透明背景）。

渲染代码在 `src/EncoDeck.Platform/Media/Scene3D`（Direct3D 11）。`EncoDeck.Probe render-model --file x.vfxms --out <目录>` 可离屏渲染为 PNG 用于排查。

## 可选组件

LHDC 与 A2DP Bridge 是实验室页的可选组件，不随安装包分发，按需下载（详见 [LHDC](LHDC.md)、[A2DP Bridge](A2DP_BRIDGE.md)）。

- **独立发布**：每个组件有自己的版本和 Release 标签（`lhdc-v0.8.8-r1`、`a2dpwb-v2.0.1-r1`），由 `.github/workflows/components.yml` 构建（LHDC，从固定的上游提交编译）或镜像（A2DPWB，上游官方构建原样复制）。应用发版不重建组件，只用 `build/verify-components.ps1` 检查固定的包已经发布。组件标签不是 SemVer，应用内更新不会把它们当成新版本。A2DPWB 的条目带 `upstreamDownloads`：应用只从上游官方 Release 下载它，不用 EncoDeck 的镜像。
- **应用内固定**：`src/EncoDeck.Platform/Components/components.json`（嵌入程序集）固定每个包的文件名、大小、SHA-256 和上游提交。`ComponentCatalog` 读取并严格校验它；应用不通过 GitHub API 决定下载什么。
- **安装**：`ComponentInstaller` 按 EncoDeck 组件 Release → 包内镜像的顺序下载（`upstreamDownloads` 的组件只用包内镜像，即上游 Release）；暂时性错误在同一来源重试并以 `Range` 续传，4xx 或摘要不符换下一个来源。校验通过后安全解压（拒绝越界路径和链接）、校验组件标识，再移入 `Components\<组件>\<版本>-<摘要前 12 位>`，最后原子更新 `current.json`。只保留当前版本：更新后旧版本立即删除；清理时先重命名再删除，正在使用的目录（例如运行中的程序）整体保留，等下次清理。`Uninstall` 以同样方式删除整个组件目录。
- **组件更新**：每个组件 Release 附带 `component.json`（与 `components.json` 中一项同形，另有 `interface` 与 `minApp`）。`ComponentUpdateClient` 读取 Releases 列表，按组件版本（`x.y.z-rN`，修订号按数字比较）从新到旧检查候选：标签、版本、接口和最低应用版本都要符合，列出的每个包都必须是该 Release 的资产且大小、GitHub 摘要一致。通过的版本与内置固定值同等对待（按其大小和 SHA-256 下载）。A2DPWB 不走这条路：`CheckUpstreamAsync` 读取上游官方 Releases，取与固定版本同一主版本（2.x）中最新的版本（含测试版，修订号记为 `r0`），要求每个包都是该 Release 的资产且有 GitHub 的 SHA-256 摘要，再用标签查出提交供安装时核对 `SOURCE.txt`。实验室页打开时每天最多检查一次，也可手动检查；LHDC 卸载前检查服务是否已禁用、是否仍有设备绑定 LHDC 驱动，A2DP Bridge 卸载时可用 `a2dpwb-driver.exe restore --records` 一次恢复所有适配器。
- **A2DP Bridge 运行时**：`A2dpBridgeMonitor` 以 A2DPWB 的单实例互斥体判断后台是否运行（打开即关闭），之后通过每用户命名管道 `\\.\pipe\a2dpwb-<SID>`（长度前缀 JSON，协议 1）接收快照和按修订号递增的事件；修订号缺口时请求完整快照并重放期间事件。`A2dpBridgeCompanion` 按 Bridge 正在连接的蓝牙地址与当前设备匹配，写入 `DeviceState.Bridge`；Bridge 推流时首页、快捷面板和托盘据此显示“A2DP Bridge”徽标，编码标记由 `CodecDisplay` 统一取 Bridge 的流信息。A2DPWB 2.0.2 及以上可经 Bridge 打开 RFCOMM 控制通道（`BridgeRoutingConnector`）；Windows 没有蓝牙适配器时以 `NoWindowsLink` 代替 Windows 链路，只跟随 Bridge。
- **商店版**：不下载、不启动组件（MSIX 子进程会进入包的虚拟化环境）；A2DP Bridge 只连接用户自行启动的后台。

## 隐私

- `BluetoothAddress` 默认格式化为 `**:**:**:**:2A:A3`；日志写入前经 `LogRedactor` 再次脱敏。
- `OppoRedaction` 遮盖包含其他设备名称/地址的帧（事件 06、`0x8112`）及能力以外的敏感响应。
- 缓存文件以哈希作键，不写入蓝牙地址。

## 扩展点

- `IChannelConnector` / `IDuplexByteChannel`：水月雨 GAIA 已加入 BLE GATT 通道，保留每条通知的消息边界；其他品牌继续使用 RFCOMM。GATT 控制不等于 LE Audio 音频支持。
- BLE 广播开盒弹窗：需先实测广播格式（见 PROTOCOL_FINDINGS §6），再实现 `IBleAdvertisementSource`。
- 新命令：在 `OppoCommand` 增加常量，在 `OppoParsers` 增加严格解析器，在控制器中通过能力位图门控。

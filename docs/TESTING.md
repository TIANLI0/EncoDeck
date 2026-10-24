# 测试（TESTING）

## 1. 自动化测试

```powershell
dotnet test tests/EncoDeck.Tests -c Debug
```

最近一次多品牌改动全量测试：480 项通过（2026-10-23）。覆盖范围如下，当前数量以实际运行结果为准：

| 领域 | 测试 |
|---|---|
| 帧编解码 | 布局、变长 LEN（127/128 边界）、分片与重组、非法帧拒绝 |
| 流式分帧 | 半包、逐字节、粘包、粘包+半包、前置垃圾、伪 0xAA、负载内的 0xAA、大量数据跨缓冲区 |
| 解析器 | **Enco X4 实测抓包**：电量（含不上报盒子）、降噪响应/事件、佩戴、功能、固件、EQ 列表/推送、事件码、确认 |
| 会话 | 请求/响应关联、超时、取消、并发串行化、备用响应命令、远端关闭、写失败、订阅者异常隔离 |
| 控制器 | 初始化、确认式命令、拒绝/未生效/超时回滚、实验性开关、未知选项、产品 ID 不符降级、能力位图门控、事件同步、盒子电量“上次读取”、设备切换隔离 |
| 重连状态机 | 链路断开等待、链路循环、通道掉线重连、有界重试→不可用→手动重试、无服务不重试、释放 |
| 其他 | 状态存储、能力过滤、弹窗冷却/全屏抑制、低电量迟滞、弹窗定位（四边/多屏/负坐标）、日志脱敏、设置持久化、缓存恢复、SDP 解析 |

`FakeByteChannel` / `FakeOppoDevice` 用于测试；应用另有显式启用的品牌 UI 演示模式，演示不运行真实蓝牙会话，不能用作实机兼容证据。

多品牌回归可单独运行：

```powershell
dotnet test tests/EncoDeck.Tests --filter "FullyQualifiedName~Xiaomi|FullyQualifiedName~Huawei|FullyQualifiedName~SonyAkg"
```

`XiaomiFeaturesTests` 覆盖官方配置报文、写后回读、拒绝写入、通知 ACK 和未知型号；`HuaweiDeviceControllerTests` 覆盖独立空间音频、错误应答和非法 TLV；`SonyAkgTests` 覆盖 MDR v1/v2、EQ 能力及 BES 自动播放/暂停。协议模拟器通过不代表全部型号实机可用。

### MOONDROP 控制通道回归

```powershell
dotnet test tests/EncoDeck.Tests -c Debug --filter "FullyQualifiedName~ControlChannelSupervisorTests|FullyQualifiedName~Moondrop|FullyQualifiedName~Gaia"
```

2026-10-24：43 项控制通道 / MOONDROP 定向测试和全套 516 项测试通过；新增 EDGE 四字节 ANC 回包与写后回读回归，覆盖 GATT 和 RFCOMM。其他控制器生命周期、错误释放和手动恢复覆盖保留。

2026-10-25：新增 GAIA LDAC / LC3 独立发现、入耳检测、空间音频 / 头部跟踪联合回读、动态低音和左右声道交换的模拟设备回归；覆盖 feature 缺失、无有效回读、写入拒绝与回滚。WinUI `--ui-snapshot --profile moondrop-edge` 验证实验性开启后控件可见、写入中禁用；关闭实验性或 feature 缺失时隐藏，空间音频状态未知时隐藏、动态低音无有效值时禁用；证据在 `artifacts/verification/moondrop-expanded/`，是合成状态，不连接蓝牙。`Moondrop` / `DemoDiscovery` 定向测试 51 项、全套 623 项测试通过。新增设置均为实验性功能，尚未在真机验证。当日 `Probe monitor --seconds 15` 只读运行识别到 EDGE，但设备未连接（`PairedNotConnected`），无法取得新增设置的真实回包。

Windows EDGE 优先通过公共 Winsock `AF_BTH/SOCK_STREAM/BTHPROTO_RFCOMM` 打开所选经典蓝牙地址的已验证通道 1。`Socket.ConnectAsync` 使用的 ConnectEx 被蓝牙提供程序拒绝，因此使用线程池上的普通 `Socket.Connect`，10 秒超时 / 取消时释放 socket。连接后以 GAIA 应用版本、能力与实际状态响应判定就绪；不扫描通道、不按附近同名 BLE 猜测、不改配对或音频连接。其他型号保留 WinRT SDP / GATT 路径。

支持页面仅显示本地化二元标签，隐藏未知项及未移植 MOONDROP 功能，但不修改协议能力与写入门控。UI 状态检查需区分控制就绪、音频已连接但控制不可用、以及未选择设备；隔离状态诊断不能证明真实蓝牙已连接。

真实 EDGE 固件 1.3.0 场景见 `artifacts/verification/windows-control-channel.txt`：实际生产 supervisor 约 4.39 秒进入 `ControlReady`，二次刷新成功，读到电量 40%、降噪关闭、增益低、双设备关闭。现有 `EncoDeck.Probe monitor --seconds 15 --verbose` 同样自动选中并进入控制就绪。设置写入数为 0，经典音频链路保持已连接。其他型号与 BLE 不同地址端点的跨协议绑定未验证。

最终发布版正常启动后，真实 EDGE 主页可见“已连接”、单个 40% 电量以及降噪 / 通透 / 关闭入口；UI Automation 确认无左右耳标记，控制页可见增益与双设备入口，未点击任何设置写入控件。截图为 `artifacts/verification/windows-live-edge-home-fixed.png`。同一发布版的隔离 OPPO 页面仍显示左 / 右 / 盒三组电量，见 `artifacts/verification/windows-published-oppo/Home-0.png`；该 OPPO 检查不是真机验证。

以下 Probe 命令示例主要面向 OPPO。其他品牌的 `monitor` 可用于观察统一设备运行时，不能把 OPPO 专用写入命令直接用于其他品牌。

### AKG N5 / N9 Hybrid 停留在 AudioConnected

2026-10-24 的 N5 Hybrid 诊断包记录 `app=1.4.0.0`、`model=akg-n5hybrid`、`connection=AudioConnected`，电量与能力全部未知，且没有控制连接日志。1.4.0 中 N5 / N9 Hybrid 使用 `AkgArtworkProfile`，只识别型号和展示素材，不实现 `IControllableProfile`，因此不会启动控制连接；反复重连也不会改变这一点。

1.5.0 已将两款型号接入 `AkgHybridProfile` 和 Airoha RACE 控制器。遇到上述旧版诊断时，先升级至 1.5.0 或更新版本，在 Windows 蓝牙设置中连接耳机后再测试。降噪入口须收到有效 ANC 回包才开放；无需开启实验性功能。

`HeadsetAdapterTests.BuiltInAkgHybridAdvancesFromAudioConnectedToControlReady` 覆盖实际内置型号注册、服务 UUID 传递、生产 supervisor、RACE 初始化与电量 / 降噪状态读取，并确认初始化不写 ANC。它使用模拟通道，不能证明 Windows SDP、真实 RFCOMM 连接或特定固件兼容性。升级后仍失败时，请重新导出诊断包，依据控制通道日志继续定位。

## 2. 用 EncoDeck.Probe 验证真实耳机

先在 Windows 蓝牙设置中连接耳机，然后：

```powershell
$probe = "tools\EncoDeck.Probe\bin\Debug\net10.0-windows10.0.26100.0\EncoDeck.Probe.exe"

& $probe devices                         # 已配对设备（地址脱敏）
& $probe services --device "Enco X4"     # SDP：应出现 0000079A-… "ELNK"
& $probe query --device "Enco X4" --eq-list --report x4-query.json   # 只读查询
& $probe watch --device "Enco X4" --seconds 120                      # 订阅事件 + 轮询，只打印变化
& $probe monitor --seconds 30            # 运行与应用完全相同的设备运行时
```

### 预期输出（Enco X4）

| 步骤 | 预期 |
|---|---|
| `services` | `0000079a-d102-11e1-9b23-00025b00a5a5 <-- OPPO control candidate`，`name: ELNK; rfcomm channel: 15` |
| `query` | 所有项 `Success`；`product id 0x06EC10`；`battery left=…% …`；`noise-control bitmap …` |
| `watch` | 在手机或耳机上切换降噪时出现 `EVENT DeviceEvent 03 01 01 …` |
| `monitor` | 状态依次 `AudioConnected → ControlConnecting → ControlReady`，并带电量/降噪/固件 |

### 写入验证（会改变耳机设置，会自动回读并恢复）

```powershell
& $probe set-anc --device "Enco X4" --index 1,2,0,4 --confirm-write
& $probe set-eq  --device "Enco X4" --preset 7,2 --confirm-write
```

只接受该型号已记录的索引；不加 `--confirm-write` 会被拒绝。`get --cmd 0x01NN` 只允许查询组命令。

### 需要反馈的字段

提交兼容性报告时请包含：型号、产品 ID（`query` 输出）、固件（`query` 中 firmware）、Windows 版本、蓝牙适配器、`services` 输出，以及 `--report` 生成的 JSON（已脱敏：不含蓝牙地址和其他设备名称）。

## 3. 手动 UI 检查清单

| 项目 | 方法 | 状态 |
|---|---|---|
| 主窗口各页面渲染、浅/深色 | 启动应用，切换主题 | 待手测 |
| 托盘左键面板：贴近托盘、不越界、再次点击关闭、点击外部关闭 | 多显示器 / 任务栏四边 | 待手测 |
| 托盘右键菜单：降噪切换、弹窗开关、退出 | | 待手测 |
| 连接弹窗：动画、悬停暂停、点击打开、减少动态效果 | 关闭再打开耳机盒 | 待手测 |
| 全屏游戏时弹窗被抑制 | 全屏应用中连接耳机 | 待手测 |
| DPI 100%/150%/200% | 显示设置 | 待手测 |
| 讲述人朗读电量/连接状态 | Win+Ctrl+Enter | 待手测 |
| 睡眠唤醒后自动恢复 | 睡眠 1 分钟后唤醒 | 待手测 |

## 4. 性能测量方法

```powershell
$p = Start-Process EncoDeck.exe -ArgumentList '--background' -PassThru
Start-Sleep 150; $c0 = (Get-Process -Id $p.Id).TotalProcessorTime
Start-Sleep 180; $c1 = (Get-Process -Id $p.Id).TotalProcessorTime
($c1 - $c0).TotalMilliseconds / 1800   # % of one core
Start-Process EncoDeck.exe -ArgumentList '--exit'
```

实测结果记录在 [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md)。

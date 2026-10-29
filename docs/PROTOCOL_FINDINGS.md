# 协议发现记录（PROTOCOL_FINDINGS）

本文档记录 EncoDeck 使用的 OPPO / 一加耳机私有控制协议的全部已知事实，以及每条事实的**证据等级**。实现代码以本文为准；新增型号或命令时请同步更新本文与 [COMPATIBILITY.md](COMPATIBILITY.md)。

本文主要覆盖 OPPO 协议；其他品牌的实现与范围见 [设备兼容性](COMPATIBILITY.md)。Sony MDR、AKG BES、Xiaomi RCSP 和 HUAWEI MBB 使用各自协议，不能复用下文的 OPPO 指令。

## 证据等级

| 标记 | 含义 |
|---|---|
| **[X4 实测]** | 用 `EncoDeck.Probe` 在真实 OPPO Enco X4 上发送/接收并确认（2026-10-20，固件 左 116 / 右 116 / 盒 100） |
| **[贡献者实测]** | 贡献者在 Windows 真机上用 EncoDeck / `EncoDeck.Probe` 验证，条目注明型号与固件 |
| **[协议实现]** | 按已知协议格式实现并有单元测试覆盖，尚未在真机上验证 |
| **[社区]** | 来自参考项目（OppoPodsWindows / OppoPodsManager / OppoPods；华为 / 荣耀见 §11，小米 / Redmi 见 §12，vivo / iQOO 见 §13，漫步者见 §14，索尼 / AKG 见 §15，USB 2.4G 接收器见 §16）的源码或说明 |
| **[推测]** | 由以上证据推断，尚未验证 |

> 隐私：耳机会通过事件上报**其他配对设备的蓝牙地址和名称**（见 §5.7）。本文中的抓包均已删除或替换这类数据，EncoDeck 的日志和诊断报告也会遮盖它们。

---

## 1. 传输层

| 事实 | 证据 |
|---|---|
| 控制通道是经典蓝牙 RFCOMM（SPP 风格） | [X4 实测] [社区] |
| Enco X4 的 SDP 中有 `0000079A-D102-11E1-9B23-00025B00A5A5`，服务名 `ELNK`，RFCOMM 通道 15 | **[X4 实测]** |
| Enco X4 的 SDP 中**没有** `00001107-D102-11E1-9B23-00025B00A5A5` | **[X4 实测]** |
| X4 另有 `0000079F-D102-11E1-9B24-00025B00A5A5`（“speech interaction”，通道 14），是翻译/语音通道，EncoDeck 不使用 | [X4 实测] |
| 每个型号只用一个 UUID（来自型号配置），缺省时用 `00001107-…` | [协议实现] |
| LE Audio 时改走 GATT：服务 `0000079A-…`，写特征 `0100079A-…`（Write/WriteWithoutResponse），通知特征 `0200079A-…`，帧格式相同，通知按字节流拼接 | **[X4 实测]**（传统链路断开、LE 已绑定且已连接时使用，见 LE_AUDIO.md） |
| Windows 上用 `RfcommDeviceService` + `StreamSocket`，保护级别 `BluetoothEncryptionAllowNullAuthentication`，未打包应用无需声明 capability | [X4 实测] [社区] |
| 耳机必须先由 Windows 建立音频连接（ACL）；EncoDeck 不会主动呼叫未连接的耳机 | 设计决定 |

## 2. 链路层帧

```
+------+------------------+------+------+-------+--------------------+
| 0xAA | LEN (1–2 字节)   | CTRL | 0x00 | [FSN] | 应用层数据块       |
+------+------------------+------+------+-------+--------------------+
```

| 字段 | 说明 | 证据 |
|---|---|---|
| `0xAA` | 帧起始 | [X4 实测] |
| `LEN` | 小端 base-128 变长整数（每字节 7 位，bit7=还有后续），计数 LEN 之后的所有字节；>127 时占 2 字节 | 1 字节形式 [X4 实测]；2 字节形式 [协议实现] |
| `CTRL` | 低 2 位：0 单帧，1 首分片，2 中间分片，3 末分片 | 0 [X4 实测]；分片 [协议实现] |
| `0x00` | 保留，发送恒为 0 | [X4 实测] |
| `FSN` | 分片序号，仅 CTRL≠0 时存在 | [协议实现] |
| 校验 | **无**校验和/CRC，无加密 | [X4 实测] |
| 最大帧长 | SPP 660 字节（GATT 500） | [协议实现] |

EncoDeck 的解码器（`OppoFrameDecoder`）支持半包、粘包、分片重组；遇到非法头部逐字节重新同步，并用应用层的 `PLEN` 交叉校验 `LEN`，防止负载中出现的 `0xAA` 吞掉后续真帧。

## 3. 应用层包

```
CMD (u16 小端) | SEQ (u8) | PLEN (u16 小端) | payload
```

| 事实 | 证据 |
|---|---|
| CMD 高字节是分组：`01` 查询，`02` 通知，`03` OTA，`04` 设置，`05` 耳机发起的请求，`0F` 调试 | 01/02/04/05 [X4 实测]；03/0F [协议实现] |
| 响应 = 请求 CMD \| `0x8000`，**回显请求的 SEQ** | **[X4 实测]**（每个查询都回显） |
| 响应 payload 第 1 字节是状态，`00` = 成功 | **[X4 实测]** |
| 耳机主动推送（`0x0204`、`0x05xx`）使用耳机自己的递增 SEQ | [X4 实测] |
| EncoDeck 请求超时 3 s | 设计决定 |
| 部分命令习惯用固定 SEQ：电池查询/设置类 `F0`，功能状态查询 `00` | [社区]（X4 对两种都正常应答 [X4 实测]） |

示例（X4 实测）：`AA 07 00 00 06 01 F0 00 00` 查询电量 → `AA 0F 00 00 06 81 F0 08 00 00 03 01 C6 02 C6 03 46`。

## 4. 会话初始化

EncoDeck 的顺序（全部在 X4 上验证）：

1. `0x0100` 握手 → `0x8100 [00][能力位图…]`。X4 的位图：`FF 77 5A EE EE 8F A6 5F 17 00 …`。**[X4 实测]**
   位图第 *i* 位允许一组命令；EncoDeck 不发送未被允许的命令（`OppoCapabilityBitmap`）。
2. `0x0103` 产品 ID → X4 为 `0x06EC10`（payload `00 10 EC 06`）。**[X4 实测]**
3. `0x0200` 查询可订阅事件 → X4 回 `0x8200 [00][09] 01 02 03 04 08 0B F1 F2 F3`（Enco X3 据社区记录回 `0x8203`）。**[X4 实测]**
4. `0x0205 [n][事件…]` 订阅。EncoDeck **只订阅自己处理的事件**（01/02/03），因为 `F2`、`F4` 需要主机回复。**[X4 实测]**
5. 查询固件、电量、佩戴、降噪、EQ、功能开关。

没有认证、密钥交换或心跳。**[X4 实测]**

连接后 X4 会立即推送 `0x0500`（时间请求）、`0x051C`（请求主机能力）和事件 `06`（设备列表）。**[X4 实测]** EncoDeck 以 `0x8500 [00][unix 秒 u32 LE]` 应答时间请求，并应答能力请求（见 `OppoDeviceController`）。

## 5. 命令与数据格式

### 5.1 电量 `0x0106` / 事件 `01`

`[状态][n]{组件, 值}×n`，组件 1=左，2=右，3=盒；值 bit7=充电，bit0–6=百分比。**[X4 实测]**

- X4 在盒中：`00 03 01 C6 02 C6 03 46` → 左 70% 充电、右 70% 充电、盒 70%。
- 耳机取出后只上报左右：`01 02 01 5A 02 5A`。**充电盒电量此时不再上报**，EncoDeck 保留旧值并标注“上次读取 HH:mm”。
- 电量变化会主动推送事件 `01`（订阅后）。

### 5.2 佩戴/位置 `0x0109` / 事件 `02`

`[n]{组件, 标志}`；bit0=1 已出盒，bit1=入耳，bit2=0 表示盒盖关闭。**[X4 实测]**（位含义 [协议实现]）
示例：取出→`01 05`，戴上→`01 07`，盒子开盖→`03 04`。

### 5.3 降噪 `0x010C` / `0x0404` / 事件 `03`

- 查询：`0x010C [子类型][1]`，响应 `[00][子类型][类型][位图 LE…]`。
  - 子类型 1：当前模式（位图中对应 protocolIndex 的位为 1）**[X4 实测]**
  - 子类型 2：长按循环包含的模式，X4 = `0x0006`（降噪+通透）**[X4 实测]**
  - 子类型 3：支持的模式，X4 回全 0（不使用）**[X4 实测]**
  - 子类型 4：智能降噪当前档位 **[X4 实测]**（智能模式下会周期性推送）
- 设置：`0x0404 [01][01][掩码…]`，掩码为 protocolIndex 的单个位、最少字节小端；成功回 `0x8404 [00]`，随后推送事件 `03 01 01 …`。**[X4 实测]**
- 耳机触控或手机切换时也会推送事件 `03`。**[X4 实测]**
- 父级索引（只选模式、由耳机决定档位）与子级索引（具体档位）都可以设置；耳机始终**上报子级索引**。**[X4 实测]**

**Enco X4（06EC10）protocolIndex 表**（模式树来自型号配置；“X4 写入”列为 EncoDeck 实测）：

| 索引 | 层级 | 含义 | X4 上报 | X4 写入+回读 |
|---|---|---|---|---|
| 0 | 顶层 | 关闭 | — | ✅（回读 3） |
| 1 | 顶层 | 降噪 | — | ✅（回读当前档位，如 7） |
| 2 | 顶层 | 通透 | — | ✅（回读 8） |
| 3 | 关闭下 | 关闭 | ✅ | ✅ |
| 4 | 降噪下 | 深度 | ✅ | ✅ |
| 5 | 降噪下 | 中度 | ✅ | 未测 |
| 6 | 降噪下 | 轻度 | ✅ | ✅ |
| 7 | 降噪下 | 智能切换 | ✅ | 未直接测（经父级 1 得到） |
| 8 | 通透下 | 通透 | ✅ | 未直接测（经父级 2 得到） |
| 11 | 顶层 | 自适应 | — | 未测 |
| 12 | 自适应下 | 更少噪音 | — | 未测 |
| 13 | 自适应下 | 标准 | ✅ | ✅ |
| 14 | 自适应下 | 更多噪音 | — | 未测 |

索引 ≥ 8 需要 2 字节掩码，例如 13 → `01 01 00 20`。

### 5.4 EQ `0x010F` / `0x0406` / 推送 `0x0504`；自定义 EQ `0x0122` / `0x0418`

- 当前预设：`0x010F` → `[00][预设]`；X4 手机端切换时推送 `0x0504 [预设]`。**[X4 实测]**
- 设置：`0x0406 [预设]`。**[协议实现]**，X4 未写入测试。
- X4 预设（型号配置）：0 丹拿原声、7 丹拿高解析、2 纯享人声、1 清亮高音、3 澎湃低音；其中 0/2/7 在 X4 上观测到。
- 列表：`0x0122 [01 05]` → `[00][n]{选中, 最小, 最大, id, 名称长度, UTF-8 名称, 频段数, (频率 u16, 增益 s8)×}`。X4 实测：自定义预设 “自定义1”，id 4，10 个频段 31/62/125/250/500/1k/2k/4k/8k/16k Hz，−6…+6 dB。**[X4 实测]**
- 写入：`0x0418 [动作 1 新增/2 更新/3 删除][最小][最大][id][名称…][频段…]`。**[协议实现]**，见 §5.8。

### 5.5 功能开关 `0x010D` / `0x0403` / 推送 `0x0503`

- 查询：`0x010D [n][id…]` → `[00][n]{id, 值}`；设置：`0x0403 [id][0/1]`。
- X4 实测应答：`05=01 04=01 0B=01 11=01 13=01 18=00 06=01 27=01 28=00`。**[X4 实测]**
- id 含义 [协议实现]：4 佩戴检测/自动暂停，6 游戏（低延迟），17(0x11) 双设备连接，27(0x1B) 空间音效 V1，39/40(0x27/0x28) 游戏音效/游戏模式主开关，59(0x3B) 头部追踪；5 含义未知。
- X4 会话启动时的完整 24 项查询实测返回：游戏模式 40=0、低延迟 6=1、佩戴检测 4=1、双设备连接 17=1、入睡暂停 58=1、Swift Pair 55=1、快捷语音 25=0、头部动作 59=0、BassWave 29=1、Hi-Res 24=0。**[X4 实测]**
- 写入路径 `0x0403 [id][00/01]` + 回读 `0x010D` 有单元测试覆盖；尚未对真机写入。

### 5.6 固件 `0x0105`

`[00][n] "组件,类型,版本,…"`（UTF-8 CSV 三元组）。X4：`1,2,116,2,2,116,3,2,100` → 左 116、右 116、盒 100。**[X4 实测]**

### 5.7 设备列表 事件 `06` / `0x0112`（敏感）

`[n]{MAC[6] 小端, 条目长度, 状态, 标志, 名称长度, 名称, …}`；标志 bit0=当前主机，bit1=主音频设备，bit2=正在播放，bit3–5=设备类型。**[X4 实测]**（含 PC、手机、手表）
该数据包含其他设备的地址和名称，EncoDeck 仅在内存中解析，日志/报告一律遮盖。

### 5.8 功能命令

所有写入流程：发送 → `0x84xx [st]` 确认 → 回读。确认状态 `0x0E` = 需要佩戴耳机，`0x0F`/`0x09` = 耳机正忙或被其他设备占用，`0x10` = 电量过低。

| 命令 | 用途 | 状态 |
|---|---|---|
| `0x0108 02 03 01` / `0x0401 01 {dev,btn,act,func}` | 触控映射读取/写入 | 读取 [X4 实测]；写入 [协议实现]。**X4 按左右耳分别上报通话手势**（`1/6/x`、`2/6/x`），尽管写入时用 dev=4；匹配规则为“当前耳 或 dev=4” |
| `0x010C 02 03/04` / `0x0404 02 03/04 <mask>` | 长捏噪声控制循环（每只耳机） | 读取 [X4 实测]（左右均为 `0x0006`）；写入 [协议实现] |
| `0x012A` / `0x0422 [0/1/2]` | 空间音频（关/固定/头部跟踪） | 读取 [X4 实测]；写入 [协议实现]；开启时关闭 Hi-Res（功能 24） |
| `0x012B` / `0x0423 [type][01]` | 游戏音效强化（3 射击类 / 0 关闭） | 读取 [X4 实测]（当前 3，支持 `[3,0]`）；写入 [协议实现] |
| `0x0130` / `0x0427 [1..10]` | 耳机提示音音量 | 读取 [X4 实测]（6）；写入 [协议实现] |
| `0x0124` / `0x041B [min][max][cur]` | BassWave 档位（有符号） | 读取 [X4 实测]（−5..5，当前 0）；写入 [协议实现] |
| `0x0134` / `0x0431 [0/1]` | 点头/摇头接听的对应关系 | 读取 [X4 实测]；写入 [协议实现] |
| `0x0136` / `0x0433 [0/1]` | LE/BR 开关（“游戏低延迟 + LE Audio”）：回复 `[st][bits]`，bit0 开、bit1 owner、bit2 reset；能力位 64 | **[X4 实测]**（`00 02` → 写 1 后 `00 03`）。有回复即说明固件支持 LE Audio；Windows 上使用 LE Audio **不需要**打开它。打开时经 LE 控制通道写 `0x0404 01 01 …`（切换降噪）与 `0x0433 00` 均回状态 `01`，`0x0404 02 …`（降噪循环）、EQ、低音仍成功；关闭时 LE 下切换降噪成功，但耳机反复回落到传统蓝牙 **[X4 实测]**。切换入口写为打开 |
| `0x0403 [1F][0/1]` | 功能开关 0x1F：告诉耳机主机的音频传输是 LE（1）还是传统蓝牙（0）；不在功能状态列表中，也不受能力位图限制 | [协议实现]；没有 LE/BR 开关（能力位 64）的固件（如 Enco X3）由它把本机的 LE 绑定同步给另一只耳机。EncoDeck 在 LE 控制通道建立后对这类耳机写 `1`，有 `0x0433` 的 X4 不写 |
| `0x0114` | 耳机报告的 A2DP 编码：回复 `[st][codec]`，1 SBC、2 AAC、3 LDAC、4 aptX、5 aptX HD、6 aptX Adaptive、7/8 LHDC | 映射 [协议实现]；LE Audio 下回复 `01 01`（无经典编码）**[X4 实测]**；EncoDeck 只读并在连接状态旁显示。回复的是耳机设置的高清编码，不一定是当前链路协商的编码 [推测]：Enco Air5s 连接 Windows 时回复 LHDC。Windows 自带的蓝牙音频没有 LDAC，LHDC 需要 LHDC-Win 服务，因此 EncoDeck 不显示 LDAC，LHDC 只在该服务启用时显示 |
| `0x0424 [type][value]` | LE Audio 相关动作，不是模式切换 | [推测]；不使用 |
| `0x0122` / `0x0418 [act][min][max][id][nl][name][n]{f,g}` | 自定义 EQ（1 添加，2 选择/修改，3 删除） | 读取 [X4 实测]（1 组，id 4）；写入 [协议实现]，“添加”字节有单元测试覆盖 |
| `0x0112` / `0x0429 01/02/03 <MAC>` | 双设备连接：连接/断开/移除；MAC 按显示顺序（列表中为反序） | 读取 [X4 实测]（3 台：本机电脑、手机、手表）；写入 [协议实现] |
| `0x0132 02` / `0x0429 04 01 <MAC>`、`04 00` | 优先设备 / 自动选择 | 读取 [X4 实测]（自动）；写入 [协议实现] |
| `0x0400 [1/0]` | 查找耳机 | [协议实现]；50 秒或双耳入盒后自动停止 |
| `0x0405 [1/0]` + 事件 `04` | 贴合度检测（1 良好，0 一般，6 较差） | [协议实现]；15 秒超时 |
| `0x0117` | 序列号/型号（敏感） | 不使用 |
| `0x0300…` | OTA | **永不实现**（第一版明确不做固件刷写） |

## 6. BLE 广播（预留）

厂商数据（公司 ID `0x079A` OPPO / `0x072F` OnePlus）中预计包含：`pid u24 LE`、标志 `u16`、固定 `01 98`、可选状态/盒盖/电量（3 字节）/颜色。**[推测]**
EncoDeck 尚未实现，也未在 X4 上抓取广播；开盒弹窗需先用实测广播验证。

## 7. 安全原则

- Probe 的 `get` 只允许 `0x01xx` 查询组；写命令必须加 `--confirm-write`，且只接受该型号已记录的值，写后回读并恢复原状态。
- 运行时遵守能力位图；产品 ID 不在内置型号列表中时使用只读通用配置，不发送任何型号专属写命令。
- 不扫描未知命令空间，不批量写入，不实现 OTA。

## 8. 型号配置的解读（所有型号）

Free4 标准版 / 丹拿版的支持范围见 [设备兼容性](COMPATIBILITY.md#enco-free4-标准版--丹拿版)。智能音量调节为功能开关 28（写入 `0x0403 [1C 00/01]`，回读 `0x010D [01 1C]`）；功能开关 ID 与命令能力位图是不同概念，不要把功能 28 当作位图第 28 位。

### 双设备管理

双设备连接开关（功能 17）、读取设备列表（命令能力位 29）与远程管理 `0x0429`（命令能力位 59）是独立能力。界面按握手返回的能力决定是否提供连接、断开、移除和优先设备操作；操作后必须回读设备列表并确认目标设备状态，不能仅凭 ACK 或任意列表通知判定成功。显式查询返回空列表时清除旧记录。

### 型号配置字段

型号配置决定每个型号提供哪些设置。EncoDeck 的解读规则见 `OppoCatalog.cs`（[协议实现]，并用 X4 交叉验证：生成结果与 X4 实测定义逐项一致）：

| 配置 | 解读 |
|---|---|
| `uuid` | RFCOMM 服务 UUID；缺省 `00001107-…` |
| `noiseReductionMode` | 降噪模式树。modeType：1 关闭、2 通透、5 降噪（子级 7 智能、4 深度、8 中度、3 轻度）、10 自适应（子级 11 更少、12 标准、13 更多噪音）；平铺树中 3 弱降噪、4 强降噪、7 智能降噪为顶层按钮，6 聆听（`decideByEarDevice`）。点顶层按钮发送该节点自己的 protocolIndex，点子级发送子级索引；上报按 protocolIndex 匹配父级或子级 |
| `equalizerMode` / `…Compat` / `…ByVersion` | 依次拼接后按 `order` 稳定排序；`minFirmVersion` 与左右耳运行版本的较小值比较。`equalizer`（1–4）只影响标题，不影响协议；modeType 1–3 在 `equalizer==2`、Enco R、Enco Air2 上用另一组名称 |
| `customEqualizer`、`customEqFrequency`、`customEqMax` | 自定义 EQ；缺省频段 62/250/1k/4k/8k/16k Hz，增益固定 −6…+6，最多 3 组；还要求耳机支持 `0x0418` |
| `control` / `callControl` | 手势行。action → 线上 action：11/19/20/27→4，16/28/32→1，17/26/29/33/36→2，18/30/34→3，22/31/35→6，其余原样；action ≥ 28 用 button 6。`support` 是功能位图（bit 9 无、7 降噪开关、2 播放暂停、5 上一首、6 下一首、0 语音助手、3/4 音量加减、10 音量调节、11 歌曲切换、12 设备切换、13 游戏模式、15 收藏、16 耳机专注、18 拒接、19 接听），手机专属功能（AI 摘要、翻译、Gemini 等）不提供；`showEar==1` 时两耳共用（devType 4）；`zenMode==3` 时三击/三捏不提供耳机专注 |
| action 11/20/25/27 | 长按切换降噪模式。`longPressType≠0` 时还可选长按功能（无/噪声控制/语音助手/游戏模式），循环按耳机分别设置（`0x0404 02 03/04`）；否则两耳共用（`0x0404 02 01`、`0x010C 02 01`）。可选模式为顶层节点，按 modeType 顺序 `[4,3,5,7,10,2,6,1]`（`noiseReductionUIVersion==2`）或 `[4,3,5,7,10,1,2,6]`；最少选择数为 `minSelectCount`，缺省时一加或有长按功能为 2，否则 1 |
| 会话开始的 `0x010D` | 按配置逐项拼出（见 `OppoCatalog.StatusIds`），X4 结果与实测的 24 项一致 |
| `gameMode` / `gameSoundList` | 有游戏音效：主开关 40、低延迟 6、音效 `0x0423`；否则单一开关 6，耳机支持 `0x0423` 时写 40 |
| `wearDetection` 1/2 → 功能 4；`multiDevicesConnect` 1/2 → 17；`sleepDetection` → 58；`swiftPair` → 55；`voiceCommand` 1/2 → 25；`headMotion` → 59；`bassEngineSupport` → 29 + `0x041B`；`highToneQuality` 1/2 → 24 | 功能开关 |
| `spatialTypes` | 空间音频按钮（0 关、1 固定/打开、2 头部跟踪）。耳机支持 `0x0422` 用 V2，否则用功能 27（只有开/关）；`spatialHiResMutex` 时开启空间音频后关闭 Hi-Res |
| `heyFindDevice` / `findDevice`、`fitDetection`、`promptVolume`（`promptVolumeRange`） | 查找耳机、贴合度检测、提示音音量（范围） |

## 9. 水月雨 GAIA（独立于 OPPO）

来源：MiuixMoondrop `2f78f6740b2f30d1c28d75ba05badb98b2eeb687` 的 GAIA 实现，以及 [moondrop-control `1ec9c77`](https://github.com/FEAKEuser/moondrop-control/tree/1ec9c77a2387c698d77fb2bf25d2c62ec7ae3d5e) 的 EDGE RFCOMM 协议记录。EDGE 固件 1.3.0 已在 Windows 真机上验证回包与控制链路；其他型号仍以参考源码和模拟会话为证据。

- PDU：`vendor=001D`（BE）、`commandValue=(feature<<9)|(type<<7)|command`（BE）、payload；请求 / 通知 / 响应的 type 为 0 / 1 / 2。等待器同时匹配 feature、command 和响应类型，通知不能冒充查询应答。
- BLE：服务 `00001100-d102-11e1-9b23-00025b00a5a5`，command / response / data 对应 `1101` / `1102` / `1103`；每次 characteristic notification 保留一个 PDU 边界。
- RFCOMM：SPP UUID `00001101-0000-1000-8000-00805f9b34fb`。EDGE 实测 Windows 公共 Winsock 直接打开已选经典地址的通道 1，发送 `FF040000001D0005`，收到 `FF040005001D0105312E332E30`（版本 1.3.0）；无 SDP 服务可见时仍可连接，不扫描其他通道。EDGE 先查询应用版本，其他型号仍先探测带帧 / 裸 CSR API。带帧 length 只计算 payload；裸可变长度消息以 40ms 静默结束，任意粘包 / payload 内长停顿仍未验证且不可靠。
- 功能：BASIC 0 的命令 1 / 2 分页，返回 `[more][feature,version]...`；兼容大端 32 位能力位图。电量 feature 13 命令 0 查类型，命令 1 携类型列表查询 `[type,level]...`；type 0 为整机，1 / 2 / 3 为左 / 右 / 盒，255 为未知，不挪动其他组件。
- ANC：AudioCuration 8 的读 / 写命令 3 / 4；EDGE 写 `[1,2,4]`、读索引 `[0,1,2]`，梦回2 / 太空漫游2 写 `[1,2,4,3]`、读 `[0,1,2,3]`。PUDDING 使用 feature 32 命令 3 / 4，UI 关闭 / 降噪 / 通透 / 抗风 / 自适应对应设备值 `[0,4,2,3,1]`。
- EDGE 实际 ANC 回包为 `FF040004001D110300010000`：四字节 payload `[mode,01,00,00]`，取首字节模式；不能要求恰好一个字节。Windows 的 `Socket.ConnectAsync` 经 ConnectEx 返回 10022，普通 `Socket.Connect` 成功；生产通道在线程池连接，并以 socket 释放处理取消 / 10 秒超时。
- 提示音 feature 14 的读 / 写命令 1 / 2 使用完整 `[enabled,volume,index]`，volume 0–100；双设备 feature 20 命令 1 / 2，与 LHDC 开启时互斥。[协议实现] feature 16 的 LC3 / LDAC / LHDC GET / SET 分别为 1 / 3、2 / 4、5 / 6；feature 17 入耳检测、27 动态低音、30 左右声道交换均使用 GET / SET 1 / 2。有效单布尔回读才开放对应控制，编解码器按各个 GET 应答独立发现；全部为实验性功能，尚无新增设置的真机验证。
- 空间音频 feature 18 的总开关 GET / SET 1 / 2 与头部跟踪 GET / SET 3 / 4 独立读写。三态 UI 投影为关闭（总开关关）、固定（总开关开、头部跟踪关）和头部跟踪（两者开）；写入分别确认两个布尔值，不能只检查 SET 应答。需实验性启用，尚无新增设置的真机验证。
- 增益 feature 15 GET / SET 1 / 2：UI 0 / 1 / 2 为低 / 中 / 高；PUDDING、GA2、ST2、GOLDEN AGES、ST2 ULTRA 设备映射为 `[2,1,0]`，其余已知且有映射的目录型号为 `[0,1,2]`，BLOCK 和通用型号无映射。LED feature 19 GET / SET 1 / 2。新增设置受运行时 feature 门控；界面只在已读状态后显示，提示音 / 手势写入前读取完整配置，目录本身不是支持证据。
- TOUCHV2 feature 22 GET / SET 2 / 3 使用五字节，每字节高 / 低半字节分别保留双耳动作；单击 / 双击 / 三击 / 长按 1 秒 / 长按 3 秒，同耳两种长按互斥。动作 4 / 5 的音量含义仍为推断，仅显式实验性启用；8–15 仅显示与保留，不能主动选择写入。
- 会话每 30 秒刷新状态；所有写入序列化并查询确认。未读到配置时不拼造提示音其他字段，不将推断功能默认开放。

### 9.1 关联项目核查

- [FxxkMoondrop `c68a7a6`](https://github.com/bqj6666/FxxkMoondrop/tree/c68a7a6dbc8ff27089428f7395eac895b970116b) 的 [`EqActivity.kt`](https://github.com/bqj6666/FxxkMoondrop/blob/c68a7a6dbc8ff27089428f7395eac895b970116b/src/com/fxxkmoondrop/secret/EqActivity.kt) 已有预设 EQ 选择、回包处理及只读 PEQ 页面；它调用 `BleSourceSwitchClient`，使用独立的 **9ECA** 服务，不是 GAIA feature 5。PEQ 编辑 / 提交未做，增益单位仍标为推断；[`ADAPTATION.md`](https://github.com/bqj6666/FxxkMoondrop/blob/c68a7a6dbc8ff27089428f7395eac895b970116b/ADAPTATION.md) 明确写明 9ECA 客户端未经真机验证。因此没有把此路径并入当前 GAIA 支持声明。
- 同一来源的 `GaiaPacketHandler.kt` 对固件版本和 ANC 循环命令 41 / 42 只记日志，不构成可移植的完整用户功能；GA2 的命令 41 回包还有不稳定记录。系统低延迟属于 HyperOS 蓝牙设置，不是已确认的跨平台 GAIA 写入。
- [PuddingPods `f527afe`](https://github.com/lingbai-rong/PuddingPods/tree/f527afe503749d7d2ceadbf5529c0639d7de1f7f) 的公开树只有文档 / 构建包装文件，没有应用源码可追踪完整读写路径。参考项目致谢的 `MegaSuite/moondrop-link-desktop` 在本次核查时不可公开访问，无法核实实现或许可证。
- EDGE 应用版本已通过真实 BASIC 命令 5 回包读取；详细双设备管理、详细固件信息、佩戴 / 充电状态、查找耳机、贴合度检测、硬件降噪循环和 OTA 仍不受支持。型号档案和 feature 常量不替代实际回包证据。

### 9.2 Music Processing（feature 5）EQ 与新增型号

来源：[moondrop-control `1ec9c77`](https://github.com/FEAKEuser/moondrop-control/tree/1ec9c77a2387c698d77fb2bf25d2c62ec7ae3d5e)（EDGE 固件 1.4.0、NEKOCAKE 固件 1.0.0 实测）、[FxxkMoondrop `5f4342e`](https://github.com/bqj6666/FxxkMoondrop/tree/5f4342ebcd3cbd45975a1b593967b80493972887)（`MoondropSpp.kt` / `MoondropEq.kt`、型号表）、[moondrop-pc `4198d9a`](https://github.com/herikk04/moondrop-pc/tree/4198d9a719d563fa65f6205ef6e7e94b1a67ae4e)（太空漫游2，Windows 10）、[Edge2Pods `ab3f799`](https://github.com/Konakonai/Edge2Pods/tree/ab3f799d2d899d78efdd922bc7201e2fef4994ea)（EDGE 2 固件 1.2.5）、[puddingpods-battery-fix `31a5071`](https://github.com/cvbyisme/puddingpods-battery-fix/tree/31a50716dca3025fc9eb1ed38fcc07b343f5e29a)，并对照 [moondrop-gaia-protocol](https://github.com/Luoning77/moondrop-gaia-protocol) 的公开记录（仅核对事实）。

- **[社区]** feature 5 命令：1 = 预设列表 `[count][id...]`（EDGE / NEKOCAKE / 太空漫游2 均为 `06 00 01 02 03 04 3f`，`0x3f` 为用户 PEQ）；2 = 当前预设 `[id]`；3 = 选择预设 `[id]`（部分固件不应答，以命令 2 回读为准）；4 = 用户 EQ 段数；5 = 读用户曲线 `[起始段][结束段]`；6 = 写用户曲线。RFCOMM 帧中即 `0A 05` 读 / `0A 06` 写，应答命令字为 `0B xx`。
- **[社区]** 用户曲线：`[起始段][结束段][前级增益 i16]` + 每段 `[频率 u16][Q×4096 u16][滤波器类型 u8][增益×60 i16]`，全部大端；EDGE 五段共 39 字节，滤波器类型恒为 0，前级增益 EDGE 恒为 0、太空漫游2 上为非零值（随曲线峰值变化）。写入格式与读回逐字节相同；写入无 ACK，偶尔丢失一次，重写即可。
- **[协议实现]** EncoDeck 只在读到完整预设列表、当前预设及（用户曲线存在时）完整单包曲线后开放 EQ。界面增益以 0.1 dB 显示；未改动的段按原始字节写回，改动的段换算为 ×60；频率、Q、类型和前级增益不改。写后逐字节回读，不一致重写一次，仍不一致返回失败。滤波器类型非 0、段数超过 7 或长度不自洽的曲线视为未知布局，只读不改。增益范围取 −12…+3 dB（社区编辑器范围），耳机已有值超出时放宽到该值，不夹断。
- **[社区]** EDGE、太空漫游2 有真机读写证据，EQ 标为已支持；其他型号即使申报 feature 5 也只作为实验性功能。NEKOCAKE 的用户配置读回 `00 01 00 00 00 00 00 00 00`，不是 PEQ 布局，故只开放预设选择（实验性）。
- **[社区]** EDGE 2：只走 RFCOMM 通道 1；ANC V2（feature 32）命令 3 / 4，0 关闭 / 1 降噪 / 2 通透 / 3 抗风噪 / 4 自适应（5 不是真实模式）；写入无应答。固件会保存并读回任何 0–5 的值，回读一致不代表模式有效，因此仍为实验性。
- **[社区]** NEKOCAKE：RFCOMM 通道 1，能力只有 0 / 1 / 3 / 5 / 6，电量（feature 13）与所有 ANC 命令都无应答。U.C.T.S：识别与左右耳电量已由用户确认，硬件无降噪。ROBIN：公开记录为 EDGE 同款读写映射，社区日志只出现两种状态 **[推测]**。
- **[社区]** PUDDING 带帧长度字段只计 payload，整帧 = 4 字节头 + 4 字节 PDU 头 + length（例：`FF 04 00 06 00 1D 1B 01 01 64 02 64 03 5E`，电量左 / 右 100、盒 94）。EncoDeck 的 RFCOMM 解码本来就按此计算，无需修改。
- 9ECA 私有服务仍未实现；没有可靠证据的型号不切换到 9ECA。

## 10. 森海塞尔 MOMENTUM TW 4（GAIA v3）

2026-10-24 独立实现，命令定义与动作约束按协议实现并有单元测试覆盖。降噪状态机核对了 [TW4 真机抓包记录](https://github.com/sandrolucy/Sennheiser-MOMENTUM-TW4-widget-gaia-protocol)，帧结构参考 [GAIA v3 协议记录](https://github.com/xndbogdan/sennheiser-control/blob/main/PROTOCOL.md)。

- 优先沿用 RFCOMM UUID `a2129ff3-081b-4c45-8afe-469d9c4842ec`；保留现有 GATT 回退。注册 feature `3` 电池、`4` 音频、`12` 通透、`13` ANC。请求/响应/通知/错误 type 位分别为 `0` / `0x100` / `0x80` / `0x180`；帧版本 3/4 均可接收。
- ANC 开关 GET/SET `1A05` / `1A04`、通透开关 GET/SET `1805` / `1804`，状态严格为单字节 0/1。`1803` 是保存的通透等级，不能用它判断通透开关。TW4 实测通知 `1A83` 大于 0 表示通透开、0 表示关；`1885` 也是通透布尔通知。通透变更可能不伴随 ANC 通知，因此独立维护两个状态，显示优先级为通透 → ANC → 关闭。
- ANC 模式 GET `1A01` 是三个 `[mode,state]` 对；mode 1 为抗风噪（0/1/2），2/3 为布尔状态。标准 / 抗风噪分别 SET `1A00 [1,0/1]`，再开启 ANC。切到降噪或关闭先关闭通透；SET 的空 ACK 不修改显示状态，最终回读两个开关与抗风噪模式。中途失败也读取实际状态，避免局部成功后显示旧模式。
- Smart Pause GET/SET `080D` / `080C` 映射佩戴检测；低延迟 GET/SET `0818` / `0817` 映射独立低延迟开关，通知 `0898`。每项分别通过有效回读开放，不由型号名直接宣称可写。
- 触控 MMI GET `1601 [side,pattern]` → `[side,pattern,function]`，SET `1600 [side,pattern,function]` → 空 ACK。wire side 左=1、右=0（与通用 `EarSide` 不同）；pattern 单击/双击/三击/长按=1/2/3/4。每个槽位分别读，写之前重新读，写之后按同一 side/pattern 回读确认；错误侧或错误动作的应答不能完成请求。
- TW4 允许的动作：0 无、1 播放暂停、2 下一首、3 上一首、4/5 音量增减、6 通透切换、7 语音助手、9 ANC 切换、13 触控锁定切换。播放/切歌仅用于单/双/三击，音量仅长按，锁定仅三击/长按；其余可用于所有四种动作。不改通话映射，未知功能值只显示并保留。
- 已通过 TW4 模拟设备读写、拒绝/超时/回读不一致、通知缺失/格式错误、版本 4 分片、动作约束和其他森海型号隔离测试，以及真实 WinUI 截图检查。用户截图固件为 5.19.1；本机无已连接 TW4，未将模拟测试标为真机验证。

### 10.1 头戴型号与 TW4 新增功能

来源：[momentum4-control `17aeea9`](https://github.com/Ariazonaa/momentum4-control/tree/17aeea90ce12ffbb4521df1832b22de0bc08c666)（MOMENTUM 4 固件 3.37.3 / 3.38.3、MOMENTUM 5 固件 6.21.5 抓包）、[momentum-menu `4dbe1c4`](https://github.com/andr3-santos/momentum-menu/tree/4dbe1c445198ea9b706934c90e75bc4a4bc69fe9)、[m4-companion `a112469`](https://github.com/Zhengyang-Liu/m4-companion/tree/a112469044103bf26fe7eea51905a974bbd44f78)、[sennheiser-control `e6d795c`](https://github.com/xndbogdan/sennheiser-control/tree/e6d795c9cefad154ed1bb92e11cd09f15018dd3f)；仅核对事实：[smartcontrol-linux](https://github.com/gabors0/smartcontrol-linux)、[sennheiser-desktop-client](https://github.com/Oein/sennheiser-desktop-client)、[SennheiserControl](https://github.com/Mefgner/SennheiserControl)、[hdb630-control-macos](https://github.com/hatemosphere/hdb630-control-macos)。

- **[社区]** 通知注册 `0x0007 [feature]`：注册后耳机会主动推送该功能的当前值。EncoDeck 按型号注册 3 电池、4 音频、8 用户 EQ、10 已配对设备、12 通透、13 ANC、22 查找。
- **[社区]** 固件 `0x1201` → 三个 u16（如 3.37.3）；HDB 630 用 `0x1202` → 三个字节。型号 `0x1206` 为以 NUL 结尾的 ASCII（如 `M4AEBT Black`）。
- **[社区]** 用户 EQ（固件 3.x 起，2.x 不查询）：`0x1000` → `[段数][最小 i8][最大 i8]...`（MOMENTUM 4 `05 C4 3C 00 00`，即 5 段 −6…+6 dB；MOMENTUM 5 为 8 段）；`0x100B [段]` → `[段][Hz u16]`（MOMENTUM 4：90 / 325 / 1500 / 6500 / 6500 Hz）；`0x1003 [00]` 一次读全部增益（i8，0.1 dB）；`0x1001 [段][增益]` 写一段，随后有整条曲线通知 `0x1082`。声音模式 `0x0804` → `[00][模式]`，`0x0803 [00][模式]` 设置，0 关闭 / 1 用户 EQ / 2 播客；切换异步生效，EncoDeck 短时轮询回读。
- **[协议实现]** EncoDeck 只写改动的段，写后用 `0x1003` 回读整条曲线比对；耳机已有的越界值保留不夹断。未知声音模式只显示原始值。
- **[社区]** ANC 强度：头戴 `0x1A02` / `0x1A03` 为 0–100 等级，与通透等级相互影响；EncoDeck 在有独立状态读数的型号上不把它当作模式，也暂未提供强度调节。ACCENTUM 的 ANC 模式表读回全零且忽略写入。
- **[社区]** 已配对设备（feature 10）：`0x1400` → 数量 u16，`0x1401 [序号]` → `[序号][优先级][已连接][名称\0]`，`0x1407` → 本机序号，`0x1409` → 最大连接数（MOMENTUM 4 为 2），`0x1402 [序号]` 连接（ACK 先于连接建立，约 3 秒后 `0x1404 [序号]` → `[序号][1]`），通知 `0x1484`。`0x1403`（断开）在部分固件上会清除该设备的配对，`0x1405` / `0x1406` 为删除，EncoDeck 永不发送。
- **[协议实现]** 连接只在 MOMENTUM 4 开放：发送前重新读取列表，要求同一序号与名称、名称唯一、非本机、本机仍连接且有空闲连接位；连接后再次读列表确认。
- **[社区]** TW4 查找耳机：feature 22，`0x2C02 [侧]` 开始、`0x2C01 [侧]` 停止（侧 0 右 / 1 左），状态 `0x2C03 [侧]` 以通知 `0x2C83 [响铃][侧]` 回报。EncoDeck 以回报的响铃状态为准，50 秒后自动停止。

## 11. 华为 / 荣耀（独立于 OPPO）

来源：[OpenFreebuds `0b4f14c`](https://github.com/melianmiko/OpenFreebuds/tree/0b4f14c0b500d491f0fb4ea38f2984ec334e3649)（含 issue / PR #56、#95、#116、#128、#133、#138、#142）及其 [vicrodh 分支 `a12ccc5`](https://github.com/vicrodh/OpenFreebuds/tree/a12ccc5a4030cba6ce2b8e9a402209b2e63268f5)、[HuaweiPods `115f080`](https://github.com/Nshpiter/HuaweiPods/tree/115f080100c07fb51174b3238473c512a2c6d3b8)、[EZBuds `4d1c6aa`](https://github.com/CenSher/EZBuds/tree/4d1c6aaddb3b089cfb1b464ac86b65e3fa365ce2)；另核对 Gadgetbridge、fxxkHilife、OpenFreebuds-Android、Nezerkill/honor-earbuds、TeskiOke/HonorEarbuds 公开的测试结果。本节的 **[社区]** 指上述项目在真机上测试或抓包确认；EncoDeck 自己尚未在华为 / 荣耀真机上验证，全部路径都有字节向量和模拟设备测试。

| 事实 | 证据 |
| --- | --- |
| 帧：`5A len_hi len_lo 00 svc cmd TLV… crc_hi crc_lo`。长度 2 字节大端 = TLV 长度 + 3；CRC-16/XMODEM 覆盖 `5A` 至最后一个 TLV 字节。超过 255 字节的应答（FreeBuds Pro 5 设备信息 282 字节）必须读高字节；EncoDeck 旧实现按小端读并要求第二字节为 0，会丢弃长应答，已修正 | [社区]（OpenFreebuds #116、fxxkHilife、EZBuds） |
| 写入应答 `7F 04 00 01 86 A0`（结果码 100000）表示已接受，只是应答而不是状态；其他结果码（如 100003 不支持）为拒绝。EncoDeck 旧实现把任何 `7F` 都当作拒绝，会把正常写入判为失败，已修正 | 应答格式 [社区]；判定 [协议实现] |
| 降噪：读 `2B2A` TLV1 = [档位, 模式]，写 `2B04` TLV1 = [模式, 档位]；档位 `FF` 保持当前档位。FreeBuds Pro 5 的写入可能没有应答，首次回读也可能仍是旧值，EncoDeck 不等待应答并重读确认 | [社区]（OpenFreebuds #133、#142） |
| 降噪档位编号随型号：FreeBuds 5 / 5i / 6 / 6i / 7 / Pro 5 为智慧动态 3、舒适 1、均衡 0、深度 2；Pro 3 / 7i 为智慧动态 1、舒适 0、均衡 2、深度 3；4E 为舒适 1、均衡 0 | [社区]（HuaweiPods）；Pro 3 / 7i 与 OpenFreebuds 的通用标注冲突，采用 HuaweiPods [推测] |
| 通透子模式：2 标准、1 人声增强（6i、Pro 5、FreeLace Pro 2）；Pro 5 的 4（智慧动态通透）只显示不提供。Pro 3 / 7i 的标准通透记录为 `FF`，与 OpenFreebuds 的 2 冲突，未开放人声增强 | [社区] |
| FreeBuds 5i 连接后主动发送 `0A0E` TLV1 = 0，主机须回复 `0A0E` TLV2 = 0（`5A0006000A0E020100FB3F`），否则耳机断开 | [社区]（OpenFreebuds #142、OpenFreebuds-Android） |
| 电量 `0108` / `0127`：TLV2 = [左, 右, 盒]，其中 0 表示该部分未上报（如在关闭的充电盒中），不是 0% | [社区]（OpenFreebuds #142） |
| 佩戴状态 `2B25`：TLV1 左、TLV2 右（1 = 佩戴）；7i、Pro 4 用它代替 `2B03` | [社区]（vicrodh 7i 抓包、OpenFreebuds #138）；Pro 4 的左右对应未核实 [推测] |
| EQ `2B4A`：TLV2 当前预设、TLV3 耳机提供的内置预设、TLV8 自定义预设（每条 36 字节，名称 24 字节）；写 `2B49` TLV1。7i 会静默丢弃名称超过 24 字节的自定义预设写入。自定义 EQ 未实现，选中时只显示原始编号 | [社区] |
| 内置预设编号：通用 1 默认、2 低音、3 高音、9 人声；FreeBuds 6 默认为 0；Pro 3 / 4 与 FreeClip 为 5 / 1 / 2 / 9；FreeBuds 7 / Pro 5 为 5 均衡、9 人声、2 低音、13 电影、15 播客、14 游戏、16 运动；FreeClip 2 / FreeArc 另有 10 运动增强。界面只显示 TLV3 列出的预设 | [社区]；Pro 5 的 2 号 OpenFreebuds 标为高音、HuaweiPods 标为低音，采用后者 [推测] |
| 低时延 `2B6C` 写 TLV1、读 TLV2；佩戴检测开关 `2B10` / `2B11`；双设备开关 `2B2E` 写 / `2B2F` 读 | [社区] |
| 设备列表 `2B31`：一次查询按设备逐帧应答，TLV2 总数、3 序号、4 地址（倒序）、5 状态（0 未连接、9 播放中）、7 优先、8 自动连接、9 名称；`2B36` 通知列表变化。连接 / 断开 `2B33`、优先设备 `2B32` 未实现，列表只读 | [社区]（EZBuds Windows 实测） |
| 功能开关 `2BB4`，TLV1 为功能 ID：24 独立空间音频（0 关、1 头部跟踪、2 固定）、2 自适应音量、11 点头 / 摇头（TLV2 开关，TLV3 点头、TLV4 摇头：0 无、1 接听、2 拒接）。各功能仅在耳机返回有效状态后开放；单耳降噪（5）、掉落提醒（7）未实现 | [社区]（HuaweiPods FreeBuds 7 逐项写入回读、vicrodh 7i）；7i 空间音频编码两个项目记录不一致，以回读一致为准 |
| 防风噪 `2B94` / `2B95` 在 5i 上极性相反（0 开、1 关）；高清音质 / 音质优先 `2BA2` / `2BA3` 在 Windows 11 与部分手机上会导致断音或重连。均未提供 | [社区]（OpenFreebuds #142、#138，Gadgetbridge） |
| HONOR Earbuds 2 SE 可用 FreeBuds 4i 的命令控制（电量、降噪、佩戴检测） | [社区]（OpenFreebuds #56） |
| HONOR CHOICE X5 / X5 Pro：同样的 `5A` 帧；`0108`、`2B2A` 查询不带 TLV；`2B04` TLV1 = [模式]（0 关、1 降噪、2 通透）；状态取 TLV1 的最后一个字节。Linux 上社区工具直接打开 RFCOMM 通道 18 / 12，Windows 工具经 SPP 服务连接 | [社区]（Nezerkill X5 实测、TeskiOke X5 Pro）；EncoDeck 经 SDP 查找 SPP 服务 [协议实现] |

## 12. 小米 / Redmi RCSP（独立于 OPPO）

来源：[BudsLink `ba3160d`](https://github.com/maniacx/BudsLink/tree/ba3160dfce72c192ebbdf2697b0341aa1e78fb56)（含 [PR #110](https://github.com/maniacx/BudsLink/pull/110) 的 REDMI Buds 6 Pro 会话记录）、[redmi-buds-bar（robin-liquidium）`e4337d2`](https://github.com/robin-liquidium/redmi-buds-bar/tree/e4337d2cb9f246c9e57d567524009d0959711825)、[mebuds `7028e23`](https://github.com/paradox-ng/mebuds/tree/7028e23435a449fd28d52fbb9ff2031946744684)、[MiBudsClient `59e0a44`](https://github.com/CesurPolat/MiBudsClient/tree/59e0a4469d80c19022d7da408453adb93b819ad3)、[OpenXia `f2650cf`](https://github.com/thetriu/OpenXia/tree/f2650cfbc62f8ebbd3c6ded9baac4027053c8f60) 与 [android_packages_apps_XiaomiTWS `0927e57`](https://github.com/web1n/android_packages_apps_XiaomiTWS/tree/0927e57cae772ac1016691b0930fb279fff51229)；Gadgetbridge、WinMi-Buds 与 redmi-buds-bar（ChristianVeneko）只用于核对事实。本项目尚未在 Windows 上用小米 / Redmi 耳机实测，下列内容为 **[社区]** 记录加 **[协议实现]**（模拟设备测试）。

- 传输：经典蓝牙 SPP，优先 `0000FD2D-0000-1000-8000-00805F9B34FB`，回退 `00001101-0000-1000-8000-008584D01810`。**[社区]**
- 帧：`FE DC BA | kind | op | len(BE16, =1+payload) | seq | payload | EF`。kind bit 7 = 请求、bit 6 = 需要应答；手机请求 `C4`，应答 `04`。应答在请求放 seq 的位置放**状态字节**，其后才是回显的 seq；状态 0 成功，4 不支持。耳机主动发来的请求（`C0` / `C7` 需应答，`87` 不需要）必须按同一格式回应。**[社区]**
- 鉴权：op `50` 双向各发一次 `01 || random(16)`，对方回 `01 || E(random)`；op `51` 结论 `01 00` 表示通过，回 `01`。E 是以挑战（末字节 XOR 6）生成 17 轮密钥、对固定块 `11 22 33 33 22 11` 循环填充的 16 字节做 8 轮替换 / 混合。旧实现加密的是另一组常量，已按 OpenXia（Redmi Buds 6 Play）和 PR #110（REDMI Buds 6 Pro）的真机向量更正。不应答鉴权的型号直接继续。**[社区]**
- 设备信息 op `02`，掩码 `FF FF FF FF`；记录为 `[len][type][value]`：0 名称、1 固件（两字节、四个半字节）、3 VID/PID（各 BE16）、7 电量 `L R 盒`。电量字节低 7 位为百分比，bit 7 充电中，`FF` 未上报（保留上次读数）。状态推送 op `0E` 记录 0 为电量。名称不在目录时按 VID/PID 识别型号。**[社区]**
- 配置读写：读 op `F3` 载荷为 BE16 id 列表，写 op `F2` / 推送 op `F4` 为 `[len(含 id 两字节)][id BE16][value]`。单值 `FF` 表示不支持。会话开始逐个 id 读取（真机上一个不支持的 id 会让合并读取失败或卡住），回读有效才开放对应功能；写入以重读确认。**[社区]** / **[协议实现]**

| id | 内容 | 说明 |
|---|---|---|
| `0002` | 手势 | `[kind][左][右]` 组；kind 1 双击 / 2 三击 / 3 长按 / 4 单击 / 5 滑动。功能 0 语音助手、1 播放暂停、2 上一首、3 下一首、4 / 5 音量加减、6 降噪切换、8 无、11 音量（滑动）。写入只发一组，另一耳填 `FF` 不变；未知 kind 不显示，未知功能只显示不改写 |
| `0003` | 自动接听 | 0 / 1 |
| `0004` | 双设备连接 | 0 / 1 |
| `0007` | EQ 预设 | 预设 id；10 为自定义曲线 |
| `0009` | 查找耳机 | `[1 响 / 0 停][1 左 / 2 右 / 3 双耳]`，只写 |
| `000B` | 降噪 | `[模式 0 关 / 1 降噪 / 2 通透][强度]`；三档机型降噪 0 均衡 / 1 轻度 / 2 深度，通透 0 标准 / 1 人声 / 2 环境声，20 档机型为 0–19 |
| `000C` | 佩戴状态 | 只读 / 推送；bit 3 左耳在耳、bit 2 右耳在耳、bit 1 / 0 左 / 右在盒 |
| `001D` | 空间音频 | bit 0 开、bit 1–2 音频偏好（写入时保留）、bit 3 头部跟踪 |
| `0025` | 自适应降噪 | 0 / 1；手动选降噪档位前先关闭 |
| `002F` | 低延迟 | 0 / 1 |
| `0037` | 自定义 EQ | `[1][mode][最大 dB][最小 dB][id][名称长度][名称][段数]{频率 BE16, 增益}*`，增益最高位为负号；保存只改增益，频段与范围以耳机回读为准 |
| `00A1` | 提示音量 | `[档位][默认][最大 10][1]`，写入一字节档位 |

- 佩戴检测开关不在配置空间：读 run-info op `09` 掩码 `00 00 04 00`，记录 10（取反，0 = 开）；写 set-device-info op `08` 记录 6 `[02 06 v]`，同样取反，再读 run-info 确认。**[社区]** / **[协议实现]**
- Redmi Buds 6 Play、REDMI Buds 8 Active 会应答 `000B` 但没有降噪硬件，因此降噪还要求型号已知具备 ANC。**[社区]**
- 查找提示音很响：开始播放前重新读 `000C`，任一耳机在耳中则拒绝，读不到也不播放；停止总是允许。Redmi Buds 6 Lite 社区记录显示没有查找命令，不提供。**[社区]** / **[协议实现]**
- 未实现：降噪循环编辑、贴合度检测、空间音频场景、固件升级（参考项目未给出可核对的真机读写记录）。**[推测]**

## 13. vivo / iQOO GAIA 设置

来源：[TWS-Pods-PC `3350f42`](https://github.com/Zhaoyi-ya/TWS-Pods-PC/tree/3350f424c1ab69c4bc6cff92dd7b603fb0025db2) 的 `vivo/PROTOCOL.md`（TWS 3e 固件 2.5.9 抓包重放），[vivopods-mac `be25133`](https://github.com/FxRayHughes/vivopods-mac/tree/be2513351f66a3643863d263587bc7aefd9e81b3)（TWS 5 固件 2.11.1 真机）。本节 [社区] 指这两个项目的真机记录。命令为大端 16 位，回报 = 命令 | `0x8000`，回报 payload 首字节为状态（0 成功）。

| 发现 | 证据 |
|---|---|
| 双击 `0x0102 [code]`，左耳 0x00–0x0F、右耳 0x10–0x1F，一次写一只耳；回报 `0x8202 [00][左][右]`，同时也是注册链第一帧的应答 | [社区]（TWS 3e）[协议实现] |
| 长按功能 `0x0150 [03][function]`，回报 `0x8250 [00][03][function]`，双耳共用 | [社区]（TWS 3e）[协议实现] |
| 长按降噪循环 `0x0131 [05][左][右]`，查询 `0x0231 [05]`，回报 `0x8231 [00][05][左][右]`；只改一只耳时另一只原样回传；仅当长按功能为降噪时有意义 | [社区]（TWS 3e）[协议实现] |
| 佩戴检测开关 `0x0103 [0/1]`，回报 `0x8203 [00][0/1]`；与实时佩戴状态 `0x820D` 不同 | [社区]（TWS 3e）[协议实现] |
| 查找 `0x0120`、音效 `0x0118`、电量 `0x8207` 末字节充电位（1 左 / 2 右 / 4 盒）与既有实现一致 | [社区] |
| 双设备列表 `0x0249` → `0x8249 [00][count]` + 每台 `[MAC:6][3 字节][state][nameLen][UTF-8 名称]`；state 0 未连接 / 1 已连接 / 2 已连接且活动 | [社区]（TWS 5） |
| 列表写入 `0x014A` 为全量 `[MAC:6][state]`；其余设备必须原样回传 state，否则活动设备会从 2 降为 1 | [社区]（TWS 5）[协议实现] |
| 移除 `0x014D [目标 MAC:6][本机 MAC:6]`；总开关 `0x014C [本机 MAC:6][0/1]` 无查询命令，EncoDeck 不提供 | [社区]（TWS 5） |
| MAC 后 3 字节形似经典蓝牙设备类别（高字节在前），仅用于图标 | [推测] |
| TWS 3e 双连常开，`0x0249` / `0x014C` 均无应答，不查询 | [社区] |
| GATT 服务 `00001100-D102-11E1-9B23-00025B00A5A5`（写 `1101` / 通知 `1102`）上消息为裸 `vendor(2) cmd(2) payload`，无 `FF ver flags len` 帧头；Windows 上仅在 RFCOMM 服务不存在时尝试 | [社区]（TWS 5 macOS）[协议实现] |
| 其他型号使用相同命令，但没有真机记录：回读后作为实验功能提供 | [推测] |

## 14. 漫步者 EDIFIER 设置

来源：[mEDIFIER `10056b5`](https://github.com/wh201906/mEDIFIER/tree/10056b51ce1b354fdf2b18e35e61f8a0b8520cd5)（`doc/KnownCommands/W820NB_Double_Gold.txt` 完整收发帧、`Qt/devices/deviceinfo.json` 型号功能隐藏表），[Macifier `5479859`](https://github.com/luanyufei/Macifier/tree/547985930840a1c374659351fc8ca1e47a53e97d)（W820NB 真机），[HyperEars `361d279`](https://github.com/silverpoetry/HyperEars/tree/361d27970002ea86a778a7eaf722c3c022975591) 的 `docs/edifier-bes-protocol.md`，[BudsLink `ba3160d`](https://github.com/maniacx/BudsLink/tree/ba3160dfce72c192ebbdf2697b0341aa1e78fb56) 的 X5 Pro 配置。本节 [社区] 指上述项目的真机记录。

| 发现 | 证据 |
|---|---|
| v1 帧 `AA len cmd payload crc16`（种子 8217）；应答头 `BB`，部分写入的应答头为 `CC` | [社区] |
| 降噪查询 `0xCC` → `[mode][环境声字节]`；设置 `0xC1 [mode]` 或 `[03][level]`，level 3–9 对应 −3…+3 | [社区]（W820NB 双金标版抓包） |
| 游戏模式 查询 `0x08` → `[0/1]`，设置 `0x09 [0/1]` 原样回显 | [社区] |
| 按键控制 查询 `0xF0 [0A]` → `[0A][mask]`，设置 `0xF1 [0A][mask]`；mask 位 1 标准 / 2 降噪 / 4 环境声，官方选项 7 / 6 / 5 / 3 | [社区]（双金标版） |
| 音效 设置 `0xC4 [0–3]`（`CC` 头应答），查询 `0xD5` → `[value]` | [社区]（双金标版） |
| 定时关机 查询 `0xD3` → `[00]`（关）或 `[分钟高][分钟低]`；设置 `0xD1 [hi][lo]`、取消 `0xD2`，均以 `CC [01]` 应答；0 分钟设置会立即关机，因此只用 `0xD2` 关闭 | [社区]（双金标版）[协议实现] |
| W820NB 不显示音效与按键控制，W200BT Plus 不显示降噪 / 环境声 / 按键 / 游戏模式（mEDIFIER 型号表） | [社区] |
| BES v2 帧 `AA EC cmd lenHi lenLo payload sum8`；多数型号 payload 双向异或 `0xA5`，校验覆盖发送字节；FitBuds Turbo 为明文 | [社区]（HyperEars） |
| 降噪槽位：W860NB PRO `10`（1 深度 / 2 舒适 / 3 防风 / 4 环境声 / 5 关）；花再 Evo Pro、FitBuds Turbo `1B`（1–3 降噪 / 4 防风 / 5 环境声 / 6 关）；X5 Pro `17`（1 降噪 / 2 环境声 / 3 关） | [社区] |
| Evo Pro 的 1–3 三个降噪值强度未命名，EncoDeck 只显示为降噪，写入用 1 | [推测] |
| TWS 状态 `0xF2`：`[peer][左][右][盒][标志]…`，标志字节为 3 表示充电盒离线 | [社区]（HyperEars） |
| FitClip Ultra 收到 `0xCC` 会断开，从不发送 | [社区] |
| X5 Pro 佩戴检测 `0xFB` / `0xFC`、游戏模式无应答样本：回读后作为实验功能 | [推测] |
| W860NB PRO 降噪切换后约 1.9 秒播报提示音，期间不应答；回读超时后延迟 2 秒重试一次 | [社区] |
| W820NB 三金标版的 SPP 通道可以打开，但对 v1 的电量 `0xD0` 与降噪 `0xCC` 查询均无应答；它以 W820NB 变体名匹配到 v1 配置 | 用户诊断日志 |
| v1 型号两项查询都无应答时，改用 BES v2 帧只读查询一次 `0xD0`（空 payload，异或与否帧相同），由应答的电量字节判断异或：明文 ≤ 100，异或后最高位必为 1；应答后只提供电量，不查询降噪与其他设置 | [推测] [协议实现] |
| 漫步者充电状态没有任何真机证据，不实现 | — |

## 15. 索尼 MDR 与 AKG（独立于 OPPO）

来源：[SonyHeadphonesClient `4061cf3`](https://github.com/mos9527/SonyHeadphonesClient/tree/4061cf353fd0044e559e9a1f1b7f2faa334d09c6)（libmdr 协议定义及 WH-1000XM3 4.5.2、WF-1000XM5 6.1.0、WF-LC900 2.0.3 真机抓包）、[SonyPods `3f701ff`](https://github.com/Mercury000/SonyPods/tree/3f701ff8f9b30c6909b9dbe8d99248f20d55560e)（GATT 特征 UUID）、[akg_earbud_control `184d2e2`](https://github.com/tthayer/akg_earbud_control/tree/184d2e21542d0f419e4d5789cc555e5c41c9863e)（N400NC 真机记录）、[jbl-control `a276b85`](https://github.com/AhmadRun/jbl-control/tree/a276b8561c15fc5a3effa76cb75500847219d750)（同一 GATT 服务的交叉核对）、[Raito `5804b65`](https://github.com/kalicyh/Raito/tree/5804b657e7d6aa5fcd17d33a3c6f13ce4602086a)（N9 Hybrid 真机记录）。除 WH-1000XM5 固件 2.5.1（见 15.1 末条）外，本项目均未在 Windows 真机上验证。

### 15.1 索尼 MDR

- **[社区]** 帧 `3E type seq len(4,BE) payload checksum 3C`；ACK 为 type 1、空 payload、sequence 取反。WH-1000XM3 4.5.2 先发送回包再发送 ACK，等待器同时接受两种顺序，不重发已被回应的请求。
- **[社区]** `CONNECT_RET_PROTOCOL_INFO` payload 长 8 字节时为 v2。功能表 `0x06 → 0x07`：v1 每项 1 字节，v2 为（功能，优先级）对。设置只在功能表列出时查询。
- **[社区]** NC/ASM v1（inquired type 2）：`[67,02,effect,ncType,ncValue,asmType,asmId,asmLevel]`，asmId 1 为聚焦人声，级别 1–20。环境声写入 `68 02 11 01 00 01 00 14` 与 XM3 抓包一致。v2 布局 0x13–0x19：聚焦人声字节位于环境声级别前一字节；写入以设备读回为基础，只改模式、聚焦人声和级别字段。
- **[协议实现]** 布尔设置：v1 DSEE `E6/E8 type 2`、佩戴控制 `F6 type 3`（以 `FC` 写入）、Speak-to-Chat `F6/F8 type 5`；v2 DSEE `E6/E8 type 1`、摘下暂停 `F6/F8 type 1`、Speak-to-Chat `F6/F8 type 0x0C`，v2 中 0 表示开启。WF-1000XM5 6.1.0 抓包的读回均可解析。
- **[社区]** 通用设置 `D0 → D1` 返回设置名称；仅 `TOUCH_PANEL_SETTING` 与 `MULTIPOINT_SETTING` 的布尔项映射为开关，值 `D6 → D7`、写入 `D8`（v1 开 = 1，v2 开 = 0）。XM3 GS1 是列表型设置，不作为开关。
- **[社区]** 自动关机：v1 `F0/F6/FC type 4`，v2 `20/26/28 type 4 或 5`；元素 0/1/2/3 = 5/30/60/180 分钟，v2 另有 4 = 15 分钟，0x11 关闭，0x10 摘下时关机（v2 仅 type 5）。只提供耳机能力回包列出的元素。
- **[协议实现]** 按键分配（v2）：`F0 → F1 type 3` 列出每个按键可选的预设，`F6 → F7 type 3` 读当前预设，`F8 03 n presets…` 写入全部按键。能力回包长度必须正好消费完，截断即拒绝。
- **[社区]** EQ：预设名称为空时（WF-LC900 2.0.3、XM3）按编号显示名称；选择预设时频段数为 0，否则固件会切到 Custom。WH-1000XM6（固件 3.0.0 起）只应用 0x30–0x33、0xA0 与用户设置，其余上报的编号隐藏。ULT 组合 EQ 未实现。
- **[社区]** WF-C700N 固件 2.0.5 会确认并回显 NC/ASM 写入但不生效，2.0.7 修复；EncoDeck 在 2.0.5 上拒绝写入。
- **[社区]** BLE GATT：服务 `5b833e20-6bc7-4802-8e9a-723ceca4bd8f`，写 `5b833c60-…`、通知 `5b833c61-…`，承载与 RFCOMM 相同的 MDR 字节流。SonyHeadphonesClient PR #43 取服务中第一个可写 / 第一个可通知特征；EncoDeck 优先使用上述命名特征，找不到时同样回退到第一个。**[推测]** 该 PR 优先无响应写入，EncoDeck 在特征支持时使用有响应写入，未发现影响。GATT 只在两个 RFCOMM 服务都失败后尝试。
- **[贡献者实测]** WH-1000XM5 固件 2.5.1：主控制服务 `956c7b26-…` 的 SDP 记录存在（服务名 `Serial HPC`），但协议描述符是 `RFCOMM:00000000`，Windows 解析出的通道号为 0，无法用它建立连接；`96cc203e-…` 在 SDP 中不出现。经典链路在线时直连通道 9 可完成握手（`CONNECT_RET_PROTOCOL_INFO` 返回 8 字节，即 v2），功能表 33 项，电量、NC/ASM、EQ、摘下暂停、多点连接、说话即对话、DSEE、触控开关均可读；降噪 Off ↔ 降噪 写入后回读一致（62–211 ms）。该固件在会话空闲约 10–25 秒后主动关闭 RFCOMM 连接，需重连后重新握手。

### 15.2 AKG N400NC / N200NC / N700NCM2 / Y600

- **[社区]** 帧 `AA cmd len payload…`，无校验、无 ACK。BLE 服务 `65786365-6c70-6f69-6e74-2e636f6d0000`，通知 `…0001`、写 `…0002`（有响应写入），jbl-control 的 JBL 耳机使用同一服务。N400NC 没有 SPP 服务；SPP 仅作为备用保留。
- **[社区]** 设备信息 `AA 11 00 → 12`：整帧偏移 22 的 bit 7 为充电、低 7 位为电量，29–31 为固件版本。电量命令 `0x25` 在 N400NC 上无应答。
- **[社区]** 状态 `AA 21 01 sub → 22 sub …`：0x30 全部（ANC、环境模式、自动关机、EQ），0x31 ANC，0x32 环境模式（0 关 / 1 对话模式 / 2 环境感知），0x34 EQ，0x38 自动播放暂停；不带选择字节的请求不会应答。
- **[协议实现]** 写入：ANC `31`、环境模式 `32`、EQ 预设 `40`（0–8：Off / Jazz / Vocal / Bass / User / Rock / Piano / Club / Studio）、自动播放暂停 `35`。切换降噪 / 环境声时先关闭另一项，再读状态确认。N200NC / N700NCM2 / Y600 共用同一命令层 **[推测]**。
- **[社区]** 原版 N700NC 使用不同的 SPP 控制层，公开资料不足，仅识别。

### 15.3 AKG N5 / N9 Hybrid（Airoha RACE）

- **[社区]** N9 Hybrid 广播独立的 `-LE` 外设，RACE GATT 服务 `5052494d-2dab-0341-6972-6f6861424c45`；经典 RACE SPP 为首选路径。
- **[社区]** 电量 `0x0CD6`（payload 为角色 0 主耳 / 1 副耳）、充电 `0x0009`、充电盒 `0x000B`；副耳无数据时以状态非零的 `0x5B` 应答。左右只在 `0x0CD4` 回报声道后分配。
- **[推测]** ANC 写 `0x0E06`、读 `0x0901` 的数值来自 JBL Tune 770NC 的社区记录，未在 AKG 上验证；只在实验性开关打开且读回布局有效时显示。


## 16. USB 2.4G 接收器（独立于 OPPO）

本节的 **[社区]** 指参考项目在真实设备上读到的报文或数值；**[协议实现]** 指 EncoDeck 按这些资料实现并有字节向量 / 模拟会话测试，但尚未在本机真机验证。所有查询只发往精确 VID/PID 的指定 HID 集合，WinRT 报告的首字节是报告 ID（无报告 ID 的集合为 `00`）。来源：[HaloBattery `2251f05`](https://github.com/HeyOkay/HaloBattery/tree/2251f0534a8e886c9692ead42056059b4f69d8c0)、[HeadsetControl `2c6790b`](https://github.com/Sapd/HeadsetControl/tree/2c6790b63ae8220f7b35d7439cd9ea165ca58751)、[barracuda-x-battery `0511a01`](https://github.com/nightdevx/barracuda-x-battery/tree/0511a0179e2db85839714b5bea5a147079336943)、[NPS.HecateBattery `32b41b2`](https://github.com/SnowDreamXUE/NPS.HecateBattery/tree/32b41b28b6d434769020321fbfab6d2fbafa6657)、[OpenRazer #2862 / #2794](https://github.com/openrazer/openrazer/pull/2862)、[blackshark-control `6dfbe94`](https://github.com/AbdullahSAhmad/blackshark-control/tree/6dfbe941e178a88b4706e7e919f2f7dabe8659d4)、[mchose-battery `e93be42`](https://github.com/Tensyd/mchose-battery/tree/e93be42b84469b6c251ae22c7e6602ef466f33b7)、[razer-barracuda-2.4-linux `cc57a8c`](https://github.com/TarikTopalovic/razer-barracuda-2.4-linux/tree/cc57a8c0e02b848c9025433dc745d3bc45120910)。

### 16.1 简单请求 / 应答

| 接收器 | 请求（64 字节输出报告） | 应答 | 等级 |
| --- | --- | --- | --- |
| 迈从 X9 `3837:6045`、V9 PRO `291D:385D`（`ff90:0001`） | `55 65 01` | `55 65 <值> <字段>`，字段 `02` 为电量；心跳与其他字段忽略 | [社区] X9 Windows 20 / 50 / 60%，V9 PRO 80% |
| HyperX Cloud Alpha 2 `03F0:08BE`（`ff13:ff00`） | `50 02` | `51 02 <电量>`；`61 02 …` 在两个电量下完全相同，不是电量 | [社区] 51% / 67% 两帧 |
| HyperX Cloud III S `03F0:02CC` / `06BE` | `0C 02 03 01 00 06`（电量）、`… 48`（充电） | 报告 `0C`：[5] 命令、[6] 值（`FF` 无值）；通知 `0D`：[4]=1 电量、[4]=10 充电 | [社区]；集合选择为 [推测] |
| SteelSeries Arctis Nova 7 `1038:22A1`（`ffc0:0001`） | `00 B0` | `B0 <链路> <电量> <状态>`：链路 `03` 已连接、`02` 未连接（此时电量是旧值）；状态 `00` 关机、`01` / `02` 充电、`03` 电池 | [社区] 73% 电池、69% 充电、关机、开机中四帧 |
| 漫步者 HECATE G3 Max `35BB:A226`（接口 3 集合 3） | `D0 35 BB 00` | `D0 35 BB 0A 00 <链路> <电量>`；链路 `00` 时电量恒为 0，不是 0% | [社区] 96% / 94% / 关机帧 |

- 每个请求只接受自己的应答；没有应答就结束本次读取，链路报告未连接也结束。
- Alpha 2 第 6 字节 bit 7 在一次充电后置位，是否表示充电未确认，[推测]，不显示。G3 Max 充电字段未确认，不显示。
- Cloud III S 和 G3 Max 的资料只给出报告 ID，不给 usage；EncoDeck 只向声明了该 64 字节输出报告的厂商集合发送（WinRT 创建报告时即可判断，不会发出任何数据）。[协议实现]

### 16.2 被动上报

- JBL Quantum 910 `0ECB:2088`（`ff13:0001`）没有查询命令：报告 `08` 的 [1] 为电量，报告 `09` 的 [1] 为耳机电源（`00` 关、`01` 开），`2F` 为麦克风静音。电量通常只在接上充电器时上报。[社区]
- EncoDeck 每次刷新只监听 1.5 秒，不发送任何报告，也不保留上一次听到的电量。[协议实现]

### 16.3 雷蛇 Airoha “PA” 命令

- 内层帧：`50 41 <组> <序号> <长度> <正文>`；应答 `50 49 <组> …`，[8..9] 为负载长度，[10] 命令、[11] `01` 应答 / `02` 主动上报、[12] 值长度、[13..] 值。组 `08` 为客户数据，GET 正文 `<id> 00 00`，SET 正文 `<id|80> 00 <n> <值…>`；组 `0E` 的 `E1 01/00` 为远程模式。[社区]
- 封装：Macronix 型 `01 80 <len> <帧>`（Barracuda X `0552`、Barracuda Pro `053A`、Barracuda `053C`，后两者序号固定 `08`）；YS-Tech 型 `02 80 <len> <crc 低> <crc 高> <帧>`，应答 `02 <len> <流>`。`0550` 的 CRC-16/XMODEM 覆盖整个 64 字节报告（CRC 字段置零），发送后用 GetInputReport 读取 `O` 已接收 / `F` 忙；BlackShark V2 Pro `0555` 同样布局但 CRC 与序号为 0。[社区]
- 读数：远程模式 → GET `21` 电量 → GET `2A` 充电线（`0` 电池，`1` / `2` 接线）→ 释放远程模式。`0555` 空闲约 0.3–0.5 秒后链路休眠，第一帧只用于唤醒，因此先发一次远程模式再查询，每次重试都重新进入远程模式。[社区]
- `0550` 采用 barracuda-x-battery 真机流程（无握手、无 `E3` / `E0`）；`0552` 保留 leag 资料的握手、`E3` 链路与 `E0` 路由检查。[社区]
- 设置（GET / SET）：EQ 预设 `13` / `93`、自定义 EQ `15` / `95`、麦克风监听 `18` / `98` 与等级 `19` / `99`、勿扰 `27` / `A7`、自动关机 `2C` / `AC`。`0550` 全部设置有真机读回记录；`0555` 预设选择需 `GET 1E` → `SET 93` → `SET 9D <家族>`（电竞预设 `FA–FE` 为 `02`，其余 `01`），跨家族首次只切换家族。[社区] EncoDeck 实现读回确认与重试；界面只开放 `0550` 的 EQ 预设、自定义 EQ、勿扰与自动关机，`0555` 未开放。[协议实现]
- Barracuda `053C` 的 `98` / `99` / `AC` 写入在 2.4G 下读回恒为 0，无法确认生效，不写入。不发送任何未记录的命令：对该接收器做未封装的命令扫描曾使其进入引导模式（PID `5020`）。[社区]

### 16.4 Logitech Centurion（G PRO X 2 LIGHTSPEED `046D:0AF7`）

- `ffa0:0001`，报告 `51`：`51 <len> <flags> <负载>`，len = 负载长度 + 1。直接请求 `<索引> <功能|01> <参数>`；桥接请求 `<桥> 11 <长度:2> 00 <索引> <功能|01> <参数>`，接收器先回 `<桥> 11` 表示收下，耳机应答 `<桥> 10 <长度:2> 00 <索引> <功能|01> <数据>`，索引 `FF` 表示拒绝。[社区]
- 流程：Root 功能 0 找 FeatureSet → 数量 → 逐个功能 ID 找桥 `0003` → 经桥在耳机上找电量 `0104` → 功能 0 读取 `<电量> ? <充电状态>`（0 放电、1 充电、2 USB 充电、3 充满）。只有接收器收下而耳机不回答时视为耳机未连接。发现结果按接收器缓存，出错后重新发现。[社区] / [协议实现]

### 16.5 只识别的接收器

- BlackShark V3 `1532:057A` / V3 Pro `0577`（OpenRazer #2794）：驱动代码与其协议说明中的电量请求格式（方向字节、长度、参数）不一致，Linux 下频繁查询 `21` 曾导致耳机掉线重连，握手序列还包含一次写入，因此暂不发送任何命令。[推测]

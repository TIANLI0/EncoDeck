<p align="center">
  <img src="src/EncoDeck.App/Assets/EncoDeck-256.png" width="88" alt="EncoDeck" />
</p>

<h1 align="center">EncoDeck</h1>

<p align="center">在 Windows 上轻松管理你的耳机，不用为了切个降噪再拿起手机。</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9PN6D8G1K9WZ?mode=direct">
    <img src="https://get.microsoft.com/images/zh-cn%20dark.svg" width="190" alt="从 Microsoft Store 获取" />
  </a>
</p>

<p align="center">
  <a href="https://github.com/TIANLI0/EncoDeck/releases/latest">下载安装包</a> ·
  <a href="README_EN.md">English</a> ·
  <a href="docs/COMPATIBILITY.md">设备兼容性</a> ·
  <a href="docs/DEVELOPMENT.md">开发文档</a>
</p>

> 🍎 **Mac 用户**推荐使用 [HeyBuds](https://github.com/abaichuan1017/HeyBuds)。

EncoDeck 是用 **WinUI 3** 编写的 Windows 耳机控制工具。它把常用的耳机设置搬到了电脑上：查看左右耳和充电盒电量、切换降噪、调整 EQ 和空间音频，以及管理耳机触控等功能。平时可以只留一个托盘图标，不需要一直开着主窗口。

目前以 **OPPO / 一加** 耳机为主要适配对象，也支持部分 **realme / DIZO、vivo / iQOO、华为、荣耀、小米 / Redmi、索尼、AKG、森海塞尔、漫步者、水月雨 MOONDROP、三星、Google、Bose、Nothing / CMF** 耳机及 USB 2.4G 游戏耳机。不同型号可用的设置不一样；**目前主要使用 OPPO Enco X4 进行真机验证**，其他型号主要依据协议和型号配置实现，欢迎反馈测试结果。

## 界面

<p align="center">
  <img src="docs/images/popup-demo.gif" width="300" alt="耳机连接弹窗" />
</p>

| 主界面 | 托盘快捷面板 |
| :---: | :---: |
| <img src="docs/images/home.png" alt="主界面" /> | <img src="docs/images/quick-panel.png" alt="托盘快捷面板" /> |

## 功能

### 电量与连接状态

连接耳机后，主界面和托盘面板会显示设备返回的电量；真无线耳机按支持情况显示左耳、右耳和充电盒，头戴式显示单组电量。充电状态、佩戴、入盒状态和固件版本取决于型号。

充电盒离开耳机后通常不会继续上报电量，因此 EncoDeck 会保留最近一次读数，并标明是「上次读取」，而不是把旧数据当作实时电量。电量过低时还会提醒。

### 降噪控制

不用打开主窗口，在托盘就能切换：

- **降噪 / 通透 / 关闭 / 自适应**（具体取决于耳机型号）；
- 降噪强度：**深度、中度、轻度、智能切换**；
- 自适应模式下的降噪偏好（耳机支持时）。

用耳机手势或者手机调整了降噪，EncoDeck 也会读取耳机上报的状态并更新界面。控制命令通过蓝牙发送，等待耳机确认并回读；失败时不会把按钮一直停留在错误状态。

还可以在「设置 → 快捷键」中绑定全局快捷键，比如一键在降噪和通透之间切换，或者直接切到指定模式。全屏游戏时同样可以使用。

### 音效

音效选项按照耳机的型号配置和实际能力显示。

| 设置 | 可以做什么 |
| --- | --- |
| **EQ** | 切换耳机内置的音效预设，如丹拿相关预设、人声、高音、低音等（名称因型号而异） |
| **自定义 EQ** | 在支持的 OPPO 型号上编辑、保存、切换和删除 EQ；Enco X4 对应最多 3 组、每组 10 段 |
| **BassWave** | 开关动态低音并调整强度 |
| **空间音频** | 切换关闭、固定空间音频和头部跟踪（需要耳机支持） |
| **游戏模式** | 开关低延迟模式及游戏音效强化，按耳机自身规则处理与其他音效设置的冲突 |

部分设置目前已能从真机读取，但写入仍主要依据协议实现，尚未逐项完成真机验证。详细情况在[兼容性表](docs/COMPATIBILITY.md)中逐项标注。

### 耳机控制与设备管理

在支持的型号上，还可以修改这些耳机端设置：

- **触控手势**：分别设置左右耳的捏合、长捏、滑动等操作及通话手势；可调整长捏时循环的降噪模式。
- **佩戴相关设置**：佩戴检测、入睡暂停，以及支持型号的点头接听、摇头拒接。
- **查找耳机 / 贴合度检测**：调用耳机提供的功能；查找耳机前会提示音量风险。
- **提示音**：调整耳机提示音音量。
- **双设备连接**：查看已记录的设备，按型号支持连接、断开、移除和设置优先设备。当前这台 Windows 电脑的蓝牙断开操作仍由系统蓝牙设置处理。

以上是*已接入的功能范围*，不是所有耳机都支持，也不代表每项操作都已经在真机上测试通过。

### Windows 桌面体验

**托盘和快速设置**

- 左键托盘图标打开电量、降噪快捷面板；右键使用系统原生菜单。
- 在支持的 Windows 11 环境中，按 **Win + A** 打开系统快速设置时，EncoDeck 的耳机面板可以贴在快速设置卡片上方；可在设置中关闭。
- 支持后台运行、开机启动、全局快捷键和浅色 / 深色主题。

**连接弹窗与设备模型**

- 耳机连接电脑时，在任务栏附近显示设备名称、电量和该型号的连接动画；全屏使用时不打扰。
- 弹窗播放动画不会打开音频输出，也不会出现在 Windows 媒体控制里，避免动画把蓝牙耳机的音频从手机抢到电脑。
- 首页按型号显示设备图片或模型。预渲染模型可以左右拖动；支持 `.vfxms` 的型号可实时旋转、缩放和复位。
- 设备素材按型号和颜色获取并缓存在本地，之后可复用。没有对应原厂动画时按设备形态展示：头戴式保留静态产品图，仅播放弹窗进入/退出动画。

*连接弹窗在耳机建立蓝牙连接时触发；目前没有实现靠近耳机或打开盒盖就弹出的 BLE 开盒检测。*

## 兼容性

各品牌按协议和型号配置实现，部分移植自开源项目。**代码里有某个型号，不等于这款耳机已经过真机测试。**

| 品牌 / 型号 | 当前支持范围 | 验证情况 |
| --- | --- | --- |
| **OPPO Enco X4** | 电量、佩戴、降噪与档位，以及 EQ、空间音频、手势、双设备连接等功能 | 电量及部分降噪操作已真机读写；不少高级设置已真机读取，写入仍待逐项验证 |
| **其他 OPPO / 一加** | 内置约 85 款型号配置；按型号能力提供降噪、音效、触控等设置 | 依据型号配置与协议实现，尚缺真机测试 |
| **realme / DIZO 与部分旧款 OPPO / 一加** | 补充约 50 款，主要提供电量、充电、佩戴状态和固件信息 | 尚缺真机测试 |
| **vivo / iQOO** | 约 43 款；电量等状态，以及部分型号的降噪、EQ、查找耳机、游戏模式；回读后开放手势、佩戴检测开关和双设备列表（TWS 3e / TWS 5 依据社区真机记录，其他型号为实验功能）；无经典蓝牙控制服务时改用 BLE | 协议移植，部分功能有社区真机记录，EncoDeck 未真机验证 |
| **华为** | 27 款；电量及按型号提供的降噪/档位、通透人声增强、EQ、佩戴检测、低延迟、双设备及设备列表；设备确认后开放独立空间音频/头部跟踪、智能音量、点头/摇头 | 协议移植，部分功能有社区真机记录，EncoDeck 未真机验证 |
| **小米 / Redmi** | 按系列识别，48 条型号能力记录；电量、降噪/档位、EQ、游戏低延迟、双设备、空间音频/头部跟踪、提示音量按型号与回读开放 | 协议实现，未真机验证 |
| **索尼** | 46 个内置型号名称；电量/充电、降噪/环境声、设备预设 EQ；环境声档位与级别、说话即对话、DSEE、摘下暂停、多点连接与触控开关、按键分配、自动关机按回读开放；LE Audio 下经 BLE 控制，手动 / 自定义 EQ 可调 10 段增益 | WH-1000XM6 的 LE Audio 控制（握手、电量、降噪）已由贡献者真机验证；WH-1000XM5 固件 2.5.1 经经典 RFCOMM 通道 9 真机验证（握手、电量、降噪读写、EQ 与开关回读）；其他型号为 MDR 协议及模拟设备测试 |
| **AKG** | N400NC、N700NCM2、N200NC、Y600：经 BLE（经典蓝牙备用）读取电量 / 充电、固件，降噪 / 环境感知 / 对话模式、预设 EQ、入耳自动播放/暂停；N5/N9 Hybrid 使用 Airoha RACE（电量、充电盒电量，降噪为实验功能）；N700NC 仅识别和素材 | 协议实现，未真机验证 |
| **漫步者** | W820NB、W200BT / W200BT Plus、W860NB PRO、花再 Evo Pro、FitBuds Turbo、FitClip Ultra、X5 Pro；Atom / Lolli 系列 28 个型号的识别、SPP / BLE 与双耳 / 盒电量；降噪 / 环境声、游戏模式、按键控制、音效与定时关机按型号和回读开放 | 协议实现，未真机验证；[范围说明](docs/COMPATIBILITY.md#漫步者) |
| **水月雨 MOONDROP** | 18 个型号档案及通用识别（非完整型号支持）；GAIA BLE / RFCOMM、电量、按型号降噪 / 抗风噪、增益；能力发现后开放 LED、提示音开关 / 音量、双耳 TOUCHV2、LHDC / 双设备互斥、出厂 EQ 预设与用户 PEQ；实验性加入 LDAC / LC3、入耳检测、空间音频 / 头部跟踪、动态低音、左右声道交换 | EDGE 固件 1.3.0 已在 Windows 真机验证 RFCOMM 控制连接与基础状态读取；EDGE、太空漫游2 的 EQ 读写有社区真机记录；新增设置及其他型号仍以协议移植与模拟验证为主；推断及新增功能须实验性启用 |
| **EarFun / ATVEL** | 23 条 GAIA / EF 型号记录，含 Air Pro 4+；电量、固件，按型号提供降噪、游戏模式、双设备、入耳检测、提示音、查找与触控；13 条精确产品图 | 协议移植与模拟设备测试，写入均读回确认，待更多真机反馈；[范围说明](docs/COMPATIBILITY.md#earfun--atvel) |
| **荣耀** | 9 款（含 HONOR CHOICE X5 / X5 Pro）；电量、降噪 / 通透及档位、佩戴检测、双设备连接、耳机返回预设后的 EQ（按型号） | 协议实现，未真机验证 |
| **森海塞尔** | 13 款电量与固件；MOMENTUM 4 / 5、ACCENTUM 系列、HDB 630 按型号提供降噪 / 自适应 / 通透、抗风噪、Smart Pause、用户 EQ 与已配对设备列表；MOMENTUM TW 4 降噪、抗风噪、佩戴检测、低延迟与触控自定义 | 协议实现与模拟设备测试，部分功能有社区真机记录，EncoDeck 未真机验证 |
| **三星** | Galaxy Buds2、Buds2 Pro、Buds4 Pro：左右耳与充电盒电量、关闭 / 降噪 / 通透、预设 EQ；Buds4 Pro 自适应模式与降噪强度，Buds2 / Buds2 Pro 触控锁定 | 协议移植，未真机验证 |
| **Google** | Pixel Buds Pro、Pixel Buds Pro 2：电量与充电、关闭 / 降噪 / 通透、预设 EQ、固件版本 | 协议移植，未真机验证 |
| **Bose** | QuietComfort 45、QuietComfort Ultra Headphones (2nd Gen)：电量、固件版本、安静 / 感知模式 | 协议移植，未真机验证 |
| **Nothing / CMF** | Nothing Ear (a)、Headphone (1)、CMF Buds 2、CMF Buds Pro 2：电量与充电、关闭 / 降噪 / 通透、预设 EQ、固件版本 | 协议移植，未真机验证 |
| **USB 2.4G 游戏耳机** | 41 款识别（漫步者 HECATE、达尔优、雷柏、迈从、雷蛇、HyperX、SteelSeries、JBL、Logitech）；迈从 X9 / V9 PRO、雷蛇 BlackShark V2 Pro / Barracuda 系列、HyperX Cloud Alpha 2 / Cloud III S、Arctis Nova 7、G PRO X 2、HECATE G3 Max 等可读电量 | 社区项目真机确认的协议，EncoDeck 未真机验证；[范围说明](docs/COMPATIBILITY.md#usb-24g-专用查询) |

更多型号、具体功能和验证记录见 **[设备兼容性列表](docs/COMPATIBILITY.md)**。如果你的耳机没在已测试列表里，但可以正常使用，欢迎提交型号、固件和测试结果。

## 下载

| | Microsoft Store | GitHub 安装包 |
| --- | --- | --- |
| 平台 | x64 / ARM64 | x64 |
| 更新 | Microsoft Store 自动更新；应用内也可检查 | 应用内检查 GitHub Releases，下载后由你确认安装 |
| 安装 | [打开商店](https://apps.microsoft.com/detail/9PN6D8G1K9WZ) | [下载 Releases](https://github.com/TIANLI0/EncoDeck/releases) |

**系统要求：Windows 10 19041+，推荐 Windows 11。**

独立安装包名称为 `EncoDeck-Setup-<版本>-win-x64.exe`。默认安装到 `%LOCALAPPDATA%\Programs\EncoDeck`，通常不需要管理员权限；安装新版本可以直接覆盖升级，设置和缓存会保留。

使用前先在 **Windows 蓝牙设置中完成耳机配对并连接**，然后启动 EncoDeck。部分型号的设备图片和动画需要首次联网获取。

### LHDC V5（可选 / 实验性）

非商店 x64 版可按需接入 [LHDC-Win](https://github.com/Wayne-u/LHDC-Win)。在“实验室”页面下载独立组件后，可以选择音频设备、设置播放参数，并尝试启用 LHDC 或恢复 Windows 原有音频配置。

这不是普通耳机控制功能：它涉及测试签名驱动、管理员权限和系统音频配置，要求 Windows 11 22H2+、x64 及支持 LHDC V5 的耳机。**商店版不支持，实际播放兼容性仍待真机验证。** EncoDeck 不会自动修改 Secure Boot 或系统测试模式。操作前请先阅读 [LHDC 说明与恢复方法](docs/LHDC.md)。

### A2DP Bridge（可选 / 实验性）

非商店版可在“实验室”页面下载并管理 [A2DP Windows Bridge](https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge)：它接管一个**独立的 USB 蓝牙适配器**，以 LDAC、LHDC V5、aptX HD 等编码向耳机发送声音，不需要测试签名驱动。EncoDeck 负责下载（上游官方构建的原样副本）、启动后台、连接与断开、音量和适配器驱动管理，并在首页识别通过它连接的耳机。

需要第二个 USB 蓝牙适配器；只经 Bridge 连接时 Windows 看不到耳机，电量与耳机设置不可用（支持双设备连接的耳机可同时连接电脑蓝牙）。商店版只连接你自行安装并启动的 A2DP Windows Bridge。详见 [A2DP Bridge 说明](docs/A2DP_BRIDGE.md)。

LHDC 与 A2DP Bridge 组件都有各自独立的 Release，更新 EncoDeck 不会重新下载它们；组件可以在实验室页单独检查更新、更新和卸载，下载内容按大小与 SHA-256 校验。

## 编译

需要 .NET SDK 10.0.100 或更新版本。普通构建不需要安装 Visual Studio。

```powershell
git clone https://github.com/TIANLI0/EncoDeck.git
cd EncoDeck

dotnet test tests/EncoDeck.Tests
pwsh build/build-installer.ps1
```

安装包输出到 `artifacts/installer`。开发环境、项目结构及其他构建命令见 [DEVELOPMENT.md](docs/DEVELOPMENT.md)。

仓库还附带命令行诊断工具 **EncoDeck.Probe**，可用于检查蓝牙设备、SDP 服务、读取耳机状态和监听协议事件。诊断信息默认会对蓝牙地址脱敏，使用方法见 [TESTING.md](docs/TESTING.md)。

## 反馈问题

如果遇到连不上、某项设置不生效或者型号识别错误，可以开一个 [Issue](https://github.com/TIANLI0/EncoDeck/issues)。请尽量写明耳机型号、固件版本、Windows 版本、操作步骤；有条件的话可以附上 Probe 报告。

协议研究、设备测试和代码贡献都欢迎。参与开发前可以看看 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [协议记录](docs/PROTOCOL_FINDINGS.md)。

## 致谢

耳机协议与设备适配参考了 [OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows)、[OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager)、[OppoPods](https://github.com/Leaf-lsgtky/OppoPods) 和 [HeyBuds](https://github.com/abaichuan1017/HeyBuds)；托盘功能使用 [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)。其他第三方组件见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
水月雨型号档案、GAIA 帧格式与控制命令参考 [MiuixMoondrop](https://github.com/huime180/MiuixMoondrop)，支持范围与限制见兼容性列表。

## 许可证

[GPL-3.0-only](LICENSE)。

EncoDeck 是非官方开源项目，与上述耳机品牌及其所属厂商无隶属、合作或背书关系。品牌和产品名称仅用于说明兼容性。

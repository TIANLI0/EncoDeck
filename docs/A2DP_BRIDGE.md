# BlueCaster 可选组件

[BlueCaster](https://github.com/SeiyaFunaokaJP/BlueCaster)（3.0 起更名，原名 A2DP Windows Bridge，简称 A2DPWB）让一个 **USB 蓝牙适配器**脱离 Windows 蓝牙，用自己的用户态蓝牙协议栈以 LHDC V5、LDAC、aptX Adaptive、aptX HD、aptX、AAC、SBC XQ 等编码向耳机发送声音，**不需要测试签名驱动，也不改动安全启动或内存完整性设置**。

EncoDeck 只负责**连接** BlueCaster：在“实验室”页可以一键安装、启动它、打开它的窗口；BlueCaster 连上耳机后，EncoDeck 自动切换到这副耳机并通过它控制耳机；EncoDeck 的“连接 / 断开”会开始、停止 BlueCaster 到耳机的连接。BlueCaster 的更新、卸载、适配器、耳机配对、编码和音量都由 **BlueCaster 自己**管理。

## 免责声明

BlueCaster（原 A2DP Windows Bridge）是 Seiya Funaoka 的第三方开源项目（Apache-2.0 / MIT）。EncoDeck 运行的是其**官方安装程序**，不修改、不重新编译；其中静态链接的 BTstack 仅许可**个人、非商业用途**。耳机兼容性、无线质量和适配器固件不在 EncoDeck 控制范围内，请自行评估并承担使用风险；EncoDeck 及其开发者在适用法律允许的范围内不对由此造成的损失承担责任。

## 与 LHDC 组件的区别

| | LHDC 组件 | BlueCaster |
| --- | --- | --- |
| 工作方式 | 在 Windows 自带蓝牙上替换音频驱动 | 接管一个 USB 蓝牙适配器，自带协议栈 |
| 额外硬件 | 不需要 | 推荐单独的 USB 蓝牙适配器；也可用电脑自带的（使用期间 Windows 没有蓝牙） |
| 系统设置 | 需要测试签名模式、关闭内存完整性 | 不需要；只为该适配器分配微软签名的 WinUSB 驱动 |
| 编码 | LHDC V5 | LHDC V5、LDAC、aptX 系列（含 aptX Adaptive）、Opus、AAC、SBC XQ、FastStream 等 |
| Windows 是否看得到耳机 | 看得到，EncoDeck 照常控制 | 只经 BlueCaster 连接时看不到；A2DPWB 2.0.2 起 EncoDeck 经 BlueCaster 控制（见下文） |
| 由谁管理 | EncoDeck（下载、更新、卸载） | BlueCaster 自己（EncoDeck 只安装一次并连接） |

两者互不依赖，可以只用其中一个。LHDC 组件不需要额外硬件，所以在实验室页排在前面。卡片标题下的编码标记按 BlueCaster 3.0 的支持列表：实心为正式支持，描边为仅在 BlueCaster 开发者模式中可用（Opus 05、LC3 / LE Audio）。

## EncoDeck 如何跟随 BlueCaster

BlueCaster 通过它自己的适配器连接耳机，这条连接对 Windows 蓝牙是不可见的。EncoDeck 连接 BlueCaster 后台的本地管道（仅当前用户可访问），读取它正在连接的耳机地址，与 Windows 中配对过的耳机对应起来。只在 BlueCaster 中配对过的耳机（Windows 从未配对，或已从 Windows 蓝牙中删除）按 BlueCaster 的连接配置列为一台设备；同一副耳机也在 Windows 中配对时，以 Windows 中的设备为准。

- 无论 BlueCaster 后台是从它的窗口、托盘、开机启动还是 EncoDeck 启动的，EncoDeck 都会在几秒内连上它。
- BlueCaster 开始向一副耳机推流，就算作这副耳机“刚连接”：EncoDeck 自动切换到它，与 Windows 新连上一副耳机时一样。在 EncoDeck 中断开过的耳机也是如此，此时 EncoDeck 仍不碰 Windows 的连接，只经 BlueCaster 控制。
- 首页和快捷面板的连接状态旁显示 **BlueCaster** 徽标，编码标签显示 BlueCaster 实际使用的编码（如 LDAC）；悬停徽标可查看码率、采样率，以及耳机设置经哪条连接控制。“设备”页把这副耳机显示为“已通过 BlueCaster 连接”。
- 只经 Bridge 连接时，Windows 看不到耳机。BlueCaster（及 A2DPWB **2.0.2 及以上**）可以替 EncoDeck 打开到耳机的控制通道（RFCOMM）：EncoDeck 经 Bridge 读取电量、更改耳机设置，首页照常显示“已连接”和 BlueCaster 徽标。电脑上唯一的蓝牙适配器交给 Bridge 后（Windows 不再有蓝牙），同样可以这样控制。控制通道只在 Bridge 以经典蓝牙（A2DP）向这副耳机推流期间可用，断开推流时一并关闭；只有 LE（GATT）控制通道的耳机仍无法经 Bridge 控制。
- 经 BlueCaster 连接的耳机，快捷面板、首页和“设备”页的 **“断开此电脑”** 会停止 BlueCaster 到这副耳机的推流，EncoDeck 随后不再控制它（与断开 Windows 连接时一样）；**“连接此电脑”** 会启动 BlueCaster 中这副耳机的连接配置（后台未运行时先启动后台）。只在 BlueCaster 中配对过、上次经 BlueCaster 连接，或 Windows 已没有蓝牙适配器时，连接走 BlueCaster；其余情况仍由 Windows 连接。
- A2DPWB 2.0.1 及更早版本不能转发控制通道：只经 Bridge 连接时，EncoDeck **读不到电量、也无法更改耳机设置**，状态显示“已连接（仅音频）”，首页给出一条简短提示。
- 另有一个适配器留给 Windows 时，支持**双设备连接**的耳机可以同时连接两边：EncoDeck 优先通过 Windows 控制耳机，声音仍走 BlueCaster。此时请把 Windows 的默认输出设为其他设备（或让 BlueCaster 采集虚拟声卡），避免两路声音同时发往耳机。

## 开始之前

- 一个可以交给 BlueCaster 的 USB 蓝牙适配器。推荐单独的蓝牙适配器（上游验证过 TP-Link UB500 / Realtek RTL8761BU 等，见其[验证过的硬件](https://seiyafunaokajp.github.io/BlueCaster/hardware)）；许多笔记本自带的 USB 蓝牙也可以用，但 BlueCaster 使用期间 Windows 没有蓝牙，可随时在 BlueCaster 的“适配器”中还给 Windows。
- Windows 10 22H2 或 Windows 11（x64 或 ARM64）。
- 非商店版 EncoDeck 才能安装和启动 BlueCaster。商店版不会安装或启动其他程序，但会自动连接你自行安装并启动的 BlueCaster，并显示状态、控制耳机。

**把电脑自带的蓝牙适配器交给 BlueCaster 后，这台电脑上经 Windows 连接的蓝牙设备（鼠标、键盘等）都会断开**，直到把驱动恢复给 Windows。

## 一、安装

打开“实验室”页的 **BlueCaster** 卡片：

- 如果已经安装过 BlueCaster，EncoDeck 会直接使用它。
- 否则点击 **“安装 BlueCaster”**：EncoDeck 从 BlueCaster 官方 Release 下载最新的官方安装程序，按 GitHub 给出的 SHA-256 校验后运行（默认按当前用户安装）。安装完成后会打开 BlueCaster 窗口。

此后 BlueCaster 由它自己检查更新；卸载请在 Windows 设置的“已安装的应用”中进行。

## 二、首次设置（在 BlueCaster 中）

在 BlueCaster 窗口（卡片上的 **“打开 BlueCaster”**）中按它的首次设置向导完成：

1. 选择适配器并完成设置，BlueCaster 会为它分配 WinUSB（Windows 会请求管理员授权）。
2. Realtek 适配器需要固件文件（如 `rtl8761bu_fw.bin`、`rtl8761bu_config.bin`），从 [linux-firmware](https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/rtl_bt) 下载**原始文件**后放入 BlueCaster 的设置文件夹。
3. 添加耳机、选择编码并连接（耳机需处于配对模式，首次连接会通过 BlueCaster 的适配器完成配对）。采集单个应用、虚拟声卡或调整码率等进阶设置也在那里修改。

耳机、连接配置、编码和音量只在 BlueCaster 中管理，EncoDeck 不另外保存一份，两边不会不一致。

## 三、日常使用

- 在 BlueCaster 或 EncoDeck 中连接 / 断开耳机；切换连接配置和调节耳机音量在 BlueCaster 中进行，EncoDeck 跟随它的状态。EncoDeck 卡片显示后台状态，正在播放时显示编码、码率、采样率和音源。
- BlueCaster 默认采集 Windows 正在播放的声音。要传输 96 kHz LDAC，请在 Windows 声音设置中把播放设备格式设为 96 kHz。
- 开启 **“EncoDeck 启动时同时启动 BlueCaster 后台”** 后，EncoDeck 随开机启动时也会在后台启动 BlueCaster（不打开它的窗口）。后台没有运行时，也可以点击卡片上的“启动后台”。
- BlueCaster 后台有自己的托盘图标，从那里退出。关闭 EncoDeck 不会中断播放。
- 停用时，在 BlueCaster 中把适配器的驱动恢复给 Windows，再从托盘退出。卸载 EncoDeck 不会改动 BlueCaster 或适配器驱动。

以前由 EncoDeck 下载的 BlueCaster 便携版副本仍可启动和使用，但 EncoDeck 不再更新它；建议点击“安装 BlueCaster”改用官方安装版。已安装或正在运行的 A2DPWB 2.0.x 也仍可照常使用。

## 四、遇到问题怎么办

| 现象 | 处理方法 |
| --- | --- |
| 适配器、固件或耳机连接有问题 | 点击“打开 BlueCaster”，在它的窗口中查看和处理。 |
| 通信协议不兼容 | BlueCaster 与 EncoDeck 版本不匹配：在 BlueCaster 中更新它，或更新 EncoDeck。 |
| 首页仍显示未连接 | 确认 BlueCaster 后台在运行、耳机在 BlueCaster 中有连接配置并正在推流（EncoDeck 按蓝牙地址对应）。只在 BlueCaster 中配对过的耳机，需要 BlueCaster 后台运行时才会出现在 EncoDeck 中。 |
| 后台 15 秒内没有响应 | 点击“打开 BlueCaster”查看原因，或查看它设置文件夹中的日志。 |
| 安装程序没有完成 | 重新点击“安装 BlueCaster”，或从 [BlueCaster 的 Release](https://github.com/SeiyaFunaokaJP/BlueCaster/releases) 手动下载 `setup.exe` 安装。 |

反馈问题时，EncoDeck 的“导出诊断”会一并附上 BlueCaster 的诊断信息（蓝牙地址已遮盖）。

## 安装程序的来源与校验

- **直接取自官方 Release**：安装程序从 [BlueCaster 的 Release](https://github.com/SeiyaFunaokaJP/BlueCaster/releases) 原样下载，不经 EncoDeck 转发，不重新编译、不修改。
- **不在 EncoDeck 中固定版本**：EncoDeck 只知道 BlueCaster 的仓库地址，不内置任何版本号、文件名或校验值。每次点击“安装 BlueCaster”时才去查询，所以总是安装当时最新的官方版本，不需要等 EncoDeck 更新。
- **最新的官方版本**：读取 BlueCaster 官方的 Release 列表，取其中最新的一个（不限主版本，测试版也包括在内；只向主动开启的用户推送的 alpha 版本和草稿不包括）中与本机架构对应的 `setup.exe`。它必须有 GitHub 给出的 SHA-256 摘要，下载按这个摘要和大小校验后才会运行。中断的下载会续传。
- 如果以后的主版本改变了通信协议，后台会在握手时拒绝连接，卡片显示“通信协议不兼容”。

## 资源占用

- EncoDeck 只在点击“安装 BlueCaster”时联网；BlueCaster 自己的更新检查由它的设置决定。
- BlueCaster 后台未运行时，EncoDeck 只检查它的单实例互斥体（打开后立即关闭）：安装了 BlueCaster 时每 3 秒一次，否则每 15 秒一次；不打开管道，也不占用适配器。
- 连接后由 BlueCaster 推送状态变化；EncoDeck 不订阅链路质量和日志等高频数据。

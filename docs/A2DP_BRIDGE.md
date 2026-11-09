# A2DP Bridge 可选组件

在 EncoDeck 的“实验室”页可以下载、启动和控制 [BlueCaster](https://github.com/SeiyaFunaokaJP/BlueCaster)（3.0 起更名，原名 A2DP Windows Bridge，简称 A2DPWB）。它让一个**独立的 USB 蓝牙适配器**脱离 Windows 蓝牙，用自己的用户态蓝牙协议栈以 LDAC、LHDC V5、aptX HD、aptX LL、AAC、SBC XQ 等编码向耳机发送声音，**不需要测试签名驱动，也不改动安全启动或内存完整性设置**。

## 免责声明

BlueCaster（原 A2DP Windows Bridge）是 Seiya Funaoka 的第三方开源项目（Apache-2.0 / MIT）。EncoDeck 分发的组件是其官方构建的**未修改副本**；其中静态链接的 BTstack 仅许可**个人、非商业用途**。耳机兼容性、无线质量和适配器固件不在 EncoDeck 控制范围内，请自行评估并承担使用风险；EncoDeck 及其开发者在适用法律允许的范围内不对由此造成的损失承担责任。

## 与 LHDC 组件的区别

| | LHDC 组件 | A2DP Bridge |
| --- | --- | --- |
| 工作方式 | 在 Windows 自带蓝牙上替换音频驱动 | 接管一个独立 USB 适配器，自带协议栈 |
| 额外硬件 | 不需要 | 需要第二个 USB 蓝牙适配器 |
| 系统设置 | 需要测试签名模式、关闭内存完整性 | 不需要；只为该适配器分配微软签名的 WinUSB 驱动 |
| 编码 | LHDC V5 | LDAC、LHDC V5、aptX 系列、AAC、SBC XQ 等 |
| Windows 是否看得到耳机 | 看得到，EncoDeck 照常控制 | 只经 Bridge 连接时看不到；A2DPWB 2.0.2 起 EncoDeck 经 Bridge 控制（见下文） |

两者互不依赖，可以只用其中一个。LHDC 组件不需要额外硬件，所以在实验室页排在前面。卡片标题下的编码标记与主页相同：实心为上游已支持，描边为上游仍属实验性（aptX HD、aptX LL、Opus）。

## 为什么以前 EncoDeck “识别不到”耳机

BlueCaster 通过它自己的适配器连接耳机，这条连接对 Windows 蓝牙是不可见的。EncoDeck 原来只看 Windows 的蓝牙状态，所以耳机明明在播放，首页却显示“未连接”。

现在 EncoDeck 会连接 BlueCaster 后台的本地管道（仅当前用户可访问），读取它正在连接的耳机地址，与 Windows 中配对过的耳机对应起来。只在 BlueCaster 中配对过的耳机（Windows 从未配对，或已从 Windows 蓝牙中删除）按 BlueCaster 的连接配置列为一台设备，BlueCaster 后台运行时出现在首页和“设备”页；同一副耳机也在 Windows 中配对时，以 Windows 中的设备为准。

- 首页和快捷面板的连接状态旁显示 **A2DP Bridge** 徽标，编码标签显示 Bridge 实际使用的编码（如 LDAC）；悬停徽标可查看码率、采样率，以及耳机设置经哪条连接控制。
- 只经 Bridge 连接时，Windows 看不到耳机。BlueCaster（及 A2DPWB **2.0.2 及以上**）可以替 EncoDeck 打开到耳机的控制通道（RFCOMM）：EncoDeck 经 Bridge 读取电量、更改耳机设置，首页照常显示“已连接”和 A2DP Bridge 徽标。电脑上唯一的蓝牙适配器交给 Bridge 后（Windows 不再有蓝牙），同样可以这样控制。控制通道只在 Bridge 以经典蓝牙（A2DP）向这副耳机推流期间可用，断开推流时一并关闭；只有 LE（GATT）控制通道的耳机仍无法经 Bridge 控制。
- A2DPWB 2.0.1 及更早版本不能转发控制通道：只经 Bridge 连接时，EncoDeck **读不到电量、也无法更改耳机设置**，状态显示“已连接（仅音频）”，首页给出一条简短提示。
- 支持**双设备连接**的耳机可以同时连接这台电脑的自带蓝牙：EncoDeck 优先通过 Windows 控制耳机，声音仍走 Bridge。此时请把 Windows 的默认输出设为其他设备（或让 BlueCaster 采集虚拟声卡），避免两路声音同时发往耳机。

## 开始之前

- 一个**独立的** USB 蓝牙适配器（与电脑自带蓝牙分开）。上游验证过 TP-Link UB500（Realtek RTL8761BU）。
- Windows 10 22H2 或 Windows 11（x64 或 ARM64）。
- 非商店版 EncoDeck 才能下载和启动组件。商店版不会下载或启动其他程序，但会自动连接你自行安装并启动的 BlueCaster，并显示状态、控制连接。

**不要把 WinUSB 分配给电脑自带的蓝牙适配器。** 分配后这台电脑上的蓝牙设备都会断开。EncoDeck 会识别 Windows 正在使用的适配器，不提供对它的分配操作。

## 一、获取组件

打开“实验室”页的 **A2DP Bridge** 卡片：

- 如果已经自行安装过 BlueCaster，EncoDeck 会直接使用它（它会自行更新）。
- 否则点击 **“下载组件”**（约 10 MB）。下载不会更改任何驱动。

## 二、首次设置

点击“启动后台”，然后在 EncoDeck 卡片中完成：

1. 展开 **“适配器”**，为独立适配器点击 **“分配 WinUSB”**（Windows 会请求管理员授权）。Windows 正在使用的适配器不会提供这个按钮。
2. Realtek 适配器需要固件文件（如 `rtl8761bu_fw.bin`、`rtl8761bu_config.bin`），从 [linux-firmware](https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/tree/rtl_bt) 下载**原始文件**后放入设置文件夹（“打开设置文件夹”），再点击“刷新”。
3. 点击 **“打开 BlueCaster”**，在它的窗口中添加耳机、选择编码并连接（耳机需处于配对模式，首次连接会通过独立适配器完成配对）。采集单个应用、虚拟声卡或调整码率等进阶设置也在那里修改。

也可以直接使用 BlueCaster 自己的首次设置向导完成以上全部步骤。耳机、连接配置、编码和音量只在 BlueCaster 窗口中管理，EncoDeck 不另外保存一份，两边不会不一致。

## 三、日常使用

- 在 BlueCaster 窗口中连接 / 断开耳机、切换连接配置和调节耳机音量。EncoDeck 卡片显示后台和适配器状态，正在播放时显示编码、码率、采样率和音源。
- BlueCaster 默认采集 Windows 正在播放的声音。要传输 96 kHz LDAC，请在 Windows 声音设置中把播放设备格式设为 96 kHz。
- 开启 **“EncoDeck 启动时同时启动 BlueCaster 后台”** 后，EncoDeck 随开机启动时也会在后台启动 BlueCaster（不打开它的窗口）。
- BlueCaster 后台有自己的托盘图标。关闭 EncoDeck 不会中断播放；点击“退出后台”会停止播放并释放适配器。

## 四、更新、停用与卸载

- **更新**：有新的组件版本时，卡片会显示“更新组件”（打开实验室页时每天最多检查一次，也可以点击“检查更新”），不必等 EncoDeck 新版本。后台空闲时会从新版本自动重新启动；正在播放时，新版本在下次启动后台时生效。更新完成后旧版本即被删除。你自行安装的 BlueCaster 由它自己检查更新。
- **从 A2DPWB 2.0.x 更新**：已安装或正在运行的 A2DPWB 2.0.x 仍可照常使用，EncoDeck 同时识别新旧两种程序、管道和互斥体名称。组件更新到 BlueCaster 后，它首次启动时会把设置文件夹 `%APPDATA%\A2DPWB` 移到 `%APPDATA%\BlueCaster`，配对、固件和设置都会保留；适配器的 WinUSB 分配不受影响。
- **停用**：在“适配器”中点击 **“恢复驱动”**（需要管理员授权），适配器回到 Windows 蓝牙；再点击“退出后台”。
- **卸载组件**：点击 **“卸载组件”**。EncoDeck 会退出它管理的后台，可以勾选一并把 BlueCaster 占用的全部适配器交还给 Windows（一次管理员授权，使用 BlueCaster 自带的驱动工具），移除指向这份副本的开机启动项，然后删除文件。BlueCaster 的设置文件夹（`%APPDATA%\BlueCaster`，2.0.x 为 `%APPDATA%\A2DPWB`：配对、固件、日志）会保留。你自行安装的 BlueCaster 请在 Windows 设置的“已安装的应用”中卸载。
- 卸载 EncoDeck 本身不会自动恢复驱动；如果已经卸载，可以用 BlueCaster 的“恢复驱动”，或在设备管理器中卸载该适配器后重新插拔。

## 五、遇到问题怎么办

| 现象 | 处理方法 |
| --- | --- |
| 卡片显示“还没有交给 BlueCaster 的适配器” | 插入独立适配器，在“适配器”中分配 WinUSB，或在 BlueCaster 窗口完成首次设置。 |
| 提示缺少固件或固件无效 | 下载**原始**文件（不是网页），放入设置文件夹后点击“刷新”。 |
| 通信协议不兼容 | BlueCaster 与 EncoDeck 版本不匹配：更新 EncoDeck，或在实验室更新组件。 |
| 首页仍显示未连接 | 确认 BlueCaster 后台在运行、耳机在 BlueCaster 中有连接配置并正在推流（EncoDeck 按蓝牙地址对应）。只在 BlueCaster 中配对过的耳机，需要 BlueCaster 后台运行时才会出现在 EncoDeck 中。 |
| 后台 15 秒内没有响应 | 点击“打开 BlueCaster”查看原因，或查看它设置文件夹中的日志。 |

反馈问题时，EncoDeck 的“导出诊断”会一并附上 BlueCaster 的诊断信息（蓝牙地址已遮盖）。

## 组件分发与校验

组件与 EncoDeck 应用分开发布：

- **直接取自官方 Release**：BlueCaster 官方 portable 包从 [BlueCaster 的 Release](https://github.com/SeiyaFunaokaJP/BlueCaster/releases) 原样下载，不经 EncoDeck 转发，不重新编译、不修改。
- **应用内固定校验值**：应用内置的 `src/EncoDeck.Platform/Components/components.json` 固定首次下载的版本及每个包的大小和 SHA-256，内容必须与固定值一致才会解压。中断的下载会续传。
- **更新跟随官方最新版本**：更新检查读取 BlueCaster 官方的 Release 列表，取其中最新的一个（不限主版本，测试版也包括在内；只向主动开启的用户推送的 alpha 版本不包括）。每个包都必须在该 Release 中存在，并有 GitHub 给出的 SHA-256 摘要；下载按这个摘要校验，并核对包内记录的提交与该标签一致。如果以后的主版本改变了通信协议，后台会在握手时拒绝连接，卡片显示“通信协议不兼容”。更新 EncoDeck 不会重新下载组件。更新前旧组件继续可用；更新完成后旧版本即被删除（正在运行的副本退出后再删除）。
- 上游源码与提交见包内的 `licenses/README.txt`（2.0.x 为 `SOURCE.txt`），许可见包内的 `licenses` 文件夹。

## 资源占用

- EncoDeck 只在下载组件、打开实验室页时的组件更新检查（每天最多一次）和点击“检查更新”时联网；BlueCaster 自己的更新检查由它的设置决定。
- BlueCaster 后台未运行时，EncoDeck 只检查它的单实例互斥体（打开后立即关闭）：安装了 BlueCaster 时每 10 秒一次，否则每 30 秒一次；不打开管道，也不占用适配器。
- 连接后由 BlueCaster 推送状态变化；EncoDeck 不订阅链路质量和日志等高频数据。

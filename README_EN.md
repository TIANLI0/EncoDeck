<p align="center">
  <img src="src/EncoDeck.App/Assets/EncoDeck-256.png" width="88" alt="EncoDeck" />
</p>

<h1 align="center">EncoDeck</h1>

<p align="center">Manage your earbuds on Windows — no more reaching for your phone just to switch noise cancelling.</p>

<p align="center">
  <a href="https://apps.microsoft.com/detail/9PN6D8G1K9WZ?mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="190" alt="Get it from Microsoft Store" />
  </a>
</p>

<p align="center">
  <a href="https://github.com/TIANLI0/EncoDeck/releases">Download installer</a> ·
  <a href="README.md">简体中文</a> ·
  <a href="docs/COMPATIBILITY.md">Compatibility</a> ·
  <a href="docs/DEVELOPMENT.md">Development</a>
</p>

> 🍎 **On a Mac?** We recommend [HeyBuds](https://github.com/abaichuan1017/HeyBuds).

EncoDeck is a Windows earbud control app built with **WinUI 3**. It brings the everyday settings from HeyMelody to your PC: battery levels for the left and right earbuds and the case, noise control, EQ and spatial audio, touch controls and more. Most of the time it can live as just a tray icon, without keeping the main window open.

**OPPO / OnePlus** earbuds are the primary target, with partial support for some **realme / DIZO, vivo / iQOO, HUAWEI, Xiaomi / Redmi, Sony, AKG, EDIFIER and MOONDROP** models. Available settings vary by model. **Hardware testing is currently done mainly on the OPPO Enco X4**; other models are implemented from protocol and model configuration data, and test reports are welcome.

## Screenshots

<p align="center">
  <img src="docs/images/popup-demo.gif" width="300" alt="Connection popup" />
</p>

| Main window | Tray quick panel |
| :---: | :---: |
| <img src="docs/images/home.png" alt="Main window" /> | <img src="docs/images/quick-panel.png" alt="Tray quick panel" /> |

## Features

### Battery and connection status

Once your device connects, the main window and tray panel show its reported battery readings: left/right earbuds and case where supported, or a single battery group for headphones. Charging, wear state, in-case state and firmware information vary by model.

The case usually stops reporting battery once the earbuds leave it, so EncoDeck keeps the last reading and labels it as "last read" instead of passing stale data off as live. You also get a low-battery reminder.

### Noise control

Switch right from the tray, without opening the main window:

- **Noise cancelling / Transparency / Off / Adaptive** (depending on the model);
- Noise cancelling strength: **Deep, Moderate, Light, Smart**;
- Adaptive-mode noise preference (where supported).

If you change noise control with an earbud gesture or from your phone, EncoDeck reads the state reported by the earbuds and updates the UI. Commands are sent over Bluetooth, then confirmed and read back from the earbuds; if one fails, the button won't stay stuck in the wrong state.

You can also bind global hotkeys under Settings → Hotkeys, e.g. to toggle between noise cancelling and transparency or jump straight to a specific mode. They work in full-screen games too.

### Sound

Sound options follow each model's configuration and actual capabilities.

| Setting | What it does |
| --- | --- |
| **EQ** | Switch the earbuds' built-in presets, such as Dynaudio-related presets, Vocal, Treble, Bass (names vary by model) |
| **Custom EQ** | Edit, save, switch and delete EQ on supported OPPO models; the Enco X4 supports up to 3 profiles of 10 bands each |
| **BassWave** | Toggle dynamic bass and adjust its strength |
| **Spatial audio** | Switch between off, fixed spatial audio and head tracking (if supported) |
| **Game mode** | Toggle low-latency mode and game sound enhancement; conflicts with other sound settings follow the earbuds' own rules |

Some of these settings can already be read from real hardware, but writes are still mostly implemented from the HeyMelody protocol and haven't all been verified on hardware yet. See the [compatibility table](docs/COMPATIBILITY.md) for per-item details.

### Earbud controls and device management

On supported models you can also change these earbud-side settings:

- **Touch gestures**: set pinch, long pinch, swipe and call gestures separately for each earbud; choose which noise modes a long pinch cycles through.
- **Wear-related settings**: wear detection, pause when asleep, and on supported models nod to answer / shake to decline.
- **Find my earbuds / fit test**: trigger the earbuds' built-in features; you'll get a volume warning before finding your earbuds.
- **Prompt tones**: adjust the earbuds' prompt tone volume.
- **Dual-device connection**: view remembered devices and, depending on the model, connect, disconnect, remove them or set a preferred device. Disconnecting this Windows PC itself is still handled in the system Bluetooth settings.

This is the *range of implemented features*: not every earbud supports all of them, and not every action has been tested on real hardware.

### Windows desktop experience

**Tray and Quick Settings**

- Left-click the tray icon for a quick panel with battery and noise control; right-click for a native system menu.
- On supported Windows 11 setups, when you open Quick Settings with **Win + A**, the EncoDeck panel can attach above the Quick Settings flyout; this can be turned off in Settings.
- Background running, launch at startup, global hotkeys and light / dark themes.

**Connection popup and device model**

- When your earbuds connect to the PC, a popup near the taskbar shows the device name, battery levels and that model's connection animation; it stays out of the way while you're in full screen.
- The popup animation doesn't open an audio output or show up in Windows media controls, so it won't pull your earbuds' audio away from your phone to the PC.
- The home page shows a picture or model of your device. Pre-rendered models can be dragged left and right; models with `.vfxms` support can be rotated, zoomed and reset in real time.
- Device artwork is fetched per model and color, then cached locally for reuse. Headphones without an official animation keep a static product image; only the popup window animates in and out.

*The connection popup fires when the earbuds establish a Bluetooth connection; BLE lid-open detection (popping up when you open the case nearby) is not implemented yet.*

## Compatibility

Support currently falls into two groups: models with configuration data from the official app to work from, and models ported from open-source protocol implementations. **A model being in the code does not mean those earbuds have been tested on real hardware.**

| Brand / model | Current support | Verification |
| --- | --- | --- |
| **OPPO Enco X4** | Battery, wear state, noise control and levels, plus EQ, spatial audio, gestures, dual connection and more | Battery and some noise control actions read/written on hardware; many advanced settings read on hardware, writes still being verified item by item |
| **Other OPPO / OnePlus** | ~85 built-in model configurations; noise control, sound and touch settings per model capabilities | Mostly based on HeyMelody configuration and protocol; not yet tested on hardware |
| **realme / DIZO and some older OPPO / OnePlus** | ~50 more models, mainly battery, charging, wear state and firmware info | Not yet tested on hardware |
| **vivo / iQOO** | ~43 models; battery and status, plus noise control, EQ, find earbuds and game mode on some models | Protocol port, not verified on hardware |
| **HUAWEI** | 24 models; battery and model-specific ANC/levels, EQ, wear detection, low latency and multipoint; independent spatial audio/head tracking after device confirmation | Protocol port and official APK analysis; not verified on hardware |
| **Xiaomi / Redmi** | Series matching with 48 official model capability records; battery, ANC/levels, EQ, low latency, multipoint, spatial audio/head tracking and prompt volume gated by model data and device readback | Official APK analysis; not verified on hardware |
| **Sony** | 46 built-in model names; battery/charging, ANC/ambient sound and device-reported EQ presets | Official MDR protocol and simulated-device tests; not verified on hardware |
| **AKG** | N400NC, N700NCM2, N200NC, Y600: battery, ANC/ambient and automatic play/pause; N700NC, N5 Hybrid, N9 Hybrid: recognition/artwork only | Official APK analysis; not verified on hardware |
| **EDIFIER** | W820NB, W200BT and generic detection; battery, noise cancelling / transparency on some models | Protocol port, not verified on hardware |
| **MOONDROP** | 17 model profiles plus generic recognition, not complete model support; GAIA BLE / RFCOMM, battery, model-specific ANC / anti-wind and gain; runtime-discovered LED, prompt switch / volume, both-ear TOUCHV2 and mutually exclusive LHDC / multipoint; experimental LDAC / LC3, wear detection, spatial audio / head tracking, dynamic bass and channel swap | EDGE firmware 1.3.0: Windows RFCOMM control connection and basic state reads verified on hardware; new settings and other models remain protocol ports / simulated validation; inferred and new controls require opt-in |

For more models, per-feature details and verification records, see the **[device compatibility list](docs/COMPATIBILITY.md)**. If your earbuds aren't on the tested list but work fine, please share the model, firmware and your results.

## Download

| | Microsoft Store | GitHub installer |
| --- | --- | --- |
| Platform | x64 / ARM64 | x64 |
| Updates | Automatic via Microsoft Store; can also check in-app | Checks GitHub Releases in-app; installs after you confirm |
| Install | [Open Store](https://apps.microsoft.com/detail/9PN6D8G1K9WZ) | [Download from Releases](https://github.com/TIANLI0/EncoDeck/releases) |

**Requirements: Windows 10 19041+, Windows 11 recommended.**

The standalone installer is named `EncoDeck-Setup-<version>-win-x64.exe`. It installs to `%LOCALAPPDATA%\Programs\EncoDeck` by default and usually doesn't need admin rights; to upgrade, just install the new version over the old one — settings and cache are kept.

Before using it, **pair and connect your earbuds in Windows Bluetooth settings**, then start EncoDeck. Device artwork and animations for some models need an internet connection the first time.

### LHDC V5 (optional / experimental)

The non-Store x64 build can optionally integrate [LHDC-Win](https://github.com/Wayne-u/LHDC-Win). After downloading the separate component from the Sound page, you can pick an audio device, set playback parameters, and try enabling LHDC or restoring the original Windows audio configuration.

This is not a regular earbud control feature: it involves test-signed drivers, admin rights and system audio configuration, and requires Windows 11 22H2+, x64 and earbuds that support LHDC V5. **The Store version doesn't support it, and real-world playback compatibility is still unverified on hardware.** EncoDeck never changes Secure Boot or system test mode on its own. Read the [LHDC guide and recovery steps](docs/LHDC.md) before trying it.

## Building

Requires .NET SDK 10.0.100 or later. Visual Studio is not needed for a regular build.

```powershell
git clone https://github.com/TIANLI0/EncoDeck.git
cd EncoDeck

dotnet test tests/EncoDeck.Tests
pwsh build/build-installer.ps1
```

The installer is written to `artifacts/installer`. See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for the dev environment, project layout and other build commands.

The repo also ships a command-line diagnostic tool, **EncoDeck.Probe**, for inspecting Bluetooth devices and SDP services, reading earbud state and listening to protocol events. Bluetooth addresses are redacted by default; see [TESTING.md](docs/TESTING.md) for usage.

## Reporting issues

If your earbuds won't connect, a setting doesn't take effect or the model is misidentified, please open an [issue](https://github.com/TIANLI0/EncoDeck/issues). Include the earbud model, firmware version, Windows version and steps to reproduce, plus a Probe report if you can.

Protocol research, device testing and code contributions are all welcome. Before contributing, have a look at [CONTRIBUTING.md](CONTRIBUTING.md) and the [protocol findings](docs/PROTOCOL_FINDINGS.md).

## Acknowledgements

Earbud protocol and device support draw on [OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows), [OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager), [OppoPods](https://github.com/Leaf-lsgtky/OppoPods) and [HeyBuds](https://github.com/abaichuan1017/HeyBuds); the tray uses [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon). Other third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
MOONDROP model data, GAIA framing and commands draw on [MiuixMoondrop](https://github.com/huime180/MiuixMoondrop). See the compatibility list for supported features and limitations.

## License

[GPL-3.0-only](LICENSE).

EncoDeck is an unofficial open-source project with no affiliation, partnership or endorsement from the brands above or their manufacturers. Brand and product names are used only to describe compatibility.

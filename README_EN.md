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
  <a href="https://github.com/TIANLI0/EncoDeck/releases/latest">Download installer</a> ·
  <a href="README.md">简体中文</a> ·
  <a href="docs/COMPATIBILITY.md">Compatibility</a> ·
  <a href="docs/DEVELOPMENT.md">Development</a>
</p>

> 🍎 **On a Mac?** We recommend [HeyBuds](https://github.com/abaichuan1017/HeyBuds).

EncoDeck is a Windows earbud control app built with **WinUI 3**. It brings everyday earbud settings to your PC: battery levels for the left and right earbuds and the case, noise control, EQ and spatial audio, touch controls and more. Most of the time it can live as just a tray icon, without keeping the main window open.

**OPPO / OnePlus** earbuds are the primary target, with partial support for some **realme / DIZO, vivo / iQOO, HUAWEI, HONOR, Xiaomi / Redmi, Sony, AKG, Sennheiser, EDIFIER, MOONDROP, Samsung, Google, Bose and Nothing / CMF** models, plus USB 2.4G gaming headsets. Available settings vary by model. **Hardware testing is currently done mainly on the OPPO Enco X4**; other models are implemented from protocol and model configuration data, and test reports are welcome.

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

Some of these settings can already be read from real hardware, but writes are still mostly implemented from the protocol and haven't all been verified on hardware yet. See the [compatibility table](docs/COMPATIBILITY.md) for per-item details.

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
- Device artwork is fetched per model and color, then cached locally for reuse. Headphones without a manufacturer animation keep a static product image; only the popup window animates in and out.

*The connection popup fires when the earbuds establish a Bluetooth connection; BLE lid-open detection (popping up when you open the case nearby) is not implemented yet.*

## Compatibility

Each brand is implemented from its protocol and model configuration data, some ported from open-source projects. **A model being in the code does not mean those earbuds have been tested on real hardware.**

| Brand / model | Current support | Verification |
| --- | --- | --- |
| **OPPO Enco X4** | Battery, wear state, noise control and levels, plus EQ, spatial audio, gestures, multipoint and more | Battery and some noise control actions read/written on hardware; many advanced settings read on hardware, writes still being verified item by item |
| **Other OPPO / OnePlus** | ~85 built-in model configurations; noise control, sound and touch settings per model capabilities | Based on model configuration and protocol; not yet tested on hardware |
| **realme / DIZO and some older OPPO / OnePlus** | ~50 more models, mainly battery, charging, wear state and firmware info | Not yet tested on hardware |
| **vivo / iQOO** | ~43 models; battery and status, plus noise control, EQ, find earbuds and game mode on some models; gestures, wear-detection switch and the multipoint device list after read-back (TWS 3e / TWS 5 from community hardware reports, other models experimental); BLE when there is no classic control service | Protocol port; some features have community hardware reports, not verified by EncoDeck |
| **HUAWEI** | 27 models; battery and model-specific ANC/levels, voice-boost transparency, EQ, wear detection, low latency, multipoint and device list; independent spatial audio/head tracking, adaptive volume and head gestures after device confirmation | Protocol port; some features have community hardware reports, not verified by EncoDeck |
| **Xiaomi / Redmi** | Series matching with 48 model capability records; battery, ANC/levels, EQ, low latency, multipoint, spatial audio/head tracking and prompt volume gated by model data and device readback | Protocol implementation; not verified on hardware |
| **Sony** | 46 built-in model names; battery/charging, ANC/ambient sound and device-reported EQ presets; ambient mode and level, Speak-to-Chat, DSEE, pause when taken off, multipoint and touch switches, button assignment and auto power-off after read-back; control over BLE on LE Audio, 10-band gains for the manual / custom EQ | WH-1000XM6 LE Audio control (handshake, battery, ANC) verified on hardware by a contributor; other models: MDR protocol and simulated-device tests |
| **AKG** | N400NC, N700NCM2, N200NC, Y600: over BLE (classic as fallback) battery / charging, firmware, ANC / ambient aware / talk-through, preset EQ and automatic play/pause; N5 / N9 Hybrid: Airoha RACE battery and case battery, ANC experimental; N700NC: recognition/artwork only | Protocol implementation; not verified on hardware |
| **EDIFIER** | W820NB, W200BT / W200BT Plus, W860NB PRO, Evo Pro, FitBuds Turbo, FitClip Ultra, X5 Pro; 28 Atom / Lolli models with identification, SPP / BLE and earbud / case battery; noise / ambient, game mode, button control, sound effect and auto power-off per model after read-back | Protocol implementation, not verified on hardware; [scope](docs/COMPATIBILITY.md#漫步者) |
| **MOONDROP** | 18 model profiles plus generic recognition, not complete model support; GAIA BLE / RFCOMM, battery, model-specific ANC / anti-wind and gain; runtime-discovered LED, prompt switch / volume, both-ear TOUCHV2, mutually exclusive LHDC / multipoint, factory EQ presets and the user PEQ; experimental LDAC / LC3, wear detection, spatial audio / head tracking, dynamic bass and channel swap | EDGE firmware 1.3.0: Windows RFCOMM control connection and basic state reads verified on hardware; EDGE and SPACE TRAVEL 2 EQ reads / writes have community hardware reports; new settings and other models remain protocol ports / simulated validation; inferred and new controls require opt-in |
| **HONOR** | 9 models (incl. HONOR CHOICE X5 / X5 Pro); battery, ANC / transparency and levels, wear detection, multipoint and device-reported EQ presets (per model) | Protocol implementation; not verified on hardware |
| **Sennheiser** | 13 models with battery and firmware; per-model ANC / adaptive / transparency, anti-wind, Smart Pause, user EQ and paired-device list on MOMENTUM 4 / 5, ACCENTUM and HDB 630; MOMENTUM TW 4 ANC, anti-wind, wear detection, low latency and touch customization | Protocol implementation and simulated-device tests; some features have community hardware reports, not verified by EncoDeck |
| **Samsung** | Galaxy Buds2, Buds2 Pro, Buds4 Pro: earbud and case battery, off / ANC / transparency, preset EQ; adaptive mode and ANC level on Buds4 Pro, touch lock on Buds2 / Buds2 Pro | Protocol port; not verified on hardware |
| **Google** | Pixel Buds Pro, Pixel Buds Pro 2: battery and charging, off / ANC / transparency, preset EQ, firmware version | Protocol port; not verified on hardware |
| **Bose** | QuietComfort 45, QuietComfort Ultra Headphones (2nd Gen): battery, firmware version, Quiet / Aware modes | Protocol port; not verified on hardware |
| **Nothing / CMF** | Nothing Ear (a), Headphone (1), CMF Buds 2, CMF Buds Pro 2: battery and charging, off / ANC / transparency, preset EQ, firmware version | Protocol port; not verified on hardware |
| **USB 2.4G gaming headsets** | 41 models recognized (EDIFIER HECATE, Dareu, Rapoo, MCHOSE, Razer, HyperX, SteelSeries, JBL, Logitech); battery on MCHOSE X9 / V9 PRO, Razer BlackShark V2 Pro / Barracuda family, HyperX Cloud Alpha 2 / Cloud III S, Arctis Nova 7, G PRO X 2, HECATE G3 Max and more | Protocols confirmed on hardware by community projects; not verified by EncoDeck |

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

The non-Store x64 build can optionally integrate [LHDC-Win](https://github.com/Wayne-u/LHDC-Win). After downloading the separate component from the Labs page, you can pick an audio device, set playback parameters, and try enabling LHDC or restoring the original Windows audio configuration.

This is not a regular earbud control feature: it involves test-signed drivers, admin rights and system audio configuration, and requires Windows 11 22H2+, x64 and earbuds that support LHDC V5. **The Store version doesn't support it, and real-world playback compatibility is still unverified on hardware.** EncoDeck never changes Secure Boot or system test mode on its own. Read the [LHDC guide and recovery steps](docs/LHDC.md) before trying it.

### A2DP Bridge (optional / experimental)

The non-Store build can download and manage [A2DP Windows Bridge](https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge) from the Labs page: it takes over a **dedicated USB Bluetooth adapter** and sends audio in LDAC, LHDC V5, aptX HD and more, without test-signed drivers. EncoDeck downloads it (an unmodified copy of the official build), starts its background process, connects and disconnects, sets the volume, manages the adapter's driver, and recognizes earbuds connected through it on the home page.

It needs a second USB Bluetooth adapter; while the earbuds are connected only through the bridge, Windows cannot see them, so battery and earbud settings are unavailable (earbuds with dual-device connection can also connect to the PC's Bluetooth). The Store version only connects to an A2DP Windows Bridge you installed and started yourself. See the [A2DP Bridge guide](docs/A2DP_BRIDGE.md).

The LHDC and A2DP Bridge components have their own releases, so updating EncoDeck does not download them again; each can be checked for updates, updated and uninstalled from the Labs page, and every download is verified by size and SHA-256.

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

### EarFun (development version)

23 GAIA / EF profiles, including EarFun Air Pro 4+ (TW700), are available with battery and firmware reads and model-specific controls gated by device readback and the experimental-features switch. EncoAPI provides 13 exact official product photographs. Hardware verification and EQ readback remain pending; see [compatibility details](docs/COMPATIBILITY.md#earfun--atvel).

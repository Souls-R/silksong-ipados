# Silksong iPadOS

[简体中文](README.md) · English

An unofficial iOS build tool for *Hollow Knight: Silksong*.

Unity IL2CPP converts the IL code in the game's original managed assemblies into C++, which is then compiled into ARM64 machine code and runs with Unity's native iOS runtime.

Rendering uses the native Metal backend, with no instruction emulation or additional graphics API translation and almost no additional performance overhead.

This project does not include any official game assets. You need to build from your own purchased copy of the macOS game.

## Getting Started

You need an **Apple Silicon Mac**. Follow the [build guide](#build-your-own-ipa) to convert game resources, generate the project, compile the IPA, and sign and install it on your iPad.

Touch controls are only partially supported. An external keyboard or controller is required for gameplay.

```text
python3 build.py resources     Convert resources
python3 build.py prepare       Generate the iOS project
python3 build.py native        Build an unsigned IPA
python3 build.py resource-zip   Package resources as a ZIP
python3 build.py mod-pack       Prepare optional skin and HP bar mods
```

Run any command with `--help` to see its options.

## Features

- iOS controller, external keyboard, and mouse support.
- Original graphics and frame rate settings, with support for the iPad's aspect ratio.
- General Animator binding fixes for animation issues.
- Logging and Metal performance overlay options in system Settings.

[Saves and App Updates](#saves-and-app-updates) · [Support and Testing](#support-and-testing)

## Notice

**This project is completely free. Please support the developers and buy the original game.**

Do not sell or pay for IPA packages, paid groups, testing access, or similar offers. If you have paid for any of these, request a refund immediately.

## Dependencies and Sources

| Item | Where to get it |
| --- | --- |
| Hollow Knight: Silksong | Purchase on Steam and download the macOS version |
| Python 3.12 | Install using your preferred method |
| Unity 6000.0.50f1 | Install the Editor and iOS Build Support through Unity Hub |
| Xcode | Open it once after installation to finish installing its components |
| Unity Input System 1.14.2 | Install through Unity Package Manager, or download the [official package](https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-1.14.2.tgz) |
| UnityPy, astc-encoder-py, texture2ddecoder, Pillow, etc. | `python3 -m pip install -r requirements.txt` |
| xcodeproj 1.27.0 | `gem install xcodeproj -v 1.27.0 --no-document --user-install` |

The repository's code is licensed under [MIT](LICENSE).

## Build Your Own IPA

Complete the following steps on an **Apple Silicon Mac**.

Use the Steam macOS release **1.0.30000** and Unity **6000.0.50f1**.

### Get the Game Files

Open Steam and sign in to an account that owns the game. Open the Steam console from Terminal:

```sh
open 'steam://open/console'
```

Then run this in the Steam console:

```text
download_depot 1030300 1030302 4136280015582261500
```

Wait for the console to report that the download has finished. The app is usually saved to:

```text
~/Library/Application Support/Steam/steamapps/content/app_1030300/depot_1030302/Hollow Knight Silksong.app
```

Keep the entire `.app` directory, not just its Data folder.

### Prepare the Tools

1. Install Python 3.12.
2. Install the Apple Silicon version of Unity 6000.0.50f1 through Unity Hub, with iOS Build Support selected.
3. Install and open Xcode to finish installing its components. The native build was tested with Xcode 26.6.
4. Install the Ruby dependency in Terminal: `gem install xcodeproj -v 1.27.0 --no-document --user-install`.

Allow at least 20 GB of free disk space for the build.

### Install Build Dependencies

Open Terminal in the repository directory, then create and activate a Python environment:

```sh
python3.12 -m venv .venv
source .venv/bin/activate
python3 -m pip install -r requirements.txt
```

Download and extract the official Input System 1.14.2 package:

```sh
buildRoot="$HOME/SilksongBuild"
mkdir -p "$buildRoot/input-system-1.14.2"
curl -fL 'https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-1.14.2.tgz' -o "$buildRoot/input-system-1.14.2.tgz"
tar -xzf "$buildRoot/input-system-1.14.2.tgz" -C "$buildRoot/input-system-1.14.2"
```

Run the remaining commands in the same Terminal session.

### Convert Resources and Generate the Project

These commands use the default Steam and Unity Hub paths. Adjust them to match your installation:

```sh
gameData="$HOME/Library/Application Support/Steam/steamapps/content/app_1030300/depot_1030302/Hollow Knight Silksong.app/Contents/Resources/Data"
unityData='/Applications/Unity/Hub/Editor/6000.0.50f1/Unity.app/Contents'
inputSystem="$buildRoot/input-system-1.14.2/package"
resourceOutput="$buildRoot/resources"
nativeOutput="$buildRoot/native"

python3 build.py resources --game-data "$gameData" --output "$resourceOutput" --jobs 2
python3 build.py prepare --game-data "$gameData" --unity-data "$unityData" --input-system "$inputSystem" --resources "$resourceOutput" --output "$nativeOutput" --bundle-id 'org.example.silksong.personal'
```

If resource conversion is interrupted, run the same command to resume. Add `--refresh` to convert everything again.

`prepare` requires an output directory that does not already exist. Choose a new `nativeOutput` path when regenerating the project.

The default version is 0.8.1, build 29. Change these with `--version` and `--build-number`. Keep the same `--bundle-id` when updating an existing installation.

### Build the IPA

Once the project is ready, run:

```sh
python3 build.py native --work "$nativeOutput"
```

The IPA and logs are under `$buildRoot`:

```text
native/output/SilksongNative-0.8.1-unsigned.ipa
native/logs/
```

Running `native` again reuses the build cache in that directory.

### Sign and Install

The generated IPA is unsigned. Install it with a sideloading tool such as [AltStore](https://altstore.io/). Using [Sideloadly](https://sideloadly.io/) as an example:

1. Install Sideloadly.
2. Connect your iPad over USB, unlock it, and select “Trust This Computer.” Select the device in Sideloadly and drag in the IPA.
3. Sign with your own Apple ID, click Start, and follow the installation prompts.
4. If prompted, trust the developer under Settings → General → VPN & Device Management, and enable Developer Mode under Privacy & Security → Developer Mode.

Free account signatures usually last 7 days. You can sign the app again or set up automatic renewal. Use the same Apple ID and bundle identifier when installing an update to keep your resources and saves. See the [Sideloadly FAQ](https://sideloadly.io/faq).

### Import Resources

Resource conversion creates `$resourceOutput/SilksongResources-v1`, containing scenes, textures, music, sound effects, animations, fonts, UI, and other data. The folder is about 2.59 GB. Copy it directly, or package it as a ZIP of about 2.35 GB:

```sh
python3 build.py resource-zip --resources "$resourceOutput" --output "$buildRoot/SilksongResources-v1.zip"
```

1. Open the app once after installing the IPA.
2. Connect your Mac and iPad over USB, then drag the resource ZIP into the Hollow Knight: Silksong app folder.
3. Tap the ZIP in Files on your iPad to extract it. Place the `SilksongResources-v1` folder under On My iPad → Hollow Knight: Silksong. You can also copy the extracted folder directly.
4. Make sure the folder is not nested inside another folder with the same name. The correct structure is shown below. After importing, close the app from the app switcher and reopen it.

```text
Documents/
  SilksongResources-v1/
    pack.json
    resource-index.json
    aa/StandaloneOSX/...
```

### Performance and Diagnostics

After installation, open Hollow Knight: Silksong in iPadOS Settings (under Apps on iPadOS 18 and later) to adjust these options. Close the app from the app switcher and reopen it for changes to take effect:

- Show Notice: show the notice every time the game starts.
- Check Resources on Next Launch: use after importing new resources or when troubleshooting.
- Diagnostic Logs: write resource loading and other logs to a file.
- Performance Overlay: show frame rate, frame time, and GPU time.
- Show Startup Details: display diagnostic text on screen.

The notice is shown by default. Set “On Next Launch” to “Go Straight to Game” on the notice page to skip it. Logs and the performance overlay are off by default.

To troubleshoot, enable Diagnostic Logs, restart the app, reproduce the issue, and export `native.log` from the app's Documents folder.

If a build fails, check the logs in the output directory. If a patch target does not match, check the game and Unity versions first.

## Saves and App Updates

### Save Location

Saves are stored in the app's `Documents/default/` folder. On your iPad, open Files → On My iPad → Hollow Knight: Silksong → default. Files `user1.dat` through `user4.dat` correspond to save slots 1 through 4. You can edit them with a save editor.

Close the game completely from the app switcher before backing up or replacing saves. Copy the entire `default` folder to back up all slots, and put it back in the same location to restore them.

### Update the App

Install the new IPA over the existing app using the same signing account and bundle identifier to keep your saves and resources. Resources usually do not need to be imported again.

Changing the signing account or bundle identifier may prevent an update from replacing the existing app. Back up your saves and resources first, then transfer them manually if a fresh installation is needed.

**Do not uninstall the app before updating. Uninstalling deletes its game resources and saves.**

## Support and Testing

Current release: **0.8.1 / build 29 test version**.

| Item | Current scope |
| --- | --- |
| Game files | Steam macOS release 1.0.30000 |
| Build environment | Apple Silicon Mac, Unity 6000.0.50f1, Python 3.12, Xcode |
| System requirements | Minimum build target: iOS / iPadOS 16.0 |
| Tested device | iPad Pro 11-inch (2021, M1) |
| iPadOS 16 testing | iPad with M2 and 8 GB RAM; reported working with an IPA rebuilt from [PR #1](https://github.com/Souls-R/silksong-ipados/pull/1) |
| Controls | External keyboard or controller for gameplay, with mouse button binding; partial touch support |
| Startup UI and app name | Simplified Chinese, Traditional Chinese, and English, following the system language |

Before using a mouse, turn off **Settings → Accessibility → Touch → AssistiveTouch** on your iPad to prevent left clicks from being converted into screen taps.

## Optional Mods (Experimental Branch)

HPBarPro 1.0.4 and PNG skins using Customizer / CustomizerT2D naming are supported experimentally.
Plugins and supported Harmony hooks are woven before IL2CPP compilation; skins become compressed
iOS texture overlays. Desktop BepInEx is not installed on the device, and arbitrary DLL loading is unsupported.

Use `build.py mod-pack --game-data <Data> --resources <converted-folder-or-zip> --skin <active-skin>
--hpbar <HPBarPro.zip> --output <mod-output>`, then add `--mods <mod-output>/inputs` to `prepare`.
The skin folder must contain `active.txt`. Static weaving requires a .NET 8 or newer SDK, or a
host-specific standalone executable supplied with `--mod-weaver`.

Install the matching IPA, quit the game, and extract the complete `Mods` folder into Documents
beside `SilksongResources-v1`. The original resource pack and saves remain usable. Missing,
partial or incompatible data disables mods. Enable diagnostic logs in system Settings and restart
to inspect the reason in `native.log`. Remove `Mods`
or create `Mods/disabled.txt` and restart to return to the original game. HP bar configuration
is created in `Mods/config/`; restart after editing it. Adding plugin DLLs requires rebuilding.
Device validation of animations, health bars, scene transitions and memory use is still required.

## Release Notes

### 0.8.1

- Automatically requests a pause when switching apps or losing focus. The pause menu stays open on return until the player resumes manually.
- Repeated notifications do not resume gameplay; an existing pause menu or settings submenu stays open.

### 0.8.0

- Fixed DualSense (PS5) touchpad button press and release input, preserving the game's original input and button binding logic.

### 0.7.9

- Lowered the minimum deployment target to iOS / iPadOS 16.0.
- Added mouse menu clicks and in-game button binding.
- Fixed missed mouse button releases and unintended menu clicks after rebinding.
- Fixed the Chinese font used by the binding prompt.

### 0.7.4

- Fixed the brief white screen at startup.
- Added controller rumble support, preserving the original game's vibration toggle and intensity settings.

### 0.7.2

- Added a startup notice styled after the original game, with an option to go straight to the game on future launches.
- Uses the original app icon. The app name and startup UI support Simplified Chinese, Traditional Chinese, and English.
- Added system Settings options for the notice, resource checks, diagnostic logs, performance overlay, and startup details.

### Feedback

Include your device, OS and app versions, and steps to reproduce the issue. Attach diagnostic logs when needed.

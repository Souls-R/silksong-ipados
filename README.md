# Silksong iPadOS

简体中文 · [English](README_EN.md)

《Hollow Knight: Silksong》非官方 iOS 构建工具。

与模拟器或兼容层等方案不同，我们可以直接通过 Unity IL2CPP 将游戏原版的托管程序集的 IL 中间代码转换为 C++，再编译生成 ARM64 机器码，配合 Unity 的 iOS 原生运行时执行。

渲染侧使用原生 Metal 后端，无需模拟指令或额外的图形 API 转译层，几乎没有额外性能损失。

本项目不包含任何官方游戏资源，你需要从你所购买的 macOS 版本游戏包体开始编译。

## 开始构建

你需要一台 **Apple Silicon Mac**。按[构建教程](#构建自己的-ipa)在 Mac 上完成游戏资源转换、工程生成、IPA 编译和签名安装。

游戏内操作仅部分支持触屏，你需要外接键盘或手柄游玩。

```text
python3 build.py resources     转换资源
python3 build.py prepare       生成 iOS 工程
python3 build.py native        编译未签名 IPA
python3 build.py resource-zip   打包资源 ZIP
```

运行对应命令的 `--help` 查看参数。

## 功能

- iOS 手柄与外接键盘。
- 原版画质菜单、帧率选项和 iPad 画幅适配。
- 修复了部分动画错误，通用 Animator 绑定修复。
- 在系统设置中配置日志记录和 Metal 性能面板选项。

[存档与程序更新](#存档与程序更新) · [支持与测试范围](#支持与测试范围)

## 声明

**本项目完全免费，请支持正版并购买原游戏。**

请勿倒卖或购买IPA包、付费群或测试资格等，如你已付费购买请立即退款。

## 依赖与来源

| 项目 | 获取方式 |
| --- | --- |
| Hollow Knight: Silksong | 在 Steam 购买并下载 macOS 版 |
| Python 3.12 | 使用你喜欢的方式安装 |
| Unity 6000.0.50f1 | 通过 Unity Hub 安装编辑器和 iOS Build Support |
| Xcode | 安装后打开一次，完成组件安装 |
| Unity Input System 1.14.2 | 通过 Unity Package Manager 安装，或下载[官方包](https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-1.14.2.tgz) |
| UnityPy、astc-encoder-py、texture2ddecoder、Pillow 等 | `python3 -m pip install -r requirements.txt` |
| xcodeproj 1.27.0 | `gem install xcodeproj -v 1.27.0 --no-document --user-install` |

仓库代码采用 [MIT](LICENSE)。

## 构建自己的 IPA

以下步骤需要在 **Apple Silicon Mac** 上完成。

游戏文件使用 Steam macOS 版 **1.0.30000**，构建使用 Unity **6000.0.50f1**。

### 获取游戏文件

打开 Steam，登录已购买游戏的账号。在终端打开 Steam 控制台：

```sh
open 'steam://open/console'
```

然后在 Steam 控制台执行：

```text
download_depot 1030300 1030302 4136280015582261500
```

等待控制台提示下载完成，目录通常为：

```text
~/Library/Application Support/Steam/steamapps/content/app_1030300/depot_1030302/Hollow Knight Silksong.app
```

保留完整 `.app` 目录，不要只复制其中的 Data 文件夹。

### 准备工具和输入

1. Python 3.12。
2. 通过 Unity Hub 安装 Apple Silicon 版 Unity 6000.0.50f1，并勾选 iOS Build Support。
3. 安装并打开 Xcode，完成组件安装。本项目原生编译验证环境使用了 Xcode 26.6。
4. 在终端安装 Ruby 依赖：`gem install xcodeproj -v 1.27.0 --no-document --user-install`。

建议预留至少 20 GB 构建空间。

### 安装构建依赖

在仓库目录打开终端，创建并启用 Python 环境：

```sh
python3.12 -m venv .venv
source .venv/bin/activate
python3 -m pip install -r requirements.txt
```

下载并解压官方 Input System 1.14.2：

```sh
buildRoot="$HOME/SilksongBuild"
mkdir -p "$buildRoot/input-system-1.14.2"
curl -fL 'https://download.packages.unity.com/com.unity.inputsystem/-/com.unity.inputsystem-1.14.2.tgz' -o "$buildRoot/input-system-1.14.2.tgz"
tar -xzf "$buildRoot/input-system-1.14.2.tgz" -C "$buildRoot/input-system-1.14.2"
```

后续命令在同一个终端中执行。

### 转换资源、生成工程

下面使用 Steam 和 Unity Hub 的默认路径，按实际安装位置修改：

```sh
gameData="$HOME/Library/Application Support/Steam/steamapps/content/app_1030300/depot_1030302/Hollow Knight Silksong.app/Contents/Resources/Data"
unityData='/Applications/Unity/Hub/Editor/6000.0.50f1/Unity.app/Contents'
inputSystem="$buildRoot/input-system-1.14.2/package"
resourceOutput="$buildRoot/resources"
nativeOutput="$buildRoot/native"

python3 build.py resources --game-data "$gameData" --output "$resourceOutput" --jobs 2
python3 build.py prepare --game-data "$gameData" --unity-data "$unityData" --input-system "$inputSystem" --resources "$resourceOutput" --output "$nativeOutput" --bundle-id 'org.example.silksong.personal'
```

资源转换中断后，运行同一命令即可继续；加 `--refresh` 可强制重新转换。

`prepare` 需要一个尚不存在的输出目录。重新生成工程时，换一个 `nativeOutput` 路径。

默认版本为 0.7.2、构建号 16，可用 `--version` 和 `--build-number` 修改。覆盖更新时，沿用原来的 `--bundle-id`。

### 编译 IPA

工程生成完成后，继续执行：

```sh
python3 build.py native --work "$nativeOutput"
```

IPA 和日志位于 `$buildRoot` 下：

```text
native/output/SilksongNative-0.7.2-unsigned.ipa
native/logs/
```

再次运行 `native` 时会复用该目录中的编译缓存。

### 签名安装

生成的 IPA 未签名，需要使用 [AltStore](https://altstore.io/) 等侧载工具安装。以 [Sideloadly](https://sideloadly.io/) 为例：

1. 安装 Sideloadly。
2. 用 USB 连接并解锁 iPad，选择「信任此电脑」。在 Sideloadly 中选择设备，拖入 IPA。
3. 使用自己的 Apple ID 签名，点击 Start，按提示完成安装。
4. 如系统提示，在「设置 → 通用 → VPN 与设备管理」信任开发者，并在「隐私与安全性 → 开发者模式」中开启开发者模式。

免费账号签名通常有效 7 天，可重新签名或配置自动续签。覆盖安装时使用相同 Apple ID 和应用标识，保留资源与存档。详见 [Sideloadly FAQ](https://sideloadly.io/faq)。

### 导入资源

资源转换会生成 `$resourceOutput/SilksongResources-v1` 文件夹，包含场景、纹理、音乐音效、动画、字体和 UI 等数据，展开约 2.59 GB。可直接复制文件夹，也可打包为约 2.35 GB 的 ZIP：

```sh
python3 build.py resource-zip --resources "$resourceOutput" --output "$buildRoot/SilksongResources-v1.zip"
```

1. 安装 IPA 后打开一次应用。
2. 用 USB 连接 Mac 和 iPad，将资源 ZIP 拖入「空洞骑士：丝之歌」的应用文件夹。
3. 在 iPad「文件」中点按 ZIP 解压，把 `SilksongResources-v1` 文件夹放进「我的 iPad → 空洞骑士：丝之歌」。也可以直接复制已解压的文件夹。
4. 确认没有套两层同名目录，正确结构如下。导入完成后，从后台关闭并重新打开应用。

```text
Documents/
  SilksongResources-v1/
    pack.json
    resource-index.json
    aa/StandaloneOSX/...
```

### 性能与诊断

安装后可在 iPadOS「设置 → App → 空洞骑士：丝之歌」中调整以下选项，从后台关闭再打开应用后才会生效：

- 显示声明：开启时每次启动游戏会显示声明。
- 下次启动重新检查资源：导入新资源或排查问题时使用。
- 诊断日志：记录资源加载等日志到文件。
- 性能面板：启用帧率、帧时间与 GPU 面板。
- 显示启动详细信息：显示屏幕上的诊断文字。

默认会显示声明。在声明页将「下次启动时」设为「直接进入游戏」即可跳过声明。日志和性能面板默认关闭。

排查问题时开启「诊断日志」，重启并复现问题，再从应用 Documents 导出 `native.log`。

构建失败时查看输出目录中的日志。补丁目标不匹配时，先检查游戏和 Unity 版本。

## 存档与程序更新

### 存档位置

存档位于应用的 `Documents/default/`，在 iPad「文件 → 我的 iPad → 空洞骑士：丝之歌 → default」中可以找到。`user1.dat` 到 `user4.dat` 分别对应第一到第四栏存档，你可以使用任意存档修改工具进行修改。

备份或替换存档前，先从后台彻底关闭游戏。备份所有存档时复制整个 `default` 文件夹，恢复时放回原位置。

### 更新程序

使用相同签名账号和应用标识覆盖安装 IPA，会保留存档和资源。资源一般无需重新导入。

更换签名账号或应用标识可能无法覆盖原应用。先备份存档和资源，若需重新安装，再手动迁移。

**更新时不要先卸载应用，卸载会删除游戏资源和存档。**

## 支持与测试范围

当前为 **0.7.2 / build 16 测试版**。

| 项目 | 当前范围 |
| --- | --- |
| 游戏文件 | Steam macOS 版 1.0.30000 |
| 构建环境 | Apple Silicon Mac、Unity 6000.0.50f1、Python 3.12、Xcode |
| 系统要求 | 构建最低目标为 iOS / iPadOS 18.0 |
| 实测设备 | iPad Pro 11 英寸（2021，M1） |
| 操作方式 | 游戏内使用外接键盘或手柄；部分支持触控 |
| 启动页与应用名称 | 简体中文、繁体中文、英文，跟随系统语言 |

## 更新说明

### 0.7.2

- 新增原版风格的启动声明，可选择下次直接进入游戏。
- 使用原版图标，应用名称和启动页支持简中、繁中、英文。
- 系统设置新增声明、重新检查资源、诊断日志、性能面板和启动详细信息开关。

### 反馈

反馈时请提供设备、系统和版本、复现步骤，必要时附上诊断日志。

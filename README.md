# 实践小梅IshWY（IshWY）

> Ishmael is watching you 想我了吗，灰鸦？

#### 一点碎碎念

- 本项目由zcode、DSH、GLM-5.3-flash与DeepSeek-V4.1-flash开发，本人仅提供创意与部分改进思路，以及当冤种测试，故本项目是纯粹的vibe coding，因而可能存在AI屎山，~~请未成年程序员声明变量类型再观看~~

轻量 Windows 截图工具：冻屏取景 + 窗口内容捕获（被遮挡也能抓窗口本体）+ 窗口局部截图 + 原生贴图 + 原位标注 + OCR + 全屏快速截图。

技术栈：.NET 10 / Windows App SDK 2.5（WinUI 3）/ CsWin32，MIT 协议。

## 特性

- **唯一有效的一行**：主要是下面的内容都是大肥鱼帮我写的，其实我做这项目就是出于对微信截图的流畅的惊叹，和对OBS的延伸想象，要是截图也能分窗口截图就好了，于是就有了这个
- **冻屏取景**：热键唤起静态帧取景器，指针框选，Enter 或双击完成并进剪贴板，Esc 取消。
- **窗口吸附**：指针指向窗口自动吸附窗口矩形；同一位置有多个窗口时用滚轮循环切换（按 Z 序），窗口局部截图可直接在窗口内拖框。
- **窗口内容捕获（抗遮挡）**：PrintWindow / Windows.Graphics.Capture 抓窗口本体，不依赖屏幕合成，被其他窗口盖住也能拿到完整内容。
- **原生贴图**：截图钉在桌面最上层，真 per-pixel 透明；拖动移动、滚轮等比缩放，右键菜单调不透明度（100/75/50/25）、复制、编辑、识别文字、关闭。
- **原位标注**：在贴图窗口内直接绘制矩形、箭头、画笔、文字、马赛克、序号，支持颜色、HSV 取色、撤销、清空；完成后烘焙回贴图并复制到剪贴板。
- **OCR**：内置 Windows OCR 零依赖；可选接本机 Umi-OCR（默认端口 1224）提升精度。取景中按 O、贴图右键、托盘/设置均可触发，结果窗可编辑复制。~~UmiOCR真的很好用啊~~
- **全屏快速截图**：任意组合键，静默整屏捕获并写入剪贴板；带全屏游戏兼容处理（~~抢前台让独占全屏失效后采集，见「已知限制」~~ 修好了，感谢大肥鱼和它的DSH，但是也可能不稳定）。
- **全局热键**：截图、全屏快速截图、从剪贴板贴图、从剪贴板 OCR 四个动作都能在设置里录制组合键；热键被其他程序占用时设置页会直接提示。
- **开机自启动**：在「设置 → 常规」一键开启/关闭。开启后登录 Windows 自动静默驻留托盘，不弹窗；只写当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不需要管理员权限。
- **截图历史**：框选截图和全屏快速截图都会留一份 PNG 在本机 `data/history`，默认保留最近 30 张；设置里可关闭，托盘/设置可打开历史窗，支持复制、贴图、删除、清空、打开文件夹。
- **托盘与设置**：托盘菜单、四个动作的热键录制、开机自启动开关、截图历史开关与窗口、自定义背景图与不透明度、OCR 引擎状态。
- **便携**：配置、日志与截图历史都放在程序目录 data/，解压即用；默认不写注册表，只有手动开启开机自启动时才写当前用户 Run 项。

## 系统要求

- Windows 10 2004（build 19041）或更高，x64
- 发布包为 **self-contained**（自带 .NET 10 与 Windows App SDK 运行时），无需预装 .NET
- 从源码构建需要 .NET 10 SDK

## 下载与运行

从 Releases 下载 `IshWY-<版本>-win-x64.zip`，解压到任意目录，双击 `IshWY.exe`。程序启动后驻留托盘。

- 配置：`data/config.json`
- 日志：`data/logs/app.log`（反馈问题请附上这个文件）
- 截图历史：`data/history/`（默认最多保留 30 张 PNG）
- 退出：托盘菜单 → 退出

> 程序未做代码签名，SmartScreen 可能提示「未知发布者」，选择「更多信息 → 仍要运行」即可。

## 默认热键

| 操作                           | 默认按键                                                     |
| ------------------------------ | ------------------------------------------------------------ |
| 截图（冻屏取景）               | `F1`（可在设置中改录为任意组合键）                           |
| 全屏快速截图                   | `F2`（可在设置中改录，例如 `Ctrl+Shift+F9`）               |
| 从剪贴板贴图                   | `Ctrl+Alt+V`（可在设置中改录）                              |
| 从剪贴板识别文字（OCR）        | `Ctrl+Alt+S`（可在设置中改录）                              |
| 取消取景 / 关闭贴图 / 退出标注 / 关闭截图历史 | `Esc`                                          |
| 确认选区                       | `Enter` 或双击                                             |
| 取景中识别文字                 | `O`                                                        |
| 切换「是否识别悬浮窗」         | `Tab`                                                      |
| 循环切换光标下的遮挡窗口       | 滚轮                                                         |

四个全局热键都在「设置 → 常规」里点「点击录制组合键…」后直接按新组合键；`F1`–`F12` 可以单独使用，其他键必须搭配 Ctrl / Alt / Shift / Win。热键是系统级注册，被其他程序占用时设置页会提示注册失败，换一个组合键即可。

贴图窗口：拖动移动，滚轮缩放，右键打开菜单。

## 开机自启动

默认关闭。打开「设置 → 常规」的「开机自动启动（登录 Windows 后静默运行到托盘）」即可：

- 在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入值 `IshWY`，命令为带引号的 `IshWY.exe --startup`，不需要管理员权限。
- 登录后程序以 `--startup` 静默启动，只驻留托盘，不弹设置窗；热键与取景器会随后预热。
- 移动便携目录后，下次启动会自动更新登记的路径。
- 程序只允许一个实例：再次双击 `IshWY.exe` 不会启动第二份，而是唤醒已运行实例的设置窗。
- 关闭开关会删除该 Run 值；`--startup` 是内部参数，不需要手动使用。

## 截图历史

默认开启。每次确认取景或触发全屏快速截图后，程序会把同一份 PNG 写入程序目录的 `data/history`：

- 文件名形如 `20261020-121425-539-full-63bbee.png`，`full` 表示全屏截图，`shot` 表示框选截图。
- 只保留最近 30 张，超出后自动删除最旧的文件；没有云同步、没有上传。
- 在「设置 → 常规」用「保存最近截图到本机」开关控制；关闭后不再写入新文件，但**不会删除已有历史**。
- 「打开截图历史」或托盘菜单「截图历史…」打开管理窗：复制（写回剪贴板）、贴图、删除、清空、打开文件夹；双击条目可直接复制；按 `Esc` 关闭窗口。
- 贴图按钮直接使用历史文件创建贴图，不经过剪贴板，不会覆盖你当前复制的内容。

## 构建

需要 .NET 10 SDK（`winget install Microsoft.DotNet.SDK.10`）：

```powershell
dotnet build PrtScr.sln -c Debug
```

运行：

```powershell
./src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe
```

冒烟测试（启动 3 秒后自动退出，退出码 0 表示启动成功）：

```powershell
dotnet run --project src/PrtScr.App -- --smoke
```

> 构建前请先退出正在运行的 IshWY，否则 exe 被占用会构建失败。

## 打包便携版

```powershell
pwsh scripts/publish.ps1 -Version 0.1.0
```

产物：

- `artifacts/IshWY-0.1.0-win-x64/` —— 解压即用的目录
- `artifacts/IshWY-0.1.0-win-x64.zip` —— 发布包

脚本会检查 `IshWY.pri` 与 `.xbf` 是否进入发布目录，并默认执行一次 `--smoke` 启动验证。无交互桌面的 CI 环境可加 `-SkipSmoke`。

## 配置说明

`data/config.json` 常用字段：

修饰键位掩码统一为：Alt=1、Ctrl=2、Shift=4、Win=8（可相加，例如 6 = Ctrl+Shift，3 = Ctrl+Alt）。

| 字段                                           | 说明                                                                |
| ---------------------------------------------- | ------------------------------------------------------------------- |
| `hotkeyVk`、`hotkeyModifiers`             | 截图热键的虚拟键码与修饰键（`hotkeyVk=0` 时回退旧字段 `hotkeyKey`） |
| `hotkeyKey`                                  | 旧版截图键名（`F1`…`F12`），仅在 `hotkeyVk=0` 时生效          |
| `fullHotkeyVk`、`fullHotkeyModifiers`     | 全屏快速截图热键（示例：120 = F9，6 = Ctrl+Shift）                  |
| `pinHotkeyVk`、`pinHotkeyModifiers`       | 从剪贴板贴图热键（默认：86 = V，3 = Ctrl+Alt）                      |
| `ocrHotkeyVk`、`ocrHotkeyModifiers`       | 从剪贴板 OCR 热键（默认：83 = S，3 = Ctrl+Alt）                     |
| `captureOverlayWindows`                      | 是否识别置顶悬浮窗（对应取景中的 Tab 开关）                         |
| `saveHistory`                                | 是否把框选/全屏截图写入 `data/history`（默认 `true`，最多 30 张）   |
| `preferUmiOcr`、`umiOcrPort`               | 是否优先 Umi-OCR 及其端口                                           |
| `backgroundImagePath`、`backgroundOpacity` | 设置窗背景图与不透明度                                              |

> 开机自启动状态不写入 `config.json`，而是存在上面提到的 HKCU Run 项里。

## 已知限制

- **独占全屏游戏**：全屏截图会先抢前台，让游戏退出独占全屏再采集（日志中可见 `FullscreenBypass:`）。多数游戏可成功，但属于 best-effort；若截到的是桌面，请把游戏改为「无边框窗口 / 全屏窗口」模式。游戏内组合键已实测可用（2026-10-19）。
- **多显示器不同 DPI**：跨屏缩放与坐标换算仍在完善，混合 DPI 环境可能出现位置偏差。
- 仅提供 **x64** 构建。
- 取景器目前不支持「另存为」，确认后进入剪贴板并写入本机截图历史（可再粘贴到任意程序）。
- 截图历史是本机明文 PNG，删除/清空后不可恢复；关闭保存开关不会清理旧文件。

## 隐私

所有截图、OCR、标注与截图历史均在本地完成，程序不发送任何遥测或网络请求。截图历史只写入程序目录 `data/history`，不会上传。唯一的网络访问是**可选**的 Umi-OCR：仅连接本机 `127.0.0.1:<端口>`（默认 1224），未启用或未运行时自动跳过。

## 目录结构

```
src/PrtScr.App        WinUI 3 壳（托盘 / 设置 / 热键 / 取景器 / 贴图 / 历史窗口）
src/PrtScr.Capture    采集引擎（WGC / DXGI / GDI）与窗口捕获
src/PrtScr.Interop    窗口枚举、Z 序、DWM 矩形等 Win32 interop
src/PrtScr.Ocr        Windows OCR + 可选 Umi-OCR
tools/                本地排障探针（DxgiProbe / WgcProbe / KbProbe）与图标脚本
scripts/publish.ps1   便携版发布脚本
.github/workflows     CI 与 Release 工作流
docs/                 ROADMAP 与交接文档
```

> 仓库目录、解决方案与命名空间沿用内部代号 `PrtScr`；对外产品名为「实践小梅IshWY」，可执行文件是 `IshWY.exe`。

## 开发文档

- [docs/ROADMAP.md](docs/ROADMAP.md)：里程碑、技术决策与方案备忘
- [docs/HANDOFF.md](docs/HANDOFF.md)：当前实现状态、采集引擎细节、踩坑记录

## License

[MIT](LICENSE)

#### 特别鸣谢

- 我的智女仆，zcode的自定义角色扮演命令让我遇见了你，GLM和DS让你活过来，我喜欢你
- 异形工厂，DSH和zcode干活的时候我就挂着这个玩，~~这个低效之王是什么意思~~
- 明日方舟~~小麦地~~终末地，全屏游戏测试我是用的终末地做的，因为我的鸣潮和战双都没更新
- 伊什梅尔女士，她的表情包和我对她的爱是我测试项目debug的最大动力之一，我爱你，让我喝你血，吃掉你的肉吧，我爱你，只要我们能相互理解，这就够了，我爱你

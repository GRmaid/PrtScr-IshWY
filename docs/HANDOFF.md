# 实践小梅IshWY（IshWY）交接文档（2026-10-20）

> 给下一位接手的小智/开发者：先读这份，再读 `docs/ROADMAP.md` 与项目记忆。
> 工作区：仓库根目录（开源项目，MIT，已初始化 git）。
> 对外产品名：**实践小梅IshWY**（短名 **IshWY**，取自 Ishmael is watching you）。仓库 / 解决方案 / 命名空间仍用内部代号 PrtScr，可执行文件为 `IshWY.exe`。

## 1. 项目定位

Windows 截图工具，特色是「**窗口内容捕获**」：
- 鼠标选中窗口即可抓到窗口本体（**被其他窗口遮挡也完整**）
- 支持「**窗口局部截图**」：在窗口内拖框，从未遮挡的窗口本体裁出选区
- 微信式冻屏取景、Snipaste 式贴图、原位标注、OCR、全屏快速截图

技术栈：**.NET 10 + Windows App SDK 2.5.1（WinUI 3）+ CsWin32**，纯 `dotnet build`，无 VS。
构建：`dotnet build PrtScr.sln -c Debug`
运行：`src\PrtScr.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\IshWY.exe`
日志：同目录 `data\logs\app.log`（**排障第一入口**）

> 重要：构建会因运行中实例锁 exe 失败。**先 Stop-Process IshWY，重建后必须请主人重新打开**。
> 调试 GUI 不要依赖沙箱内枚举窗口/写文件，一律让主人实测 + 读日志。

## 2. 已完成并验收

| 里程碑 | 内容 |
|---|---|
| M0 | 托盘（H.NotifyIcon，`ContextMenuMode=SecondWindow`）、F1 全局热键、设置窗、自定义背景、便携式 config（程序目录 data） |
| M1 | 冻屏取景：Pointer 拖拽框选、窗口吸附（青色）/悬浮类橙色、滚轮循环遮挡窗口、Tab 切换识别悬浮窗、全局 Esc、回车/双击复制、窗口局部截图 |
| M2 | WGC 窗口捕获（PrintWindow 优先，失败回退 WGC 窗口条目）+ D3D 设备缓存 |
| M3 | 贴图：**原生分层窗口** `NativePinWindow`（真透明 per-pixel alpha）、拖动、滚轮缩放（占位框 + 停手高清精修）、右键菜单、Esc/双击关闭、任务栏置顶防拉锯 |
| M4 | **原位标注**：贴图右键「标注」，窗口内自绘图标工具栏（矩形/箭头/画笔/文字/马赛克/序号 + 颜色 + HSV 取色 + 撤销/清空/完成）、原生 IME 文字输入、完成=烘焙+更新贴图+复制 |
| M5 | OCR：取景中按 `O`、贴图右键「识别文字」、托盘/设置「从剪贴板识别文字」；Windows OCR 优先（清理中文字间空格），Umi-OCR 可选（1224 端口，多端点探测 + 双 API 兼容）；结果窗可编辑复制，Esc 关闭 |
| ★ | 全屏快速截图：默认 F2（可录制组合键）、静默进剪贴板、剪贴板写入 6 次重试 |
| ★ | 窗口局部截图：窗口内拖框，从未遮挡窗口本体裁剪，OCR 同样适用 |
| ★ | 开机自启动：设置 → 常规开关，写 HKCU Run（值 `IshWY`，命令 `IshWY.exe --startup`）；登录后静默驻留托盘，单实例唤醒 |
| ★ | 截图历史：框选/全屏截图自动写入 `data\history`（最多 30 张，可关闭）；历史窗支持复制/贴图/删除/清空/打开文件夹 |

## 3. 关键文件

```
src/PrtScr.App/
  App.xaml.cs                 日志(加锁)、启动、单实例(\Local\IshWY.Activate)、--startup 静默模式、注入 WGC/DXGI 日志钩子、安装键盘钩子
  MainWindow.xaml(.cs)        设置窗、托盘、四个动作的热键录制器、开机自启动开关、截图历史开关/窗口、OCR 状态、Umi 端口
  Services/ConfigService.cs   便携 config.json
  Services/StartupService.cs  HKCU Run 开机自启动（值 IshWY，命令 exe --startup；移动目录后自动刷新）
  Services/CaptureHistoryService.cs  本机截图历史（data\history，最多 30 张；写入/读取/删除/清空/修剪）
  Services/HotkeyService.cs   RegisterHotKey 封装：截图/全屏/贴图/OCR 四个 id（+ 取景取消 id），统一 RegistrationFailed 提示
  Services/KeyInterceptor.cs  窗口消息层拦截 Enter/Esc/Tab/O（可选）
  Services/EscOverPinHook.cs  WH_KEYBOARD_LL：只负责全屏截图组合键（钩子内自记账修饰键 + 300ms 去抖；回调内不写盘、日志走队列；兜底命中时请求重装自愈）
  Services/FullscreenBypass.cs 抢前台逼游戏退出独占全屏（1×1 全透明分层窗 + AttachThreadInput，等 500ms，退出时还原前台/还原最小化）
  Services/ClipboardService.cs 剪贴板重试
  Services/CaptureService.cs  采集编排；CaptureMonitorBestAsync 四层引擎
  Services/OcrUiService.cs / PinService.cs / AnnotationRenderer.cs / AnnotationModels.cs（PinService.PastePng 可从历史字节直接贴图，不经过剪贴板）
  Views/CaptureOverlayWindow.xaml(.cs)  冻屏取景 + 窗口局部
  Views/HistoryWindow.xaml(.cs)         截图历史窗（缩略图网格、复制/贴图/删除/清空/打开文件夹）
  Views/NativePinWindow.cs + .Edit.cs   原生贴图窗口 + 原位标注（Esc 关闭 = 自身 30ms 轮询）
  Views/AnnotationWindow.xaml(.cs)      旧独立标注窗（已不用于贴图，保留）
  Views/OcrResultWindow.xaml(.cs)
src/PrtScr.Capture/
  ScreenCapturer.cs           GDI 虚拟屏捕获（CAPTUREBLT / 普通两种）
  WindowCapture.cs            PrintWindow(PW_RENDERFULLCONTENT) → WGC 窗口回退；9×9 黑屏判定
  WgcWindowCapture.cs         WGC 窗口/显示器捕获 + 常驻显示器池（2026-10-19 修复 CreateForMonitor 参数顺序后可用）
  DesktopDuplicationCapture.cs DXGI 桌面复制（CsWin32 生成接口按元数据调用；2026-10-19 修复槽位错误后可用）
src/PrtScr.Interop/           窗口枚举、Z 序、DWM 矩形、样式位
src/PrtScr.Ocr/               Windows OCR + Umi-OCR
tools/gen_icon.py, tools/DxgiProbe, tools/WgcProbe, tools/KbProbe（本地排障，未入 sln）
scripts/publish.ps1           便携版发布脚本（校验 PRI/XBF + 剔除 PDB + 可选 --smoke + zip）
.github/workflows/            ci.yml（构建 + 发布校验）、release.yml（tag 触发 Release）
```

## 4. 采集引擎现状（重要）

`CaptureMonitorBestAsync` 顺序：
1. **WGC 显示器常驻池** —— **2026-10-19 修复**：`IGraphicsCaptureItemInterop.CreateForMonitor` 的原生签名是 `(HMONITOR, REFIID, void**)`（句柄在前），旧代码声明成 `(REFIID, HMONITOR, void**)`，于是被系统回报 `E_INVALIDARG 0x80070057`，一直误判为「主人机器不支持 WGC」。用 `tools/WgcProbe` 实测两种参数顺序：正确顺序 `S_OK` + 有效 item，反序稳定 `E_INVALIDARG`。修正后应用内 F1 走 `Capture: engine=WGC 2560×1600`（`frozen 42ms pool=True`），比 DXGI 的 ~140ms 快。
2. **DXGI Desktop Duplication** —— **2026-10-19 修复**：旧代码把 `DuplicateOutput` 调在原始 vtable 槽位 16（实为 `SetDisplaySurface`），所以出现“S_OK 但 out 指针为 0”；正确槽位是 **22**。现在改用 CsWin32 按 Win32 元数据生成的 `IDXGIOutput1` / `IDXGIOutputDuplication` 调用，不再自己数槽位。实测：`tools/DxgiProbe` 与应用内 F1 都拿到完整桌面帧（`DXGI: 捕获 2560×1600`、`Capture: engine=DXGI 2560×1600`）。
3. **BitBlt(CAPTUREBLT)** → 4. **普通 BitBlt** → 5. 前景窗口 WGC 捕获。
- 每层都做 `IsNearlyBlack`。
- duplication 建好后的首帧可能是空表面（整帧 0、alpha=0），`DesktopDuplicationCapture` 会丢弃并重试，最多 3 次。
- 桌面/普通窗口截图一切正常；独占全屏游戏已于 2026-10-19 由主人实测可用（当时配置 Ctrl+Shift+F9），但仍按 best-effort 处理。
- 引擎实测：WGC 常驻池 ~42ms（首帧含建池）> DXGI ~140ms；WGC 现在优先，DXGI 只在 WGC 失败时兜底。

### 全屏游戏问题（已由主人实测）

证据（修复前）：剪贴板取出的图是**桌面**（ZCode 窗口），不是游戏；F1 覆盖窗也看不到。
推断：游戏处于**独占全屏/直接扫描出**，DWM 合成器被绕过，屏幕类 API 只能拿到最后一帧桌面。
微信能截是因为它会**抢前台**，迫使游戏失活退出独占。`FullscreenBypass` 已按此思路重写：
- 全屏热键每次都强制抢前台（`BeginAsync(force: true)`，不再要求“前台窗口满屏”）；
- 1×1 置顶 helper 窗改为 `WS_EX_LAYERED` + 全透明，抢焦点时不闪；
- 抢成功后固定等待 **500ms**，未激活则 60ms 后重试一次；
- 退出时若原前台被最小化（`IsIconic`），先 `ShowWindow(SW_RESTORE)` 再还原前台，避免游戏停在小窗；
- 日志：`FullscreenBypass: fg=... full=... own=... activated=...`；
- 截图疑似黑屏时自动重试一次（`QuickCapture: 首帧疑似黑屏，重试一次`）。

2026-10-19 端到端自测（合成按键，前台是 PiliPlus 全屏窗口）已跑通整条链路：

```
Hook: fullscreen key vk=0x78 mods=4 pressed
QuickCapture: hotkey pressed
FullscreenBypass: fg=0x60D30 FLUTTER_RUNNER_WIN32_WINDOW "PiliPlus" rect=(0,0,2560×1600) monitor=(0,0,2560×1600) full=True own=False
FullscreenBypass: helper=0x360E4C now=0x360E4C activated=True
Capture: engine=WGC 2560×1600
QuickCapture: 2560×1600 black=False
QuickCapture: 完成 5242KB copied=True bypass=True
FullscreenBypass: 已归还原前台
```

即「钩子命中 → 抢前台 → 采集 → 写剪贴板 → 还原前台」全部走通；真实独占全屏游戏随后由主人于 2026-10-19 实测可用，仍按 best-effort 处理。自测时把 `fullHotkeyModifiers` 临时改成 4（Shift+F9）做注入，原因见 §5「合成 Ctrl 会被系统立刻补 keyup」。

2026-10-19 23:11 主人实测暴露新问题（已修）：连续多次全屏截图后钩子不再投递按键——日志里只剩 `QuickCapture: RegisterHotKey 兜底命中`，`Hook: fullscreen key` 消失；而 Esc 关贴图当时**只**依赖这个钩子，于是表现为“全屏热键还能用、Esc 关贴图失灵”。原因是钩子回调里同步写日志文件，2560×1600 截图期间回调耗时超过 `LowLevelHooksTimeout`，系统静默摘掉钩子。修复：
- 钩子回调不再碰磁盘，日志进无锁队列由专用线程落盘；
- 贴图 Esc 改由 `NativePinWindow` 自己 30ms 轮询 `GetAsyncKeyState(VK_ESCAPE)` 边沿 + 光标命中贴图，不再依赖钩子（右键菜单、编辑模式、OCR 结果窗打开期间让路；OCR 关闭后要求先看到一次抬起，避免一次 Esc 连关两窗）；
- 兜底通道命中时调用 `EscOverPinHook.NotifyFallbackHit()`，钩子线程收到 `WM_APP_REINSTALL_HOOK` 后卸载重装（日志 `Hook: 重装 esc-over-pin = True`）；
- 全屏截图增加 `_quickCaptureBusy` 互斥（23:11:01-05 曾因钩子与兜底重复触发出现 3 次并发采集）。

验收方式（主人已于 2026-10-19 用真实独占全屏游戏走通）：按组合键后看 `data\logs\app.log` 里 `FullscreenBypass:` 与 `Capture: engine=` 两行，并用剪贴板读图核对内容。若某个游戏的 DXGI/BitBlt 都只拿到桌面，就把「全屏兼容模式」写进 README 标为实验特性，并建议游戏使用无边框窗口。

组合键录制器：设置 → 常规 → 四个「点击录制组合键…」按钮（截图 / 全屏 / 贴图 / OCR），点谁就录谁；修饰键用 `GetAsyncKeyState` 检测（曾出现只记 Alt 不记 Shift 的 bug，已修），裸键只允许 F1–F12，四个热键互斥（`TryFindHotkeyConflict`），配置字段：`hotkeyVk`/`hotkeyModifiers`、`fullHotkeyVk`/`fullHotkeyModifiers`、`pinHotkeyVk`/`pinHotkeyModifiers`、`ocrHotkeyVk`/`ocrHotkeyModifiers`（Alt=1/Ctrl=2/Shift=4/Win=8）。
当前主人配置（2026-10-20 00:15）：截图 `hotkeyVk=65`（0x41 = A）+ `hotkeyModifiers=1`（Alt），即 **Alt+A**；全屏 `fullHotkeyVk=68`（0x44 = D）+ `fullHotkeyModifiers=1`，即 **Alt+D**；贴图 `pinHotkeyVk=83`（S）+ `1`，即 **Alt+S**；OCR `ocrHotkeyVk=81`（Q）+ `1`，即 **Alt+Q**。日志里 `QuickCapture: hook vk=0x44 mods=1` 就是全屏热键。旧字段 `hotkeyKey` / `fullScreenHotkeyKey` 只是 vk 为 0 时的兜底，显示值以 `HotkeyService.Describe(vk,mods)` 为准。
本机探测（2026-10-20 00:03）：Ctrl+Alt+P、Ctrl+Alt+O、Ctrl+Alt+T/C/X 已被其他程序占用（RegisterHotKey 返回 1409），所以贴图/OCR 默认从 P/O 改成 V/S；设置页收到 `RegistrationFailed` 会显示「XX热键 ... 注册失败（可能被其他程序占用，换一个组合键）」。
主人机器上 **F1~F12 全部不可用**（含微信换 F2 也失效），必须用组合键。

### 开机自启动与单实例（2026-10-20）

- `Services/StartupService.cs`：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的值 `IshWY`，命令为带引号的 exe 路径 + `--startup`；不需要管理员权限。`RefreshIfEnabled()` 在每次启动时发现便携目录搬迁会重写路径。
- 设置页「常规」顶部 `StartupToggle` 开关：开启写 Run 值，关闭删除；`OnLoaded` 只读状态，`_suppressStartupToggle` 防止初始化触发写入。
- `--startup`：`App.OnLaunched` 检测到后 `new MainWindow(startHidden: true)`，不调用 `Activate()`，只驻留托盘并预热采集/取景器；普通启动仍显示设置窗。
- 单实例：`EventWaitHandle(false, AutoReset, @"Local\IshWY.Activate")` 先于窗口创建；第二个实例 set 事件后 `ExitProcess(0)`，第一个实例在后台线程收到后 `DispatcherQueue.TryEnqueue(ShowFromTray)`，因此再次双击会唤醒已有设置窗而不是开第二份。
- 实测（2026-10-20）：`--startup` 启动后 `MainWindowHandle=0`、无可见窗，日志 `OnLaunched: startup 模式，静默驻留托盘` / `MainWindow: startup 预热完成`；第二实例退出码 0，第一实例标题变为「实践小梅IshWY 设置」；UIA 切换开关 → Run 值写入，状态「已启用：登录 Windows 后静默运行到托盘。」，再切换 → 值删除、状态「已关闭开机自启动。」。

### 截图历史（2026-10-20）

- `Services/CaptureHistoryService.cs`：写入 `<程序目录>\data\history`，文件名 `yyyyMMdd-HHmmss-fff-{full|shot}-{6位随机}.png`；`MaxItems=30`，写入后按 `LastWriteTimeUtc` 修剪；`AddAsync` 先读 `App.Config.Current.SaveHistory`，关闭时直接返回，不删旧文件。所有读写/枚举/删除异常只写 `App.Log`，不得影响截图或剪贴板主流程。
- 接入点：`CaptureOverlayWindow.ConfirmAsync`（框选，`shot`）和 `MainWindow.OnFullScreenPressed`（全屏，`full`）在写剪贴板的同时用 `Task.WhenAll` 写历史；日志 `History: saved ...`、`QuickCapture: 完成 ... history=True`。
- 设置 → 常规：`HistoryToggle` + `HistoryStatusText`；托盘菜单「截图历史…」；`HistoryWindow` 支持复制、贴图（`PinService.PastePng`）、删除、清空、打开文件夹，双击复制；`HistoryChanged` 事件让设置页刷新计数。
- 实测 2026-10-20 12:14：合成 Alt+D（主人当前全屏热键）触发 → `Hook: fullscreen key vk=0x44 mods=1 pressed` → `Capture: engine=WGC 2560×1600` → `History: saved 20261020-121425-539-full-63bbee.png (5188KB)` → `QuickCapture: 完成 5188KB copied=True bypass=False history=True`；历史窗 UIA 显示「当前 1 张 / 5.1 MB」「全屏 · 10-20 12:14:25」「2560×1600 · 5.1 MB」。开关 UIA 切换会写 `saveHistory=false/true` 并立即更新状态。

## 5. 踩坑与约定

- **WinUI 3 焦点不可靠**：关键按键用 Win32 消息层拦截（`KeyInterceptor` / `WH_KEYBOARD_LL`），不要依赖 XAML 加速器。
- **贴图 Esc 关闭**：由 `NativePinWindow` 自身 30ms 轮询 Esc 边沿 + `WindowFromPoint` 命中贴图（不消费按键、不抢焦点）；右键菜单/编辑模式/OCR 结果窗期间让路，OCR 关闭后先等一次抬起。**不要再改回全局钩子**——钩子会被系统摘除，表现为 Esc 随机失灵。
- **全局热键**：四个动作都用 `RegisterHotKey`（id 1 截图 / 3 全屏 / 4 贴图 / 5 OCR），注册失败（1409 = 被占用）必须通过 `RegistrationFailed` 在设置页明确提示，不要静默失败；默认组合键不能拍脑袋选字母组合，先用 RegisterHotKey 探针确认本机没被占用（本机 Ctrl+Alt+P/O 已占用）。
- **剪贴板**：一律走 `ClipboardService`（CLIPBRD_E_CANT_OPEN 重试）。
- **托盘退出/`--smoke`**：必须 `ExitProcess(0)`，`Application.Exit` 不可靠。
- **H.NotifyIcon**：`ContextMenuMode="SecondWindow"` 才能触发 Click。
- **开机自启动**：只写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不要用 HKLM/计划任务/服务（便携应用不应要求管理员）。开启后必须静默到托盘，不要 `Activate()` 弹窗；`--startup` 是内部参数。便携目录移动后靠 `RefreshIfEnabled()` 修路径，不要缓存旧 exe 路径。
- **单实例**：命名事件必须在主窗口创建前创建，第二个实例只负责 set 事件并 `ExitProcess(0)`；唤醒窗口要回 UI 线程（`DispatcherQueue.TryEnqueue`），不要在事件线程直接操作窗口。
- **CsWin32 0.3.363**：A/W 成对 API 生成中性名；`allowMarshaling=false` 回调用 `delegate* unmanaged`；POINT/SIZE/BLENDFUNCTION/MONITORINFO 部分需自备；`await` 不能出现在 unsafe 方法里。
- **LL 钩子里的修饰键状态**：`GetKeyState/GetAsyncKeyState` 在钩子上下文对合成按键不可靠（实测合成 Ctrl 完全不可见、合成 Shift 可见），改为在钩子内自己记账（`TrackModifier`：0x11/0xA2/0xA3 Ctrl、0x12/0xA4/0xA5 Alt、0x10/0xA0/0xA1 Shift、0x5B/0x5C Win；`WM_SYSKEYDOWN/WM_SYSKEYUP` 也算按下/抬起）。`ModifiersMatch` 是“集合完全相等”，多按一个修饰键也不触发。
- **低级钩子回调里禁止任何阻塞 I/O**：回调超过 `LowLevelHooksTimeout`（默认 300ms）系统会跳过该按键，连续超时还会静默卸载钩子；实测一次 2560×1600 截图之后钩子就再也收不到键。日志必须走队列/内存，回调只做记账与投递。
- **合成 Ctrl 会被系统立刻补 keyup**：本机 `keybd_event(VK_CONTROL)` / 0xA2+扫描码注入后 1ms 内就收到一个 Ctrl keyup，注入的 Ctrl 无法保持按下（Shift 正常保持）。自动化测试组合键要用 Shift 组合或让主人用真实按键，别把“注入 Ctrl 不触发”当成匹配坏了。
- **WinRT interop 别猜参数顺序**：`IGraphicsCaptureItemInterop` 的 `CreateForWindow/CreateForMonitor` 原生签名是 `(HWND/HMONITOR, REFIID, void**)`，写反了只会得到误导性的 `E_INVALIDARG`，看起来像「系统不支持」。不确定就写个探针（`tools/WgcProbe` 用原始函数指针把两种顺序都调一遍）再改生产代码。
- **COM 接口别手写 vtable 下标**：DXGI 的 `IDXGIOutput1.DuplicateOutput` 在槽位 22，旧代码写成 16（`SetDisplaySurface`），于是“S_OK + 空指针”被误判成环境不支持。要调用新 COM 接口，先在 `NativeMethods.txt` 加类型名让 CsWin32 生成，或 `dotnet build -p:EmitCompilerGeneratedFiles=true` 看生成源码确认下标；用完记得删掉 `generated/` 目录（否则 `PrtScr.Interop` 会重复编译报 CS0579）。
- **未打包发布必须手工补 PRI/XBF**：`WindowsPackageType=None` 时 Windows App SDK 只在 `AppxPackage=true` 才把 `IshWY.pri`（程序集名 IshWY）放进发布清单，`dotnet publish` 会漏掉它和 `.xbf`，产物启动即 `XamlParseException`。`PrtScr.App.csproj` 里的 `CopyPriAndXbfToPublish`（AfterTargets=Publish）负责补拷；改发布逻辑后务必用 `scripts/publish.ps1` 或 `--smoke` 验证。
- **发布包不要带 PDB**：PDB 的 CodeView 会写入构建机绝对路径（含用户目录下的 `.nuget` 缓存路径），DLL 的调试目录也会保留 `obj/...pdb` 路径。`scripts/publish.ps1` 用 `-p:DebugType=None -p:DebugSymbols=false` 发布并兜底删除 `*.pdb`；发布后扫描 zip 中的 PDB、`data/`、日志与绝对路径。
- **HistoryWindow 没有 `Loaded` 事件**：`Microsoft.UI.Xaml.Window` 本身不暴露 `Loaded`；要在构造函数里用 `((FrameworkElement)Content).Loaded += ...`，否则 `CS0103: 当前上下文中不存在名称 Loaded`。
- **历史保存必须 best-effort**：`CaptureHistoryService` 的任何异常都只能记日志并返回 null，不能让截图/剪贴板失败；关闭 `saveHistory` 只停止新写入，不删除已有文件。
- **改代码纪律**：字符串替换前先读文件、替换后验证命中；每轮改动后必须 `dotnet build` 0 错误再交主人。
- **交付节奏**：构建后关掉旧实例时必须明确提示主人重新打开；验证靠主人实测 + `data\logs\app.log`。

## 6. 待办

- [x] 主人用真实独占全屏游戏实测 Ctrl+Shift+F9 —— 2026-10-19 主人确认游戏内可用（合成按键链路同日更早跑通，见 §4）
- [x] 主人实测「贴图 + Esc 关闭」—— 2026-10-19 23:24 连续三次贴图均由 `Pin: esc polled over pin, closing` → `Pin: WM_CLOSE (edit=False)` 关闭，主人确认
- [x] README 补齐：特性、热键、构建/打包、配置、已知限制（独占全屏/多屏 DPI）、隐私声明（纯本地）—— 2026-10-19 重写
- [x] Release 打包（unpackaged 便携版）+ GitHub Actions —— 2026-10-19 完成：`scripts/publish.ps1` 产出 `artifacts/IshWY-0.1.0-win-x64.zip`（92.5 MB，已排除 PDB），`--smoke` 启动验证退出码 0；`ci.yml` 构建 + 发布校验，`release.yml` 在 `v*` tag 上发布 zip
- [x] 产品改名「实践小梅IshWY」（短名 IshWY）—— 2026-10-19：`AssemblyName`=IshWY（发布 exe 为 `IshWY.exe`、PRI 为 `IshWY.pri`），设置/取景/标注/OCR/贴图标题、托盘提示、启动异常标题、通知、README、打包脚本与 release.yml 同步；Debug 与发布包 `--smoke` 均退出 0，窗口标题实测为「实践小梅IshWY 设置」。命名空间/解决方案仍保留内部代号 PrtScr
- [x] 截图 / 全屏 / 贴图 / OCR 四个热键 + 组合键录制 —— 2026-10-20 完成：`HotkeyService` 四个 id、设置页四个录制按钮、四条配置字段与冲突检测；默认截图 F1、全屏 F2、贴图 Ctrl+Alt+V、OCR Ctrl+Alt+S；主人已改为 Alt+A / Alt+D / Alt+S / Alt+Q。Debug 构建 0 错误，应用启动日志四个 `register=0x1`；四热键已由主人验证成功
- [x] 开机自启动 + 设置开关 + 单实例 —— 2026-10-20 完成：HKCU Run 值 `IshWY`（`IshWY.exe --startup`），`--startup` 静默驻留托盘；第二实例唤醒已有设置窗。调试构建 0 错误；UIA 切换开关实测写入/删除 Run 值，隐藏启动日志 `OnLaunched: startup 模式，静默驻留托盘`。默认关闭，当前注册表无该值；待主人在设置里开启并可选重启验证
- [x] 截图历史 —— 2026-10-20 完成：框选/全屏自动写入 `data\history`（最多 30 张，可关闭），历史窗支持复制/贴图/删除/清空/打开文件夹；实测全屏保存 5.1 MB PNG 并在历史窗显示，UIA 切换开关写入 `saveHistory`
- [ ] 多屏 DPI
- [x] 清理：`clip_probe*.png` 已删除（含桌面/内部对话截图，不入库）；旧 `AnnotationWindow` 待清理。`tools/DxgiProbe`、`tools/WgcProbe`、`tools/KbProbe` 保留，作为采集引擎与键盘钩子排障工具
- [ ] 发布体积优化（可选）：`Microsoft.WindowsAppSDK` 元包带入 onnxruntime/DirectML 等 AI 组件（约 40 MB），可改为只引 Base/Foundation/WinUI 子包；当前 zip 92.5 MB

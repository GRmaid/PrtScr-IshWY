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
| M1 | 冻屏取景：Pointer 拖拽框选、窗口吸附（青色）/悬浮类橙色、滚轮循环遮挡窗口、**Tab 在窗口截图/常规截图间切换**、全局 Esc、回车/双击复制、窗口局部截图 |
| M2 | WGC 窗口捕获（PrintWindow 优先，失败回退 WGC 窗口条目）+ D3D 设备缓存 |
| M3 | 贴图：**原生分层窗口** `NativePinWindow`（真透明 per-pixel alpha）、拖动、滚轮缩放（占位框 + 停手高清精修）、右键菜单、Esc/双击关闭、任务栏置顶防拉锯 |
| M4 | **原位标注**：贴图右键「标注」，窗口内自绘图标工具栏（矩形/箭头/画笔/文字/马赛克/序号 + 颜色 + HSV 取色 + 撤销/清空/完成）、原生 IME 文字输入、完成=烘焙+更新贴图+复制 |
| M5 | OCR：取景中按 `O`、贴图右键「识别文字」、托盘/设置「从剪贴板识别文字」；Windows OCR 优先（清理中文字间空格），Umi-OCR 可选（1224 端口，多端点探测 + 双 API 兼容）；结果窗可编辑复制，Esc 关闭 |
| ★ | 全屏快速截图：默认 F2（可录制组合键）、静默进剪贴板、剪贴板写入 6 次重试 |
| ★ | 窗口局部截图：窗口内拖框，从未遮挡窗口本体裁剪，OCR 同样适用 |
| ★ | 开机自启动：设置 → 常规开关，写 HKCU Run（值 `IshWY`，命令 `IshWY.exe --startup`）；登录后静默驻留托盘，单实例唤醒 |
| ★ | 截图历史：框选/全屏截图自动写入 `data\history`（最多 30 张，可关闭）；历史窗支持复制/贴图/标注/另存为/删除/清空/打开文件夹 |
| ★ | 常规截图模式：取景中 Tab 从窗口重建切到「按冻结画面所见即所得」，右键菜单/悬停提示/下拉框/指针都能截到；窗口模式仍保留抗遮挡 |
| ★ | 放大取色器：跟随鼠标 19×19 放大 + 十字准星 + HEX/RGB；无选区 Ctrl+C 复制色值，有选区仍复制截图 |
| ★ | 高分辨率应用图标：`tools/gen_icon.py` 以 256×256 为基底生成 16–256 共 10 档 ICO |
| ★ | 另存为：取景器 `Ctrl+S`/「保存」、历史「另存为」、贴图右键「另存为…」、标注工具栏「保存」；取景器保存后仍复制并写历史 |
| ★ | 延时截图与鼠标指针：设置 → 常规可选 0/3/5/10 秒，等待中再按截图热键取消；托盘有「延时截图」「取消延时截图」；可让 F1 与全屏截图包含当前指针 |

## 3. 关键文件

```
src/PrtScr.App/
  App.xaml.cs                 日志(加锁)、启动、单实例(\Local\IshWY.Activate)、--startup 静默模式、注入 WGC/DXGI 日志钩子、安装键盘钩子
  MainWindow.xaml(.cs)        设置窗、托盘、四个动作的热键录制器、延时截图/取消入口、鼠标指针开关、默认截图模式、开机自启动开关、截图历史开关/窗口、OCR 状态、Umi 端口
  Services/ConfigService.cs   便携 config.json
  Services/StartupService.cs  HKCU Run 开机自启动（值 IshWY，命令 exe --startup；移动目录后自动刷新）
  Services/CaptureHistoryService.cs  本机截图历史（data\history，最多 30 张；写入/读取/删除/清空/修剪）
  Services/FileSaveService.cs 系统 FileSavePicker 另存为（PNG/JPEG，JPEG 质量 92；取消/异常只记日志）
  Services/HotkeyService.cs   RegisterHotKey 封装：截图/全屏/贴图/OCR 四个 id（+ 取景取消 id），统一 RegistrationFailed 提示
  Services/KeyInterceptor.cs  窗口消息层拦截 Enter/Esc/Tab/O（可选）；MakeClientAreaFullWindow 把 1px 非客户区并进客户区
  Services/EscRouter.cs       全局 Esc 统一仲裁：编辑中只退编辑、OCR/贴图重叠关最上层、不重叠先关 OCR
  Services/EscOverPinHook.cs  WH_KEYBOARD_LL：全屏截图组合键 + 取景窗可见期间的 Tab/Esc/Enter/O/Ctrl+C/Ctrl+S 直通（不依赖取景窗焦点；钩子内自记账修饰键 + 300ms 去抖；回调内不写盘、日志走队列；兜底命中时请求重装自愈）
  Services/FullscreenBypass.cs 抢前台逼游戏退出独占全屏（1×1 全透明分层窗 + AttachThreadInput，等 500ms，退出时还原前台/还原最小化）
  Services/ClipboardService.cs 剪贴板重试
  Services/CaptureService.cs  采集编排；CaptureMonitorBestAsync 四层引擎；DrawCursorIfEnabled 统一合成鼠标指针
  Services/OcrUiService.cs / PinService.cs / AnnotationRenderer.cs / AnnotationModels.cs（PinService.PastePng / PastePngAndEdit 可从历史字节直接贴图，不经过剪贴板）
  Views/CaptureOverlayWindow.xaml(.cs)  冻屏取景 + 窗口局部；`CaptureMode`（Window/Normal，Tab 切换，起始模式读 `defaultCaptureMode`）；窗口预高亮/单击选窗两种模式共用；指针事件挂在根 `Root`；选区右上角「保存」/`Ctrl+S` 另存为，保存后仍复制并写历史；`Ctrl+C`（无选区）复制放大镜色值；19×19 取色器复用 WriteableBitmap 与 byte 缓冲
  Views/HistoryWindow.xaml(.cs)         截图历史窗（缩略图网格、复制/贴图/标注/另存为/删除/清空/打开文件夹；根 Grid PreviewKeyDown 处理 Esc；AppWindow.SetIcon 与主窗一致；关闭时清空 ItemsSource/缩略图集合）
  Views/NativePinWindow.cs + .Edit.cs   原生贴图窗口 + 原位标注（Esc 关闭 = 自身 30ms 轮询后交给 EscRouter 仲裁；右键「另存为…」，标注工具栏「保存」= 烘焙+复制+另存为；不常驻全分辨率解码位图，按需解码并直接绘制到常驻 DIB；标注中键/右键拖动平移；拖动加 SWP_NOREDRAW，退出标注回收 DIB）
  Views/AnnotationWindow.xaml(.cs)      旧独立标注窗（已不用于贴图，保留）
  Views/OcrResultWindow.xaml(.cs)   OCR 结果窗（关闭时清空文本，释放大段 OCR 结果）
src/PrtScr.Capture/
  ScreenCapturer.cs           GDI 虚拟屏捕获（CAPTUREBLT / 普通两种）
  WindowCapture.cs            PrintWindow(PW_RENDERFULLCONTENT) → WGC 窗口回退；9×9 黑屏判定
  WgcWindowCapture.cs         WGC 窗口/显示器捕获 + 常驻显示器池（2026-10-19 修复 CreateForMonitor 参数顺序后可用）
  DesktopDuplicationCapture.cs DXGI 桌面复制（CsWin32 生成接口按元数据调用；2026-10-19 修复槽位错误后可用）
src/PrtScr.Interop/           窗口枚举、Z 序、DWM 矩形（失败回退 GetWindowRect）、样式位、子对话框（#32770）枚举
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
- 贴图 Esc 改由 `NativePinWindow` 自己 30ms 轮询 `GetAsyncKeyState(VK_ESCAPE)` 边沿 + 光标命中贴图，不再依赖钩子；2026-10-20 傍晚起轮询与 OCR 的 Esc 热键都交给 `EscRouter` 统一仲裁（规则见下），并在退出编辑时置 `_escNeedsRelease`，避免同一次 Esc 连退编辑又关贴图；
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
- 2026-10-20 补充：历史窗根 `Grid` 的 `PreviewKeyDown` 处理 Esc，`Loaded`/`Activated` 时把焦点放到 `HistoryGrid`（实测 12:37 合成 Esc 后窗口关闭、标题回到「设置」，有历史项时复测通过；12:38 临时移走历史文件后空列表状态也能用 Esc 关闭（随后恢复文件））；`AppWindow.SetIcon(Assets/app.ico)` 使标题栏图标与设置窗一致（`WM_GETICON` 取到的 48×48 图标渲染哈希相同）。

### 另存为与历史标注（2026-10-20）

- `Services/FileSaveService.cs`：`SavePngAsync(ownerHwnd, bytes, suggestedName)` 用 WinRT `FileSavePicker`（PNG/JPEG 两种选项，默认扩展名 .png），`WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd)` 绑定拥有者窗口；JPEG 用 `System.Drawing` 质量 92 转码。取消返回 null，异常只记 `Save: 失败`。
- 取景器：`CaptureOverlayWindow` 选区右上角 `SaveButton` + `KeyboardAccelerator Ctrl+S` → `SaveSelectionAsync`。保存成功后与回车确认一致，同时 `ClipboardService.SetImageAsync` + `CaptureHistoryService.AddAsync(fullScreen:false)`，日志依次 `Capture: copied` / `Capture: saved as ...` / `Capture: history saved ...`，然后 `Dismiss()`；取消保存则重新聚焦 `FocusSink`，取景器不关闭。
- 历史窗：`HistoryWindow` 新增「标注」= 读历史字节 → `PinService.PastePngAndEdit`（直接贴图并 `RequestEditMode`，不经过剪贴板）；「另存为」= 读原图字节 → `FileSaveService`。
- 贴图：右键菜单 `CmdSaveAs=5` → `SaveAs()`；标注工具栏新增 id 24（`ToolbarOrder` 末尾前），`HandleToolbarCommand(24)` → `ApplyEdit(saveAs:true)`，先烘焙标注、更新贴图、复制到剪贴板，再另存为；`unsafe` 类内不能写 async，因此 `SaveAs`/`ApplyEdit` 只 fire-and-forget。
- 实测：历史另存为 `IshWY-history-20261020-125340.png` 与源文件 sha256 相同；贴图另存为 `IshWY-pin-20261020-125509.png` 字节相同；历史「标注」创建 640×400 贴图并 `Pin: edit mode on`；主人实测取景器 `Ctrl+S` 保存后剪贴板与历史均正常（2026-10-20）。

### 内存与体积优化（2026-10-20）

- 主人基线（优化前）：空闲 ~56 MB；截图后 98–107 MB；贴图 178–207 MB；标注峰值 222 MB，完成回落到 ~178 MB。目标整进程尽量 <100 MB，最好 75–80 MB。
- 取景器：`CaptureOverlayWindow.Dismiss()` 释放 `Frozen.Source`（2560×1600 WriteableBitmap）与 `_full` GDI Bitmap，强制 `GC.Collect(2, Forced, blocking, compacting)`，日志 `Overlay: frozen buffers released, managed=..->..MB`；随后把 `_full` 置为 1×1，避免旧帧被引用。
- 贴图：删除常驻 `_source` 全分辨率 Bitmap；`RenderHighQuality()`/`RebuildBaseScaled()`/`ApplyEdit()` 都通过 `DecodeSource()` 按需从 `_pngBytes` 解码，用完立即 Dispose。`DecodeSource()` 直接返回 `Image.FromStream`（`DecodedSource` 持有流），不再复制一份全分辨率 PArgb 位图，单次解码省约 16 MB；`AnnotationRenderer.Render` 参数放宽为 `Image`。`CreateDestBitmap()` 用 `_destBits` + `_destAllocW*4` 步长把常驻 DIB 包成 Bitmap 直接绘制，`RenderEditMode()` 不再每帧 `new Bitmap(_winW,_winH)`；`CopyToDestAndPresent` 已删除。闲置贴图只剩压缩 PNG（约 5 MB）+ 窗口尺寸 DIB + 分层窗口系统表面。
- 贴图平移：编辑模式下左键继续绘制；中键/右键在图片区域按下走 `BeginEditPan`，`MoveEditPan` 按屏幕光标增量 `SetWindowPos` 并同步 `_imgX/_imgY`，`EndEditPan` 返回是否移动；右键未移动才 `ShowMenu()`，工具栏/取色面板区域不参与平移。
- 历史窗：`Closed` 里把 `HistoryGrid.ItemsSource=null` 并 `_items.Clear()`，释放最多 30 张 `DecodePixelWidth=360` 缩略图（约 9 MB）。
- 发布体积：`scripts/publish.ps1` 保留元包但删除未加载的 AI/ML/Search/Widgets 组件（39 个文件 55.3 MB），发布目录 232→177 MB，zip 92.5→69.6 MB（`IshWY-0.1.2-local-win-x64.zip`），`--smoke` 通过；细粒度替换元包仍因 H.NotifyIcon 的旧 targets 冲突不可行。
- 滚轮缩放：`OnWheel` 的放大上限从固定 8× 改为「显示器工作区 / 原图尺寸」，避免 2560 宽贴图被放到 20480 px（DIB 仍按 8192 封顶，旧代码会越界，同时分层窗口表面可膨胀到数百 MB）。代价是贴图最大只能铺满当前显示器。
- 取景初帧命中：`CaptureOverlayWindow.InitializeFrozen()` 在 `Activate()` 后按 `GetCursorPos` 主动做一次 `HitTestHover` + `UpdateVisuals`，并在 60ms 焦点重试时再刷一次；`Activated` 也刷新。修复「打开取景器后不动鼠标就不认光标下窗口」。
- 主人复测（2026-10-20 20:34）：三步 Esc 逻辑均通过，整进程资源占用基本稳定在约 142 MB；滚轮缩放/编辑标注峰值、取景初帧命中修复也随本轮验收通过。WGC 常驻池（约 48 MB）暂未改。

### Esc 统一仲裁（2026-10-20 傍晚）

- 问题：标注编辑中按 Esc 会把编辑退出后**继续关闭贴图**；OCR 结果窗与贴图同时存在时，Esc 会**两个一起关**，与重叠/层级无关。
- 根因：贴图的 30ms 轮询与 OCR 窗口自己的 `RegisterHotKey(VK_ESCAPE)` 各自处理同一个按键，谁先到谁关；编辑模式之前还额外注册了 `RegisterHotKey(100, VK_ESCAPE)`，与 OCR 的 Esc 热键冲突（1409），且退出编辑后同一次按键还能继续命中贴图关闭。
- 修复：新增 `Services/EscRouter.cs`。贴图轮询、贴图 `WM_KEYDOWN`、OCR 的 `KeyInterceptor` 与 OCR Esc 热键全部改为调用 `EscRouter.Dispatch()`；贴图不再注册 Esc 热键。
- 规则与优先级：① 有贴图处于编辑模式 → 只发 `ExitEditMessage (WM_APP+13)` 退出编辑，绝不关窗；② 有 OCR 窗口 → 计算所有 OCR/贴图矩形是否相交，有相交就按全局 Z 序关**最上层**那个，无相交就固定关最上层 OCR；③ 无 OCR → 关光标命中且最上层的贴图；④ 贴图右键菜单打开时不处理（菜单自己消费 Esc）。`Dispatch()` 用 400ms 去重，保证一次按键只产生一个动作。
- 焦点/抬起：`ExitEditMode()` 只在 `GetAsyncKeyState(VK_ESCAPE)` 仍按住时置 `_escNeedsRelease`，贴图轮询必须看到抬起才接受下一次。不能无条件置位——否则鼠标点「完成」退出标注后，下一次 Esc 会被吞掉。OCR 窗口关闭时 `EscRouter.Unregister`，不会留下失效条目。
- 日志：`EscRouter: target=pin-edit 0x...`、`EscRouter: target=ocr|pin 0x... overlap=...`。
- 主人实测（2026-10-20 20:34）：三步（标注中 Esc 只退编辑 / 重叠只关最上层 / 不重叠先关 OCR）全部通过；同轮确认整进程资源占用基本稳定在约 142 MB。

### 延时截图与鼠标指针（2026-10-20 晚）

- 配置：`captureDelaySeconds`（0 默认 / 3 / 5 / 10）、`captureCursor`（bool，默认 false）。
- 设置 → 常规：`DelayCombo`（不延时/3/5/10 秒）+ `CaptureCursorToggle`；`OnLoaded` 在 `_suppressCaptureOptions` 保护下初始化，改动立即写 config 并刷新 `CaptureOptionsText`。托盘 `DelayCaptureItem` 文本随延时变化，没有配置时按 3 秒执行；`CancelDelayItem` 只在倒计时期间显示。
- `MainWindow.OnHotkeyPressed` 的顺序：倒计时中再按一次 → 取消；取景器已打开 → 走原有取消取景；否则按配置延时。延时用 `StartDelayedCaptureAsync`：`CancellationTokenSource` + 每秒更新托盘提示/`HotkeyStatusText`，到点只调用一次 `CaptureService.StartCaptureAsync()`；`finally` 清理 cts、隐藏取消项、恢复托盘提示。全屏快速截图不走延时，保持即时。
- 鼠标指针：`CaptureService.DrawCursorIfEnabled(Bitmap, WindowRect)` 在 `StartCaptureAsync` 的引擎采集之后、`CaptureFullScreenPngAsync` 写 PNG 之前调用。用 `GetCursorInfo`（只处理 `CURSOR_SHOWING`）取屏幕坐标与 HICON，`GetIconInfo` 得到 hotspot 并扣掉偏移，`DrawIconEx(..., DI_NORMAL)` 画进同一张 Bitmap，最后 `DeleteObject(hbmMask/hbmColor)`。任何失败只记 `Capture: 光标合成失败`，不影响截图。
- WGC 会话的 `IsCursorCaptureEnabled` 仍保持 false，统一由 GDI 合成，避免 WGC 与 BitBlt 引擎出现双指针/行为不一致。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误，`IshWY.exe --smoke` 退出 0（四个热键均 `register=0x1`）；真实热键、倒计时与指针合成待主人实测（按约定不做合成输入测试）。

### 常规截图与放大取色器（2026-10-20 晚）

- 取景模式：`CaptureMode { Window, Normal }`，`_captureMode` 每次 F1 从配置 `defaultCaptureMode` 重置（`window` 默认 / `normal`）。`Tab` 调 `ToggleCaptureMode()`：清空选区、窗口引用与候选列表，`RefreshHoverFromCursor()` 后重绘，日志 `Overlay: mode=window|normal`；切换后在顶部徽标下方弹出 `CopyToast`（「已切换到常规截图」/「…原选区已清除」），避免连续按 Tab 却看不出模式变化。
- 两种模式**都**做窗口预高亮与单击选中：`HitTestHover` 不再对 Normal 提前返回，`ContainsNear(rect,x,y,2)` 给窗口右/下边界留 2px 容差（`WindowRect.Contains` 本身不含右/下边界），`HandlePressed` 统一 `_clickedWindow = _hoverWindow`。因此 Normal 模式也能从屏幕/窗口边界开始拖框、单击选中整个窗口并看到高亮；`_subregionCandidate` 仍仅 Window 模式，Normal 不会走窗口局部重建。
- `CaptureSelectionAsync()` 的第一分支是模式闸门：`Normal` 直接 `CropFullScreenSelection()`（DIP×scale、夹在显示器内、`_full.Clone`），**不调用** `WindowCapture.CaptureAsync`，所以右键菜单、悬停提示、下拉框、鼠标指针都按冻结画面原样裁；`Window` 仍走窗口局部/窗口本体重建。
- 旧 Tab「是否识别悬浮窗」已合并进窗口模式：`HitTestHover` 不再按 `IsOverlayClass` 过滤，`_includeOverlays`、`captureOverlayWindows`、设置页 `OverlayToggle` 全部删除。取景器顶部 `ModeBadge` 常显「窗口截图 · Tab 切换常规」/「常规截图 · Tab 切换窗口」。
- 取色器：`LoupeSampleSize=19`、`LoupeCell=10`、`LoupePixelSize=190`；`UpdateColorLoupe` 把 DIP 坐标换成冻结图像素，`LockBits(19×19)` 一次拷进复用的 `_loupeSample`，中心 BGRA 生成 `_loupeHex`/`_loupeRgb`，再展开到复用的 `_loupePixels` 并写入常驻 `WriteableBitmap`；`LoupePanel` 跟随光标并按边界翻转/夹取。十字准星为中心像素黑白双框。
- 资源：取色器只多 190×190 的 WriteableBitmap（约 145 KB）与两块 byte 缓冲，没有新增整屏位图；锁的是 19×19 小块，不逐像素 `GetPixel`。选区存在或拖拽中隐藏放大镜；`Dismiss()` 一并隐藏并停掉 toast 计时器。
- 复制色值：`Ctrl+C` 有选区 → `ConfirmAsync()`（仍复制截图）；无选区 → `CopyLoupeColorAsync()` 调 `ClipboardService.SetTextAsync(_loupeHex)`，日志 `Overlay: color copied #RRGGBB`，底部 `CopyToast` 显示 1.4s。
- 图标：`tools/gen_icon.py` 改为以 256×256 为基底再 `save(..., sizes=[16,20,24,30,32,40,48,64,128,256])`；旧代码把 16×16 当基底，`sizes` 参数实际只产出 16×16，所以标题栏/托盘发糊。新 `Assets/app.ico` 155941 字节，Pillow 校验含 10 档。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误；Debug `IshWY.exe --smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。Tab/取色器/Ctrl+C 的真实交互待主人实测（按约定不做合成输入）。

### 默认截图模式、子对话框与重操作内存回收（2026-10-20 晚补）

- 默认模式：配置项 `defaultCaptureMode`（`window` / `normal`）；设置 → 常规的 `DefaultModeCombo` 选择后立即写 config 并刷新提示，日志 `CaptureOptions: defaultMode=...`。每次按截图热键由 `CaptureOverlayWindow.Prepare` 读取该值决定起始模式，Tab 仍只做本次取景的临时切换。设置项旁写明「取景中按 Tab 临时切换，切换会清除已有选区」。
- 子对话框自动识别：`WindowEnumerator` 先 `EnumWindows`，再对每个可见顶层窗做 `EnumChildWindows`；只把 `#32770` 且宽≥120、高≥60 的子窗当作独立候选，插入到父窗之前（Z 序命中优先）。这样屏幕中间的错误框不会被父窗口吞掉；`WindowInfo.IsChild` 供调试日志使用。DWM `DWMWA_EXTENDED_FRAME_BOUNDS` 返回空矩形时回退 `GetWindowRect`。
- 边界起拖：取景器的 Pointer 事件从 `Layer` 移到根 `Root`，避免点在非命中子元素时丢事件；`HandlePressed` 不再立即把整窗矩形设为选区，而是先清空选区，等到 `HandleReleased` 且 `!_dragMoved` 时才把 `_clickedWindow` 的整窗矩形设为选区。单击（未拖动）才选整窗。**仅移动 Pointer 事件不够**：取景窗 Win32 非客户区 1px 仍会把屏幕边缘按下截走，真正修复见下一节 `MakeClientAreaFullWindow()`。
- 命中日志：`UpdateHover` 在悬停目标变化时输出 `Overlay: hover 0x... class="..." child=false rect=... title="..."` 或 `Overlay: hover none`，排查「为什么选不到某个窗口」先看这行。
- 重操作内存：贴图拖动 `SetWindowPos` 增加 `SWP_NOREDRAW`；`ExitEditMode` 在重绘前调用 `MaybeShrinkDestDib()`，回收标注工具栏撑大的 DIB；`ApplyEdit` 先释放 `_baseScaled` 再解码全分辨率源图，避免三张大图同时存活；`WindowsOcrEngine` 的 `InMemoryRandomAccessStream` / `DataWriter` 改为 `using`，`OcrResultWindow.Closed` 清空 `TextOutput/StatusText/EngineText`。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067 ×2），`IshWY.exe --smoke` 退出 0。主人报的「移动/标注/OCR 后停在 ~177 MB、重复到 184–192 MB」待本轮实测复核；不做合成输入测试。

### 取景窗非客户边框与全屏点击穿透层（2026-10-20 深夜补）

- 根因一：取景窗虽已 `SetBorderAndTitleBar(false,false)`，Win32 仍留 1px 非客户区。对取景 hwnd 发 `WM_NCHITTEST`，四边/四角返回 18（`HTBORDER`）、只有内部返回 1（`HTCLIENT`）；鼠标在屏幕边缘按下落到非客户区，XAML 根 `Root` 收不到 `PointerPressed`。客户区同时只有 2558×1598，冻结图 2560×1600 被拉伸，取色器按 DIP×`_scale` 取样在右/下边缘偏移——两个症状同一根因。
- 修复：`KeyInterceptor` 增加 `MakeClientAreaFullWindow()`：`WndProc` 对 `WM_NCCALCSIZE`（wParam≠0）返回 0、对 `WM_NCHITTEST` 一律返回 `HTCLIENT`，再 `SetWindowPos(..., SWP_FRAMECHANGED)` 让新客户区生效；`CaptureOverlayWindow` 构造时调用。两个分支只受 `_borderlessClient` 控制，`WM_KEYDOWN`/`WM_HOTKEY` 拦截链不变。
- 根因二：`Prepare` 只过滤 cloaked/自身/零宽窗口，Z 序最前的全屏点击穿透层（DSH 歌词插件 `dancing-lyrics-toast`，ex-style `0x08080028` = `NOACTIVATE|LAYERED|TRANSPARENT|TOPMOST`，rect 0,0 2561×1529）永远是 `_hoverCandidates[0]`，高亮画在屏幕边缘看不见；滚轮能循环到真实窗口，于是表现为「只能用滚轮选」。
- 修复：`Prepare` 增加 `w.ProcessId != Environment.ProcessId` 与 `!IsFullScreenClickThrough(w)`；后者只排除面积 ≥ 显示器 85% 的 `IsClickThrough` 窗口，桌面歌词/宠物等小点击穿透窗仍保留为滚轮候选（普通悬停不会选中它们，`WS_EX_TRANSPARENT` 本就不收鼠标）。
- 取色器：`UpdateColorLoupe` 改用 `Root.ActualWidth/ActualHeight` 把 DIP 映射到 `_full` 像素（`dipX * _full.Width / rootW`），并把取样点夹在 `_full.Width-1/Height-1`；不再依赖 `_scale`，客户区与冻结图差 1–2px 时也不漂移。面板翻转/夹取逻辑不变。
- 验证：构建 0 错误（仅既有 CS0067 ×2），`--smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。对运行中 PID 13932 的取景 hwnd `0x3A140C` 发 `WM_NCHITTEST`，`(0,0)/(1,1)/四边/中心` 全部返回 1（修复前为 18）。该窗口当时隐藏停在 `-10000,-10000 202×56`，所以只验证了 hit-test 分支；真实取景尺寸下的边缘拖拽与悬停待主人实测。

### 取景器键盘直达：Tab / Ctrl+C / Esc 不依赖焦点（2026-10-20 深夜）
- 症状：按 F1 后立刻连按 Tab 多次没反应；先单击选窗或拖一次选区，再快速按两次 Tab 才切换；无选区 Ctrl+C 复制色值同样要“先点一下”；Esc 偶发关不掉。不是按键映射问题，而是取景窗亮窗后不保证拿到前台/XAML 焦点：`InitializeFrozen` 的 `Activate()` 与 `FocusSink.Focus()` 都可能被系统静默拒绝；顶层 `KeyInterceptor` 只挂 top-level WndProc，WinUI 的键盘焦点实际落在 `InputSiteWindowClass` 子窗（`GetGUIThreadInfo` 实测 active=0/focus=0 时消息到不了）；全局 Esc 的 `RegisterHotKey` 又是在 `Prepare` 之后才注册且失败不记日志，留下启动空窗。
- 修复：复用 `EscOverPinHook` 的 `WH_KEYBOARD_LL`，取景窗可见且激活时由钩子直接吞键并投递 UI 线程。`TryHandleOverlayKey` 白名单：Tab 仅无修饰；Esc/Enter/O 仅无 Ctrl/Shift；C/S 仅 Ctrl 无 Shift；Alt/Win 组合一律放行（Alt+Tab / Win+Tab 仍可切走），Ctrl+Tab 与 Shift+Tab 也放行。命中时返回 1 消费按键，否则底下的 Chrome/编辑器还会收到同一个 Tab/Ctrl+C；钩子回调只 `DispatcherQueue.TryEnqueue`，不写盘、不等待。
- 生命周期：构造时 `SetOverlayKeyHandler(OnOverlayHotkey)`；`InitializeFrozen` 在 `Activate()` 前 `SetOverlayKeysActive(true)`；`Dismiss` 与 `Closed` 立刻置 false 并清 handler；`Activated` 失活（Alt+Tab、另存为对话框）置 false，重新激活且未关闭再置 true。若不在失活时放行，会把别的程序的 Tab/Ctrl+C/Esc 一起吞掉。
- 分发：`HandleOverlayHotkey` 先守 `_closed`，然后 Esc→`Cancel()`、Enter→有选区 `ConfirmAsync()`、Tab→`ToggleCaptureMode()`、O→有选区 `OcrSelectionAsync()`、Ctrl+C→有选区 `ConfirmAsync()` 无选区 `CopyLoupeColorAsync()`、Ctrl+S→有选区 `SaveSelectionAsync()`。原 `KeyInterceptor`/`FocusSink`/`KeyboardAccelerator` 保留兜底；钩子消费后不会二次触发。
- 验证：构建 0 错误（仅既有 CS0067 ×2），`--smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。日志新增 `Hook: overlay key vk=0x9 ctrl=False`，可确认按键走的是钩子而不是焦点路径。真实按键待主人实测（按约定不做合成输入）。

### 贴图滚轮缩放内存（2026-10-20 晚）

- 主人复测发现：整进程静止约 142 MB，但滚轮缩放贴图时会在约 120 MB ↔ 190–200 MB 间波动；标注编辑也可能摸到 200 MB。
- 已做的低风险收敛（`NativePinWindow.cs`）：
  1. `OnWheel` 不再先 `MoveWindow` 再 `UpdateLayeredWindow`。`UpdateLayeredWindow` 传 `psize` 本身就会调整分层窗口尺寸/位置，连续滚轮时每帧只提交一次表面，减少 DWM 为每个中间尺寸保留 surface。
  2. 高清结算从 150ms 提到 350ms，快速滚轮期间不再反复触发全窗双三次重绘。
  3. `RenderHighQuality` 在 `_opacity >= 0.999` 时走 `CompositingMode.SourceCopy` + 无 `ImageAttributes` 的直拷；只有半透明贴图才走 `SourceOver` + `ColorMatrix`，避免不透明常见路径创建逐像素混合临时面。
  4. `MaybeShrinkDestDib()` 在 `WM_APP_ZOOM_SETTLED` 调用：窗口面积不到 DIB 分配面积一半时释放并重建 DIB（日志 `Pin zoom: 收缩 DIB A×B -> C×D`），避免滚轮放大一次后 16 MB 的 DIB 永久驻留。
- 尚未做的根治方案：把贴图 HWND 缩到「逻辑图像矩形 ∩ 显示器工作区」，只渲染可见部分。全尺寸透明窗口即使不画屏外像素，系统分层表面仍按整窗分配，所以单靠裁绘制不省表面内存。视口化会牵动拖动、滚轮锚点、标注坐标、工具栏与右键菜单，需单独立项并让主人复测。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误，`IshWY.exe --smoke` 退出 0；滚轮峰值是否下降待主人用任务管理器复测，代理不做合成输入测试。
## 5. 踩坑与约定

- **WinUI 3 焦点不可靠**：取景窗/贴图等没有普通 XAML 焦点的窗口用 Win32 消息层拦截（`KeyInterceptor` / `WH_KEYBOARD_LL`）；但带 XAML 焦点控件的普通窗口（如 `HistoryWindow`）键盘焦点实际落在 `InputSiteWindowClass` 子窗，top-level `WndProc` 收不到 `WM_KEYDOWN`，应改用根元素 `PreviewKeyDown`。不要依赖 XAML `KeyboardAccelerator` 的隐式焦点。取景窗亮窗后可能既没前台也没 XAML 焦点，Tab/Ctrl+C/Esc 不能只靠焦点路径；现在由 `EscOverPinHook` 在取景激活期间直通，钩子失活时必须立刻放行，避免吞掉别的程序的按键。
- **Esc 必须统一仲裁**：贴图轮询、OCR 热键、贴图 WndProc 都会收到 Esc，不能各关各的。全部走 `EscRouter.Dispatch()`：编辑中只退编辑；OCR 与贴图重叠关最上层、不重叠先关 OCR。**不要再改回全局钩子**——钩子会被系统摘除，表现为 Esc 随机失灵；也不要让贴图/OCR 各自 `RegisterHotKey(VK_ESCAPE)`，会 1409 冲突并一次关两窗。
- **另存为不是「只写文件」**：取景器的「保存」/Ctrl+S 与回车确认共用同一份 PNG；保存成功后必须同时写剪贴板与截图历史，否则主人会认为「保存了但粘贴不出来、历史也没有」。2026-10-20 已修。
- **`unsafe` 类里不能写 async**：`NativePinWindow` 是 `unsafe partial class`，`async` 方法会报 CS4004「无法在不安全的上下文中等待」；另存为只能 fire-and-forget，把异常留在 `FileSaveService` 内部记录。
- **FileSavePicker 必须绑定拥有者 HWND**：WinUI 3 直接 `new FileSavePicker()` 在桌面应用里会因没有窗口句柄失败；必须 `InitializeWithWindow.Initialize(picker, ownerHwnd)`。`HistoryWindow` 用自身 HWND，取景器/贴图用各自 HWND，不要用主窗句柄。
- **历史窗不会自动刷新新截图**：`HistoryWindow` 仅在打开/重新加载时读目录；截图保存后如果历史窗正开着，需要关掉重开才能看到新条目（当前已知行为，未做实时监听）。
- **贴图不要常驻全分辨率解码位图**：`_pngBytes` 是压缩 PNG，按需 `DecodeSource()` 即可；常驻 `_source` 会为每张贴图多占 16 MB 以上，且标注时再叠加 `_baseScaled`/临时全窗 Bitmap，峰值会到 220 MB。渲染要用 `CreateDestBitmap()` 复用 `_destBits`，不要每帧新建全窗 Bitmap。
- **编辑模式平移不要抢左键**：左键是绘制；平移用中键或右键，先判断工具栏/取色面板区域，再 `SetCapture`。右键松开时只有 `_panMoved == false` 才弹菜单，否则用户拖完贴图会意外弹出菜单。
- **分层窗口缩放别重复提交尺寸**：`UpdateLayeredWindow` 传 `psize` 就会调整窗口尺寸/位置，滚轮里再先 `MoveWindow` 是两次 DWM 表面更新，连续滚轮会堆中间尺寸 surface。只保留 `UpdateLayeredWindow`，并用 350ms 结算避免滚动中途反复高清重绘。
- **常驻 DIB 只增不减会吃内存**：`EnsureDestDib` 为了复用只扩容不缩容，贴图放大到工作区后再缩回，16 MB 的 DIB 仍常驻。停手后按面积比（<50%）触发 `MaybeShrinkDestDib` 重建；不要每个滚轮事件都重建。
- **不透明贴图别走 ColorMatrix**：100% 不透明度用 `CompositingMode.SourceCopy` 直拷即可；只有半透明才需要 `SourceOver` + `ImageAttributes`/`ColorMatrix`，否则常见的 100% 路径会多出逐像素混合临时面。
- **常规截图也要有窗口预高亮**：窗口预览/单击选中不是窗口模式专属；`HitTestHover` 不要在 Normal 提前返回。`WindowRect.Contains` 不含右/下边界，边界拖框需要 2px 容差，否则从窗口右下边缘起手会落空。
- **历史窗关闭要清空缩略图**：只 `Close()` 窗口不会释放 `_items` 里的 BitmapImage；在 `Closed` 里把 `HistoryGrid.ItemsSource=null` 并 `_items.Clear()`。
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
- **HistoryWindow 的 Esc 不能在 top-level WndProc 拦截**：WinUI 3 的键盘焦点在 `InputSiteWindowClass` 子窗（`GetGUIThreadInfo` 可确认，焦点 HWND 不是 `WinUIDesktopWin32WindowClass` 顶层窗），所以顶层 `KeyInterceptor` 收不到 Esc；用根 `Grid` 的 `PreviewKeyDown` 才能在 XAML 路由里关闭窗口，并在 `Loaded`/`Activated` 时把焦点放到 `HistoryGrid`（空列表也能聚焦），否则窗口刚打开、焦点尚未进入 XAML 树时 Esc 仍可能丢失。`KeyInterceptor` 保留作兜底。
- **历史保存必须 best-effort**：`CaptureHistoryService` 的任何异常都只能记日志并返回 null，不能让截图/剪贴板失败；关闭 `saveHistory` 只停止新写入，不删除已有文件。
- **延时截图只有一个倒计时源**：`_delayCts` 必须在 UI 线程上判断/赋值，重复按热键先取消而不是开第二个计时器；到点采集前不要清空 cts，否则取消逻辑会失效。全屏快速截图不要接入延时，否则会破坏「按下即得」的预期。
- **模式闸门必须放在 `CaptureSelectionAsync()` 最前面**：常规模式一旦先走 `_subregionWindow`/`_clickedWindow`，就会重新调用 `WindowCapture.CaptureAsync` 重建窗口，菜单、悬停提示、下拉框、指针全都截不到；这正是主人报的「能看到但抓不到」。新增取图来源时先判断模式，再碰窗口分支。
- **取色器不要逐像素 `GetPixel`**：19×19 用 `LockBits` 一次拷进复用缓冲；`WriteableBitmap.PixelBuffer.AsStream()` 每次写前 `Position=0`，否则第二次开始写偏。`_loupePixels/_loupeSample` 与 `WriteableBitmap` 都常驻复用，不要跟随鼠标每帧 new。
- **多尺寸 ICO 的基底必须够大**：Pillow 的 `save(format='ICO', sizes=[...])` 只会从当前图缩出不超过基底尺寸的档位；用 16×16 当基底再传 10 个 sizes，最终只得到 16×16。先裁方图并 resize 到 256×256，再生成全部尺寸。
- **鼠标指针只合成一次**：WGC 采集已显式关闭指针捕获，不要再打开，否则 WGC 与 GDI 兜底会出现双指针；`GetIconInfo` 返回的 `hbmMask`/`hbmColor` 必须 DeleteObject，`GetHdc` 必须 `ReleaseHdc`。
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
- [x] 另存为 / 历史标注 / 标注完成保存 —— 2026-10-20 完成：`FileSaveService` + 取景器 `Ctrl+S`/保存按钮 + 历史「标注」「另存为」+ 贴图右键「另存为…」+ 标注工具栏 id 24；历史/贴图另存为字节与源一致，主人实测取景器 Ctrl+S 后剪贴板与历史均正常
- [x] 主人实测 Esc 仲裁：标注中 Esc 只退编辑；OCR+贴图重叠只关最上层、不重叠先关 OCR —— 2026-10-20 20:34 主人三步实测通过
- [x] 内存与发布体积优化：已完成取景器位图释放、贴图按需解码+复用 DIB、历史缩略图释放、发布包裁剪（zip 69.6 MB）。2026-10-20 20:34 主人复测：整进程约 142 MB 基本稳定，滚轮/标注峰值与取景初帧命中均通过；WGC 常驻池（约 48 MB）保留
- [x] 多屏 DPI：按主人决定取消（无第二显示器，不使用虚拟显示器）
- [x] 清理：`clip_probe*.png` 已删除（含桌面/内部对话截图，不入库）；旧 `AnnotationWindow` 待清理。`tools/DxgiProbe`、`tools/WgcProbe`、`tools/KbProbe` 保留，作为采集引擎与键盘钩子排障工具
- [x] 发布体积裁剪：`scripts/publish.ps1` 删除未加载的 AI/ML/Search/Widgets 组件（39 文件 55.3 MB），zip 92.5→69.6 MB；细粒度替换元包因 H.NotifyIcon targets 冲突不可行，保留元包+裁剪
- [ ] 主人实测延时截图（设置 3/5/10 秒、按热键后开菜单/悬停、再按取消、托盘入口）与鼠标指针开关（F1 + 全屏）；构建已 0 错误、`--smoke` 通过，未做合成输入测试
- [x] 实现「取景器跟随鼠标的放大取色器 + Ctrl+C 复制色值」：19×19 放大、HEX/RGB、十字准星、无选区复制色值（有选区仍复制截图）；构建 0 错误，待主人实测
- [ ] 主人复测 2026-10-20 深夜二次修复：从屏幕绝对边缘（0,0/右/下）与窗口边框起拖、单击选窗、悬停预高亮、屏幕中间 `#32770` 与其他悬浮窗可悬停高亮、滚轮循环仍可用、取色器在右/下边缘不漂移且 `Ctrl+C` 色值正常。已修 1px 非客户边框 + 全屏点击穿透层过滤；构建 0 错误、`--smoke` 通过，未做合成输入测试
- [ ] 主人复测贴图滚轮缩放内存：已去掉 `MoveWindow` 双提交、高清结算 350ms、不透明 `SourceCopy`、缩小后回收 DIB；峰值是否下降待任务管理器实测；若仍偏高再评估「只渲染可见区域」的视口化改造
- [ ] 主人复测取景器键盘直达：F1 后**不点鼠标**连续按 Tab（应每次切换并弹提示）、无选区 Ctrl+C（应复制色值）、Esc（应关闭）；再拖选区/选窗后重复 Tab 与 Ctrl+C；切到别的程序（例如 Alt+Tab）后按 Tab/Ctrl+C/Esc，确认不再被取景窗吞掉。构建 0 错误、`--smoke` 通过；未做合成输入测试
- [x] 主人实测取景器 Tab 在窗口截图/常规截图间切换 —— 2026-10-20 深夜确认切换与提示正常，窗口模式仍吸附抗遮挡、常规模式能截到右键菜单/悬停提示/指针
- [ ] 主人复测取色器跟随与 `Ctrl+C` 色值（右/下边缘漂移已修）、标题栏/托盘图标清晰度
- [x] 主人实测默认截图模式设置 —— 2026-10-20 深夜确认：设置 → 常规切到常规/窗口后，每次按截图热键从对应模式起步，Tab 临时切换与提示正常；`data/config.json` 的 `defaultCaptureMode` 随选择更新
- [ ] 主人复测重操作内存：移动贴图、标注、OCR 后是否仍停在 ~177 MB 并随重复操作升到 ~200 MB；2026-10-20 深夜主人实测峰值后约 5 分钟回落到 ~100 MB，符合 Windows 工作集裁剪/GC，不是无界泄漏。本轮已加 `SWP_NOREDRAW`、退出标注回收 DIB、`ApplyEdit` 提前释放 `_baseScaled`、OCR 流 using 与结果清空
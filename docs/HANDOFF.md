# 实践小梅IshWY（IshWY）交接文档（2026-10-22）

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
| M0 | 托盘（H.NotifyIcon，`ContextMenuMode=SecondWindow`）、全局热键（历史默认 F1，v1.0.0 起 Alt+A）、设置窗、自定义背景、便携式 config（程序目录 data） |
| M1 | 冻屏取景：Pointer 拖拽框选、窗口吸附（青色）/悬浮类橙色、滚轮循环遮挡窗口、**Tab 在窗口截图/常规截图间切换**、全局 Esc、回车/双击复制、窗口局部截图 |
| M2 | WGC 窗口捕获（PrintWindow 优先，失败回退 WGC 窗口条目）+ D3D 设备缓存 |
| M3 | 贴图：**原生分层窗口** `NativePinWindow`（真透明 per-pixel alpha）、拖动、滚轮缩放（占位框 + 停手高清精修）、右键菜单、Esc/双击关闭、任务栏置顶防拉锯 |
| M4 | **原位标注**：贴图右键「标注」，窗口内自绘图标工具栏（矩形/箭头/画笔/文字/马赛克/序号 + 颜色 + HSV 取色 + 撤销/清空/完成）、按宽度自适应的分组工具栏、原生 IME 文字输入与 10–96 字号、完成=烘焙+更新贴图+复制 |
| M5 | OCR：取景中按 `O`、贴图右键「识别文字」、托盘/设置「从剪贴板识别文字」；Windows OCR 优先（清理中文字间空格），Umi-OCR 可选（1224 端口，多端点探测 + 双 API 兼容）；结果窗可编辑复制，Esc 关闭 |
| ★ | 全屏快速截图：默认 F2（v1.0.0 起 Alt+D，可录制组合键）、静默进剪贴板、剪贴板写入 6 次重试 |
| ★ | 窗口局部截图：窗口内拖框，从未遮挡窗口本体裁剪，OCR 同样适用 |
| ★ | 开机自启动：设置 → 常规开关，写 HKCU Run（值 `IshWY`，命令 `IshWY.exe --startup`）；登录后静默驻留托盘，单实例唤醒 |
| ★ | 截图历史：框选/全屏截图自动写入 `data\history`（最多 30 张，可关闭）；历史窗支持复制/贴图/标注/另存为/删除/清空/打开文件夹 |
| ★ | 常规截图模式：取景中 Tab 从窗口重建切到「按冻结画面所见即所得」，右键菜单/悬停提示/下拉框/指针都能截到；窗口模式仍保留抗遮挡 |
| ★ | 放大取色器：跟随鼠标 19×19 放大 + 十字准星 + HEX/RGB；无选区 Ctrl+C 复制色值，有选区仍复制截图 |
| ★ | 高分辨率应用图标：`tools/gen_icon.py` 以 256×256 为基底生成 16–256 共 10 档 ICO |
| ★ | 另存为：取景器 `Ctrl+S`/「保存」、历史「另存为」、贴图右键「另存为…」、标注工具栏「另存」；取景器保存后仍复制并写历史 |
| ★ | 延时截图与鼠标指针：设置 → 常规可选 0/3/5/10 秒，等待中再按截图热键取消；托盘有「延时截图」「取消延时截图」；可让截图取景与全屏截图包含当前指针 |
| ★ | 2A 贴图工作流：贴图管理、锁定/穿透、吸附（2026-10-21 晚） | ✅ 完成（2026-10-22 00:xx 主人验收「验收通过，做的非常好」）：`PinService` 注册表 + `PinManagerWindow` 集中管理全部贴图；右键菜单可锁定位置、点击穿透；拖动 8px 吸附工作区/其他贴图并画 `PinSnapGuide`；左右半屏切换历史贴图、底部序号提示；管理/历史窗打开时临时降下置顶贴图，关闭后恢复 |
| ★ | 贴图指针锚定滚轮缩放（2026-10-21 晚） | ✅ 完成（主人验收）：`WM_MOUSEWHEEL` 用屏幕坐标计算归一化锚点，缩放后贴图仍停在指针下；上限仍夹到当前显示器工作区，保留分层 DIB 内存保护 |
| ★ | 新贴图初始大小设置（2026-10-21 晚） | ✅ 完成（主人验收）：设置 → 贴图新增 `pinInitialScale`（原图 80–100%，默认 100%）；`ComputeInitialZoom` 先取偏好再按工作区夹取，新贴图/左右切换历史生效；NumberBox 改 Inline 避免 PopupWindowSiteBridge 悬浮箭头关不掉 |
| ★ | 热键删除 ✕（2026-10-22 凌晨） | ✅ 完成（主人验收）：设置 → 截图与热键五行各加 28×28 ✕；删除后行显示「未设置」、✕ 变灰，钩子与 RegisterHotKey 同时注销；截图/全屏写 `-1` 哨兵，重启不回退 F1/F2；「修改」可重新录制 |

## 3. 关键文件

```
src/PrtScr.App/
  App.xaml.cs                 日志(加锁)、启动、单实例(\Local\IshWY.Activate)、--startup 静默模式、注入 WGC/DXGI 日志钩子、安装键盘钩子
  MainWindow.xaml(.cs)        设置窗、托盘、五个动作的热键录制器与删除 ✕、延时截图/取消入口、鼠标指针开关、默认截图模式、开机自启动开关、截图历史开关/窗口、贴图管理入口、OCR 状态、Umi 端口
  Services/ConfigService.cs   便携 config.json
  Services/StartupService.cs  HKCU Run 开机自启动（值 IshWY，命令 exe --startup；移动目录后自动刷新）
  Services/CaptureHistoryService.cs  本机截图历史（data\history，每类默认 30 张；写入/读取/删除/清空/修剪）
  Services/FileSaveService.cs 系统 FileSavePicker 另存为（PNG/JPEG，JPEG 质量 92；取消/异常只记日志）
  Services/HotkeyService.cs   RegisterHotKey 封装：截图/全屏/贴图/OCR/切换档位五个 id（+ 取景取消 id），统一 RegistrationFailed 提示；删除热键时注销并返回「未设置」
  Services/KeyInterceptor.cs  窗口消息层拦截 Enter/Esc/Tab/O（可选）；MakeClientAreaFullWindow 把 1px 非客户区并进客户区
  Services/EscRouter.cs       全局 Esc 统一仲裁：编辑中只退编辑、OCR/贴图重叠关最上层、不重叠先关 OCR
  Services/EscOverPinHook.cs  WH_KEYBOARD_LL：全屏截图组合键 + 取景窗可见期间的 Tab/Esc/Enter/O/Ctrl+C/Ctrl+S 直通（不依赖取景窗焦点；钩子内自记账修饰键 + 300ms 去抖；回调内不写盘、日志走队列；兜底命中时请求重装自愈）
  Services/FullscreenBypass.cs 抢前台逼游戏退出独占全屏（1×1 全透明分层窗 + AttachThreadInput，等 500ms，退出时还原前台/还原最小化）
  Services/ClipboardService.cs 剪贴板重试
  Services/CaptureService.cs  采集编排；CaptureMonitorBestAsync 四层引擎；DrawCursorIfEnabled 统一合成鼠标指针
  Services/OcrUiService.cs / PinService.cs / AnnotationRenderer.cs / AnnotationModels.cs（PinService 维护所有贴图的 PinRecord 注册表、快照、命令与 PinsChanged；PastePng / PastePngAndEdit 可从历史字节直接贴图，不经过剪贴板）
  Views/CaptureOverlayWindow.xaml(.cs)  冻屏取景 + 窗口局部；`CaptureMode`（Window/Normal，Tab 切换，起始模式读 `defaultCaptureMode`）；窗口预高亮/单击选窗两种模式共用；指针事件挂在根 `Root`；选区右上角「保存」/`Ctrl+S` 另存为，保存后仍复制并写历史；`Ctrl+C`（无选区）复制放大镜色值；19×19 取色器复用 WriteableBitmap 与 byte 缓冲
  Views/HistoryWindow.xaml(.cs)         截图历史窗（缩略图网格、复制/贴图/标注/另存为/删除/清空/打开文件夹；根 Grid PreviewKeyDown 处理 Esc；AppWindow.SetIcon 与主窗一致；关闭时清空 ItemsSource/缩略图集合）
  Views/PinManagerWindow.xaml(.cs) + PinCardView.cs  贴图管理窗：全部贴图卡片、复制/另存/OCR/关闭/恢复交互、隐藏/显示/关闭全部；打开时把所有贴图降到 NOTOPMOST，关闭后恢复 TOPMOST
  Views/PinSnapGuide.cs        拖动贴图时的 8px 吸附参考线（两条 1px 青色分层线，松手即隐藏）
  Views/NativePinWindow.cs + .Edit.cs   原生贴图窗口 + 原位标注（Esc 关闭 = 自身 30ms 轮询后交给 EscRouter 仲裁；右键菜单可锁定/点击穿透/裁剪/另存/打开历史；标注工具栏「完成」= 烘焙+复制、「另存」= 烘焙+复制+另存为；不常驻全分辨率解码位图，按需解码并直接绘制到常驻 DIB；指针锚定滚轮缩放、左右半屏切换历史、8px 吸附、拖动加 SWP_NOREDRAW，退出标注回收 DIB；工具栏按宽度分两排并在文字工具下显示字号步进器；窗口类挂 IDC_ARROW 光标，文字输入框按图片原点定位、输入中可用滚轮调字号）
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
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误，`IshWY.exe --smoke` 退出 0（四个热键均 `register=0x1`）；真实热键、倒计时与指针合成已由主人实测通过（按约定不做合成输入测试）。

### 常规截图与放大取色器（2026-10-20 晚）

- 取景模式：`CaptureMode { Window, Normal }`，`_captureMode` 每次按截图热键从配置 `defaultCaptureMode` 重置（当时 `window` 默认 / `normal`；v1.0.0 起默认 `normal`）。`Tab` 调 `ToggleCaptureMode()`：清空选区、窗口引用与候选列表，`RefreshHoverFromCursor()` 后重绘，日志 `Overlay: mode=window|normal`；切换后在顶部徽标下方弹出 `CopyToast`（「已切换到常规截图」/「…原选区已清除」），避免连续按 Tab 却看不出模式变化。
- 两种模式**都**做窗口预高亮与单击选中：`HitTestHover` 不再对 Normal 提前返回，`ContainsNear(rect,x,y,2)` 给窗口右/下边界留 2px 容差（`WindowRect.Contains` 本身不含右/下边界），`HandlePressed` 统一 `_clickedWindow = _hoverWindow`。因此 Normal 模式也能从屏幕/窗口边界开始拖框、单击选中整个窗口并看到高亮；`_subregionCandidate` 仍仅 Window 模式，Normal 不会走窗口局部重建。
- `CaptureSelectionAsync()` 的第一分支是模式闸门：`Normal` 直接 `CropFullScreenSelection()`（DIP×scale、夹在显示器内、`_full.Clone`），**不调用** `WindowCapture.CaptureAsync`，所以右键菜单、悬停提示、下拉框、鼠标指针都按冻结画面原样裁；`Window` 仍走窗口局部/窗口本体重建。
- 旧 Tab「是否识别悬浮窗」已合并进窗口模式：`HitTestHover` 不再按 `IsOverlayClass` 过滤，`_includeOverlays`、`captureOverlayWindows`、设置页 `OverlayToggle` 全部删除。取景器顶部 `ModeBadge` 常显「窗口截图 · Tab 切换常规」/「常规截图 · Tab 切换窗口」。
- 取色器：`LoupeSampleSize=19`、`LoupeCell=10`、`LoupePixelSize=190`；`UpdateColorLoupe` 把 DIP 坐标换成冻结图像素，`LockBits(19×19)` 一次拷进复用的 `_loupeSample`，中心 BGRA 生成 `_loupeHex`/`_loupeRgb`，再展开到复用的 `_loupePixels` 并写入常驻 `WriteableBitmap`；`LoupePanel` 跟随光标并按边界翻转/夹取。十字准星为中心像素黑白双框。
- 资源：取色器只多 190×190 的 WriteableBitmap（约 145 KB）与两块 byte 缓冲，没有新增整屏位图；锁的是 19×19 小块，不逐像素 `GetPixel`。选区存在或拖拽中隐藏放大镜；`Dismiss()` 一并隐藏并停掉 toast 计时器。
- 复制色值：`Ctrl+C` 有选区 → `ConfirmAsync()`（仍复制截图）；无选区 → `CopyLoupeColorAsync()` 调 `ClipboardService.SetTextAsync(_loupeHex)`，日志 `Overlay: color copied #RRGGBB`，底部 `CopyToast` 显示 1.4s。
- 图标：`tools/gen_icon.py` 改为以 256×256 为基底再 `save(..., sizes=[16,20,24,30,32,40,48,64,128,256])`；旧代码把 16×16 当基底，`sizes` 参数实际只产出 16×16，所以标题栏/托盘发糊。新 `Assets/app.ico` 155941 字节，Pillow 校验含 10 档。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误；Debug `IshWY.exe --smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。Tab/取色器/Ctrl+C 已由主人实测通过（按约定不做合成输入）。

### 默认截图模式、子对话框与重操作内存回收（2026-10-20 晚补）

- 默认模式：配置项 `defaultCaptureMode`（`window` / `normal`，v1.0.0 起默认 `normal`）；设置 → 常规的 `DefaultModeCombo` 选择后立即写 config 并刷新提示，日志 `CaptureOptions: defaultMode=...`。每次按截图热键由 `CaptureOverlayWindow.Prepare` 读取该值决定起始模式，Tab 仍只做本次取景的临时切换。设置项旁写明「取景中按 Tab 临时切换，切换会清除已有选区」。
- 子对话框自动识别：`WindowEnumerator` 先 `EnumWindows`，再对每个可见顶层窗做 `EnumChildWindows`；只把 `#32770` 且宽≥120、高≥60 的子窗当作独立候选，插入到父窗之前（Z 序命中优先）。这样屏幕中间的错误框不会被父窗口吞掉；`WindowInfo.IsChild` 供调试日志使用。DWM `DWMWA_EXTENDED_FRAME_BOUNDS` 返回空矩形时回退 `GetWindowRect`。
- 边界起拖：取景器的 Pointer 事件从 `Layer` 移到根 `Root`，避免点在非命中子元素时丢事件；`HandlePressed` 不再立即把整窗矩形设为选区，而是先清空选区，等到 `HandleReleased` 且 `!_dragMoved` 时才把 `_clickedWindow` 的整窗矩形设为选区。单击（未拖动）才选整窗。**仅移动 Pointer 事件不够**：取景窗 Win32 非客户区 1px 仍会把屏幕边缘按下截走，真正修复见下一节 `MakeClientAreaFullWindow()`。
- 命中日志：`UpdateHover` 在悬停目标变化时输出 `Overlay: hover 0x... class="..." child=false rect=... title="..."` 或 `Overlay: hover none`，排查「为什么选不到某个窗口」先看这行。
- 重操作内存：贴图拖动 `SetWindowPos` 增加 `SWP_NOREDRAW`；`ExitEditMode` 在重绘前调用 `MaybeShrinkDestDib()`，回收标注工具栏撑大的 DIB；`ApplyEdit` 先释放 `_baseScaled` 再解码全分辨率源图，避免三张大图同时存活；`WindowsOcrEngine` 的 `InMemoryRandomAccessStream` / `DataWriter` 改为 `using`，`OcrResultWindow.Closed` 清空 `TextOutput/StatusText/EngineText`。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067 ×2），`IshWY.exe --smoke` 退出 0。主人复测：重复操作峰值约 200 MB，停止约 5 分钟后回落到约 100 MB，属工作集裁剪/GC，不是无界泄漏。

### 取景窗非客户边框与全屏点击穿透层（2026-10-20 深夜补）

- 根因一：取景窗虽已 `SetBorderAndTitleBar(false,false)`，Win32 仍留 1px 非客户区。对取景 hwnd 发 `WM_NCHITTEST`，四边/四角返回 18（`HTBORDER`）、只有内部返回 1（`HTCLIENT`）；鼠标在屏幕边缘按下落到非客户区，XAML 根 `Root` 收不到 `PointerPressed`。客户区同时只有 2558×1598，冻结图 2560×1600 被拉伸，取色器按 DIP×`_scale` 取样在右/下边缘偏移——两个症状同一根因。
- 修复：`KeyInterceptor` 增加 `MakeClientAreaFullWindow()`：`WndProc` 对 `WM_NCCALCSIZE`（wParam≠0）返回 0、对 `WM_NCHITTEST` 一律返回 `HTCLIENT`，再 `SetWindowPos(..., SWP_FRAMECHANGED)` 让新客户区生效；`CaptureOverlayWindow` 构造时调用。两个分支只受 `_borderlessClient` 控制，`WM_KEYDOWN`/`WM_HOTKEY` 拦截链不变。
- 根因二：`Prepare` 只过滤 cloaked/自身/零宽窗口，Z 序最前的全屏点击穿透层（DSH 歌词插件 `dancing-lyrics-toast`，ex-style `0x08080028` = `NOACTIVATE|LAYERED|TRANSPARENT|TOPMOST`，rect 0,0 2561×1529）永远是 `_hoverCandidates[0]`，高亮画在屏幕边缘看不见；滚轮能循环到真实窗口，于是表现为「只能用滚轮选」。
- 修复：`Prepare` 增加 `w.ProcessId != Environment.ProcessId` 与 `!IsFullScreenClickThrough(w)`；后者只排除面积 ≥ 显示器 85% 的 `IsClickThrough` 窗口，桌面歌词/宠物等小点击穿透窗仍保留为滚轮候选（普通悬停不会选中它们，`WS_EX_TRANSPARENT` 本就不收鼠标）。
- 取色器：`UpdateColorLoupe` 改用 `Root.ActualWidth/ActualHeight` 把 DIP 映射到 `_full` 像素（`dipX * _full.Width / rootW`），并把取样点夹在 `_full.Width-1/Height-1`；不再依赖 `_scale`，客户区与冻结图差 1–2px 时也不漂移。面板翻转/夹取逻辑不变。
- 验证：构建 0 错误（仅既有 CS0067 ×2），`--smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。对运行中 PID 13932 的取景 hwnd `0x3A140C` 发 `WM_NCHITTEST`，`(0,0)/(1,1)/四边/中心` 全部返回 1（修复前为 18）。该窗口当时隐藏停在 `-10000,-10000 202×56`，所以只验证了 hit-test 分支；真实取景尺寸下的边缘拖拽、单击选窗与悬停高亮已由主人实测通过。

### 取景器键盘直达：Tab / Ctrl+C / Esc 不依赖焦点（2026-10-20 深夜）
- 症状：按 F1 后立刻连按 Tab 多次没反应；先单击选窗或拖一次选区，再快速按两次 Tab 才切换；无选区 Ctrl+C 复制色值同样要“先点一下”；Esc 偶发关不掉。不是按键映射问题，而是取景窗亮窗后不保证拿到前台/XAML 焦点：`InitializeFrozen` 的 `Activate()` 与 `FocusSink.Focus()` 都可能被系统静默拒绝；顶层 `KeyInterceptor` 只挂 top-level WndProc，WinUI 的键盘焦点实际落在 `InputSiteWindowClass` 子窗（`GetGUIThreadInfo` 实测 active=0/focus=0 时消息到不了）；全局 Esc 的 `RegisterHotKey` 又是在 `Prepare` 之后才注册且失败不记日志，留下启动空窗。
- 修复：复用 `EscOverPinHook` 的 `WH_KEYBOARD_LL`，取景窗可见且激活时由钩子直接吞键并投递 UI 线程。`TryHandleOverlayKey` 白名单：Tab 仅无修饰；Esc/Enter/O 仅无 Ctrl/Shift；C/S 仅 Ctrl 无 Shift；Alt/Win 组合一律放行（Alt+Tab / Win+Tab 仍可切走），Ctrl+Tab 与 Shift+Tab 也放行。命中时返回 1 消费按键，否则底下的 Chrome/编辑器还会收到同一个 Tab/Ctrl+C；钩子回调只 `DispatcherQueue.TryEnqueue`，不写盘、不等待。
- 生命周期：构造时 `SetOverlayKeyHandler(OnOverlayHotkey)`；`InitializeFrozen` 在 `Activate()` 前 `SetOverlayKeysActive(true)`；`Dismiss` 与 `Closed` 立刻置 false 并清 handler；`Activated` 失活（Alt+Tab、另存为对话框）置 false，重新激活且未关闭再置 true。若不在失活时放行，会把别的程序的 Tab/Ctrl+C/Esc 一起吞掉。
- 分发：`HandleOverlayHotkey` 先守 `_closed`，然后 Esc→`Cancel()`、Enter→`ConfirmCurrentTargetAsync()`（有选区直接确认；无选区但有高亮窗口时把该窗口合成整窗选区再确认）、Tab→`ToggleCaptureMode()`、O→有选区 `OcrSelectionAsync()`、Ctrl+C→有选区 `ConfirmAsync()` 无选区 `CopyLoupeColorAsync()`、Ctrl+S→有选区 `SaveSelectionAsync()`。原 `KeyInterceptor`/`FocusSink`/`KeyboardAccelerator` 保留兜底；钩子消费后不会二次触发。
- 验证：构建 0 错误（仅既有 CS0067 ×2），`--smoke` 日志含四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`。日志新增 `Hook: overlay key vk=0x9 ctrl=False`，可确认按键走的是钩子而不是焦点路径。主人 2026-10-20 23:17 真键复测：连续 Tab、无选区 Ctrl+C、Esc、拖选/选窗后重复按键、Alt+Tab 透传全部通过。

### 截图历史按模式/进程归档与取景器进程标签（2026-10-20 深夜）
- 取景器右键取消：`HandlePressed` 先取 `e.GetCurrentPoint(Root)`，若 `Properties.IsRightButtonPressed` 直接 `Cancel()` 并返回，不设置 `_pressPoint`、不抓指针；日志 `Overlay: right-click cancel`。只对取景器生效，贴图右键仍是菜单。
- 进程标签：`SizeTip` 内改成横向 `StackPanel`（`SizeText` + `ProcessText`）。窗口模式下 `SetProcessLabel()` 显示「进程 {ProcessName}」，常规模式、自由框选或进程名为空时折叠；只显示进程名，不显示图标。提示框宽度改用 `MeasureSizeTipWidth()` 实测，右边缘不再溢出。
- 进程标签可见性（2026-10-21 凌晨修复）：`UpdateVisuals` 原来在「无选区」分支无条件 `SizeTip.Visibility = Collapsed`，把悬停时刚显示的尺寸/进程提示又折掉，主人只看到高亮框。现用 `tipShown` 记录本帧是否已有提示，只在没有悬停/选区时折叠；悬停提示文字同时加「回车截此窗」。
- 历史元数据：每次保存 PNG 同时写同名 `.json`（`HistoryMetadata.Version/Mode/ProcessName/WindowTitle/CapturedAt`）。`HistoryCaptureMode { Window, Normal, Legacy }`；窗口截图记录 `WindowInfo.ProcessName` 与标题，常规/全屏记 `Normal`，没有 sidecar 的旧文件归 `Legacy`（旧文件名标签无法区分窗口与常规）。`ResolveHistoryTarget()` 在 `ConfirmAsync`/`SaveSelectionAsync` 里决定模式：窗口模式下自由拖框最终回退整屏时按常规截图记。
- 分类保留：`HistoryItem.CategoryKey` 为 `window:{process}` 或 `mode:{mode}`；`CaptureHistoryService.PerCategoryLimit` 读 `historyPerCategory`（1–200，默认 30）。`PruneLocked` 按 `CategoryKey` 分组，每组只留最新 N 张，同时删 PNG 与 sidecar；设置页改数字后调用 `PruneNow(value)` 立即清理。窗口截图下每个进程各自 30 张，常规/早期各一组。
- 历史窗：左侧 `CategoryList`（全部 / 窗口截图 + 各进程子项 / 常规截图 / 早期未分类，带计数），右侧 `HistoryGrid` 改成 `SelectionMode="Extended"`，`DecodePixelWidth=240`。分类切换只从内存重排并用 `_loadVersion` 取消过期缩略图加载；「全部」最多展示 120 张（`DisplayCap`），各分类计数仍完整。
- 另存为：保留单张 `另存为`；新增 `批量另存为` 走 `FileSaveService.SaveManyAsync()` + `FolderPicker`，把当前选中条目导出到同一文件夹，重名用 `GenerateUniqueName`。底部同时加 `全选`。`SelectedViews()` 取全部选中，单张动作取第一个。
- 设置：`HistoryToggle` 文案改为「按模式 / 进程分类」，新增 `HistoryLimitBox`（NumberBox 1–200）+ `HistoryLimit_ValueChanged`；`_suppressHistoryLimit` 防止初始化回写。`RefreshHistoryStatus` 说明窗口按进程、常规/早期按模式。
- 品牌：去掉「轻量」表述；设置窗副标题改为「Ishmael is watching you · 分窗口截屏 · 窗口内容捕获 · 贴图 · OCR」，README 首段改为普通「Windows 截图工具」。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067 ×2）；`--smoke` 通过（四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`）。主人 2026-10-20 23:56 复测：右键取消、历史分类计数、单张/批量另存为均通过；进程名当时未显示，已修复待复测。

### 进程标签提示框可见性与回车截悬停窗口（2026-10-21 凌晨补）
- 症状：窗口模式下悬停/选中窗口时看不到进程名；滚轮循环到窗口后按 `Enter` 没有反应，必须先单击出选区。
- 可见性根因：`UpdateVisuals` 的悬停分支虽然设置了 `SizeTip.Visibility = Visible`，后面的「无选区」else 又无条件 `Collapsed`，整个提示框（尺寸 + 进程名）被折掉。修复：局部 `tipShown`，悬停或选区分支置 true，只有两者都没有时才折叠；两分支都先显示再 `MeasureSizeTipWidth()`，首帧宽度不再回退 200 造成右缘溢出。
- 回车截悬停窗口：新增 `ConfirmCurrentTargetAsync()`。无选区但有 `_hoverWindow` 时，先合成 `_clickedWindow=_hoverWindow`、`_selection=DipRect(FrameBounds)`、`_hasSelection=true`、`_subregionWindow=null`，再走原 `ConfirmAsync()`；窗口模式抓窗口位图，常规模式按该矩形裁冻结帧，历史分类仍由 `ResolveHistoryTarget()` 判定。无选区也无悬停时直接返回，不关窗。
- 视觉卡顿修复（2026-10-21 00:10）：合成状态后**不再调用 `UpdateVisuals()`**。否则捕获的几十毫秒内会先画出选区边框/遮罩（亮框缩到窗口），主人看到“先亮框、再截图”的跳变；现在保持原悬停高亮，`ConfirmAsync` 完成后直接 `Dismiss()`，中间不重绘。
- 三个 Enter 入口统一：`EscOverPinHook` 分发、`FocusSink_KeyDown`、XAML `KeyboardAccelerator` 全部改调 `ConfirmCurrentTargetAsync()`；`_confirming` 防重入。悬停日志 `Overlay: hover ...` 增加 `process=...`。
- 验证：构建 0 错误（仅既有 CS0067 ×2）；`--smoke` 通过，日志 00:00:43 四个热键 `register=0x1`、`Capture: overlay pre-created`，00:00:46 `OnLaunched: smoke exiting`。真机验收待主人执行。

### 主窗口信息架构与热键表（2026-10-21 中午）
- 目标：把首页从一长列设置项改为「先常用操作、再分组设置」，让四个主操作与当前热键在第一屏可见。
- 首页 2×2 操作卡片：窗口截图 / 全屏截图 / 贴图 / 文字识别，分别调用 `CaptureNow_Click`（复用 `OnHotkeyPressed`）、`FullScreen_Click`、`PastePin_Click`、`OcrClipboard_Click`；每张卡右上角是热键角标 `ScreenshotCardHotkey` / `FullCardHotkey` / `PinCardHotkey` / `OcrCardHotkey`。
- 布局细节：角标用 `HorizontalAlignment=Right` + `VerticalAlignment=Top` 叠加在卡片内，不占 Grid 列宽；否则说明文字会被挤成省略号（窗口卡片曾只剩「滚轮选窗，回车…」）。说明统一 `MaxLines=2` + `TextTrimming=CharacterEllipsis`，窗口卡片用 `&#10;` 显式两行。
- 历史摘要卡读取 `RefreshHistoryStatus()`，显示张数、分类规则、每类保留数；`打开历史` 走原 `OpenHistory_Click`。
- `SetStatus()` 同时写 `HotkeyStatusText` 与首页 `HomeStatusText`，热键录制/取消/失败状态不再只在折叠区可见；`SyncHotkeyTexts()` 在构造与 `ApplyHotkey` 后把四个当前组合键同步到卡片角标。
- 热键表：`操作 / 当前组合键 / 修改` 三列四行，录制按钮文案平时「修改」、录制中「按键…」，Esc 取消；状态行在表下方。
- 折叠分组：截图与热键（默认展开）、截图行为（延时 / 默认模式 / 指针）、截图历史、启动与托盘、外观·自定义背景、OCR 引擎、关于。只改 `MainWindow.xaml` / `MainWindow.xaml.cs`，配置键与行为未变。
- 验证：构建 0 错误（仅既有 CS0067 ×2）；Debug PID 9804 实际启动，PrintWindow 780×900 截图确认四张卡、历史摘要、提示行完整，窗口卡片两行说明不截断。主人 2026-10-21 12:39 验收「都没问题」。

### 取景器底部 HUD 与选区操作条（2026-10-21 中午）
- 目标：取景时不再把快捷键说明塞进尺寸提示，也不靠顶部模式徽标；改为底部常驻 HUD 说明「当前模式 + 此刻能做什么」，并把选区动作放到选区旁边。
- HUD：`HudBar` 是底部居中的半透明圆角条（#E6202020，`IsHitTestVisible=False`，距底 16px）。左侧一个蓝色描边框（#3300C8FF 底、#5900C8FF 边）内并排「Tab」与 `HudModeText`，两者同为 12px SemiBold、#FF8FE3FF，共用同一色框，Tab 不再有灰色键帽；右侧 `HudHintText` 白字，末尾固定「右键 / Esc 退出」。顶部旧 `ModeBadge`/`ModeText` 已删除。
- 状态提示：`UpdateHud(width,height)` 按状态切换——拖拽中「松开 完成选区」/「拖拽 框选 · 单击 选中窗口」；有选区「回车 截图 · O 识别 · Ctrl+S 保存 · Tab 切换」；悬停窗口「滚轮 切换窗口 · 回车 截取 · 拖拽 截取局部」（常规模式为「单击 选中 · 拖拽 自由框选」）；空闲「拖拽 框选区域 · 滚轮 选窗 · Tab 切换模式」。HUD 用无限高测量实际宽度，再居中放到距底 16px，返回实际高度给其他浮层避让。
- 选区操作条：`SelectionActions` 放「截图」（Accent）/「识别」/「保存」三个紧凑按钮，默认浮在选区下方外侧；下方被 HUD 顶住则优先放选区上方，选区太矮才回退到选区内右上角。尺寸/进程提示只显示「W × H + 进程」，与操作条并排时放左下、操作条在右下；横向放不下时提示翻到选区上方。
- 浮层避让：取色放大镜的翻转/夹取改用 `bottomLimit = rootH - (HudBar 高度 + 20)`，底部不再压住 HUD；放大镜面板加「Ctrl+C 复制色值」提示；模式切换/复制提示 toast 固定在 HUD 上方（`rootH - toastH - hudH - 36`），不再按顶部位置弹。
- 行为不变：Enter / O / Ctrl+S / 右键 / Esc 仍走原钩子与分发，操作条只是可见入口；`Capture_Click` 调 `ConfirmAsync`，`Ocr_Click` 只对有选区时识别。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067 ×2）；`--smoke` 通过（四个热键 `register=0x1`、`Capture: overlay pre-created`、`OnLaunched: smoke exiting`）。主人 2026-10-21 13:16 验收「完全没问题」；此前七项布局检查已通过。

### 贴图标注工具栏、文字输入与光标（2026-10-21 下午）
- 工具栏重排：旧实现用一条 `ToolbarOrder` 自动换行，窄贴图会叠出三四行并把分层窗口撑高。现在 `BuildToolbarLayout()` 先试「文本动作」再三档字号（正常 30/4/8/28/18/8/42、紧凑 26/3/6/25/14/6/38、极紧凑 22/2/5/22/12/4/34，均乘 DPI），工具与颜色保持一组，撤销/清空/取消/完成/另存固定单独一排；动作仍放不下才退到图标按钮。绘制与扩窗共用同一份布局，不再出现换行规则错位。
- 工具栏视觉：深色底 #EE242424，活动工具/完成用圆角强调色，色块白边 + 选中环；动作条右侧显示当前工具、颜色和快捷键，空间不足逐级缩短。自定义调色盘颜色不再误高亮黑色快速色（`_colorArgbColorId()` 遍历 `QuickColors`，找不到返回 -1）。
- 快捷键：标注中 `Enter` 完成、`Ctrl+S` 另存、`Ctrl+Z` 撤销、`Esc` 取消；`ApplyEdit()` 用 `_applyingEdit` 防重复提交。
- 文字字号：`_textSize` 默认 18，范围 10–96、步长 2；选中文字工具后动作条右侧出现「− 字号 N +」，点击只改字号、不销毁正在输入的 `EDIT`；输入框内滚轮（`KeyInterceptor` 转发 `WM_MOUSEWHEEL`）和贴图上滚轮都可调。`CommitTextEditor()` 存 `_textSize / EditScale`，最终烘焙字号与预览一致。
- 输入框位置：`BeginTextEditor()` 原来用 `_winY + y`，但 `y` 是图片局部坐标；工具栏在顶部时 `_winY` 比图片原点高一个工具栏高度，输入框会整体上移。改用 `_imgX/_imgY + 图片局部坐标` 后，输入框左上角贴住鼠标点击点，最终标注位置不变。
- 光标消失：贴图窗口类 `WNDCLASSW` 没有设置 `hCursor`，`DefWindowProc` 在 `WM_SETCURSOR` 时会把光标隐藏，表现为鼠标停在贴图上“被盖住”。`RegisterClassOnce()` 现在用 `LoadCursorW(0, IDC_ARROW)` 设置箭头光标；文字 `EDIT` 仍使用系统类自带的 I 型光标。CsWin32 的 `NativeMethods.txt` 不生成 `LoadCursor`/`LoadCursorW`（试过会 CS0117），因此用 `[DllImport("user32.dll", EntryPoint = "LoadCursorW")]`。
- 验证：构建 0 错误（仅既有 CS0067 ×2），`--smoke` 通过；Debug PID 42244 由主人实测：输入框不再上下偏移、字号按钮/滚轮可用、最终位置与字号正确、光标不再消失。主人 2026-10-21 13:46 验收「完美通过」。

### 贴图滚轮缩放内存（2026-10-20 晚）

- 主人复测发现：整进程静止约 142 MB，但滚轮缩放贴图时会在约 120 MB ↔ 190–200 MB 间波动；标注编辑也可能摸到 200 MB。
- 已做的低风险收敛（`NativePinWindow.cs`）：
  1. `OnWheel` 不再先 `MoveWindow` 再 `UpdateLayeredWindow`。`UpdateLayeredWindow` 传 `psize` 本身就会调整分层窗口尺寸/位置，连续滚轮时每帧只提交一次表面，减少 DWM 为每个中间尺寸保留 surface。
  2. 高清结算从 150ms 提到 350ms，快速滚轮期间不再反复触发全窗双三次重绘。
  3. `RenderHighQuality` 在 `_opacity >= 0.999` 时走 `CompositingMode.SourceCopy` + 无 `ImageAttributes` 的直拷；只有半透明贴图才走 `SourceOver` + `ColorMatrix`，避免不透明常见路径创建逐像素混合临时面。
  4. `MaybeShrinkDestDib()` 在 `WM_APP_ZOOM_SETTLED` 调用：窗口面积不到 DIB 分配面积一半时释放并重建 DIB（日志 `Pin zoom: 收缩 DIB A×B -> C×D`），避免滚轮放大一次后 16 MB 的 DIB 永久驻留。
- 尚未做的根治方案：把贴图 HWND 缩到「逻辑图像矩形 ∩ 显示器工作区」，只渲染可见部分。全尺寸透明窗口即使不画屏外像素，系统分层表面仍按整窗分配，所以单靠裁绘制不省表面内存。视口化会牵动拖动、滚轮锚点、标注坐标、工具栏与右键菜单，需单独立项并让主人复测。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误，`IshWY.exe --smoke` 退出 0；滚轮峰值是否下降待主人用任务管理器复测，代理不做合成输入测试。
### 截图档位、快捷键目录、历史交互与贴图菜单/裁剪（2026-10-21 晚）
- 截图档位：`AppConfig.CaptureProfile` 为 `normal` / `quick`；`CaptureProfileCombo` 保存默认档位，`Alt+Shift+C`（`modeHotkeyVk=0x43`、`modeHotkeyModifiers=5`）走 `HotkeyService` 的 mode id 6 与 `EscOverPinHook` 的 `ModeSwitchPressed` 双通道，游戏前台也能收到。切换只改本次运行的 `_runtimeCaptureProfile`，设置下拉是下次启动默认值；旧的 `persistCaptureProfile` 与“切换模式后记住选择”已删除。`OnHotkeyPressed` 在快捷档位直接调用 `OnFullScreenPressed`，固定全屏热键仍保留。
- 快捷键目录：设置页「完整快捷键目录」按「取景器 / 贴图·裁剪 / 截图历史」列固定语义；全局组合键仍在上面表格录制。取景器键由 `EscOverPinHook` 直通，不依赖焦点；`Esc` / 鼠标右键始终作为兜底。
- 历史交互：浏览模式单击只更新状态；双击直接 `PinViewAsync` 贴图；右键 `HistoryGrid_RightTapped` 进入多选并选中该项；选择模式下左键切换勾选、`Ctrl+A` 全选、`Delete` 删除、`Esc` 先退多选再关窗。选中卡片用 2.5px 强调色边框 + 右上勾选徽标 + 半透明底色；缩略图只加载当前分类，「全部」最多 120 张。
- 贴图菜单：`PinMenuWindow` 改为 WinUI 3 窗口自身就是面板，`MoveAndResize` 到指针处，`DesktopAcrylicBackdrop` + DWM 圆角 + `BORDER_COLOR_NONE`；`KeyInterceptor.MakeClientAreaFullWindow()` 去掉 1px 非客户区白边。`WH_MOUSE_LL` 在菜单打开期间调用 `PinClickGuard.Suppress(450ms)`，关闭菜单的那一下不会被贴图误当成左右半屏切换。命令：复制/标注/裁剪/识别/另存/打开历史/不透明度/关闭。
- 贴图裁剪：`PinCropWindow` 是 WinUI 3 选区层，结果只带回源 PNG 与全分辨率矩形；`NativePinWindow.StartCrop()` 在后台线程解码、裁剪、PNG 编码，再回 UI 线程创建偏移新贴图 / OCR / 另存。`HistoryCaptureMode.PinCrop` 与 `historyPinCrop` 控制「贴图裁剪」历史分类。裁剪时 Enter/O/Ctrl+S/Esc 由裁剪窗自己的 `WH_KEYBOARD_LL` 消费，不依赖前台。
- 贴图历史打开：`PinService.HistoryRequested` → `MainWindow.ShowHistoryAbovePins()`，先用 `HWND_NOTOPMOST` 降下所有贴图，再把历史窗设为 `HWND_TOPMOST`；关闭历史时 `RestorePinsAfterHistory()` 恢复。
- 历史切换提速：`NativePinWindow.Navigate` 只解码一次源图，`SetWindowPos(SWP_NOREDRAW)` 后 `RenderAndPresent(highQuality:true, image)`，不再先解码两遍；主人确认明显跟手。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067 ×2）；`--smoke` 通过（四热键 + mode 热键 `register=0x1`、overlay pre-created）。菜单白边、自定位、点外关闭与四项行为由主人确认；其余档位/历史/裁剪行为主人已验收。

### 2A 贴图管理、指针锚定缩放、初始大小与热键删除（2026-10-22 凌晨）

- 贴图注册表：`PinService` 维护 `ConcurrentDictionary<nint,PinRecord>`，记录 HWND、压缩 PNG、像素尺寸、屏幕矩形、透明度、锁定/穿透/隐藏状态、来源分类与历史序号；`SnapshotPins()` 给管理窗，`PinsChanged` 合并刷新，`SendCommand` 统一投递 `NativePinWindow.WmAppPinCommand`。
- 贴图管理：`PinManagerWindow` 1080×760，卡片显示缩略图、像素尺寸、透明度、状态与来源；单张可复制/另存/OCR/关闭/恢复交互，头部可隐藏/显示/关闭全部；入口只在主页历史摘要与托盘，不新增全局热键。打开时把所有贴图降到 `HWND_NOTOPMOST`，关闭后恢复 `HWND_TOPMOST`；管理窗内 Focus 用 `HWND_TOP`，避免选中的贴图盖住管理窗。
- 贴图状态：右键菜单新增锁定位置与点击穿透；锁定时禁止拖动、滚轮缩放、左右半屏切换与双击关闭，但右键菜单仍可用；穿透设置 `WS_EX_TRANSPARENT`。左上角显示「锁 / 穿」徽标，状态变化写回 `PinService`。
- 吸附：拖动结束按 8×DPI 吸附当前显示器工作区与其他可见贴图；`PinSnapGuide` 用两条 1px 青色分层线显示吸附边，松手即隐藏。
- 指针锚定缩放：`WM_MOUSEWHEEL` 的 lParam 是屏幕坐标；先算 `anchorX/Y = clamp((screen - _winX)/_winW, 0, 1)`，更新窗口尺寸后令 `_winX = round(screenX - anchorX*_winW)`，Y 同理。缩放上限仍为 `min(8, workW/pixelW, workH/pixelH)`，保留分层 DIB 内存保护。
- 初始大小：`AppConfig.PinInitialScale`（80–100，默认 100）；`ComputeInitialZoom()` 先取 `PinInitialScale/100`，再与工作区 fit 比例取 min，新贴图与左右切换历史图片生效，已有贴图不变。设置页 NumberBox 使用 `SpinButtonPlacementMode=Inline`（Compact 会创建独立 `PopupWindowSiteBridge`，悬浮箭头可能关不掉）。
- 热键删除：设置页五个热键行各加 28×28 ✕；`ClearHotkey` 清空配置、注销 `RegisterHotKey` 与 `EscOverPinHook`，行内显示「未设置」并禁用 ✕。窗口/全屏截图写 `-1` 哨兵，`ResolveScreenshotVk/ResolveFullscreenVk` 不再回退旧 `hotkeyKey/fullScreenHotkeyKey`；贴图/OCR/切换档位写 0。`HotkeyService.Describe(0,0)` 返回「未设置」。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0，五个热键均 `register=0x1`；2026-10-22 00:xx 主人验收「验收通过，做的非常好」。

### v1.0.0 初始状态与瘦身发布（2026-10-22 凌晨）

- 主人确认的首次启动状态：窗口截图 `Alt+A`、全屏快速截图 `Alt+D`、默认「常规截图」、默认 Windows OCR（Umi 默认关闭）、开机自启动默认关闭、关闭主窗口驻留托盘。只影响全新配置；旧配置不迁移、不覆盖。
- `Services/ConfigService.cs`：`HotkeyVk=0x41`(65)、`HotkeyModifiers=1`、`FullHotkeyVk=0x44`(68)、`FullHotkeyModifiers=1`、`DefaultCaptureMode="normal"`、`PreferUmiOcr=false`。`hotkeyKey`/`fullScreenHotkeyKey` 仅保留为旧配置回退，不能写成 `"A"`/`"D"`——`HotkeyService.KeyNameToVirtualKey` 只认 F1–F12/Pause/Scroll，其余一律回退 F1，所以默认组合键必须走显式 vk 字段。
- UI 文案同步：设置页鼠标指针说明改为「截图取景和全屏截图都生效」；OCR 说明标明 Windows OCR 默认、Umi 需手动开启；默认模式提示与历史空状态改为「按截图热键」，不再写死 F1。
- 打包：`PrtScr.App.csproj` 增加 `BuiltInComInteropSupport=true`、`JsonSerializerIsReflectionEnabledByDefault=true`（partial trim 下 WGC 的 built-in COM 与 ConfigService 的反射 JSON 都依赖它们）；`scripts/publish.ps1` 使用 `-p:PublishTrimmed=true -p:TrimMode=partial`，并继续删除 57 个未加载的 App SDK 可选组件（约 65 MB）、37 个 `.winmd`（约 2.4 MB），语言目录只留 en-us/en-GB/zh-CN/zh-TW（约 3.2 MB）。
- 产物：`artifacts/IshWY-1.0.0-win-x64/` 72.7 MB；`artifacts/IshWY-1.0.0-win-x64.zip` 30,588,110 字节（约 29.2 MB），sha256 `ad1e6568f61860f8c40d94e2c307702a9577272b7fdf3331d66466c90235fd50`。
- 发布：提交 `30b93e4` → https://github.com/GRmaid/PrtScr-IshWY/releases/tag/v1.0.0 ，资产 `IshWY-1.0.0-win-x64.zip` 下载回读 30,588,110 字节、sha256 一致；旧 `v0.1.0` Release 已删除，保留历史 tag 供 compare。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`--smoke` 退出 0。全新目录首次启动生成的 `data/config.json` 实测为 `hotkeyVk=65/mod=1`、`fullHotkeyVk=68/mod=1`、`defaultCaptureMode=normal`、`preferUmiOcr=false`，日志五个热键均 `register=0x1`。包内解压后再次 `--smoke` 同样退出 0；trim 后 WGC 2560×1600 帧与 Windows OCR 探针此前已通过。启动内存 `ws=150MB private=98MB`，仅作 smoke 记录，不代表主人性能验收。
- 发布注意：`--smoke` 会像正常启动一样读取并写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\IshWY`，把启动路径改成当前测试副本的 exe。对复制/解压出来的包做 smoke 前先临时删除该 Run 值，测完恢复原路径；否则主人下次登录会启动到临时目录。本轮已按此恢复。

### 取景选框移动、操作条、历史计数与命名（2026-10-22 下午）

- 常规截图选区可移动：`CaptureOverlayWindow` 新增 `_movingSelection` / `_moveStartSelection`；只有 Normal 模式且按在已有选区内部才进入移动，`HandleMoved` 按总位移移动并把选框 clamp 在 Root 内，`HandleReleased` 保留选区；`ResetSessionState` 与 `ToggleCaptureMode` 都会清掉移动状态。窗口模式的选区仍代表窗口目标，不开放移动。
- 选区操作条：`CaptureOverlayWindow.xaml` 由「截图 / 识别 / 保存」改为「✕ / ✓ / 文字识别 / 保存」，四个按钮带 tooltip；X 调 `Cancel()` 取消整次取景，✓ 截图并复制，O / Ctrl+S 与按钮等价；操作条宽度兜底从 176 调到 260。
- 历史计数即时刷新：`CaptureHistoryService` 新增 `HistoryChanged` 事件，在 `AddAsync`、`PruneNow`、`Delete`、`Clear` 后触发；`MainWindow` 订阅后按 UI 线程调用 `RefreshHistoryStatus()`，不再等打开历史窗才更新。
- 命名统一：主页历史摘要按钮、托盘菜单、`PinManagerWindow` 标题/页头与 README 统一为「截图/贴图管理」（2026-10-22 深夜按主人要求改为「屏幕贴图管理」）。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 38248 已启动。主人 2026-10-22 晚验收五项（含常规选区拖动、操作条、历史计数、命名与设置项）「做的好，你要我测的五个都很符合我的预期」。

### OCR 窗口、历史实时刷新与清空分类（2026-10-22 晚）

- OCR 结果窗补图标：`OcrResultWindow` 构造时调用 `AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"))`，与设置/历史/管理窗一致；标题栏左上角不再空白。
- OCR 不再带起设置窗：设置页的剪贴板 OCR 与 OCR 热键改为 `OcrUiService.RecognizeAndShowAsync(bytes)`，不传 `ownerHwnd`；`OcrResultWindow` 仅在 owner 非 0 时设置 `GWLP_HWNDPARENT`，避免激活从属 OCR 窗时把 Settings 一并拉到前台。贴图触发的 OCR 仍传贴图 HWND，关闭后焦点回贴图；取景器触发仍不传 owner。
- 历史窗实时刷新：`CaptureHistoryService.HistoryChanged` 继续覆盖添加/修剪/删除/清空；`MainWindow.OnHistoryChanged()` 在 UI 线程刷新设置计数，并调用 `RefreshOpenHistoryWindow()`（`_historyWindow?.RefreshAsync()`）。`HistoryWindow` 自己删除/清空时置 `_suppressExternalRefresh`，防止服务事件触发重复重载。
- 删除与清空分类：`CaptureHistoryService.DeleteMany(IEnumerable<HistoryItem>)` 一次锁内删 PNG + sidecar，锁外只发一次 `HistoryChanged`；`Delete(HistoryItem)` 改为委托它。历史窗「清空」改为「清空分类」，只删 `FilterItems(_selectedCategoryKey)` 的当前分类（「全部」仍清全部），状态显示「已清空「分类」N 张」；设置页清空全部仍走 `CaptureHistoryService.Clear()`。
- 历史底栏重排：从左到右为常用「复制（强调）/ 贴图 / 标注 / 另存为」、批量「批量另存为 / 删除 / 清空分类」、工具「全选 / 打开文件夹 / 关闭」，三组用 1×20 分隔线。当时每个按钮显式设置 `ToolTipService.ToolTip`；WinUI 3 仍会在分类悬停时残留「删除」提示，且默认 `MinWidth=120` 会溢出裁切。后续先把批量另存为并入「另存为」，再彻底移除悬浮提示，见下节。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0（五个热键 `register=0x1`、overlay 预创建、`Memory[startup]: ws=170MB private=101MB managed=2MB`）；Debug PID 39260 已启动，五项交互待主人实测。

### 历史底栏提示、屏幕贴图管理与管理器刷新策略（2026-10-22 深夜）

- 历史底栏第一次改为单行 `ActionHintText` + `Tag`/`PointerEntered` 切换说明，但主人仍看到分类处浮出的「删除」提示，且「批量另存为」被裁成「量另存为」；该方案已被 2026-10-22 深夜的「彻底移除悬浮提示 + 合并另存为」取代，见下节。
- 命名：主页历史摘要按钮、托盘菜单、管理窗标题/页头与 README 从「截图/贴图管理」改为「屏幕贴图管理」。
- 管理器刷新：`PinService.UpsertPin(record, notify=true)`，`NativePinWindow.PublishPinState(bool notify=true)`；拖动结束与缩放结算只更新注册表快照（`notify:false`），不再重建卡片；新建、关闭、左右切换图片、标注/裁剪完成、透明度、锁定/穿透/隐藏等状态变化仍通知。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 11004 已启动，三项交互待主人实测。

### 历史另存为合并与悬浮提示彻底移除（2026-10-22 深夜）

- 症状：主人悬停历史分类或任意底栏按钮时仍会浮出「删除」提示；底栏「批量另存为」只剩「量另存为」。改动前 XAML 中已无 `ToolTipService`，XBF 里也只有按钮的 `Tag` 字符串，判断为 WinUI 3 旧 tooltip/复用路径残留，不再保留任何悬停机制。
- 另存为合并：`HistoryWindow.xaml` 删除独立「批量另存为」按钮，只留一个「另存为」。`SaveAs_Click` 先取 `SelectedViews()`：0 张提示先选图；1 张走 `FileSaveService.SavePngAsync` 单张 `FileSavePicker`；多张调用 `SaveManyAsync(selected)`，内部用 `FileSaveService.SaveManyAsync` + `FolderPicker` 一次导出，重名仍由 `GenerateUniqueName` 处理。
- 悬浮提示：9 个底栏按钮去掉全部 `Tag`/`PointerEntered`/`PointerExited`；底栏上方保留固定说明「选中截图后可复制、贴图、标注或另存为；多选时另存为会批量保存。」；构造时 `ClearBottomBarToolTips()` 遍历 `BottomBar` 的各 `StackPanel`，对每个 `Button` 显式 `ToolTipService.SetToolTip(button, null)`，防止模板/复用残留。
- 状态栏同步：选中分类有图时显示「当前分类 N 张 · 单张另存为，多选批量另存为」。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 42884 已启动，待主人实测。

### 全选开关、批量语义与「删除」提示源（2026-10-22 深夜三）

- 症状：主人验收历史另存为批次后，又反馈三处：全选不能二次取消；多选下复制/贴图/标注/删除语义不明确；历史窗悬停仍浮出「删除」。
- 全选：`HistoryWindow.xaml` 的「全选」按钮加 `x:Name="SelectAllButton"`；`SelectAll_Click`、`Ctrl+A` 与 `SelectAllAccelerator` 统一走 `ToggleSelectAll()`。未全选时进入多选并 `SelectAll()`；已全选时 `ExitSelectionMode()`，按钮文案由 `UpdateSelectionVisuals()` 在「全选 / 取消全选」间切换。
- 空白退出：根 Grid 命名 `RootGrid`，构造时 `AddHandler(UIElement.PointerPressedEvent, ..., handledEventsToo:true)`；`RootGrid_PointerPressed` 在多选模式下排除 `GridViewItem`/`ListViewItem`/`ButtonBase`/`ScrollBar` 后退出多选，`FindAncestor<T>` 沿视觉树判断。
- 批量语义：`SelectedViews()` 按 `CreatedAt` 倒序（最新在前）。复制取 `selected[0]`；标注取 `selected[0]`；贴图多选时逐张 `PinService.PastePngOffset(bytes, created*24, created*24)` 阶梯错开并统计成功/失败；删除仍作用于全部选中；另存为单张/批量不变。
- 「删除」提示源（早期判断，已被下一节推翻）：全仓库仅剩 `MainWindow.xaml` 五个热键删除 ✕ 上的 `ToolTipService.ToolTip="删除这个快捷键"`。已全部删除；`HistoryWindow` 底栏仍显式 `ClearBottomBarToolTips()`，本轮不再新增任何 `Tag`/悬停事件。但历史窗悬停仍出现「删除」，说明真正来源不是按钮 tooltip。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 42180 已启动，待主人实测。

### 历史窗「删除」浮层的真实根因：KeyboardAccelerator 键名提示（2026-10-22 深夜四）

- 症状：删掉全仓库所有 `ToolTipService` 后，主人悬停历史窗仍看到「删除」；此时底栏已是普通按钮，`ClearBottomBarToolTips()` 也无效，提示不来自按钮。
- 根因：`HistoryWindow.xaml` 根 Grid 上的 `KeyboardAccelerator Key="Delete"`。WinUI 3 的 `UIElement.KeyboardAcceleratorPlacementMode` 默认 `Auto`，会把关联加速键的组合显示为控件 tooltip；中文系统把 Delete 渲染成「删除」，所以悬停窗口任意位置都会浮出。
- 修复：根 Grid 设 `KeyboardAcceleratorPlacementMode="Hidden"`，加速键本身仍可用；`CaptureOverlayWindow.xaml` 根 Grid 同样设置，避免 Esc/Enter/Ctrl+S/Ctrl+C 的键名提示。键盘路径不变（`HistoryWindow` 仍由根 `PreviewKeyDown` 处理 Esc/Delete/Ctrl+A）。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 38284，日志 20:04:57 五个热键 `register=0x1`、overlay pre-created；待主人悬停复测。

### 历史窗左键/右键选择说明（2026-10-22 深夜五）

- 需求：主人要求历史窗明确说明「左键选中、右键多选」。
- 实现：`HistoryWindow.xaml` 标题区 `SubtitleText` 下新增固定说明「左键选中；右键进入多选，之后左键勾选/取消；点空白处或 Esc 退出。」12px、0.62 透明度、可换行；选择逻辑、快捷键与底栏均未改。
- 验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`IshWY.exe --smoke` 退出 0；Debug PID 39784 已启动，待主人确认文案位置与可读性。

### v1.0.1 发布与 README 约束（2026-10-22 深夜六）

- 版本：`src/PrtScr.App/PrtScr.App.csproj` 的 `Version` 与 `src/PrtScr.App/app.manifest` 的 `assemblyIdentity version` 由 1.0.0 升到 1.0.1；配置键、默认值均未改。
- 纳入本版的已验收改动：常规截图已有选区可拖动；选区操作条 ✕ / ✓ / 文字识别 / 保存；历史计数即时刷新；「屏幕贴图管理」统一命名；历史底栏固定提示 + 单「另存为」（单张另存、多选批量）；全选二次点击退出、点空白处退出多选、多选复制/标注取最新、贴图全部贴出；`KeyboardAcceleratorPlacementMode="Hidden"` 去掉历史/取景器键名提示；历史标题下新增左键/右键选择说明。
- README 新约束（主人 2026-10-22 定）：只写最新版本的功能与信息，不写版本历史、开发过程、更新记录/开发日志；主人自己改过的段落不得改动。本次只做定向修改，保留「一点碎碎念」「唯一有效的一行」、致谢与 UmiOCR 备注等个人内容。
- 发布验证：`dotnet build PrtScr.sln -c Debug --nologo` 0 错误（仅既有 CS0067×2）；`pwsh -NoProfile -File scripts/publish.ps1 -Version 1.0.1` 通过 PRI/XBF 校验与 `--smoke`；产物 `artifacts/IshWY-1.0.1-win-x64` 72.7 MB、`artifacts/IshWY-1.0.1-win-x64.zip` 29.2 MB；仅有预期 IL2026/IL2104 trim 警告。
- 发布记录：提交、tag、GitHub Release 资产与 sha256 在发布完成后补入本节的下一行与 §6。

## 5. 踩坑与约定

- **WinUI 3 焦点不可靠**：取景窗/贴图等没有普通 XAML 焦点的窗口用 Win32 消息层拦截（`KeyInterceptor` / `WH_KEYBOARD_LL`）；但带 XAML 焦点控件的普通窗口（如 `HistoryWindow`）键盘焦点实际落在 `InputSiteWindowClass` 子窗，top-level `WndProc` 收不到 `WM_KEYDOWN`，应改用根元素 `PreviewKeyDown`。不要依赖 XAML `KeyboardAccelerator` 的隐式焦点。取景窗亮窗后可能既没前台也没 XAML 焦点，Tab/Ctrl+C/Esc 不能只靠焦点路径；现在由 `EscOverPinHook` 在取景激活期间直通，钩子失活时必须立刻放行，避免吞掉别的程序的按键。
- **Esc 必须统一仲裁**：贴图轮询、OCR 热键、贴图 WndProc 都会收到 Esc，不能各关各的。全部走 `EscRouter.Dispatch()`：编辑中只退编辑；OCR 与贴图重叠关最上层、不重叠先关 OCR。**不要再改回全局钩子**——钩子会被系统摘除，表现为 Esc 随机失灵；也不要让贴图/OCR 各自 `RegisterHotKey(VK_ESCAPE)`，会 1409 冲突并一次关两窗。
- **另存为不是「只写文件」**：取景器的「保存」/Ctrl+S 与回车确认共用同一份 PNG；保存成功后必须同时写剪贴板与截图历史，否则主人会认为「保存了但粘贴不出来、历史也没有」。2026-10-20 已修。
- **`unsafe` 类里不能写 async**：`NativePinWindow` 是 `unsafe partial class`，`async` 方法会报 CS4004「无法在不安全的上下文中等待」；另存为只能 fire-and-forget，把异常留在 `FileSaveService` 内部记录。
- **FileSavePicker 必须绑定拥有者 HWND**：WinUI 3 直接 `new FileSavePicker()` 在桌面应用里会因没有窗口句柄失败；必须 `InitializeWithWindow.Initialize(picker, ownerHwnd)`。`HistoryWindow` 用自身 HWND，取景器/贴图用各自 HWND，不要用主窗句柄。
- **历史窗现在会实时刷新**：`CaptureHistoryService.HistoryChanged` 在添加/修剪/删除/清空后触发；`MainWindow.OnHistoryChanged()` 刷新设置计数并调用已打开窗口的 `RefreshAsync()`。窗口自己删除/清空时用 `_suppressExternalRefresh` 防止服务事件重复重载；不要退回「只在打开时读目录」。
- **贴图不要常驻全分辨率解码位图**：`_pngBytes` 是压缩 PNG，按需 `DecodeSource()` 即可；常驻 `_source` 会为每张贴图多占 16 MB 以上，且标注时再叠加 `_baseScaled`/临时全窗 Bitmap，峰值会到 220 MB。渲染要用 `CreateDestBitmap()` 复用 `_destBits`，不要每帧新建全窗 Bitmap。
- **编辑模式平移不要抢左键**：左键是绘制；平移用中键或右键，先判断工具栏/取色面板区域，再 `SetCapture`。右键松开时只有 `_panMoved == false` 才弹菜单，否则用户拖完贴图会意外弹出菜单。
- **分层窗口缩放别重复提交尺寸**：`UpdateLayeredWindow` 传 `psize` 就会调整窗口尺寸/位置，滚轮里再先 `MoveWindow` 是两次 DWM 表面更新，连续滚轮会堆中间尺寸 surface。只保留 `UpdateLayeredWindow`，并用 350ms 结算避免滚动中途反复高清重绘。
- **常驻 DIB 只增不减会吃内存**：`EnsureDestDib` 为了复用只扩容不缩容，贴图放大到工作区后再缩回，16 MB 的 DIB 仍常驻。停手后按面积比（<50%）触发 `MaybeShrinkDestDib` 重建；不要每个滚轮事件都重建。
- **不透明贴图别走 ColorMatrix**：100% 不透明度用 `CompositingMode.SourceCopy` 直拷即可；只有半透明才需要 `SourceOver` + `ImageAttributes`/`ColorMatrix`，否则常见的 100% 路径会多出逐像素混合临时面。
- **常规截图也要有窗口预高亮**：窗口预览/单击选中不是窗口模式专属；`HitTestHover` 不要在 Normal 提前返回。`WindowRect.Contains` 不含右/下边界，边界拖框需要 2px 容差，否则从窗口右下边缘起手会落空。
- **历史窗关闭要清空缩略图**：只 `Close()` 窗口不会释放 `_items` 里的 BitmapImage；在 `Closed` 里把 `HistoryGrid.ItemsSource=null` 并 `_items.Clear()`。
- **历史分类不能靠文件名猜**：旧文件名只有 `full`/`shot`，无法区分窗口截图与常规截图。新保存必须写同名 `.json` sidecar（模式/进程/标题/时间），没有 sidecar 的旧图统一归 `Legacy`；清空/删除时要连 sidecar 一起删，否则会留下孤儿元数据。
- **每类保留数按 `CategoryKey` 分组算**：窗口截图是 `window:{process}`（每个进程独立 30 张），常规/早期是 `mode:{mode}`；不要退回全局 `Take(30)`，否则某一进程会把其他进程的截图挤掉。设置页改数后调用 `PruneNow` 立即生效。
- **批量另存为用 FolderPicker 而不是 FileSavePicker**：多选后逐张写同一文件夹，文件名用 `{Source}-{时间}` 并 `GenerateUniqueName` 防重名；单张「另存为」仍保留 FileSavePicker。
- **取景器右键只在取景器取消**：`HandlePressed` 先判断 `IsRightButtonPressed` 再抓指针；贴图右键是菜单、标注中右键是平移，三者语义不同，不能共用同一个取消入口。
- **进程标签要参与提示框宽度计算**：`SizeTip` 加了进程名后比原来宽，右边缘定位必须用 `MeasureSizeTipWidth()` 实测宽度，否则会超出屏幕。
- **全局热键**：五个动作都用 `RegisterHotKey`（id 1 截图 / 3 全屏 / 4 贴图 / 5 OCR / 6 切换档位），注册失败（1409 = 被占用）必须通过 `RegistrationFailed` 在设置页明确提示，不要静默失败；删除热键时截图/全屏写 `-1` 哨兵，其余写 0，并同步清钩子与 RegisterHotKey。默认组合键不能拍脑袋选字母组合，先用 RegisterHotKey 探针确认本机没被占用（本机 Ctrl+Alt+P/O 已占用）。
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
- **partial trim 必须保留 COM 与反射 JSON 开关**：`PublishTrimmed=true` + `TrimMode=partial` 会裁掉 WGC 依赖的 built-in COM 和 `ConfigService` 的反射序列化，表现为 `Built-in COM has been disabled via a feature switch`（0x80131515）和「配置保存失败: Reflection-based serialization has been disabled」。csproj 必须保留 `BuiltInComInteropSupport=true`、`JsonSerializerIsReflectionEnabledByDefault=true`；改 trim 开关后先 `dotnet clean PrtScr.sln -c Release` 再发布，否则 ILLink 会复用旧产物而 runtimeconfig 已变，看到的现象不可信。
- **默认组合键不能写进旧 key 字段**：`HotkeyService.KeyNameToVirtualKey` 只认 F1–F12/Pause/Scroll，`HotkeyKey="A"` 会静默回退 F1；首次启动默认 Alt+A/Alt+D 必须写显式 `HotkeyVk=65`/`FullHotkeyVk=68` 与 `HotkeyModifiers=1`，旧 `hotkeyKey/fullScreenHotkeyKey` 只作回退。
- **`--smoke` 会改开机自启动路径**：启动流程会按当前 exe 重写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\IshWY`。对复制/解压出来的包做 smoke 前先删除该 Run 值，测完恢复原路径；否则主人下次登录会启动到临时目录。
- **发布包不要带 PDB**：PDB 的 CodeView 会写入构建机绝对路径（含用户目录下的 `.nuget` 缓存路径），DLL 的调试目录也会保留 `obj/...pdb` 路径。`scripts/publish.ps1` 用 `-p:DebugType=None -p:DebugSymbols=false` 发布并兜底删除 `*.pdb`；发布后扫描 zip 中的 PDB、`data/`、日志与绝对路径。
- **HistoryWindow 没有 `Loaded` 事件**：`Microsoft.UI.Xaml.Window` 本身不暴露 `Loaded`；要在构造函数里用 `((FrameworkElement)Content).Loaded += ...`，否则 `CS0103: 当前上下文中不存在名称 Loaded`。
- **HistoryWindow 的 Esc 不能在 top-level WndProc 拦截**：WinUI 3 的键盘焦点在 `InputSiteWindowClass` 子窗（`GetGUIThreadInfo` 可确认，焦点 HWND 不是 `WinUIDesktopWin32WindowClass` 顶层窗），所以顶层 `KeyInterceptor` 收不到 Esc；用根 `Grid` 的 `PreviewKeyDown` 才能在 XAML 路由里关闭窗口，并在 `Loaded`/`Activated` 时把焦点放到 `HistoryGrid`（空列表也能聚焦），否则窗口刚打开、焦点尚未进入 XAML 树时 Esc 仍可能丢失。`KeyInterceptor` 保留作兜底。
- **历史保存必须 best-effort**：`CaptureHistoryService` 的任何异常都只能记日志并返回 null，不能让截图/剪贴板失败；关闭 `saveHistory` 只停止新写入，不删除已有文件。
- **延时截图只有一个倒计时源**：`_delayCts` 必须在 UI 线程上判断/赋值，重复按热键先取消而不是开第二个计时器；到点采集前不要清空 cts，否则取消逻辑会失效。全屏快速截图不要接入延时，否则会破坏「按下即得」的预期。
- **模式闸门必须放在 `CaptureSelectionAsync()` 最前面**：常规模式一旦先走 `_subregionWindow`/`_clickedWindow`，就会重新调用 `WindowCapture.CaptureAsync` 重建窗口，菜单、悬停提示、下拉框、指针全都截不到；这正是主人报的「能看到但抓不到」。新增取图来源时先判断模式，再碰窗口分支。
- **取色器不要逐像素 `GetPixel`**：19×19 用 `LockBits` 一次拷进复用缓冲；`WriteableBitmap.PixelBuffer.AsStream()` 每次写前 `Position=0`，否则第二次开始写偏。`_loupePixels/_loupeSample` 与 `WriteableBitmap` 都常驻复用，不要跟随鼠标每帧 new。
- **多尺寸 ICO 的基底必须够大**：Pillow 的 `save(format='ICO', sizes=[...])` 只会从当前图缩出不超过基底尺寸的档位；用 16×16 当基底再传 10 个 sizes，最终只得到 16×16。先裁方图并 resize 到 256×256，再生成全部尺寸。
- **鼠标指针只合成一次**：WGC 采集已显式关闭指针捕获，不要再打开，否则 WGC 与 GDI 兜底会出现双指针；`GetIconInfo` 返回的 `hbmMask`/`hbmColor` 必须 DeleteObject，`GetHdc` 必须 `ReleaseHdc`。
- **自绘窗口类必须设置 `hCursor`**：`WNDCLASSW.hCursor` 为 NULL 时，`DefWindowProc` 处理 `WM_SETCURSOR` 会把光标隐藏；贴图窗口曾因此出现“鼠标停在贴图上就消失/像被盖住”。用 `LoadCursorW(0, IDC_ARROW)` 设置箭头；文字 `EDIT` 是独立系统窗口，仍用自带 I 型光标。CsWin32 的 `NativeMethods.txt` 不生成 `LoadCursor`/`LoadCursorW`（会 CS0117），这类小 API 用 `DllImport` 更省事。
- **文字输入框按图片原点定位**：`BeginTextEditor` 收到的是图片局部坐标，必须用 `_imgX/_imgY + 局部坐标` 换算屏幕位置；用 `_winY + y` 会在工具栏位于顶部时整体上移一个工具栏高度。最终标注仍记图片坐标，所以“输入框偏了但最终位置对”正是这个坐标系混用的特征。
- **工具栏布局与扩窗共用一套来源**：`BuildToolbarLayout()` 同时决定 `ComputeToolbarHeight()` 和实际绘制；不要另写一套换行规则，否则扩窗高度与画出来的工具栏会错位。字号步进器放在动作行右侧，不参与行高计算，避免切换文字工具时窗口突然变高。
- **改代码纪律**：字符串替换前先读文件、替换后验证命中；每轮改动后必须 `dotnet build` 0 错误再交主人。
- **交付节奏**：构建后关掉旧实例时必须明确提示主人重新打开；验证靠主人实测 + `data\logs\app.log`。
- **贴图管理/历史窗要临时降下置顶贴图**：`HWND_TOPMOST` 的贴图会盖住普通管理窗；打开前把所有 pin 改到 `HWND_NOTOPMOST`，关闭后恢复 `HWND_TOPMOST`，选中的贴图用 `HWND_TOP` 而不是再次置顶，否则它会盖住管理窗。
- **热键删除必须用 -1 哨兵**：`hotkeyVk/fullHotkeyVk` 的 0 是「旧配置，按 hotkeyKey 回退」，不能拿来表示删除；用户删除时写 -1，解析函数见到 -1 直接返回 0，否则重启会复活默认 F1/F2。删除还要同时清 `EscOverPinHook` 与 `HotkeyService` 两条通道，截图键调用 `Restart(0,0)` 重建线程。
- **指针锚定缩放用屏幕坐标**：`WM_MOUSEWHEEL` 的 lParam 是屏幕坐标，不是窗口客户区；先算归一化锚点再更新尺寸，最后用 anchor 反推 `_winX/_winY`。`UpdateLayeredWindow` 的 `psize` 会调整窗口尺寸，不要再额外 `MoveWindow`。
- **NumberBox Compact 会生成独立弹窗**：WinUI 3 的 `SpinButtonPlacementMode=Compact` 上下箭头在单独的 `PopupWindowSiteBridge` 里，滚动/失焦后可能不消失且无法关闭；设置页一律用 `Inline`。

## 6. 待办

- [x] 主人验收 2026-10-22 下午四项反馈（原话「做的好，你要我测的五个都很符合我的预期」）：常规截图已有选区可拖动移动；操作条为 ✕ / ✓ / 文字识别 / 保存（✕ 取消整次取景，✓ 截图复制，O / Ctrl+S 仍可用）；设置页历史张数在截图、删除、清空后即时刷新；主页按钮、托盘菜单与管理窗统一显示「截图/贴图管理」（现已改名「屏幕贴图管理」）。
- [x] 主人复测 2026-10-22 晚五项（部分通过）：OCR 结果窗标题栏图标、OCR 不再带起设置窗、清空分类只清当前分类、历史窗打开时新截图即时出现均通过；历史底栏 tooltip 与裁切未通过，已转入下一批修复。
- [ ] 主人复测 2026-10-22 深夜三项（部分通过）：底栏不裁切、主页/托盘/管理窗统一「屏幕贴图管理」、管理窗拖动/缩放不刷新均通过；历史分类悬停的「删除」提示仍未消除，已转入下一批（Debug PID 11004）。
- [x] 主人验收 2026-10-22 深夜（二）（原话「不错，测试都通过」）：底栏只有「另存为」一个保存按钮，选一张直接另存、选多张自动批量导出；「量另存为」裁切消失；重复悬停的「删除」popup 转入下一批（Debug PID 42884）。
- [x] 主人验收 2026-10-22 深夜（三）除悬停提示外的全部（原话「其他的做的都达到目标，都做的很好」）：全选第一次全选、按钮变「取消全选」，再点退出多选；选择模式下点列表空白退出；多选复制/标注取最新一张，贴图全部贴出并按 24px 错开，删除全部均通过。
- [x] 主人验收 2026-10-22 深夜（四/五）（原话「很好，今天就结束了」）：历史窗任意位置悬停不再出现「删除」；取景器无 Esc / Enter / Ctrl+S / Ctrl+C 键名提示；历史窗 Delete 删除、Ctrl+A 全选与 Esc 关闭仍生效；标题下「左键选中；右键进入多选，之后左键勾选/取消；点空白处或 Esc 退出。」说明正常。当前批次转入 v1.0.1 发布。
- [ ] v1.0.1 发布：版本 1.0.1、README 按「仅最新功能、不写版本历史/开发过程」重写、构建 0 错误、发布会话已生成 72.7 MB 目录与 29.2 MB zip；待提交/tag/推送与 GitHub Release 资产核验。
- [x] v1.0.0 初始状态：全新配置 Alt+A / Alt+D、「常规截图」、Windows OCR、开机自启动关闭、关闭驻留托盘；全新目录 `--smoke` 与 `data/config.json` 已核对，旧配置不迁移。
- [x] v1.0.0 瘦身包：partial trim + 删除 57 个可选组件 / 37 个 `.winmd` / 82 个语言目录，目录 72.7 MB、zip 30,588,110 字节（29.2 MB），sha256 `ad1e6568f61860f8c40d94e2c307702a9577272b7fdf3331d66466c90235fd50`；Debug 构建 0 错误，Debug 与解压包 `--smoke` 均退出 0。
- [x] GitHub Release：旧 `v0.1.0` Release 已删除；`v1.0.0` 已发布并上传 `IshWY-1.0.0-win-x64.zip`（30,588,110 字节，sha256 一致），https://github.com/GRmaid/PrtScr-IshWY/releases/tag/v1.0.0 。
- [x] 主人验收 2026-10-22 凌晨 2A 贴图批次：贴图管理、锁定/点击穿透、8px 吸附与参考线、左右半屏切换、指针锚定缩放、新贴图初始大小 80–100%、五个热键删除 ✕ 全部通过（主人原话「验收通过，做的非常好」）。构建 0 错误，`--smoke` 通过。
- [x] 2026-10-22 补文档并提交本轮整批（2A + 指针缩放 + pinInitialScale + 热键删除），详见 README 与 ROADMAP。
- [x] 主人用真实独占全屏游戏实测 Ctrl+Shift+F9 —— 2026-10-19 主人确认游戏内可用（合成按键链路同日更早跑通，见 §4）
- [x] 主人实测「贴图 + Esc 关闭」—— 2026-10-19 23:24 连续三次贴图均由 `Pin: esc polled over pin, closing` → `Pin: WM_CLOSE (edit=False)` 关闭，主人确认
- [x] README 补齐：特性、热键、构建/打包、配置、已知限制（独占全屏/多屏 DPI）、隐私声明（纯本地）—— 2026-10-19 重写
- [x] Release 打包（unpackaged 便携版）+ GitHub Actions —— 2026-10-19 完成：`scripts/publish.ps1` 产出 `artifacts/IshWY-0.1.0-win-x64.zip`（92.5 MB，已排除 PDB），`--smoke` 启动验证退出码 0；`ci.yml` 构建 + 发布校验，`release.yml` 在 `v*` tag 上发布 zip
- [x] 产品改名「实践小梅IshWY」（短名 IshWY）—— 2026-10-19：`AssemblyName`=IshWY（发布 exe 为 `IshWY.exe`、PRI 为 `IshWY.pri`），设置/取景/标注/OCR/贴图标题、托盘提示、启动异常标题、通知、README、打包脚本与 release.yml 同步；Debug 与发布包 `--smoke` 均退出 0，窗口标题实测为「实践小梅IshWY 设置」。命名空间/解决方案仍保留内部代号 PrtScr
- [x] 截图 / 全屏 / 贴图 / OCR 四个热键 + 组合键录制 —— 2026-10-20 完成：`HotkeyService` 四个 id、设置页四个录制按钮、四条配置字段与冲突检测；默认截图 F1、全屏 F2、贴图 Ctrl+Alt+V、OCR Ctrl+Alt+S；主人已改为 Alt+A / Alt+D / Alt+S / Alt+Q。Debug 构建 0 错误，应用启动日志四个 `register=0x1`；四热键已由主人验证成功
- [x] 开机自启动 + 设置开关 + 单实例 —— 2026-10-20 完成：HKCU Run 值 `IshWY`（`IshWY.exe --startup`），`--startup` 静默驻留托盘；第二实例唤醒已有设置窗。调试构建 0 错误；UIA 切换开关实测写入/删除 Run 值，隐藏启动日志 `OnLaunched: startup 模式，静默驻留托盘`。默认关闭，当前注册表无该值；待主人在设置里开启并可选重启验证
- [x] 截图历史 —— 2026-10-20 完成：框选/全屏自动写入 `data\history`（每类默认 30 张，可关闭），历史窗支持复制/贴图/删除/清空/打开文件夹；实测全屏保存 5.1 MB PNG 并在历史窗显示，UIA 切换开关写入 `saveHistory`
- [x] 另存为 / 历史标注 / 标注完成保存 —— 2026-10-20 完成：`FileSaveService` + 取景器 `Ctrl+S`/保存按钮 + 历史「标注」「另存为」+ 贴图右键「另存为…」+ 标注工具栏 id 24；历史/贴图另存为字节与源一致，主人实测取景器 Ctrl+S 后剪贴板与历史均正常
- [x] 主人实测 Esc 仲裁：标注中 Esc 只退编辑；OCR+贴图重叠只关最上层、不重叠先关 OCR —— 2026-10-20 20:34 主人三步实测通过
- [x] 内存与发布体积优化：已完成取景器位图释放、贴图按需解码+复用 DIB、历史缩略图释放、发布包裁剪（zip 69.6 MB）。2026-10-20 20:34 主人复测：整进程约 142 MB 基本稳定，滚轮/标注峰值与取景初帧命中均通过；WGC 常驻池（约 48 MB）保留
- [x] 多屏 DPI：按主人决定取消（无第二显示器，不使用虚拟显示器）
- [x] 清理：`clip_probe*.png` 已删除（含桌面/内部对话截图，不入库）；旧 `AnnotationWindow` 待清理。`tools/DxgiProbe`、`tools/WgcProbe`、`tools/KbProbe` 保留，作为采集引擎与键盘钩子排障工具
- [x] 发布体积裁剪：`scripts/publish.ps1` 删除未加载的 AI/ML/Search/Widgets 组件（39 文件 55.3 MB），zip 92.5→69.6 MB；细粒度替换元包因 H.NotifyIcon targets 冲突不可行，保留元包+裁剪
- [x] 主人实测延时截图（设置 3/5/10 秒、按热键后开菜单/悬停、再按取消、托盘入口）与鼠标指针开关（F1 + 全屏）—— 2026-10-20 已通过，无双指针
- [x] 实现「取景器跟随鼠标的放大取色器 + Ctrl+C 复制色值」：19×19 放大、HEX/RGB、十字准星、无选区复制色值（有选区仍复制截图）；构建 0 错误；2026-10-20 主人实测取色器与 Ctrl+C 通过
- [x] 主人复测 2026-10-20 深夜二次修复通过：屏幕绝对边缘与窗口边框起拖、单击选窗、悬停预高亮、`#32770` 与其他悬浮窗悬停、滚轮循环、取色器边缘与 `Ctrl+C` 色值均正常。1px 非客户边框 + 全屏点击穿透层过滤已验证
- [x] 贴图滚轮缩放内存：已去掉 `MoveWindow` 双提交、高清结算 350ms、不透明 `SourceCopy`、缩小后回收 DIB；主人接受当前峰值（约 145 MB）。若后续仍偏高，再评估「只渲染可见区域」的视口化改造
- [x] 主人复测取景器键盘直达 —— 2026-10-20 23:17 全部通过：F1 后不点鼠标连续 Tab、无选区 Ctrl+C 复制色值、Esc 关闭；拖选区/选窗后重复按键正常；Alt+Tab 到其他程序不被取景窗吞键
- [x] 主人实测取景器 Tab 在窗口截图/常规截图间切换 —— 2026-10-20 深夜确认切换与提示正常，窗口模式仍吸附抗遮挡、常规模式能截到右键菜单/悬停提示/指针
- [x] 主人实测取色器跟随与 `Ctrl+C` 色值（右/下边缘不漂移）、标题栏/托盘图标清晰度 —— 已通过
- [x] 主人实测默认截图模式设置 —— 2026-10-20 深夜确认：设置 → 常规切到常规/窗口后，每次按截图热键从对应模式起步，Tab 临时切换与提示正常；`data/config.json` 的 `defaultCaptureMode` 随选择更新
- [x] 主人实测重操作内存：移动贴图、标注、OCR 后峰值约 200 MB，停止约 5 分钟后回落到约 100 MB，属 Windows 工作集裁剪/GC，不是无界泄漏。本轮已加 `SWP_NOREDRAW`、退出标注回收 DIB、`ApplyEdit` 提前释放 `_baseScaled`、OCR 流 using 与结果清空
- [x] 主人验收 2026-10-20 深夜历史/取景小批次（部分）：① 右键取消、③ 历史分类计数与筛选（含「早期未分类」）、④ 单张/批量另存为、⑥ 去「轻量」均通过
- [x] 主人验收 2026-10-21 12:39 主窗口 UI 第一批：2×2 操作卡片与热键角标、历史摘要、分组折叠、紧凑热键表与录制态、首页状态提示全部通过。
- [x] 主人验收 2026-10-21 13:16 取景器 HUD：底部常驻模式/快捷键提示、选区外侧「截图 / 识别 / 保存」操作条、尺寸与进程提示纯数据化、取色器与 toast 避让 HUD 全部通过；顶部模式徽标已移除，Tab 与模式名共用一个蓝框且无灰色键帽。
- [x] 主人验收 2026-10-21 13:46 贴图标注工具栏 + 文字输入 + 光标：分组两排/窄图降级、撤销/清空禁用态、当前工具/颜色/快捷键提示、Enter 完成与 Ctrl+S 另存均通过；文字输入框贴合点击处、字号按钮/滚轮可调、最终位置与字号正确；鼠标指针不再被贴图窗口隐藏。
- [x] 主人验收 2026-10-21 凌晨补丁：① 窗口模式悬停/选中窗口时尺寸提示旁出现「进程 xxx.exe」，常规模式不出现；② 滚轮循环到目标窗口后直接按 `Enter`，无需单击即可截该窗口并复制/写历史（常规模式按同一矩形裁冻结帧）；③ 右键取消仍正常；④ 回车捕获改为不预先 `UpdateVisuals()`，主人反馈整体满意，重操作内存约 165 MB。构建 0 错误、`--smoke` 通过
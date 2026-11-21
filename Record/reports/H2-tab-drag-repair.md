# H2 · 拖拽中按 Tab 仍冻结：运行态证伪与全链路复核（诊断报告）

- 单号：task-44（H2，H1 返修前诊断）
- 仓库：`E:\Explore\GoodGood\PrtScr`，HEAD `5e74f62`（含 H1 提交 `52c5565`，2026-11-07 11:55:33 +0800）
- 日期：2026-11-07
- 写域：`src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs`、`src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs`；本报告 `Record/reports/H2-tab-drag-repair.md`
- 本轮只诊断，未改任何产品代码、未 commit、未跑构建与 GUI

## 0 裁决摘要（三句话）

1. **主人本次「仍冻结」的实测对象不含 H1。** 运行实例 PID 8156 是 10:53:30 启动、加载 10/30 22:46:47 的 `IshWY.dll`；该 DLL 里搜不到 H1 新增的两条字符串，而今天 11:48 产出的 `.tmp/out-merge17/IshWY.dll` 两条都在。
2. **即便重建，只要 PID 8156 活着，新 exe 也起不来。** 单实例用命名事件 `Local\IshWY.Activate`（`src/PrtScr.App/App.xaml.cs:157-179`），非首实例在 `App.xaml.cs:87-92` 直接 `ExitProcess(0)`，与 exe 路径无关。
3. **静态复核 H1 守卫成立**：三条 Tab 入口在 `CaptureOverlayWindow.Mode.cs:28` 汇聚，守卫 `:38-43` 早于所有状态赋值；10/30 旧代码可完整复现「选区冻结 + 指针捕获残留」。残留风险 R1（拖拽中捕获丢失→`_dragging` 永久为真→H1 守卫从此永久吞 Tab）真实存在，但不是本次报告的症状。

## 1 实测对象与二进制证据

### 1.1 进程与文件

| 项 | 值 |
| --- | --- |
| 进程 | `IshWY` PID 8156，StartTime `2026/11/07 10:53:30` |
| 镜像 | `src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe` |
| 该目录主程序集 | `IshWY.dll`，966144 字节，`LastWriteTime = 2026/10/30 22:46:47` |
| 今日构建产物 | `.tmp/out-merge17/IshWY.dll`，967168 字节，`2026/11/07 11:48:02` |

### 1.2 程序集字面量判据（静态，不执行）

H1 新增两条字符串，按 UTF-16LE 在 DLL 字节里检索：

| 字符串（H1 新增） | 默认 bin（运行中） | `.tmp/out-merge17`（今日构建） |
| --- | --- | --- |
| `松开鼠标后再按 Tab 切换模式`（`Mode.cs:41`） | **不存在** | 存在 |
| `Overlay: mode switch ignored (dragging)`（`Mode.cs:40`） | **不存在** | 存在 |

结论：正在运行的镜像早于 H1 八天，**从未包含守卫代码**；今日构建已把 H1 编进去，但没有成为运行镜像。

### 1.3 为什么「重建后也测不到」

- 单实例实现（`src/PrtScr.App/App.xaml.cs:157-179`）用命名事件 `Local\IshWY.Activate` 的 `createdNew` 判定首实例；
- 非首实例 `Set()` 唤醒旧实例后，在 `App.xaml.cs:87-92` 走 `PInvoke.ExitProcess(0)` 退出；
- 该判定只看会话内事件名，**与 exe 所在路径、构建目录无关**；
- 因此 PID 8156 存活期间，启动 `.tmp/out-merge17/IshWY.exe` 或任何新构建，都只会唤醒旧实例后退出，主人看到的仍是 10/30 的行为。

### 1.4 git 对照

```
$ git log -1 --format='%h %ci %s' 52c5565
52c5565 2026-11-07 11:55:33 +0800 feat(history,overlay): 拖拽中 Tab 守卫与历史元数据 version 2
$ git status --porcelain -- src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs
（空）
```

HEAD 两文件与提交一致，工作树无未提交改动；H1 代码确实在 HEAD 里，只是不在运行的 DLL 里。

## 2 三条 Tab 入口与分派优先级（静态链路）

| # | 入口 | 位置 | 触发条件 | 投递方式 |
| --- | --- | --- | --- | --- |
| E1 | 低级键盘钩子 `WH_KEYBOARD_LL` | `src/PrtScr.App/Services/EscOverPinHook.cs:397-454` | `_overlayKeysActive == 1`、无 Ctrl/Shift、无 Alt/Win（`:410-411`、`:434-437`） | `DispatchOverlayKey`（`:453`、`:456-469`）→ `CaptureOverlayWindow.Capture.cs:68-71` `DispatcherQueue.TryEnqueue` → `:74-111`，Tab 分支 `:87-89` |
| E2 | 窗口消息层 `KeyInterceptor` | `src/PrtScr.App/Services/KeyInterceptor.cs:104-119` | 取景窗 HWND 收到 `WM_KEYDOWN` 且 vk∈{Enter,Esc,Tab,O} | 拦截并 `return 0`，回调 `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:135-155`，Tab 分支 `:147-149` |
| E3 | XAML 焦点锚 `FocusSink` | `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:114-133` | FocusSink 持有焦点且事件未被子类处理 | 直接调用，`e.Handled = true`（`:128-131`） |

三个分支最终都调用 `CaptureOverlayWindow.Mode.cs:28` 的 `ToggleCaptureMode()`，**H1 只在 `:32-43` 加了一处守卫**，因此理论覆盖三条入口。

关键次序事实：E1 在钩子线程 `return true` 吞掉按键（`EscOverPinHook.cs:453-469`），`WM_KEYDOWN` 根本不会到窗口，所以 **E2/E3 在正常取景态不可达**；E2/E3 只在钩子被卸载重装的空窗（`EscOverPinHook` 自愈路径）或 `_overlayKeysActive == 2`（失焦只留 Esc）时才有机会。这一冗余设计使「三条入口双触发」在稳态下不成立。

## 3 10/30 旧代码的精确因果链（本次症状的可复现解释）

旧 `ToggleCaptureMode()`（`git show 52c5565^:...Mode.cs`）无任何守卫，开头即：

```csharp
var hadSelection = _hasSelection;
var previousSelection = _selection;
var previousMode = _captureMode;
_captureMode = _captureMode == CaptureMode.Window ? CaptureMode.Normal : CaptureMode.Window;
_dragging = false;              // ← 冻结起点
_dragMoved = false;
_movingSelection = false;
_moveWholeWindow = false;
_resizingSelection = false;
_resizeEdge = ResizeEdge.None;
_wheelAccum = 0;
_lastTipWindow = null;
HideColorLoupe();
```

逐步链路：

1. 按下左键 → `Root.PointerPressed`（`CaptureOverlayWindow.xaml:11-14` 挂的是 Root，事件从 Layer 冒泡上来）→ `HandlePressed`（`Selection.cs:41`）→ `Layer.CapturePointer`（`Selection.cs:61`）；
2. 进入三种拖拽之一并置 `_dragging = true`：移动选区 `Selection.cs:88-99`（`:94`）、新框选 `:108`、缩放 `BeginResize` `:391`；
3. 拖拽中按 Tab → E1 吞键 → `DispatcherQueue` 切 UI 线程（`Capture.cs:68-71`）→ `HandleOverlayHotkey` 的 `:76` 只判 `_closed`，`:87-89` 调 `ToggleCaptureMode()`；
4. 旧函数无守卫 → 切换模式并清空 `_dragging/_dragMoved/_movingSelection/_resizingSelection/_resizeEdge`（**指针捕获此时并未释放**）；
5. 鼠标继续移动 → `HandleMoved` 的 `if (_dragging)`（`Selection.cs:144`）为假，落到 `:243` 的 `UpdateHover(point)`；选区不再更新，HUD 从「松开 完成选区」切到悬停文案（`CaptureOverlayWindow.Hud.cs:314`、`:331-347`）——即主人看到的「选区冻住」；
6. 松手 → 旧 `HandleReleased`（`git show 52c5565^:...Selection.cs`）首行是 `if (!_dragging) return;`，释放捕获写在早退之后：

```csharp
private void HandleReleased(PointerRoutedEventArgs e)
{
    if (!_dragging)
        return;                  // ← 状态已被 Tab 清零，直接返回
    Layer.ReleasePointerCapture(e.Pointer);
    _dragging = false;
```

7. 结果：Layer 一直抓着指针，窗口在鼠标离开覆盖区后仍收到移动（走悬停），直到下一次按下才重新捕获/释放。表现即「冻结」且带一点「鼠标不跟手」。

这条链条与 H1 报告 §1.1 一致，且解释了「按 Tab 后还能看到模式芯片变了、但选区不动」的组合症状。

## 4 H1 守卫为什么成立（新代码复核）

- 唯一汇聚点：E1/E2/E3 都调 `ToggleCaptureMode()`（`Capture.cs:88`、`Capture.cs:130`、`xaml.cs:148`），守卫加在 `Mode.cs:38-43` 即全覆盖；
- 守卫位置早于一切写入：`:38` < 首个状态读取 `:45`、模式赋值 `:48`、`_dragging = false` `:50`；返回前只做日志与提示，不触碰拖拽状态；
- `_closed` 前置判断在 `:32-33`，窗口已关闭时不做清理（H1 一并修掉了旧代码对已关闭窗口写状态的隐患）；
- 三种拖拽在进入 Tab 分派前都已置位：`:94`、`:108`、`:391`，因此正常拖拽期间 `_dragging` 恒为真；
- 与释放的两种时序都安全：Tab 先到 → 守卫忽略、松手走正常收尾；松手先到 → `_dragging` 已清，Tab 正常切换（此时不冻结）；
- `HandleReleased:262` 的幂等释放使「历史上任何路径清了 `_dragging`」都不再漏释放捕获，消除了旧链第 6 步的残留。

因此：**没有静态证据表明 H1 会失败；也没有任何运行证据表明 H1 被执行过。**

## 5 用户点名的三条链路：XAML 焦点导航 / 捕获丢失 / Dispatcher

### 5.1 XAML 原生焦点导航

- Root 只挂了 `PointerMoved/Pressed/Released/WheelChanged`（`CaptureOverlayWindow.xaml:7-14`），键盘加速器只有 Escape/Enter/Ctrl+S/Ctrl+C（`:246-251`），**没有 Tab 加速器**；
- 全窗唯一 tab stop 是 1×1、`Opacity=0` 的 `FocusSink`（`xaml:238-244`，`IsTabStop=True`）；
- 焦点在 FocusSink 时按 Tab，`FocusSink_KeyDown`（`Capture.cs:114-133`）先置 `e.Handled = true`（`:129`），XAML 的默认 Tab 焦点导航不会执行——这是 10/20 起「焦点锚」设计的本意；
- 焦点锚可能失败：`xaml.cs:192` 丢弃 `FocusSink.Focus(...)` 的返回值，`xaml.cs:175-176` 的注释自承首帧 Focus 可能被静默丢弃。此时第一次 Tab 只会被焦点管理器用于把焦点移到唯一 tab stop，**不切模式**（若 E1/E2 都未命中）；第二次 Tab 才命中 E3。表现为「偶尔第一下 Tab 没反应」，不是冻结；
- 结论：XAML 焦点导航在稳态被 E1 吞键 + E3 `Handled` 双重短路，不构成本次症状；其独立缺陷是「首按可能被吃掉」与下面 R3 的修饰键不一致。

### 5.2 指针捕获丢失（`PointerCaptureLost`）

- 全仓 grep `PointerCaptureLost|CaptureLost`：**0 个处理器**（`ToastPlacementWindow`/`PinCropWindow` 只有各自的 Pressed/Released 配对，也没有丢失处理）；
- `Layer.CapturePointer(e.Pointer)` 的返回值被丢弃（`Selection.cs:61`），捕获失败无感知；
- 若拖拽中捕获丢失：`_dragging` 仍为真，鼠标在窗内时 `PointerMoved` 仍会到（命中测试不依赖捕获），移出覆盖区即停更；在窗外松手不会产生 `PointerReleased`，**`_dragging` 永久为真**；
- 此时 H1 守卫会把之后所有 Tab 都吞掉并只提示「松开鼠标后再按 Tab」，直到下一次按下/松开循环——这是 H1 引入的新失败模式（R1）。

### 5.3 DispatcherQueue 投递

- 钩子线程只做 `TryEnqueue`（`Capture.cs:68-71`），不碰 UI 状态，符合线程约束；
- UI 线程回调里 `HandleOverlayHotkey:76` 只查 `_closed`，未查 `_dragging`——但守卫在 `ToggleCaptureMode` 内，语义等价；
- 投递延迟不改变结论：`_dragging` 在松手前恒为真；若 Tab 排在松手之后执行，则切换发生在拖拽结束之后，属正常行为；
- 未发现重入/双投递：E1 吞键后 E2/E3 收不到同一按键。

## 6 残余风险（按严重度）

| 编号 | 风险 | 证据 | 严重度 |
| --- | --- | --- | --- |
| R1 | 捕获丢失后 `_dragging` 永久为真；H1 守卫使其后的 Tab 全部无效，只能靠再按一次鼠标恢复 | `Selection.cs:61` 忽略返回值；全仓 0 个 `PointerCaptureLost`；`Mode.cs:38-43` | 中 |
| R2 | Tab 长按自动重复：拖拽中每次重复都提示；松开后仍按住的重复会连续切模式 | `Capture.cs:87-89` 无去抖；`Mode.cs:38` 仅按状态忽略 | 低 |
| R3 | 修饰键语义不一致：钩子明确放行 Ctrl/Shift+Tab（`EscOverPinHook.cs:435-436`），但 E2（`KeyInterceptor.cs:104-119`）与 E3（`Capture.cs:128-131`）不判修饰键，仍会切模式 | 三处代码 | 低 |
| R4 | E2 的 HWND 覆盖面未静态证实：WinUI 3 输入可能落在子窗口/InputSite，`KeyInterceptor` 只子类化顶层 HWND | `KeyInterceptor.cs:104-121`；需实机日志验证 | 低 |

R1 是唯一值得在 H1 之后补的硬伤，但它与 Tab 无因果关系，属「拖拽鲁棒性」；在未做任何实机验收前叠改会污染归因。

## 7 最小修法（待 lead 裁决，本轮不改产品）

### 方案 0（推荐，零代码）

1. 主人关闭 PID 8156（不要 `taskkill /T`，按项目惯例正常退出）；
2. 用 `.tmp/build-guard.ps1` 串行重建默认 `bin`（H1 已在 HEAD，无需改码）；
3. 重开并手测第 8 节五项；
4. 若仍冻结，再按本文第 3/5 节定位（下一步优先抓 `App.Log` 是否出现 `Overlay: mode switch ignored (dragging)`，该行能把「守卫是否执行」与「执行后仍冻结」分开）。

### 方案 A（可选，R1 自愈；需扩写域 1 行）

- `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs` 构造函数加 `Layer.PointerCaptureLost += Layer_PointerCaptureLost;`；
- `Selection.cs` 新增处理器：仅当 `_dragging && !_releasingPointer` 时清拖拽状态并 `UpdateVisuals()`；
- `HandleReleased` 在 `Layer.ReleasePointerCapture` 前后置/清 `_releasingPointer`，避开「主动释放触发 Lost → 提前清状态 → 收尾逻辑被 `if (!_dragging) return` 跳过」的顺序陷阱；
- 约 15 行；需扩写域到 `xaml.cs`（当前写域外），风险中。

### 方案 B（仅 `Mode.cs`，不推荐单独用）

- 守卫内先探活：`Layer.PointerCaptures` 为空且 `_dragging` 为真时视为陈旧状态，清理后继续切换；
- 风险：若按下时捕获本就失败（`:61` 返回值被丢弃），会把「真实拖拽」误判为陈旧而重新引入冻结；`PointerCaptures` 在 WinUI 3 的可用性也需先核实（静态未证）。

### 方案 C（去重与修饰键统一，仅在实机出现抖动时做）

- 三入口收敛到一个 `RequestToggleCaptureMode()`，带 250–300ms 去抖并统一忽略 Ctrl/Shift；
- 需改 `Capture.cs`、`xaml.cs`（写域外）。

**建议：先执行方案 0；方案 A 作为独立小单排在实机验收之后，不并入 H1 返修。**

## 8 实机验收清单（主人本人按键，不用合成键/UIA）

| # | 操作 | 期望 |
| --- | --- | --- |
| 1 | F1 亮窗，按住左键拖框，不松手按 Tab | 选区继续跟随鼠标；提示「松开鼠标后再按 Tab 切换模式」；模式芯片不变 |
| 2 | 松开左键后再按 Tab | 正常切模式，选区保留（窗口→常规/常规→窗口各自验证） |
| 3 | 已有选区内按住拖动（移动）时按 Tab | 同 1，选区继续跟随，松手完成移动 |
| 4 | 拖 8 个边/角缩放时按 Tab | 同 1，缩放继续，松手完成缩放 |
| 5 | 拖拽中按 Tab 后把鼠标拖到屏幕另一角/另一显示器再松手 | 选区仍跟随；松手后 Tab 仍可切模式（验证无捕获残留） |
| 6 | 连按/长按 Tab（非拖拽） | 模式按预期切换，不应出现提示「松开鼠标后…」 |

日志旁证：`App.Log` 中每次被忽略的 Tab 应恰好出现一行 `Overlay: mode switch ignored (dragging)`；出现该行而选区仍冻结，才说明 H1 守卫之外还有第二条冻结路径。

## 9 边界与未做

- 未改任何产品代码；未 commit/push；未跑 `dotnet build`/`dotnet test`/`dotnet format`（本轮为只读诊断，改码后再按 `.tmp/build-guard.ps1` 门禁）；
- 未启动 GUI、未合成按键、未用 UIA、未杀任何进程；
- 未生成 HTML；未召唤子代理；
- 团队工具在本会话不可用（`agent_teams_status`：本会话是 fork，不属于任何活动团队），**未能 claim task-44**，以用户消息中的任务单为准；
- 扩读文件及理由：`Services/EscOverPinHook.cs`（E1 钩子与吞键条件）、`Services/KeyInterceptor.cs`（E2 消息层拦截）、`Views/CaptureOverlayWindow.Hud.cs`（提示与 HUD 状态，排除提示本身导致冻结）、`Views/CaptureOverlayWindow.xaml`（焦点锚与事件挂载）、`App.xaml.cs`（单实例，解释新构建为何测不到）——均为回答「完整链路」所必需，未做全仓盲扫。


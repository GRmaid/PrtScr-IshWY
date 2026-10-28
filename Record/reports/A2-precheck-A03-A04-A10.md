# A-2 前置确认：A03 / A04 / A10 在 HEAD 的成立性与最小改法

- 单号：task-7（A-2 前置确认）
- 执行：maid-check
- 基线：HEAD 0b1ca41（v1.3.0-2-g0b1ca41），源码树干净
- 方式：只读静态核查 + 公开文档核验；未运行 GUI、未杀 IshWY.exe、未改产品代码、未跑构建/测试
- 结论日期：2026-10-28

## 裁决速览

| 项 | 裁决 | 一句话结论 | 最小改法落点 | 与 A-1 并行 |
|---|---|---|---|---|
| A03 | **仍成立** | PrintWindow 无受控期限；挂住时取景窗已 `_closed`，取消被早退，`_isCapturing` 永久为真 | `src/PrtScr.Capture/WindowCapture.cs` 的 `CaptureAsync`（必要时加晚到结果处置） | **可并行**（只改 WindowCapture.cs 时不冲突；若同时改 `CaptureOverlayWindow.Capture.cs` 则与 P-04/P-05 冲突） |
| A04 | **仍成立（版本门槛确证）** | `TryCreateFromWindowId` 最低 10.0.20348.0，声明却是 19041；Win10 客户端全部低于门槛，且异常被静默吞掉 | `src/PrtScr.Capture/WgcWindowCapture.cs`：改走已有 `IGraphicsCaptureItemInterop.CreateForWindow` | **可并行**（不碰 A-1 文件；若选择抬最低版本则需改 Directory.Build.props，超出 P-08 写域） |
| A10 | **仍成立（分可达性）** | 至少 1 处常态可达的全屏 Bitmap 泄漏 + 数处异常/取消路径泄漏；不是「每次截图都泄漏」 | P-09 需拆两半：`PrtScr.Capture/*` 可先做；`Services/CaptureService.cs` 等 P-04 完成 | **部分冲突**：PrtScr.Capture 子集可并行；CaptureService 部分与 P-04 同文件，须串行 |

---

## 一、做了什么

1. 读外部评审原文 §7 A03、§8 A04、A10 表格行，以及交接文档对应行，确认待核 claim 的原文措辞。
2. 沿调用链只读核查：`WindowCapture.CaptureAsync` → `CaptureOverlayWindow.ConfirmAsync/CaptureSelectionAsync` → `CaptureService.StartCaptureAsync/CaptureMonitorBestAsync`；WGC 入口 `WgcWindowCapture.CaptureWindowAsync` 及其 interop 定义；A10 涉及的 `WgcWindowCapture` 池/帧/复制路径、`ScreenCapturer`、`DesktopDuplicationCapture`。
3. 核对平台声明：`Directory.Build.props` 的 TFM/最低版本、`PrtScr.App.csproj` 的打包方式、是否存在 `.appxmanifest`。
4. 用微软官方文档核对 `GraphicsCaptureItem.TryCreateFromWindowId` 的引入版本，并检查 screen-capture 文档是否提到能力/授权要求。
5. 核对本机 OS build，说明本机可复现/不可复现的边界。
6. 用 `git log -S` 确认 HEAD 是否存在评审所说的 capture busy 标志，避免沿用旧版本措辞。
7. 产出本报告；未改任何产品代码与测试，未执行构建（本单为只读前置确认，且 App bin 被运行中 PID 29836 占用）。

## 二、证据

### 2.1 基线

```
git rev-parse HEAD                          → 0b1ca413f895b1e73beaafd299e5d3812afc4467
git describe --tags --always                → v1.3.0-2-g0b1ca41
git status --porcelain -- src PrtScr.sln Directory.Build.props → （空）
```

- 源码树干净，以下行号均按 HEAD 0b1ca41。
- 本机 OS：Windows 11 家庭中文版 24H2，build **26100**（`[Environment]::OSVersion.Version.Build`）。高于 20348，**本机无法复现 A04 的门槛失败**。
- `git log --all --oneline -S"_captureBusy" -- src` 输出为空 → HEAD 及历史从未有该标志；评审所说「busy」在 HEAD 的实现名是 `CaptureService._isCapturing`。
- `glob **/*.appxmanifest` → 无结果；`src/PrtScr.App/PrtScr.App.csproj:15` 为 `<WindowsPackageType>None</WindowsPackageType>`（未打包应用，无包清单）。

### 2.2 A03：PrintWindow 挂住后状态无法释放

**裁决：仍成立。** 评审的核心机制在 HEAD 精确成立，只有「busy」的名字需要更正；且「可能一直保留」应升级为「按当前代码路径必然一直保留」。

证据链：

1. `src/PrtScr.Capture/WindowCapture.cs:24-31`
   - `:27` `var printed = await Task.Run(() => CaptureWindow(window), cancellationToken);`
   - PrintWindow 是同步 P/Invoke（`:50` `PInvoke.PrintWindow((HWND)window.Handle, hdc, PW_RENDERFULLCONTENT)`，`PW_RENDERFULLCONTENT` 定义在 `:19`），没有超时、没有 `WaitAsync`；`Task.Run` 只换线程，不会让原生调用可取消。
   - `CaptureWindow` 自身的 bitmap 在 `:41` 创建，`:67-71` 的 finally 按 `keep` 释放；**函数本身失败不泄漏**，问题在调用方会永久等待。
2. 取景窗在等待捕获前已把「关闭」置真：`src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:116-135` `HideForCapture`
   - `:120` `_closed = true;`
   - `:121` `StopEscPolling();`
   - `:122` `EscOverPinHook.SetOverlayKeysActive(false);`
   - `:123` `AppWindow.Hide();`
   - 也就是说，进入捕获等待后取景窗自己的 Esc/取消通道已被停用。
3. 确认流程在 await 返回后才收尾：`CaptureOverlayWindow.Capture.cs:334-383` `ConfirmAsync`
   - `:341` `HideForCapture();`
   - `:342` `captured = await CaptureSelectionAsync();`
   - `:346` `FinishCaptureSession();`
   - `:377-382` finally 只在 await 返回后执行；`FinishCaptureSession`（`:138-173`）在 `:172` 触发 `Dismissed?.Invoke()`，这才是 `_isCapturing` 被清零的唯一入口（`CaptureService.cs:43-49`）。
4. 取消路径对已关闭窗口是空操作：
   - `:109` `public void Cancel() => Dismiss();`
   - `:176-182` `Dismiss()` 开头 `if (_closed) return;` → 直接返回，既不 `HideForCapture` 也不 `FinishCaptureSession`。
5. 重复按 F1 的路径：
   - `src/PrtScr.App/Services/CaptureService.cs:60-68`：若 `_isCapturing` 为真 → `_overlay?.Cancel(); return;` → 命中第 4 条的空操作。
   - 结果：`_isCapturing` 保持 true（`:29` `IsCapturing => _isCapturing`），后续 F1 永远走「already capturing, cancel」，不会重新取景，也到不了任何 WGC 回退逻辑。
6. 调用点都没有取消令牌：
   - `CaptureOverlayWindow.Capture.cs:232` `var fullWindow = await WindowCapture.CaptureAsync(sub);`（窗口局部；失败后 `:240` 回退屏幕裁剪）
   - `CaptureOverlayWindow.Capture.cs:247` `var windowBitmap = await WindowCapture.CaptureAsync(win);`（窗口模式；失败后 `:252` `CropFullScreenSelection()`）
   - `CaptureService.cs:201` `var bitmap = await WindowCapture.CaptureAsync(info);`（全屏兜底链末段；外层 `:180-212` 有 try/catch，但 catch 也要等 await 返回）
7. 附加并发缺口：`CaptureOverlayWindow.Capture.cs:392` `Capture_Click => _ = ConfirmAsync();` 没有 `_confirming` 守卫；`ConfirmCurrentTargetAsync` 在 `:303-332` 有守卫，但按钮路径没有。挂住期间再点按钮可启动第二个 `ConfirmAsync`，两次 `HideForCapture` 由 `_closed` 挡住第二次，但两次 async 流程会并行等待、重复收尾。

**最小改法（P-07）**

- 位置：`WindowCapture.CaptureAsync`（`WindowCapture.cs:24-31`）内部，不改签名、不改上层。
- 做法：把 PrintWindow 放到**专用线程**（`Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)` 或独立 `Thread`），调用方用 `task.WaitAsync(TimeSpan.FromMilliseconds(...), cancellationToken)` 设受控期限；超时/取消时记录一次日志并返回 `null`，让上层按既有逻辑回退 WGC/屏幕裁剪。
- 晚到结果处置：对超时后仍会完成的 task 挂 `ContinueWith`，若其结果为非 null Bitmap 则 `Dispose`，避免「调用方脱身、后台结果张张泄漏」。
- 单飞约束：超时后底层 PrintWindow 线程仍可能阻塞且不可中止，必须限制同一时间只有一个在途 PrintWindow（例如静态 `Interlocked` 门闩或每窗口一个门闩），否则反复触发会累积阻塞线程。
- 不改 `CaptureOverlayWindow.Capture.cs` 也能满足本单验收「挂住时 busy 最终释放」：`CaptureAsync` 在 deadline 返回 null → `CaptureSelectionAsync` 返回 → `ConfirmAsync` 的 `:346` 执行 → `FinishCaptureSession` → `Dismissed` 清 `_isCapturing`。**若同时想做到「挂住时按 F1 立即取消」，那属于 P-05 的会话身份/取消语义，不应塞进 P-07**，否则与 P-04/P-05 在同一文件上互相踩。
- 风险：
  - `WaitAsync` 只能让调用方脱身，**不能终止底层 PrintWindow**；阻塞线程会残留到系统自行返回为止。
  - 超时后回退 WGC 若也失败，上层会走屏幕裁剪，而窗口来源标注可能仍显示「窗口捕获成功」——这正是 A02/P-04 的范围。P-07 落地时必须与 P-04 的「实际来源/降级原因」协同，不能在 P-07 里再造一个静默降级。
  - 超时阈值建议 1500–2500ms（与 WGC 路径 2s 量级对齐），需 lead 拍板。

**与 A-1 并行性**

- 只改 `WindowCapture.cs`：A-1 的 P-01/P-02/P-03 不碰该文件；P-04 碰 `CaptureOverlayWindow.Capture.cs` 与 `CaptureService.cs`，**不冲突**。可并行。
- 若实现者顺手改 `CaptureOverlayWindow.Capture.cs`（例如让 Cancel 在 `_closed` 时也能收尾），则与 P-04/P-05 冲突，必须等它们完成。建议 P-07 严守 `WindowCapture.cs` 单文件。

### 2.3 A04：WGC 窗口入口与宣称的平台范围不一致

**裁决：仍成立——版本门槛已由官方文档确证；「Programmatic 授权/包能力」子 claim 本次未能从公开文档证实，需实测。**

证据：

1. 声明范围：
   - `Directory.Build.props:4` `<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>`
   - `:5` `<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>`
   - `:6` `<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>`
   - `src/PrtScr.App/PrtScr.App.csproj` 内无 TFM/最低版本声明（`:15` 只是 `WindowsPackageType=None`，`:18` 是 Version 1.3.0），所以 19041 的承诺由 Directory.Build.props 承载。
2. 代码入口：
   - `src/PrtScr.Capture/WgcWindowCapture.cs:122-144` `CaptureWindowAsync`
   - `:129` `var windowId = new global::Windows.UI.WindowId { Value = (ulong)window.Handle };`
   - `:133` `var item = GraphicsCaptureItem.TryCreateFromWindowId(windowId);`
   - `:134-135` item 为 null → return null
   - `:140-143` `catch { return null; }`（注释「WGC 任何失败都回退到 PrintWindow 路径」）——**无版本探测、无 API 存在性检查、无日志**。
3. 官方文档（2026-10-28 拉取，HTTP 200）：
   - https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureitem.trycreatefromwindowid
   - Requirements 表：`Device family | Windows 10, version 2104 (introduced in 10.0.20348.0)`
   - 20348 是 Windows Server 2022 / Iron 的 build；Windows 10 客户端（19041–19046）全部低于它，Windows 11（22000+）才满足。评审「19045 同样低于门槛」成立；「不能推断所有 Win11 都失败」也成立。
4. 文档中的能力子 claim：同一页与 https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture 的正文均未检出 `capabilit` 字样；本应用又是未打包（无 Package.appxmanifest），包能力声明本身不适用。因此：
   - 版本门槛：**已确证**。
   - Programmatic 授权/包能力：**未证实**；若真实存在，只会进一步说明 19041 声明不成立。需要 Win10 实机日志或评审原始证据补全。
   - 具体失败形态（激活工厂缺失抛 `COMException`，还是投影层抛 `TypeLoadException`）也需 Win10 实测；无论哪种，都会被 `:140-143` 吞掉，表现为静默回退 PrintWindow。
5. 可复用的 interop 已存在：
   - `WgcWindowCapture.cs:31-41` `IGraphicsCaptureItemInterop`；`:38` `void CreateForWindow(nint window, in Guid iid, out IntPtr item);`；`:36-37` 注释记录了参数顺序实测（句柄在前、IID 在后，写反会 E_INVALIDARG）。
   - `:409-442` `CreateItemForMonitor` 已给出完整工厂模式：`ActivationFactory.Get` → `Marshal.GetObjectForIUnknown` → `CreateForMonitor` → `GraphicsCaptureItem.FromAbi` → `Marshal.Release`。窗口路径照抄一份即可。

**最小改法（P-08）**

- 推荐 a：新增 `CreateItemForWindow(nint hwnd)`，仿 `:409-442`：
  - `IGraphicsCaptureItemInterop` 走 `WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem))`（或现有写法）；
  - 调 `CreateForWindow(hwnd, IID_IGraphicsCaptureItem, out var abi)`；
  - `GraphicsCaptureItem.FromAbi(abi)` 后 `Marshal.Release(abi)`；
  - `CaptureWindowAsync` 改用它，保留 null/异常回退，并**加一次限频日志**记录失败类型/HResult，便于 Win10 现场诊断。
  - Win10 1803+ 即可用，保留 19041 声明，只改 `WgcWindowCapture.cs`，符合 P-08 写域。
- 备选 b：坚持 `TryCreateFromWindowId`，把 `Directory.Build.props:5-6` 抬到 `10.0.20348.0`，并在 README/发布说明声明放弃 Win10。代价：**Directory.Build.props 不在 P-08 写域**（P-08 只写 WgcWindowCapture.cs 与 PrtScr.App.csproj），且需要主人拍板放弃 Win10；不推荐。
- 备选 c：按 OS build 分支（≥20348 用新 API，否则 interop）。无必要；统一 interop 更简单，也避免两套路径行为分叉。
- 风险：
  - interop 的 vtable 签名必须保持 `(HWND, REFIID, void**)`；本仓库已有实测注释，照现有 `CreateForMonitor` 的调用约定即可。
  - 对 cloaked/最小化/受保护内容窗口，`CreateForWindow` 仍可能失败；上层已有 PrintWindow → 屏幕裁剪回退，不新增崩溃面。
  - `IsBorderRequired` 已由 `:206-212`、`:471-477` 的 try/catch 保护，与本项无关。
  - 本机 26100 只能验证「interop 路径在 Win11 仍正常」，不能验证 19045；后者需要发布包到 Win10 实机（主人侧）。
- 与 A-1 并行性：**可并行**，不碰 A-1 的 `CaptureOverlayWindow.Capture.cs` / `CaptureService.cs`；P-04 不涉及 WGC。

### 2.4 A10：失败、超时与并发路径的资源清理遗漏

**裁决：仍成立，但需按可达性分级。** 评审列举的「候选 Bitmap、额外帧、部分初始化的会话/帧池、晚到结果」在 HEAD 都能对上；其中 #1 是常态可达，其余多为异常/取消分支。不能概括为「每次截图都永久泄漏」。

| # | 位置 | 泄漏对象 / 问题 | 触发条件 | 可达性 | 严重度 |
|---|---|---|---|---|---|
| 1 | `src/PrtScr.App/Services/CaptureService.cs:157,173-175` | `fallback`（全屏 Bitmap）未 Dispose 就 return | 全屏兜底链走到 BitBlt 后，前景窗口 WGC/PrintWindow 成功返回非 null | **常态可达**（全屏游戏路径） | 高（每次一张全屏图） |
| 2 | `src/PrtScr.Capture/WgcWindowCapture.cs:467,469,479-483,489,492-496` | `pool`、额外帧、晚到帧 | `CreateCaptureSession` 抛异常（pool 未释放）；多帧到达（`TrySetResult` 返回 false 的帧未 Dispose）；`WaitAsync(2s)` 超时/取消后晚到帧无人 Dispose | 异常/超时/取消分支 | 中 |
| 3 | `src/PrtScr.Capture/WgcWindowCapture.cs:188-204,208,229,237-241` | `item` / `pool` / `session` | `GetOrCreateDevice` 返回 null（item 泄漏）；`IsBorderRequired` 赋值、`StartCapture` 抛异常（pool/session/item 全泄漏） | 异常分支 | 中 |
| 4 | `src/PrtScr.Capture/WgcWindowCapture.cs:336-372` | `fallback`（Direct3D11CaptureFrame） | `Task.Delay(5, token)` 取消抛异常；`IsOverlayResidue` 等路径抛异常 | 当前调用方都传 default（`CaptureService.cs:124,257`），**暂不可达**；P-05 引入取消后变可达 | 潜在/中 |
| 5 | `src/PrtScr.Capture/WgcWindowCapture.cs:539-542,548-551` | `bitmap`（GDI+）、Map 失败未校验 | `LockBits` 抛异常；`Map` 返回失败但 pData 非空 | 异常分支 | 低-中 |
| 6 | `src/PrtScr.Capture/ScreenCapturer.cs:35,40-42` | `bitmap` | `GetDC(null)` 返回 null → throw | 极罕见（桌面 DC 耗尽） | 低 |
| 7 | `src/PrtScr.Capture/DesktopDuplicationCapture.cs:136-153` | 未匹配的 `IDXGIOutput` RCW | 多显示器枚举时，不匹配的 output 直接丢弃，仅 matched 在 `:161` 保存并由 `:251-258` 的 finally 释放 | 多显示器常态，但靠 GC 兜底 | 低 |
| 8 | `src/PrtScr.App/Services/CaptureService.cs:83,94,104-105,108-113` | `frozen`（冻结全屏 Bitmap） | 取到 frozen 后，窗口枚举 / `_overlay.Prepare` / `Hotkey.BeginCaptureMode` 抛异常，catch 只清 `_isCapturing` 并 rethrow | 异常分支（低概率） | 中 |
| 9 | `src/PrtScr.Capture/WgcWindowCapture.cs:391-401` | `MonitorPool.Item` | `RebuildMonitorPool` 释放 Latest/Session/Pool，未显式 Dispose Item | 重建池时 | 低（RCW 可 GC） |

补充说明：

- `CaptureMonitorBestAsync` 的另两条路径是**正确**的：`:158-164` 黑屏时先 Dispose 再重建；`:131-141` WGC 黑屏先 Dispose。`CaptureFullScreenPngAsync`（`:236-267`）的 bitmap 在 `:249-260` try/finally 释放，也正确。
- `CaptureService.StartCaptureAsync` 的 catch 除了泄漏 frozen，还有状态不一致：`:103` 已置 `_isCapturing=true`、`:104` 可能已 Prepare 出 overlay，`:105` 抛异常后 `:110` 又置 false，但 overlay 仍可见、frozen 仍挂在 `Frozen.Source`。下一次 F1 会在同一 overlay 上再次 Prepare。修复 frozen 释放时应一并考虑这个状态回滚。
- #2 的 `OnFrameArrived` 当前写法：`firstFrame.TrySetResult(sender.TryGetNextFrame())`——`TryGetNextFrame()` 先执行，`TrySetResult` 返回 false 时该帧直接丢失，没有 Dispose。
- #2 的 `WaitAsync` 超时后，TCS 仍可能在未来完成并持有 frame；`finally` 只移除事件并 Dispose pool，不会碰 TCS 的结果。
- #4 的 `WaitFrameAfterAsync` 正常路径在 `:358/:362/:370` 都 Dispose 了 fallback；只有异常/取消路径缺 finally。
- #7 的 `EnumOutputs` 每次返回的 output 都是 RCW；未匹配的 output 既未保存也未 Release，多显示器场景每次全屏捕获都会新增一批待 GC 的 RCW（不是 GDI 泄漏，但会推高 COM 引用计数与 GC 压力）。

**最小改法（P-09，逐条）**

1. `CaptureMonitorBestAsync`：`fallback` 的整个后续段用 try/finally；若 `foregroundResult` 非 null，则 finally 里 `fallback.Dispose()`；异常路径同样释放。注意区分「返回 fallback」与「返回 foregroundResult」两种 owner。
2. `CaptureCoreAsync`：`using var pool = ...`（保证 `CreateCaptureSession` 抛异常时 pool 释放）；`OnFrameArrived` 改为
   ```csharp
   var f = sender.TryGetNextFrame();
   if (f is not null && !firstFrame.TrySetResult(f)) f.Dispose();
   ```
   `WaitAsync` 外层 try/catch：超时/取消时对 `firstFrame.Task` 挂 `ContinueWith(t => t.Result?.Dispose())` 再 rethrow，避免晚到帧泄漏。
3. `EnsureMonitorPool`：局部 `pool/session/item` 全部放进 try/catch，catch 中按 `session?.Dispose(); pool?.Dispose(); item 可空则 Dispose` 逆序释放；`GetOrCreateDevice` 为 null 时也释放 item。
4. `WaitFrameAfterAsync`：用 try/finally 表达「fallback 从进入起归本函数所有，除非作为返回值交出，否则一律 Dispose」；`GetLatestMonitorFrameAsync` 的 `:283` 取得 latest 后即转移所有权，取消/异常由 callee 兜底。
5. `CopyFrameToBitmap`：bitmap 创建后套 try/catch，失败 Dispose 再 throw；`deviceContext->Map` 的 HRESULT 至少判断失败并走清理路径。
6. `ScreenCapturer`：先 `GetDC`，成功后再建 bitmap；或失败分支里先 `bitmap.Dispose()` 再 throw。
7. `DesktopDuplicationCapture`：循环内不匹配的 output 立即 `Marshal.ReleaseComObject(output)`；`EnumOutputs` 返回错误时，已枚举出的 output 也要处理。
8. `StartCaptureAsync`：`frozen` 声明提到 try 外，catch 里 `frozen?.Dispose()`；同时考虑恢复 overlay 隐藏/状态。
9. `RebuildMonitorPool`：`state?.Item` 有 `Dispose` 就调用（RCW 低危，可顺手做，不阻塞验收）。

**与 A-1 并行性**

- `PrtScr.Capture/*`（#2/#3/#4/#5/#6/#7/#9）：A-1 不碰，**可立即并行**。
- `Services/CaptureService.cs`（#1/#8）：与 P-04 同文件（P-04 写域含 `Services/CaptureService.cs`），必须等 P-04 完成或把 P-09 拆成 P-09a（PrtScr.Capture）与 P-09b（CaptureService）。
- 建议：P-09 拆单，或在整改总纲的排期里把 P-09 标成「PrtScr.Capture 子集先做，CaptureService 子集等 P-04 合并后」。

### 2.5 与 A-1 写域冲突矩阵（按整改总纲 P-01..P-06）

| 本单修改文件 | A-1 中触碰它的单 | 是否冲突 | 建议 |
|---|---|---|---|
| `src/PrtScr.Capture/WindowCapture.cs`（A03） | 无 | 否 | 可并行 |
| `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs`（A03 若扩展取消语义） | P-04（A02）、P-05（A05，A-2 后续） | 是 | P-07 尽量只改 WindowCapture.cs；取消语义留给 P-05 |
| `src/PrtScr.Capture/WgcWindowCapture.cs`（A04/A10） | 无 | 否 | 可并行 |
| `src/PrtScr.Capture/ScreenCapturer.cs`、`DesktopDuplicationCapture.cs`（A10） | 无 | 否 | 可并行 |
| `src/PrtScr.App/Services/CaptureService.cs`（A10） | P-04、P-05 | 是 | 等 P-04 完成，或拆 P-09b |
| `Directory.Build.props`（A04 备选 b） | 无，但不在 P-08 写域 | 写域冲突 | 需 lead 扩写域，或选 interop 方案 |

### 2.6 未验证 / 不能由本单确认的

- 未在 Windows 10 19045（或任何 build < 20348）上运行发布包，A04 的实际失败形态与「是否存在 Programmatic 授权/包能力要求」未实测。
- 未构造 PrintWindow 挂住现场（可用调试器挂起目标窗口线程来复现，但涉及目标进程操作，本单只读不做；需要主人同意）。
- 未运行 `dotnet build/test/format`：本单为只读前置确认；且 App 整包构建受运行中的 IshWY.exe（PID 29836）占用，属已知环境限制。
- 未运行 GUI，未验证真实热键下的取消时序。

---

## 三、进行中

- 本单交付物即本报告，无产品或测试代码改动。
- task-6（P-12 回归测试补强与独立核查）仍被 task-1..task-5 阻塞，等 lead 通知后 claim。
- 等 lead 对「A04 路线」「A03 阈值/单飞」「A10 拆单」拍板后，对应实现单才具备开工条件。

## 四、风险与遗留

1. **A03 的修法是「脱身」不是「终止」**：WaitAsync 超时后 PrintWindow 线程仍可能长期阻塞。若没有单飞门闩，反复触发会累积线程；若加了门闩，又要定义「门闩被长期占用时后续截图怎么办」（建议直接回退 WGC/屏幕裁剪，并记录一次日志）。
2. **A03 与 P-04（A02）耦合**：超时返回 null 后，上层会回退屏幕裁剪；若历史仍记为「窗口捕获」，就是 A02 的静默降级。P-07 落地时至少要在日志里留下「PrintWindow timeout → fallback」，来源标注由 P-04 统一。
3. **A04 interop 路径本身未在本仓库被窗口用过**：`CreateForMonitor` 已验证，但 `CreateForWindow` 只有声明、没有调用点。改完必须在 Win11 26100 上回归「窗口模式/窗口局部」两条路径，再送 Win10 19045 实测。
4. **A04 的 20348 是 Server 2022 build**：意味着这不是「Win10 旧版本差一点」，而是 Win10 客户端从未支持该 API。若选 interop，19041 声明才自洽；若选抬版本，等于公开宣布不支持 Win10。
5. **A10 修复可能引入重复 Dispose**：多处 ownership 在方法间转移（latest、fallback、frame），修的时候要给每个变量写清 owner，否则容易 double-dispose 或漏 dispose。建议在 PR 描述里逐条列出 owner 转移点。
6. **A10 缺自动化回归手段**：Bitmap/GDI 泄漏可用 `GetGuiResources` 句柄计数做进程内断言，但需要能注入测试宿主，成本不低；本单不要求。若 task-6 要覆盖，需另立测试设计。
7. **未复现 A03/A04 的运行时证据**：本报告的「仍成立」是静态 + 文档结论，不等于已在目标系统复现；对 A04 尤其如此（本机 build 26100 在门槛之上）。

## 五、待确认（请 lead 拍板）

1. **A04 路线**：选 interop（保留 19041，只改 `WgcWindowCapture.cs`，符合 P-08 写域）还是抬最低版本到 20348（需改 `Directory.Build.props`，超出 P-08 写域，且等于放弃 Win10）？我的建议：interop。
2. **A04 的授权/能力子 claim**：是否由主人在 Win10 实机上用发布包验证并回传日志？若无法验证，报告与实现单应把它标成「未证实」，不据此加包能力检查。
3. **A03 参数**：超时阈值取多少（建议 1500–2500ms）；是否强制单飞；超时后是否只记一次日志（建议是）。另外确认 P-07 是否允许只改 `WindowCapture.cs`（我建议是，取消语义留给 P-05）。
4. **A10 拆单**：是否把 P-09 拆成 P-09a（`PrtScr.Capture/*`，可立即与 A-1 并行）与 P-09b（`CaptureService.cs`，等 P-04 完成）？若不拆，P-09 需排在 P-04 之后。
5. **task-6 解锁通知**：P-01/P-02/P-03/P-04/P-06 全部完成且 lead 确认后，我再 claim。

---

### 附：证据复跑命令（只读）

```
git -C E:/Explore/GoodGood/PrtScr rev-parse HEAD
git -C E:/Explore/GoodGood/PrtScr describe --tags --always
git -C E:/Explore/GoodGood/PrtScr status --porcelain -- src PrtScr.sln Directory.Build.props
powershell -NoProfile -Command "[Environment]::OSVersion.Version.Build"
```

行号核对基准：HEAD 0b1ca41；若源码在整改中变动，行号需按新 HEAD 重定位后再引用。

### 附：评审/交接原文锚点

- 评审 A03：`大佬修改材料/新建 文本文档 (2).md:89-97`（核心句在 `:91`、`:93`、`:97`）
- 评审 A04：`大佬修改材料/新建 文本文档 (2).md:99-107`（核心句在 `:101`、`:105`、`:107`）
- 评审 A10：`大佬修改材料/新建 文本文档 (2).md:158`
- 交接索引：`docs/HANDOFF-2026-10-28.md:73`（A03）、`:74`（A04）
- 派单与写域：`Record/plans/2026-10-28-整改总纲.md:91`（P-07）、`:92`（P-08）、`:93`（P-09）

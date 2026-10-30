# P-08 独立复核：WGC 窗口条目改走 interop CreateForWindow

- 单号：task-11（P-08 独立复核，只读）
- 复核对象：`src/PrtScr.Capture/WgcWindowCapture.cs`（工作树 753 行，`git diff --numstat` = **185 增 / 22 删**，与 P-08 自报 +185/-22 完全一致）
- 基线：HEAD 0b1ca41；P-08 自报基线 HEAD 9c81858（docs）、功能提交 86d3541
- 快照时刻：2026-10-30。工作树同时有 P-04 未提交改动（`CaptureService.cs` +44、`CaptureOverlayWindow.Capture.cs` +388、`Record/ProjectLog.md` +4），**不属于 P-08**；`WgcWindowCapture.cs` 的 185/22 与 P-08 自报一致 → 判定当前文件即 P-08 结束态，P-09a 尚未落改。
- **结论：7 条复核项全部通过，0 项必须返工。** 另有 5 条不阻塞验收的观察/后续（见「风险与遗留」与可选清单）。
- 边界遵守：未改任何源码；**未跑 dotnet build / test / format**（按 task-11 指令，P-04 与 P-09a 正在并行改文件，构建由 lead 统一裁决）；未启动 GUI、未杀 IshWY.exe。

## 做了什么

1. 通读复核对象全文（753 行）、P-07 产物 `src/PrtScr.Capture/WindowCapture.cs`（393 行）、P-08 自报 `Record/reports/P-08-wgc-interop.md`（149 行）、A-2 前置确认 `Record/reports/A2-precheck-A03-A04-A10.md`。
2. 用 `git show HEAD:src/PrtScr.Capture/WgcWindowCapture.cs` 取改前版本（590 行），对比 `CaptureWindowAsync`（旧 :122-144）、`CaptureItemAsync`（旧 :444-458）、interop 声明与显示器捕获段，判断「改前等价」与「是否误改显示器路径」。
3. 用 `git diff -U0 -- src/PrtScr.Capture/WgcWindowCapture.cs` 逐 hunk 核对改动面（15 个 hunk），确认全部落在窗口捕获路径与新增类型/注释。
4. `grep` 全 `src` 的 WGC 消费者、App 日志接线（`App.xaml.cs:56`）与 App 侧窗口捕获调用点，评估对 P-04 接线的影响。
5. 只读核查，未执行任何构建/测试命令，未修改工作树。

## 证据

### 环境与快照

| 命令 | 结果 |
| --- | --- |
| `git status --porcelain` | `M Record/ProjectLog.md`、`M src/PrtScr.App/Services/CaptureService.cs`、`M src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs`、`M src/PrtScr.Capture/WgcWindowCapture.cs`；未跟踪 `.tmp/`、`Record/reports/P-08-wgc-interop.md`、`大佬修改材料/` |
| `git diff --numstat -- src/PrtScr.Capture/WgcWindowCapture.cs` | `185	22	src/PrtScr.Capture/WgcWindowCapture.cs` |
| `git show HEAD:... \| wc -l` | 改前 590 行；当前 753 行 |
| `grep -rn 'WgcWindowCapture.CaptureWindow\|WgcWindowCaptureResult\|WgcWindowCaptureFailure' src --include=*.cs` | 仅 `WgcWindowCapture.cs` 自身定义处 + `WindowCapture.cs:253`（兼容入口调用）；App 与测试无消费者 |
| `grep -rn 'WgcWindowCapture.Log' src/PrtScr.App` | `App.xaml.cs:56: PrtScr.Capture.WgcWindowCapture.Log = Log;` |

### 7 条复核结论表

| # | 复核点 | 结论 | 行号证据 |
| --- | --- | --- | --- |
| 1 | interop 声明/参数顺序是否与 CreateItemForMonitor 同构、GUID/IID 正确复用 | **通过** | 声明 `WgcWindowCapture.cs:85-95` 未被 diff 触及：`:86 [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]`、`:92 void CreateForWindow(nint window, in Guid iid, out IntPtr item)`、`:94 CreateForMonitor` 同形；`:83 IID_IGraphicsCaptureItem = 79C3F95B-31F7-4EC2-A464-632EF5D30760`；调用 `:591 interop.CreateForWindow(hwnd, IID_IGraphicsCaptureItem, out var itemAbi)` 与显示器 `:549 interop.CreateForMonitor(hMonitor, IID_IGraphicsCaptureItem, out var itemAbi)` 参数序完全同构（句柄在前、IID 在后，`:90-91` 注释记录 2026-10-19 实测原因） |
| 2 | item 获取失败的每条出口是否可判别 Failure 且带 HRESULT/Detail；日志是否真的会写 | **通过** | 出口全覆盖：CreateForWindow 抛异常 → `:614-620` 记 `hresult=ex.HResult`、`detail=Describe(ex)`、日志；返回空 item → `:592-598` 记 `0x80004003`(E_POINTER)、Detail、日志；二者由 `:229-234` 映射为 `ItemCreationFailed` 并原样带出。其余出口：InvalidWindow `:192-197`（0x80070057）、Canceled `:199-204`/`:212-217`、DeviceUnavailable `:236-242`、CaptureFailed `:248-253`/`:267-272`、NoFrame `:261-266`，全部带 Failure；非 COM 失败 HResult=0 属文档化选择（`:41-44` 注释）。日志：`:79 public static Action<string>? Log`，各出口均 `Log?.Invoke(...)`（null 时静默不抛，失败仍由返回值可判别）；App 已接线 `App.xaml.cs:56`，`WindowCapture.Trace` 也回退到同一 sink（`WindowCapture.cs:98-99`） |
| 3 | ABI 指针与 interop RCW 释放链是否有 use-after-release/重复 Release | **通过** | `CreateItemForWindow` `:581-621`：`:587` factory → `:588` RCW → `:591` CreateForWindow → `:602` `FromAbi(itemAbi)` → `:604-607 finally Marshal.Release(itemAbi)` → `:609-612 finally Marshal.ReleaseComObject(interop)`。itemAbi 恰释放一次、interop RCW 恰释放一次；`FromAbi` 抛异常时仍走同一 finally；itemAbi==0 时不释放空指针。与已投产的显示器模板 `:541-574`（`:545` factory、`:546` RCW、`:549` CreateForMonitor、`:557` FromAbi、`:562` Release、`:566` ReleaseComObject）逐行同构；无 Release 后再使用。备注：item RCW 本身在成功/设备不可用路径未 Dispose，属 P-09a（改前同样如此，非本单回归） |
| 4 | 兼容入口 CaptureWindowAsync 行为是否与改前等价，上层调用是否受语义变化影响 | **通过** | 签名改前 `HEAD :122 CaptureWindowAsync(WindowInfo, CancellationToken = default) → Task<Bitmap?>`，改后 `:179-183` 完全相同；改前 catch-all `HEAD :140-142 return null`，旧 `CaptureItemAsync` `HEAD :444-458` 也 catch-all 返回 null；改后 `:181-182` 转调详细入口并返回 `.Bitmap`，详细入口 `:189-224` 内部消化所有异常 → 失败一律 null、从不抛出，与改前可观察行为一致（仅新增日志）。全仓唯一调用点 `WindowCapture.cs:253`；App 现有调用：`CaptureOverlayWindow.Capture.cs:297/:339` 用 P-07 的 `WindowCapture.CaptureDetailedAsync`、`CaptureService.cs:243` 用 `WindowCapture.CaptureAsync`、`MainWindow.xaml.cs:148` 用同步 `WindowCapture.CaptureWindow`——均不依赖 WGC 新类型，不受影响 |
| 5 | 是否误改显示器捕获路径（CreateItemForMonitor、显示器常驻池） | **通过** | `git diff -U0` 的 15 个 hunk 全部对应旧 :15-:458 的窗口捕获区与新增类型/注释；`CreateItemForMonitor :541-574`、`EnsureMonitorPool`、`GetLatestMonitorFrameAsync`、`RebuildMonitorPool`、监视器池字段均无 hunk；`CaptureCoreAsync :623-660`、`CopyFrameToBitmap :662-752` 亦无 hunk（仅行号因新增 51+ 行整体下移） |
| 6 | 取消是否被当作 ItemCreationFailed 而非 Canceled | **通过** | 进入前已取消 → `:199-204` 直接返回 `Canceled`；Task.Run 因 token 取消 → `:212-217 catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)` 返回 `Canceled`；core 内 `CaptureCoreAsync` 的 `WaitAsync(2s, token)`（`:652`）取消时由 `:257-260` 重抛、外层 `:212` 归 `Canceled`；全路径不会落到 `ItemCreationFailed`（该值只在 `:229-234` item 为 null 时产生） |
| 7 | 是否有与本单无关的顺手改动 | **通过** | 15 个 hunk 全部可归因：新增 `WgcWindowCaptureFailure`/`WgcWindowCaptureResult`（`:17-65`）、类注释改述（`:67-75`）、兼容入口注释与转调（`:175-183`）、详细入口（`:189-224`）、core 拆分与失败分类（`:226-273`）、`Describe`（`:275-276`）、`CaptureItemAsync`→`CreateItemForWindow`（`:576-620`）。无 csproj/Directory.Build.props/README/.gitignore/测试/App 改动；工作树里两个 App 文件属 P-04（未提交，见快照表） |

### diff hunk 一览（`git diff -U0`，旧 → 新）

| 旧位置 | 新位置 | 内容 |
| --- | --- | --- |
| -15 | +16,51 | 新增 failure 枚举 + result 类 |
| -20 | +71,4 | 类注释改述为 CreateForWindow 路线 |
| -121 | +175,4 | 兼容入口注释 |
| -123 | +181,25 | 兼容入口转调详细入口 + 详细入口开头（HWND=0/预取消） |
| -126,6 | +208,17 | Task.Run 改调 core + 取消/异常分类 |
| -133,3 | +226,9 | core 拆分 + CreateItemForWindow 调用 |
| -137,2 | +236,6 | 设备可用性失败分类 |
| -140 | +243,25 | 帧拷贝空/NoFrame/异常分类 |
| -142 | +269,3 | core 兜底 catch 记录 HRESULT |
| -145 | +275,3 | 新增 Describe |
| -444 | +576,6 | 删旧 CaptureItemAsync，改 CreateItemForWindow 注释/签名 |
| -446,4 | +583,2 | hresult/detail 初始化 |
| -452 | +587,26 | ActivationFactory → interop → CreateForWindow → FromAbi → Release 链 |
| -454 | +614 | catch 改 `Exception ex` |
| -455 | +616,3 | catch 记录 hresult/detail/日志 |

### 对 P-04 接线的影响（本单验收要求）

1. **P-04 现状与新 API 无耦合，无需为 P-08 改任何代码。** P-04 当前消费 P-07 的 `WindowCapture.CaptureDetailedAsync`（`CaptureOverlayWindow.Capture.cs:297` 窗口局部、`:339` 窗口模式）；P-08 只加新类型、不改 `WindowCaptureResult`，所以 P-04 的编译与行为都不受影响。
2. **P-08 的详细 API 目前是「死 API」。** 全 src grep 只有 `WindowCapture.cs:253` 调用兼容入口 `WgcWindowCapture.CaptureWindowAsync`；`CaptureWindowDetailedAsync` / `WgcWindowCaptureFailure` / `WgcWindowCaptureResult` 无任何 App 或测试消费者。失败模型要真正产生用户可见价值，必须有人接线。
3. **接线点只能在 `WindowCapture.cs`（P-07 写域），不在 P-04 写域。** 若要 A02/P-04 在 UI/历史里区分 WGC 失败原因（例如 Win10 上 `ItemCreationFailed`），需把 `WindowCapture.cs:250-263` 的兼容调用换成 `CaptureWindowDetailedAsync`，并把 `Failure/HResult/Detail` 映射进 `WindowCaptureResult`（新增字段或塞进 `Detail`）。这需要 lead 决定：扩 P-04 写域、另立 follow-up（建议 P-07b，写域仅 `WindowCapture.cs`）、或接受当前「只标注来源、不解释 WGC 失败」。
4. **取消语义的连带缺口（P-07 侧，非 P-08 缺陷）。** 兼容入口把取消吞成 null，`WindowCapture.cs:253` 之后无法区分「WGC 取消」与「WGC 失败」：若在 `:247` 的 token 检查之后、WGC 返回之前取消，P-07 会落到 `:278-283` 报 `PrintWindowUnavailable`/`Timeout` 而非 `Canceled`；同时 `WindowCapture.cs:255-257` 的 `catch (OperationCanceledException)` 在 P-08 后成为死分支（兼容入口不再抛 OCE）。接线详细入口后可一并修正：`IsCanceled` → `WindowCaptureFailure.Canceled`。
5. **快照提醒。** P-04 正在改 `CaptureService.cs` 与 `CaptureOverlayWindow.Capture.cs`（git status：+44/+388），以上调用点行号基于当前工作树；P-04 落定后如行号漂移，结论不变（依赖的是 `WindowCapture` 公共 API，不是行号）。

## 进行中

- 无。本单只读复核已完成；未执行构建/测试/format，未实机验证 Win10。

## 风险与遗留

- **P-08 自报门禁未经独立复跑。** 自报：PrtScr.Capture 单项目 0 警 0 错 2.65s、sln `OutDir=.tmp/out-p08/` 0 警 0 错 15.44s、Core.Tests 77/77 78ms、format rc=0。本单按指令不跑，需 lead 统一裁决。
- **item RCW 未释放**（`WgcWindowCapture.cs:229-236`：先建 item 再查设备，设备不可用时 item 泄漏；成功路径也未显式 Dispose）。改前同样不释放，非本单回归；P-08 已主动交 P-09a。
- **晚到首帧未 Dispose**（`:642-658`：`TrySetResult` 多帧/超时/取消后帧无人释放）。P-08 已交 P-09a。
- **Win10 19041 未实机验证**：CreateForWindow 1803+ 可用性来自官方文档 + 现有显示器 interop 同模板，属静态可信但未在目标 OS 运行；本机 26100 无法复现门槛。
- **极端防御缺口（理论不可达）**：若 `GraphicsCaptureItem.FromAbi` 返回 null，`CreateItemForWindow` 会以 `hresult=0/detail=""` 返回 null，下游得到无细节的 `ItemCreationFailed`。实际 FromAbi 不会返回 null，不构成返工项。
- **新 API 无消费者**（见 P-04 影响第 2 条），验收后仍会有「新增能力未被使用」的状态，需 lead 决定后续单。

### 可选后续（不阻塞 P-08 验收，勾选后另立单）

- [ ] **P-07b**：`WindowCapture.cs:253` 改用 `WgcWindowCapture.CaptureWindowDetailedAsync`，把 `Failure/HResult/Detail` 映射进 `WindowCaptureResult`；WGC 段取消归 `Canceled`（写域仅 `src/PrtScr.Capture/WindowCapture.cs`）。
- [ ] **P-09a**：确认覆盖 item RCW 释放（`WgcWindowCapture.cs:229-236`）与晚到首帧 Dispose（`:642-658`）。
- [ ] **主人实机**：在 Win10 19041 按 P-08 报告 5 步跑发布包，日志搜 `CreateForWindow` 验证不再出现 20348 依赖失败。

## 待确认

1. 是否另立 **P-07b**（写域 `WindowCapture.cs`）完成 WGC 失败原因向 `WindowCaptureResult` 的映射与取消归一？若立，P-04 无需扩写域。
2. P-09a（task-10）的验收清单是否明确包含「item RCW 释放」与「晚到首帧 Dispose」两处？
3. Win10 19041 实机验证是否列入主人验收清单（本机无法覆盖）？
4. P-08 自报的四条门禁由 lead 统一复跑时，是否需要在 P-04/P-09a 全部落定后的干净树上跑（当前工作树含 P-04 未提交改动）？
5. 在 P-07b 落地前，P-08 新 API 保持无消费者是否可接受（当前仅为后续能力预留）？

# B-1 CaptureSession / CaptureResult 设计草案（task-19）

- 状态：设计定稿待评，未改任何产品代码。
- 基线：HEAD 0b1ca41 + A 批次未提交工作树（2026-10-30 21:0x 快照）；文中行号均按该快照核对。
- 编号口径：整改总纲 197 行版里 B-1 = EditDocument（P-13）、B-2 = CaptureSession / Result（P-14）；lead 派 task-19 时把 CaptureSession / Result 提为「B 批次第一块」并称 B-1。**本文只覆盖 CaptureSession / CaptureResult（原 B-2 / P-14），不含 EditDocument。**
- 交付范围：五节 = 现状清单 / 类型草案 / 迁移路径 / 边界裁决建议 / 未决问题。

## 0. 一句话结论

把「一次取景」从一组分散布尔量（`CaptureService._isCapturing` / `_captureSessionId`、overlay `_closed` / `_sessionFinished` / `_confirming` / `_saving`）收敛为**一个单槽、带状态机、带唯一收尾漏斗的 CaptureSession 对象**；把「一次采集的结果」从五种互不兼容的返回形态（`SelectionCapture` / `CaptureOutcome` / `WindowCaptureResult` / `WgcWindowCaptureResult` / `(Bitmap?, bool)`）收敛为**一个带 Origin × Engine × Failure 与 Bitmap 所有权的 CaptureResult**。P-04b 已经用 `FinishCaptureSession` 把收尾做对了，但那是方法级约定；本设计把它变成类型不变量。

---

## 1. 现状清单：一次取景从热键到落历史

### 1.1 调用链与线程（当前工作树）

| 阶段 | 入口/代码 | 线程 | 备注 |
|---|---|---|---|
| 热键命中 | `EscOverPinHook` 钩子回调 → `RaiseDeferred`（src/PrtScr.App/Services/EscOverPinHook.cs:275-292） | 低级钩子线程 → 线程池 | 回调内只投递，不写盘/不持锁（P-06） |
| 进 UI | src/PrtScr.App/MainWindow.Hotkeys.cs:54-64 `DispatcherQueue.TryEnqueue(() => { App.Log(...); OnHotkeyPressed(); })` | 线程池 → UI | 与 RegisterHotKey 兜底共用 300ms 去重（:36） |
| 开始取景 | src/PrtScr.App/MainWindow.Capture.cs:114-164 `OnHotkeyPressed` → :158 `CaptureService.Instance.StartCaptureAsync()` | UI | 延时截图/快捷模式在此分流（:135-156） |
| 冻结帧 + 枚举 | src/PrtScr.App/Services/CaptureService.cs:120-193 | UI（内部 await 后回 UI） | WGC 池取帧 :203、DXGI :223、BitBlt :236、前台窗口 :259 |
| 取景交互 | src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs / .xaml.cs | UI | 钩子/轮询只 enqueue（:66-69、xaml.cs:354-376） |
| 确认取图 | Capture.cs:529-648 `ConfirmAsync` → :558 `CaptureSelectionAsync(session)` → :299-403 | UI；窗口捕获在引擎线程 | 窗口整窗 :359、窗口局部 :317 调 `WindowCapture.CaptureDetailedAsync`；屏幕裁切 :431-445 |
| 编码/落库 | Capture.cs:594 `Task.Run(EncodePng)`；:603-606 Clipboard + History | 编码在线程池，写入在 UI 上下文 | 每次 await 后 :597/:614 校验会话 |
| 收尾 | Capture.cs:197-237 `FinishCaptureSession` → CaptureService.cs:74-94 `EndCaptureSession` | UI | P-04b 唯一漏斗 |
| 取消 | Capture.cs:148-156 `Cancel` → :243-254 `Dismiss` | UI | 先 Invalidate 再 Dismiss |
| 快捷全屏 | src/PrtScr.App/MainWindow.Capture.cs:22-89 → CaptureService.cs:340-371 `CaptureFullScreenPngAsync` | UI + 引擎线程 | **不经过 CaptureSession**，另有 `_quickCaptureBusy`（:171，Interlocked） |
| 延时 | MainWindow.Capture.cs:167-210 `StartDelayedCaptureAsync` + `_delayCts` | UI | 到点才进入 StartCaptureAsync |

### 1.2 状态清单（变量 → 写点 → 读点 → 线程）

**CaptureService（src/PrtScr.App/Services/CaptureService.cs）**

| 状态 | 声明 | 写点 | 读点 | 写入线程 |
|---|---|---|---|---|
| `_isCapturing` | :22 | :169（true）、:80（EndCaptureSession 清 false） | :31 IsCapturing、:123；MainWindow.Hotkeys.cs:44、MainWindow.Capture.cs:129、MainWindow.Windows.cs:321/:352 | UI |
| `_captureSessionId` | :25 | :171（新会话）、:63（Invalidate）；End 不清（见 :71-72 注释） | :39 CurrentCaptureSessionId、:50 IsCurrentCaptureSession | UI；读取持 `_sessionGate`（:24）可跨线程 |
| `_currentMonitor` | :23 | :158 | :91 EndCaptureSession → WGC NotifyOverlayClosed | UI |
| `_overlay` | :21 | :107 EnsureOverlay | :128 Cancel、:164/:172 | UI |
| `Hotkey` | :97 | 由 MainWindow 注入（MainWindow.Hotkeys.cs:33） | :90/:176 | UI |

**CaptureOverlayWindow（src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs）**

| 状态 | 声明 | 写点 | 读点 | 线程 |
|---|---|---|---|---|
| `_full`（Bitmap） | :63 | :122 初始 1×1、:227 Prepare 接管 frozen、:210 Finish 释放、:216 Closed 释放 | InitializeFrozen :390-394、CaptureSelection 裁剪 | UI |
| `_monitor` / `_windows` | :64-65 | Prepare :228-239 | 裁剪、窗口枚举 | UI |
| `_captureMode` | :72 | Prepare :247-249、ToggleCaptureMode | 选区分支 | UI |
| `_selection` / `_hasSelection` | :71/:76 | 鼠标/键盘/Reset :272-273 | Confirm :508、Crop | UI |
| `_hoverWindow` / `_clickedWindow` / `_subregionWindow` / `_subregionCandidate` | :77-80 | 鼠标移动/点击/Reset | ConfirmCurrentTargetAsync :511-514、CropFromWindow | UI |
| `_dragging` / `_movingSelection` / `_resizingSelection` 等 | :69-89 | 指针事件 | 指针事件 | UI |
| `_closed`（volatile） | :97 | :176 HideForCapture（true）、:260 Reset（false）、:783 RestoreFailure（false） | 钩子 :74、轮询 :356、Dismiss :246、Confirm :533、Save :679 | 写 UI；读 UI + 轮询线程 |
| `_sessionFinished` | :100 | :262 Reset（false）、:201 Finish（true） | :406 CanContinue、:777 Restore | UI |
| `_confirming` | :99 | :500 guard、:525 finally | :500 | UI |
| `_saving` | :98 | :684、:759 | :679 | UI |
| `_keysSuspended`（volatile） | :101 | :686、:759 | Activated :182/:189、轮询 :371 | 写 UI；读 UI + 轮询线程 |
| `_escPollTimer` / `_escPollHeld` | :90-91 | Start :340-346、Stop :348-352 | PollEscKey :354-376 | System.Threading.Timer 线程 + UI enqueue |

**CaptureOverlayWindow.Capture.cs（流程局部状态）**

- `Cancel` :148-156：`CurrentSessionId()` :150、`InvalidateCaptureSession` :152、`Dismiss(session, "cancel")` :155。
- `FinishCaptureSession` :197-237：`_sessionFinished` 幂等 :199-203；`Frozen.Source=null` :209；`_full.Dispose` :210；`_full=new Bitmap(1,1)` :211；延迟 GC :220-232；`CaptureService.EndCaptureSession` :234；日志 :235；`Dismissed?.Invoke` :236。
- `Dismiss` :243-254：`_closed` 早退分支已删除，改为「已隐藏也收尾」:247-249；否则 HideForCapture + Finish :252-253。
- `CaptureSelectionAsync` :299-403：产出私有 `CaptureOutcome`；成功带 `SelectionCapture`（:35-41）；Busy/Timeout/Canceled 走 `ToAbort` :412-428。
- `ConfirmAsync` :529-648：局部 `captured/ended/keepOverlay/ownsFinish` :547-552，正是缺一个会话对象时的**手工状态机**。

**引擎层**

| 状态 | 声明 | 写/读 | 线程 |
|---|---|---|---|
| `WindowCapture._printWindowFlights` | src/PrtScr.Capture/WindowCapture.cs:138 | `TryEnterFlight` :140、`ExitFlight` :141；`StartPrintWindow` :158-188 | UI/线程池（ConcurrentDictionary） |
| PrintWindow 晚到 Task | WindowCapture.cs:158-188 | LongRunning 线程；`ContinueWith` 在 :173 Dispose 被放弃的 Bitmap | 专用线程 + 线程池 |
| `WgcWindowCapture._cachedWinrtDevice/_cachedDevice/_cachedContext` | src/PrtScr.Capture/WgcWindowCapture.cs:123-126 | `GetOrCreateDevice` :128（`_deviceLock` :123） | WGC 窗口捕获 Task.Run（:218-220）与显示器池（:365） |
| `WgcWindowCapture._monitorPools` | WgcWindowCapture.cs:337-338 | `EnsureMonitorPool` :344-423（`_poolLock` :346/:401）、`NotifyOverlayClosed` :425、`GetLatestMonitorFrameAsync` :438 | UI 发起；FrameArrived 在 WGC 线程（RetainLatest :487，锁 FrameLock :326） |
| `MonitorPool.Latest` / `OverlayClosedTick` | WgcWindowCapture.cs:321/:327 | RetainLatest/TakeLatest/WaitFrameAfterAsync | WGC 线程 + 取景线程 |
| `GraphicsCaptureItem` / 池 / 会话 | WgcWindowCapture.cs:312-328 | `EnsureMonitorPool` finally 逆序释放 :413-422；`CaptureCoreAsync` 池 finally :756-760、晚到首帧 Dispose :739-748；`ReleaseCaptureItem` :297-310 | WGC 线程 |

### 1.3 现状缺陷（本设计要消除的，不是本轮 bug）

1. **同一语义三种表达**：「本次取景是否还活着」由 `_isCapturing`（服务）、`_captureSessionId`（身份）、`_sessionFinished` / `_closed`（窗口）分别表达；P-04b 靠一个方法约定让它们同步，新增路径很容易漏。
2. **结果形态五套**：`SelectionCapture`（Capture.cs:35-41）、`CaptureOutcome`（:53-63）、`WindowCaptureResult`（WindowCapture.cs:42-105）、`WgcWindowCaptureResult`（WgcWindowCapture.cs:38-65）、显示器链 `(Bitmap?, bool)`（CaptureService.cs:198）、快捷 `QuickCaptureResult`（:338）。来源、失败原因、超时标志在层间被反复拆装，P-04 的 WGC 细节在 WindowCapture.cs:322-334 之后就只剩 Detail 字符串。
3. **取消无唯一定义**：`_closed`（窗口隐藏）、`InvalidateCaptureSession`（身份作废）、`CancellationToken`（引擎）、`_keysSuspended`（另存对话框）四个概念混用；P-04b 之前正是漏了「已隐藏仍要收尾」。
4. **线程归属未类型化**：会话状态事实上只在 UI 线程写，但 `_closed` / `_keysSuspended` 是 volatile、`_captureSessionId` 有锁、WGC 回调在别的线程；没有任何注释以外的机制阻止误写。
5. **所有权靠注释**：frozen（CaptureService.cs:132-134/:188-191）、capture bitmap（Capture.cs:547-552/:639-647）、PNG（:594-614）、WGC 帧/池（WgcWindowCapture.cs:694-761）各自靠 `finally` + 注释维持，跨层转移点没有类型约束。

---

## 2. 类型草案

### 2.1 分层与放置

| 类型 | 放置 | 理由 |
|---|---|---|
| `CaptureSessionState` / `CaptureEndReason` / `CaptureOrigin` / `CaptureEngine` / `CaptureFailure` / 纯映射规则 | `src/PrtScr.Core/CaptureSessionState.cs`（新） | 无 Bitmap、无 WinUI 依赖，可在现有 `PrtScr.Core.Tests` 直接测状态机与文案映射；避免为测试给 Core.Tests 加 App/Capture 引用 |
| `CaptureResult`（含 Bitmap） | `src/PrtScr.Capture/CaptureResult.cs`（新） | 引擎层产物；PrtScr.Capture 已引用 System.Drawing，不引用 App |
| `CaptureSession` / `CaptureTarget` / `CaptureSelectionSnapshot` | `src/PrtScr.App/Services/CaptureSession.cs`（新） | 生命周期与取景窗、热键、历史同一 UI 线程；不把 WinUI 依赖塞进 Capture 层 |

> 备选：把 CaptureSession 也放 Core（不持 Bitmap，只持状态）。否决理由：会话需要冻结图与窗口快照（App 的 WindowInfo），且注定 UI 线程亲和；放 Core 会诱使别处误用。

### 2.2 枚举与纯规则（PrtScr.Core）

```csharp
public enum CaptureSessionState
{
    Idle = 0,
    Preparing = 1,   // 抓冻结帧 + 枚举窗口
    Selecting = 2,   // 取景窗可见，等用户操作
    Capturing = 3,   // 已按确认/OCR/另存，取景窗隐藏，正在出图
    Committing = 4,  // 已有 Bitmap，编码/剪贴板/历史写入中
    Completed = 5,   // 终态
    Canceled = 6,    // 终态
    Failed = 7,      // 终态
}

public enum CaptureEndReason { None, Cancel, ConfirmSuccess, ConfirmEnd, Ocr, Save, StartFailed }

/// <summary>内容语义：窗口本体/窗口局部仍算 Window；屏幕裁切算 Screen。</summary>
public enum CaptureOrigin { None = 0, Window = 1, Monitor = 2, Screen = 3 }

/// <summary>实现机制：与 Origin 正交，供日志、降级提示和历史元数据使用。</summary>
public enum CaptureEngine { None = 0, Wgc = 1, PrintWindow = 2, Dxgi = 3, BitBlt = 4 }

public enum CaptureFailure
{
    None = 0,
    InvalidTarget = 1,       // HWND/矩形无效
    Busy = 2,                // 同一窗口上一张仍在飞
    Timeout = 3,             // PrintWindow 2000ms 或 WGC 2s 首帧
    Canceled = 4,            // 用户/令牌取消
    PrintWindowUnavailable = 5,
    WgcItemCreationFailed = 6,
    WgcDeviceUnavailable = 7,
    WgcNoFrame = 8,
    WgcCaptureFailed = 9,
    MonitorUnavailable = 10, // WGC/DXGI/BitBlt 全失败
    EncodeFailed = 11,
    ClipboardFailed = 12,
    HistoryFailed = 13,
    Internal = 14,
}

public static class CaptureRules
{
    public static bool CanTransition(CaptureSessionState from, CaptureSessionState to);
    public static string EndReasonText(CaptureEndReason reason);      // 精确输出 cancel/confirm-success/confirm-end/ocr/save/start-failed
    public static HistoryCaptureMode ToHistoryMode(CaptureOrigin origin); // Window → Window，其余 → Normal
    public static string? NoticeFor(CaptureFailure failure, int hresult = 0, string? detail = null);
    public static bool IsRecoverable(CaptureFailure failure);         // Busy/Timeout → 恢复取景窗；其余终态
}
```

状态机合法迁移（`CaptureRules.CanTransition` 的唯一真源）：

| From | 允许的 To | 触发 |
|---|---|---|
| Idle | Preparing | 服务开始一次取景 |
| Preparing | Selecting | 冻结帧 + 窗口枚举 + Prepare 成功 |
| Preparing | Canceled / Failed | 启动期取消 / 抓帧或准备异常 |
| Selecting | Capturing / Canceled | 确认/OCR/另存 / Esc、再按 F1 |
| Capturing | Committing / Selecting / Canceled / Failed | 出图成功 / Busy·Timeout 恢复 / 飞行期取消 / 不可恢复失败 |
| Committing | Completed / Selecting / Failed | 写入完成（含部分失败）/ 编码失败恢复 / 不可恢复 |
| Completed/Canceled/Failed | （无） | 终态不可逆 |

### 2.3 CaptureResult（PrtScr.Capture）

```csharp
public sealed class CaptureResult : IDisposable
{
    public Guid SessionId { get; init; }
    public CaptureOrigin Origin { get; init; }
    public CaptureEngine Engine { get; init; }
    public CaptureFailure Failure { get; init; }
    public bool PrintWindowTimedOut { get; init; }
    public int HResult { get; init; }
    public string? Detail { get; init; }
    public Bitmap? Bitmap { get; private set; }        // 所有权：本对象
    public bool IsSuccess => Bitmap is not null && Failure == CaptureFailure.None;
    public bool IsCanceled => Failure == CaptureFailure.Canceled;
    public string? Notice => CaptureRules.NoticeFor(Failure, HResult, Detail);

    public static CaptureResult Success(Guid sessionId, Bitmap bitmap, CaptureOrigin origin, CaptureEngine engine,
        bool printWindowTimedOut = false, string? detail = null);
    public static CaptureResult Failed(Guid sessionId, CaptureFailure failure, CaptureOrigin origin,
        CaptureEngine engine, int hresult = 0, string? detail = null, bool printWindowTimedOut = false);

    /// <summary>把 Bitmap 所有权交还调用方；之后本对象不再 Dispose 它。重复调用返回 null。</summary>
    public Bitmap? TakeBitmap();
    public void Dispose();                              // 只 Dispose 仍归本对象的 Bitmap
}
```

**所有权与转移点（设计约束）**：

| 对象 | 创建 | 转移 | 释放责任 |
|---|---|---|---|
| frozen Bitmap | CaptureService.StartCaptureAsync :149 | Prepare 时 `CaptureSession.Target.Frozen` 接管（:227） | session.Finish/Dispose；服务 finally 只在未交接时释放（:188-191） |
| 窗口捕获 Bitmap | `CaptureResult.Success`（WindowCapture.CaptureDetailedAsync :291/:322） | 成功结果交给 CaptureSession.CaptureAsync 的调用方；裁剪后由 overlay 生成 SelectionCapture | 调用方 `TakeBitmap()` 或 `Dispose()`；裁剪副本归 session 的 SelectionCapture |
| WGC 帧 | WgcWindowCapture.CaptureCoreAsync :733 | CopyFrameToBitmap 后帧 `using` 释放；晚到帧 :739-748 释放 | 引擎内部闭合，不外泄 |
| PNG bytes | overlay EncodePng :594 | 剪贴板 + 历史 | 写入完成即弃；失败只日志/toast |

### 2.4 CaptureSession（PrtScr.App）

```csharp
public sealed class CaptureSession : IDisposable
{
    public Guid Id { get; }
    public CaptureSessionState State { get; private set; }
    public CaptureTarget Target { get; }                 // 冻结帧 + 显示器 + 窗口快照
    public CaptureFailure? Failure { get; private set; }
    public string? Detail { get; private set; }
    public CancellationToken Token { get; }              // 取消请求；不负责中止 PrintWindow（P-07 已处理晚到）
    public bool IsActive => State is Preparing or Selecting or Capturing or Committing;
    public bool IsTerminal => State is Completed or Canceled or Failed;

    public static CaptureSession Begin(nint monitorHandle, WindowRect monitor, Bitmap frozen,
        IReadOnlyList<WindowInfo> windows);

    public bool TryEnterSelecting();
    public bool TryEnterCapturing(CaptureSelectionSnapshot selection); // 冻结确认时的选区/模式/窗口
    public bool TryEnterCommitting(CaptureResult result);              // result 所有权转入会话
    public void ReturnToSelecting(CaptureFailure failure, string detail); // Busy/Timeout
    public void Complete();                                            // Committing → Completed
    public void Cancel();                                              // 任意非终态 → Canceled + 取消令牌
    public void Fail(CaptureFailure failure, string detail);            // 不可恢复 → Failed
    public void Terminate(CaptureEndReason reason);                     // 唯一收尾漏斗；幂等

    public event Action<CaptureSession, CaptureEndReason>? Ended;       // 服务在这里做 Esc 注销/WGC 通知/日志
    public void Dispose();                                             // 释放仍归会话的 Bitmap
}

public sealed record CaptureTarget(
    nint MonitorHandle, WindowRect Monitor, Bitmap Frozen, IReadOnlyList<WindowInfo> Windows);

public readonly record struct CaptureSelectionSnapshot(
    Rect DipRect, CaptureMode Mode, WindowInfo? Window, bool IsWindowRegion);
```

**服务端承载**（改造后）：

```csharp
public sealed class CaptureService
{
    private CaptureSession? _session;                       // 单槽
    public bool IsCapturing => _session is { IsActive: true };
    public Guid CurrentCaptureSessionId => _session?.Id ?? Guid.Empty;
    public bool IsCurrentCaptureSession(Guid id) => _session is { } s && s.Id == id && !s.IsTerminal;
    internal void EndCaptureSession(CaptureSession session, CaptureEndReason reason); // 仅由 session.Ended 调用
}
```

`EndCaptureSession` 内部与现在 CaptureService.cs:74-94 等价：清 `_session`、`Hotkey.EndCaptureMode()`、`WgcWindowCapture.NotifyOverlayClosed`、日志 `Capture: session {id|none} finished reason=…`；重复收尾仍打印 `finish ignored (not capturing, reason=…)` 以保持 G1 关键字不变。

**不变量（应由类型 + 断言保证，不靠注释）**：

| # | 不变量 | 失败时的表现 |
|---|---|---|
| I1 | 进程内同时最多一个 ActiveSession（服务单槽） | Begin 前断言 `_session is null or terminal` |
| I2 | Id 每次新生成且不复用；终态后所有写入必须校验 `IsCurrent` | 晚到结果丢弃并记日志 |
| I3 | 只有 `Terminate` 能离开 Active；`Terminate` 幂等且只触发一次 `Ended` | 第二次仅记 `finish ignored` |
| I4 | 终态不可逆（Completed/Canceled/Failed 无出边） | `TryTransition` 返回 false 并记 invalid transition |
| I5 | Bitmap 只有一个 owner；转移用 `TakeBitmap` / 构造参数，禁止两边都 Dispose | Debug 断言 + `ObjectDisposedException` 日志 |
| I6 | 状态只在 UI 线程写；引擎层只拿 `Token` 与只读 Target | Debug 断言 `DispatcherQueue.HasThreadAccess` |
| I7 | 取消 ≠ 失败：Canceled 不写剪贴板/历史、不发失败 toast；Busy/Timeout 不终结会话 | 行为矩阵（§4.2） |
| I8 | 收尾日志格式与 G1 判据逐字不变 | P-12 行为表 G1 可直接复用 |

---

## 3. 迁移路径（三步，每步可独立验收）

### 第 1 步：只加类型与纯规则（零行为变化）

- 新增 `src/PrtScr.Core/CaptureSessionState.cs`（枚举 + `CaptureRules`）、`src/PrtScr.Capture/CaptureResult.cs`、`src/PrtScr.App/Services/CaptureSession.cs`；只在 `CaptureService` 里加一个未使用的 `CaptureSession? _session` 字段与内部方法，不接线。
- 新增 Core 测试（`src/PrtScr.Core.Tests/CaptureSessionTests.cs`）：状态机全部合法/非法迁移（至少 14 条）、终态不可逆、Terminate 幂等、EndReasonText 六个字符串、ToHistoryMode 三档、NoticeFor/IsRecoverable 的 Busy/Timeout/Canceled 分支。
- **验收判据**：`dotnet build PrtScr.sln -c Debug` 0 警 0 错；`dotnet test` 全过且总数 ≥ 89 + 新增；`dotnet format` rc=0；`git diff` 除新增文件外无行为改动（`git diff --stat` 与调用点 grep 证明）；运行时行为与改前一致（未接线）。
- 风险：类型草案与后续实现漂移。缓解：本文件就是实现单附件，P-14 实现时先对照本表勾选。

### 第 2 步：接线（引擎出 CaptureResult；App 用 CaptureSession）

- `WindowCapture.CaptureDetailedAsync` 改为返回 `CaptureResult`（`CaptureAsync` 兼容入口保留到第 3 步），映射表：`PrintWindowUnavailable→PrintWindowUnavailable`、`Busy→Busy`、`Timeout→Timeout`、`Canceled→Canceled`、`InvalidBounds→InvalidTarget`、`Error→Internal`；`WgcFailure` 细化到 `WgcItemCreationFailed/WgcDeviceUnavailable/WgcNoFrame/WgcCaptureFailed`。
- `WgcWindowCaptureResult` 保留为引擎内部类型，只在 WindowCapture 边界映射；`CaptureWindowDetailedAsync` 的 `HResult/Detail` 原样进 `CaptureResult`。
- `CaptureService.StartCaptureAsync`：`_session = CaptureSession.Begin(...)`；`EnsureOverlay` / `Prepare(session, ...)`；`Hotkey.BeginCaptureMode` 不变；`EndCaptureSession` 改由 `session.Ended` 驱动。
- `CaptureOverlayWindow`：`Prepare(CaptureSession session, WindowRect monitor, IReadOnlyList<WindowInfo> windows)`（frozen 从 session.Target 取）；`SelectionCapture` / `CaptureOutcome` 删除，改用 `CaptureResult` + `CaptureSelectionSnapshot`；`FinishCaptureSession` → `session.Terminate(reason)`；`_closed` 只表示窗口可见性，`_sessionFinished` 删除（由 `session.IsTerminal` 代替）；`_confirming` / `_saving` 保留为 UI 防重入（不是会话状态）。
- 所有 `CaptureDetailedAsync` 调用传 `session.Token`（当前 :317/:359 未传）。
- **验收判据**：G1 三条日志逐字不变（P-12 报告 :159 判据）；7 条路径矩阵（§4.2）逐条与改前一致；`_isCapturing`、`_captureSessionId`、`_sessionFinished` grep 命中为 0；`SelectionCapture` / `CaptureOutcome` grep 为 0；tests/build/format 全绿；一次手工全流程（截图/窗口局部/整窗/取消/OCR/另存）。
- 风险：`_closed` 语义拆分（窗口隐藏 vs 会话终结）是最容易回归的点；建议保留 `_closed` 名字但注释改为「窗口当前是否已隐藏」，所有收尾判断只问 session。

### 第 3 步：删旧 API 与兼容层

- 删除 `WindowCaptureResult`（若 WindowCapture 已全量返回 CaptureResult）、`CaptureService.EndCaptureSession` / `InvalidateCaptureSession` 的公开形态（内部改 session）、`WgcWindowCaptureResult` 若边界已全量映射则降为 `internal`。
- `CaptureMonitorBestAsync` 返回 `CaptureResult`（Origin=Monitor，Engine 逐级 WGC/DXGI/BitBlt），保留 `IsNearlyBlack` 逻辑；`CaptureFullScreenPngAsync` 返回 `(byte[]? Png, bool LikelyBlack, CaptureResult capture)` 或持有 `CaptureResult` 供快捷路径读来源。
- 快捷全屏路径（MainWindow.Capture.cs:22-89）**本步不并入会话**，只消费 `CaptureResult` 的来源/失败；是否会话化见 §5 问题 2。
- **验收判据**：`grep -rn "_isCapturing|_captureSessionId|EndCaptureSession|SelectionCapture|CaptureOutcome|WindowCaptureResult" src/` 仅剩注释/设计文档；tests/build/format；G1 手工复跑一次；主人实测清单原样通过。
- 风险：删除公开 API 会影响未纳入本仓库的测试/工具；本项目无该情况（Core.Tests 不引用 App/Capture），仍建议 `git grep` 全仓确认。

---

## 4. 边界裁决建议

### 4.1 取消的唯一定义

- **算 Canceled**：Esc、取景中再按 F1、确认后取图飞行期再按 F1/Esc、`Token` 已请求（引擎返回 Canceled）、启动期被取消。
- **不算 Canceled**：窗口失效/尺寸非法（InvalidTarget/Failed）、PrintWindow 或 WGC 超时（Timeout → 恢复取景窗，不终结）、同一窗口上一张还在飞（Busy → 恢复取景窗）、剪贴板/历史写入失败（Completed 或 Failed，见下）。
- **取消的可见效果**：不写剪贴板、不写历史、不发失败 toast；日志仍走 `Capture: session … finished reason=cancel`；取景窗隐藏（与当前一致）；Esc 全局热键在 `Ended` 中注销。

### 4.2 结果矩阵（改前 → 改后必须一致）

| 场景 | 会话终态 | 历史 | 剪贴板 | 取景窗 | 日志/提示 |
|---|---|---|---|---|---|
| 正常屏幕裁切 | Completed | 写（Normal） | 写 | 隐藏 | `engine=…` + 成功 toast |
| 窗口整窗（WGC） | Completed | 写（Window，进程/标题） | 写 | 隐藏 | 来源日志 |
| 窗口局部（WGC） | Completed | 写（Window） | 写 | 隐藏 | 来源日志 |
| PrintWindow 成功 | Completed | 写（Window） | 写 | 隐藏 | `PrintWindow` |
| PrintWindow 超时→WGC 成功 | Completed | 写（Window） | 写 | 隐藏 | `PrintWindowTimedOut` + 提示 |
| PrintWindow 超时→WGC 失败 | Failed | 不写 | 不写 | 恢复 | 窗口响应超时 |
| Busy（同窗口在飞） | Selecting（不终结） | 不写 | 不写 | 恢复 | 上一张截图还在处理中 |
| 用户取消 | Canceled | 不写 | 不写 | 隐藏 | `reason=cancel` |
| 编码失败 | Failed | 不写 | 不写 | 恢复 | 保留编辑内容 |
| 剪贴板失败 | Completed（历史成功）或 Failed | 按实际写入 | 不写 | 隐藏 | 复制失败 toast |
| 历史失败 | Completed | 不写 | 写 | 隐藏 | 失败 toast/日志 |
| OCR / 另存 | Completed | 按现有语义 | 按现有语义 | 隐藏 | `reason=ocr/save` |

### 4.3 把 P-04b 漏斗上升为类型规则

- 所有路径只允许调用 `session.Terminate(reason)`；`_full` 释放、`Hotkey.EndCaptureMode`、`NotifyOverlayClosed`、`_isCapturing=false` 全部挂在 `Ended` 事件上（服务订阅一次）。
- `Dismiss(session, reason)` 不再直接调用 `FinishCaptureSession`；它只负责隐藏窗口并 `Terminate`。`FinishCaptureSession` 可保留为 `Ended` 的 UI 侧清理（释放 frozen、GC 提示、`Dismissed` 通知）。
- 终态后再收到 Dismiss/Cancel 只记 `finish ignored`，不重复副作用（与 P-04b 现状一致）。
- 建议 Debug 下加断言：`Terminate` 时若 `State` 已是终态，日志必须包含 `finish ignored`；若 `State` 是 Idle 且 `reason != StartFailed`，记一条 `invalid transition` 供测试捕获。

### 4.4 提交点（不可逆边界）

- `Capturing → Committing` 在拿到 Bitmap 后；`Committing` 内先校验 `IsCurrent`，再编码，再校验，再写剪贴板/历史。
- 一旦任一次写入开始（剪贴板或历史），取消只影响其后的步骤，不回滚已写内容；此时会话可在 `Complete` 前被取消，但 `Terminate` 只清状态不删已写文件（与现状一致）。
- 建议把「先剪贴板后历史」的顺序固定进规则（当前 `Task.WhenAll` 并行），避免取消时一半写一半不写；若要并行，需在结果里显式记录各自成功/失败（CaptureResult 可加 `ClipboardWritten` / `HistoryWritten`，属实现细节）。

---

## 5. 未决问题（需 lead/主人拍板）

1. **CaptureSession 是否持有 overlay 引用？** 建议不持有：服务持 overlay，session 只持状态；overlay 通过参数接收 session。否则循环引用，且窗口复用会让 session 生命周期与窗口生命周期纠缠。
2. **快捷全屏路径是否也会话化？** 建议本批不做（它没有取景窗、没有用户选择，只有 `_quickCaptureBusy` 互斥）；只复用 `CaptureResult` 的来源/失败。若要做，单列 P-14b。
3. **历史元数据是否新增 `source` / `fallbackReason`？** P-04 报告已提为待确认；本设计建议加可选字段（不改旧字段），但这会改 `CaptureHistoryService` / `HistoryItem` / `.json`，超出 P-14 原始写域，需另立单或扩写域。
4. **延时截图**：建议延时期间不算会话（保持 MainWindow `_delayCts`），到点进入 `StartCaptureAsync` 才 Begin；否则取消语义会与「再按一次取消延时」冲突。
5. **多显示器/跨屏选区**：当前一次只取鼠标所在显示器（CaptureService.cs:15 注释）；会话的 `Monitor` 单值设计需在 M7 扩展为多显示器目标，类型上预留 `IReadOnlyList<WindowRect>` 还是保持单值，请 lead 定。
6. **A15 设备缓存/设备失效**：`_cachedWinrtDevice` 与 `_monitorPools` 是进程级，不随会话释放。建议保持进程级（否则每次 F1 重建池会回到爆闪），设备失效（A15）另做「重建一次并重试」的规则，不塞进 CaptureSession。
7. **测试工程**：状态机/规则可进 Core.Tests（推荐）；`CaptureResult` 与引擎映射无法在 Core.Tests 测（不引用 Capture）。是否新建 `src/PrtScr.Capture.Tests`（纯单元，不碰 GUI）请 lead 定；若不建，映射靠 §4.2 手工矩阵。
8. **`CaptureResult` 是否取代 `WgcWindowCaptureResult`**：建议 WGC 保持内部类型、WindowCapture 边界映射，避免 Capture 层内部 API 大改；请实现单确认。

---

## 附录 A：改动文件预估（实现单直接可用）

| 步骤 | 新文件 | 改文件 | 预计行数 |
|---|---|---|---|
| 1 | Core/CaptureSessionState.cs、Capture/CaptureResult.cs、App/Services/CaptureSession.cs、Core.Tests/CaptureSessionTests.cs | 无 | +260 / 测试 +180 |
| 2 | 无 | WindowCapture.cs、CaptureOverlayWindow.Capture.cs、CaptureOverlayWindow.xaml.cs、CaptureService.cs、WgcWindowCapture.cs（边界映射） | 约 +180/-140 |
| 3 | 无 | WindowCapture.cs、CaptureService.cs、MainWindow.*（消费点）、CaptureHistoryService?（元数据另单） | 约 -120 |

## 附录 B：关键证据索引（当前工作树行号）

- 收尾漏斗：CaptureOverlayWindow.Capture.cs:197-237、:243-254；CaptureService.cs:74-94。
- 会话身份：CaptureService.cs:34-67；Capture.cs:148-156、:159-165。
- 取图分支：Capture.cs:299-403（:317/:359 窗口捕获，:385 降级，:399 降级日志）。
- 确认路径：Capture.cs:529-648（:547-552 局部状态、:587 收尾、:639-647 finally）。
- 另存/OCR：Capture.cs:448-492、:677-769。
- 引擎结果：WindowCapture.cs:42-105、:212-363；WgcWindowCapture.cs:38-65、:199-287、:694-761。
- 显示器链：CaptureService.cs:198-282、:319-332。
- 热键/键盘：MainWindow.Hotkeys.cs:36-64；Capture.cs:66-109；CaptureOverlayWindow.xaml.cs:340-376。
- 状态字段：CaptureService.cs:21-25；CaptureOverlayWindow.xaml.cs:63-110。

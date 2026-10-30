# P-09b（task-13 / A10 后半）：CaptureService 失败/降级路径的位图释放

- 单号：task-13 / P-09b / A10（后半）
- 写域：src/PrtScr.App/Services/CaptureService.cs、Record/reports/**（本单只改这 1 个源文件 + 本报告）
- 执行基线：HEAD = 9c81858（0b1ca41 + 86d3541 + 9c81858）。工作树中 CaptureService.cs 已含 P-04 的未提交改动（会话闸门），本单在 P-04 落地后的当前态上继续改；P-04 的会话字段/方法原样保留。
- 未提交、未跑 --smoke、未用 -t:Compile、未改他人文件。

## 1. 做了什么

P-09a 报告 2.2 表列出的两处 App 层泄漏，逐条修；核心原则与 P-09a 一致：**每张位图任何时刻只有一个 owner，交出所有权必须显式置标志，释放只在 owner 手里发生**。

### 1.1 降级链最后一段：前台窗口 WGC 成功后 fallback 未释放（原 :215-217，现 :233-247）

原实现：

    var foregroundResult = await CaptureForegroundWindowAsync(monitor);
    if (foregroundResult is not null)
        return (foregroundResult, IsNearlyBlack(foregroundResult));
    return (fallback, true);

前台窗口捕获成功时，`fallback`（整屏 BitBlt 图）既不是返回值也没人 Dispose，每次走「全屏游戏 → BitBlt 黑屏 → 前台窗口 WGC 成功」都会漏一张全屏 Bitmap。

改法（:211-256）：
- `var fallbackOwned = true;` + `try/finally`（:213、:252-256）：只要没作为返回值交出，finally 一律 `fallback.Dispose()`。
- 三条出口各自显式置位：黑屏换普通 BitBlt 前先释放第一张（:220-223，置 false→捕获→true）；普通 BitBlt 成功返回时置 false（:229-230）；前台 WGC 成功时保持 true，由 finally 释放（:235-247）。
- 前台结果自身的异常安全：`IsNearlyBlack(foregroundResult)` 若抛（采样异常），catch 内先 `foregroundResult.Dispose()` 再 rethrow（:242-246）——此前这条路径会漏前台图。

### 1.2 StartCaptureAsync 冻结图：异常路径释放（原 :121-155，现 :108-167）

原实现取到 `frozen` 后，`Task.Run(WindowEnumerator.EnumerateVisibleWindows)` / `EnsureOverlay` 之后的分支 / `Prepare` / `Hotkey.BeginCaptureMode()` 任一抛异常，catch 只置 `_isCapturing=false` 并 rethrow，`frozen` 泄漏。

改法：
- `frozen` 声明提到 try 外，配 `frozenHandedOff` 标志（:108-110），catch 保持 rethrow（:155-160），新增 finally（:161-167）：
  `if (!frozenHandedOff) frozen?.Dispose();`
- 取图改为 `var (captured, frozenBlack) = ...; frozen = captured;`（:125-126），让局部变量可被 finally 看见。
- `_overlay is null` 分支不再就地 Dispose（原 :137），改为直接 return（:140-141），释放统一由 finally 完成，避免两处释放点。
- 交接点显式化：`_overlay.Prepare(frozen, monitor, windows)` 成功返回后置 `frozenHandedOff = true`（:148-151）。Prepare 的第 2 条语句就是 `_full = frozen`（`src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:226-227`，前一条是幂等的旧图 Dispose），从这以后 frozen 由取景窗持有，本方法绝不重复释放。

### 1.3 明确未改

- 采集引擎顺序与降级优先级（WGC 常驻池 → DXGI → BitBlt CAPTUREBLT → 普通 BitBlt → 前台窗口 WGC）、`IsNearlyBlack` 阈值、前台窗口「几乎铺满显示器」判定全部未动。
- P-04 会话闸门（`_sessionGate`、`CurrentCaptureSessionId`、`IsCurrentCaptureSession`、`InvalidateCaptureSession`、开始取景时 `Guid.NewGuid()` 与日志）原样保留，本单只在其周围加释放，不改语义。
- 未改 `CaptureOverlayWindow*`、`WindowCapture.cs`、`Wgc*`（P-04/P-09a 已定稿）。

## 2. 证据

### 2.1 门禁（全部实跑，共享锁）

| # | 命令 | 结果 |
|---|---|---|
| 1 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p09b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p09b/"` | **0 警告 0 错误**，17.04s，`BUILD-GUARD: EXIT=0` |
| 2 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p09btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` | **通过 77 / 失败 0 / 跳过 0**，81ms，EXIT=0 |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | **rc=0**（无输出） |

未用 `-t:Compile`（已知假绿：不跑 WinUI XAML 代码生成，冷态 1248 个 CS0103）。构建经 `.tmp/build-guard.ps1` 串行化，避免与并行女仆抢 obj/bin。

### 2.2 资源 → 获取点 → 释放点（本单范围内，行号为改后当前树）

| 资源 | 获取点 | 释放点（文件:行） | owner / 说明 |
|---|---|---|---|
| fallback 整屏 Bitmap（CAPTUREBLT） | `CaptureService.cs:211` `CaptureMonitorByBitBlt` | 黑屏弃用 :220；作为 BitBlt 结果交出 :229-230（置 `fallbackOwned=false`）；前台 WGC 接管或异常 :254-255 | `CaptureMonitorBestAsync` 独占，标志位保证只释放一次 |
| fallback 整屏 Bitmap（普通 BitBlt 重试） | `CaptureService.cs:222` | 作为结果交出 :249-250；前台 WGC 接管或异常 :254-255 | 同上；替换旧图前旧图已在 :220 释放 |
| 前台窗口 WGC 结果 | `CaptureService.cs:234`（`CaptureForegroundWindowAsync`，底层 `WindowCapture.CaptureAsync`） | 正常作为返回值交出 :240（owner 转给 `StartCaptureAsync` 的 frozen 链）；`IsNearlyBlack` 抛异常 :244 | 交出前归 `CaptureMonitorBestAsync`；交出后与 frozen 同一 owner 规则 |
| frozen 冻结整屏 Bitmap | `CaptureService.cs:125`（`CaptureMonitorBestAsync` 返回值） | 取图为 null 无需释放 :127-131；未交接（overlay 为空/窗口枚举/EnsureOverlay 异常）:165-166；已交接 → 取景窗：`CaptureOverlayWindow.Capture.cs:200`（`FinishCaptureSession` 的 `_full.Dispose()`）或下一次 `Prepare` 的 `CaptureOverlayWindow.xaml.cs:226` | `StartCaptureAsync` 独占到 `Prepare` 成功（:151 置 `frozenHandedOff=true`），之后归 overlay |
| 取景窗冻结 WriteableBitmap | `CaptureOverlayWindow.xaml.cs:387`（`InitializeFrozen` 内 `new WriteableBitmap`），:423 挂到 `Frozen.Source` | 取景窗 `FinishCaptureSession`/`Dismiss`（`CaptureOverlayWindow.Capture.cs:190-205`，`Frozen.Source=null` + `_full.Dispose`） | **不在本单写域**；`InitializeFrozen` 整体 try/catch 吞异常（xaml.cs:457-460），不会把异常抛回 `StartCaptureAsync` |
| 放大镜 WriteableBitmap | `CaptureOverlayWindow.xaml.cs:130` `_loupeBitmap` | 取景窗自身生命周期 | 不在本单写域；与 CaptureService 无交接 |

说明：派单/ A-2 提到的「两张 WriteableBitmap」在当前采集路径上并不由 `CaptureService` 获取——`CaptureMonitorBestAsync` 只产出 1 张 GDI `Bitmap`；两张 WriteableBitmap 是取景窗内的冻结图与放大镜图，在 `InitializeFrozen`（自带 catch）里创建，由取景窗自己释放。本单按写域只修 App 层这 1 张 frozen + 1 张 fallback 的所有权。

### 2.3 与 P-04 会话闸门的交互（不重复释放）

1. **所有权与会话身份解耦**：会话 id 在 `Prepare` 之前由 `CaptureService.cs:144-147` 写入，位图所有权在 `Prepare` 成功后才转给取景窗（:151）。`Prepare` 之后本方法没有 await，因此不存在「await 回来发现会话已失效、再决定要不要释放冻结图」的竞态；失效判断（P-04）只在取景窗内部的确认/OCR/另存 await 之后发生，作用对象是已交接的 `_full`，与 `StartCaptureAsync` 的 finally 不重叠。
2. **取消路径**：Esc → `CaptureOverlayWindow.Cancel` → `InvalidateCaptureSession` + `Dismiss` → `FinishCaptureSession` 释放 `_full`（`CaptureOverlayWindow.Capture.cs:200`）。该 `_full` 就是 `frozenHandedOff=true` 后的 frozen；本方法 finally 看到 `handedOff=true` 不会再 Dispose，**单 owner**。
3. **异常早于交接**：窗口枚举/EnsureOverlay 抛异常时会话 id 尚未生成（:144 之后才生成），取消或会话失效不可能持有所得位图；finally 释放后不存在第二 owner。
4. **Prepare 抛异常**：Prepare 的 `_full = frozen`（xaml.cs:227）之后、`:151` 置标志之前抛出时，finally 会 Dispose frozen，而 overlay 的 `_full` 仍引用它。此路径下 overlay 尚未亮窗：`Prepare` 的最后一步 `InitializeFrozen` 自带 try/catch（xaml.cs:457-460），能抛异常的只有它之前的 MoveAndResize/DetectScale/ApplySelectionColor/UpdateVisuals；因此不会出现「已显示的取景窗绘制已释放位图」。`Bitmap.Dispose` 幂等，下一次 `Prepare` 的 `_full.Dispose()`（xaml.cs:226）是空操作后重新赋值。列为风险 1。
5. **`InvalidateCaptureSession` 不触碰位图**：它只把 `_captureSessionId` 置 Empty（:54-67），不在位图释放链上，无二次释放来源。

### 2.4 改动量与工作树

- `git diff --stat -- src/PrtScr.App/Services/CaptureService.cs`：`1 file changed, 101 insertions(+), 22 deletions(-)`（该 diff 相对 HEAD，含 P-04 既有未提交的会话闸门 +44 行；本单新增的是 :108-110、:125-126、:140-141、:148-151、:161-167 与 :211-256 的 fallback 重写块）。
- 工作树同时有他人未提交改动（MainWindow.xaml(.cs)、CaptureHistoryService.cs、ConfigService.cs、CaptureOverlayWindow.Capture.cs、src/PrtScr.Capture/*、P-02/P-04/P-08/P-09a 报告等），与本单无关；本单只碰 CaptureService.cs 与本报告。
- 行号漂移记录：A-2 旧锚点 :157/:173-175（0b1ca41）→ P-04 之后的 :199/:215-217/:121-155 → 本单改后 :211-256/:108-167。

### 2.5 文件末尾换行（FINALNEWLINE）复核

- 本单重写 CaptureService.cs 期间曾短暂以 LF 且缺末尾换行落盘，导致并行节点跑整解 format 时命中 `CaptureService.cs(548,2) FINALNEWLINE`；20:49 已用 perl 统一 CRLF 并补末尾换行。
- 2026-10-30 20:53:11 复核：文件 548 行、末尾两字节 `0d 0a`（CRLF）；整解 `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → **FORMAT_RC=0**。
- 报告落盘时间晚于 20:49，故本报告 2.1 的 format rc=0 为修复后的结果；lead 收到的 P-07b 阻塞快照应早于该修复。

## 3. 进行中（交接用）

已完成：
- 两处释放修复 + 三门禁同工作树通过（build 0/0、Core.Tests 77/77、format rc=0）+ 本报告。

下一步第一件事：
- 主人按下方实测步骤验收（重点：全屏/游戏路径反复截图后工作集不涨；异常/取消后下一次 F1 仍正常）。
- 通过后由 lead 提交；共享工作树还有他人未提交改动，提交需按路径只 add：
      git add src/PrtScr.App/Services/CaptureService.cs Record/reports/P-09b-captureservice-resources.md

下一步该读的文件：
- src/PrtScr.App/Services/CaptureService.cs:108-167（frozen owner 与 finally）
- src/PrtScr.App/Services/CaptureService.cs:211-256（fallback owner 三段出口）
- src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:224-227、383-461（Prepare 接管点、InitializeFrozen 自带 catch）
- src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:190-205（FinishCaptureSession 的 `_full.Dispose`）

## 4. 风险与遗留

1. **Prepare 抛异常后的短暂双引用**（2.3-4）：finally 释放后 overlay 的 `_full` 仍指向已释放 Bitmap，直到下一次 Prepare/FinishCaptureSession。Dispose 幂等 + 该路径不会亮窗，实际无崩溃面；若要彻底消除，应把 `Prepare` 改成事务式（失败自行回滚并释放 `_full`），属 CaptureOverlayWindow 写域，建议另立小单。
2. **未改 A-2 #8 的状态回滚部分**：`StartCaptureAsync` 在 `Prepare` 之后、`BeginCaptureMode` 抛异常时，overlay 可能已 Prepare/可见而 `_isCapturing=false`，下一次 F1 会再次 Prepare 覆盖。资源不泄漏（frozen 归 overlay，下次 Prepare 释放），但状态短暂不一致；本次按派单只修资源，状态回滚列为遗留。
3. **早窗口并发**：`_isCapturing=true` 在窗口枚举之后才置（:145），此前再次按 F1 会进入第二个 `StartCaptureAsync`（既有行为，非本单引入）。两者各自持有自己的 frozen，Prepare 串行执行并释放上一张 `_full`，本单不产生跨会话二次释放；但并发本身未修。
4. **异常后会话 id 残留**：`Prepare`/`BeginCaptureMode` 抛异常时 `_captureSessionId` 已写入（:144-147），catch 未清。下一次开始取景会覆盖，旧 id 只能通过 `InvalidateCaptureSession` 显式失效；当前无持有者使用它，判定为无害，未改。
5. **未做运行时资源验证**：没有句柄/工作集计数，也没有 --smoke（会写主人真实 data/）。释放正确性目前只有代码级 owner 证明 + 编译/单测；最终确认依赖主人实测。
6. **无自动化测试**：Core.Tests 为纯 Core 层，覆盖不到 App 捕获路径；77 条未减少。
7. **提交粒度**：工作树混有 P-02/P-04/P-08/P-09a 的未提交改动，必须按路径 add，勿用 `git add -A`。

## 5. 待确认

1. 第 4 节风险 1/2 是否要另立一单做「Prepare 事务化 + StartCaptureAsync 失败状态回滚」？涉及 CaptureOverlayWindow 写域，本单未越界。
2. 主人验收时若方便，可否用任务管理器记录连续全屏截图的「工作集」与「GDI 对象」列（GDI 对象持续上涨最能证明 fallback 泄漏已修）；代理侧无法代测。
3. 是否需要把「资源 owner」约定写进 Record/plans 的编码守则（交出前置标志、单点释放），供后续 Capture 相关单引用。

## 主人实测步骤

1. **全屏/游戏降级路径（本单主目标）**：对全屏视频或全屏游戏按 F1 完成截图并 Esc 退出，重复 10 次。预期：每次出图正常；任务管理器里 IshWY 的「GDI 对象」与工作集不持续上涨（此前每次会多一张全屏位图）。
2. **普通窗口/区域截图回归**：常规模式下框选截图、窗口模式点选被遮挡窗口，各 10 次。预期：出图、历史与剪贴板正常，工作集平稳。
3. **异常/取消路径**：F1 进入取景立即 Esc，连续 10 次；随后再正常截一张。预期：无异常弹窗、功能正常；data/logs/app.log 不出现新的「Capture: 异常」刷屏。
4. **快速连按**：快速连按 F1 三次（不等待窗口出现）。预期：不崩溃、最终停在一次正常取景或一次取消；下一次 F1 仍能正常出图。
5. **内存对比**：如方便，记录连续 20 次全屏截图前后的工作集/GDI 对象数，作为「不泄漏」的证据留档。

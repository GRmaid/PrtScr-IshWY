# P-14b 取景会话状态机接线（task-23）

- 基线：HEAD 69616cc（chore(release-prep): 版本号 1.3.1 与 README 更新）。本单改动全部在工作树，未提交。
- 写域：src/PrtScr.App/Services/、src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs、Record/reports/。实际只改 2 个源文件 + 本报告，未碰 Core/Capture/csproj/.gitignore。
- 依赖：P-14a 已交付的 PrtScr.Core 类型（CaptureSessionState/CaptureEndReason/CaptureOrigin/CaptureEngine/CaptureFailure/CaptureRules/CaptureResult），App 通过 GlobalUsings 的 global using PrtScr.Core; 直接可用。

## 1. 做了什么

### 1.1 CaptureService 成为会话单槽（CaptureService.cs，+236 行改动）

1. 会话槽字段 :27-35：_lastSessionId（状态槽身份，取消后仍保留）、_captureState = Idle、_sessionOrigin/Engine/Failure、_sessionPrintWindowTimedOut、_sessionDetail。_captureSessionId 语义保持不变（A05 有效性），两个 id 分工写进注释。
2. 新增 CurrentCaptureSessionState :44-51（lock 读），供后续单查询。
3. InvalidateCaptureSession :84-86：取消时若槽属于该会话且状态活动，记 _sessionFailure = Canceled；原 invalidated 日志 :89 不动。
4. EndCaptureSession :97-151 重写为唯一收尾漏斗：先 lock 清 _isCapturing（全仓唯一 false 赋值 :103）→ 非 capturing 走原 finish ignored 日志 :109 → 匹配槽会话时 ParseEndReason → CaptureRules.TerminateDecision → 写终态与 result 日志 :127 → 槽位回 Idle :135 → 原 Hotkey.EndCaptureMode/NotifyOverlayClosed/finished reason= :146 不动 → 追加输出 stateLog/resultLog。
5. 新增状态推进 API：BeginCaptureSession :157（Idle→Preparing，重置结果元数据）、MarkCapturePrepared :187（Preparing→Selecting）、BeginCaptureAttempt :193（Selecting→Capturing）、MarkCaptureAttemptSucceeded :199（写 Origin/Engine/Timeout，Capturing→Committing，结果日志 :222）、MarkCaptureAttemptFailed :230（写 Failure/Detail，结果日志 :240，Capturing→Selecting 允许直接重试）、ResetCaptureAttempt :250（Committing→Selecting，对话框取消/异常）、TransitionSession :304（一律走 CaptureRules.CanTransition，拒绝也留日志）。
6. StartCaptureAsync 接线：BeginCaptureSession(sessionId) :404（设 _captureSessionId 之后、overlay.Prepare 之前）、MarkCapturePrepared(sessionId) :409（frozenHandedOff 之后、BeginCaptureMode/overlay shown 之前）。

### 1.2 取景窗产出 CaptureResult 语义（CaptureOverlayWindow.Capture.cs，+111 行改动）

1. 删除私有 CaptureContentOrigin，SelectionCapture 的 Origin 改用 Core 的 CaptureOrigin，并携带 Engine 与 PrintWindowTimedOut（:27-38）；CaptureOutcome 增加 Failure（默认 None），Abort 可带失败分类。
2. CaptureSelectionAsync :301 所有构造点补齐：Normal 裁切→Normal/Fallback；窗口局部与整窗成功→Window/MapEngine(Source)/result.PrintWindowTimedOut；无窗口目标兜底→Normal；窗口失败降级→Screen/Fallback + Notice。MapEngine :417。所有 Abort 带分类：取消→Canceled，无选区→InvalidTarget，Busy/Timeout/Canceled 沿用 ToAbort 映射（:429-443）。
3. 三个出口接线：OCR :472/:485/:494/:513，确认 :575/:607/:616/:621，另存 :718/:738/:747/:766/:799；确认来源日志改用 selection.Origin == CaptureOrigin.Screen 判断（:624）。
4. 取消路径未改：Cancel :150 → Invalidate（记 Canceled）→ Dismiss(session, cancel) :157 → FinishCaptureSession :250/:255 → EndCaptureSession(reason=cancel) → Canceled 终态 + result 行。

### 1.3 用户可见行为零变化

- 五条既有日志关键字文本与参数一字未改（见 2.2）；新增日志只追加，不替换。
- 未改 Dismiss/FinishCaptureSession/Cancel 的控制流、未改任何 toast/剪贴板/历史/保存调用点、未改热键与取景交互。
- 状态槽在 End 写完终态后立即回 Idle：设计稿终态无出边，槽位释放不是迁移边；下一次取景仍从 Idle→Preparing 开始，故连续取景不依赖终态出边。
- 收尾用 CaptureRules.TerminateDecision（活动态一律 Allowed），阶段推进用 CanTransition；Selecting→Failed 这类设计稿未列的终止边由 Terminate 承担，没有伪造迁移。
- 取图失败后仍保持取景态：MarkFailed 把 Capturing 推回 Selecting，_isCapturing 仍为 true，主人可立即重试（Enter/Ctrl+C 走 Selecting→Capturing），与 HEAD 的「恢复取景窗」行为一致。
- 另存对话框取消/异常：ResetCaptureAttempt 推回 Selecting，窗口不关闭，与 HEAD 相同。

## 2. 证据

### 2.1 门禁（2026-10-30 22:5x，全部实跑）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14b/" | EXIT=0，已成功生成。0 个警告 0 个错误（19.71s） |
| Core 测试 | pwsh ... -Tag p14btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | EXIT=0，失败 0、通过 206、跳过 0、总计 206（93ms） |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | EXIT=0，无输出 |
| _isCapturing 唯一清零点 | grep "_isCapturing\s*=\s*false" src/PrtScr.App | 仅 Services/CaptureService.cs:103 |

### 2.2 旧关键字→新关键字对照表

| 既有关键字（位置） | 状态 | 新增配套行（同会话） |
| --- | --- | --- |
| Capture: session <n> invalidated（CaptureService.cs:89） | 原样保留 | 取消时新增 state <from> -> Canceled (terminate:cancel) 与 result ... reason=cancel |
| Capture: session <n> finished reason=<r>（CaptureService.cs:146） | 原样保留 | 在同会话追加 state <from> -> <terminal> (terminate:<r>) 与 result state=... origin=... engine=... failure=... timedOut=... success=... canceled=... history=... notice=... reason=<r> |
| Capture: session <n> finish ignored (not capturing, reason=<r>)（CaptureService.cs:109） | 原样保留 | 无 |
| Capture: session <n> already capturing, cancel session=<m>（CaptureService.cs:359） | 原样保留 | 无（取消会话随后产生 state/result 行） |
| Overlay: session <n> finished reason=<r>（CaptureOverlayWindow.Capture.cs:237） | 原样保留 | 无 |
| Overlay: session <n> finish ignored (already finished ...)（:203） | 原样保留 | 无 |
| Overlay: session <n> dismiss while hidden ...（:249） | 原样保留 | 无 |

新增调试关键字（全部追加、可 grep）：

- Capture: session <n> state <from> -> <to> (<context>)：context ∈ begin / prepared / attempt:confirm|ocr|save / attempt-success / attempt-failed:<Failure> / <reset 原因>。
- Capture: session <n> result state=<终态> origin=<Origin> engine=<Engine> failure=<Failure> timedOut=<bool> success=<bool> canceled=<bool> history=<Window|Normal|Legacy> notice="..." reason=<r>。
- 异常分支：state ... rejected (...)、state ... forced (begin)、terminate Duplicate|Invalid (...)、finish while slot=<m> reason=<r>。

### 2.3 六条状态迁移证据（文件:行）

1. Idle→Preparing：CaptureService.cs:157（BeginCaptureSession，CanTransition 判定）与调用点 :404。
2. Preparing→Selecting：CaptureService.cs:187（MarkCapturePrepared）与调用点 :409（overlay.Prepare 成功之后）。
3. Selecting→Capturing：CaptureService.cs:193（BeginCaptureAttempt）与三处调用 CaptureOverlayWindow.Capture.cs:472（OCR）、:575（确认）、:718（另存）。
4. Capturing→Committing：CaptureService.cs:199（MarkCaptureAttemptSucceeded）与三处调用 :494 / :616 / :747。
5. Capturing→Selecting（失败可重试）：CaptureService.cs:230（MarkCaptureAttemptFailed）与三处调用 :485 / :607 / :738；另存取消/异常回 Selecting 在 :250 与调用 :766 / :799。
6. 活动态→终态（Canceled/Completed/Failed）：CaptureService.cs:97-135（EndCaptureSession，TerminateDecision + TerminalStateFor），入口全是 Dismiss/Finish：Overlay :250/:255（cancel/finish），:621（confirm-success→Completed），:680（confirm-end→Failed）。reason=cancel 示例链：Overlay Cancel :150 → Dismiss(session,cancel) :157 → FinishCaptureSession → EndCaptureSession → Canceled。

### 2.4 行为保持证据

- git diff 全文复核：无删除既有日志、无删改 Dismiss/FinishCaptureSession/Cancel 分支、无改任何 toast/剪贴板/历史/保存语句；改动的只有类型字段、Abort 失败标注、状态日志与 StartCaptureAsync 两行调用。
- 五条旧关键字定位：CaptureService.cs:89/109/146/359；CaptureOverlayWindow.Capture.cs:203/237/249。
- git diff --stat 仅两文件：CaptureService.cs 236 行、CaptureOverlayWindow.Capture.cs 111 行。其他工作树改动均属并发单（P-13b 的 NativePinWindow.*、B-3a 的 WgcWindowCapture.cs、Record/plans 与他人报告），本单未触碰。

## 3. 进行中（交接用）

- 已完成：代码 + 三门禁 + 本报告。task-23 完成时 expected_revision=2。
- 下一步第一件事：P-14c 建议——把 CaptureResult（元数据）与 SelectionCapture 的 Bitmap 合成一个提交结果，让 Clipboard/History/Toast 分支统一按 result.Failure/Notice 决策，而不是每个出口自己 if。
- 下一步该读的文件：src/PrtScr.App/Services/CaptureService.cs（:97-151 EndCaptureSession、:157-304 状态 API）、src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs（:301 CaptureSelectionAsync、:466 OCR、:547 确认、:711 另存）、src/PrtScr.Core/CaptureSessionState.cs（规则与迁移真源）、src/PrtScr.Core/CaptureResult.cs。
- 提交注意：本单只 add 两个 cs + 本报告，其他并发改动不要带进 commit。

## 4. 风险与遗留

1. GUI 交互未实测（禁 --smoke，且会写主人真实 data/）：状态机正确性只有编译 + Core 206 条规则测试 + 代码走查支撑；主人实测步骤见第 6 节。
2. 日志量增加：每次取景新增 begin/prepared/每次 attempt/每次 result，菜单级 OCR/另存也各有 attempt。app.log 单次取景约 +6~10 行；日志轮转/上限不在本单范围。
3. 状态槽回 Idle 是设计选择：若未来要在终态上查询「上一次结果」，需改为保留 LastResult；本单按设计稿终态无出边处理。
4. 取消与收尾的竞态仍靠 _sessionGate + 两条日志：Invalidate 记 Canceled 后若会话被新取景替换，End 会走 finish while slot=/finish ignored，属预期路径，不会写错终态。
5. 结果日志的 HResult 固定为 0：App 层没有把 WindowCapture 的 WgcHResult 传到 CaptureResult；若后续排障需要，应在 SelectionCapture 增加 HResult 并在成功/失败两处带入。
6. 另存成功路径：MarkCaptureAttemptSucceeded 先于文件写入（:747），若后续磁盘写入失败，状态仍是 Committing→Dismiss(save)→Completed；与 HEAD 的提示语义一致，但结果日志不能说「文件已写成功」。
7. 未新增自动化测试：Core 规则测试已覆盖矩阵与迁移（206 条），App 层 WinUI 路径无法在 Core.Tests 覆盖；总数未减少。
8. 他人并发改动在同一工作树（P-13b/B-3a），构建通过含其代码；若其回退，需重跑本单门禁。

## 5. 待确认

1. 结果日志是否要写入固定前缀（如 CAPRESULT）方便后续日志解析？当前用 Capture: session ... result ...，已可按 result state= grep。
2. 第 12 行矩阵（OCR/另存）已在 CaptureRules.ExpectationFor 保留为 OcrOrSave 单行（P-14a 交付），本单在日志层用 reason=ocr|save 区分；若 lead 要求断言层拆两行，归 P-14c。
3. 是否需要把 CaptureResult 暴露给 History/剪贴板调用方（当前仍在 CaptureService 内部构建并写日志）。

## 6. 主人实测步骤

1. 启动 IshWY，按 F1 框选任意区域后 Enter：应正常截图并提示；app.log 中同一 session 依次出现 state Idle -> Preparing (begin)、Preparing -> Selecting (prepared)、Selecting -> Capturing (attempt:confirm)、Capturing -> Committing (attempt-success)、state Committing -> Completed (terminate:confirm-success) 与一行 result ... reason=confirm-success。
2. 按 F1 后直接 Esc：不应有剪贴板/历史写入；日志出现 state ... -> Canceled (terminate:cancel) 与 result ... canceled=True reason=cancel，且 finished reason=cancel 仍存在。
3. 截一个窗口（窗口模式）被遮挡也能抓：日志 result 行 origin=Window engine=Wgc 或 PrintWindow；若窗口响应慢改用 WGC，行内 timedOut=True 且界面仍提示「窗口响应较慢」。
4. 对同一窗口在 2 秒内快速连续两次触发：第二次应提示「该窗口上一张截图还在处理中，请稍后重试」，取景窗保留可直接重试；日志出现 result ... failure=Busy 与 state Capturing -> Selecting (attempt-failed:Busy)，随后重试可成功提交。
5. 按 F1 后按 O 做文字识别、或 Ctrl+S 另存：均正常完成；日志 result 行 reason=ocr 或 reason=save，state 终态为 Completed；另存时在文件对话框点取消，取景窗保持不关、日志 state Committing -> Selecting (save-dialog-canceled)。


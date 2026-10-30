# P-14b' 设计：快捷全屏并入 CaptureSession 会话模型

> 基线：HEAD 69616cc + 工作树中 P-14b 未提交改动（task-23）。本文行号取自 2026-10-30 22:2x 的工作树；task-26/task-29 若再改 CaptureService.cs，实施前须按 grep 复核行号。
> 范围：只做设计，不改源码、不改热键、不实现。

## 1. 现状：快捷全屏的完整调用链

### 1.1 三个入口

| 入口 | 文件:行 | 说明 |
| --- | --- | --- |
| 全屏热键（F2 默认） | src/PrtScr.App/MainWindow.Hotkeys.cs:108 | 低级键盘钩子命中后 DispatcherQueue.TryEnqueue(OnFullScreenPressed) |
| RegisterHotKey 兜底 | src/PrtScr.App/MainWindow.Hotkeys.cs:110-119 | 先去重（TryClaimFullScreenHotkey :112）、请求重装钩子（:117），再入队 |
| 截图热键自动改道 | src/PrtScr.App/MainWindow.Capture.cs:142-149，:151-156 | 前台全屏游戏或 quick 档位时，OnHotkeyPressed 直接调 OnFullScreenPressed |
| 延时截图到点 | src/PrtScr.App/MainWindow.Capture.cs:192-197 | IsQuickProfile 时同样调 OnFullScreenPressed |

钩子到入口的链路：EscOverPinHook.EvaluateFullScreenHotkey（src/PrtScr.App/Services/EscOverPinHook.cs:511-519，TryClaim 在 :515、RaiseDeferred 在 :518）→ FullScreenPressed 订阅（MainWindow.Hotkeys.cs:108）→ UI 队列。

### 1.2 完整链（10 个环节，全部带 文件:行）

| # | 环节 | 文件:行 | 与取景链的关系 |
| --- | --- | --- | --- |
| 1 | 钩子判定 + 去抖 | src/PrtScr.App/Services/EscOverPinHook.cs:511-519 | 共用（F1 与 F2 同一钩子） |
| 2 | 投递到 UI 线程 | src/PrtScr.App/MainWindow.Hotkeys.cs:108；兜底 :110-119 | 共用投递方式 |
| 3 | 入口互斥 _quickCaptureBusy | src/PrtScr.App/MainWindow.Capture.cs:26-30（字段 :171） | 取景链不查此字段 |
| 4 | 首轮抓屏 | src/PrtScr.App/Services/CaptureService.cs:599-633（取光标显示器 :603、保证池 :607、CaptureMonitorBestAsync :608） | 与冻结帧共用 CaptureMonitorBestAsync :433-528 |
| 5 | 黑屏/无帧重试 + 抢前台 | src/PrtScr.App/MainWindow.Capture.cs:39-45；FullscreenBypass.BeginAsync src/PrtScr.App/Services/FullscreenBypass.cs:105，等待 60/60/500ms :160/:166/:174 | 取景链没有此步骤 |
| 6 | 失败分支 | src/PrtScr.App/MainWindow.Capture.cs:49-63（文案 :51、:59） | 取景链走 CaptureOutcome/Toast/恢复取景 |
| 7 | 编码 PNG | src/PrtScr.App/Services/CaptureService.cs:618-621 | 取景链 EncodePng src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:684 |
| 8 | 剪贴板 + 历史 | src/PrtScr.App/MainWindow.Capture.cs:65-68（SetImageAsync :65；AddAsync(..., Normal) :66-68） | 取景链在 CaptureOverlayWindow.Capture.cs 的 ConfirmAsync 内，且带 A05 校验与 Mode/ProcessName |
| 9 | 用户提示 | src/PrtScr.App/MainWindow.Capture.cs:70-74（SetStatus/Toast） | 取景链 Toast 文案/降级提示不同 |
| 10 | 收尾日志 + 释放互斥 | src/PrtScr.App/MainWindow.Capture.cs:75-77（完成日志）、:84-88（bypass.Dispose + Volatile.Write busy=0） | 取景链走 FinishCaptureSession → EndCaptureSession 漏斗 |

补充事实：快捷全屏**不经过取景窗**，src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs 内没有任何 Quick/全屏分支（grep Quick|FullScreen 仅命中 CropFullScreenSelection :449 等无关项）。任务输入里把它列为「QuickCapture 现有路径」不成立；本设计只需处理它与取景会话的互斥，不需改取景窗。

### 1.3 与取景链重复 / 缺失对照

| 能力 | 快捷全屏现状 | 取景链现状 | 判断 |
| --- | --- | --- | --- |
| 采集引擎顺序 | 共用 CaptureMonitorBestAsync（:433-528） | 同 | 已共用，无需改 |
| 光标绘制 | 共用 DrawCursorIfEnabled（:617） | 同 | 已共用 |
| 会话身份 | 无 | Guid + A05 校验 | 缺失（本单要补） |
| 来源/引擎元数据 | 无（历史固定 Normal :66-67） | Origin/Mode/ProcessName（P-04） | 缺失 |
| 收尾漏斗 | 无（只有 _quickCaptureBusy 复位） | FinishCaptureSession → EndCaptureSession（P-04b/P-14b） | 缺失 |
| 失败分类 | 只有文案 :51/:59 + 日志 | CaptureFailure + 恢复取景 | 缺失 |
| 结果日志 | 1 条完成日志 :75 | state/result 行（P-14b） | 缺失 |
| 剪贴板/历史失败语义 | 提示但不算失败 | 同（矩阵第 10/11 行 Completed） | 已一致 |
| 互斥 | _quickCaptureBusy | _isCapturing | **互不相识**（见 §2） |

## 2. 冲突点：_quickCaptureBusy 与 _isCapturing

### 2.1 两个字段的所有权

| 字段 | 定义 | 置位 | 复位 | 保护范围 |
| --- | --- | --- | --- | --- |
| _quickCaptureBusy | src/PrtScr.App/MainWindow.Hotkeys.cs:171 | Interlocked.Exchange :26 | Volatile.Write :87 | 只防 F2 重入 |
| _isCapturing | src/PrtScr.App/Services/CaptureService.cs:23 | StartCaptureAsync :402；唯一 false 在 EndCaptureSession :103-104 | 同左 | 只防取景重入/取景热键取消 |

结论：两条路径各用一把锁，互不检查。F1 在 _quickCaptureBusy=1 时照常开取景；F2 在 _isCapturing=true 时照常抓屏。

### 2.2 交错时序表（现状，7 行）

| # | 时序（先→后） | 现状结果 | 关键日志 | 风险 |
| --- | --- | --- | --- | --- |
| 1 | 取景窗已开（Selecting）→ 按 F2 | 快捷全屏照抓，画面含取景窗的暗色 UI；写完剪贴板/历史后取景窗仍在 | Capture: overlay shown session=…；QuickCapture: hotkey pressed；QuickCapture: 完成… | 截到取景器自身；两条历史 |
| 2 | F2 抓屏在飞（busy=1）→ 按 F1 | StartCaptureAsync 不查 busy，照开取景；若 F2 正在抢前台，取景窗 Activate 可能落在 helper 窗口后面 | QuickCapture: hotkey pressed；FullscreenBypass: helper=… activated=True；Capture: overlay shown | 前台抢焦点竞争，取景窗可能不激活 |
| 3 | F1 取景中 → 再按 F1 → 再按 F2（几十 ms 内） | 第二次 F1 走 already capturing 取消取景；F2 另起一条抓屏，取消漏斗清 _isCapturing 与 F2 无关 | Capture: already capturing, cancel session=…；Overlay: session … finished reason=cancel | 取消与新截图并发写剪贴板 |
| 4 | F2 首帧失败 → bypass 等待 60+60+500ms → 再抓 | 期间按 F1：取景窗在 bypass 等待期启动，可能被 helper 抢前台；第二次抓屏可能带取景器 | QuickCapture: 首帧不可用…；FullscreenBypass: … activated=True；Capture: overlay shown | 全屏图可能包含取景器 |
| 5 | 取景确认刚结束（EndCaptureSession → NotifyOverlayClosed）→ 60ms 内按 F2 | WGC 池把新会话的前几帧判为取景器残影（IsOverlayResidue，60ms 窗口），可能等下一帧或重建池 | Capture: session … finished；WgcWindowCapture 池重建日志 | 偶发首帧变慢/回退 BitBlt |
| 6 | F2 写历史/剪贴板尚未完成（busy 仍 1）→ F1 确认取景并复制 | 两条写路径无会话校验；谁后写谁覆盖剪贴板，历史顺序可能与操作顺序相反 | QuickCapture: 完成…；Capture: copied | 主人拿到错图（最后一次写赢） |
| 7 | F2 异常/失败 → finally 复位 busy → F1 立刻开始 | 正常，但失败没有会话/失败分类记录，只剩一行「QuickCapture: 失败」 | QuickCapture: 失败，无图像 | 复盘缺来源与失败原因 |

### 2.3 目标不变量（并入后必须成立）

| 编号 | 不变量 | 验证方式 |
| --- | --- | --- |
| I1 | 任一时刻最多一个截图面持有会话槽（_isCapturing）；F2 与 F1 共用同一把逻辑锁 | grep _isCapturing 赋值 + 交错测试/日志 |
| I2 | 每张成功图都有一条 CaptureResult 日志（origin/engine/failure/session） | 日志行数 = 成功次数 |
| I3 | 会话失效后的晚到写入被丢弃（A05） | 手动：F2 中按 F1 取消，观察无剪贴板覆盖 |
| I4 | 忙时重复触发只记日志、不排队 | QuickCapture: ignored 类日志 |
| I5 | F1 的既有语义（取景中再按取消）不变 | 五条保护日志不变 |
| I6 | 热键按下到首帧采集开始的额外延迟 ≤ 5ms（仅日志/锁，不新增 await 与线程跳转） | Stopwatch 日志对比 |

## 3. 方案取舍

### 3.1 方案 A：快捷全屏也发 session，复用同一漏斗（推荐）

做法：

1. CaptureService 新增 `StartQuickCaptureAsync(QuickCaptureTrigger trigger)`：内部完成「占会话槽 → BeginCaptureSession → MarkCapturePrepared（立即进 Selecting）→ BeginCaptureAttempt(quick) → 采集（含失败重试/bypass）→ 编码 → MarkCaptureAttemptSucceeded(Origin.Normal, engine, false) → 剪贴板/历史 → EndCaptureSession(session, reason, surface: Quick)」。
2. 会话槽复用 `_isCapturing` + `_captureSessionId`：占槽失败即 Ignored，保留现有「忽略并记日志」语义；删掉 _quickCaptureBusy。
3. EndCaptureSession 增加 surface 参数（Overlay/Quick）：状态机、result 日志、_isCapturing 清理共用；Quick 跳过 Hotkey.EndCaptureMode 与 WgcWindowCapture.NotifyOverlayClosed（这两步只属于取景窗，见 CaptureService.cs:144-146）。
4. 新增 EndReason：QuickSuccess / QuickFailed，EndReasonText 输出 quick-success / quick-failed，终态分别 Completed / Failed；不走 TerminateDecision 的新分支。
5. CaptureMonitorBestAsync 返回值增加 CaptureEngine（现有只在日志里写 engine=，:449/:463/:488/:510/:520）；快捷全屏 Origin 固定 Normal，前景窗口 WGC 也记 Wgc（detail 里注明 foreground）。
6. MainWindow 只留 UI：读 outcome，SetStatus/Toast；重试/bypass/编码/写入全部下沉到 service。

改动文件与预估行数：

| 文件 | 改动 | 预估 |
| --- | --- | --- |
| src/PrtScr.App/Services/CaptureService.cs | StartQuickCaptureAsync + 占槽 + surface 参数 + engine 返回 + 结果映射 | +150 / -45 |
| src/PrtScr.App/MainWindow.Capture.cs | OnFullScreenPressed 改为调 service + 提示；删内联采集/编码/写入 | +12 / -70 |
| src/PrtScr.App/MainWindow.Hotkeys.cs | 删 _quickCaptureBusy 字段（:171） | +0 / -3 |
| src/PrtScr.Core/CaptureSessionState.cs | CaptureEndReason 加 2 值、EndReasonText 加 2 串 | +10 / -0 |
| src/PrtScr.Core.Tests/CaptureRulesTests.cs | 新增 reason 串/终态/非法组合用例 | +25 / -0 |
| 合计 | 5 个文件 | 约 +197 / -118 |

风险：

- 直接改 P-04b 的「唯一漏斗」与 P-14b 的 EndCaptureSession 签名，评审面变大；缓解=surface 默认值 Overlay、五条保护日志与既有行为逐字不动，added 日志只追加。
- EndReason 新增值需同步 Core 测试与 P-14b 文档；不影响既有 6 个 reason 串。
- 快捷全屏失败需要新的终态映射（LikelyBlack→Unavailable 还是 Failed？见 §5 第 1 条）。

热键延迟：占槽与 Begin/Prepared/Attempt 只有 lock 与 3-4 行日志，无 await、无线程跳转；首帧采集入口与现在同一个调用点，预估 +1-3ms（日志写盘），远小于 bypass 的 500ms。

### 3.2 方案 B：独立 QuickCaptureSession 子类型

做法：新建 App 层 QuickCaptureSession 类（Id/State/Engine/Origin/Result），CaptureService 增加独立的 `_quickSession` 字段与 TryBeginQuick/EndQuick；不扩展现有漏斗签名；与取景槽通过显式交叉判断互斥（quick 入口查 _isCapturing，StartCaptureAsync 查 _quickSession 非空）。为可测试，把纯状态/文案抽成 Core 的 QuickCaptureRules。

改动文件与预估行数：

| 文件 | 改动 | 预估 |
| --- | --- | --- |
| src/PrtScr.App/Services/QuickCaptureSession.cs（新） | 子类型 + 日志格式 | +150 / -0 |
| src/PrtScr.App/Services/CaptureService.cs | 独立槽 + 交叉判断 + engine 返回 | +40 / -10 |
| src/PrtScr.App/MainWindow.Capture.cs | 同 A 的瘦身 | +10 / -70 |
| src/PrtScr.App/MainWindow.Hotkeys.cs | 删字段 | +0 / -3 |
| src/PrtScr.Core/QuickCaptureRules.cs（新） | 纯状态/文案（供测试） | +60 / -0 |
| src/PrtScr.Core.Tests/QuickCaptureRulesTests.cs（新） | 独立测试 | +40 / -0 |
| 合计 | 6 个文件（2 新） | 约 +300 / -83 |

风险：两套会话模型长期并存，result 日志格式与失败分类易分叉；仍需交叉互斥，未减少并发面；后续并回 A 要二次迁移。优点是不动 P-04b/P-14b 的漏斗与签名，回退面小。

### 3.3 推荐

**推荐 A**。理由：

1. 本单目标就是「每张图都有来源与身份」；A 让快捷全屏与取景共用同一个 CaptureResult/日志/失败分类，B 会留下第二套。
2. A05 的会话校验只在一个地方实现；B 需要两处，且未来 P-14c 的 F1 修复（stale finish）也要考虑 B 的槽。
3. A 的并发面收敛为「一个槽」，直接满足 I1；B 的交叉判断天然容易漏（F2 入口、StartCaptureAsync、延迟入口、profile 入口四处都要加）。
4. A 的热键延迟增量可忽略；B 反而多一次对象分配与自定义状态判断。
5. 代价是评审面大，可用 surface 默认值 + 五条保护日志冻结 + 独立复核单来控制。

若 lead 认为 A 的漏斗改动风险不可接受，B 可作为过渡：但需在 P-14c 前明确「B 是临时态」，否则两套模型会长期并存。

## 4. 迁移步骤（3 步）

### 步骤 1：Core 与采集引擎元数据（零行为变化）

- 改 src/PrtScr.Core/CaptureSessionState.cs：CaptureEndReason 加 QuickSuccess/QuickFailed，EndReasonText 加 quick-success/quick-failed。
- 改 src/PrtScr.App/Services/CaptureService.cs 的 CaptureMonitorBestAsync 返回 engine（调用方只有 StartCaptureAsync :382 与 CaptureFullScreenPngAsync :608）。
- 判据：build-guard（tag p14bp1，OutDir=.tmp/out-p14bp1/）0 错 0 警；dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug 全过且总数 ≥206+5；dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore rc=0；grep quick-success src/PrtScr.Core 命中 1 处。
- 回退点：只 revert 上述 2 个源文件 + 1 个测试文件，不动 App 行为。

### 步骤 2：快捷全屏接会话（核心改动）

- CaptureService 新增 StartQuickCaptureAsync/占槽/surface 参数；MainWindow.Capture.cs 改调并删内联逻辑；MainWindow.Hotkeys.cs 删 _quickCaptureBusy。
- 判据：同上门禁（tag p14bp2）；grep _quickCaptureBusy src/PrtScr.App 为 0；grep StartQuickCaptureAsync 至少 2 处（定义+调用）；五条保护日志（invalidated / finish ignored / finished reason= / already capturing, cancel session= / dismiss while hidden）与 HEAD 逐字对比不变；Core.Tests ≥211。
- 回退点：git checkout 步骤 2 触及的 3 个 App 文件，回到步骤 1 状态；步骤 1 的 Core 增量可保留（未被引用）。

### 步骤 3：收口与回归

- 删除或变薄旧 CaptureFullScreenPngAsync（:599-633）；更新 Record/plans/B1-capture-session-design.md 与 P-14b/P-04b 报告中「漏斗只服务取景」的表述；补 F2/取景交错的手动验收记录。
- 判据：同上门禁（tag p14bp3）；grep CaptureFullScreenPngAsync 只剩 0 或 1 处（薄包装）；Core.Tests ≥211；format rc=0；app.log 中 F2 成功一条 completed 结果行、失败一条 failed 结果行。
- 回退点：revert 步骤 3 的清理提交即可恢复步骤 2 行为（无数据迁移，历史格式不变）。

## 5. 未决（5 条，含推荐答案）

| # | 问题 | 推荐答案 | 理由 |
| --- | --- | --- | --- |
| 1 | 快捷全屏失败要不要在历史元数据留痕？ | 不留历史条目，只写日志与 CaptureResult failure；如确需可观测，只在 app.log 写 failed 结果行 | 历史 JSON 没有 failed 状态字段，新增会破坏旧版本读取；失败图没有内容可存 |
| 2 | 退出无人值守时快捷全屏在飞如何收尾？ | App 退出钩子里 best-effort 调 End(cancel)，最多等 300ms；不等待采集线程 | 进程退出会回收 GDI/线程，阻塞退出比丢一条日志更糟；取消语义与 §4.1 一致 |
| 3 | 取景中按 F2、F2 中按 F1 谁优先？ | 取景优先：取景中 F2 直接忽略并提示「取景中，已忽略全屏快捷键」；F2 在飞时 F1 取消该 quick 会话（Invalidate + End(cancel)）后立刻开取景 | 取景是主交互路径；复用 A05 的失效机制即可，不引入排队 |
| 4 | 快捷全屏 Origin 用 Normal 还是新增 Monitor？ | 用 Normal | lead 已裁决 Origin 只有 Window/Normal/Screen；显示器整屏与常规截图历史同类，无需新枚举 |
| 5 | 剪贴板/历史失败时 result 记 Completed 还是 Failed？ | Completed（failure=None），日志带 copied/history 字段；用户提示保持现状 | 与 P-14b 矩阵第 10/11 行一致：图像已成功，辅助写入失败不改变截图结果 |

## 附：与 P-14b 接口的对应关系

| 现有接口 | 位置 | A 方案中的用法 |
| --- | --- | --- |
| BeginCaptureSession | src/PrtScr.App/Services/CaptureService.cs:158 | quick 发号后调用 |
| MarkCapturePrepared | 同 :188 | quick 立即调用（无选区阶段） |
| BeginCaptureAttempt | 同 :194 | trigger=quick |
| MarkCaptureAttemptSucceeded | 同 :200 | Origin.Normal + engine + PrintWindowTimedOut=false |
| MarkCaptureAttemptFailed | 同 :230 | 无帧/黑屏/异常时调用 |
| EndCaptureSession | 同 :98 | 增加 surface；Quick 跳过 :144-146 的取景专属清理 |
| FinishCaptureSession | src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:199 | 取景专用，quick 不经过 |

设计稿结束。实施单建议拆两张：P-14b'-1（步骤 1+2）与 P-14b'-2（步骤 3 + 手动验收），避免一次评审面过大。


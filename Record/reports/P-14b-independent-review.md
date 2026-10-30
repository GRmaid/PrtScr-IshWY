# P-14b 独立复核（task-27）

- 复核对象：P-14b（task-23）工作树改动——src/PrtScr.App/Services/CaptureService.cs、src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs（HEAD 69616cc，未提交）。
- 复核方式：只读源码 + git show/git diff 机械对比 + 独立跑门禁；未改任何源码，未跑 --smoke。
- 声明：复核人与实现人为同一成员（lead 指派）。为降低自证偏差，本报告所有结论均给出可复现命令与文件:行，并以 HEAD 原文做两侧逐字对比；建议 lead 再抽 1 项人工抽查（推荐清单 2 与 6）。

## 1. 结论

**可放行，0 项阻断返工。** 六项清单全部通过（2 项附观察、1 项潜在问题需 P-14c 处理）。

- 阻断返工：无。
- 非阻断建议：F1（旧会话晚到收尾可拆掉新会话，P-14b 前后行为一致，属既有缺陷）、F2（Canceled 结果在 token 接线后会记成 Failed/None，当前不可达）、F3（两处不经 CanTransition 的裸赋值，已有注释与日志）、F4（一条非保护日志文本变化）、F5/F6（防御性不可达分支、两条未使用迁移边，均为信息项）。
- 建议返工清单：无。建议新增一张 P-14c 单：会话收尾守卫（F1）+ Canceled 结果落分类（F2）。

## 2. 六项逐条

### 2.1 清单 1：_isCapturing 唯一赋值 —— 通过

grep -rn "_isCapturing" src/PrtScr.App --include=*.cs 全量结果：

| 文件:行 | 内容 | 性质 |
| --- | --- | --- |
| CaptureService.cs:22 | private bool _isCapturing; | 字段 |
| CaptureService.cs:41 | public bool IsCapturing => _isCapturing; | 只读 |
| CaptureService.cs:102 | wasCapturing = _isCapturing; | 读 |
| CaptureService.cs:103 | _isCapturing = false; | **唯一 false 赋值** |
| CaptureService.cs:355 | if (_isCapturing) | already capturing 分支读 |
| CaptureService.cs:401 | _isCapturing = true; | **唯一 true 赋值（HEAD 为 :169）** |
| CaptureOverlayWindow.Capture.cs:197/243/581 | 注释 | 无赋值 |

结论：false 赋值仅 :103、true 赋值仅 :401，均在 CaptureService.cs；取景窗无任何赋值。满足验收 4。

### 2.2 清单 2：五条既有日志关键字逐字未改 —— 通过

方法：分别取 git show HEAD:<file> 与工作树中包含关键字的行，去掉行号与缩进后 diff。结果：两个文件各 4 行 / 3 行，**IDENTICAL（逐字相同）**。

| 关键字 | HEAD 位置 | 工作树位置 | 对比结果 |
| --- | --- | --- | --- |
| Capture: session {id} invalidated | CaptureService.cs:66 | :89 | 相同 |
| Capture: session {t} finish ignored (not capturing, reason={r}) | CaptureService.cs:86 | :109 | 相同 |
| Capture: session {t} finished reason={r} | CaptureService.cs:93 | :146 | 相同 |
| Capture: already capturing, cancel session={t} | CaptureService.cs:127 | :359 | 相同 |
| Overlay: session {t} finish ignored (already finished, reason={r}) | Overlay:201 | :203 | 相同 |
| Overlay: session {t} finished reason={r} | Overlay:235 | :237 | 相同 |
| Overlay: session {t} dismiss while hidden session={t} reason={r} | Overlay:247 | :249 | 相同 |

只有行号位移，字符串与插值表达式完全一致。P-14b 新增日志均以独立 App.Log 追加，不替换上述行。

### 2.3 清单 3：EndCaptureSession 幂等 —— 通过

代码路径（CaptureService.cs:97-151）：

1. 第一次调用：lock 内读 wasCapturing=true 并置 false（:102-103）→ 写终态/结果日志（:117-135）→ Hotkey.EndCaptureMode + NotifyOverlayClosed（:143-145）→ finished reason= 与追加日志（:146-150）。
2. 第二次调用：lock 内 wasCapturing=false，**在 :107-111 早退**；早退位置在终态处理（:115 起）与 EndCaptureMode（:143）之前，因此不会重复清状态、不会重复 EndCaptureMode、不会重复 NotifyOverlayClosed，只多一条 finish ignored 日志。
3. 并发两次调用：清标志在 _sessionGate 内（:100-104），仅一个线程能看到 true，另一个按第 2 条早退；调用方均为 UI 线程（overlay Dismiss/Finish、StartCaptureAsync catch），实际串行。
4. 幂等语义与设计稿一致：终态重复收尾本应由 TerminateDecision 返回 Duplicate，但被 wasCapturing 提前拦截，故 Duplicate/Invalid 分支当前不可达（F5），属防御性代码。

### 2.4 清单 4：状态迁移是否全部经 CanTransition —— 通过（2 处受控例外）

grep -n "_captureState" CaptureService.cs 全部赋值点：

| 行 | 赋值 | 是否经规则 |
| --- | --- | --- |
| :30 | 字段初始化 Idle | 初始化 |
| :125 | _captureState = terminal | 经 CaptureRules.TerminateDecision 判定（:121-122） |
| :135 | _captureState = Idle | **裸赋值**：槽位释放，不是迁移边（:134 注释） |
| :172 | = Preparing | 经 CanTransition（:170） |
| :178 | = Preparing | **裸赋值**：异常兜底 forced，日志带 forced（:179） |
| :315 | = to | 经 CanTransition（:313） |

TransitionSession（:304-326）对拒绝分支只写 state ... rejected (...) 日志（:320），不改状态、不抛异常；调用方（:189/:195/:216/:246/:252）均无异常处理需求。
结论：正常阶段推进全部经 CanTransition；两处裸赋值分别是「终态后释放槽位」与「异常兜底」，都有注释与日志，不构成绕过。F3 建议 P-14c 收口成 ReleaseSessionSlot() 以便审计。
另注（F6）：CanTransition 定义 13 条边，其中 Preparing→Canceled / Preparing→Failed 目前无调用者（收尾统一走 TerminateDecision）；这是规则比调用更宽，不是缺陷。

### 2.5 清单 5：用户可见行为零变化 —— 通过

方法：git diff -U0 取两个文件全部删除行与新增行，逐类归属。

删除行（27 行）全部属于：CaptureContentOrigin 枚举删除、SelectionCapture/CaptureOutcome 签名调整、Abort 旧签名、6 处来源常量改名、1 处 sessionText 抽取、1 条日志文本（见 F4）。**没有一行**是 Toast/Clipboard/History/FileSave/Hotkey/Win32 调用或控制流语句。

新增行（除 App.Log/注释/空行外）全部属于：会话槽字段与只读属性、Invalidate 的 Canceled 标注、End/状态推进 API、SessionText/ParseEndReason/TerminalStateFor/BuildResultLocked/FormatResultLog/TransitionSession、StartCaptureAsync 两行调用；Overlay 侧为 SelectionCapture/CaptureOutcome/Abort 签名、来源构造参数、MapEngine、三出口的 Begin/Mark/Reset 调用。**没有新增或修改任何用户可见调用**。

| 用户可见面 | HEAD 与工作树差异 | 证据 |
| --- | --- | --- |
| Toast 文案/时机 | 无 | 删除/新增行均无 ToastService 调用变化 |
| 剪贴板写入 | 无 | ClipboardService 调用行未在 diff 中出现 |
| 历史写入/分类 | 无 | CaptureHistoryService.AddAsync 调用行未变（仍 selection.Mode/ProcessName/WindowTitle） |
| 另存文件对话框/保存 | 无 | FileSaveService.SavePngAsync 调用行未变 |
| 热键/Esc 注册 | 无 | 删除/新增行无 HotkeyService/RegisterHotKey/EscOverPinHook |
| Win32 调用 | 无 | 无新增 PInvoke/SetWindowPos/ShowWindow 等 |

F4 例外说明：Overlay :624-626 的日志由 Capture: 本次来源=窗口 process=... 变为 Capture: 本次来源={Origin}（窗口场景输出 Window 而非「窗口」）。属 app.log 文本、非用户可见，且不在五条保护关键字内；若 lead 要求日志也冻结，可改回按 Origin 分支输出中文，非阻断。

### 2.6 清单 6：失败分类是否误判 Canceled/Failed —— 通过（1 项潜在 F2）

| 路径 | 代码 | 分类是否正确 |
| --- | --- | --- |
| 取消（Esc/F1 打断） | Invalidate :84-86 置 failure=Canceled → End(reason=cancel) 终态 Canceled | 正确：只对活动态且槽匹配的会话覆盖 |
| Busy/Timeout/失败 | Overlay 三出口先 MarkCaptureAttemptFailed(:607/:485/:738) 再恢复取景；恢复失败则 End(confirm-end) 终态 Failed，result failure=Busy/Timeout | 正确：不把可重试失败误报成取消 |
| 取图返回 Canceled | Overlay :600-604 直接 return，不记失败；finally 可能 End(confirm-end) | **潜在 F2**：若将来传 CancellationToken，会出现终态 Failed + result failure=None/canceled=False。当前 App 未传 token（P-07b 已记录），路径不可达 |
| 成功提交 | MarkSucceeded 置 failure=None → End(confirm-success/ocr/save) 终态 Completed | 正确 |
| 失败后用户 Esc | Invalidate 覆盖 failure=Canceled → 终态 Canceled | 正确：用户主动结束，取消优先 |

Abort 映射核对：Canceled 分支均带 CaptureFailure.Canceled（Overlay:323/:366/:386/:429-443 的 ToAbort；:308/:392 的 NoSelection 为 InvalidTarget），无 Abort 不带 Failure 的调用。

## 3. 观察与建议（不阻断）

### F1（建议 P-14c，既有缺陷，非 P-14b 回归）：晚到的旧会话收尾会拆掉新会话

链：旧 ConfirmAsync 在取图 await 期间被 Esc 取消 → Cancel 收尾；主人立刻再按 F1 → 新会话 Prepare（overlay 的 _sessionFinished 被 ResetSessionState 复位）→ 旧取图晚到返回，:591 判定会话失效后 return，但 finally :679-680 仍调用 FinishCaptureSession(旧)；该函数无会话守卫 → 释放新会话的 _full、EndCaptureSession(旧) 在 :102-104 无条件清 _isCapturing、:143-145 注销新会话的取景期 Esc。

HEAD 同样在 finally 调 FinishCaptureSession 且 End 无条件清 _isCapturing，故**行为零变化成立**；P-14b 只是新增 finish while slot= 日志（:139）让该现象可观测。建议 P-14c 在 FinishCaptureSession 增加「会话既非当前有效、也非本窗口当前会话」的守卫，或在 EndCaptureSession 的 slot 不匹配分支跳过 _isCapturing 清理（注意 cancel 路径需要保留收尾能力）。

### F2（潜在）：Canceled 结果在 token 接线后会落成 Failed/None

见 2.6 第 3 行。建议 P-14c 增加 MarkCaptureAttemptCanceled(session, detail)（置 failure=Canceled 但不改变活动态），或在 Confirm 的 Canceled 分支调用 MarkCaptureAttemptFailed(session, CaptureFailure.Canceled, detail) 后再由 Cancel 路径收尾；需与 §4.1 取消唯一定义一起评审。

### F5/F6：防御性分支与未使用迁移边

Duplicate/Invalid 终止分支、Preparing→Canceled/Failed 两条边当前无调用者；保留无妨，但建议在 P-14c 接线 token 或「启动期失败」路径时补测试或删除，避免误以为已覆盖。

## 4. 复核人独立门禁（2026-10-30 22:1x）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14brev -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14brev/" | BUILD-GUARD EXIT=0，0 警告 0 错误，15.11s |
| Core 测试 | pwsh ... -Tag p14brevtest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | EXIT=0，失败 0、通过 206、跳过 0、总计 206（85ms） |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | FORMAT_RC=0 |
| 赋值清单 | grep -rn "_isCapturing" src/PrtScr.App --include=*.cs | false 仅 :103、true 仅 :401 |

## 5. 待确认

1. F1 是否单开 P-14c 会话收尾守卫单？（建议开；不加也不影响本单放行，因为 HEAD 行为相同）
2. F4 的日志文本变化（本次来源=Window）是否要求改回中文「窗口」输出？当前只影响 app.log。
3. 本复核与实现同人，是否需 lead 或 maid-check 再做一次抽查签字？


# A 批次 App 层独立复核（task-14）

- 复核对象：A 批次已落地改动（工作树，HEAD 9c81858 + 未提交 diff）：P-04（捕获来源与会话身份）、P-06（钩子回调去阻塞）、P-07/P-07b（PrintWindow 期限 + WGC 详细原因）、P-09a/P-09b（资源释放）。
- 复核方式：只读代码；未改任何源码；未跑 --smoke / GUI / 性能测试（按纪律由主人本人实测）。
- 输入：Record/reports/P-04-capture-source-session.md、P-06-hook-offload.md、P-07b-wgc-detailed.md、P-09a-capture-resources.md、P-09b-captureservice-resources.md；src/PrtScr.App/{Views/CaptureOverlayWindow.Capture.cs, Views/CaptureOverlayWindow.xaml.cs, Services/CaptureService.cs, Services/EscOverPinHook.cs, Services/HotkeyService.cs, Services/EscRouter.cs, MainWindow.Capture.cs, MainWindow.Hotkeys.cs, App.xaml.cs}；src/PrtScr.Capture/WindowCapture.cs；git diff HEAD（P-06 钩子改动、P-04 收尾守卫）。

## 一、结论

**A 批次不可放行：需返工 1 项（P1 阻断，已转 task-18 P-04b / maid-impl），其余四项通过；P2 观察按 lead 裁决不返工。**

- P1（返工已立单 task-18）：**在「确认取图进行中」取消，会让 CaptureService 永久停留 _isCapturing=true，之后 F1 全部空转；同时取景期注册的全局 Esc 永不注销，Esc 被整机吞掉，直到重启应用。**
- 通过：会话闸门（内容写入）、双重释放（frozen/_full 单 owner）、钩子语义（P-06 投递化未破坏去抖/吞键/顺序）、Wgc* 失效字段（属「已就绪未接线」而非缺陷）。
- 日志基本可归因，但 P1 这条路径当前没有任何日志能指出「卡在哪」（见五、缺口 G1）。

## 二、逐条清单

### 2.1 会话闸门：每个 await 之后的内容写入都比对 session —— 通过

| await 点（CaptureOverlayWindow.Capture.cs） | 恢复后的写入 | 会话校验 |
|---|---|---|
| :297 / :339 WindowCapture.CaptureDetailedAsync | 选框位图处置 / 后续裁剪 | :298 / :340 CanContinue，晚到 :300 / :342 Dispose |
| :434 OCR 取图 | 返回位图 | :435 CanContinue，晚到 :438 Dispose + 日志 |
| :462 OcrUiService.RecognizeAndShowAsync | 无本地内容写入 | 不适用（OCR 窗自身生命周期） |
| :534 ConfirmAsync 取图 | captured / 剪贴板 / 历史 | :537 IsCurrentSession → :540 Dispose；:573 编码后校验；:590 写入后再校验 |
| :570 Task.Run(EncodePng) | 剪贴板 / 历史 | :573 校验后才写（:579-582） |
| :582 Task.WhenAll（剪贴板+历史） | Toast | :590 再校验才弹提示 |
| :665 SaveSelection 取图 | captured | :666 CanContinue |
| :685 FileSaveService.SavePngAsync | 剪贴板 / 历史 | :689 CanContinue 后才写（:702-705）；:707 再校验才收尾 |
| :705 Task.WhenAll | Toast / Dismiss | :707 再校验 |

结论：**未发现内容写入漏校验**；`_full` 的处置也只在 :563 / :620 两处，且都有会话或幂等守卫。

唯一未加会话判断的共享 UI 写入：**SaveSelectionAsync finally（:732-742）**——`captured.Dispose()`、`_saving=false`、`_keysSuspended=false`，并在 `!_closed` 时 `SetOverlayKeysActive(true)` + `FocusSink.Focus`。旧会话晚归时可能在新会话里重新开启取景按键并抢焦点。属 P-04 报告已主动登记的风险 5，概率低、可自愈，判 **P2 不阻塞本批**。

### 2.2 取消/异常路径的双重释放 —— 通过（两处无崩溃面边界，已归档）

- `_full` 与确认结果不是同一对象：常规来源 CropFullScreenSelection :411-425 用 `_full.Clone(...)`；窗口来源 CropFromWindow :237-272 同样 Clone；整窗成功返回独立 windowBitmap。故 ConfirmAsync :617 `captured.Dispose()` 与 FinishCaptureSession :200 `_full.Dispose()` 不会同对象双放。
- `_full` 单次释放：FinishCaptureSession :192/:194 有 `_sessionFinished` 幂等；Prepare 每会话只交接一次（xaml.cs:226-227），ResetSessionState（xaml.cs:258-293，:262）复位守卫。
- P-09b `frozenHandedOff` 与 P-04 取消路径：CaptureService.cs:151 在 Prepare 返回后置真，:161-167 finally 只放未交接的 frozen；交接后由 overlay 的 FinishCaptureSession 释放。**单 owner 成立**，P-04 的取消不会与 P-09b 重复释放。
- 边界 A（P-09b 风险 1，保留）：Prepare 内 `_full = frozen`（xaml.cs:227）之后、返回之前抛异常（MoveAndResize/UpdateVisuals）→ CaptureService finally 释放 frozen，overlay 仍引用它。Bitmap.Dispose 幂等且窗口未显示，无崩溃面。
- 边界 B（P-09b 风险 3，保留）：`_isCapturing = true` 在 CaptureService.cs:145（窗口枚举 await :137 之后），两个极快 F1 可并发进 StartCaptureAsync；第二个 Prepare 会 Dispose 第一个的 `_full`，第一个 ConfirmAsync 的裁剪可能 ObjectDisposedException，被 :610 catch 成失败 Toast。属既有并发窗口，P-06 投递化未新增（见 2.3）。

### 2.3 钩子语义（P-06 之后是否仍同 HEAD）—— 通过

`git diff HEAD -- src/PrtScr.App/Services/EscOverPinHook.cs` 只含：
- 新增 RaiseDeferred :275-292（ThreadPool + 订阅者异常 → HookLog）；
- 新增 IsHookThread :266-267 / LogDeferred :270；
- 三个 Evaluate* 的 `事件?.Invoke()` 改成 `RaiseDeferred(事件)`（:489-493 / :504-508 / :515-519 区域）。

**未被改动的关键语义**（逐行读过）：TrackModifier 自记账 :354-363；钩子内同步吞键 `if (isDown && TryHandleOverlayKey(vk)) return 1` :551（Esc/Tab/Enter/O 保持顺次、不进线程池，与 P-06 报告 2.3 声明一致）；三个热键 300ms 去抖（TryClaim*）仍在钩子线程完成；RegisterHotKey 兜底与钩子通道共用同一 claim（MainWindow.Hotkeys.cs:36 / :86 / :112）。

顺序结论：
- 同一通道内，钩子线程顺序调用 RaiseDeferred，订阅者只做 `DispatcherQueue.TryEnqueue`（MainWindow.Hotkeys.cs:40/45/59/81/108/118），UI 端按入队顺序执行 ⇒ 取景内 Esc/Tab/Enter 保序（这条本来就不走线程池）。
- 线程池投递在多按场景理论上可乱序，但同一热键两次按键的两种到达顺序，结果都等价于「第二次取消第一次」，未发现新增阻断。
- 跨通道不会被处理两次：300ms claim 在钩子线程先完成合并。

### 2.4 Wgc* 失效字段 —— 通过（记「已就绪未接线」，不是缺陷）

- `grep WgcFailure|WgcHResult|WgcDetail src/PrtScr.App` → **0 命中**。
- 两处窗口调用 CaptureOverlayWindow.Capture.cs:297 / :339 用 `WindowCapture.CaptureDetailedAsync`，**未传 CancellationToken**；CaptureService.cs:280 仍用兼容入口 `CaptureAsync`。
- 因此 P-07b 的 `WgcFailure=Canceled` 归一当前不可达，`WgcHResult/WgcDetail` 也未进入 Toast/日志。属「字段已就绪、消费者未接线」，符合 P-07b 报告声明；转 B 批次接线即可。

### 2.5 日志与可观测 —— 基本通过，3 个缺口

已具备：
- P-04 来源日志：成功时 `Capture: 本次来源=窗口/屏幕 …`（:566-568）；降级原因进 `selection.Notice` 与 Toast（:600-604）。
- P-04 会话日志：发号（CaptureService.cs:153）、取消（:153）、晚到丢弃（:539 / :575 / :592 / :668 / :691 / :709）。
- P-06 钩子日志：命中（HookLog）、订阅者异常（`Hook: 热键订阅者异常 …`）、日志队列满丢弃（无计数）。
- P-09b 释放日志：`Overlay: frozen buffers released, managed=X->YMB`（:216）。

缺口（G1/G2 已转 task-18 P-04b 验收输入，G3 可选）：
- **G1**：`Dismiss()` 在 `_closed` 时直接 return（:230），**无日志**——P1 卡住时日志只显示 “already capturing, cancel”，后续静默。
- **G2**：`Capture: already capturing, cancel`（CaptureService.cs:103）不带 session，无法区分「取消可见取景」与「打在隐藏的确认中」。
- **G3**：HookLog 队列 >128 静默丢弃（EscOverPinHook.cs:294-302），无丢弃计数；极端连按后无法证明日志完整性。

## 三、需 lead 裁决的问题

### Q1（对应 P1，已裁决 m00645）：转 task-18 P-04b 修复

**现象**：确认取图（Enter）期间再按 F1 → 会话被作废，但收尾被跳过 → `_isCapturing` 永久 true；取景期注册的全局 Esc 也随之永不注销。之后 F1 无任何反应（每次只写 “already capturing, cancel”），Esc 在整机范围被吞（HotkeyService.cs:345-346 → MainWindow.Hotkeys.cs:44-45 → CancelCurrent 空转），只能重启应用恢复。

**完整证据链**：
1. 开始取景：CaptureService.cs:145 `_isCapturing=true`；:152 `Hotkey?.BeginCaptureMode()` → HotkeyService.cs:365-368 注册全局 Esc（VK_ESCAPE, MOD_NOREPEAT）。
2. Enter 确认：CaptureOverlayWindow.Capture.cs:533 `HideForCapture()` → :170-172 `_closed=true`（同时 :174 关 overlay keys、:173 停 Esc 轮询）；:534 await 取图（PrintWindow 最长 2s + WGC 回退，或 PNG 前的窗口重建）。
3. 此间再按 F1：CaptureService.cs:101-105 `_isCapturing` → `_overlay?.Cancel()`；Cancel :148-155 先 InvalidateCaptureSession → Dismiss()；**Dismiss :228-234 因 _closed=true 直接 return** → 不 FinishCaptureSession、不触发 Dismissed。
4. 取图返回：ConfirmAsync :537 发现会话失效 → :540 丢图 return；finally :617-620 的收尾条件 `!ended && !keepOverlay && IsCurrentSession(session)` 为假 → **仍不收尾**。
5. `_isCapturing` 全仓只有三处：:145 置真、:83 由 Dismissed 清、:157 catch 清。本路径三处都不触发 ⇒ 永久 busy。
6. `Dismissed` 不触发 ⇒ CaptureService.cs:84 `Hotkey?.EndCaptureMode()` 不执行 ⇒ Esc 注册残留（HotkeyService.cs:375-377 永不调用）。
7. Esc 为何没能救场：overlay 隐藏时 `SetOverlayKeysActive(false)`（:174），钩子不再派发取景键；即使派发，HandleOverlayHotkey :74-75 也会因 `_closed` 早退。所以能触发本缺陷的是 F1/托盘「立即截图」，Esc 此时是空操作。

**回归归属（关键）**：HEAD 的 ConfirmAsync finally 是 `if (!ended) FinishCaptureSession();`（HEAD CaptureOverlayWindow.Capture.cs:346 附近；git diff 中对应 `- if (!ended)`）。HEAD 下同样取消会造成 Dismiss 空操作，但取图返回后 finally **仍会** FinishCaptureSession → Dismissed → 清 busy，属「取消晚一点生效」。P-04 把条件改成 `!ended && !keepOverlay && IsCurrentSession(session)`（工作树 :619-620）以保护新会话，代价是**取消了这条兜底收尾**，于是变成永久卡死。**故判 P-04 回归，不是 HEAD 既有问题。**

**复现（需要慢窗口，或不加 --smoke 的实机）**：选一个响应慢的窗口（或大窗口让窗口重建耗时）→ Enter 确认 → 约 2 秒内再按 F1 → 等画面结束 → 再按 F1，预期无取景窗且日志固定为 “already capturing, cancel”；此时按 Esc，预期系统范围 Esc 失效。

**建议修法（App 层，不在我写域，需你定）**：
- 首选：ConfirmAsync 自己记录「本次是否已 HideForCapture」的局部 owned 标志（或把 session + hidden 状态绑定），finally 只要 owned && !ended 就 FinishCaptureSession，不再依赖全局 IsCurrentSession；对新会话的干扰用「仅当会话仍是自己或自己仍处于 hidden 且未结束」判定。
- 备选：Dismiss() 在 `_closed && !_sessionFinished` 时也执行 FinishCaptureSession（须确认 ConfirmAsync 之后不再碰 `_full`，否则会 ObjectDisposedException）。
- 无论哪种，补一条日志（hidden/closed/session 三态）与一条可回归的检查（取消后 F1 仍可用、Esc 不被吞）。

**裁决结果（lead m00645）**：P1 成立、阻断 A 批次放行，已开 **task-18 P-04b**（maid-impl，P-04 作者）修复：单一收尾漏斗 + ConfirmAsync 局部 owned 标志 + Dismiss 不再因 `_closed` 早退；我的修法方向与之一致，本报告只作复核结论，不参与改 App 层。

### Q2（已裁决）：SaveSelectionAsync finally 的会话判断

lead 裁决：列 P2 已记风险（P-04 报告风险 5），**不返工**；本报告保留记录，供后续 P2 清理时参考。

### Q3：P-06 投递顺序是否需要严格序列号

我判「不需要」：同热键重复按的乱序结果等价；跨通道有 300ms claim 合并。若你要求日志可完整还原按键先后，才需要给 RaiseDeferred 加序号，属 P-06 小改。

### Q4：G3 日志队列丢弃计数

是否值得一行计数器（HookLog 满时累计，随下次成功日志输出）？

## 四、建议后续单

1. **P-04b（task-18，已派 maid-impl）**：ConfirmAsync 收尾所有权 + Dismiss 兜底 + 回归日志；验收含「确认中取消后 F1 恢复可用」「Esc 不再被全局吞」「新会话不被旧 finally 关闭」。
   - 本报告提供的验收输入（日志缺口 G1/G2）：`Dismiss()` 在 `_closed` 早退时补一条带 session / `_closed` / `_sessionFinished` 的日志；`already capturing, cancel` 补 session 号，使「取消打在隐藏确认中」能从日志直接定位。
2. **B 批次接线单**：App 消费 WgcFailure/WgcHResult/WgcDetail（Toast/日志），两处窗口调用透传 CancellationToken，使 P-07b 的 Canceled 归一可达；同时把 CaptureService.cs:280 兼容入口按需要迁到 CaptureDetailedAsync。
3. **P2 清理单**：SaveSelectionAsync finally 会话校验；StartCaptureAsync 提前置 `_isCapturing`（或串行化）消除双 F1 并发窗口。
4. **可观测性小改**：G1/G2/G3（Dismiss 早退日志带 session/_closed/_sessionFinished；already capturing 日志带 session；HookLog 丢弃计数）。

## 五、复核边界（未验证项）

- 未运行 GUI、未跑 --smoke、未做资源/性能实测（按纪律由主人本人执行）。
- 未构造真实慢窗口复现 P1（结论由代码路径与 git diff 推出；HEAD 对照可证明收尾语义变化）。
- 未覆盖 B 批次未落地部分与 src/PrtScr.Core、Ocr 等非 A 批次文件。

## 主人实测步骤

1. **正常确认**：按 F1 出取景 → Enter。预期：正常出 Toast、历史与剪贴板都有图，日志出现 `Capture: 本次来源=…` 与 `Overlay: frozen buffers released`。
2. **正常取消**：按 F1 → Esc。预期：取景窗立即消失；再按 F1 仍能出取景（验证可重入）。
3. **P1 复现/回归**（需慢窗口或大窗口）：Enter 确认后约 2 秒内再按 F1，等 3 秒后再按 F1。修复前预期：无取景窗、日志固定 `Capture: already capturing, cancel`；按 Esc 系统 Esc 失效。修复后预期：F1 恢复正常出取景，Esc 正常还给系统。
4. **连按**：快速连按两次 F1。预期：第二次取消第一次，最终停在空闲；再按一次仍能出取景（不出现永久 busy）。
5. **日志抽查**：打开 data/logs/app.log，搜索 `Capture: 本次来源`、`Overlay: cancel`、`Hook: 热键订阅者异常`。预期：来源日志齐全、无订阅者异常；若出现 “already capturing, cancel” 后长时间无 `Overlay:` 收尾日志，即命中本报告 P1。
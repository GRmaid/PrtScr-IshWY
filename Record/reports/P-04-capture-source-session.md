# P-04 捕获来源与会话身份（A02 + A05）交付报告

- 单号：`task-5`（派单 P-04），执行：maid-impl
- 日期：2026-10-30
- 基线：派单写 HEAD `0b1ca41`；实测工作树 HEAD 已前移为 `9c81858`（期间 `86d3541` 提交了 P-01/P-07，`9c81858` 为记录提交）。**本单两个文件仍未提交**（按纪律不 commit）。
- 写域：`src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs`、`src/PrtScr.App/Services/CaptureService.cs`、`Record/reports/`。实际只改了前两个 + 本报告。
- 结论：A02（降级留痕）与 A05（会话身份）代码完成，三门禁在本工作树同 SHA 上全绿；GUI 交互未实测。

## 一、做了什么

### A02：来源从捕获结果取，降级必须留痕

1. **单一来源出口**。`CaptureSelectionAsync()` 从 `Task<Bitmap?>` 改为 `Task<CaptureOutcome>`（本文件 279 行），返回值内嵌 `SelectionCapture(Bitmap Bitmap, CaptureContentOrigin Origin, HistoryCaptureMode Mode, string ProcessName, string WindowTitle, string? Notice)`（类型定义 28 行起）。确认、OCR、另存三处出口只消费这个结构，不再各自从 `_captureMode` / `_subregionWindow` 猜来源。
2. **删除推断函数**。HEAD 原有的 `ResolveHistoryTarget()`（原文件 483 行）整段删除，历史写入改为直接传 `selection.Mode / selection.ProcessName / selection.WindowTitle`（确认路径 580 行、另存路径 703 行）。`grep ResolveHistoryTarget` 0 命中。
3. **窗口捕获成功**：来源取 `WindowCaptureResult.Source`，历史 `Window` + 进程名/标题（窗口局部裁剪同样按窗口来源记）。PrintWindow 超时但 WGC 成功时，`Source=Wgc`、`PrintWindowTimedOut=true`，界面提示「窗口响应较慢，已改用 WGC 抓取窗口内容」（`TimedOutNotice()` 389 行）。
4. **允许降级的失败才降级**：`PrintWindowUnavailable / InvalidBounds / Error` → 屏幕区域裁切，结果 `Origin=Screen`、历史 `HistoryCaptureMode.Normal`、`Notice` 为「窗口捕获失败，已按常规截图保存（原因）」（382 行），日志 `Capture: 窗口捕获降级为屏幕区域裁切：{reason}`（379 行）。历史条目因此落在「常规截图」组，不再误记窗口来源。
5. **Busy 不降级**（P-07 契约）：同一窗口上一张 PrintWindow 未返回时，`ToAbort`（392 行）返回 `Abort(Busy, "该窗口上一张截图还在处理中，请稍后重试")` 并写日志「不降级」；本次不落历史、不写剪贴板、不生成屏幕替代图，取景窗恢复后可立即重试。
6. **Timeout 全失败不降级**：`Abort(Timeout, "窗口响应超时，本次未截图；请稍后重试")`；`Canceled` 静默退出（不弹错误）。只有三类「可降级」失败才走屏幕裁切。
7. **「无窗口目标」不算降级**：窗口模式下自由拖拽、没有命中任何窗口时仍安静裁屏幕（HEAD 行为），只写日志 `Capture: 无窗口目标，按屏幕区域裁切`（373 行），不再误报「窗口捕获失败」。
8. **失败后保留取景窗**：新增 `RestoreOverlayAfterCaptureFailure(string message)`（749 行），Busy/超时/失败时重新 `AppWindow.Show()`、恢复 Esc 轮询与键盘激活、保留当前选区、用底部提示条显示原因，主人可直接再按 Enter 重试（A02「允许重新发起」）。

### A05：每次截图一个会话身份，晚到结果不写新会话

1. **发号**：`CaptureService.StartCaptureAsync()` 在真正开始取景前 `Guid.NewGuid()`，存 `_captureSessionId`（`CaptureService.cs` 142-148 行），日志 `Capture: overlay shown session={sessionId:N}`。`Dismissed` 处理器**不清空**会话（正常确认后编码/写历史还要校验）。
2. **会话 API**：`CurrentCaptureSessionId`（34 行）、`IsCurrentCaptureSession(Guid)`（44 行，Empty 恒 false）、`InvalidateCaptureSession(Guid)`（54-67 行，只作废匹配 id）；`_sessionGate` 锁保护（24 行）。取景窗侧 `CurrentSessionId()` 158 行、`IsCurrentSession(Guid)` 161 行、`CanContinue(Guid)` = 会话仍是当前且未结束（386 行）。
3. **取消即失效**：`Cancel()`（148 行）先 `InvalidateCaptureSession(session)` 再 `Dismiss()`，日志 `Overlay: cancel session=…`；Esc 与「截图热键再按一次」都经此路径。
4. **所有 await 之后的写入先校验**：
   - 确认：取图后 537 行校验（日志 539 行）、PNG 编码后 573 行校验（日志 575 行）；copy/history 在 580 行前后；写入完成后再校验一次，旧会话不再弹收尾提示（592 行）。
   - OCR：取图后 435 行（日志 437 行）、编码后 454 行（日志 456 行）校验；晚到即 dispose 位图并日志 `… OCR 取图晚到，已丢弃`。
   - 另存：取图后 666 行校验；文件对话框返回后再校验 689 行（日志 691），晚到只保留已保存的文件、跳过剪贴板/历史。
   - finally 里 `FinishCaptureSession()` 也带会话判断（`!ended && !keepOverlay && IsCurrentSession(session)`，611 行），不会误结下一次取景的会话。
5. **防同会话重复写**：`ConfirmAsync()` 入口增加 `if (_closed) return;`（515 行）——`HideForCapture` 之后贴图窗已隐藏，重复的 Ctrl+C/按钮触发不会再抓一次图、写第二条历史。

### 附带

- 历史元数据 schema 未改（`CaptureHistoryService` 不在本单写域）：降级原因落在**日志 + 界面提示**，历史条目本身通过「窗口/常规」分组体现实际来源。是否要把原因写进 .json 元数据见「待确认」。
- `CaptureOverlayWindow.xaml.cs`、`Prepare` 签名未改；会话 id 由服务层设置，取景窗只读。

## 二、证据

### HEAD 是否仍成立

- 派单基线 `0b1ca41` 已不在 HEAD：`git log --oneline 0b1ca41..HEAD` → `9c81858 docs(record)…`、`86d3541 fix(pin,capture)…`。`86d3541` 含 P-01 的 10 个贴图文件与 P-07 的 `src/PrtScr.Capture/WindowCapture.cs`（+314 行）；**这两个提交未触及本单两个文件**，本单 diff 相对 HEAD 仍成立。
- 本单依赖的 P-07 契约在 HEAD 中可用：`WindowCapture.CaptureDetailedAsync(WindowInfo, CancellationToken)` 返回 `WindowCaptureResult`（`Source/Failure/PrintWindowTimedOut/Detail`），`WindowCaptureFailure` 枚举含 Busy/Timeout/PrintWindowUnavailable/Canceled/InvalidBounds/Error；签名与派单描述一致。
- `HistoryCaptureMode`（`src/PrtScr.Core/HistoryItem.cs`）仍为 `Window/Normal/Legacy/PinCrop`，未改。
- 行号说明：下文行号均为**改后工作树**行号；HEAD 原行号另行标注（如 `ResolveHistoryTarget` 原 483 行）。

### 命令与结果（2026-10-30，工作树，共享锁 `.tmp/build-guard.ps1`）

1. 构建：`pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p04c -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p04/"` → **EXIT=0**，`已成功生成`，0 警告 0 错误（12s）。此前 p04b/p04 两轮同样 EXIT=0（13s / 16.22s）。
2. 测试：`... -Tag p04test3 -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` → **EXIT=0**，`已通过! - 失败: 0，通过: 77，已跳过: 0，总计: 77`（74 ms）。p04test2/p04test 同为 77/77。
3. 格式：`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → **exit 0**，无输出（连续三轮 rc=0）。
4. 变更面：`git diff --stat -- <两个文件>` → `CaptureOverlayWindow.Capture.cs | 396 ++++…`、`CaptureService.cs | 44 ++-`，合计 **381 insertions(+), 59 deletions(-)**；两个文件均未提交。
5. 写域核对：`git status --porcelain` 中属于本单的只有上述两文件；同树其余为他人/既有内容（`M Record/ProjectLog.md`、`M src/PrtScr.Capture/WgcWindowCapture.cs`、`?? .tmp/`、`?? Record/reports/P-08-wgc-interop.md`、`?? 大修改资料/`），未触碰。

### 代码走查证据（每个出口都走同一来源）

| 出口 | 取图 | 来源字段消费 | 晚到处理 |
| --- | --- | --- | --- |
| 确认（Enter/按钮/Ctrl+C） | `ConfirmAsync` 509 行 → 534 行 | `selection.Mode/ProcessName/WindowTitle` 580 行 | 539 / 575 / 592 行 |
| OCR（O 键，有选区） | `OcrSelectionAsync` 428 行 → 434 行 | 日志 460 行（来源+模式） | 437 / 456 行 |
| 另存（Ctrl+S） | `SaveSelectionAsync` 651 行 → 665 行 | `selection.*` 703 行 | 691 行（对话框后） |
| 降级出图 | `CaptureSelectionAsync` 365-382 行 | `Screen` + `Normal` + Notice | — |
| Busy/超时/失败 | `ToAbort` 392-408 行 | 不进历史/剪贴板 | 恢复取景窗 749 行 |

- `grep -n "ResolveHistoryTarget" src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs` → 0 命中。
- `grep -n "CaptureDetailedAsync" …` → 297 行（窗口局部）、344 行（整窗）两处，均直接消费结果对象。

## 三、进行中（交接用）

- **已完成**：A02 + A05 全部代码；本轮三门禁全绿；本报告。
- **下一步第一件事**：主人按文末「主人实测步骤」验收（尤其第 2 条降级、第 3 条 Busy、第 4 条旧会话丢弃），把 `%LOCALAPPDATA%\…\logs` 或应用内日志里的 `Capture: session …` / `Capture: 窗口捕获降级…` 行回传核对。
- **下一步该读的文件**：
  - `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs`：`CaptureSelectionAsync` 279、`ConfirmAsync` 509、`SaveSelectionAsync` 651、`RestoreOverlayAfterCaptureFailure` 749。
  - `src/PrtScr.App/Services/CaptureService.cs`：24-67（会话 API）、142-148（发号）。
  - `docs/HANDOFF-2026-10-28.md` 的 A02/A05 条目、`Record/reports/P-07-printwindow-timeout.md`（Busy/超时语义来源）。
- **未做**：GUI 自动化/冒烟（会写主人真实 data，按派单禁止）；历史元数据字段扩展（服务不在写域）。

## 四、风险与遗留

1. **交互未实测**：所有结论来自编译、走查与日志代码路径；Toast 文案、取景窗恢复的视觉表现需主人在真机确认。
2. **Busy 的 2 秒窗口**：P-07 的单飞门闩在 PrintWindow 线程真正返回前不释放；若目标窗口卡住，2 秒内重复截图会连续提示「上一张还在处理中」。行为正确，但观感像「一直忙」。
3. **降级原因不落历史元数据**：`.json` 元数据 schema 未改，原因只在日志与 toast。要长期可追溯需另开单改 `CaptureHistoryService`（超本单写域）。
4. **已发出的写不会撤销**：会话校验发生在写之前；若剪贴板/历史写入已经开始、期间又开了新截图，旧图可能仍落一次剪贴板（历史写入同理）。窗口很小（编码后到 SetImageAsync 返回），但严格意义上不是原子隔离。
5. **旧会话 finally 的副作用**：两次截图重叠时，旧会话的 `SaveSelectionAsync` finally 会重置 `_saving/_keysSuspended` 并把键盘激活设回 true；仅在「旧会话另存对话框极晚返回 + 新会话恰好在保存」时可能互相干扰，概率极低。
6. **恢复取景窗与 `_confirming`**：按钮/Enter 路径由 `ConfirmCurrentTargetAsync` 在 finally 复位 `_confirming`；恢复后瞬间再按 Enter 可能被忽略一次（微秒级窗口）；Ctrl+C 直连 `ConfirmAsync` 不受影响。
7. **门禁基于含他人改动的工作树**：`src/PrtScr.Capture/WgcWindowCapture.cs`（P-08）当前有未提交改动，本单构建/测试是在该工作树上跑的；该文件若再变需要重跑三门禁。
8. **未新增回归测试**：`PrtScr.Core.Tests` 是纯 Core 层，贴图/取景的 Win32 + WinUI 路径无法覆盖；77 条未减少。

## 五、待确认

1. 降级原因是否要写进历史 `.json` 元数据（需改 `CaptureHistoryService`，超出本单写域）？若需要，请开单并把字段名定下（建议 `sourceFallbackReason`）。
2. Busy 提示当前是「提示 + 恢复取景窗 + 手动重试」；是否需要自动重试或「稍后重试」按钮？
3. 降级成功时的 toast 用 `ToastService.ShowInfo`（中性样式，不受成功/失败开关影响）；是否要改成更醒目的警示样式？

## 主人实测步骤

1. **窗口来源正常**：打开一个被别的窗口遮住一部分的窗口（如资源管理器），按 F1 → Tab 切到「窗口」→ 点该窗口 → Enter。预期：剪贴板里是**窗口本体**（遮挡处也是完好的窗口内容），历史里新增一条在「窗口 · 进程名」分组；日志出现 `Capture: 窗口捕获成功 source=PrintWindow`（或 `Wgc`）。
2. **降级为屏幕要留痕**：对一个 PrintWindow 与 WGC 都抓不到的窗口（受保护/DRM/以更高权限运行且本程序未提权的窗口）同样做窗口截图。预期：截图内容是**屏幕上那块区域**（含遮挡物），弹出「窗口捕获失败，已按常规截图保存（原因）」；历史条目进「常规截图」组而不是窗口组；日志出现 `Capture: 窗口捕获降级为屏幕区域裁切：…`。
3. **Busy 不静默降级**：对同一窗口做两次间隔不到 2 秒的整窗截图（第一次 Enter 后立刻再 F1、点同一窗口、Enter）。预期：第二次提示「该窗口上一张截图还在处理中，请稍后重试」，**不**新增历史、**不**改剪贴板；取景窗自动回来并保留选区，可直接再按 Enter 重试。
4. **旧会话晚到丢弃**：第一次 F1 选区后按 Enter（开始编码），1 秒内再按 F1 开新取景、截另一块区域并确认。预期：剪贴板与历史里只有第二次的图，第一次的结果不会覆盖；日志出现 `Capture: session … 的确认结果晚到，已丢弃` 或 `Capture: session … invalidated`。
5. **另存旧会话**：一次截图按 Ctrl+S 弹出保存对话框先别管它；另开一次 F1 截另一块区域并确认；再回对话框点保存。预期：保存下来的文件是**第一次**的图（对话框已选），但剪贴板与历史**不**写入这张旧图；日志出现「另存结果晚到，跳过剪贴板/历史」。

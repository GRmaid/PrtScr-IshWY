# P-14b · F1 旧会话晚到收尾与全屏失败三态（task-29）

- 状态：代码与三条门禁完成，待 lead 验收/收单（不自行 complete）。
- 环境：E:/Explore/GoodGood/PrtScr，HEAD 69616cc。
- 写域：src/PrtScr.App/Services/CaptureService.cs、src/PrtScr.App/MainWindow.Capture.cs、src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs、src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs、Record/reports。
- 未改：src/PrtScr.Capture/**、csproj、Directory.Build.props、.gitignore、README；未新增依赖；未 git commit/push；未跑 --smoke、未启动 GUI、未合成热键、未杀运行实例。
- 一句话结论：F1 的收尾链现在是两层闸门——overlay 入口的会话归属闸门先把旧会话晚到的 Finish 挡在 _full/幂等位之外，EndCaptureSession 的 stale 判断作为第二道防线；全屏快捷键失败路径改成显式三态（Success / LikelyBlack / AllEnginesFailed），两个失败态都不写历史、不写剪贴板。

## 一、做了什么

### 1.1 第一层闸门：CaptureService.IsOwnedCaptureSession（新增）

- 位置：src/PrtScr.App/Services/CaptureService.cs:74-85。
- 实现：Guid.Empty 返回 true（启动期失败没有会话归属，保持原语义放行）；其余在 _sessionGate 锁内返回 sessionId == _lastSessionId。
- 语义依据：Cancel 只把 _captureSessionId 置空，不释放 _lastSessionId；BeginCaptureSession 写入 _lastSessionId（:190），EndCaptureSession 不清理它（:112-179）。因此：
  - 正常取消：_captureSessionId 已是 Empty，但 _lastSessionId 仍是 A → A 仍被认作归属，放行。
  - 旧 A 晚到而 B 已 Begin：_lastSessionId 已是 B → A 被挡。
  - 旧 A 在 B 已收尾后晚到：B 的 End 不清 _lastSessionId → 仍是 B，A 被挡。
  - Guid.Empty：无归属，放行启动期收尾。

### 1.2 第一层闸门的落点：FinishCaptureSession 入口

- 位置：src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:199-215。
- 顺序：先 IsOwnedCaptureSession(session)（:204-208）→ 再做 _currentSessionFinished 幂等判断（:210-215）→ 才执行 Frozen.Source=null、_full.Dispose()（:220-222）→ 最后 EndCaptureSession（:245）。
- 旧会话晚到时只记 Overlay: session <A> finish ignored (stale session, current=<B>, reason=confirm-end)，然后直接 return，不碰 _full、不写 _currentSessionFinished、不跳 End、不触发 Dismissed。
- 幂等位语义：原 _sessionFinished 改名为 _currentSessionFinished（xaml.cs:100-101），注释明确“本会话是否已收尾：Prepare 时复位；旧会话由归属闸门挡住，不会污染这里”。Prepare/ResetSessionState 复位（xaml.cs:263）；CanContinue（:420）和 RestoreOverlayAfterCaptureFailure（:831）同步使用新字段。
- 正常取消仍走原路径：Cancel 先 InvalidateCaptureSession 再 Dismiss；Dismiss 在 _closed 时也调 Finish（:254-265），此时 IsOwnedCaptureSession(A) 仍为 true，因此不会被误挡。

### 1.3 第二层闸门：EndCaptureSession 的 stale 判断保留

- 位置：src/PrtScr.App/Services/CaptureService.cs:112-139。
- :123 stale = wasCapturing && sessionId != Guid.Empty && sessionId != lastSessionId；:124-125 仅非 stale 清 _isCapturing；:135-139 stale 只记日志并 return，不执行 EndCaptureMode / NotifyOverlayClosed / finished。
- 这层挡的是“旧 Finish 已经绕过或没经过 overlay 入口、直接调用 EndCaptureSession”的情况；正常路径由第一层先挡，第二层是兜底，不替代第一层。

### 1.4 全屏快捷键失败三态

- 新枚举：CaptureService.QuickCaptureStatus { Success=0, LikelyBlack=1, AllEnginesFailed=2 }（CaptureService.cs:633-638）。
- QuickCaptureResult 改为 record(byte[]? Png, QuickCaptureStatus Status)（:640）。
- CaptureMonitorBestAsync 返回 (Bitmap?, QuickCaptureStatus)（:460-564）：
  - 新增局部 sawFrame（:462-463），WGC/DXGI/BitBlt/前台 WGC 任一路径拿到过非 null 帧就置 true（:476、:491、:505、:517、:532）。
  - 成功返回 (bitmap, Success)（:480、:495、:525、:547）。
  - 末尾 return (null, sawFrame ? LikelyBlack : AllEnginesFailed)（:557）：看到过帧但全黑=疑似独占全屏；一个帧都没拿到=硬失败。
- CaptureFullScreenPngAsync（:642-676）：显示器矩形无效 → AllEnginesFailed（:648）；CaptureMonitorBestAsync 返回 null 时透传 status（:652-655）；成功 → Success（:664）；外层异常 → AllEnginesFailed（:674）。
- MainWindow.Capture.cs 三态分流（:39-68）：首抓重试条件改为 Png is null || Status != Success（:39）；日志写 status（:41）。最终 guard 同样（:49）；LikelyBlack → “当前画面像是独占全屏游戏，Windows 无法截取”（:51-54）；否则 → “采集失败，请重试”（:56-60）。两个失败态都在 :62-63 return，历史/剪贴板在 :66-69，失败不执行。防御分支仍覆盖 Png 非 null 但 Status 为 LikelyBlack/AllEnginesFailed 的组合，不落历史。

### 1.5 未改动的部分

- 未改 P-14b 的会话状态机、EndCaptureSession 的非 stale 五条日志关键字、_isCapturing 的唯一清零点。
- 未改采集引擎实现、WGC/DXGI/BitBlt 逻辑、剪贴板/历史服务、csproj。
- CaptureService.cs 的 git diff 仍包含 P-14b 并行改动；本单只占 stale hunk、IsOwnedCaptureSession、三态 hunk。

## 二、证据

### 2.1 两层闸门各挡什么

| 层 | 位置 | 挡什么 | 放行什么 | 失败时行为 |
| --- | --- | --- | --- | --- |
| 第一层：overlay 会话归属 | CaptureOverlayWindow.Capture.cs:204-208 | 旧会话晚到的 Finish（A 已不是 _lastSessionId）在触碰 _full / _currentSessionFinished / End 之前 | 当前会话；正常取消（_lastSessionId 仍是 A）；Guid.Empty 启动期失败 | 只写 Overlay stale 日志，return |
| 第二层：End stale | CaptureService.cs:112-139 | 直接进入 EndCaptureSession 的旧会话（sessionId != _lastSessionId） | 当前会话；非 capturing；Guid.Empty | 只写 Capture stale 日志，return；不清 _isCapturing、不注销 Esc、不写 finished |

### 2.2 “A 飞行中按 F1 起 B，A 晚到”逐行写入点与挡板

场景：A 已 HideForCapture，ConfirmAsync 在 await CaptureSelectionAsync；主人按 F1 取消 A，再按 F1 起 B；A 的取图晚到。

| 步骤 | A 的写入点/动作 | 代码位置 | 修复后结果 | 被哪道闸门挡住 |
| --- | --- | --- | --- | --- |
| 1 | A 按 Enter，HideForCapture，ownsFinish=true，开始取图 | :595-597 | A 的取图在飞 | — |
| 2 | F1 #1：Cancel 取 A；Invalidate 只清 _captureSessionId；Dismiss(A,"cancel") | :150-158 | _captureSessionId=Empty，_lastSessionId=A | 此时 A 仍归属，放行 |
| 3 | FinishCaptureSession(A,"cancel") | :199-247 | 第一层 true；_currentSessionFinished=false→true；释放 A 的 _full；End A 非 stale，清 _isCapturing、注销 Esc、写 finished | —（正常取消） |
| 4 | F1 #2：StartCaptureAsync 发 B；_isCapturing=true；BeginCaptureSession 写 _lastSessionId=B；Prepare 复位 _currentSessionFinished=false；挂 B 的 _full | CaptureService.cs:428-433；xaml.cs:263 | 新会话 B 正常建立 | — |
| 5 | A 的 CaptureSelectionAsync 返回；:600 IsCurrentSession(A) 为 false | :599-605 | 丢弃 A 的结果 bitmap，return；该 return 仍会进 finally | 晚到分支（不是会话状态闸门） |
| 6 | A 的 finally 调 FinishCaptureSession(A,"confirm-end")；入口 IsOwned(A) 比较 _lastSessionId=B | :688-689；:204 | false → 只写 Overlay stale 日志并 return | 第一层 |
| 7 | A 的 _currentSessionFinished=true / _full.Dispose() / End A | :215、:220-222、:245 | 全部不执行 | 第一层（在 :204 提前 return） |
| 8 | B 之后的 FinishCaptureSession(B,"...") | :199-247 | 第一层 true；正常收尾 B；_isCapturing 被 End B 清 | —（B 未被 A 污染） |

补充：如果 A 的 Finish 不经过 overlay 入口而直接调用 EndCaptureSession(A)，则第二层在 :123/:135-139 判 stale（last=B）并 return，仍不清 B 的 _isCapturing/Esc；两层是纵深防御。

### 2.3 “取消 → 旧 A 晚到”三条时序逐行推演

**时序 1：正常取消 A（A 仍在取景，未 HideForCapture）**

1. F1 取消 → Cancel: CurrentSessionId=A → InvalidateCaptureSession(A)：_captureSessionId=Empty，_lastSessionId=A（:190 未被清），App.Log “invalidated”。
2. Dismiss(A,"cancel")：窗口可见时 HideForCapture；FinishCaptureSession(A)。
3. 第一层 IsOwnedCaptureSession(A)：_lastSessionId 仍是 A → true；_currentSessionFinished=false → 置 true；释放 _full；EndCaptureSession(A)。
4. End：wasCapturing=true，stale=false（A==last）→ 清 _isCapturing、EndCaptureMode 注销 Esc、写 finished。
5. 结果：一次取消只收尾一次；没有旧 A 晚到路径；下一次 F1 能正常开始。

**时序 2：A 确认取图在飞；F1 取消 A；F1 起 B；A 晚到（本单核心）**

1. A 已 HideForCapture，_closed=true；ConfirmAsync 在 await。
2. F1 #1：Cancel(A) → Invalidate（_captureSessionId=Empty，_lastSessionId=A）→ Dismiss(A,"cancel")。
3. Dismiss 在 _closed 分支仍调 Finish（:254-265）→ 第一层 true（last=A）→ 收尾 A；End A 非 stale → 清 _isCapturing、注销 A 的 Esc。
4. F1 #2：StartCaptureAsync 看到 _isCapturing=false → 发 B；_isCapturing=true；_captureSessionId=B；BeginCaptureSession 写 _lastSessionId=B；Prepare 复位 _currentSessionFinished=false、挂 B 的 _full。
5. A 的 CaptureSelectionAsync 返回：:600 IsCurrentSession(A)=false → 丢结果，return；finally 因 ownsFinish=true 调 FinishCaptureSession(A,"confirm-end")。
6. 第一层 IsOwned(A)：A != _lastSessionId(B) → false，写 Overlay stale 日志后 return；不碰 B 的 _full/_currentSessionFinished，不调 End A。
7. B 正常显示、可继续操作；B 之后 Finish 时第一层 true → End B 非 stale → 清 _isCapturing、注销 B 的 Esc。
8. 结果：A 只留下 stale 日志 + 自身结果 bitmap 的释放；B 的冻结图、幂等位、busy、Esc 全部保留。

**时序 3：A 晚到发生在 B 已收尾之后**

1. B 已完成：Finish B → End B，_isCapturing=false，_lastSessionId 仍为 B（End 不清它）。
2. A 的 finally 才进 FinishCaptureSession(A,"confirm-end")。
3. 第一层 IsOwned(A)：A != B → false → stale 日志 + return；不释放 _full（B 已释放过，且 A 不能碰当前状态），不调 End A。
4. 即使直接 End A：第二层 stale（last=B）→ 只记 Capture stale 日志，不写 finished、不注销 Esc。
5. 结果：A 的晚到不会重新激活或污染已收尾的 B。

### 2.4 三态矩阵与历史/剪贴板

| Status | Png | MainWindow 实际路径 | 状态栏文案 | 写历史/剪贴板 |
| --- | --- | --- | --- | --- |
| Success | 非 null | :49 guard 不成立 → :66-69 | 全屏截图已复制到剪贴板 / 复制失败提示 | 是 |
| Success | null（防御，不应出现） | :49 guard 成立，Status 非 LikelyBlack → :56-60 | 采集失败，请重试 | 否 |
| LikelyBlack | null | :49 guard 成立 → :51-54 | 当前画面像是独占全屏游戏，Windows 无法截取 | 否 |
| LikelyBlack | 非 null（防御） | :49 guard 成立 → :51-54 | 同上 | 否（:66 不会到） |
| AllEnginesFailed | null | :49 guard 成立，Status 非 LikelyBlack → :56-60 | 采集失败，请重试 | 否 |
| AllEnginesFailed | 非 null（防御） | :49 guard 成立，Status 非 LikelyBlack → :56-60 | 采集失败，请重试 | 否 |

CaptureFullScreenPngAsync 的全部源码调用点（grep CaptureFullScreenPngAsync，仅 src/PrtScr.App）：

| 调用点 | 用途 |
| --- | --- |
| src/PrtScr.App/MainWindow.Capture.cs:38 | 全屏快捷键首次抓取 |
| src/PrtScr.App/MainWindow.Capture.cs:44 | 首帧失败/疑似黑屏后短暂切前台重试 |

### 2.5 三态代码证据

- CaptureMonitorBestAsync 的 sawFrame 置位点：:476（WGC）、:491（DXGI）、:505（首次 BitBlt）、:517（普通 BitBlt）、:532（前景窗口 WGC）。
- 最终返回：:557 return (null, sawFrame ? QuickCaptureStatus.LikelyBlack : QuickCaptureStatus.AllEnginesFailed)。
- CaptureFullScreenPngAsync：:648 无效显示器 → AllEnginesFailed；:652-655 透传 status；:664 成功 → Success；:674 异常 → AllEnginesFailed。
- StartCaptureAsync：:409 接收 captureStatus；:417 日志写 status。
- MainWindow：:39/:49 双 guard；:51 LikelyBlack 分支；:58 通用失败文案；:66-69 写入点全部在 guard 之后。
- 防御组合仍被 guard 拦下：Status != Success 时即使 Png 非 null 也不写历史/剪贴板（:49 条件包含状态）。

### 2.6 门禁（全绿）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14bf1b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14bf1b/" | 0 警告 0 错误，29.07s，BUILD-GUARD: EXIT=0 elapsed=30s |
| 测试 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14bf1btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | 通过 268 / 失败 0 / 跳过 0，93ms，BUILD-GUARD: EXIT=0 |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | rc=0 |

说明：构建命令按 lead 要求使用共享锁 build-guard 与独立 OutDir，未使用 -t:Compile（已知会跳过 XAML MarkupCompilePass1，属假绿）。未跑单项目 build（整包已覆盖）；未跑 GUI、--smoke、合成热键；未 git commit/push。

### 2.7 不变量复核（grep 原始结果）

- _isCapturing = 全仓仅两处：src/PrtScr.App/Services/CaptureService.cs:125（false，EndCaptureSession 非 stale 路径）与 :429（true，StartCaptureAsync 发会话）。
- 五条既有日志关键字仍在原位置：:131 finish ignored (not capturing、:154 state {from} ->、:159 terminate {decision}、:167 finish while slot、:174 finished reason。
- 第一层闸门只在 overlay 入口；IsOwnedCaptureSession 为 internal，未扩散到其他调用点。
- :688-689 的局部所有权注释保留：确认成功时本会话仍是 current/owned，第一层放行；只有旧会话晚到被挡，不改变 P-04b 的 confirm 收尾语义。

## 三、进行中（交接用）

- 已完成：IsOwnedCaptureSession + overlay 入口闸门 + _currentSessionFinished 语义改名；三态枚举/记录、CaptureMonitorBestAsync 三态、CaptureFullScreenPngAsync 三态、MainWindow 三态分流；三条门禁全绿；本报告。
- 未完成：尚未收单。lead 明确要求两条都做完再找他验收，收单前先确认 lead 已验收。
- 下一步第一件事：send_message 给 lead，附本报告路径、门禁三行、两层闸门/三态结论，请求验收；验收后再 team_task_get task-29 取最新 revision，team_task_update(task_id="task-29", action="complete", expected_revision=<最新>, output=三行)。注意 action 必填，枚举是 complete（不是 completed）；本任务无 attempt_id。
- 下一步该读：本报告 2.1/2.2/2.3/2.4；src/PrtScr.App/Services/CaptureService.cs:74-85、112-179、460-564、642-676；src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:199-247、599-605、688-689；src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:100-101、263；src/PrtScr.App/MainWindow.Capture.cs:39-69。

## 四、风险与遗留

1. 未运行时实测：没有 GUI/--smoke/合成热键；第一层闸门、旧 A 晚到、三态文案均为静态推演 + 编译/测试证据。主人实测才能确认界面与日志。
2. 窄竞态（当前 UI 线程模型下不成立）：StartCaptureAsync 先写 _captureSessionId（:430-431），再 BeginCaptureSession 写 _lastSessionId（:432）。若未来有后台线程在这两行之间调用旧 A 的 Finish，IsOwnedCaptureSession 可能仍看到旧 _lastSessionId。当前 Start/Finish/Cancel 都在 UI 线程，未扩大；若以后引入后台收尾，应把会话发布与归属判定做成同一锁内原子操作。
3. Guid.Empty 放行是刻意的：启动期失败（尚未发会话号）仍要能收尾；若未来出现“空会话晚到”的调用，它会被第一层放行并可能碰当前 _full/_currentSessionFinished。当前取消路径的 Empty 只来自无当前会话，未观察到风险；需在新增调用点时复核。
4. _lastSessionId 不清是契约：IsOwnedCaptureSession 依赖 EndCaptureSession 不清理 _lastSessionId。若后续有人为了“释放槽位”顺手清掉它，第一层会把正常取消/当前会话误判为 stale；本报告和代码注释都记录了这个依赖。
5. 文案语义：AllEnginesFailed 现在能落到“采集失败，请重试”；LikelyBlack 才显示独占全屏文案。若某个真实硬失败前恰好拿到过一帧全黑，会被归为 LikelyBlack（仍不写历史/剪贴板），属于可接受的视觉归因误差。
6. 提交拆分：CaptureService.cs 的 diff 含 P-14b 并行改动；提交时按写域拆 hunk，不能整文件归属本单。
7. 未改 .gitignore、README、csproj；未新增依赖；未杀运行实例；未 commit。

## 五、待确认

1. lead 是否验收本单的两层闸门 + 三态实现；验收后再收单。
2. QuickCaptureStatus 目前定义在 CaptureService 类内（嵌套 public enum），MainWindow 用 Services.CaptureService.QuickCaptureStatus 引用；若希望提升到命名空间层，需另改（不影响当前行为）。
3. 旧 A 晚到的 Overlay stale 日志使用 CurrentCaptureSessionId（当前可能是 B 或 Empty）；是否还需要同时打印 _lastSessionId 以便更强归因？当前 End 的 stale 行已打印 lastSessionId，两层日志可互补。
4. 是否要为三态补自动化测试（当前 Core.Tests 268 条全过，但没有 App 层三态/闸门测试；写域不含测试项目）。

## 主人实测步骤

以下步骤需主人真实按键执行；代理不代按热键、不启动 GUI 代测。

1. 正常截图回归：F1 → 选一个窗口 → Enter，确认剪贴板有图、历史新增一张；再 F1 → Esc，确认取景窗关闭，下一次 F1 仍能打开。
2. F1 核心复现：F1 选窗口 → Enter 开始确认取图；在取图尚未结束时按 F1 取消，紧接着再按 F1 起新取景。预期：新取景正常显示冻结帧、能继续选区和 Esc 取消；不再出现“下一次 F1 没反应/永久 busy”。
3. 旧会话晚到日志：重复第 2 步后查看 data/logs/app.log，预期出现 Overlay: session <A> finish ignored (stale session, current=<B>, reason=confirm-end)；不应出现 Capture: session <A> finished reason=confirm-end；新会话 B 之后仍应有自己的 Capture: session <B> finished reason=...。
4. 三态文案：在独占全屏游戏/黑屏场景按 F2，预期状态栏是“当前画面像是独占全屏游戏，Windows 无法截取”，历史与剪贴板没有新增；制造一次硬失败（例如切换显示器配置后立即按 F2，或临时禁用采集路径），预期显示“采集失败，请重试”且同样不写历史/剪贴板。
5. 日志抽查：在 data/logs/app.log 搜 status=LikelyBlack / status=AllEnginesFailed / finish ignored (stale session，核对失败路径没有 QuickCapture: <w>×<h> 的成功行，也没有历史写入。

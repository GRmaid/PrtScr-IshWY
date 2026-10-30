# P-06（A01）钩子回调去阻塞：热键事件投递化 + 钩子线程日志降级

- 单号：task-3 / P-06 / A01
- 写域：src/PrtScr.App/Services/EscOverPinHook.cs、src/PrtScr.App/MainWindow.Hotkeys.cs、src/PrtScr.App/App.xaml.cs、Record/reports/**
- 完成时间：2026-10-30
- 代码基线：HEAD = 9c81858（0b1ca41 + 86d3541 + 9c81858，后两个提交只涉及 P-01/P-07 与 docs/Record）。本单三个源文件执行 git diff 0b1ca41 HEAD 为空，即 A01 相关代码与派单所述 HEAD 0b1ca41 一致；本单改动全部在工作树，未提交。

## 一、做了什么

A01 的原始缺陷：低级键盘钩子（WH_KEYBOARD_LL）回调里会同步执行订阅者代码，而截图热键的订阅者在钩子线程上直接调用 App.Log（加锁 + 建目录 + 同步写盘）。系统对 LL 钩子回调有超时限制（LowLevelHooksTimeout），超时会跳过这次按键、连续超时还会静默摘钩。

改动三处，语义只动「执行线程」，不动判定：

1) EscOverPinHook 的三个热键事件改为投递式（EscOverPinHook.cs）
   - 新增线程池投递方法 RaiseDeferred（275-292）：订阅者一律在线程池执行，异常在普通线程上捕获并走 HookLog 队列，绝不回到钩子线程。
   - EvaluateScreenshotHotkey / EvaluateModeHotkey / EvaluateFullScreenHotkey 的 ScreenshotPressed?.Invoke() / ModeSwitchPressed?.Invoke() / FullScreenPressed?.Invoke() 分别替换为 RaiseDeferred(...)（492、507、518）。
   - 记录钩子线程托管 id（_hookManagedThreadId，字段 50，在 HookThreadMain 307 首行赋值），用于 IsHookThread 判定。
2) App.Log 增加钩子线程兜底（App.xaml.cs 243-269）
   - Log 开头判断 EscOverPinHook.IsHookThread：是则转 EscOverPinHook.LogDeferred（只入无锁 ConcurrentQueue），直接返回，绝不加 LogLock、不建目录、不写文件（247-251）。
   - 其余线程行为完全不变（255-263 原有 lock + Directory.CreateDirectory + File.AppendAllText 逻辑未动）。
   - 新增 IsHookThread（266-267）与 LogDeferred（270）供上述判定与队列复用；日志落盘仍由 StartLogWriter 的 PrtScr.HookLog 线程执行（240-262）。
3) MainWindow.Hotkeys.cs 截图热键订阅点把日志移入 UI 队列（54-64）
   - 原写法在订阅 lambda 里先 App.Log 再 TryEnqueue；现改为 TryEnqueue 内执行 App.Log + OnHotkeyPressed（59-63），热键投递路径上没有任何日志写入。

明确未改的部分（守住「不改热键语义」）：
- 五热键注册/修饰键自记账（TrackModifier、_ctrlDown 等）、300ms 去抖（TryClaim*）、Esc/取景键白名单与消费判定（TryHandleOverlayKey）、EscRouter 仲裁规则、RegisterHotKey 兜底通道（HotkeyService 自有 PrtScr.Hotkey 线程 + NotifyFallbackHit）全部未动。
- 取景窗的键盘直达仍是同步调用 handler（DispatchOverlayKey 456-469 → OnOverlayHotkey），刻意保留：该 handler 只做 DispatcherQueue.TryEnqueue（CaptureOverlayWindow.Capture.cs:65-69），且必须同步返回 true 以吞掉这次按键、并保持连续按键（Esc/Tab/Enter）的先后顺序；若也投递到线程池会引入乱序风险。它不在「阻塞源」清单内。

## 二、证据

### 2.1 门禁（命令与原始结果）

1) 共享锁构建（完整解决方案，重定向 OutDir 避开运行中的 IshWY.exe）：

    pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p06b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p06/"

   结果：BUILD-GUARD: EXIT=0，已成功生成。0 个警告，0 个错误，10.96s（首轮 p06 同样 EXIT=0）。

2) 测试：

    pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p06test2 -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"

   结果：已通过! - 失败: 0，通过: 77，已跳过: 0，总计: 77，73 ms（首轮 p06test 同样 77/77）。

3) 格式：

    dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore

   结果：exit 0，无输出。

### 2.2 改了哪些文件（git diff --stat，工作树）

    src/PrtScr.App/App.xaml.cs                |  8 ++++++
    src/PrtScr.App/MainWindow.Hotkeys.cs      |  9 +++++--
    src/PrtScr.App/Services/EscOverPinHook.cs | 44 ++++++++++++++++++++++++++-----
    3 files changed, 53 insertions(+), 8 deletions(-)

工作树同时存在他人的未提交改动（MainWindow.xaml/.xaml.cs、CaptureHistoryService.cs、ConfigService.cs、Capture* 等，属 P-02/P-09a 等单），与本单无关；本单只改上述三个文件与这份报告。

### 2.3 钩子回调内逐行确认清单（EscOverPinHook.cs，HookProc 533-569）

| 行 | 语句 | 分类 | 阻塞风险 |
| --- | --- | --- | --- |
| 533 | [UnmanagedCallersOnly] 标记 | 元数据 | 无 |
| 534-535 | HookProc 签名 / try 开始 | 入口 | 无 |
| 538 | if (nCode >= 0 && lParam != 0) | 判定 | 无 |
| 540-543 | 读 KbdLlHookStruct、消息类型、isDown/isUp | 判定 | 仅 lParam 解引用 |
| 545-548 | TrackModifier 记账 4 个 bool | 记账 | 无锁、无 IO |
| 551-552 | TryHandleOverlayKey → DispatchOverlayKey → handler(vk,ctrl) | 同步投递 | handler 只 DispatcherQueue.TryEnqueue（见 2.5） |
| 554-559 | 三个热键 Evaluate* | 判定 + 投递 | 见下 |
| 563-566 | catch 吞异常 | 兜底 | 无 |
| 568 | CallNextHookEx | 返回 | 无 |

热键 Evaluate* 内部（481-531）只做三类事：读 volatile 状态与修饰键（Volatile.Read / GetAsyncKeyState）、TryClaim* 的 Interlocked 去抖比较、命中后 HookLog（ConcurrentQueue.Enqueue + AutoResetEvent.Set）与 RaiseDeferred（ThreadPool.QueueUserWorkItem）。RaiseDeferred 是回调里唯一的新增投递原语（275-292），O(1)、不等待、不持锁。

对照证据（grep EscOverPinHook.cs 的 App.Log / File. / Directory. / lock ( / Monitor. / await / .Wait(）：

    219:        lock (InstallLock)              // Install()，UI 线程，非回调
    237:        _ = InstallDone.Wait(2000);     // Install()，UI 线程，非回调
    242:        lock (InstallLock)              // StartLogWriter()，安装期，非回调
    253:                        App.Log(line);  // PrtScr.HookLog 写线程，非钩子线程
    269: 文档注释里对 App.Log 的引用

可见 HookProc 及 Evaluate* 路径内没有文件/目录/锁/await/无界等待调用；App.Log 的唯一落盘点在 253 行的日志线程，不在回调线程。

### 2.4 回调 → 实际动作的线程路径（改动后）

- 截图热键：HookProc → EvaluateScreenshotHotkey → TryClaim（Interlocked 300ms 去重）→ HookLog（无锁队列）→ ThreadPool 投递 → 订阅者（MainWindow.Hotkeys.cs 54-64）→ DispatcherQueue.TryEnqueue → UI 线程 App.Log + OnHotkeyPressed → CaptureService.StartCaptureAsync。
- 全屏热键：同上 → MainWindow.Hotkeys.cs:108（仅 TryEnqueue）→ OnFullScreenPressed。
- 模式切换热键：同上 → MainWindow.Hotkeys.cs:78-82（仅 TryEnqueue）→ ToggleCaptureProfile。
- 取景器按键：HookProc → TryHandleOverlayKey → handler（OnOverlayHotkey，只 TryEnqueue）→ UI 线程 HandleOverlayHotkey。
- 钩子线程自身的日志：HookLog 队列 → PrtScr.HookLog 线程（BelowNormal）→ App.Log（非钩子线程，正常写盘）。

### 2.5 订阅者清单（全仓库 grep 三个事件 +=）

    src/PrtScr.App/MainWindow.Hotkeys.cs:54   ScreenshotPressed
    src/PrtScr.App/MainWindow.Hotkeys.cs:78   ModeSwitchPressed
    src/PrtScr.App/MainWindow.Hotkeys.cs:108  FullScreenPressed

三处均只做 DispatcherQueue.TryEnqueue（54-64 的 App.Log 也已移入该队列）。除此之外没有其他订阅者。

## 三、进行中（交接用）

已完成：
- A01 代码修复（三文件，工作树未提交），三门禁同工作树通过（build 0/0、test 77/77、format rc=0）。
- 本报告。

下一步第一件事：
- 主人按下方「主人实测步骤」做交互验收（重点：连续热键 20 次不丢键、游戏中 F1 仍生效、日志无「Hook: 热键订阅者异常」）。
- 验收通过后由 lead 提交，注意共享工作树还有他人未提交改动，提交需按路径只 add 本单三个文件：
      git add src/PrtScr.App/Services/EscOverPinHook.cs src/PrtScr.App/MainWindow.Hotkeys.cs src/PrtScr.App/App.xaml.cs Record/reports/P-06-hook-offload.md

下一步该读的文件：
- src/PrtScr.App/Services/EscOverPinHook.cs:265-302（IsHookThread / LogDeferred / RaiseDeferred / HookLog）
- src/PrtScr.App/Services/EscOverPinHook.cs:481-569（三个 Evaluate 与 HookProc）
- src/PrtScr.App/App.xaml.cs:243-269（Log 的钩子线程分支）
- src/PrtScr.App/MainWindow.Hotkeys.cs:54-64（截图订阅点）
- 若继续收口其他钩子：src/PrtScr.App/Views/PinCropWindow.cs:202-230、src/PrtScr.App/Views/PinMenuWindow.cs:470-496（见风险与遗留）

## 四、风险与遗留

1) 未做运行时测量：本单只有编译/静态走查证据，没有钩子回调耗时实测，也没有 --smoke（按纪律禁跑，避免写主人真实 data/）。回调耗时收益需由主人实测期间观察（连续按键不丢、Esc 不失灵）。
2) 热键事件从「回调内同步」变为「线程池异步」：多约一次线程池调度（微秒级）。三个订阅者都只 TryEnqueue，不依赖同步时序；300ms 去重仍在钩子线程先完成，故不会出现同一按键被处理两次。
3) 取景器按键 handler 仍同步执行（DispatchOverlayKey → OnOverlayHotkey）：这是刻意保留的，handler 只 TryEnqueue，且同步返回 true 才能吞键、保持按键顺序；若未来有人往该 handler 里加日志/IO，会把阻塞带回钩子线程，需在代码评审中盯住。
4) 其他低级钩子未纳入本单（超出写域）：
   - src/PrtScr.App/Views/PinCropWindow.cs:188 安装 WH_KEYBOARD_LL，回调 202-230 只读键状态 + DispatcherQueue.TryEnqueue，未见写盘/锁。
   - src/PrtScr.App/Views/PinMenuWindow.cs:457 安装 WH_MOUSE_LL，回调 470-482 会调用 _current.CheckOutside（Marshal.PtrToStructure + GetWindowRect + 可能 TryEnqueue，484-496）与 PinClickGuard.Suppress()；理论上比键盘回调重，若后续出现菜单卡顿/丢点击，建议另立单把 CheckOutside 的窗口矩形计算移出回调。
5) HookLog 队列上限 128 条，超限直接丢弃（294-302）；这是既有策略，防止回调被日志反压。丢弃只影响诊断日志，不影响按键。
6) PendingLogs.Count（297）不是锁，但 ConcurrentQueue.Count 会遍历段；队列上限 128 条、段数极少，实测代价可忽略。若要把回调压到绝对最小，可改为 Interlocked 计数器，属于可选优化。
7) IsHookThread 依赖 HookThreadMain 首行赋值 _hookManagedThreadId；将来若新增第二个钩子线程（例如另一个 LL 钩子类），需同样登记，否则 App.Log 的兜底不会生效（但该线程上直接调用 LogDeferred 仍安全）。
8) 未新增自动化测试：Core.Tests 是纯 Core 层，覆盖不到 App 的 Win32 钩子路径；77 条未减少。
9) 工作树并发：本单三个文件未被他人修改；MainWindow.xaml/.xaml.cs 等他人改动同时存在，验收时若整包行为异常需先区分来源。

## 五、待确认

1) PinCropWindow（键盘 LL）与 PinMenuWindow（鼠标 LL）的回调是否要在后续单里也做去阻塞？本单按写域只改 EscOverPinHook，未动这两处。
2) 「游戏中按 F1 仍生效」属系统级行为，静态改动只能保证回调快速返回；是否被系统摘钩仍需主人在真实全屏游戏里验证（本环境无法代测）。
3) 若主人希望日志保留「钩子命中」的纳秒级先后顺序（例如与 RegisterHotKey 兜底日志对比），线程池投递会让日志顺序落后一个调度周期；当前日志语义（命中/未命中/重装）不受影响，如需严格时序可再议。

## 主人实测步骤

1. 连续按 F1 共 20 次（每次取景窗出现后按 Esc 退出，等窗口消失再按下一次）。预期：20 次都能正常出取景窗，无丢键、无卡顿；data/logs/app.log 中「Capture: overlay shown session=」出现 20 次，「Hotkey: 键盘钩子捕获截图键」出现 20 次。
2. 打开一个全屏应用或游戏（或视频全屏播放），按 F1。预期：仍能进入取景并完成截图（钩子通道未被吞），Esc 仍能退出取景。
3. 取景过程中依次按 Tab（切窗口/常规模式）、Enter（确认截图）、Esc（取消）。预期：与改动前一致，Tab 只切本次模式并保留选区，Enter 出图，Esc 退出且不写历史。
4. 贴一张图，按 Esc 关闭贴图；再进入标注（右键 → 标注…）后按 Esc。预期：第一次关贴图，标注态下只退出标注、贴图仍在（Esc 仲裁不变）。
5. 打开 data/logs/app.log，搜索「Hook: 热键订阅者异常」。预期：0 条；同时确认没有出现「Hotkey: 键盘钩子捕获截图键」缺失（即日志没有因投递丢失）。

# A 批次采集层独立复核（P-08 / P-09a / P-07b 交叉核对）

- 单号：task-15；写域：Record/reports/**（本单未改任何源码）；执行者：maid-impl；日期：2026-10-30
- 基线：HEAD `9c81858`；采集层 4 文件为未提交工作树改动，`git diff --stat -- src/PrtScr.Capture` = 4 files, 501 insertions(+), 143 deletions(-)：
  - `src/PrtScr.Capture/WgcWindowCapture.cs`（853 行，P-08/P-09a）
  - `src/PrtScr.Capture/WindowCapture.cs`（466 行，P-07/P-07b）
  - `src/PrtScr.Capture/ScreenCapturer.cs`（74 行，P-09a）
  - `src/PrtScr.Capture/DesktopDuplicationCapture.cs`（391 行，P-09a）
- 方法：四个源文件全文只读 + 每条失败/取消/超时/早退出口逐一枚举 + P-07b 契约矩阵逐行对代码。未运行构建/测试/--smoke（本单只读，不产生代码改动）。

## 1. 做了什么与结论

- 复核范围：`WindowCapture.cs`、`WgcWindowCapture.cs`、`ScreenCapturer.cs`、`DesktopDuplicationCapture.cs` 全文；对照 `Record/reports/P-08-wgc-interop.md`、`P-08-independent-review.md`、`P-09a-capture-resources.md`、`P-07b-wgc-detailed.md`。
- 结论：**可放行（本批三单不阻断）**；0 项阻断返工，1 项建议修（Q1），2 项需 lead 裁决（Q2/Q3），2 项记录级（Q4 理论缺口、Q5 矩阵文档），1 项清单外观察（Q6）。
- 一句话：三单声称的修复基本成立；剩余问题集中在「异常/并发窗口极窄的泄漏」与「入口未预判 token 的语义边界」，不影响当前正常截图链路（App 尚未传 CancellationToken）。

## 2. 证据

### 2.1 五项必查清单结论

| # | 清单项 | 结论 | 关键证据 |
| --- | --- | --- | --- |
| 1 | COM/WinRT 释放（每条失败/取消/超时出口） | **通过**，仅 Q1 一处异常分支遗漏 | WGC item 全出口 `finally` 释放 `WgcWindowCapture.cs:273-276`；session/pool `:707`/`:749`；首帧与晚到帧 `:698-703`/`:726-739`；池失败逆序释放 `:403-412`、重建 `:562-600` |
| 2 | 线程与单飞（门闩全出口、同 HWND 串行、晚到 Bitmap 释放） | **通过**，Q4 为理论缺口 | `TryEnterFlight :140`；continuation 释放晚到帧并归还门闩 `:166-185`（Dispose `:173`、`ExitFlight :180`）；`AbandonLate :359-369`；线程未起/未建 flight 兜底 `:247`/`:350-351` |
| 3 | CancellationToken 归一 | **部分通过**：三条路径均可归 Canceled，但入口未预判 token（Q3） | PrintWindow 等待取消 `:261-265`；超时后取消 `:284-285`；WGC 显式归一 `:328-336`、防御 OCE `:294-300`；WGC 入口预取消 `WgcWindowCapture.cs:199-204`、首帧 WaitAsync `:723` |
| 4 | GetDC/ReleaseDC、Map/Unmap、GetHbitmap/DeleteObject 配对 | **通过** | `ScreenCapturer.cs:36`→`:42`→`:71`、`:49`/`:58`、`:63`；WGC `:794`/`:835`、`:840`/`:845`/`:850`、`:827`；DD `:104-105`、`:157`、`:258-262`、`:290`/`:388`、`:323`/`:383`、`:374` |
| 5 | 与 P-07b 契约矩阵自洽（抽查 5 行） | **通过**（行 5/8/10/14/16 全一致） | 见 2.6 |

### 2.2 WGC 窗口捕获：出口枚举

`WgcWindowCapture.CaptureWindowCoreAsync`（`:226-277`）每个出口：

| 出口 | 触发 | item 释放 | session/pool | frame | 证据 |
| --- | --- | --- | --- | --- | --- |
| 成功 | bitmap 非空 | 是（finally） | `using session :707`、外层 finally `pool.Dispose :749` | `using var frame :723` 已释放 | `:255`、`:273-276`、`:746-750` |
| item 创建失败 | CreateForWindow=null | 不适用 | 未创建 | 不适用 | `:229-234`、`CreateItemForWindow :642-682` |
| 设备不可用 | GetOrCreateDevice=null | 是 | 未创建 | 不适用 | `:238-244` |
| 拷贝失败 | CopyFrameToBitmap=null | 是 | 是 | 已释放 | `:248-253` |
| 调用方取消 | OCE when token | 是 | 是 | 晚到首帧由 continuation 释放 | `:257-260`、`:726-739` |
| 首帧超时 | TimeoutException | 是 | 是 | 同上 | `:261-266` |
| 其它异常 | catch | 是 | 是 | 同上 | `:267-272` |
| 入口预取消/句柄非法 | token 已取消 / HWND=0 | 不适用 | 未创建 | 不适用 | `:192-204` |

`CreateItemForWindow` 本身：`itemAbi` 在 `:665-668` finally 释放，interop RCW 在 `:670-673` finally 释放；`CreateItemForMonitor :602-635` 同模板（`:620-623`/`:625-628`）。`ReleaseCaptureItem :287-300` 用 `((IWinRTObject)item).NativeObject.Dispose()` 并在 `:294` 释放，异常只记日志。

### 2.3 显示器常驻池：出口枚举

- 已存在：`EnsureMonitorPool :336-340` 锁内命中直接返回，无新资源。
- 创建失败：`:403-412` finally 在 `registered=false` 时按逆序 `session?.Dispose(); pool?.Dispose(); ReleaseCaptureItem(item)`，覆盖 CreateItemForMonitor/CreateFreeThreaded/CreateCaptureSession/属性/订阅/StartCapture 任一处抛异常。
- 注册成功：`:391-396` 进 `_monitorPools`；`RebuildMonitorPool :562-600` 先摘字典、再 Dispose Latest `:574-578`、分别 try Session/Pool `:581-600`、最后 ReleaseCaptureItem。
- 帧所有权：`RetainLatest :477-494` 锁内替换并 Dispose 旧帧；`TakeLatest :497-505` 转移给调用方；`WaitFrameAfterAsync :507-554` 三条出口（早返回 `:513-514`、命中新帧先 Dispose 兜底 `:528-533`、超时可用 `:540-545`、异常/残影 finally `:549-553`）全部释放；`GetLatestMonitorFrameAsync :457-467` 用 `using(frame)`。
- 进程退出：静态 `_monitorPools` 无显式 Dispose（进程回收），可接受；`NotifyOverlayClosed :415-422` 只写时间戳。
- **Q2**：创建全程在锁外、`:391-394` 覆盖注册（详见 2.7）。

### 2.4 GDI / DXGI Desktop Duplication 配对

- `ScreenCapturer.CaptureVirtualScreen`：`GetDC(null) :36` 失败先抛（不建 Bitmap），Bitmap 在 `:42` 创建；`:69-72` finally `ReleaseDC`；`GetHdc :49` / `ReleaseHdc :58`；Bitmap 创建或 BitBlt 异常在 `:61-65` Dispose。**全部配对**。附注：`:52-55` 的 `BitBlt` 返回值未检查，失败会静默返回全黑图（非资源问题，既有行为）。
- `DesktopDuplicationCapture.Capture`：`:98-106` finally 释放 device/context；每输出：非目标 `:157` ReleaseComObject、目标由 `outputObject` 在 `:260` 释放；`:181` 释放 GetIUnknownForObject；`:258-262` 释放 dup/output1/outputObject/adapterObject/deviceObject。
- 纹理链：`CopyTexturePointer :278-292` QI 的 texture `:290` 释放；`CopyTexture :294-390` staging `:388`、Map/Unmap `:323`/`:383`、Bitmap 异常 `:374`；帧侧 `:242-246` finally `TryReleaseFrame` + resource Release，超时/失败/全零均走同一 finally。
- 极小概率路径（未计入 Q1-Q6，仅备注）：`:92-96` D3D11CreateDevice 返回失败但 device 非 0 时不释放（文档约定失败置空）；`:149` `output.GetDesc` 抛异常时该 output RCW 未释放；`:221-225` AcquireNextFrame 非超时失败且 resource 非空时 resource 未释放。三者均为异常窗口极窄路径。

### 2.5 CancellationToken 归一

| 路径 | 代码 | 结果 |
| --- | --- | --- |
| PrintWindow 等待期取消 | `WindowCapture.cs:255-256` delay 被取消 → `:261-265` | `Failure=Canceled`，`PrintWindowTimedOut=false`，WGC 字段 None |
| 超时后 token 再取消 | `:269-273` 超时 → `:284-285` 检查 token | `Failure=Canceled`，`PrintWindowTimedOut=true`（矩阵未列，Q5） |
| WGC 显式取消 | `:328-336` | `Failure=Canceled` + `WgcFailure=Canceled` + wgc.HResult/Detail |
| WGC 抛 OCE（防御） | `:294-300` | `Failure=Canceled` + `WgcFailure=Canceled` + HResult=0 + 固定 Detail |
| WGC 入口预取消 | `WgcWindowCapture.cs:199-204` | `WgcFailure.Canceled`（不建任务） |
| 首帧 WaitAsync 取消 | `WgcWindowCapture.cs:723`、catch `:726-739` | OCE 上抛 → 外层归 Canceled，晚到首帧由 continuation Dispose |
| 显示器池 | `WgcWindowCapture.cs:522` 仅 `Task.Delay` 用 token | 取消以 OCE 上抛，不产出 WindowCaptureResult（调用方未传 token） |

**Q3**：`CaptureDetailedAsync` 入口（`:214-228`）未先判 token，预取消仍会占门闩并起 PrintWindow；WGC 侧却预判。详见 2.7。

### 2.6 契约矩阵抽查（P-07b §2.4 行 5/8/10/14/16）

| 行 | 矩阵声称 | 代码事实 | 判定 |
| --- | --- | --- | --- |
| 5 | 失败 + WGC 成功 → `Failure=None`、`Source=Wgc`、`PrintWindowTimedOut=false`、Wgc `None`/wgc.HResult/wgc.Detail | `WindowCapture.cs:312-324`；WGC 成功构造 `WgcWindowCapture.cs:59-60`（HResult=0、Detail=null） | **一致** |
| 8 | 失败 + WGC `Canceled` → `Failure=Canceled`、`Source=None`、Wgc `Canceled`/hr/detail | `WindowCapture.cs:328-336` | **一致** |
| 10 | 失败 + `ItemCreationFailed` → `Failure=PrintWindowUnavailable`、Wgc `ItemCreationFailed`/hr/detail | `:338-343`（`:340` 非超期分支） | **一致** |
| 14 | 超期 + 任一 WGC 失败 → `Failure=Timeout`、`PrintWindowTimedOut=true`、Wgc 对应枚举 | `:338-343`（`:340` 超期分支） | **一致** |
| 16 | WGC 抛 OCE → `Failure=Canceled`、Wgc `Canceled`/0/固定文本 | `:294-300` | **一致** |

### 2.7 问题清单（按严重度）

#### Q1【建议修｜低｜真实泄漏】`WgcWindowCapture.cs:148-171` 设备 ABI 异常分支未释放

- 代码：`:150` `CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDeviceAbi)` → `:158` `_cachedWinrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi);` → `:159` `Marshal.Release(graphicsDeviceAbi);`
- 缺陷：若 `:158` 抛异常，catch `:165-171` 只释放 `dxgiDevice`/`device`/`context`，**未释放 `graphicsDeviceAbi`**。
- 后果：每次异常泄漏一个 WinRT ABI 引用（连带 D3D 设备引用）。触发条件罕见（FromAbi 类型不匹配/OOM），但属 P-09a「设备全链释放」的遗漏。
- 建议：把 `:158-159` 包进 try/finally（finally `Marshal.Release(graphicsDeviceAbi)`），或在 catch 首行补 Release；补后把 P-09a 报告状态改为「已复核 + 1 处补丁」。

#### Q2【需 lead 裁决｜低｜并发泄漏】`WgcWindowCapture.cs:334-413` 池创建竞态覆盖

- 代码：`:336-340` 只在 `_poolLock` 内做 `ContainsKey` 快检；`:348-389` 创建 item/device/pool/session 全程锁外；`:391-394` 再上锁 `_monitorPools[hMonitor] = state` 并置 `registered=true`。
- 缺陷：两个线程并发首次 `EnsureMonitorPool` 同一 HMONITOR，后写覆盖前一个 state；前者已 `registered=true` 不走 finally，其 pool/session/item 失去引用且永不释放。
- 可达性：当前调用点都在 UI 线程串行（`CaptureService.StartCaptureAsync` 与 `GetLatestMonitorFrameAsync`），实际几乎不可达；若将来并行预热/快速截图并发化会暴露。
- 选项：a) 创建纳入 `_poolLock`（启动耗时长会阻塞其它显示器）；b) 创建中占位（`Dictionary<nint, Task<MonitorPool?>>`）；c) 接受现状并在注释声明单线程约束。**请裁决。**

#### Q3【需 lead 裁决｜低｜契约语义】`WindowCapture.cs:211-228` 入口未预判 token

- 代码：`:216-228` 先校验句柄尺寸、`TryEnterFlight`，`:241` 起 PrintWindow；直到 `:255-265` 才观测取消。WGC 侧 `WgcWindowCapture.cs:199-204` 则是入口预判。
- 边界：预取消 token 仍会起一次最长 2s 的 PrintWindow；若 PrintWindow 在取消可观测前完成（`:259` → `:281-282`），返回 `Success(PrintWindow)` 而非 `Canceled`。
- 现状影响：App 两处窗口捕获均未传 token（P-07b 已承认 Canceled 不可达），无实际行为差异；一旦把会话取消接进 token 需统一。
- 选项：入口加 `if (cancellationToken.IsCancellationRequested) return Failed(Canceled, false, "捕获已取消")`（与 WGC 对齐），或矩阵补「预取消」「超时后取消」两行。**请裁决。**

#### Q4【记录｜极低｜理论】`WindowCapture.cs:158-188` ContinueWith 注册失败的理论缺口

- 若 `Task.Factory.StartNew` 成功后 `ContinueWith` 抛异常（正常不会），外层 catch `:243-248` 立即 `ExitFlight` 并置 `printTask=null`；已启动的 PrintWindow 线程结束时无人释放晚到 Bitmap（一张整窗口位图），同 HWND 也可再起一次。
- 不要求本批修；若修，把 `ContinueWith` 包进同一 try，异常时给 flight 挂兜底释放。

#### Q5【文档补注】P-07b 矩阵行 4 / 行 6

- 行 4 只写「等待期被调用方取消 → `PrintWindowTimedOut=false`」，未列「超时后 token 再取消」（`:284-285` 返回 `Canceled` 且 `PrintWindowTimedOut=true`）。
- 行 6 的 `WgcDetail` 列按矩阵为 `wgc.Detail`（成功时 null），超时原因实际在顶层 `Detail`（`WindowCapture.cs:322`「PrintWindow 超过 2000ms，已改用 WGC」）；不矛盾，但抽查易误读。
- 建议矩阵补一行 + 行 6 脚注说明顶层 `Detail` 与 `WgcDetail` 分工。

#### Q6【清单外观察｜建议后续单】D3D11 immediate context 跨线程共享

- `WgcWindowCapture.GetOrCreateDevice` 缓存单例 device/context（`_cachedDevice`/`_cachedContext` `:125-126`）；窗口捕获 `CopyFrameToBitmap`（`Task.Run` 线程，`:757-852`）与显示器池 `CopyFrameToBitmap`（`GetLatestMonitorFrameAsync` 调用线程，`:465`）都会对同一 immediate context 做 `CopyResource`/`Map`。
- D3D11 immediate context 默认非线程安全；正常取景时序（先冻屏、后窗捕）不重叠，但「全屏快速截图 + 取景中的窗口捕获」并发组合未加锁。
- 不属 P-08/P-09a/P-07b 引入；建议后续单评估加 context 锁或 deferred context。

### 2.8 与三份报告声称的对照

- P-08-independent-review 两条待修（item RCW 未释放、晚到首帧未 Dispose）→ **P-09a 已修**：`ReleaseCaptureItem :287-300` 与 finally `:273-276`；`OnFrameArrived :698-703`（非首帧当场 Dispose）+ 超时 continuation `:726-739`。
- P-08「新 API 无消费者」→ **仍成立**：App 未读取 `WgcFailure/WgcHResult/WgcDetail`；`src/PrtScr.App/Services/CaptureService.cs:280` 仍用兼容 `CaptureAsync`（不返回 `WindowCaptureResult`）。P-07b 报告 2.2 已如实承认。
- P-09a 三条声称（WGC item 释放、ScreenCapturer 先取 DC 再建图、DD 输出枚举与 Map/Unmap）→ **代码核实成立**；Q1 的 FromAbi 异常分支是设备释放链唯一遗漏。
- P-07b「Canceled 归一当前不可达」→ **成立**（App 两处调用未传 token）；矩阵抽查 5 行与代码一致，仅 Q5 文档补充。

## 3. 进行中（交接用）

- 已完成：五项清单复核、WGC 窗口/池/DD-GDI 出口枚举、契约矩阵抽查 5 行、问题清单 Q1-Q6、结论已发 lead；本报告落盘。
- 下一步第一件事（若 lead 派 Q1 修）：改 `src/PrtScr.Capture/WgcWindowCapture.cs:148-171`，把 `FromAbi` 与 `Marshal.Release(graphicsDeviceAbi)` 包进 try/finally（或 catch 补 Release），然后跑整包 build + Core.Tests（≥77）+ format。
- 下一步该读的文件：本报告 2.2/2.3 出口表；`src/PrtScr.Capture/WgcWindowCapture.cs:128-173`、`:334-413`；`src/PrtScr.Capture/WindowCapture.cs:211-353`。
- 本单未改任何源码；采集层 4 文件仍是他人未提交改动（P-08/P-09a/P-07b），行号以当前工作树为准。

## 4. 风险与遗留

1. 未运行构建/测试/--smoke：结论基于代码走查；GDI/D3D 对象计数无真机证据。
2. Q1/Q2/Q4 均为异常/并发窄窗口，代码可定位但难以构造复现；未做句柄/GDI 对象计数对比。
3. Q3 在 App 不传 token 的前提下无实际影响；接入会话取消后需复测「取景取消不弹失败提示」。
4. Q6 仅按 D3D11 文档推断风险，未做并发压测。
5. 复核基线与行号会随后续提交漂移；合并/重排 P-08/P-09a/P-07b 后需重核 Q1/Q2 行号。
6. 另：`ScreenCapturer.cs:52-55` 未检查 BitBlt 返回值（静默全黑图），不属本批资源问题，建议另立小单。

## 5. 待确认

1. Q2 采用哪种修法（双检锁/占位/接受单线程约束），是否并入 P-09a 补丁单。
2. Q3 是否要求 `CaptureDetailedAsync` 入口预判 token（与 WGC 对齐）；若要求，归哪张单（`WindowCapture.cs` 属 P-07b 写域）。
3. Q1/Q4 是否本批修，还是记入 v1.3.1 遗留清单。
4. 是否需要为 Q6 起「D3D11 共享 immediate context 并发」后续单（只报告、不动代码）。
5. `ScreenCapturer.cs:52-55` BitBlt 返回值未检查是否另立单。

## 主人实测步骤（真实按键，勿用合成热键）

1. **被遮挡窗口仍能截**：启动 IshWY → 打开记事本，用浏览器盖住它 → 按 F1 → 窗口模式单击记事本 → 预期截到记事本本体（浏览器不在图里）；日志成功行带来源（WGC/PrintWindow），无「窗口响应较慢」以外的异常。
2. **WGC 降级路径**：对一个 PrintWindow 取不到内容的窗口（播放中的视频/硬件加速应用）按 F1 选它 → 预期成功并提示「窗口响应较慢，已改用 WGC 抓取窗口内容」，日志出现「降级成功」。
3. **Busy 不降级**：用 Sysinternals pssuspend 挂起一个记事本 → F1 选它 → 预期 2 秒内给出超时/失败提示而不是屏幕裁切；同一窗口立刻再按 F1 → 预期提示「该窗口上一张截图还在处理中，请稍后重试」，历史不落条、剪贴板不写。
4. **取消语义**：F1 进入取景后在窗口重建阶段立刻按 Esc → 预期取景关闭、不弹失败提示；日志出现「捕获已取消」而非「捕获超时」。
5. **资源回落（可选）**：连续 20 次「F1 → Esc」后，在任务管理器看 GDI 对象/句柄数是否回到初始水平（验证常规路径晚到帧与池释放；Q1/Q4 为异常路径，正常操作看不到）。

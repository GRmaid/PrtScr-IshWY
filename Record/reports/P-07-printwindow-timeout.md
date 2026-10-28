# P-07（A03）PrintWindow 受控期限：2000ms 超时、晚到帧释放、强制单飞

- 单号：task-8 / P-07 / A03
- 状态：完成（本单代码 + 门禁 + 报告）；task-9（P-08）、task-10（P-09a）按 lead 指令暂缓
- 基线：HEAD 0b1ca41（v1.3.0-2-g0b1ca41）
- 写域：仅 `src/PrtScr.Capture/WindowCapture.cs`（write_scopes 内，未越界）

## 做了什么

1. **给 PrintWindow 加 2000ms 受控期限，调用方必能脱身。** 原实现 `Task.Run(() => PrintWindow(...))` 只是换线程，PrintWindow 是同步 P/Invoke，一旦目标窗口无响应就会永久挂住调用方（A-2 前置确认结论 A03）。现在 `CaptureDetailedAsync` 用 `Task.WhenAny(printTask, Task.Delay(2000, ct))`，超期立即返回并回退 WGC，不再让取景会话陪着无限等。
2. **新增可判别的原因模型（不静默返回黑图）。** `WindowCaptureSource {None, PrintWindow, Wgc}` 注明实际来源；`WindowCaptureFailure {None, Busy, Timeout, PrintWindowUnavailable, Canceled, InvalidBounds, Error}` 与 `WindowCaptureResult.PrintWindowTimedOut`、`Detail` 供上层标注「这张图是超时降级来的」。超时且 WGC 也失败时返回 `Failure=Timeout`；超时但 WGC 成功时返回 `Source=Wgc, PrintWindowTimedOut=true, Detail="PrintWindow 超过 2000ms，已改用 WGC"`。P-04 可直接据此标来源。
3. **晚到帧释放。** 每次 PrintWindow 建一个 `PrintFlight`（HWND + `volatile bool Abandoned`），由 `StartPrintWindow` 挂 `ContinueWith(ExecuteSynchronously)`：任务正常结束且调用方已脱身 → `t.Result?.Dispose()`（晚到帧唯一释放点）；异常 → `_ = t.Exception` 观察掉，避免 UnobservedTaskException。`AbandonLate` 另补一次幂等 Dispose，覆盖「后继已先跑过」的竞态。
4. **强制单飞（按 HWND）。** `ConcurrentDictionary<nint, byte> _printWindowFlights`：在飞时再次调用立即返回 `Busy`，不排队、不走 WGC。关键修正：**门闩由 PrintWindow 线程真正返回时的后继释放，不在调用方 finally 释放**——否则超时脱身后门闩提前放开，每按一次 F1 都会再叠一条挂起线程。
5. **兼容旧签名，不改 App 层。** `CaptureAsync` 保留 `Task<Bitmap?>`，内部改调 `CaptureDetailedAsync`；`CaptureWindow` 保留同步入口（启动预热用，无期限、共用门闩、在飞返回 null）。日志走 `Log ?? WgcWindowCapture.Log`，App 现有接线（`src/PrtScr.App/App.xaml.cs:56`）无需改动。
6. **未改** `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs`（会话取消语义归 P-04）、未改 csproj/最低版本/README/.gitignore、未新增依赖、未 git commit。

## 证据

### 文件与行号（src/PrtScr.Capture/WindowCapture.cs，当前 394 行）

| 内容 | 位置 |
| --- | --- |
| `WindowCaptureSource` / `WindowCaptureFailure` / `WindowCaptureResult` | :13 / :21 / :40 |
| 超时常量 `PrintWindowTimeoutMs = 2000` | :90 |
| 日志入口 `Log`（汇入 `WgcWindowCapture.Log`） | :96 |
| 单飞门闩 `_printWindowFlights` / `TryEnterFlight` / `ExitFlight` | :104 / :106 / :107 |
| `PrintFlight`（Hwnd + `volatile Abandoned`） | :110 |
| `StartPrintWindow`（LongRunning + 一次性后继） | :124 |
| 晚到帧 Dispose | :139 |
| 后继释放门闩 | :146 |
| `CaptureDetailedAsync` | :174 |
| 非法句柄/尺寸 → `InvalidBounds` | :182-183 |
| 单飞失败 → `Busy` | :189-190 |
| 启动线程失败 → 记日志 + `ExitFlight` → 走 WGC | :207-211 |
| 超时期限 `Task.WhenAny(printTask, Task.Delay(2000, ct))` | :218 |
| 取消 → `AbandonLate` + `Canceled` | :226-228 |
| 超时 → `AbandonLate` + 回退 WGC | :231-235 |
| PrintWindow 成功 → `Source=PrintWindow` | :245 |
| WGC 回退调用 | :253 |
| WGC 成功 → `Source=Wgc`，超时则 `PrintWindowTimedOut=true` + Detail | :271-275 |
| WGC 也失败 → `Timeout` 或 `PrintWindowUnavailable` | :278-282 |
| finally：仅「线程未起」时兜底 `ExitFlight` | :287-291 |
| `AbandonLate`（置 Abandoned + 已完成则补 Dispose） | :299-307 |
| 同步 `CaptureWindow`（预热入口，共用门闩） | :316-331 |
| `CaptureWindowCore`（原 PrintWindow 实现下移，失败 Dispose 保留） | :334，:370 |

### 失败出口 → 资源影响 / 释放动作对照表

| 失败出口 | 返回 | 资源影响 | 释放点 |
| --- | --- | --- | --- |
| 句柄/尺寸非法 | `InvalidBounds` | 未创建任何资源 | 无 |
| 同一窗口已有 PrintWindow 在飞 | `Busy` | 本次未创建资源 | 无（门闩归在飞者） |
| PrintWindow 线程启动失败 | 走 WGC 回退 | 无原生调用在飞 | :210 `ExitFlight` |
| 调用方取消（PrintWindow 未返回） | `Canceled` | 底层线程仍在飞，晚到 Bitmap 待回 | :226 `AbandonLate` → 线程返回时 :139 Dispose + :146 ExitFlight |
| 超时（PrintWindow 未返回） | 回退 WGC；WGC 成功则 `Source=Wgc`，失败则 `Timeout` | 同上 | :233 `AbandonLate` → :139 / :146 |
| PrintWindow 返回失败/纯黑 | WGC 回退，失败则 `PrintWindowUnavailable` | 底层已返回，无残留帧 | :370 内部失败 Dispose；门闩 :146 |
| WGC 回退成功 | `Source=Wgc`（超时则 `PrintWindowTimedOut=true`） | 无 | 无（Bitmap 交给调用方） |
| 同步预热入口在飞 | 返回 null | 未创建资源 | 无（门闩归在飞者） |
| 同步预热入口完成 | Bitmap | 无 | :330 自身 finally ExitFlight |

### 门禁（本单代码最终态后重跑）

```
dotnet build src/PrtScr.App/PrtScr.App.csproj -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out/
  → 已成功生成。0 个警告 0 个错误（00:00:13.71）

dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo
  → 已通过！失败 0，通过 77，已跳过 0，总计 77（69ms）

dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
  → rc=0（无输出）

dotnet build PrtScr.sln -c Debug --nologo -t:Compile
  → 0 个警告 0 个错误（00:00:01.07，热态）
```

**关于 `-t:Compile` 的如实说明（重要）**：该命令只跑 Compile/CoreCompile，不跑 `PrepareResources → MarkupCompilePass1`（WinUI XAML 生成的 g.cs 挂在 `PrepareResourcesDependsOn`，见 `~/.nuget/packages/microsoft.windowsappsdk/1.6.250108002/build/Microsoft.UI.Xaml.Markup.Compiler.interop.targets:241-249`）。因此 App 项目冷编译时它会报 624 个 CS0103（`HomeSection`/`InitializeComponent` 等 XAML 成员缺失）+ CS5001（无 Main），那是**命令本身的盲区，不是源码错误**；只有 App 的 csc 恰好被判 up-to-date 跳过时才「通过」，属假绿。证据日志：`.tmp/sln-compile.log`（首 15 行含 `CSC : error CS5001`）。另外排障中曾发现 `src/PrtScr.App/obj/Debug/net10.0-windows10.0.22621.0/win-x64/MainWindow.g.cs` 为 0 字节（旁边有 48645 字节的 `.backup`），整包 App 构建后恢复。**建议后续编译门禁以整包 App OutDir 构建为准，`-t:Compile` 仅作整包构建后的快速增量复查。**

### 工作树与写域确认

`git status --porcelain` 中属于本单的改动只有：
```
 M src/PrtScr.Capture/WindowCapture.cs   （+314 行）
```
其余 `M` 项均在 `src/PrtScr.App/Views/NativePinWindow*.cs`、`PinManagerWindow.cs`、`docs/HANDOFF.md`，属 maid-impl / lead 并行改动；`Record/` 与 `.tmp/` 为未跟踪产物（报告与构建日志）。未动 `.gitignore`、未 git commit。

## 进行中（交接用）

- **已完成**：P-07 代码（2000ms 受控期限、晚到帧释放、按 HWND 单飞、可判别失败原因、旧签名兼容）+ 全部门禁（App OutDir 0/0、Core.Tests 77/77、format rc=0、`-t:Compile` 0/0 热态）+ 本报告。
- **下一步第一件事**：由 lead 决定是否/何时开 task-9（P-08）。若开，直接改 `src/PrtScr.Capture/WgcWindowCapture.cs`，把窗口条目从 `GraphicsCaptureItem.TryCreateFromWindowId` 换成已声明的 interop `CreateForWindow`（该声明在 :31-41，实现模板照抄同文件 `CreateItemForMonitor` 的 :409-442），并给 :140-143 的 catch 补 HRESULT 日志。task-9、task-10 当前均未 claim、未改代码。
- **下一步该读的文件**：`src/PrtScr.Capture/WgcWindowCapture.cs`（窗口捕获入口、interop 声明、2s 帧等待与池管理）；`Record/reports/A2-precheck-A03-A04-A10.md`（A04 官方文档证据与 A10 泄漏清单）；本报告。
- **P-04 需要的接口契约**：调用 `WindowCapture.CaptureDetailedAsync(window, ct)` 可得 `Source`（PrintWindow/Wgc）与 `Failure`（Busy/Timeout/Canceled/...）以及 `PrintWindowTimedOut`，用于在会话结果上标注「来源=WGC 且因 PrintWindow 超时降级」。旧 `CaptureAsync` 仍返回 `Bitmap?`，拿不到原因。

## 风险与遗留

1. **超时不能终止目标窗口的 PrintWindow**：2000ms 只是让调用方脱身，底层线程会残留到系统自行返回为止；这期间门闩被占，同一窗口的新请求得到 `Busy`（不排队、不叠加线程）。这是设计上的硬限制，报告与代码注释均已写明。
2. **Busy 对上层是新增语义**：P-04 若不处理 `Busy`，用户连按 F1 时可能看到「本次未取到图」——需要 P-04 决定提示或重试策略（本单不改 App 层）。
3. **调用方尚未消费新原因**：`CaptureService.cs:201`、`CaptureOverlayWindow.Capture.cs:232/:247` 仍走 `CaptureAsync`，来源标注要等 P-04 接 `CaptureDetailedAsync`。
4. **同步预热入口 `CaptureWindow` 无期限**（`MainWindow.xaml.cs:148`）：启动预热若遇挂起窗口仍会占着该 HWND 的门闩；预热结果本就被丢弃，属既有 App 层问题（P-09b 范围），本单只保证共用门闩不叠线程。
5. **未实测真实挂起窗口**：本单未构造「目标窗口不响应 PrintWindow」的现场，超时分支尚未在真机上触发验证（门禁只证明编译/测试/格式）。建议主人实测（见下）。
6. **`-t:Compile` 假绿风险**：已在上文说明，后续门禁请以整包构建为准。

## 待确认

1. P-04 是否采用 `CaptureDetailedAsync` 并在 UI/会话结果上标注来源？`Busy` 的呈现由谁定？
2. 2000ms 是否合适（正常 PrintWindow 数毫秒～数十毫秒；取景窗在捕获前已隐藏，拉长到 5s 会让用户空等）——如主人实测体感需要调整，只需改 `PrintWindowTimeoutMs` 一处。
3. 是否把「超时降级」计入诊断日志上报（当前只写 `WgcWindowCapture.Log` sink 的 Trace，不弹 UI）。
4. task-9（P-08）与 task-10（P-09a）的排期——本单交付后按 lead 指令暂缓，等待重新派单。

## 主人实测步骤

1. **正常窗口截图不受影响**：启动 IshWY.exe，按 F1 进入窗口模式，选中一个普通窗口（如资源管理器）回车截图 → 预期立即出图、与原行为一致，无卡顿、无黑图。
2. **遮挡窗口仍能抓本体**：用另一个窗口完全遮住目标窗口，按 F1 在窗口模式下选中被遮窗口回车 → 预期仍能拍到被遮窗口内容（PrintWindow/WGC 回退链路正常）。
3. **连按 F1 不叠加卡死**：连续快速按 F1 进入取景再 Esc 退出 5-10 次 → 预期每次都能正常进出取景，任务管理器里 IshWY.exe 线程数不持续增长（单飞门闩生效）。
4. **（可选，需制造无响应窗口）**：打开一个会「未响应」的程序窗口，按 F1 窗口模式选中它回车 → 预期最多约 2 秒内返回（可能出 WGC 结果或提示未取到图），取景器不会无限卡住、Esc 仍可退出。
5. **回归**：F1 全屏/常规模式截图、贴图、OCR 各走一遍 → 预期与 v1.3.0 一致。

# P-09a（task-10 / A10 前半）：PrtScr.Capture 失败/取消/晚到路径资源释放

- 执行分支：HEAD 9c81858 + 未提交工作树（主功能提交 86d3541 = P-01 + P-07；P-08 改动在工作树）
- 写域：只改 `src/PrtScr.Capture/**` 与 `Record/reports/**`；未改 App 层、csproj/Directory.Build.props、.gitignore/README；未新增依赖、未 git commit、未杀 IshWY.exe、未跑 --smoke
- 本单改动：3 个文件（`WgcWindowCapture.cs` 753 → 853 行，`ScreenCapturer.cs` 36 行改动，`DesktopDuplicationCapture.cs` 80 行改动）；`WindowCapture.cs` 经核查**无需再改**（P-07 已完成，见证据 2.3）

## 1. 做了什么

按 A-2 前置确认的泄漏清单逐条修；核心原则：**每个获取点在失败/取消/晚到路径上都确定性释放，不依赖 GC 终结器**。

### 1.1 WGC 窗口捕获（`WgcWindowCapture.cs`）

1. **`GraphicsCaptureItem` 确定性释放**：`GraphicsCaptureItem` 没有 `IDisposable`（PowerShell 反射 Microsoft.Windows.SDK.NET.dll 确认），但 CsWinRT 的原生引用挂在 `IWinRTObject.NativeObject` 上。新增 `ReleaseCaptureItem(GraphicsCaptureItem? item)`（文件第 287-300 行）执行 `((IWinRTObject)item).NativeObject.Dispose()`，失败只记日志。
2. `CaptureWindowCoreAsync`：item 创建后的全部逻辑（设备检查、首帧、三个 catch）包进 `try`，新增 `finally { ReleaseCaptureItem(item); }`（第 273-276 行）。修掉「设备不可用 → return 时 item 泄漏」。
3. `EnsureMonitorPool`：item/pool/session 改为可空局部变量 + `registered` 标志（第 342-345 行）；成功后置 `registered = true`（第 395 行）；`finally` 中未注册则按 `session → pool → item` 逆序释放（第 403-412 行）。修掉「CreateItemForMonitor 成功但设备 null / CreateCaptureSession / StartCapture 抛异常 → 三者全漏」。
4. `RebuildMonitorPool`：Latest、Session、Pool、Item 逐个独立 try 释放（第 562-600 行），会话已失效不再阻止池和 item 的释放（第 599 行 `ReleaseCaptureItem(state.Item)`）。
5. `WaitFrameAfterAsync`：fallback 帧改为「从进入函数起归函数所有」，`try/finally` 统一释放（第 507-554 行；finally 第 549-553 行）；命中新帧与超时兜底两条正常返回路径都先置 `fallback = null` 再交出所有权（第 530-532、542-544 行）。修掉「`Task.Delay` 取消抛异常 / `IsOverlayResidue` 抛异常时兜底帧泄漏」。
6. `CaptureCoreAsync` 重写（第 684-751 行）：
   - `pool` 在任何可能抛异常的调用之前创建，外层 `finally { pool.Dispose(); }`（第 746-750 行）——`CreateCaptureSession` / 属性赋值 / `StartCapture` 抛异常时池也释放；
   - `session` 改为 `using var` 且在 try 内（第 707 行）；
   - `OnFrameArrived`：`frame = sender.TryGetNextFrame()`，`TrySetResult` 返回 false 时**当场 `frame.Dispose()`**（第 698-703 行）；
   - 首帧 `WaitAsync(2s, ct)` 超时/取消时，给 `firstFrame.Task` 挂一次性 `ContinueWith`，帧晚到时 Dispose（第 726-739 行，Dispose 在第 733 行），然后 rethrow——修掉「超时/取消后晚到首帧无人释放、占住帧池缓冲」。
7. `CopyFrameToBitmap`：`bitmap` 创建后若 `LockBits`/拷贝抛异常，`catch { bitmap.Dispose(); throw; }`（第 825-829 行）；`Unmap` 的 `finally`（第 833-836 行）现在覆盖 Map 成功后的全部路径。

### 1.2 屏幕 BitBlt（`ScreenCapturer.cs`）

- 先 `GetDC(null)` 并判空抛异常（第 36-38 行），**再**建 bitmap（第 42 行）——修掉「GetDC 失败时 bitmap 已建未释放」；
- bitmap 的 `Graphics.FromImage` / `BitBlt` / `ReleaseHdc` 全部包在 try 内，异常时 `bitmap.Dispose()` 再 rethrow（第 43-65 行）；
- `ReleaseDC` 仍在外层 `finally`（第 69-72 行），成功返回的 bitmap 交给调用方（第 67 行）。

### 1.3 DXGI Desktop Duplication（`DesktopDuplicationCapture.cs`）

- 输出枚举循环（第 135-158 行）：非目标 `IDXGIOutput` 立即 `Marshal.ReleaseComObject`（第 157 行）；`EnumOutputs` 失败但带出对象时也释放（第 144-145 行）；匹配到的交外层 `finally`（第 260 行）。
- `CopyTexture`（第 289-390 行）：`Map` 成功后把空指针检查、全零扫描、bitmap 创建与拷贝全部包进 `try`，`finally { Unmap(...); }`（第 380-384 行）——包括「整帧全零早退」也不会再跳过 Unmap；bitmap 的 `LockBits`/拷贝异常时 `bitmap.Dispose()` 再 rethrow（第 372-376 行）。

### 1.4 未改动但已核查

- `CreateItemForMonitor` 参数与显示器常驻池行为未改（只补 item 释放点）；
- `WindowCapture.cs`（P-07）晚到帧释放与单飞门闩完整（证据 2.3），本单未改；
- `CaptureService.cs` 两处 App 层泄漏属 P-09b，本单未碰（见 2.4）。

## 2. 证据

### 2.1 门禁（全绿）

| # | 命令 | 结果 |
|---|---|---|
| 1 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p09a -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p09a/"` | **0 警告 0 错误**，14.67s，`BUILD-GUARD: EXIT=0 elapsed=15s` |
| 2 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p09atest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` | **通过 77 / 失败 0 / 跳过 0**，77ms，EXIT=0 |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | **rc=0**（无输出） |
| 附 | `dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo` | 0 警告 0 错误，1.92s（逐批编译时反复验证） |

- 未用 `-t:Compile`（已知假绿：不跑 XAML MarkupCompilePass1，冷态 624 CS0103 + CS5001）。
- 构建经 `.tmp/build-guard.ps1` 串行化，避免与 P-04 并行构建竞争。

### 2.2 资源 → 获取点 → 释放点（四列表）

| 资源 | 获取点 | 释放点（文件:行） | 未覆盖原因 |
|---|---|---|---|
| PrintWindow 位图 | `WindowCapture.cs:341` | 失败 `WindowCapture.cs:367-371`；晚到帧 `WindowCapture.cs:132-146`（Dispose :139）、`AbandonLate` :299-306；成功交调用方 | 无（P-07 已完成，本单核查未改） |
| WGC 窗口 `GraphicsCaptureItem` | `WgcWindowCapture.cs:229` | `WgcWindowCapture.cs:273-276` → `ReleaseCaptureItem` :287-300（NativeObject.Dispose :294） | 无 |
| WGC 显示器池 item | `WgcWindowCapture.cs:348` | 成功存池 :378；重建 `:599`；启动失败 `:410` | 无 |
| WGC 显示器池 frame pool | `WgcWindowCapture.cs:362` | 启动失败 :409；重建 :592；窗口路径池 :749 | 无 |
| WGC 显示器池 session | `WgcWindowCapture.cs:364` | 启动失败 :408；重建 :583 | 无 |
| WGC 窗口帧池 pool | `WgcWindowCapture.cs:691` | `finally` :746-750（:749） | 无 |
| WGC 窗口 session | `WgcWindowCapture.cs:707` | `using` 作用域结束（早于 pool.Dispose） | 无 |
| WGC 首帧 `Direct3D11CaptureFrame` | `OnFrameArrived` `WgcWindowCapture.cs:700-701` | 正常 `using` :723；额外帧 :702；超时/取消晚到帧 :729-737（Dispose :733） | 无 |
| WGC 显示器 Latest 帧 | `WgcWindowCapture.cs:462-480`（RetainLatest） | TakeLatest 取走；重建 :576；`WaitFrameAfterAsync` finally :552 | 无 |
| WGC 兜底帧 fallback | `WgcWindowCapture.cs:516` | :530、:535、:542-543 置空后交出；异常/取消 finally :552 | 无 |
| WGC staging 纹理 | `WgcWindowCapture.cs:784` | `finally` :838-841（:840） | 无 |
| WGC 输出 bitmap | `WgcWindowCapture.cs:802` | 失败 `:825-829`（:827）；成功交调用方；Unmap :835 | 无 |
| WGC D3D texturePtr / access | `WgcWindowCapture.cs:759` / :756 | :845 / :850 | 无 |
| BitBlt bitmap | `ScreenCapturer.cs:42` | 异常 :61-65（:63）；成功交调用方 :67 | 无 |
| BitBlt 屏幕 DC | `ScreenCapturer.cs:36` | `finally` :69-72（:71） | 无（GetDC 失败在建 bitmap 前抛出，见 :38） |
| DD 未匹配 `IDXGIOutput` | `DesktopDuplicationCapture.cs:138` | 非目标 :157；EnumOutputs 失败 :144-145；匹配的交 :260 | 无 |
| DD output1 原始指针 / duplication | `DesktopDuplicationCapture.cs:172` / :208 | `finally` :259 / :258 | 无 |
| DD 每帧 `IDXGIResource` | `DesktopDuplicationCapture.cs:209-225` | :244 `ReleaseFrame` + :245 `Marshal.Release` | 无 |
| DD staging 纹理 | `DesktopDuplicationCapture.cs:314` | `finally` :386-389（:388）；Unmap :383 | 无 |
| DD 输出 bitmap | `DesktopDuplicationCapture.cs:349` | 异常 :372-376（:374）；成功交调用方 | 无 |
| **App 层 fallback 全屏 Bitmap** | `src/PrtScr.App/Services/CaptureService.cs:199` | 未修 | **P-09b 写域**；当前树 :215-217：前台窗口 WGC 成功时 `fallback` 直接弃用未 Dispose |
| **App 层 frozen 冻结 Bitmap** | `src/PrtScr.App/Services/CaptureService.cs:121` | 未修 | **P-09b 写域**；当前树 :121-155：窗口枚举/Prepare/BeginCaptureMode 抛异常时 catch 只清 `_isCapturing` 后 rethrow，frozen 未释放 |

### 2.3 失败出口 → 行为 → 释放点

| 失败出口 | 返回/行为 | 释放点 |
|---|---|---|
| 窗口 item 创建失败 | `ItemCreationFailed` + HRESULT/Detail | item 为 null，无需释放 |
| WGC 设备不可用 | `DeviceUnavailable` | item :275 |
| `CreateCaptureSession` 抛异常 | 异常上抛给外层 catch | pool :749；item :275 |
| `StartCapture` 抛异常 | 同上 | session using :707；pool :749；item :275 |
| 首帧 2s 超时 | `NoFrame`（WGC 窗口路径） | 晚到帧 :733；session :707；pool :749；item :275 |
| 取消（token） | `Canceled` | 晚到帧 :733；session；pool；item |
| `CopyFrameToBitmap` 失败（LockBits/拷贝抛） | `CaptureFailed` | bitmap :827；Unmap :835；staging :840；texturePtr :845；access :850；pool；session；item |
| `EnsureMonitorPool` item null | 返回 false | 无 |
| `EnsureMonitorPool` 设备 null | 返回 false | item :410 |
| `EnsureMonitorPool` 建池/建会话/属性/StartCapture 抛 | 记日志返回 false | session :408；pool :409；item :410 |
| `RebuildMonitorPool` 任一步抛 | 不抛出 | Latest :576；session :583；pool :592；item :599 各自独立 |
| `ScreenCapturer` GetDC null | `InvalidOperationException` | 抛在建 bitmap 之前（:38） |
| `ScreenCapturer` Graphics/BitBlt/ReleaseHdc 抛 | 上抛 | bitmap :63；DC :71 |
| DD EnumOutputs 失败 | 返回 null（已枚举对象释放） | :144-145；outputObject :260 |
| DD 未匹配输出 / QI 失败 / DuplicateOutput 失败 | 返回 null | :157 / :259-260 / :258-260 |
| DD AcquireNextFrame 失败 | 返回 null | 每帧 :244-245；dupPtr/output1/outputObject :258-260 |
| DD Map 后全零早退 | 返回 null | Unmap :383；staging :388 |
| DD bitmap LockBits/拷贝抛 | 上抛到 CaptureCore catch | bitmap :374；Unmap :383；staging :388 |

### 2.4 P-07 遗留核查（`WindowCapture.cs`，未改）

- 晚到帧唯一释放点：`WindowCapture.cs:132-146`（`ContinueWith` 内 `t.Result?.Dispose()` :139），超时/取消脱身后门闩由原生线程结束时释放（:146）；
- `AbandonLate` :299-306 对已完成任务补一次 Dispose（幂等）；
- PrintWindow 核心失败路径 `finally` :367-371 释放 bitmap；参数非法提前返回（:338-339 无 bitmap）。
- 结论：P-09a 无新增改动项。

### 2.5 已核实的技术事实（写入报告供复核）

1. **`GraphicsCaptureItem` 无 `IDisposable`**：反射 `C:\\Users\\asus\\.nuget\\packages\\microsoft.windows.sdk.net.ref\\10.0.22621.57\\lib\\net8.0\\Microsoft.Windows.SDK.NET.dll` 得到接口 `ICustomQueryInterface / WinRT.IWinRTObject / IDynamicInterfaceCastable / IUnmanagedVirtualMethodTableProvider / IEquatable`，方法仅 As/CreateFromVisual/TryCreateFromWindowId/TryCreateFromDisplayId/FromAbi/add_Closed/remove_Closed/get_DisplayName/get_Size。
2. **确定性释放路径**：`WinRT.IWinRTObject` 与 `WinRT.IObjectReference` 均 public（WinRT.Runtime.dll），`IObjectReference.Dispose()` public；故 `((IWinRTObject)item).NativeObject.Dispose()`。
3. **CsWin32 的 `Map` 不返回 HRESULT**（`-p:EmitCompilerGeneratedFiles` 导出 207 个 .g.cs 核实，`Windows.Win32.ID3D11DeviceContext.g.cs:544-547` 为 `public unsafe void Map(...)` 内部 `.ThrowOnFailure()`）。因此 A-2 所说「Map 忽略 HRESULT」不是缺陷，失败会抛 `COMException`；新增的 `mapped.pData is null` 分支仅作兜底。
4. 释放顺序：item 的 `NativeObject.Dispose` 必须在 session/pool 之后（本单所有路径均满足：窗口路径先 `CaptureCoreAsync` 返回、再 item finally；显示器池先 session/pool、再 item）。

## 3. 进行中（交接用）

- **已完成**：全部计划编辑（WGC / ScreenCapturer / DXGI）、逐文件编译、三条门禁（sln+OutDir 0/0、Core.Tests 77/77、format rc=0）、本报告。
- **下一步第一件事**：`team_task_update(task_id="task-10", action="complete", expected_revision=<最新>, output=三行)`（三行：做了什么 / 验证了 / 风险与未做）。
- 下一步应读：本报告 2.2/2.3 表；`Record/reports/A2-precheck-A03-A04-A10.md` 第 2.4 节（原始泄漏清单）；`src/PrtScr.App/Services/CaptureService.cs:121-155、:199-219`（P-09b 待修，行号随 P-04 变动需重核）。
- 未做（明确排除）：App 层两处泄漏、`-t:Compile` 禁用、commit/push、GUI/--smoke 实测。
- 临时物（`.tmp` 不入 git）：`p09a-edit1.mjs`、`p09a-edit2.mjs`、`p09a-edit3.mjs`、`gen-cswin32/`、`probe-item.ps1`、`probe-winrt.ps1`、`probe-winrt2.ps1`；未删除，供复核。

## 4. 风险与遗留

1. **item 释放依赖 CsWinRT 内部结构**：`IWinRTObject.NativeObject` 是 CsWinRT 公共类型，但属于互操作实现细节；升级 Windows App SDK / CsWinRT 后需复核（若被移除会编译失败，不会静默失效）。
2. **item 释放与并发的窗口**：显示器池的 item 在 `RebuildMonitorPool` 释放；`GetLatestMonitorFrameAsync` 先取 state 再在锁外使用，理论上重建期间可能拿到已释放 item 的 state（既有并发语义，本单未改变）。`Map`/拷贝期间 item 不会被释放（窗口路径由 finally 保证，显示器路径由 state 生命周期保证）。
3. **DD 对未匹配 output 调 `ReleaseComObject`**：本函数内该 RCW 无其他引用，安全；若未来缓存 `IDXGIOutput` 或复用 adapter，需重审引用计数。
4. **ScreenCapturer 错误语义未改**：BitBlt 返回值仍不校验（失败仍可能返回黑图，由上层 IsNearlyBlack 判定）——本单只修泄漏，不改变错误语义。
5. **未实测**：仅为构建/单测层验证；未用 GUI、未跑 --smoke（会写主人真实 data/）、未用 PerfView/句柄计数做释放验证。资源释放的最终确认依赖主人实测（见下）。
6. **App 层两处未修**：`CaptureService.cs:215-217`（fallback 弃用）、`CaptureService.cs:121-155`（frozen 异常路径）；属 P-09b，且该文件正被 P-04 修改，行号交付时需重核。
7. **日志量**：新增释放失败日志只在异常时触发（`WGC: 释放 GraphicsCaptureItem 失败`），不改变正常路径日志。

## 5. 待确认

1. P-09b 派单时请先按 P-04 落地后的 `CaptureService.cs` 重核行号（当前 :215-217 / :121-155 是 P-04 编辑中的工作树状态；A-2 原文锚点 :157 / :173-175 是 HEAD 0b1ca41 状态）。
2. 是否需要在 Core.Tests 之外补「资源释放」自动化测试（当前 77 条不覆盖 Capture 层，本单未改测试）；建议另立单。
3. `.tmp` 下的探针与编辑脚本是否保留：建议保留到本批实测通过，之后可清理（.tmp 不入 git）。
4. 本单与 P-04 并行改动同在工作树，提交时需由 lead 统一分批（本单仅 3 个 Capture 文件）。

## 主人实测步骤（请用真实按键，3-5 条）

1. **窗口捕获重复取景**：打开 IshWY，对同一个被遮挡的窗口连续按 F1 → 选窗截图 10 次（每次完成后 Esc 退出取景）。预期：10 次都出图，任务管理器里 IshWY 工作集不持续上涨。
2. **超时脱身**：用 Process Explorer 或任务管理器「挂起」某个记事本窗口（或用调试器挂起），F1 → 窗口模式选中该窗口。预期：2 秒内取景界面脱身（不卡死整个 UI），日志出现「WindowCapture: 超时」，若 WGC 可用则仍能出图。
3. **取消路径**：F1 进入取景后立刻 Esc 取消，连续 10 次；然后再正常截一张。预期：功能正常、无异常弹窗、内存平稳。
4. **多显示器（有副屏时）**：把取景窗口拖到副屏，反复 F1 截图 10 次。预期：副屏也能出图，工作集稳定。
5. **全屏/游戏路径（若方便）**：对全屏视频或游戏按 F1。预期：能出图；若 WGC 黑屏应自动降级到 DXGI/BitBlt 而不是黑图；日志中无异常刷屏。

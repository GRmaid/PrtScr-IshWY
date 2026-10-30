# P-08（A04）WGC 窗口捕获改走 interop CreateForWindow

- 单号：task-9 / P-08 / A04
- 状态：完成（代码 + 门禁 + 报告）；task-10（P-09a）未开工
- 基线：HEAD 9c81858（docs 提交），主功能提交 86d3541（P-01 + P-07）
- 写域：仅 `src/PrtScr.Capture/WgcWindowCapture.cs`（write_scopes 内），另写本报告；未动 App 层、csproj、Directory.Build.props、.gitignore、README，未新增依赖，未 git commit

## 做了什么

1. **窗口条目改走 `IGraphicsCaptureItemInterop::CreateForWindow`。** 原路径 `GraphicsCaptureItem.TryCreateFromWindowId`（改前 :133）官方最低 10.0.20348.0，而项目声明 19041，在 Win10 客户端上必然不可用。新实现 `CreateItemForWindow` 与既有 `CreateItemForMonitor` 同模板：`ActivationFactory.Get` → `Marshal.GetObjectForIUnknown(factory.ThisPtr)` → `interop.CreateForWindow(hwnd, IID_IGraphicsCaptureItem, out itemAbi)` → `GraphicsCaptureItem.FromAbi` → `Marshal.Release(itemAbi)` → `Marshal.ReleaseComObject(interop)`。interop 声明（vtable 顺序、GUID）是文件里原有的（:85-95），未改。
2. **旧吞异常路径整体删除。** 删掉 `CaptureWindowAsync` 里的 `TryCreateFromWindowId` 与私有 `CaptureItemAsync`（改前 :444-458，catch 全吞返回 null）。`TryCreateFromWindowId` 现在只出现在类注释（:71-74）里说明「为什么不用」，满足验收。
3. **新增可判别失败模型。** `public enum WgcWindowCaptureFailure { None, InvalidWindow, ItemCreationFailed, DeviceUnavailable, NoFrame, CaptureFailed, Canceled }`（:17-32）+ `public sealed class WgcWindowCaptureResult`（:38-65：`Bitmap` / `Failure` / `HResult` / `Detail` / `IsSuccess` / `IsCanceled`）。不再出现「静默 null，上层当成窗口捕获不可用」。
4. **详细入口与兼容入口并存。** `CaptureWindowDetailedAsync(WindowInfo, CancellationToken)`（:189-224）不抛异常，失败返回带 HRESULT/Detail 的结果；旧签名 `CaptureWindowAsync`（:179-183）保留，内部转调详细入口后返回 `.Bitmap`，既有调用方零改动。
5. **失败一律写日志（含 HRESULT）。** 统一格式 `Describe(ex) => "{ExceptionType} 0x{HResult:X8}: {Message}"`（:275-276）。CreateForWindow 抛出的 COMException 在 :614-619 记 `WGC: CreateForWindow(HWND=0x…)` 失败 <类型> 0x<HRESULT>: <消息>；返回空 item 记 E_POINTER（0x80004003，:592-597）。
6. **未改显示器捕获。** `CreateItemForMonitor`（:541-574）与显示器常驻池逻辑一字未动；`CaptureCoreAsync`（:623-660）与 `CopyFrameToBitmap` 的算法未改（其资源缺口归 P-09a，见「风险与遗留」）。
7. **CRLF 归一。** 写回后用 Node 把整文件统一为 CRLF（工作树约定），避免 `dotnet format whitespace` 的 ENDOFLINE 失败。

## 证据

### 文件与行号（src/PrtScr.Capture/WgcWindowCapture.cs，当前 753 行，+185/-22）

| 内容 | 位置 |
| --- | --- |
| `WgcWindowCaptureFailure` 枚举 | :17-32 |
| `WgcWindowCaptureResult`（Bitmap/Failure/HResult/Detail） | :38-65 |
| 类注释（CreateForWindow 1803+，不依赖 20348） | :67-75 |
| `IGraphicsCaptureItemInterop` 声明（GUID 3628E81B-…，CreateForWindow :92） | :85-95 |
| 兼容入口 `CaptureWindowAsync` → 详细入口 | :179-183 |
| 详细入口 `CaptureWindowDetailedAsync` | :189-224 |
| HWND==0 → `InvalidWindow` + 0x80070057 | :192-197 |
| 调用前已取消 → `Canceled` | :199-204 |
| 外层兜底 catch → `CaptureFailed` + `ex.HResult` | :212-223 |
| `CaptureWindowCoreAsync`（条目 → 设备 → 捕获） | :226-273 |
| `CreateItemForWindow` 失败 → `ItemCreationFailed` | :229-234 |
| 设备不可用 → `DeviceUnavailable` | :236-242 |
| 帧拷贝返回 null → `CaptureFailed` | :248-253 |
| 2s 无首帧 → `NoFrame` | :261-266 |
| core 其它异常 → `CaptureFailed` + HRESULT | :267-272 |
| `Describe(Exception)` | :275-276 |
| `CreateItemForMonitor`（未改，作为模板） | :541-574 |
| `CreateItemForWindow` 实现 | :581-621 |
| CreateForWindow 返回空 item → E_POINTER + 日志 | :592-597 |
| `FromAbi` 后 `Marshal.Release(itemAbi)` | :600-607 |
| `Marshal.ReleaseComObject(interop)` | :609-612 |
| CreateForWindow 异常 → HRESULT + Detail + 日志 | :614-620 |
| `CaptureCoreAsync`（未改；首帧 TCS / 2s WaitAsync / finally 退订+Dispose pool） | :623-660 |
| `TryCreateFromWindowId` 仅存于注释（说明移除理由） | :71-74 |

### 失败出口 → 返回值 / 日志 / HRESULT 对照表

| 失败出口 | 返回 Failure | HResult | 日志与位置 |
| --- | --- | --- | --- |
| HWND==0 | `InvalidWindow` | `0x80070057` | `WGC: 窗口捕获失败：HWND=0`（:194） |
| 进入前 token 已取消 | `Canceled` | 0 | `WGC: 窗口捕获已取消 HWND=0x…`（:201） |
| Task.Run 抛出取消 | `Canceled` | 0 | 同上（:214） |
| 外层未预期异常 | `CaptureFailed` | `ex.HResult` | `WGC: 窗口捕获异常 …`（:220） |
| CreateForWindow 抛 COMException | `ItemCreationFailed` | 原始 HRESULT | `WGC: CreateForWindow(HWND=0x…) 失败 <类型> 0x<HRESULT>: <消息>`（:618） |
| CreateForWindow 返回空 item | `ItemCreationFailed` | `0x80004003`（E_POINTER） | `… CreateForWindow 返回空 item（E_POINTER）`（:596） |
| D3D11 设备/上下文创建失败 | `DeviceUnavailable` | 0 | `WGC: 窗口捕获设备不可用 HWND=0x…`（:239） |
| 帧拷贝返回 null | `CaptureFailed` | 0 | `WGC: 首帧拷贝失败 HWND=0x…`（:250） |
| 2s 内无首帧（TimeoutException） | `NoFrame` | 0 | `WGC: 2s 内未收到首帧 HWND=0x…`（:263） |
| core 其它异常 | `CaptureFailed` | `ex.HResult` | `WGC: 窗口捕获异常 …`（:269） |
| core 中取消（重抛） | `Canceled`（外层 :212） | 0 | `WGC: 窗口捕获已取消 …`（:214） |
| 成功 | `None` | 0 | 无 |

### 条目获取与释放链（CreateForWindow）

| 步骤 | 位置 | 失败/释放行为 |
| --- | --- | --- |
| `ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")` | :587 | 抛异常 → catch :614 记 HRESULT |
| `Marshal.GetObjectForIUnknown(factory.ThisPtr)` | :588 | 抛异常 → catch（factory 由 ActivationFactory 缓存，未额外 Release，与既有 CreateItemForMonitor 同模式） |
| `interop.CreateForWindow(hwnd, IID, out itemAbi)` | :591 | 抛 COMException → 内层 finally :611 释放 interop → 外层 catch 记 HRESULT |
| `itemAbi == 0` | :592-597 | 记 E_POINTER，返回 null（无指针可释放） |
| `GraphicsCaptureItem.FromAbi(itemAbi)` | :602 | 抛异常 → :606 `Marshal.Release(itemAbi)` → :611 释放 interop |
| 成功返回 item | :602-607 | :606 释放 ABI 指针；:611 释放 interop RCW |

与 `CreateItemForMonitor`（:541-574）逐行同构，只是 `CreateForMonitor(hMonitor)` 换成 `CreateForWindow(hwnd)`、日志前缀不同、空 item 额外记 HRESULT。

### 门禁（本单代码最终态，全绿）

```
dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo
  → 已成功生成。0 个警告 0 个错误（00:00:02.65）

pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p08 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p08/"
  → 已成功生成。0 个警告 0 个错误（00:00:15.44）
  → BUILD-GUARD: EXIT=0 elapsed=15s tag=p08

pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p08test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"
  → 已通过！失败: 0，通过: 77，已跳过: 0，总计: 77（78 ms）
  → BUILD-GUARD: EXIT=0 elapsed=3s tag=p08test

dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
  → rc=0（无输出）
```

按 lead 要求未使用 `-t:Compile` 作门禁（该命令不跑 XAML MarkupCompilePass1，冷态会假红/热态假绿，P-07 报告已详述）。

### 19041 可用性依据

- 项目声明：`Directory.Build.props:5` `TargetPlatformMinVersion=10.0.19041.0`，本单未改。
- 旧 API `GraphicsCaptureItem.TryCreateFromWindowId` 的官方文档标注 **Introduced in 10.0.20348.0**（A-2 前置确认 A2-precheck-A03-A04-A10.md 已核对）；Win10 客户端最高 19045，低于门槛 → 旧路径在 Win10 全系不可用，这正是 A04。
- 新路径 `IGraphicsCaptureItemInterop`（GUID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356`）是 `Windows.Graphics.Capture` 自 **1803（17134）** 起随附的 COM interop 接口；`CreateForWindow` 与 `CreateForMonitor` 是同一 vtable 的相邻方法（本文件 :92/:94，签名顺序「句柄在前、IID 在后」有 2026-10-19 实测记录 :90-91）。本项目显示器捕获长期走同接口的 `CreateForMonitor` 且在开发机（Win11 26100）正常，窗口路径改用同接口的 `CreateForWindow`，不引入新的系统版本门槛。
- **仍未实测**：本机是 Win11 26100，没有 Win10 19041~19045 实机；未跑 GUI / `--smoke`（运行中的旧实例占全局热键，且 `--smoke` 会写主人真实 `data/`）。实机证据只能在主人机器上补，见文末实测步骤。

### 工作树与写域确认

```
git status --porcelain
 M Record/ProjectLog.md                      （非本单：主线记录）
 M src/PrtScr.Capture/WgcWindowCapture.cs    （本单唯一源码改动）
?? .tmp/                                     （构建产物/日志）
?? 主人修改素材/                              （主人素材）
```

`git diff --stat`：`1 file changed, 185 insertions(+), 22 deletions(-)`。改前该文件是干净状态；未动 `.gitignore`、README、csproj/Directory.Build.props，未 git commit。

## 进行中（交接用）

- **已完成**：P-08 代码 + 四条门禁 + 本报告。task-9 当前 revision 2、status=in_progress、owner=maid-capture。
- **任务状态工具限制**：本 fork 会话里 `agent_teams_claim_task/update_task` 报「no active team」，`team_task_get` 正常；`team_task_update(action=claim, expected_revision=2)` 返回 "not ready to claim"（单据已在 in_progress 且归我），故未重复 claim。完成时用 `team_task_update(task-9, expected_revision=<最新>, action=complete)`。
- **下一步第一件事（若 lead 开 P-09a/task-10）**：读 `src/PrtScr.Capture/WgcWindowCapture.cs:623-660`（`CaptureCoreAsync` 超时/取消后的晚到首帧）与 `:226-273`（`CreateItemForWindow` 成功但 `GetOrCreateDevice` 返回 null 时 item 未释放），先补这两处确定性释放，再按 A-2 清单处理 `CopyFrameToBitmap`、`ScreenCapturer`、`DesktopDuplicationCapture`、`EnsureMonitorPool`。
- **下一步该读的文件**：`Record/reports/A2-precheck-A03-A04-A10.md`（A10 泄漏点清单，含行号）、`Record/reports/P-07-printwindow-timeout.md`（同批风格与门禁口径）、`src/PrtScr.Capture/WgcWindowCapture.cs`。
- **若要接线 P-04**：读 `src/PrtScr.Capture/WindowCapture.cs:253`（仍调兼容入口 `CaptureWindowAsync`）与 `src/PrtScr.App/Services/CaptureService.cs`（App 层，非本单写域）。

## 风险与遗留

1. **App 层还拿不到可判别原因。** 唯一 WGC 窗口调用点 `src/PrtScr.Capture/WindowCapture.cs:253` 仍走兼容入口 `CaptureWindowAsync`（失败只给 null，原因在日志里）。新 API `CaptureWindowDetailedAsync` 已就绪，但接线属 P-04 / 后续单，本单写域外。因此「不再静默」目前体现在**日志 + 新 API**，App 界面层要等 P-04。
2. **条目泄漏（P-09a 范围）。** `CaptureWindowCoreAsync` 先创建 item（:229），再检查设备（:236）；设备不可用时直接返回 `DeviceUnavailable`，item RCW 未显式释放（最终靠 GC）。已在 A10 清单内，留给 task-10。
3. **晚到首帧（P-09a 范围）。** `CaptureCoreAsync` 的 `WaitAsync(2s, ct)` 超时/取消后，`OnFrameArrived` 仍可能 `TryGetNextFrame()` 产出帧并存入 TCS，该帧不会 Dispose（:642-658 只退订并 Dispose pool）。同样留给 task-10。
4. **未实机验证 Win10 19041 与真实窗口 CreateForWindow 运行时。** 若某版本接口缺失，会走 `ItemCreationFailed` + HRESULT 日志，不会崩溃；但「19041 上窗口模式可用」目前只有接口版本依据，没有实机证据。
5. **`IsBorderRequired` 空 catch 保留**（:636-640）：这是可选能力探测（旧系统不支持该属性），不是失败路径吞异常，与 A04 的「catch 不得整段吞掉」不冲突。
6. **显示器捕获未改**：`CreateItemForMonitor` 与常驻池逻辑保持原样，本单不引入显示器路径回归。
7. **未新增测试**：`PrtScr.Core.Tests` 是纯逻辑测试，WGC 依赖 Windows GUI/GPU，本单按既有基线保持 77 条不变。
8. **写域外未动**：`WindowCapture.cs` 的 PrintWindow 回退仍不消费 WGC 详细失败（它只把 WGC 当兜底），属 P-04 接线时的连带项。

## 待确认

- **lead**：是否把 `WindowCapture.cs:253` 接到 `CaptureWindowDetailedAsync` 以便 P-04 拿到 `Failure/HResult/Detail`？该改动在 `WindowCapture.cs`（P-07 写域），不在本单 write_scopes。
- **lead**：task-10（P-09a）是否连上「item 在设备不可用路径未释放」与「晚到首帧未 Dispose」两处（均已列在 A-2 清单，边界清楚）。
- **主人**：是否有 Win10 19041~19045 实机可做窗口模式实测；若无，本单只能以接口版本依据交付。

## 主人实测步骤

1. **窗口抗遮挡（核心）**：用新构建启动 IshWY（先退出旧实例，或在 `.tmp/out-p08/` 下运行），按 F1 → Tab 切到「窗口」模式 → 选一个被其他窗口遮住大半的记事本/资源管理器 → 单击。预期：截到该窗口本体内容（不是黑图、不是桌面、不是遮挡它的窗口）。
2. **日志无失败**：打开 App 的日志面板（或 `data/` 下最新日志），搜 `CreateForWindow`。预期：成功捕获时没有「失败 / 返回空 item」行；若失败，行内带异常类型与 `0x` 开头的 HRESULT，可直接区分原因。
3. **失效窗口/快速关闭**：按 F1 后选一个窗口，在单击前把它关掉（或选一个刚最小化的窗口）。预期：界面按既有逻辑降级/提示，不崩溃、不静默黑图；日志出现 `ItemCreationFailed` 或对应原因。
4. **Win10 实机（本单的主要目的）**：在 Win10 19041~19045 机器上重复步骤 1。预期：窗口模式能捕获（旧构建在此环境会走 PrintWindow 且可能不支持抗遮挡；新构建应走 WGC）。
5. **连续捕获**：连续按 F1 截 5 个不同窗口，观察任务管理器里 IshWY 的句柄数与内存。预期：无持续增长、无异常弹窗。

# P-09c WGC 设备链异常出口补释放（FromAbi 抛异常时 graphicsDeviceAbi 泄漏）

- 单号：task-16（P-09c，来自 task-15 复核 Q1）；写域：`src/PrtScr.Capture/WgcWindowCapture.cs`、`Record/reports/**`；执行者：maid-impl；日期：2026-10-30
- 基线：HEAD `9c81858`；`WgcWindowCapture.cs` 为 P-08/P-09a 的未提交工作树改动（`git diff --stat` 显示 1 file changed, 355 insertions(+), 82 deletions(-)，其中本单只新增 158-167 的 try/finally）。
- 环境：IshWY.exe(PID 29836) 仍占用 `src/PrtScr.App/bin/x64/Debug`，构建统一走 build-guard + 重定向 OutDir；本单未跑 --smoke。

## 1. 做了什么

- 修 Q1：`GetOrCreateDevice()` 中 `MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi)` 抛异常时，原代码只走 catch 释放 `dxgiDevice`/`device`/`context`，**漏还 `graphicsDeviceAbi`**，每次异常泄漏一个 WinRT ABI 引用（连带一个 D3D 设备引用）。
- 改法：把 FromAbi 调用包进独立 try，`finally` 中 `Marshal.Release(graphicsDeviceAbi)`；成功路径与异常路径都只释放一次，随后照旧缓存 `_cachedWinrtDevice = winrtDevice`。
- 边界：只动 `GetOrCreateDevice` 这一段；设备创建参数、d3d11 缓存策略、窗口/显示器捕获流程、失败枚举与日志文案均未改；未新增依赖、未改 .gitignore/README、未提交。
- 同批裁决落地（lead m00745）：Q2（EnsureMonitorPool 并发覆盖）本轮不改，写入本报告 §4「已知并发前提（当前不可达）」；Q6（D3D11 immediate context 跨线程共享）写入本报告 §4「B 批次评估项」；Q3 由 maid-capture 的 task-17 处理，本单未碰 `WindowCapture.cs`。

## 2. 证据

### 2.1 改动前后代码（`src/PrtScr.Capture/WgcWindowCapture.cs`，行号为当前工作树）

改前（HEAD 版本同 P-09a 工作树）：

```csharp
_cachedWinrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi);
Marshal.Release(graphicsDeviceAbi);
_cachedDevice = device;
```

改后：

```csharp
IDirect3DDevice winrtDevice;
try
{
    winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi);
}
finally
{
    // ABI 引用的所有权在本方法：无论 FromAbi 成功还是抛异常都只还一次。
    Marshal.Release(graphicsDeviceAbi);
}

_cachedWinrtDevice = winrtDevice;
_cachedDevice = device;
```

- 位置：`src/PrtScr.Capture/WgcWindowCapture.cs:158-167`（FromAbi 161、finally 内 Release 166、缓存赋值 169）；外层 catch 在 `:175-181`。
- 未改动的相邻行为：`CreateDirect3D11DeviceFromDXGIDevice` 失败分支 `:150-156` 仍释放 dxgi/device/context 后返回；外层 catch `:175-181` 仍释放 dxgi/device/context 后返回 `(null!, 0, 0)`。

### 2.2 为什么不会双重释放（owner 交接点逐条）

| 路径 | abi 释放次数 | dxgi/device/context | 说明 |
| --- | --- | --- | --- |
| FromAbi 成功 | 1（finally 166） | 外层正常路径 `:172` 释放 dxgi，device/context 转缓存 | 与改前 `:159` 的释放等价，只是搬进 finally |
| FromAbi 抛异常 | 1（finally 166） | 外层 catch `:177-179` 释放 dxgi/device/context | catch 不碰 abi，故不重复；`winrtDevice` 未赋值，不会写缓存 |
| CreateDirect3D11DeviceFromDXGIDevice 失败 | 0（abi 未产生） | `:152-154` 释放 | 与改动无关 |
| 设备/QI 失败 | 0 | `:143-144` 释放 | 与改动无关 |

- `MarshalInterface<T>.FromAbi` 不接管 ABI 指针所有权，调用方仍需 Release（与改前 159 行语义一致）；把 Release 放进 finally 只是保证「成功/异常各一次」。
- `_cachedWinrtDevice = winrtDevice` 是引用赋值，不会抛；即使它之后某行异常，外层 catch 也只释放 dxgi/device/context，不会二次释放 abi。

### 2.3 门禁（全部实跑，2026-10-30 20:5x-21:0x）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整解构建 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p09c -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p09c/"` | `BUILD-GUARD: EXIT=0`，已成功生成，**0 个警告 0 个错误**，14.84s |
| 核心测试 | `... -Tag p09ctest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` | `EXIT=0`，**失败 0、通过 89、跳过 0、总计 89**（基线 77，本批他人新增 12 条，未减少） |
| 格式 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | `FORMAT_EXIT=0`，无输出 |

- 行尾：改动插入后实测 `crlf=863 / lf=863 / bare=0`，文件仍为全 CRLF 且以 `}\r\n` 结束；format 未报 ENDOFLINE/FINALNEWLINE。
- 变更范围核对：本单只改 `src/PrtScr.Capture/WgcWindowCapture.cs` 一处；`git status --porcelain` 中其余改动（CaptureService.cs、MainWindow.*、CaptureHistoryService.cs、Record/ProjectLog.md 等）均为他人/前序未提交改动，本单未触碰。

### 2.4 复核依据（task-15 报告）

- 问题定义与原始证据见 `Record/reports/A-batch-capture-layer-review.md` §2.7 Q1：FromAbi 抛异常时 catch 未 Release graphicsDeviceAbi，属 P-09a「设备全链释放」唯一遗漏；同报告已核实其余设备释放链（`:143-144`、`:152-154`、`:172`、`:177-179`）配对正确。

## 3. 进行中（交接用）

- 已完成：Q1 代码修复；整解构建 0 警 0 错；Core.Tests 89/89；format rc=0；本报告落盘。
- 下一步第一件事：把本单与 A 批次其余未提交改动一起走统一门禁/提交（按路径 add，见下）；若 lead 要求，可把 `P-09a-capture-resources.md` 的报告状态从「全绿」改为「已复核 + P-09c 补丁」。
- 提交时只应包含：`src/PrtScr.Capture/WgcWindowCapture.cs`（含他人 P-08/P-09a 改动，需与 lead 约定提交边界）、`Record/reports/P-09c-device-release.md`；本单不负责整体提交。
- 下一步该读的文件：本报告 §2.2；`src/PrtScr.Capture/WgcWindowCapture.cs:128-183`；`Record/reports/A-batch-capture-layer-review.md` §2.7。

## 4. 风险与遗留

1. **异常路径未运行时构造**：FromAbi 抛异常依赖类型不匹配/OOM 等，无法在本机稳定复现；本次修复靠所有权走查 + 编译/测试/格式门禁，未做运行时泄漏计数（禁止把合成场景写进主人真实数据）。
2. **已知并发前提（当前不可达）**：`EnsureMonitorPool`（`WgcWindowCapture.cs:334-413`）在锁内只做 `ContainsKey` 快检，创建全程锁外、注册时覆盖写；两个线程并发首调同一 HMONITOR 会泄漏前一个 state 的 pool/session/item。当前所有调用都在 UI 线程串行（`CaptureService.StartCaptureAsync` / `GetLatestMonitorFrameAsync`），实际不可达；按 lead 裁决本轮不改，留给 B 批次「资源生命周期统一」。
3. **B 批次评估项**：窗口捕获 `CopyFrameToBitmap`（Task.Run 线程）与显示器池 `CopyFrameToBitmap`（调用线程）共用 `GetOrCreateDevice` 缓存的同一个 D3D11 immediate context（`:125-126`），并发 `CopyResource`/`Map` 未加锁；正常取景时序（先冻屏、后窗捕）不重叠，但「全屏快速截图 + 取景中的窗口捕获」组合未验证。建议 B 批次评估 context 锁或 deferred context。
4. 未新增自动化测试：异常注入难以在 PrtScr.Core.Tests 覆盖 WinRT interop；测试总数 89 ≥ 基线 77。
5. 行号相对当前未提交工作树；P-08/P-09a 若重排，本报告 §2.1 行号需重核。
6. 极小概率的相邻路径（本单未改，仅记录）：`DesktopDuplicationCapture.cs:92-96` device 非 0 但创建失败时不释放；`:149` GetDesc 抛异常时 output RCW 未释放；`:221-225` AcquireNextFrame 非超时失败且 resource 非空时未释放。

## 5. 待确认

1. 提交边界：`WgcWindowCapture.cs` 同时含 P-08/P-09a 与本单改动，是否随 A 批次统一提交由 lead 决定；本单只声明自己那 10 行。
2. 是否需要回写 P-09a 报告状态（建议追加一行「P-09c 已补 FromAbi 异常出口」而不是改写原文结论）。
3. Q6 是否另立 B 批次单（本报告只记录，不动代码）。

## 主人实测步骤（真实按键，勿用合成热键）

1. **正常窗口截图不回归**：启动 IshWY → 打开记事本 → F1 → 窗口模式单击记事本 → 预期截到记事本本体；日志无 `WGC: ...失败` 新增行。
2. **WGC 降级仍工作**：对播放中的视频窗口 F1 选它 → 预期提示「窗口响应较慢，已改用 WGC 抓取窗口内容」，截图成功。
3. **被遮挡窗口仍能截**：把记事本用浏览器盖住 → F1 选记事本 → 预期截到记事本本体（浏览器不在图内）。
4. **连续多次截图看资源**：连续 10 次「F1 → Esc」→ 任务管理器看 GDI 对象/句柄数是否稳定（正常路径不涉及本单异常分支，作回归观察）。
5. **失败路径不崩**：对已关闭/最小化的窗口快速连按 F1 两次 → 预期不崩溃、给出失败或 Busy 提示，历史不落异常条目。

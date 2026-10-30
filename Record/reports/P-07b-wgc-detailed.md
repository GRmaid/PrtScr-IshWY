# P-07b（task-12 / A02 续）WindowCapture → WGC 详细失败原因桥接

- 单号：task-12 / P-07b；写域：src/PrtScr.Capture/WindowCapture.cs、Record/reports/**
- 基线：工作树位于主功能提交 86d3541（P-01 + P-07）之后，含 P-04/P-08/P-09a 未提交改动
- 执行者：maid-capture；日期：2026-10-30

> **状态（2026-10-30 20:58）**：已收单（task-12 completed）。P-09b 合并树（frozenHandedOff 交接 + CaptureService.cs 以 CRLF 结尾）
> 上最终复跑：整解 `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → **rc=0**；
> Gate1 整包 OutDir 构建 → **0 警告 0 错误，14.46s，BUILD-GUARD EXIT=0**。
> 本单自始至终未修改 `src/PrtScr.App/**`。

## 1. 做了什么

1. **WGC 回退改走详细入口**：`src/PrtScr.Capture/WindowCapture.cs` 的 WGC 段由
   `WgcWindowCapture.CaptureWindowAsync`（返回 `Bitmap?`）改为
   `WgcWindowCapture.CaptureWindowDetailedAsync`（返回 `WgcWindowCaptureResult`），
   从而拿到 WGC 侧的 `Failure` / `HResult` / `Detail`。
2. **结果类型扩字段**：`WindowCaptureResult` 新增 `WgcFailure`（`WgcWindowCaptureFailure`）、
   `WgcHResult`（`int`）、`WgcDetail`（`string?`），以及便捷属性 `IsCanceled`；
   `Success` / `Failed` 工厂新增三个可选参数（默认 `None` / 0 / `null`），旧调用点零改动。
3. **取消归一**：WGC 侧 `WgcWindowCaptureFailure.Canceled` 归一为
   `WindowCaptureFailure.Canceled`（含防御性 `OperationCanceledException` 分支），
   不再落到 `PrintWindowUnavailable` / `Timeout`。
4. **粗粒度语义不变**：WGC 失败时 `Failure` 仍取 `Timeout`（PrintWindow 曾超期）或
   `PrintWindowUnavailable`（未超期），保持 P-04 在
   `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:392-408` 的既有分支行为；
   WGC 真实原因由三个 `Wgc*` 字段承载。**唯一**改变 `Failure` 取值的归一就是 Canceled。
5. 新增两个私有辅助：`DescribeWgcFailure`（枚举名 + HRESULT + Detail 的一行描述）、
   `ComposeWgcFailureDetail`（PrintWindow 侧结论 + WGC 侧原因拼进 `Detail`）。
6. 未改 `WgcWindowCapture.cs`（P-08/P-09a 定稿）、未改 App 层、未新增依赖、未改 csproj/最低版本、未 commit。
7. 报告指针：`Record/reports/P-07b-windowcapture-wgc-bridge.md`（task-12 描述里的文件名）指向本文件。

规模：`git diff --stat -- src/PrtScr.Capture/WindowCapture.cs` → `+89 −16`；当前 466 行，CRLF。

## 2. 证据

### 2.1 代码位置（当前 src/PrtScr.Capture/WindowCapture.cs）

| 物件 | 行号 | 说明 |
| --- | --- | --- |
| `WindowCaptureResult` | 42-105 | 私有构造带 3 个 WGC 参数 |
| `WgcFailure` | 71 | WGC 真实失败原因（`None` 表示未走 WGC 或 WGC 成功） |
| `WgcHResult` | 74 | WGC 原生 HRESULT；未失败为 0 |
| `WgcDetail` | 77 | P-08 产出的原始 Detail |
| `IsCanceled` | 83 | `Failure == Canceled` |
| `Success` / `Failed` 工厂 | 85-94 / 96-104 | 三个 WGC 参数可选，旧调用点无需改 |
| `CaptureAsync`（兼容旧签名） | 195-199 | 仍返回 `Bitmap?`，内部转调 `CaptureDetailedAsync` |
| `CaptureDetailedAsync` | 211-353 | 签名与返回类型未变（仍 `Task<WindowCaptureResult>`） |
| ├ 前置校验 | 214-221 | HWND=0 或尺寸非法 → `InvalidBounds`（WGC `InvalidWindow` 在桥上不可达） |
| ├ 单飞忙 | 223-228 | → `Busy`，不走 WGC |
| ├ PrintWindow 等待 | 251-279 | 2000ms 期限；超时 → `printWindowTimedOut=true` + `AbandonLate` |
| ├ PrintWindow 成功 | 281-282 | `Source=PrintWindow` |
| ├ 调用方已取消 | 284-285 | → `Canceled`（WGC 字段 `None`） |
| ├ WGC 详细调用 | 287-292 | `await WgcWindowCapture.CaptureWindowDetailedAsync(window, cancellationToken)` |
| ├ 防御 catch OCE | 294-300 | → `Canceled` + `WgcFailure=Canceled` |
| ├ 防御 catch 其它 | 301-310 | → 粗粒度 Timeout/PrintWindowUnavailable + `WgcFailure=CaptureFailed` + `ex.HResult` |
| ├ WGC 成功 | 312-324 | `Source=Wgc`，`WgcFailure=None`，透出 `wgc.HResult`/`wgc.Detail`；超时时 Detail 为「PrintWindow 超过 2000ms，已改用 WGC」 |
| ├ WGC 取消 | 328-336 | → `Failure=Canceled` + `WgcFailure=Canceled` |
| └ WGC 其它失败 | 338-343 | 粗粒度不变，Detail 拼 WGC 原因 |
| `DescribeWgcFailure` | 371-375 | `WGC {Failure}（HRESULT=0x{hr:X8}）：{Detail}` |
| `ComposeWgcFailureDetail` | 377-382 | PrintWindow 结论 + 「：」 + WGC 原因 |
| WGC 详细入口（未改） | WgcWindowCapture.cs:189-224 | 契约不抛异常；token 取消 → `Canceled` |
| WGC 兼容入口（未改） | WgcWindowCapture.cs:179-183 | 内部转调 Detailed；**当前仓内已无调用方** |

### 2.2 旧调用点 → 新调用点

| # | 旧（HEAD 86d3541 的 WindowCapture.cs） | 新（当前 WindowCapture.cs） | 变化 |
| --- | --- | --- | --- |
| 1 | 253 行 `viaWgc = await WgcWindowCapture.CaptureWindowAsync(window, cancellationToken)`（`Bitmap?`） | 292 行 `wgc = await WgcWindowCapture.CaptureWindowDetailedAsync(window, cancellationToken)`（`WgcWindowCaptureResult`） | 拿到 Failure/HRESULT/Detail |
| 2 | 旧 WGC 段 250-283 行：null → 粗粒度 Timeout/PrintWindowUnavailable，Detail 只有固定文案 | 312-343 行三分支：成功 → `Source=Wgc`；`Canceled` → `Failure=Canceled`；其它 → 粗粒度 + `Wgc*` 字段 | 原因可判别 |
| 3 | 旧代码无 WGC 侧 HRESULT | `WgcHResult` = `wgc.HResult`；防御 catch = `ex.HResult` | 新增 |
| 4 | App `CaptureOverlayWindow.Capture.cs:297/:339` 已用 `CaptureDetailedAsync`（P-04） | 未改 App；新增字段可被后续读取 | 未消费 `Wgc*` |
| 5 | `CaptureService.cs:280` 仍用兼容 `CaptureAsync` | 未改 | 拿不到新字段（P-09b/P-04 范围） |

### 2.3 Canceled 归一证据

- 枚举值：`WgcWindowCaptureFailure.Canceled = 6`（`src/PrtScr.Capture/WgcWindowCapture.cs:17-32`）。
- 归一分支在 `WindowCapture.cs:328-336`：判断 `wgc.Failure == WgcWindowCaptureFailure.Canceled` 后
  `return WindowCaptureResult.Failed(WindowCaptureFailure.Canceled, printWindowTimedOut, ComposeWgcFailureDetail(...), wgc.Failure, wgc.HResult, wgc.Detail);`。
- 防御分支：`WindowCapture.cs:294-300` 捕获 `OperationCanceledException` → `Failure=Canceled`、`WgcFailure=Canceled`、`WgcDetail` 为「WGC 调用抛出 OperationCanceledException」。
- 上层可判别：`IsCanceled`（`WindowCapture.cs:83`）与 `Failure==Canceled`。
- App 侧已有对应出口：`CaptureOverlayWindow.Capture.cs:399-401` → `CaptureOutcome.Abort(CaptureOutcomeStatus.Canceled, "取景已取消")`，不会弹失败提示。
- **可达性事实**：WGC 的 `Canceled` 只能来自传入的 `CancellationToken`（`WgcWindowCapture.cs:189-224` 契约），而当前 App 两处窗口捕获调用均未传 token（`CaptureOverlayWindow.Capture.cs:297/:339` 只传 window，默认 `default`）。因此该归一目前是防御性契约，等 P-04 把会话取消接到 token 后才真正生效；PrintWindow 侧的取消同理。报告如实写明，不宣称已实测该路径。

### 2.4 失败分类矩阵

| # | PrintWindow 结论 | WGC 结果 | `Failure`（粗） | `Source` | `PrintWindowTimedOut` | `WgcFailure` / `WgcHResult` / `WgcDetail` |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 成功 | 未调用 | `None` | `PrintWindow` | false | `None` / 0 / null |
| 2 | 非法句柄或尺寸 | 未调用 | `InvalidBounds` | `None` | false | `None` / 0 / null |
| 3 | 同窗已有在飞 | 未调用 | `Busy` | `None` | false | `None` / 0 / null |
| 4 | 等待期被调用方取消 | 未调用 | `Canceled` | `None` | false；超时后再取消 → **true** | `None` / 0 / null |
| 5 | 失败（false/全黑） | 成功 | `None` | `Wgc` | false | `None` / wgc.HResult / wgc.Detail |
| 6 | 超期（2000ms） | 成功 | `None` | `Wgc` | **true** | `None` / wgc.HResult / wgc.Detail；超时原因在顶层 `Detail`（`WindowCapture.cs:332`，P-07c 前为 :322），不在 `WgcDetail` |
| 7 | PrintWindow 线程未起 | 成功 | `None` | `Wgc` | false | `None` / wgc.HResult / wgc.Detail |
| 8 | 失败 | `Canceled` | `Canceled` | `None` | false | `Canceled` / wgc.HResult / wgc.Detail |
| 9 | 超期 | `Canceled` | `Canceled` | `None` | true | `Canceled` / wgc.HResult / wgc.Detail |
| 10 | 失败 | `ItemCreationFailed` | `PrintWindowUnavailable` | `None` | false | `ItemCreationFailed` / hr / detail |
| 11 | 失败 | `DeviceUnavailable` | `PrintWindowUnavailable` | `None` | false | `DeviceUnavailable` / hr / detail |
| 12 | 失败 | `NoFrame` | `PrintWindowUnavailable` | `None` | false | `NoFrame` / hr / detail |
| 13 | 失败 | `CaptureFailed` | `PrintWindowUnavailable` | `None` | false | `CaptureFailed` / hr / detail |
| 14 | 超期 | 上述任一失败 | `Timeout` | `None` | true | WGC 对应枚举 / hr / detail |
| 15 | 任意 | `InvalidWindow` | 桥上不可达 | — | — | 前置 216 行已挡 HWND=0，单飞 223 行要求 HWND 非 0 |
| 16 | 任意 | WGC 抛 OCE（防御） | `Canceled` | `None` | 保留 | `Canceled` / 0 / WGC 调用抛出 OperationCanceledException |
| 17 | 任意 | WGC 抛其它异常（防御） | `Timeout`/`PrintWindowUnavailable` | `None` | 保留 | `CaptureFailed` / ex.HResult / 异常文本 |

**P-07c 入口预判（P-07b 收尾后新增）**：token 在进入 `CaptureDetailedAsync` 时已取消 → 立即返回 `Canceled`，**不占单飞门闩、不发起 PrintWindow**（`WindowCapture.cs:216-222`）。该判断先于 `InvalidBounds`/`Busy`；起跑之后才取消的竞态不变（仍由 App 会话闸门丢弃），本表行 4/8/9 语义不变。

### 2.5 门禁（P-07b + P-09b 合并树，全绿）

| # | 命令 | 结果 |
| --- | --- | --- |
| 0 | `dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo` | 0 警告 0 错误，2.35s |
| 1 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p07b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p07b/"` | 0 警告 0 错误，17.54s，`BUILD-GUARD: EXIT=0`；队友补末尾换行后最终树复跑 `EXIT=0` 15.87s |
| 2 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p07btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` | 通过 77 / 失败 0 / 跳过 0，84ms，`EXIT=0` |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | **rc=0**（最终树） |

Gate3 过程（如实记录）：首次运行 rc=2，唯一违规为
`src/PrtScr.App/Services/CaptureService.cs(548,2): error FINALNEWLINE`——该文件属 P-04 改动、不在本单写域；
同期我的写域自检 `dotnet format whitespace src/PrtScr.Capture/PrtScr.Capture.csproj --verify-no-changes` 为 rc=0。
P-04 补齐文件末尾 CRLF 后整解复跑 rc=0，故最终树三条门禁全绿。

P-09b 落地后（`frozenHandedOff` 交接 + `CaptureService.cs` 以 CRLF 结尾）在合并树上最终复跑：
Gate3 整解 format **rc=0**；Gate1 整包 OutDir 构建 **0 警告 0 错误，14.46s，EXIT=0**（上表第 1 行首跑为 17.54s，均为 0/0）。

构建一律经 `.tmp/build-guard.ps1` 共享锁串行（P-04 并行构建），未使用 `-t:Compile`（已知假绿：不跑 XAML MarkupCompilePass1，冷态报 CS5001 + 624 CS0103，见 `Record/reports/P-07-printwindow-timeout.md`）。

## 3. 进行中（交接用）

- 本单已完成：代码、三条门禁、本报告；task-12 完成上报后无本单遗留改动。
- 唯一未落地的后续（**不在本单写域**）：让 App 真正用上这三个新字段——
  读 `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:392-408`（`ToAbort` 的失败分支）
  与 `311` / `348` 行（成功日志），把 `result.WgcFailure / WgcHResult / WgcDetail` 写进日志或提示；
  属 P-04 写域，需 lead 另派或并入 P-09b。
- 下一步第一件事（带路径）：读 `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:392-408`，
  在 `case WindowCaptureFailure.Timeout:` 与 `default:` 之间补一条基于 `result.WgcFailure` 的日志；
  只加日志、不改 UI 文案，避免影响 P-04 的验收。
- 下一步该读的文件：本报告 2.4 矩阵；`src/PrtScr.Capture/WindowCapture.cs:287-343`；
  `Record/reports/P-07b-windowcapture-wgc-bridge.md`（指针）。

## 4. 风险与遗留

1. **未实测**：未跑 GUI / 未跑 `--smoke`（会写主人真实 data/），窗口捕获与 WGC 失败路径均无真机证据；
   `PrtScr.Core.Tests` 77 条不覆盖采集层，本单无自动化测试。
2. **粗粒度语义保持不变**的代价：只读 `Failure` 的调用方（如 `CaptureService.cs:280` 的兼容入口）
   无法区分 WGC 的 ItemCreationFailed / DeviceUnavailable / NoFrame / CaptureFailed，
   必须读 `WgcFailure`；`CaptureAsync` 连 `WindowCaptureResult` 都不返回。
3. **App 尚未消费 WgcHResult**：日志里只能看到经 `ComposeWgcFailureDetail` 拼进 `Detail` 的文本
   （`CaptureOverlayWindow.Capture.cs:403` 会打印 Timeout 分支的 Detail），WGC 的原始 HRESULT 仍未输出。
4. **Canceled 归一的现实可达性**：App 两处调用未传 `CancellationToken`（`297`/`339` 行），
   该分支当前不可达；若 P-04 后续接 token，需要复测「取景中取消 → 不弹失败提示」。
5. **防御性 catch**：P-08 契约下 WGC 入口不抛异常；两条 catch 只防实现变化，
   其粗粒度归类（Timeout/PrintWindowUnavailable）未经过真实异常演练。
6. 工作树含队友未提交改动（P-04 等），本单未 commit、未 push；提交由 lead 统一分批。
7. `.tmp/p07b-edits.json`、`.tmp/p07b-apply.mjs` 为编辑脚本，`.tmp` 不入 git，可在提交前清理。

## 5. 待确认

1. 是否要派单让 App 消费 `WgcFailure / WgcHResult / WgcDetail`（写域在 `src/PrtScr.App`，需另派）。
2. 是否接受「Canceled 归一当前不可达、待 P-04 接 token 后复测」这一状态；
   若要求本单内可达，需要 App 侧传入 `CancellationToken`，超出写域。
3. 报告文件名：task-12 描述写 `Record/reports/P-07b-windowcapture-wgc-bridge.md`，
   lead 消息指定 `Record/reports/P-07b-wgc-detailed.md`；已双写（后者为正文，前者为指针），请确认以哪份为准。
4. 是否需要为 `WindowCaptureResult` 的新增字段补注释级契约测试（当前无测试工程，建议不补、改由主人实测）。

## 主人实测步骤（真实按键，勿用合成热键）

1. **被遮挡窗口仍能截**：启动 IshWY → 打开记事本并用浏览器盖住它 → 按 F1 → 窗口模式单击记事本 →
   预期截到记事本本体（浏览器不在图里），状态栏/提示无「窗口响应较慢」；日志无 WGC 回退失败。
2. **WGC 降级路径**：对一个 PrintWindow 取不到内容的窗口（如播放中的视频窗口/硬件加速应用）按 F1 选它 →
   预期成功且提示「窗口响应较慢，已改用 WGC 抓取窗口内容」；日志出现「降级成功」。
3. **取消不弹错误**：按 F1 进入取景后立刻按 Esc（在窗口重建阶段取消）→ 预期取景直接关闭、不出现失败提示；
   日志出现「Capture: ... 捕获已取消」（P-04 出口）而非「捕获超时」。
4. **无响应窗口**（可选，需构造）：用 Sysinternals pssuspend 挂起一个记事本进程 → F1 选它 →
   预期 2 秒内不再卡住取景，给出超时/降级结果；同一窗口立刻再按 F1 应得到「上一张截图还在处理中，请稍后重试」（Busy）。
5. **失败原因可见**：任意制造一次窗口捕获失败（如选一个正在关闭的窗口）→ 打开日志，
   确认失败行带「WGC <枚举名>（HRESULT=0x........）」形式的原因，而不是只有 null。

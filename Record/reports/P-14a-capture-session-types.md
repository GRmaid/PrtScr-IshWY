# P-14a CaptureSession/CaptureResult 第一阶段：Core 类型与结果矩阵

- 任务：`task-20`（rev2，owner=maid-impl），写域 `src/PrtScr.Core`、`src/PrtScr.Core.Tests`、`Record/reports`。
- 基线：HEAD `69616cc`（`chore(release-prep): 版本号 1.3.1 与 README 更新`）；工作树里的 `Record/ProjectLog.md`、`Record/plans/2026-10-28-整改总纲.md` 是既有改动，不是本单所为。
- 交付时间：2026-10-30 22:5x（本机）。本单只加类型与纯规则，零运行时行为变化。

## 1. 做了什么

### 1.1 新增文件（5 个，全部未跟踪）

| 文件 | 行数 | 内容 |
| --- | --- | --- |
| `src/PrtScr.Core/CaptureSessionState.cs` | 265 | 6 个枚举（会话状态/收尾原因/来源/引擎/失败分类/Terminate 判定）+ `CaptureRules` 的状态机与判定规则 |
| `src/PrtScr.Core/CaptureScenario.cs` | 88 | 结果矩阵 12 场景 `CaptureScenario`、期望 `CaptureExpectation`、`CaptureRules.ExpectationFor`（`CaptureRules` 的 partial 部分） |
| `src/PrtScr.Core/CaptureResult.cs` | 62 | 纯元数据 `CaptureResult`（会话 ID × 来源 × 引擎 × 失败 × 诊断）+ `Success`/`Failed`/`Canceled` 工厂 |
| `src/PrtScr.Core.Tests/CaptureRulesTests.cs` | 299 | 状态机、Terminate、EndReasonText、来源/取消/可恢复、提示文案、矩阵 12 行 |
| `src/PrtScr.Core.Tests/CaptureResultTests.cs` | 84 | `CaptureResult` 工厂、属性与参数校验 |

### 1.2 关键类型（行号为本单改后工作树）

- `CaptureSessionState`（`CaptureSessionState.cs:4`）：`Idle=0`、`Preparing=1`、`Selecting=2`、`Capturing=3`、`Committing=4`、`Completed=5`、`Canceled=6`、`Failed=7`。
- `CaptureEndReason`（`:32`）：`None/Cancel/ConfirmSuccess/ConfirmEnd/Ocr/Save/StartFailed`。
- `CaptureOrigin`（`:57`）：`None/Window/Normal/Screen`。
- `CaptureEngine`（`:73`）：`None/Wgc/PrintWindow/Dxgi/BitBlt/Fallback`。
- `CaptureFailure`（`:95`）：`None/InvalidTarget/Busy/Timeout/Canceled/PrintWindowUnavailable/WgcItemCreationFailed/WgcDeviceUnavailable/WgcNoFrame/WgcCaptureFailed/MonitorUnavailable/Unavailable/EncodeFailed/ClipboardFailed/HistoryFailed/Internal`。
- `CaptureTerminateDecision`（`:147`）：`Allowed/Duplicate/Invalid`；`CaptureRules.TerminateDecision`（`:178`）实现「终态重复收尾=Duplicate 幂等、Idle 只接受 StartFailed」。
- `CaptureRules`（`:161`）：`IsTerminal`（`:164`）、`IsActive`（`:168`）、`CanTransition`（`:192`，13 条合法迁移）、`EndReasonText`（`:211`，六个日志串）、`ToHistoryMode`（`:223`）、`IsDowngrade`（`:227`）、`ResolveScreenOrigin`（`:232`）、`CountsAsCancel`（`:236`）、`IsRecoverable`（`:239`，仅 Busy/Timeout）、`NoticeFor`（`:246`）。
- `CaptureExpectation`（`CaptureScenario.cs:47`）与 `ExpectationFor`（`:60`）：把设计稿 §4.2 的 12 行矩阵写成可测数据。
- `CaptureResult`（`CaptureResult.cs:10`）：`SessionId/Origin/Engine/Failure/PrintWindowTimedOut/HResult/Detail`；属性 `IsSuccess`（`:20`）、`IsCanceled`（`:23`）、`IsRecoverable`（`:26`）、`HistoryMode`（`:29`）、`Notice`（`:32`）；工厂 `Success`（`:35`）、`Failed`（`:44`）、`Canceled`（`:60`）。

### 1.3 设计稿到实现的映射（决策与理由）

- **来源枚举用 `Normal` 代替设计稿的 `Monitor`**：task-20 的验收口径写的是 `Window/Normal/Screen`，且三者在历史分类上等价（`Window → Window`，其余 → `Normal`）。`Screen` 单独保留，表示「窗口捕获失败后降级为屏幕区域裁切」，供 `IsDowngrade`/提示/日志使用。
- **`CaptureEngine` 增加 `Fallback`**：采集链兜底（多层降级后无法单独归因）需要一个显式值，避免把兜底结果伪装成单层引擎。
- **`CaptureFailure` 增加 `Unavailable`**：task-20 的口径要求通用不可用项；细分的 `MonitorUnavailable`/`Wgc*` 仍保留，提示文案对两者统一。
- **`CaptureResult` 落 Core 且只持元数据**：本单边界明确「不改 `src/PrtScr.Capture/**`、不改 csproj 引用」，而 `PrtScr.Capture` 目前不引用 `PrtScr.Core`；Core 又不允许引用 `System.Drawing`。因此第一阶段把结果收敛为纯元数据 record（Origin×Engine×Failure + 来源细节），带 Bitmap 的所有权类型留 P-14b。设计稿 §2.3 的 `IDisposable/TakeBitmap` 不在本单落地。
- **矩阵 12 行写进 `CaptureRules` 而不是测试内联**：矩阵是后续接线的唯一真源，测试只负责钉住它；`CaptureExpectation.WritesHistory/WritesClipboard` 用 `bool?`，`null` 只用于第 12 行（OCR 出口不写、另存出口写，无法用单一布尔表达）。
- **`CaptureResult.Failed` 拒绝 `None/Canceled`**：失败工厂必须带真实失败分类，取消走 `Canceled` 工厂，防止把取消误记为失败（设计稿 §4.1 的取消唯一定义）。

### 1.4 本单明确未做

- 未改 `src/PrtScr.App/**`（`CaptureSession` 接线属 P-14b）。
- 未改 `src/PrtScr.Capture/**`（含 `WindowCapture.cs`、`WgcWindowCapture.cs`）。
- 未改任何 `.csproj`、未新增 NuGet、未改 `.gitignore`、未 git commit/push。
- 未跑 `--smoke`，未启动/关闭正在运行的 IshWY。

## 2. 证据

### 2.1 门禁（全部实跑）

| # | 命令 | 结果 |
| --- | --- | --- |
| 1 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14a2 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14a/"` | `BUILD-GUARD: EXIT=0`，0 个警告 0 个错误，13.88s |
| 2 | `dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo` | `失败: 0，通过: 206，已跳过: 0，总计: 206`，88ms |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | 退出码 0，无输出 |

首轮构建（tag `p14a`）同样 EXIT=0、0 警 0 错；以后一次（含 Terminate 规则）为准。测试基线为本单开始时的 89 条（P-12 已并入），本单新增 117 条，总数 206。

### 2.2 测试增量明细

- `CaptureRulesTests.cs`：28 个测试方法展开为 108 个用例——合法迁移 13、非法迁移 22、终态无出边 1、`IsActive` 8、Terminate Idle 6 / 终态幂等 3 / 活动态 5、`EndReasonText` 7、`ToHistoryMode` 4、`IsDowngrade` 4、`ResolveScreenOrigin` 2、`CountsAsCancel` 4、`IsRecoverable` 5、`NoticeFor` 静默 2 / 精确文案 5 / Internal 2、矩阵 12、矩阵行数 1、矩阵一致性 2。
- `CaptureResultTests.cs`：9 个用例（3 个工厂、2 个属性组合、2 个参数校验、3 档来源历史分类）。
- 合计新增 117，`89 + 117 = 206`。

### 2.3 结果矩阵覆盖（12/12，逐行一条用例）

| # | `CaptureScenario` | 终态 | 失败 | 恢复取景窗 | 写历史 | 写剪贴板 | 提示 | PrintWindow 超时 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `ScreenSelection` | `Completed` | `None` | 否 | 是 | 是 | 否 | 否 |
| 2 | `WindowWhole` | `Completed` | `None` | 否 | 是 | 是 | 否 | 否 |
| 3 | `WindowRegion` | `Completed` | `None` | 否 | 是 | 是 | 否 | 否 |
| 4 | `PrintWindow` | `Completed` | `None` | 否 | 是 | 是 | 否 | 否 |
| 5 | `PrintWindowTimeoutWgc` | `Completed` | `None` | 否 | 是 | 是 | 是 | 是 |
| 6 | `PrintWindowTimeoutFailed` | `Failed` | `Timeout` | 是 | 否 | 否 | 是 | 是 |
| 7 | `Busy` | `Selecting` | `Busy` | 是 | 否 | 否 | 是 | 否 |
| 8 | `UserCancel` | `Canceled` | `Canceled` | 否 | 否 | 否 | 否 | 否 |
| 9 | `EncodeFailed` | `Failed` | `EncodeFailed` | 是 | 否 | 否 | 是 | 否 |
| 10 | `ClipboardFailed` | `Completed` | `ClipboardFailed` | 否 | 是 | 否 | 是 | 否 |
| 11 | `HistoryFailed` | `Completed` | `HistoryFailed` | 否 | 否 | 是 | 是 | 否 |
| 12 | `OcrOrSave` | `Completed` | `None` | 否 | 由出口决定 | 由出口决定 | 否 | 否 |

`MatrixRow_MatchesDesignContract` 用 `[Theory]` 12 个 `[InlineData]` 逐行断言；`Matrix_CoversAllTwelveScenarios` 钉住枚举恰好 12 个；`Matrix_OnlyCancelRowIsCanceledAndNeverWrites` 与 `Matrix_RecoverableRowsRestoreOverlayWithoutWriting` 钉住取消/恢复的横切约束。

### 2.4 工作树与写域

- `git status --porcelain` 与本单相关的新增文件只有 5 个：`src/PrtScr.Core/` 下 3 个 + `src/PrtScr.Core.Tests/` 下 2 个。
- 既有、非本单：`Record/ProjectLog.md`、`Record/plans/2026-10-28-整改总纲.md`、`.tmp/`、`大修改资料/`。
- `git diff --stat` 只列出上述两个 Record 文件（本单 5 个文件均为未跟踪），说明没有改到任何已跟踪源码。
- 已用 `grep` 确认 `src/PrtScr.App/**` 与 `src/PrtScr.Capture/**` 中没有本单新增符号的引用（`CaptureSessionState`/`CaptureResult` 等只出现在 Core 与 Core.Tests）。

## 3. 进行中（交接用）

### 已完成

- Core 三文件（枚举 + 规则 + 矩阵 + 结果元数据）与 Core.Tests 两文件，206/206 通过，整解构建 0 警 0 错，format 0。
- 设计稿 §2.2/§2.3/§4.2 的类型与矩阵已落地；§3 第 1 步的测试判据（迁移 ≥14、终态不可逆、Terminate 幂等、EndReasonText 六串、来源三档、Notice/Recoverable）均有对应用例。

### 下一步第一件事（P-14b）

先裁决 `CaptureResult` 的最终位置与 Bitmap 所有权：

1. 若要在 `src/PrtScr.Capture/CaptureResult.cs` 放带 `Bitmap` 的结果类，需要给 `PrtScr.Capture.csproj` 增加 `ProjectReference` 到 `PrtScr.Core`（本单边界禁止，P-14b 需单子明确授权）。
2. 若不改引用，则保留本单的元数据 `CaptureResult` 在 Core，另在 Capture 内定义只含 Bitmap 的 `CapturePayload`/`CapturedBitmap`，由 App 层组合二者。

然后读：`Record/plans/B1-capture-session-design.md` §2.3/§3 第 2 步、`src/PrtScr.App/Services/CaptureService.cs`、`src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs` 的 `SelectionCapture`/`CaptureOutcome`，把状态机与 `TerminateDecision` 接到现有 `_isCapturing`/会话闸门。

### 该读的文件

- `src/PrtScr.Core/CaptureSessionState.cs`（全部枚举与规则）
- `src/PrtScr.Core/CaptureScenario.cs`（12 行矩阵）
- `src/PrtScr.Core/CaptureResult.cs`（元数据结果）
- `src/PrtScr.Core.Tests/CaptureRulesTests.cs`（行为契约的可执行版本）

## 4. 风险与遗留

1. **`CaptureResult` 位置偏离设计稿**：设计稿把它放在 `PrtScr.Capture` 并持 Bitmap；本单受「不改 csproj 引用 + Core 不引 System.Drawing + 不改 Capture」三条边界约束，只能先落元数据版。P-14b 必须显式裁决，不能两个同名类型并存。
2. **矩阵第 12 行是复合行**：`OcrOrSave` 的两个写入标记为 `null`（由出口决定），测试只钉终态/恢复/提示。若后续要逐出口断言，应拆成 `Ocr` 与 `Save` 两行（会变成 13 行，需同步设计稿）。
3. **`NoticeFor` 的 `hresult` 参数当前未参与文案**：为设计稿签名预留；若 P-14b 想让提示带 HRESULT，需要补文案规则与测试。
4. **`CaptureFailure.Unavailable` 与细分项语义重叠**：调用方应优先用细分项，只有无法归因时才用 `Unavailable`；否则日志会丢失可诊断性。
5. **矩阵只是数据，尚未接线**：本单不改变任何运行时行为，P-14b 必须用 `ExpectationFor` 反向驱动实现，否则矩阵会与实现漂移。建议 P-14b 增加一条「实现分支必须命中矩阵」的走查清单。
6. **`CaptureRules` 用 partial 拆在两文件**：`ExpectationFor` 在 `CaptureScenario.cs`，其余在 `CaptureSessionState.cs`；查找单个方法时需按 partial 搜。若嫌分散，可合并，但会拉长单文件。
7. **未做运行时/GUI 验收**：本单没有可交互变更；主人实测只能是编译/测试/格式与代码走查（见下）。
8. **并发写者**：本单期间工作树有 lead 对 `Record/` 的改动；本单 5 个文件均为新增，未与任何已跟踪文件冲突。提交时按路径 add 这 5 个文件，不要 `git add -A`。

## 5. 待确认

1. **P-14b 的 `CaptureResult` 落点**：加 `PrtScr.Capture → PrtScr.Core` 项目引用并把类型移到 Capture（设计稿原意），还是保留 Core 元数据 + Capture `Bitmap` 载荷（不动 csproj）？推荐后者，改动面小且不破坏「Core 纯规则」。
2. **`Origin.Monitor` 是否彻底放弃**：本单用 `Normal` 覆盖设计稿 `Monitor` 的语义；若后续日志/指标需要区分「显示器池」与「屏幕裁切」，需要再加一个枚举值并重跑矩阵（现有 12 行不受影响）。
3. **`CaptureResult.Failed` 拒绝 `Canceled`** 是否接受：取消只能走 `Canceled` 工厂；若有调用方习惯传 `Failed(Canceled)`，P-14b 接线时会立即抛异常，属于有意的早失败。
4. **矩阵第 12 行的 `null` 语义**是否作为长期契约，还是 P-14b 拆成两行。

## 6. 主人实测步骤

1. 跑测试：`dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo`，预期最后一行 `失败: 0，通过: 206，已跳过: 0，总计: 206`。
2. 跑整解构建：`pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p14a-check -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14a/"`，预期 `BUILD-GUARD: EXIT=0`、`0 个警告`、`0 个错误`。
3. 跑格式门禁：`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore`，预期退出码 0、无输出。
4. 看矩阵：打开 `src/PrtScr.Core/CaptureScenario.cs`，确认 12 个 `CaptureScenario` 与 `ExpectationFor` 的 12 行；再打开 `Record/plans/B1-capture-session-design.md` §4.2 对照，预期逐行一致（第 12 行为复合行，已在此报告说明）。
5. 看写域：`git status --porcelain`，预期只多出本单 5 个未跟踪 `.cs` 文件（`Record/` 下两条是既有改动，不是本单）。

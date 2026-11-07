# B-P14c 历史元数据 version 2 实现报告

- 单号：task-42（P-14c 实现）
- 基线：HEAD 69616cc 之上的工作树（含并行单改动，非冻结树）
- 执行：maid-check，2026-11-07
- 结论：**实现完成、三道门禁全绿**；App 侧四个写入点全部接线，元数据 DTO/规则下沉 Core 并可单测。

## 一、做了什么

按单子裁决实现历史元数据 version 2，并把 JSON 契约下沉到 `PrtScr.Core` 以便单测。

### 1.1 新增 Core（3 文件）

| 文件 | 行数 | 内容 |
| --- | --- | --- |
| `src/PrtScr.Core/CaptureHistoryRules.cs` | 96 | 纯函数：`ToHistorySource`、`ResolveSource`、`ToHistoryEngine`、`ExtractFallbackReason` + 常量 |
| `src/PrtScr.Core/HistoryMetadata.cs` | 68 | version 2 DTO（10 键）+ `Serialize` / `Deserialize` + JSON 选项 |
| `src/PrtScr.Core/HistoryCaptureMeta.cs` | 32 | 提交点传递用的不可变 record + `FromCapture` / `ScreenQuick` |

核心 public 成员：

- `CaptureHistoryRules`：常量 `SourceWindow="window"`、`SourceNormal="normal"`、`SourceScreen="screen"`、`EngineWgc="wgc"`、`EnginePrintWindow="printwindow"`、`EngineDxgi="dxgi"`、`EngineBitBlt="bitblt"`、`EngineFallback="fallback"`、`PrintWindowTimeoutToken="printwindow-timeout"`、`MaxFallbackReasonLength=200`；方法 `static string? ToHistorySource(CaptureOrigin)`、`static string? ResolveSource(HistoryCaptureMode mode, string? storedSource)`、`static string? ToHistoryEngine(CaptureEngine)`、`static string? ExtractFallbackReason(string? notice, bool printWindowTimedOut)`。
- `HistoryMetadata`：`public const int CurrentVersion = 2`；`[JsonPropertyName]` 的 `version`(int)、`mode`(string)、`processName`(string?)、`windowTitle`(string?)、`capturedAt`(DateTime)、`source`、`engine`、`fallbackReason`(string?)、`printWindowTimedOut`(bool)、`sessionId`(string?)；`static string Serialize(HistoryMetadata)`、`static HistoryMetadata? Deserialize(string)`；选项 `WriteIndented=false`、`DefaultIgnoreCondition=WhenWritingNull`。
- `HistoryCaptureMeta`：`sealed record HistoryCaptureMeta(string? Source, string? Engine, string? FallbackReason, bool PrintWindowTimedOut, string? SessionId)`；`static HistoryCaptureMeta ScreenQuick`；`static HistoryCaptureMeta FromCapture(CaptureOrigin origin, CaptureEngine engine, bool printWindowTimedOut, string? notice)`。

### 1.2 改造 App 服务（1 文件）

`src/PrtScr.App/Services/CaptureHistoryService.cs`（现 456 行；净 diff 31 增 36 删）：

- 删除私有 `MetadataVersion = 1`、`MetadataJson` 静态选项、私有 `sealed class HistoryMetadata`（原 :443-459）以及对 `System.Text.Json` 的两个 using。
- `AddAsync` 追加可选参数：`public async Task<string?> AddAsync(byte[] pngBytes, HistoryCaptureMode mode, string? processName = null, string? windowTitle = null, HistoryCaptureMeta? meta = null)`（:61 起），并透传给 `WriteMetadataAsync`。
- `WriteMetadataAsync` 改用 Core DTO：`Version = HistoryMetadata.CurrentVersion`、`Source/Engine/FallbackReason` 取自 `meta`、`PrintWindowTimedOut = meta?.PrintWindowTimedOut ?? false`、`SessionId = meta?.SessionId`；序列化改 `HistoryMetadata.Serialize(...)`；写盘仍是「临时 .tmp → `File.Move(overwrite:true)`」。
- `TryReadMetadata` 改 `HistoryMetadata.Deserialize(File.ReadAllText(path))`（:371 附近）。
- **`ToItem` 零改动**：`git diff --ignore-cr-at-eol` 中不含该函数的 hunk；旧键名（version/mode/processName/windowTitle/capturedAt）与新 DTO 完全一致，因此旧 JSON 与 v2 JSON 的归类/进程名/标题语义逐字不变；`HistoryItem`、`CategoryKey`、配额、`PruneLocked` 均未触碰。

### 1.3 接线四个历史写入点（3 文件）

| # | 提交点 | 位置 | 传入 meta |
| --- | --- | --- | --- |
| 1 | 取景确认（回车/按钮） | `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:647-652` | `HistoryCaptureMeta.FromCapture(selection.Origin, selection.Engine, selection.PrintWindowTimedOut, selection.Notice)` |
| 2 | 取景另存为 | `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:787-792` | 同上 |
| 3 | 一键全屏快照 | `src/PrtScr.App/MainWindow.Capture.cs:67-68` | `meta: HistoryCaptureMeta.ScreenQuick`（source=normal、engine=null） |
| 4 | 贴图裁剪 | `src/PrtScr.App/Views/NativePinWindow.Interaction.cs:265` | `meta: null`（本阶段不写 source/engine） |

（第 5 处 `AddAsync(` 命中是服务自身定义 `CaptureHistoryService.cs:61`。）

## 二、证据

### 2.1 三个返回值语义（与标准一致）

- `ToHistorySource`：`Window→"window"`、`Normal→"normal"`、`Screen→"screen"`、`None→null`。与 `src/PrtScr.Core/CaptureSessionState.cs:223-224` 的 `ToHistoryMode`（Window→Window，其余→Normal）和 `:227` 的 `IsDowngrade`（Screen）同构。
- `ToHistoryEngine`：Wgc/PrintWindow/Dxgi/BitBlt/Fallback → 小写字符串；`None→null`。
- `ExtractFallbackReason`：`printWindowTimedOut=true` 时优先返回固定 token `"printwindow-timeout"`；否则若 notice 被「窗口捕获失败，已按常规截图保存（…）」外壳包裹则剥壳取纯原因；空白→null；超 200 字符截断。
- `ResolveSource(mode, storedSource)`：非空 `storedSource` 优先；否则 `Window→"window"`、`Normal→"normal"`、其余（PinCrop/Legacy）→null；**绝不把旧 JSON 判成 screen 降级**。读取侧预留，本阶段无消费者（见 §五）。

### 2.2 JSON 示例（窗口截图，PrintWindow 超时后由 WGC 成功）

```json
{"version":2,"mode":"Window","processName":"Code.exe","windowTitle":"README.md","capturedAt":"2026-11-07T11:30:00+08:00","source":"window","engine":"wgc","printWindowTimedOut":true}
```

- `source`/`engine`/`fallbackReason`/`sessionId` 为 null 时**不写键**（`WhenWritingNull`）；`printWindowTimedOut` 恒写（含 false）；`sessionId` 本阶段恒 null → 恒不落盘。
- 屏幕降级：`source="screen"`、`engine="fallback"`、`fallbackReason="<剥壳后的纯原因>"`。

### 2.3 新增测试（37 条，268 → 305）

`src/PrtScr.Core.Tests/CaptureHistoryRulesTests.cs`（130 行，29 用例）：

- `ToHistorySource_MapsEachOrigin`（Theory 4）、`ToHistoryEngine_MapsEachEngine`（Theory 6）；
- `ResolveSource_StoredValueWinsOtherwiseInfersFromMode`（Theory 6：Window/null→window、Normal/null→normal、PinCrop/null→null、Legacy/null→null、Window/Screen→screen、Normal/Window→window）；
- `ExtractFallbackReason_*` 8 条（Null/Blank/Trim 保留/剥壳/空壳→null/截断 200/超时优先/无 notice 仍出 token）；
- `FromCapture_*` 4 条 + `ScreenQuick_IsNormalWithoutEngine`。

`src/PrtScr.Core.Tests/HistoryMetadataJsonTests.cs`（132 行，8 条）：`CurrentVersion_IsTwo`、`Serialize_WritesVersionTwoAndAllNewKeys`、`Serialize_OmitsNullOptionalFields`、`Serialize_AlwaysWritesPrintWindowTimedOutFalse`、`Deserialize_LegacyJsonWithoutNewFields_UsesSafeDefaults`、`Deserialize_MissingVersion_LeavesZeroForCallerToTreatAsLegacy`、`RoundTrip_PreservesEveryField`、`Serialize_NullMetadata_Throws`。

### 2.4 grep 证据

- `MetadataVersion` 在 `src/PrtScr.App`：**0 命中**（已随私有类删除）。
- `private sealed class HistoryMetadata`：**0 命中**。
- `JsonSerializer` / `MetadataJson` / `System.Text.Json` 在 `CaptureHistoryService.cs`：**0 命中**。
- `HistoryCaptureMeta.FromCapture`：`Capture.cs:652`、`Capture.cs:792`；`HistoryCaptureMeta.ScreenQuick`：`MainWindow.Capture.cs:68`；`meta: null`：`Interaction.cs:265`。
- `AddAsync(` 在 App 共 5 命中 = 4 调用 + 1 定义（`CaptureHistoryService.cs:61`）。

### 2.5 门禁原始输出（终版）

```text
$ powershell.exe -NoProfile -File .tmp/build-guard.ps1 -Tag p14c3 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p14c/"
    0 个警告
    0 个错误
已用时间 00:00:46.12
BUILD-GUARD: EXIT=0 elapsed=47s tag=p14c3

$ powershell.exe -NoProfile -File .tmp/build-guard.ps1 -Tag p14ctest3 -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"
已通过! - 失败:     0，通过:   305，已跳过:     0，总计:   305，持续时间: 91 ms - PrtScr.Core.Tests.dll (net10.0)
BUILD-GUARD: EXIT=0 elapsed=3s tag=p14ctest3

$ dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
FORMAT-RC=0
```

过程记录：首轮 `p14ctest` 有 1 条失败（`ExtractFallbackReason_EmptyWrapperReason_ReturnsNull` 返回了空壳原文而不是 null），原因是剥壳长度判断用了 `>` 导致 `（）` 空壳不剥；改为 `>=` 后 `p14ctest2` 299/299，加 `ResolveSource` 及其 6 条用例后 `p14ctest3` 305/305。

### 2.6 写域与工作树

- 本单改动：3 个新 Core 文件 + 2 个新测试文件 + `CaptureHistoryService.cs` + `CaptureOverlayWindow.Capture.cs` + `MainWindow.Capture.cs` + `NativePinWindow.Interaction.cs` + 本报告。
- 未触碰：`CaptureOverlayWindow.Mode.cs` / `CaptureOverlayWindow.Selection.cs`（同事 task-41 在改）、`HistoryItem.cs`、配额/Prune 逻辑、XAML、README、csproj 版本号。
- 未 commit、未跑 `--smoke`；新文件全部 CRLF（`dotnet format` 无 ENDOFLINE 报错）。

## 三、进行中

无。本单代码、测试、门禁、报告均已完成。

## 四、风险与遗留

1. **门禁跑在非冻结工作树**：同一工作树还含 task-41（`CaptureOverlayWindow.Mode.cs`、`Selection.cs`）与 lead 的 `Record/ProjectLog.md` 改动，最终裁决需 lead 在冻结树上同 SHA 复跑。
2. **未真机验证 JSON 落盘**：本单只做编译与单测；实际 `data/history/*.json` 的 version=2 需要在主人机器上按 §六 步骤确认。
3. **`ResolveSource` 当前无消费者**：为 P-14d「历史列表露出降级标记」预留；若 lead 认为本阶段不应留未用 API，可删（测试同步删 6 条）。
4. **全屏快照元数据偏粗**：按裁决只写 `source=normal`、`engine=null`，不区分该图实际来自 WGC/DXGI/BitBlt；需要时另单扩 `QuickCaptureResult`（本单写域外）。
5. **贴图裁剪 meta=null**：JSON 中无 `source`/`engine` 键，历史列表无法区分「贴图裁剪」的来源（当前无此需求）。
6. **旧 JSON 不回填**：仍为 `version:1` 且无新键，读取按默认值处理；不迁移、不重写文件。
7. **`sessionId` 恒 null**：DTO 保留字段但不落盘，符合 lead「用户数据不放易变身份」的裁决。

## 五、待确认

1. `ResolveSource` 是否保留到 P-14d（本单无消费者）——建议保留，读取侧逻辑已测好。
2. 「printWindowTimedOut 恒写 false 键」是否接受（当前实现：false 也写；若希望 false 省略，需改 DTO 为 `bool?` 并调整 2 条测试）。
3. 旧 JSON 是否需要一次性升级脚本（当前答案：不需要，不回填）。

## 六、主人手测步骤

1. 启动候选包，分别做四类截图：①窗口模式截一张被遮挡的窗口（应 `source="window"`、`engine` 为 `wgc` 或 `printwindow`）；②制造降级（用一个会拒绝 PrintWindow 的窗口，或按历史复现路径）截一张（应 `source="screen"`、`engine="fallback"`、`fallbackReason` 有值）；③贴图后裁剪并勾选入历史（JSON 无 `source`/`engine` 键）；④一键全屏热键（`source="normal"`、无 `engine` 键）。
2. 打开 `data/history/`，逐个查看同名 `.json`：`version` 应为 `2`；窗口图有 `source`/`engine`；降级图另有 `fallbackReason`；超时改 WGC 的图应有 `"printWindowTimedOut":true`。
3. 找一张改造前生成的旧 `.json`：应仍是 `version:1`、无新键，且历史列表分组/进程名与改造前一致（不回填、不改名）。
4. 若窗口捕获在 2 秒内超时但 WGC 成功，检查 `fallbackReason` 是否为固定 token `printwindow-timeout`（该情况同时 `source="window"`、`printWindowTimedOut=true`）。

## 七、三行摘要

- 做了什么：新增 Core `CaptureHistoryRules` / `HistoryMetadata`(version 2) / `HistoryCaptureMeta`，`CaptureHistoryService` 改用 Core DTO 并给 `AddAsync` 加 `meta`，四个历史写入点全部按本地采集结果接线。
- 验证了：新增 37 条 Core 用例（268→305 全过）、整包 build 0 警 0 错、format rc=0；grep 确认 App 内 `MetadataVersion`/私有 `HistoryMetadata` 已清零。
- 风险与未做：未真机验证落盘、非冻结树门禁需 lead 复跑；`ResolveSource` 暂无消费者；全屏快照 engine 固定 null；贴图裁剪不写来源。

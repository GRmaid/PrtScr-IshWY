# P-13c 第二步：EditDocument 接入贴图标注

任务：task-33（rev2 → completed）；写域：src/PrtScr.App/Views/NativePinWindow*.cs、src/PrtScr.App/Services/Annotation*.cs、Record/reports/。
基线：HEAD 69616cc + 工作树中 P-13b（task-24）与并行任务未提交改动。本单未 commit、未跑 --smoke、未做 GUI 点按。

## 一、做了什么

把贴图标注的状态从 P-13b 的临时撤销栈换成 Core 的 `EditDocument<Annotation>`，行为按 P-13b 保持，只换底层类型。

改动 8 个文件（6 个已有文件 + 2 个 P-13b 新增文件继续演进）：

1. `src/PrtScr.App/Services/AnnotationModels.cs`：`Annotation` 实现 `IEditNumbered`（`AnnotationModels.cs:30`；`Number` 在 `:44` 已是 int，非序号标注保持默认 0）。
2. `src/PrtScr.App/Views/NativePinWindow.EditHistory.cs`：整文件重写为薄适配层（80 行），删除 `EditUndoKind` / `EditUndoEntry` / `_editUndoStack` / `SyncNumberCounter` / `ResetEditUndo`。
3. `src/PrtScr.App/Views/NativePinWindow.Edit.cs`：删掉 `_annotations` / `_draft` / `_numberCounter` 字段；进入改 `BeginEditDocument()`（`Edit.cs:123`，在 `_editMode = true` 之前，`EditBase` 抛异常不会留下半开编辑态），退出改 `EndEditDocument()`（`Edit.cs:200`）。
4. `src/PrtScr.App/Views/NativePinWindow.EditApply.cs`：`ApplyEditCore` 加唯一提交点 `_document?.TryCommit(bytes)`（`:91`）；成功才换 `_pngBytes` 与 `_publishedRevision`（`:100-101`），失败保留文档并提示（`:92-98`）。
5. `src/PrtScr.App/Views/NativePinWindow.EditInput.cs`：序号工具改用 `Number = _numberCounter`（`:72`，Core 的 NextNumber）；草稿创建/移动/抬起全走 `SetDraft` / `AddAnnotation`（`:85-106`、`:212-225`）。
6. `src/PrtScr.App/Views/NativePinWindow.CloseGuard.cs`：关窗待丢弃数量改读 `HasPendingAnnotations` + `_document.Count` + 是否有草稿（`:23-25`）。
7. `src/PrtScr.App/Views/NativePinWindow.Export.cs`：新增 `_publishedRevision`（`:56`）；`PinExportResult` 增加第三参数 `EditRevision Revision = default`（`:24` 附近）；导出请求的完成结果改为在贴图线程构造（`ExportRequest.Completion` 类型 `TaskCompletionSource<PinExportResult>`，`:45-50`、`:129-132`）；`TryComposeCurrentPng` 的标注列表改 `_document.GetDrawOrder()`（`:155-157`）。
8. `src/PrtScr.App/Views/NativePinWindow.cs`：`Navigate` 切图时推进版本（`:450` `_publishedRevision = _publishedRevision.Next()`）。

未改 Core（task-31 已定稿）、未改测试、未改 CaptureService / MainWindow.Capture.cs / CaptureOverlayWindow（并行单在改）。

## 二、证据

### 2.1 接线映射表（原字段/方法 → Core 调用）

| 原来（P-13b） | 现在（P-13c2） | 位置 |
| --- | --- | --- |
| `List<Annotation> _annotations` | 只读属性 → `_document.Annotations`（空文档返回 `Array.Empty`） | `NativePinWindow.EditHistory.cs:16` |
| `Annotation? _draft` | 只读属性 → `_document.Draft` | `EditHistory.cs:19` |
| `int _numberCounter` | 只读属性 → `_document.NextNumber` | `EditHistory.cs:22` |
| `bool CanUndo`（栈计数） | `_document.CanUndo` | `EditHistory.cs:24` |
| —（原无） | `HasPendingAnnotations → _document.HasPendingWork` | `EditHistory.cs:26` |
| 进入：Clear + ResetEditUndo + counter=0 + draft=null | `BeginEditDocument()` = `EditDocument<Annotation>.Start(new EditBase(_pngBytes, _pixelWidth, _pixelHeight, _publishedRevision))` | `EditHistory.cs:29-33`、`Edit.cs:123` |
| 退出：Clear + ResetEditUndo + draft=null | `EndEditDocument()` = `_document = null` | `EditHistory.cs:36`、`Edit.cs:200` |
| `AddAnnotation`：List.Add + push Add 项 | `_document.Add(annotation)` | `EditHistory.cs:39-48` |
| 直接给 `_draft` 赋值 | `SetDraft(draft)` → `_document.WithDraft(draft)` | `EditHistory.cs:51-57` |
| `ClearAnnotations`：push Clear 快照 + List.Clear + counter=0 | `_document.Clear()` | `EditHistory.cs:60-66` |
| `UndoEdit`：pop + FindLastIndex/RemoveAt 或 AddRange + SyncNumberCounter | `_document.Undo()`（序号由 Core 重算） | `EditHistory.cs:69-80` |
| 提交：TryCompose 成功即换 `_pngBytes` | 先 `TryCommit`，返回非 null 才换图与版本 | `NativePinWindow.EditApply.cs:91-101` |
| 导出：`_draft is null ? _annotations : [.. _annotations, _draft]` | `_document.GetDrawOrder()`（标注顺序 + 草稿最后） | `NativePinWindow.Export.cs:155-157` |
| 关窗待丢弃数：`_annotations.Count + draft` | `HasPendingWork` + `_document.Count` + 草稿 | `NativePinWindow.CloseGuard.cs:23-25` |

调用点全部保持原方法名（`AddAnnotation` / `SetDraft` / `ClearAnnotations` / `UndoEdit` / `CanUndo`），因此工具栏、序号工具、文字提交、指针抬起等既有调用点不改语义：
- 工具栏撤销/清空：`NativePinWindow.EditApply.cs:36-42`；撤销按钮禁用条件 `NativePinWindow.EditRender.cs:227-232`（20 → `!CanUndo`，21 → `_annotations.Count == 0`）。
- 序号工具：`NativePinWindow.EditInput.cs:67-80`（`Number = _numberCounter`）。
- 文字提交：`NativePinWindow.EditApply.cs:210-231`（`AddAnnotation(annotation)` 在 `:230`）。
- 指针抬起：`NativePinWindow.EditInput.cs:212-225`（`AddAnnotation(draft)` 后 `SetDraft(null)`）。

### 2.2 行为回归对照（P-13b 每条仍成立）

| P-13b 行为 | 现在由谁保证 | 证据 |
| --- | --- | --- |
| 操作级撤销（每次 Add / 一次 Clear 各算一步） | Core `EditDocument.UndoHistory` / `UndoOperation` | `EditHistory.cs:77`；Core 侧 `EditDocument.Clear` 只在非空时记一步 |
| 清空可撤销（恢复原顺序） | Core `EditOperation.Clear` 保存快照，`Undo` 恢复 | `EditHistory.cs:65`；Core `EditDocumentTests.Undo_Clear_RestoresAnnotationsInOriginalOrder` |
| 撤销后序号复用现存最大值 | Core 每次构造重算 `NextNumber = EditNumbering.NextAfter(现存 Number)` | `EditHistory.cs:22`；`EditDocumentTests.Undo_NumberedAnnotation_ReusesTailNumber` |
| 离开/重入清空撤销栈 | `BeginEditDocument` 新建文档；`EndEditDocument` 置 null | `EditHistory.cs:29-36` |
| 提交不可撤销（提交后退出编辑） | `TryCommit` 返回已清空历史的新文档，随即 `ExitEditMode` | `EditApply.cs:91-105`；`EditDocumentTests.Commit_ClearsAnnotationsDraftAndUndo` |
| 草稿不进入撤销历史 | Core 的 `Draft` 与 `UndoHistory` 分离 | `EditHistory.cs:56`；`EditDocumentTests.Draft_IsNeverInAnnotationsNorCounted` |
| 草稿永远最后绘制 | Core `GetDrawOrder()` | `Export.cs:155-157`；`EditDocumentTests.GetDrawOrder_WithDraft_PutsDraftLast` |
| 清空后序号回到 1 | Core `NextNumber` 空列表 = 1 | `Clear_ResetsNextNumberToOne` |
| 重入编辑序号从 1（lead 已裁定不修重号） | 新文档无标注 → NextNumber = 1 | 同上 |

Core 侧不变量与用例已由 task-31 报告 `Record/reports/P-13c-editdocument-core.md` 的「8 条不变量 → 测试方法」映射覆盖，本单未改测试（268/268 与基线一致）。

### 2.3 grep：旧的独立撤销语义只剩一处来源

在 `src/PrtScr.App/Views` 下：

```
_editUndoStack|SyncNumberCounter|ResetEditUndo|EditUndoKind|EditUndoEntry
  → 0 命中（NativePinWindow*.cs 已无第二套历史语义）

_annotations
  → NativePinWindow.EditRender.cs:35  foreach (var annotation in _annotations)
  → NativePinWindow.EditRender.cs:48  foreach (var annotation in _annotations)
  → NativePinWindow.EditRender.cs:230 21 => _annotations.Count == 0,
  → NativePinWindow.EditHistory.cs:8  注释
  → NativePinWindow.EditHistory.cs:16 属性定义（唯一 backing）
  → NativePinWindow.Export.cs:156     _annotations（无文档时的兜底）
  （全部是读；无 Add/Clear/RemoveAt 写点）

_numberCounter 赋值/自增 → 仅 EditHistory.cs:22 属性定义，无赋值点
_draft 赋值          → 仅 EditHistory.cs:19 属性定义，无赋值点
_document            → 22 处，定义/读写全部在 EditHistory.cs、Export.cs:132/155-157、CloseGuard.cs:24（均在贴图线程）
IEditNumbered        → App 内仅 Services/AnnotationModels.cs:30

_annotations.RemoveAt|FindLastIndex|_annotations.AddRange|EditUndoStack（全 Views）
  → 仅 AnnotationWindow.xaml.cs:284（另一个旧标注窗，独立类，本单未触碰）
```

结论：`NativePinWindow` 的撤销历史只剩 Core `EditDocument` 一处来源；`AnnotationWindow.xaml.cs` 是另一个未迁移的旧窗口（见 §四遗留）。

### 2.4 门禁原文

1. 整包 build（.tmp/build-guard.ps1，禁 -t:Compile）：
```
.tmp/build-guard.ps1 -Tag p13c2b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p13c2b/"
  已成功生成。
    0 个警告
    0 个错误
  已用时间 00:00:15.14
BUILD-GUARD: EXIT=0 elapsed=15s tag=p13c2b
```
（同轮首版 build tag p13c2 也是 0 警 0 错 / 15.88s。）

2. Core.Tests（未改测试）：
```
.tmp/build-guard.ps1 -Tag p13c2final -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"
已通过! - 失败: 0，通过: 268，已跳过: 0，总计: 268，持续时间: 110 ms
BUILD-GUARD: EXIT=0 elapsed=3s tag=p13c2final
```
与基线 268/268 完全一致（P-13b 收单时也是 268）。

3. 全量格式：
```
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
FORMAT-RC=0
```

### 2.5 写域核验

本单改动：`AnnotationModels.cs` + `NativePinWindow.{Edit,EditApply,EditInput,Export}.cs` + `NativePinWindow.cs`（6 个既有文件），以及 `NativePinWindow.EditHistory.cs` / `NativePinWindow.CloseGuard.cs`（P-13b 新增文件，继续演进）。全部在写域内。
工作树中其余 M 文件（`MainWindow.Capture.cs`、`CaptureService.cs`、`CaptureOverlayWindow.Capture.cs`、`ScreenCapturer.cs`、`WgcWindowCapture.cs`、`Record/ProjectLog.md`、总纲）属并行任务与记录提交，本单未触碰。

## 三、进行中

无。本单实现、门禁、grep 均已完成；等 lead 在同一 SHA 上复跑最终裁决。

## 四、风险与遗留

### 4.1 若 Core 与视图状态不一致会怎样（3 条，含缓解）

1. **基底有两份表示**：`_document.Base.PngBytes` 与视图的 `_pngBytes` / `_baseScaled` 是两份引用。进入编辑时 `BeginEditDocument` 以 `_pngBytes` 快照基底（`EditHistory.cs:31-33`），提交成功时同一处更新 `_pngBytes` 与 `_publishedRevision`（`EditApply.cs:100-101`）。若未来有人新增一个「编辑中写 `_pngBytes`」的路径（除 `ApplyEditCore` 外），预览会继续用旧基底缩放、导出用新基底，画面与导出分叉。
   缓解：编辑期 `Navigate` 已被 `_editMode` 挡住（`NativePinWindow.cs:414`）；提交只有 `ApplyEditCore` 一个点；本单已在适配层注释写明「所有新增标注点必须走 AddAnnotation」。建议后续把「编辑中禁止写 `_pngBytes`」写进代码注释或断言。

2. **版本号不等于画面**：编辑期 `TryComposeCurrentPng` 导出的是「基底 + 未提交标注」，但 `PinExportResult.Revision` 取基底的版本（`Export.cs:132`），未提交标注不会推进版本。若消费方用 Revision 缓存缩略图，可能把「同一 revision 的新合成图」当成旧图（管理器目前未用 Revision，见遗留）。
   缓解：XML doc 已写明「非标注态=发布版本，标注态=当前基底版本，未提交标注不推进版本」；消费方要「所见即所得」必须用 Bytes。后续若需要，可加 `PreviewRevision` 或在 PinRecord 里区分「已提交版本/预览版本」。

3. **Annotation 是浅不可变对象**：Core 的标注序列存的是引用，`Annotation.Points` 是可变 `List<PointD>`（`AnnotationModels.cs:34`）。若已加入文档的标注之后被原地修改，文档内容会绕过版本号与撤销记录变化（撤销也会还原不了原形）。
   缓解：当前唯一的原地修改对象是 `_draft`，且在 `AddAnnotation` 前完成、加入后立即 `SetDraft(null)`（`EditInput.cs:212-225`）；grep 未发现加入后的 mutate 路径。后续若支持「选中已有标注再编辑」，应改为快照式 `EditOp` 或不可变几何。

### 4.2 遗留

- **PinRecord.Revision 仍缺**：本单让 `PinExportResult` 带上了 Revision，但 `src/PrtScr.App/Services/PinService.cs` 不在写域，`PinRecord` 仍只有 `PngBytes`，订阅方（管理器缩略图）拿不到版本。若要求「管理器按版本判断陈旧」，需另立单扩写域。
- **`PinExportResult.Revision` 目前无消费者**：`PinManagerWindow.cs:88` / `:178` 只调用 `TryGetCurrentPng`，未读 `.Revision`（grep 命中 0）。
- **AnnotationWindow.xaml.cs 仍是旧撤销语义**：`:276` `_numberCounter = 0`、`:284` `_annotations.RemoveAt(_annotations.Count - 1)`，属另一个旧标注窗（非 P-13b/P-13c 作用域）。需要决定废弃还是迁移到 Core。
- **`_publishedRevision` 的跨线程读**：`TryGetCurrentPng` 非标注态在管理器线程读 `pin._publishedRevision`（`Export.cs:74`），与既有的 `pin._pngBytes` 读取同类（4 字节结构体、无 volatile 保证）。当前 Revision 仅作提示，无消费者；若变成判据，建议改为在贴图线程构造结果或用 `Volatile.Read`。
- **未做 GUI 运行时点按**：委托纪律禁止合成热键/代操作，行为验证留给主人；本单只保证编译、Core 单测与格式。
- **工作树含并行单改动**：本单门禁是在含 task-26/29 等未提交改动的工作树上跑的；最终裁决以 lead 冻结后的同 SHA 复跑为准。

## 五、待确认

1. `PinRecord` / `PinService` 是否加 `Revision`（写域外，需另单或扩写域）？
2. `AnnotationWindow.xaml.cs` 的旧标注窗是废弃还是迁移到 Core（影响是否还存在第二套撤销语义）？
3. `_publishedRevision` 的跨线程读是否要在本批内改成 `Volatile`/贴图线程构造（建议与第 1 项同单）？
4. 「标注态导出的 Revision = 基底版本、未提交标注不推进版本」这一语义是否可接受（管理器若按 revision 缓存需知道这点）？

---

三行摘要：做了 P-13c 第二步接线（Annotation:IEditNumbered、NativePinWindow 改用`EditDocument<Annotation>`、删除临时撤销栈、唯一 TryCommit 提交点、导出加 Revision）；验证了整包 build 0 警 0 错、Core.Tests 268/268（未改测试）、format rc=0、grep 旧撤销语义 0 命中；风险是 PinRecord.Revision 未落、AnnotationWindow 旧语义未迁移、未做 GUI 点按。

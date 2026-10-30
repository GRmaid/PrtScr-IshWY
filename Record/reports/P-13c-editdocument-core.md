# P-13c 第一步 · EditDocument 纯逻辑下沉 Core（task-31）

- 单号：task-31（P-13c 第一步）
- 执行：maid-check
- 日期：2026-10-30
- 结论：**通过**。7 个 Core 类型 + 62 条新测试落地，三条门禁全绿；未改 Views/App，未引入依赖，未提交。
- 基线：HEAD `69616cc` + 并行任务未提交改动（P-13b/P-14b 等，见 §2.5）；本单仅新增 9 个文件。

## §1 做了什么

1. 新增 `src/PrtScr.Core/Edit/` 7 个纯规则类型（不引用 System.Drawing / WinUI）：
   - `EditRevision`：单调递增版本号值类型。
   - `EditNumbering`：`NextAfter(numbers)` 返回现存最大编号 + 1，撤销后尾号自动复用。
   - `IEditNumbered`：标注「序号」抽象（0 = 非序号标注），供 App 的 `Annotation` 在步骤 2 实现。
   - `EditBase`：不可变基底（PNG 字节 + 像素尺寸 + 版本号），无 `with` 防止绕过版本。
   - `EditOperation<TAnnotation>` + `EditOpKind`：撤销历史一步（Add 持标注 / Clear 持快照）。
   - `EditDocument<TAnnotation>`：不可变编辑文档（Base / Annotations / Draft / UndoHistory / NextNumber / Count / CanUndo / HasPendingWork + Start / Add / WithDraft / Clear / Undo / Commit / TryCommit / Discard / GetDrawOrder / Publish）。
   - `EditPublishedSnapshot`：跨线程只传 (PNG 字节, Revision)，不含 Annotation/Bitmap。
2. 新增 `src/PrtScr.Core.Tests/EditNumberingTests.cs`（6 方法 / 13 用例）与 `EditDocumentTests.cs`（45 Fact + 1 Theory×4 = 49 用例），合计 **62 条**；覆盖单子点名的空文档 Undo、清空可撤销、撤销后序号复用、连续 Undo 到底、重入清栈、Add 序号递增。
3. 未改任何 `src/PrtScr.App/Views/**`、未接线、未改配置、未新增依赖、未 commit。

### 与设计稿的偏差（需 lead 知悉）

`Record/plans/B1-editdocument-design.md` §3.2 原把 `EditDocument` 放 App 并直接持有 `Annotation`（因 Annotation 在 App 层）。本单要求纯逻辑进 Core 且禁 System.Drawing/WinUI，故：

| 设计稿 | 本单实现 | 原因 |
| --- | --- | --- |
| `EditDocument`（App）直接持有 `Annotation` | `EditDocument<TAnnotation> where TAnnotation : class, IEditNumbered`（Core） | 去掉 App 依赖，步骤 2 让 `Annotation` 实现 `IEditNumbered` 即可闭合 |
| `Ops`（Add/Clear 混在同一操作列表，兼作绘制顺序） | 「可见标注序列 `Annotations`（绘制顺序）」+「撤销历史 `UndoHistory`（操作列表）」分离 | 不变量 3（草稿最后画、不进 Ops）与撤销语义互不纠缠；`GetDrawOrder()` 提供绘制顺序 |
| `ComposePng()` / `EditCommit` / `EditCommitReason` | 未实现（仅 `Commit`/`TryCommit` 纯状态迁移） | 合成需 App 的 `AnnotationRenderer` + System.Drawing，属步骤 2 接线 |
| Redo | 未实现 | 设计稿与任务均未要求 |

## §2 证据

### 2.1 public 成员签名清单（文件:行）

**src/PrtScr.Core/Edit/EditRevision.cs**
- :7 `public readonly record struct EditRevision(uint Value)`
- :10 `public static EditRevision Initial => new(1)`
- :13 `public EditRevision Next() => new(Value + 1)`
- :15 `public override string ToString() => Value.ToString()`

**src/PrtScr.Core/Edit/EditNumbering.cs**
- :4 `public static class EditNumbering`
- :10 `public static int NextAfter(IReadOnlyList<int> numbers)`（null 抛；空/全 0/负数→1；否则最大正数+1）

**src/PrtScr.Core/Edit/IEditNumbered.cs**
- :4 `public interface IEditNumbered`；成员 `int Number { get; }`（0 = 非序号标注）

**src/PrtScr.Core/Edit/EditBase.cs**
- :7 `public sealed record EditBase`
- :9 `public EditBase(byte[] pngBytes, int pixelWidth, int pixelHeight, EditRevision revision)`（null / 尺寸≤0 抛）
- :22 `public byte[] PngBytes { get; }`
- :25 `public int PixelWidth { get; }`
- :28 `public int PixelHeight { get; }`
- :31 `public EditRevision Revision { get; }`

**src/PrtScr.Core/Edit/EditPublishedSnapshot.cs**
- :7 `public sealed record EditPublishedSnapshot(byte[] PngBytes, EditRevision Revision)`

**src/PrtScr.Core/Edit/EditOperation.cs**
- :4 `public enum EditOpKind { Add, Clear }`
- :13 `public sealed record EditOperation<TAnnotation> where TAnnotation : class`
- :24 `public EditOpKind Kind { get; }`
- :27 `public TAnnotation? Annotation { get; }`
- :30 `public IReadOnlyList<TAnnotation>? Cleared { get; }`
- :33 `public static EditOperation<TAnnotation> Add(TAnnotation annotation)`
- :40 `public static EditOperation<TAnnotation> Clear(IReadOnlyList<TAnnotation> cleared)`

**src/PrtScr.Core/Edit/EditDocument.cs**
- :9 `public sealed class EditDocument<TAnnotation> where TAnnotation : class, IEditNumbered`
- :31 `public EditBase Base { get; }`
- :34 `public IReadOnlyList<TAnnotation> Annotations { get; }`
- :37 `public TAnnotation? Draft { get; }`
- :40 `public IReadOnlyList<EditOperation<TAnnotation>> UndoHistory { get; }`
- :43 `public int NextNumber { get; }`
- :46 `public int Count => _annotations.Length;`
- :49 `public bool CanUndo => _operations.Length > 0;`
- :52 `public bool HasPendingWork => _annotations.Length > 0 || Draft is not null;`
- :55 `public static EditDocument<TAnnotation> Start(EditBase editBase)`
- :65 `public EditDocument<TAnnotation> Add(TAnnotation annotation)`
- :81 `public EditDocument<TAnnotation> WithDraft(TAnnotation? draft)`
- :85 `public EditDocument<TAnnotation> Clear()`
- :98 `public EditDocument<TAnnotation> Undo()`
- :115 `public EditDocument<TAnnotation> Commit(byte[] composedPng)`
- :129 `public EditDocument<TAnnotation>? TryCommit(byte[]? composedPng)`
- :133 `public EditDocument<TAnnotation> Discard()`
- :139 `public IReadOnlyList<TAnnotation> GetDrawOrder()`
- :154 `public EditPublishedSnapshot Publish(byte[] pngBytes)`

### 2.2 8 条不变量 → 测试方法映射

| # | 不变量（设计稿 §3.3） | 测试方法（src/PrtScr.Core.Tests/EditDocumentTests.cs 等） |
| --- | --- | --- |
| 1 | 基底只有提交成功一个写点 | `Start_ExposesBaseAndInitialRevision`、`Add_KeepsBaseAndRevision`、`WithDraft_KeepsBaseAndRevision`、`Clear_KeepsBaseAndRevision`、`Undo_KeepsBaseAndRevision`、`Discard_KeepsBaseAndRevision`、`Commit_DoesNotMutatePreviousDocument` |
| 2 | 基底尺寸/字节随提交结果重建，不残留旧图 | `Commit_ReusesBasePixelSize`、`Start_NullBase_Throws`、`Add_NullAnnotation_Throws`、`EditBase_NullPng_Throws`、`EditBase_NonPositiveSize_Throws`（Theory 4 例） |
| 3 | 绘制顺序 = 标注顺序；Draft 永远最后且不进 Ops | `GetDrawOrder_WithoutDraft_ReturnsAnnotationsInOrder`、`GetDrawOrder_WithDraft_PutsDraftLast`、`Draft_IsNeverInAnnotationsNorCounted`、`Add_DoesNotTurnDraftIntoAnnotation` |
| 4 | NextNumber 由现存最大编号推出；撤销复用尾号；清空回 1 | `Add_NumberedAnnotation_AdvancesNextNumber`、`Add_NonNumberedAnnotation_KeepsNextNumber`、`Undo_NumberedAnnotation_ReusesTailNumber`、`Clear_ResetsNextNumberToOne`、`UndoClear_RestoresNextNumber`、`Undo_AllNumberedAnnotations_ReturnsNextNumberOne` |
| 5 | Revision 单调递增，只有 Commit 推进 | `NonCommitOperations_KeepInitialRevision`、`Commit_AdvancesRevisionByOne`、`Commit_Twice_IsMonotonic`、`Commit_ClearsAnnotationsDraftAndUndo`；`EditNumberingTests` 的 `Initial_IsOne`、`Next_IncrementsByOne`、`Next_Repeated_IsMonotonic` |
| 6 | 提交失败文档不变（含 Draft），不退出编辑 | `TryCommit_NullPng_ReturnsNullAndKeepsDocument`、`TryCommit_EmptyPng_ReturnsNullAndKeepsDocument`、`TryCommit_ValidPng_ReturnsCommittedDocument`、`TryCommit_FailureKeepsDraftAndAnnotations` |
| 7 | 跨线程只传 (PNG, Revision) | `Publish_UsesCurrentBaseRevision`、`Publish_ExposesOnlyPngAndRevision`（反射断言仅 PngBytes/Revision 两属性）、`Publish_EmptyPng_Throws`、`Publish_NullPng_Throws` |
| 8 | 取消/关窗不改基底；未提交内容可整体丢弃（D1 侧由 P-13b 关窗确认承担） | `Discard_ClearsAnnotationsDraftAndUndo`、`Discard_OnFreshDocument_ReturnsSameInstance`、`HasPendingWork_TracksAnnotationsAndDraft` |

补充：单子点名的「重入清栈」映射到 `Start_AfterEditing_StartsFreshHistory`（重新 `Start` 后无撤销历史、计数 1）；「连续 Undo 到底」映射到 `Undo_Sequence_UnwindsEverything`（Add/Add/Clear/Add 四步逐步回退，序号依次 1→3→2→1）。

### 2.3 与 `NativePinWindow.EditHistory.cs`（P-13b 行为基准）对照

基准文件 88 行：`EditUndoKind` :12-16、`EditUndoEntry` :18、`_editUndoStack` :20、`CanUndo` :22、`ResetEditUndo` :24、`AddAnnotation` :27-31、`ClearAnnotations` :34-42、`UndoEdit` :45-75、`SyncNumberCounter` :78-87。

| 语义 | 视图层（EditHistory.cs） | Core（EditDocument） | 结论 |
| --- | --- | --- | --- |
| 新增一条 = 记一步 Add | `:29-30` 加入 + push Add | `Add` :65 追加 + push Add | 一致 |
| 清空为空时不记步、不重绘 | `:36-37` 直接 return | `Clear` :85 返回 `this` | 一致（Core 额外返回同实例，测试 `Clear_WhenEmpty_DoesNotPushUndoStep`） |
| 清空非空 = 快照 + 清空 + 序号归零 | `:39-41` push Clear 快照、Clear、`_numberCounter=0` | `Clear` :85 push Clear 快照、清空；`NextNumber` 重算=1 | 一致（测试 `Clear_WithAnnotations_PushesSingleClearOperation`、`Clear_ResetsNextNumberToOne`） |
| 撤销 Add = 引用相等删最后匹配项 | `:56-58` `FindLastIndex(ReferenceEquals)` + `RemoveAt` | `Undo` :98 同算法 | 一致（`Undo_Add_RemovesLastMatchingReference`） |
| 撤销 Clear = 清空后恢复快照原序 | `:65-66` `Clear` + `AddRange` | `Undo` :98 同算法 | 一致（`Undo_Clear_RestoresAnnotationsInOriginalOrder`） |
| 撤销后序号按现存最大值重算 | `:78-87` `SyncNumberCounter` 取最大 Number | 每次构造 `NextNumber = EditNumbering.NextAfter(...)` | 一致（`Undo_NumberedAnnotation_ReusesTailNumber`） |
| 空栈撤销无状态变化 | `:47-70` 不弹栈，仅重绘/日志 | `Undo` :98 返回 `this` | 等价（`Undo_OnFreshDocument_IsNoOp`） |
| 重入编辑清空历史 | `ResetEditUndo` :24（Enter/Exit 调用） | 重新 `Start` 新文档、无历史 | 一致（`Start_AfterEditing_StartsFreshHistory`） |
| 不可变文档 / `HasPendingWork` / `Discard` / `GetDrawOrder` / `TryCommit` / `Publish` / Revision | 无对应（直接改 `_annotations`/`_pngBytes`） | 新增 | Core 侧新增语义，步骤 2 接线后生效 |
| 提交（烘焙）本身 | `ApplyEditCore`（EditApply.cs:77-106）在视图层换 `_pngBytes` | Core 只做 `Commit`/`TryCommit` 状态迁移，合成与发布留给视图层 | 分层：Core 不碰 Bitmap/渲染 |

### 2.4 门禁（原始输出摘要）

1. 整包构建（`.tmp/build-guard.ps1` 串行锁，禁 `-t:Compile`）：
   ```
   .tmp/build-guard.ps1 -Tag p13c -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p13c/"
   已成功生成。 0 个警告 0 个错误  已用时间 00:00:20.59
   BUILD-GUARD: EXIT=0 elapsed=21s tag=p13c
   ```
2. Core.Tests（`-Tag p13ctest2`）：
   ```
   已通过! - 失败: 0，通过: 268，已跳过: 0，总计: 268，持续时间: 83 ms
   BUILD-GUARD: EXIT=0 elapsed=3s tag=p13ctest2
   ```
   - 基线 206 → 268（+62 用例，≥231 达标）。
   - 首轮 `-Tag p13ctest` 曾有 1 处失败：`Publish_UsesCurrentBaseRevision` 误把 `doc.Base.PngBytes` 与 `Publish([8,8])` 的入参做 `Assert.Same`；改为对 `publishedBytes` 变量做 Same 后复跑全过（失败原因是测试笔误，非产品缺陷）。
3. 格式（全量）：
   ```
   dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
   EXIT 0（无输出）
   ```
   - 新文件一律 CRLF；首次落盘即通过，无 ENDOFLINE。

### 2.5 范围核验

- 本单新增文件（`git status --short` 中 `??`）：`src/PrtScr.Core/Edit/`（7 文件）、`src/PrtScr.Core.Tests/EditDocumentTests.cs`、`src/PrtScr.Core.Tests/EditNumberingTests.cs`。
- 未修改任何已跟踪文件；工作树其余 `M`（App/Capture/Views、ProjectLog、总纲）与 `??`（P-14b/P-13b/并行报告）均为其他任务产物，非本单改动。
- 未 `git add`/`commit`/`push`；未跑 `--smoke`。

## §3 进行中

- 本单（task-31）已全部完成，无遗留子项。
- 下一步「步骤 2 接线」不在本单写域：需改 `NativePinWindow` 系列（Views）与 `PinRecord`/`PinExportResult`，并新增 `ComposePng`/`EditCommit`（依赖 `AnnotationRenderer`）。建议另立单。

## §4 风险与遗留

1. **泛型约束需步骤 2 闭合**：`Annotation`（src/PrtScr.App/Services/AnnotationModels.cs:28-50，`public int Number { get; set; }` :42）尚未实现 `IEditNumbered`；接线时必须加接口（或适配器），且不得改变 Number 语义（0 = 非序号）。
2. **合成失败语义只到状态层**：「合成失败保留草稿、不退出编辑」在 Core 由 `TryCommit(null/[])` + 原文档不变表达；真实渲染失败（`AnnotationRenderer` 抛异常）仍由 App 的 `ApplyEditCore`（EditApply.cs:77-106）承担，本单未接线故未改变现状。
3. **NextNumber 的分工待统一**：Core 是「每次构造按现存最大值重算」，视图层现在是「Add 前 `++_numberCounter` 再 Add」（EditInput.cs:72）。接线时建议改为「用 `doc.NextNumber` 赋值再 Add」，否则两条路径可能各算一次。
4. **视图侧不变量只能行为验证**：不变量 2（`_baseScaled` 随基底重建）、3（预览绘制顺序）、7（PinRecord 发布原子性）、8（关窗确认）在 Core 测试中只能证明数据模型，真实 WinUI 行为仍需 P-13b/P-13c 步骤 2 的实测清单。
5. **无 Redo**：设计稿未要求，Core 与视图层都没有；若后续要加，`UndoHistory` 需改成可重放结构（当前只存增量）。
6. **测试替身局限**：`TestAnnotation` 是测试内私有类，只能证明接口契约；真实 `Annotation`（含 Tool/Color/Points 等）接线后建议补 2-3 条集成级测试。

## §5 待确认

1. **是否接受 Core 泛型分层与 Ops 拆分**（与设计稿 §3.2 的 App/Annotation 直持方案有偏差）？若接受，后续设计稿是否需要回写这一段？
2. **步骤 2 接线的归属**：是否另立 P-13c 续单（改 Views/Services + `Annotation:IEditNumbered` + `ComposePng`/`EditCommit` + `PinRecord.Revision`）？还是并入 P-13b 的后续单？
3. **`Annotation` 实现 `IEditNumbered` 的改动归属**：该文件在 `src/PrtScr.App/Services/`，不在本单写域；由步骤 2 承接还是我另接一小单？

---
附：新增文件行数 —— EditBase.cs 32、EditDocument.cs 205、EditNumbering.cs 23、EditOperation.cs 50、EditPublishedSnapshot.cs 7、EditRevision.cs 16、IEditNumbered.cs 8、EditNumberingTests.cs 61、EditDocumentTests.cs 604。

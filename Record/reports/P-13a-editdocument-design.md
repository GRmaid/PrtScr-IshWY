# P-13a EditDocument 边界设计（task-21）交付报告

## 1. 做了什么

只读盘点标注编辑的全部状态与出口，产出设计文档 `Record/plans/B1-editdocument-design.md`（25,175 字符，五节齐备），未改任何产品代码、未提交 git。

- 读取：src/PrtScr.App/Views/NativePinWindow.Edit.cs（388 行）、EditApply.cs（245 行）、EditRender.cs（511 行）、EditInput.cs（232 行）、EditPaint.cs（234 行）、Export.cs（183 行）、Interaction.cs（313 行）、Render.cs（331 行）、Window.cs（422 行）、NativePinWindow.cs（515 行）、Commands.cs，PinService.cs、PinManagerWindow.cs、AnnotationRenderer.cs、AnnotationModels.cs。
- 文档五节：1 现状清单（线程 + 逐变量 文件:行 + 三种图表示 + 出口表 + 已验证正确项）；2 缺陷清单 D1–D9（按「画面/`_pngBytes` 不一致、撤销栈与基底错位、跨线程半成品」三类映射）；3 类型草案（`EditBase` / `EditOp` / `EditDocument` / `EditCommit` / `EditRevision` + 状态机 + 8 条不变量 + 与 Export 分层结论）；4 迁移三步（每步 build/tests/format/grep/行为矩阵判据）；5 未决 6 条（每条给推荐答案）。
- 关键更正：任务单提到的 `_undoStack` 在 HEAD 不存在；实际撤销是 `_annotations.RemoveAt(Count-1)`（src/PrtScr.App/Views/NativePinWindow.EditPaint.cs:151-156），因此「撤销栈与基底错位」的准确表述是「没有栈 + 清空/提交不可撤销 + 序号不回收」。

## 2. 证据

### 2.1 结构证据（文档内每条均带行号，摘要）

- 三种表示：预览 `_destBits`（src/PrtScr.App/Views/NativePinWindow.cs:318-319）由 `RenderEditMode`（EditRender.cs:17-82）画；已提交 `_pngBytes`（NativePinWindow.cs:268）只在 `ApplyEditCore` EditApply.cs:93 被换；导出投影 `TryComposeCurrentPng`（Export.cs:130-159）调 `AnnotationRenderer.Render`（AnnotationRenderer.cs:12-41）。
- 预览 vs 烘焙：单点画笔预览要求 `Points.Count>=2`（EditRender.cs:118-126），烘焙画圆点（AnnotationRenderer.cs:79-83）；马赛克预览按缩放块（EditPaint.cs:184），烘焙按原图 `a.Size`（AnnotationRenderer.cs:124 起）；文字 `TextRenderingHint` 预览画注释时未设（EditRender.cs:48-55），到工具栏才设 AntiAliasGridFit（EditRender.cs:160），烘焙显式设（AnnotationRenderer.cs:29）。
- 提交顺序：EditApply.cs:93（换 `_pngBytes`）→ :94（复制，实际是 `TryEnqueue`，NativePinWindow.cs:507-514）→ :97（`ExitEditMode(apply:true)`）→ :99（`PublishPinState`）→ :101-105（可选另存）。
- 关窗：WM_CLOSE Window.cs:352-356 直接 `DestroyWindow`；菜单 CmdClose Interaction.cs:199-201 同样；切图 `Navigate` 反而有 `_editMode` 保护（NativePinWindow.cs:414）。
- 文字丢弃：点工具栏先 `DestroyTextEditor()`（EditInput.cs:43-44），点画布同样（EditInput.cs:50）；只有 Enter 走 `CommitTextEditor`（EditApply.cs:142-143）。
- 序号：`_numberCounter` 只增（EditInput.cs:72），进入编辑复位 0（Edit.cs:128），撤销不回退。
- 跨线程：`TryGetCurrentPng` 在 UI 线程读 `_editMode` 后决定直读 `_pngBytes` 或投 `WM_APP_EXPORT_PNG`（Export.cs:54-68）；写序为「先换 bytes 后置 false」，byte[] 引用替换原子 → 无可证撕裂读，缺的是 revision（`PinRecord` PinService.cs:25-41 无 revision）。

### 2.2 门禁（当前工作树，2026-10-30 22:5x）

| 门禁 | 命令 | 结果 |
|---|---|---|
| 整包构建 | `.tmp/build-guard.ps1 -Tag p13a -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p13a/"` | **0 警告 0 错误**，16.83s，`BUILD-GUARD: EXIT=0 elapsed=17s` |
| 单元测试 | `.tmp/build-guard.ps1 -Tag p13atest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo"` | **失败 0 / 通过 206 / 总计 206**，95ms，EXIT=0 |
| 格式 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | **rc=0**，无输出 |

说明：HEAD `69616cc` 的 Core.Tests 基线是 89 条；本次门禁 206 条是因为并行任务 task-20（P-14a，maid-impl）在工作树新增未跟踪的 `src/PrtScr.Core/Capture*.cs` 与 `CaptureResultTests.cs` / `CaptureRulesTests.cs`。我的改动只有 `Record/plans/` 与 `Record/reports/`，不触碰源码；该并行改动被同一次构建覆盖且 0 错，属额外证据而非本单产物。

### 2.3 写域核对（`git status --short`）

```
 M Record/ProjectLog.md
 M Record/plans/2026-10-28-整改总纲.md
?? .tmp/
?? Record/plans/B1-editdocument-design.md      ← 本单
?? Record/reports/P-14a-capture-session-types.md ← 并行任务 task-20
?? src/PrtScr.Core.Tests/CaptureResultTests.cs  ← 并行任务 task-20
?? src/PrtScr.Core.Tests/CaptureRulesTests.cs   ← 并行任务 task-20
?? src/PrtScr.Core/CaptureResult.cs             ← 并行任务 task-20
?? src/PrtScr.Core/CaptureScenario.cs           ← 并行任务 task-20
?? src/PrtScr.Core/CaptureSessionState.cs       ← 并行任务 task-20
?? 大佬修改材料/
```

本单新增仅 `Record/plans/B1-editdocument-design.md` 与 `Record/reports/P-13a-editdocument-design.md`；其余未跟踪项属于并行任务或既有资料，未改产品源码、未 commit。

## 3. 进行中

- 无。设计文档五节已完成并可评审；未进入实现（本单边界只允许设计与现状盘点）。
- 已把「迁移三步」写成可独立转派单的形式（步骤 1 纯类型+Core 测试、步骤 2 接线、步骤 3 删旧+合并渲染+关窗确认），每步含 build/tests/format/grep/行为矩阵判据。

## 4. 风险与遗留

- **可测边界**：`EditDocument` 依赖 App 的 `Annotation`，不能放进 `PrtScr.Core`；Core.Tests 只能测 `EditRevision` / `EditNumbering` 两条纯规则（步骤 1 新增约 6 条），其余靠步骤 2/3 的人工行为矩阵。若 lead 要求更高自动化覆盖，需要新建 `PrtScr.App.Tests`（本设计不建议，App 层多是 Win32/GDI 交互）。
- **步骤 2 是唯一有意行为变化**：撤销尾号复用、清空可撤销；重入仍从 1 开始（重号问题未修）。发布说明/主人实测清单需要写明，避免被当成回归。
- **D1（编辑中关窗丢标注）** 是否并入本设计未定；文档推荐拆 P-13b 最小单，若 lead 要求并入，步骤 3 的范围与风险都会变大（UI 线程/贴图线程弹确认的阻塞问题）。
- **D3 合并渲染** 触及热路径，需同图同参数截图对比验证；本设计只做几何共用，不保证像素完全一致。
- 未在真机点按贴图窗（本单要求只读设计，且主人机器上的 IshWY.exe 可能占用热键）；行为矩阵全部标为「待步骤 2/3 实测」。

## 5. 待确认（给 lead）

1. **关卡确认归属**：是否另立 P-13b（推荐），还是并入 P-13a 步骤 3？
2. **撤销粒度**：接受「操作级 + 清空可撤销、提交不可撤销」？还是要求提交也可撤销（需要快照/历史，成本明显上升）？
3. **重入序号**：接受「新会话从 1 开始（维持现状）」，还是本批就要修重号（需要另定规则，例如提示用户手动编号/增加重编号工具）？
4. **迁移步骤 2 的行为变化**是否可接受：撤销尾号复用 + 清空可撤销（预计用户可见，但是修复）。
5. **Core 类型落点**：本设计只把 `EditRevision`/`EditNumbering` 放 Core；`EditDocument`/`EditCommit` 放 `PrtScr.App/Services`。若 lead 希望文档类型也进 Core（需把 Annotation 抽象成 Core 模型），需另做数据模型单，超出 P-13a。
6. **门禁口径**：本次 206 条测试包含并行任务 task-20 的新增；lead 冻结树后复跑时以当时总数为准（本文只声明「全过且构建 0/0、format rc=0」）。

（完）
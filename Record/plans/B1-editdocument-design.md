# B-1 EditDocument 边界设计草案（P-13a / task-21）

- 状态：设计草案待评，未改任何产品代码。
- 基线：HEAD `69616cc`「chore(release-prep): 版本号 1.3.1 与 README 更新，补记冒烟通过（不发布）」，2026-10-30 22:4x 快照；取样时源码树干净，随后并行任务 P-14a（task-20）在工作树新增未跟踪的 `src/PrtScr.Core/Capture*.cs` 与测试，不影响本文涉及的 App 贴图文件与行号。文中行号按该快照核对。
- 编号口径：整改总纲 197 行版里 **B-1 = EditDocument（P-13）**、**B-2 = CaptureSession / Result（P-14）**。lead 的 task-19 曾把 CaptureSession / Result 提为「B 批次第一块」并称 B-1；**本文对应总纲的 B-1 / P-13a，只覆盖标注编辑**，不含 CaptureSession / CaptureResult（见 `Record/plans/B1-capture-session-design.md`）。
- 交付范围：五节 = 现状清单 / 缺陷清单 / 类型草案 / 迁移三步 / 未决问题。

## 0. 一句话结论

标注编辑目前由**一组可变字段直接改已提交图像**：`_annotations` / `_draft` / `_numberCounter` / `_baseScaled` 在贴图线程上被任意入口读写，`_pngBytes` 在 `ApplyEditCore` 中途被替换（src/PrtScr.App/Views/NativePinWindow.EditApply.cs:93），而预览（`RenderEditMode`）与最终烘焙（`AnnotationRenderer`）是**两套独立绘制实现**。结果是三类可复现问题：预览与导出不一致（单点画笔、马赛克颗粒、文字抗锯齿）、撤销栈与基底错位（没有栈、清空与提交不可撤销、序号不回收且重入重号）、提交点不显式（换图、复制、发布、另存四步无原子边界，关窗可静默丢标注）。本设计把它收敛为 **`EditDocument`（不可变基底 + 操作列表 + 草稿 + 序号 + revision）+ `EditCommit`（唯一提交点，compose 成功才换基底）+ `EditRevision`（跨线程判别版本）**，迁移三步可独立交付。

---

## 1. 现状清单

### 1.1 线程与所有权

| 线程 | 入口 | 会触碰的编辑态 | 现状规则 |
|---|---|---|---|
| 贴图线程（STA） | `NativePinWindow.cs:380-382` `new Thread(pin.Run){IsBackground=true, Name="PrtScr.Pin"}`；`Run` Window.cs:21-113；消息循环 Window.cs:86-90 | 全部 `_annotations` / `_draft` / `_baseScaled` / `_pngBytes` 写；全部 WndProc 与 Edit* 逻辑 | 编辑态事实上的唯一写线程；但没有任何 assert 或注释声明该所有权 |
| UI 线程（WinUI） | `_uiQueue` NativePinWindow.cs:274（ctor 赋值 :329）；PinService / PinManagerWindow / OCR / 剪贴板回调 | 读 `_editMode`（Export.cs:64）、读 `_pngBytes`（非编辑态 Export.cs:65）、读 `PinRecord.PngBytes` | 跨线程只靠 `_editMode` volatile 与 byte[] 引用替换；无 revision、无锁 |
| Esc 轮询线程 | NativePinWindow.cs:187-231（`_escPollStop` :51）；读 `_editMode` 经 lambda Window.cs:64，`EscRouter.RegisterPin` :62-67 | 读 `_editMode` / `_menuOpen` / `_escNeedsRelease`（:220） | 只读标志；`EscRouter.Dispatch` :231 的后续动作不在本设计范围 |
| 线程池 | 裁剪 `Interaction.cs:233`；剪贴板 `PinClipboardHelper.Copy` NativePinWindow.cs:507-514；OCR :483-489 | 只接收 `byte[]` 快照 | 不直接触碰编辑字段 |
| 定时器 | `_settleTimer`（Interaction.cs:62-66）、`_topmostTimer`、`_navHintTimer`、`_flashTimer` | 无 | 只 `PostMessage` 回贴图线程 |

结论：编辑态是「单线程写、多线程读一个 volatile bool + 一个 byte[] 引用」的模型。byte[] 整体替换在 CLR 里是引用原子写，不会读到撕裂数组；但**没有版本号**，读方无法判断拿到的是提交前还是提交后的图（见 D6）。

### 1.2 状态变量（变量 → 声明/写点/读点/线程）

**已提交图像与窗口几何（src/PrtScr.App/Views/NativePinWindow.cs，515 行）**

| 状态 | 声明 | 写点 | 读点 | 线程 |
|---|---|---|---|---|
| `_pngBytes` | :268 | ctor :326（调用方线程，先于线程启动）；`Navigate` :447；`EditApplyCore` EditApply.cs:93 | `DecodeSource` Render.cs:255；`TryGetCurrentPng` Export.cs:65；`TryComposeCurrentPng` Export.cs:133；`EnterEditMode` Edit.cs:123（只判空）；`PublishPinState` :342 | ctor 在宿主线程；其余写均为贴图线程；读跨到 UI 线程（Export.cs:65） |
| `_pixelWidth` / `_pixelHeight` | :269-270 | ctor :327-328；`Navigate` :448-449 | 缩放、裁剪、导出、坐标换算 | 贴图线程（ctor 除外） |
| `_zoom` / `_opacity` | :279-280 | 滚轮 Interaction.cs:47-54；透明度命令 | 渲染 Render.cs:141-190 | 贴图线程 |
| `_winX` / `_winY` / `_winW` / `_winH` | :281-284 | `EnterEditMode` Edit.cs:169-170；`ExitEditMode` :193-204；`MoveEditPan` EditInput.cs:184-185；滚轮/吸附 | 渲染、指针命中、`PublishPinState` :342 | 贴图线程 |
| `_renderedW` / `_renderedH` / `_needsRedrawAfterZoom` | :298-300 | `RenderAndPresent` Render.cs:39-41；`ExitEditMode` Edit.cs:211 | `RenderAndPresent` 缓存判定 :26 | 贴图线程 |
| `_destDib` / `_destBits` / `_destAllocW` / `_destAllocH` | :318-321 | `CreateDib` Render.cs:298-313；`ReleaseDestResources` :316-323 | `RenderPlaceholder` :105/:114；`RenderHighQuality` :145；`CreateDestBitmap` :272 | 贴图线程；GDI 句柄不跨线程 |
| `_escNeedsRelease`（volatile） | :53 | `ExitEditMode` Edit.cs:190；轮询复位 :218 | 轮询 :220 | 贴图线程写；轮询线程读并清 |
| `_menuOpen`（volatile） | :54 | `ShowMenu` Interaction.cs:143/:151 | 轮询 :220、EscRouter 判定 | 贴图线程写；轮询线程读 |
| `_locked` | :306 | 锁定命令 | `OnWheel` Interaction.cs:25；`Navigate` :414 | 贴图线程 |

**标注编辑态（src/PrtScr.App/Views/NativePinWindow.Edit.cs，388 行）**

| 状态 | 声明 | 写点 | 读点 | 线程 |
|---|---|---|---|---|
| `_editMode`（volatile） | :60 | :126（true）、:187（false） | 见本节末行全集 | 贴图线程写；UI 线程 Export.cs:64、Esc 轮询 Window.cs:64 读 |
| `_annotations` | :61 | `Clear` :127/:206；`EditApply.cs:43`；`EditInput.cs:69-76`（序号）/:227（抬笔加入）；`EditPaint.cs:154`（撤销尾删）；`EditApply.cs:222`（文字提交） | `EditRender.cs:35-55`（绘制）；`Export.cs:145-147`（合成）；`EditApply.cs:37/:41`（撤销/清空可用性）；`EditRender.cs:226`（禁用） | 贴图线程 |
| `_draft` | :62 | :129/:205（清空）；`EditInput.cs:88/:97`（新建/续画）/:228（抬笔置空） | `EditRender.cs:40/:53`（最后绘制）；`Export.cs:145`（合成含草稿） | 贴图线程 |
| `_toolId` | :63 | `HandleToolbarCommand` EditApply.cs:20 | `EditInput.cs:61/:67/:84`；`EditRender.cs:211/:263/:308/:310`；`OnWheel` Interaction.cs:30 | 贴图线程 |
| `_colorArgb` | :64 | EditApply.cs:23（快捷色）；`UpdatePaletteFromPoint` EditPaint.cs:95 | `EditRender.cs:86/:198/:309` | 贴图线程 |
| `_numberCounter` | :65 | :128（进入复位 0）；EditApply.cs:44（清空复位）；`EditInput.cs:72`（`++_numberCounter`） | `EditInput.cs:72` | 贴图线程；**无回退点** |
| `_textSize` | :66 | `AdjustTextSize` EditApply.cs:196 | `BeginTextEditor` / `ApplyEditorFontAndBounds` / `DrawTextSizeControl` / `DrawToolbarMeta` | 贴图线程；不持久化，每次进入默认 18（:66） |
| `_baseScaled`（Bitmap） | :67 | `RebuildBaseScaled` :378-379；`ApplyEditCore` EditApply.cs:80-81（Dispose+null）；`ExitEditMode` :207-208 | `RenderEditMode` EditRender.cs:19/:29 | 贴图线程；GDI+ Bitmap 有线程亲和性，未外泄 |
| `_toolbarButtons` | :68 | `DrawToolbar` EditRender.cs:157（Clear）/:192（Add）；`DrawTextSizeControl` :285/:293；`ExitEditMode` :209 | `OnEditPointerDown` EditInput.cs:38；`BeginEditPan` EditInput.cs:155 | 贴图线程 |
| `_toolbarHeight` | :69 | `EnterEditMode` Edit.cs:165（ComputeToolbarHeight）；`DrawToolbar` EditRender.cs:154 | `DrawPalette` EditPaint.cs:40；`DrawToolbar` :156 | 贴图线程 |
| `_applyingEdit` | :70 | `ApplyEdit` EditApply.cs:66/:73 | EditApply.cs:63（重入 guard） | 贴图线程 |
| `_editDragging` / `_editPanning` / `_panMoved` / `_panStartCursor` / `_panStartWinX` / `_panStartWinY` | :71-76 | 指针事件 EditInput.cs:81-82/:170/:215-216/:193-201 | `OnEditPointerMove` :125；WndProc Window.cs:187/:203/:211 | 贴图线程 |
| `_editControl` / `_editFont` / `_editKeys` | :77-81 | `BeginTextEditor` EditApply.cs:122-139；`ApplyEditorFontAndBounds` :172-176；`DestroyTextEditor` :231-240 | 焦点/键盘/尺寸 | 贴图线程（EDIT 子窗口同线程） |
| `_editLocalX` / `_editLocalY` / `_editTextPosition` | :79-82 | `BeginTextEditor` EditApply.cs:115-117；`ApplyEditorFontAndBounds` :178-182 | `CommitTextEditor` :221 | 贴图线程 |
| `_dpiScale` | :83 | `EnterEditMode` Edit.cs:134 | 工具栏/文字/坐标换算 | 贴图线程 |
| `_imgX` / `_imgY` / `_imgW` / `_imgH` | :86-89 | `EnterEditMode` :160-163；`ExitEditMode` :195-198；`MoveEditPan` EditInput.cs:186-187 | 渲染/裁剪/文字框/导出几何 | 贴图线程 |
| `_paletteOpen` / `_paletteDrag` / `_paletteRect` / `_svRect` / `_hueRect` / `_svBitmap` / `_svHue` / `_hue` / `_sat` / `_val` | :91-100 | 调色盘 `OnEditPointerDown` EditInput.cs:20-35、`UpdatePaletteFromPoint` EditPaint.cs:79-97 | `DrawPalette` EditPaint.cs:26-77 | 贴图线程 |

`_editMode` 的读点全集（逐条核对过）：Edit.cs:116/:183；EditApply.cs:63；NativePinWindow.cs:224/:414；Export.cs:64/:132/:167/:171；Interaction.cs:25/:27/:173/:215/:221-223/:292/:306；EditRender.cs:19；Render.cs:240；Window.cs:64/:161/:178/:183/:187/:191/:195/:199/:207/:211/:221/:226/:230/:275/:331/:336/:354；Commands.cs:117。

> 任务单提到的 `_undoStack` 在 HEAD **不存在**（grep 无命中）。撤销 = `_annotations.RemoveAt(Count-1)`（src/PrtScr.App/Views/NativePinWindow.EditPaint.cs:151-156），没有操作历史，也没有快照。

**导出请求（src/PrtScr.App/Views/NativePinWindow.Export.cs，183 行）**

| 状态 | 声明 | 写点 | 读点 | 线程 |
|---|---|---|---|---|
| `_exportRequest` | :47 | `RequestCurrentPng` :78；`ClearExportRequest` :105；`CompleteExportRequest` :117 | :76（单槽判断）；:105；:116 | UI 线程投递；贴图线程完成；`_exportLock`（:46）保护 |
| `_exportLock` | :46 | — | 上述三处 | 跨线程锁 |

**快照（跨线程发布）**

| 状态 | 声明 | 写点 | 读点 | 线程 |
|---|---|---|---|---|
| `PinRecord.PngBytes` | src/PrtScr.App/Services/PinService.cs:27 | `UpsertPin` :81-86，由 `PublishPinState` NativePinWindow.cs:336-357 调用（:342 放 `_pngBytes`） | `SnapshotPins` :78-79 → PinManagerWindow 缩略图 | 贴图线程发布；UI 线程读 |
| Pins 注册表 | PinService.cs:70 `ConcurrentDictionary<nint, PinRecord>` | `UpsertPin` / 关闭时移除 | PinManagerWindow、PinService | 并发字典保证单条可见性，**无 revision** |

### 1.3 三种「图」的表示与转换

| 表示 | 载体 | 坐标系 | 谁生成 | 谁消费 |
|---|---|---|---|---|
| 预览画面 | `_destDib` / `_destBits`（NativePinWindow.cs:318-319）经 `UpdateLayeredWindow`（Render.cs:278-296）上屏 | 屏幕像素（窗口尺寸） | `RenderEditMode`（EditRender.cs:17-82）：`_baseScaled` 缩放底图 + `DrawAnnotationShape` 逐条画 + 工具栏/调色盘/提示 | 用户眼睛 |
| 已提交基底 | `_pngBytes`（:268） | 原图像素 | 只有 `ApplyEditCore` EditApply.cs:93 会在编辑期替换；另外 ctor :326 / `Navigate` :447 | `DecodeSource` 渲染、`TryGetCurrentPng` 非编辑态直出、PinRecord |
| 导出投影 | `TryComposeCurrentPng()`（Export.cs:130-159） | 原图像素 | `_pngBytes` → `DecodeSource`（Render.cs:250-265）→ `AnnotationRenderer.Render`（AnnotationRenderer.cs:12-41，草稿也计入 :145-147）→ PNG | 复制/OCR/裁剪/另存/管理器动作/完成提交 |

关键事实：预览与导出**不是同一套绘制代码**（`EditRender.DrawAnnotationShape` :84-149 vs `AnnotationRenderer.DrawAnnotation` :43-108）；预览在缩放位图上按 `scale` 画，导出在原图上按原坐标画；马赛克预览在缩放位图上打码（EditPaint.cs:174-225），导出在原件上按 `a.Size` 原块打码（AnnotationRenderer.cs:124 起）。这是 D3 的根。

### 1.4 出口清单（编辑态下）

| 出口 | 入口 | 取图方式 | 提交后的副作用 | 编辑态行为 | 失败行为 |
|---|---|---|---|---|---|
| 完成（按钮/Enter） | `HandleToolbarCommand` case 22 EditApply.cs:46-48；WndProc Window.cs:226-227 | `TryComposeCurrentPng` EditApply.cs:84 | `_pngBytes=bytes` :93 → `Copy` :94 → `ExitEditMode(apply:true)` :97 → `PublishPinState` :99 | 退出标注，标注清空（Edit.cs:206） | compose null → 保留编辑态 + hint + 日志 :85-90 |
| 完成并另存（case 24 / Ctrl+S） | EditApply.cs:49-51；Window.cs:230-232 | 同上 | 同上 + `FileSaveService.SavePngAsync` :101-105 | 同上 | 另存失败/取消无回滚无提示 |
| 复制（Ctrl+C） | Window.cs:235-239 → `CopyCurrentPng` Export.cs:162-175 | `TryComposeCurrentPng` :164（含草稿） | 只写剪贴板 | **不退出编辑**（设计如此） | compose null → hint :167 |
| 复制（菜单/管理器） | Interaction.cs:159-161；PinManagerWindow.cs:139/:157/:167 → `TryGetLivePng` :176-188 | 非编辑态直读 `_pngBytes`；编辑态投 `WM_APP_EXPORT_PNG`（Export.cs:67/:81） | 剪贴板/OCR/另存 | 编辑态可用，1000ms 超时（:38/:88） | Unavailable → 管理器提示「贴图正在标注且暂未响应…」（:186） |
| OCR | Interaction.cs:168-171 → `OpenOcrCurrent` :301-311 | compose :303 | 打开 OCR 结果窗（`PinClipboardHelper.OpenOcr` :483-489） | 不退出编辑 | compose null → hint :306 |
| 裁剪 | Interaction.cs:165-167 → `StartCrop` :209-234 | compose :212 | 打开裁剪窗，产物可落历史（`HistoryPinCrop` :264-265） | 不退出编辑；`cropY` 用 `_winY+_editOffsetY` :221 | compose null → hint :213-217 |
| 另存为（菜单） | Interaction.cs:172-177 | 编辑态走 `ApplyEdit(saveAs:true)`；非编辑态 `SaveAs` :287-298 | 保存 PNG | 编辑态等同「完成并另存」 | 无回滚 |
| 取消（Esc/菜单） | `ExitEditMode(apply:false)` EditApply.cs:52-54；WndProc 的 Esc 路径经 EscRouter | 不取图 | 无 | 丢弃标注，基底不变 | 无提示（静默丢弃） |
| 关闭窗口 | WM_CLOSE Window.cs:352-356；菜单 CmdClose Interaction.cs:199-201 | 不取图 | 无 | **直接 DestroyWindow，标注丢失** | 无确认（D1） |
| 快照发布 | `PublishPinState` :336-357 | `_pngBytes`（无 revision） | PinRecord 更新 / 管理器刷新 | 编辑期间不调用（除提交点 :99） | — |

### 1.5 已验证正确、本设计必须保留的点

- 进入编辑幂等：`EnterEditMode` 首部 guard（Edit.cs:116-121，日志 `Pin: already in edit mode, ignored`）。
- 四出口统一走同一 compose：`Export.cs:164`、`EditApply.cs:84`、`Interaction.cs:212/:289/:303`（grep `TryComposeCurrentPng` 只这些调用点）。
- compose 失败保留编辑内容：EditApply.cs:85-90（B03 行为）。
- 导出单槽 + 1000ms 超时 + 超时清槽（Export.cs:76-92）；超时后晚到的 `TrySetResult` 在 `CompleteExportRequest` :120-121 因槽已空而无副作用。
- 文字编辑器是独立 EDIT 子窗口 + 独立 `KeyInterceptor`（EditApply.cs:122-139），不会把主窗 Ctrl+C 抢走。
- `_applyingEdit` 防止 ApplyEdit 重入（EditApply.cs:63-73）。
- 所有编辑态绘制/合成都在贴图线程，Bitmap 不外泄，无 GDI 句柄跨线程。

---

## 2. 缺陷清单

### 2.1 三类现象的对应关系

| 任务单要问的现象 | 对应缺陷 | 结论 |
|---|---|---|
| 画面与 `_pngBytes` 不一致 | D2、D3、D5、D7 | 存在。D3 是常态（预览 vs 烘焙两套渲染）；D5 是「看得到但没提交」；D2/D7 是异常路径 |
| 撤销栈与基底错位 | D4 | 存在，但不是栈错位——是**根本没有栈**：清空不可撤销、提交不可撤销、序号不回收、重入从 1 重号 |
| 跨线程读到半成品 | D6 | **没有可证的撕裂读**（byte[] 引用替换原子、compose 在贴图线程、写序为先换 bytes 后置 false）。真正缺的是版本号：读方拿到旧图无法判别；编辑器缩略图在编辑期间保持旧图 |

### D1（高，数据丢失）编辑中关窗无确认

- 机制：`WM_CLOSE`（Window.cs:352-356）只记日志 `Pin: WM_CLOSE (edit=...)` 就 `DestroyWindow`；右键菜单 `CmdClose`（Interaction.cs:199-201）同样。`_annotations` / `_draft` 只存在于内存，窗口销毁即丢。
- 对照：切图路径 `Navigate` 有 `_editMode` 保护（NativePinWindow.cs:414），关闭没有。
- 影响：用户画了 20 笔误点关闭 → 全丢；无自动保存、无草稿。

### D2（中，提交非原子）换基底、退出、发布、另存四步无边界

- 顺序（EditApply.cs:93-105）：`_pngBytes = bytes`（:93）→ `PinClipboardHelper.Copy`（:94，实际只是 `TryEnqueue`，NativePinWindow.cs:507-514）→ `ExitEditMode(apply:true)`（:97）→ `PublishPinState`（:99）→ 可选 `SavePngAsync`（:101-105）。
- 风险一：:93 之后、:97 之前若抛异常（例如 `ExitEditMode` 内 `MaybeShrinkDestDib`/`RenderAndPresent` 的罕见异常），结果是 `_pngBytes` 已含标注、`_editMode` 仍 true、`_annotations` 仍在 → 下一次 `RenderEditMode` 把「已含标注的新底图 + 旧标注」再叠一遍（视觉重复，且再次完成会二次烘焙）。
- 风险二：另存失败/用户取消后无任何回滚或提示，用户以为没保存但图已提交并复制。
- 风险三：复制结果由 UI 队列异步回调（`PostCopyResult` Export.cs:178-183），提交时不等待；`_pngBytes` 与剪贴板可能短暂不同源（对用户不可见，但设计上应明确「提交成功不依赖复制成功」）。

### D3（中，所见非所得）预览与烘焙两套渲染

| 差异 | 预览 | 烘焙 | 可复现表现 |
|---|---|---|---|
| 单点画笔 | `DrawAnnotationShape` 的 Pen 分支要求 `Points.Count >= 2`（EditRender.cs:118-126） | `AnnotationRenderer.DrawAnnotation` 对单点画圆点（AnnotationRenderer.cs:79-83） | 点一下不拖动：预览看不见，完成后图上突然出现一个点 |
| 文字抗锯齿 | 画注释时未设置 `TextRenderingHint`（EditRender.cs:48-55），到画工具栏才设成 `AntiAliasGridFit`（EditRender.cs:160） | 显式 `AntiAliasGridFit`（AnnotationRenderer.cs:29） | 字形边缘、字重观感略有差异 |
| 马赛克 | 在 `_baseScaled`（已缩放）上按 `block=max(4, a.Size*scale)` 打码（EditPaint.cs:174-225，:184） | 在原图上按 `a.Size` 原块打码（AnnotationRenderer.cs:124 起） | 颗粒大小/边界不同，放大或小图时明显 |
| 绘制顺序 | 先马赛克（EditRender.cs:35-41），再按列表画形状（:48-55） | 先马赛克（AnnotationRenderer.cs:22-24），再形状（:26-38） | 顺序一致；但马赛克覆盖范围因缩放不同可能压住不同边缘 |

### D4（中，无撤销栈/基底错位）

- 没有 `_undoStack`：撤销只 `_annotations.RemoveAt(Count-1)`（EditPaint.cs:151-156）。
- 「清空」（EditApply.cs:40-45）不可撤销：一次清空所有标注，Ctrl+Z 无效。
- 提交不可撤销：完成后 `_pngBytes` 已换、`_annotations` 清空（Edit.cs:206），Ctrl+Z 无效果；用户只能重新进入标注手工修补。
- 序号不回收：`_numberCounter` 只增（EditInput.cs:72），撤销掉最后一个序号后重画会跳号（1,2,3 → 撤销 3 → 再画得 4）。
- 重入重号：`EnterEditMode` 把 `_numberCounter=0`（Edit.cs:128），在已烘焙序号 1..N 的图上再次标注，新序号从 1 开始，与图上既有编号重复。

### D5（中，看得到但没提交）文字输入被静默丢弃

- 文字框未按 Enter 时点工具栏：`OnEditPointerDown` 先对所有非字号按钮执行 `DestroyTextEditor()`（EditInput.cs:43-44），随后才 `HandleToolbarCommand`。点「完成」时文字未提交，compose 不含该文字，用户看到框里的字却不在结果里。
- 点画布其他位置同样 `DestroyTextEditor()`（EditInput.cs:50），无「提交/取消」提示。
- 对照：`CommitTextEditor`（EditApply.cs:202-227）只在 Enter 时调用（EditApply.cs:142-143）。

### D6（低-中，跨线程无版本）

- 事实链：`TryGetCurrentPng`（Export.cs:54-68）在 UI 线程先读 `_editMode`，false 则直接返回 `pin._pngBytes`；true 则 PostMessage 由贴图线程 compose。
- 写序：进入 `_editMode=true`（Edit.cs:126）→ 清 annotations（:127）；退出 `_pngBytes` 先换（EditApply.cs:93）→ `_editMode=false`（Edit.cs:187）。因此非编辑读到的 bytes 要么是提交前、要么是提交后，**不会撕裂**；编辑态读会走 compose，结果至少与 `_pngBytes` 同源。
- 缺口：`PinRecord`（PinService.cs:27）没有 revision，UI 线程拿到 `(bytes)` 无法判断是不是自己请求时的版本；编辑期间 `PublishPinState` 不调用，管理器缩略图保持旧图（只能靠动作入口的 `TryGetLivePng` 取新图）；反过来，导出请求超时（1000ms）后返回 Unavailable，调用方也无法区分「慢」和「失败」。
- 设计结论：以 `EditRevision` 关死；不为此加锁（锁会把 UI 线程阻塞在贴图线程上）。

### D7（低，失败静默空白）`RebuildBaseScaled` 解码失败留下空白画布

- `Edit.cs:374-386`：先 `new Bitmap(width,height)`（:379），再 `DecodeSource()`（:380）；解码失败返回 null 时直接 `return`（:381-382），`_baseScaled` 是空位图，`_pngBytes` 未变，界面显示空白，无提示（只有 `DecodeSource` 内部日志 `Pin 解码源图失败`，Render.cs:262）。

### D8（低，资源）编辑态关窗不释放 `_baseScaled` / `_editFont`

- `WM_CLOSE`（Window.cs:352-356）不调 `ExitEditMode`，`_baseScaled`（可能原图大小）与 `_editFont`（Hfont）等只能等 GC/finally；`Run` 的 finally 只释放屏幕/目标 DC 与 DIB（Window.cs:104 → Render.cs:325-330）。不构成永久泄漏，但峰值内存与 GDI 句柄会多留一段时间。

### D9（低，可接受但需写明）提交与复制的时序

- `_applyingEdit` 只护 `ApplyEdit`；Ctrl+C / 裁剪 / OCR 在贴图线程消息队列里串行，不会真并发。提交后异步复制回调（`PostCopyResult`）可能晚于用户下一次编辑，语义上是「复制了提交时的新图」，可接受，但设计文档要把它定为「提交的副作用，不回滚」。

---

## 3. 类型草案

### 3.1 分层与职责

| 层 | 类型 | 职责 | 不做什么 |
|---|---|---|---|
| Core（可测） | `EditRevision`、`EditNumbering` | 版本号；序号规则（空→1，否则 max+1；清空→1） | 不引用 App 的 Annotation / Bitmap |
| App 服务 | `EditBase`、`EditOp`、`EditDocument` | 不可变文档：基底 PNG + 操作顺序 + 草稿 + 序号 + revision；提供 `ComposePng()` 唯一投影 | 不持有 Bitmap、不碰窗口/渲染 |
| App 提交 | `EditCommit` / `EditCommitResult` | 唯一提交点：compose 成功才允许换基底；失败返回原因 | 不直接写 `_pngBytes`、不发 PinRecord（由 NativePinWindow 执行这两步，保证副作用集中） |
| 视图适配 | `NativePinWindow.Edit*` | 把指针/工具栏事件翻译成 `EditDocument` 操作；渲染读 document | 不再自己维护 `_annotations/_draft/_numberCounter` |

### 3.2 签名草案

```csharp
// src/PrtScr.Core/EditRevision.cs
public readonly record struct EditRevision(uint Value)
{
    public static EditRevision Initial => new(1);
    public EditRevision Next() => new(Value + 1);
}

// src/PrtScr.Core/EditNumbering.cs  （纯函数，可单测）
public static class EditNumbering
{
    /// <summary>空列表返回 1；否则返回现有最大序号 + 1（撤销后可复用被删掉的尾号）。</summary>
    public static int NextAfter(IReadOnlyList<int> numbers);
}
```

```csharp
// src/PrtScr.App/Services/EditDocument.cs
public sealed record EditBase(byte[] PngBytes, int PixelWidth, int PixelHeight, EditRevision Revision);

public enum EditOpKind { Add, Clear }

public sealed record EditOp(EditOpKind Kind, Annotation? Annotation);

public sealed class EditDocument
{
    public EditBase Base { get; }
    public IReadOnlyList<EditOp> Ops { get; }
    public Annotation? Draft { get; }
    public int NextNumber { get; }

    public static EditDocument Start(EditBase editBase);
    public EditDocument WithAnnotation(Annotation annotation);   // 抬笔加入；序号操作同时推进 NextNumber
    public EditDocument WithDraft(Annotation? draft);
    public EditDocument Undo();                                  // 弹最后一个 Add；Clear 也作为一条 Op 可弹
    public EditDocument Clear();                                 // 记一条 Clear Op，可 Undo
    public byte[]? ComposePng();                                 // 唯一投影：AnnotationRenderer.Render(Base + Ops + Draft)
    public EditDocument Commit(byte[] composedPng);              // 新基底；Ops 清空；Revision = Base.Revision.Next()
}
```

```csharp
// src/PrtScr.App/Services/EditCommit.cs
public enum EditCommitReason { Apply, ApplyAndSaveAs }

public sealed record EditCommitResult(EditDocument Document, byte[]? PngBytes, string? Failure)
{
    public bool IsSuccess => PngBytes is not null;
}

public static class EditCommit
{
    /// <summary>compose 成功才返回 PngBytes；失败时返回同一个 Document + Failure，调用方必须保持编辑态。</summary>
    public static EditCommitResult TryCompose(EditDocument document, EditCommitReason reason);
}
```

`NativePinWindow` 侧只保留一个字段 `EditDocument? _document`（编辑态非 null）；提交顺序固定为：

1. `var result = EditCommit.TryCompose(_document, reason);`
2. 失败：`RebuildBaseScaled()` + `ShowHint("标注合成失败，已保留编辑内容，请重试", 2600)` + 日志，**不换 `_pngBytes`、不退出**（保持 B03）。
3. 成功（单块、不抛异常）：`_pngBytes = result.PngBytes; _document = result.Document; PublishPinState(); ExitEditMode(apply:true);`。
4. 副作用（复制、另存）：在提交之后各自 try/catch + 提示，失败不回滚，日志明确 `Pin: edit commit side-effect failed op=clipboard|save-as`。

### 3.3 状态机与不变量

```
Idle --Enter(base rev N)--> Editing(doc=Start(base))
Editing --Add/Draft/Undo/Clear--> Editing(doc 新实例)
Editing --Commit ok--> Idle(base rev N+1, ops 空)
Editing --Commit fail--> Editing(doc 不变)
Editing --Cancel--> Idle(base rev N)
Editing --Close confirm--> Idle(base rev N，已提交部分不回滚)
```

不变量（写进类型注释，代码评审按此勾）：

1. `_pngBytes` 在编辑期只有一个写点（提交成功）；`Enter/Navigate` 之外的写都是 bug。
2. `_baseScaled` 必须由 `Document.Base.PngBytes` 缩放而来；基底变化即重建，不允许残留旧图。
3. 绘制顺序 = `Ops` 顺序；`Draft` 永远最后画，且永不进入 `Ops`。
4. `NextNumber` 由 `EditNumbering.NextAfter(已有序号)` 推出；Undo 后自动可复用尾号；Clear 后回到 1。
5. `EditRevision` 单调递增；只有 `Commit` 推进；`PinRecord.Revision` 与 `PngBytes` 同一次发布写入。
6. 提交失败文档不变（含 Draft），界面提示，不退出标注。
7. 跨线程只传 `(byte[] png, EditRevision rev)`，不传 `Annotation` / `Bitmap` / `EditDocument` 实例。
8. 取消/关窗不改基底；未提交的 Ops 丢弃前必须经用户确认（D1 的修复项）。

### 3.4 与 Export / TryComposeCurrentPng 的关系（合并还是分层）

**结论：分层，共用同一个 compose 实现，不合并成一个类型。**

- `EditDocument.ComposePng()` 是唯一烘焙实现（内部仍调 `AnnotationRenderer.Render`，不复制渲染代码）。
- `TryComposeCurrentPng()`（Export.cs:130-159）保留为**只读投影**的薄封装：编辑态 → `_document.ComposePng()`；非编辑态 → `_pngBytes`。这样 P-01 已改好的四个出口（复制/OCR/裁剪/另存）零改动。
- `EditCommit.TryCompose` 只做「compose + 结果包装」，不写窗口状态；换基底、发布 PinRecord、退出编辑仍由 `NativePinWindow` 完成（保持视图层对 GDI/窗口的所有权）。
- 若把 Commit 塞进 Export，会让「只读导出」带上「可提交」语义，管理器/OCR 就会有机会误提交，故不合并。

### 3.5 线程与所有权

- `EditDocument` 是不可变对象；`_document` 字段只在贴图线程替换（引用写原子）。
- `ComposePng` 返回新 `byte[]`；调用方持有所有权，贴图线程不缓存返回数组。
- `EditRevision` 随 `PinRecord` 与 `PinExportResult` 发布；UI 线程只比较 revision，决定是否丢弃旧结果。
- 不给 `_document` 加锁：UI 线程不直接读它，只经 `TryGetCurrentPng` 投消息。

---

## 4. 迁移三步（每步可单独交付）

### 步骤 1：只加类型与纯规则，行为零变化

改动：

- 新增 `src/PrtScr.Core/EditRevision.cs`、`src/PrtScr.Core/EditNumbering.cs`（见 3.2）。
- 新增 `src/PrtScr.App/Services/EditDocument.cs`、`EditCommit.cs`（只编译，不接线）。
- 新增 `src/PrtScr.Core.Tests/EditDocumentRulesTests.cs`：`NextAfter` 空→1、[1]→2、[1,2,3]→4、撤销至 [1,2]→3（复用）、[3,7]→8；`EditRevision.Next` 单调；`Initial` 语义。

判据：

- `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=.../out-p13a-step1/` → 0 错 0 警（走 `.tmp/build-guard.ps1`，禁 `-t:Compile`）。
- `dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug` → 全过；HEAD 基线 89 条，本设计新增用例数在此基础上累加（并行任务可能继续抬高基数，判据是「全过且不少于基线+新增」）。
- `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → rc=0。
- `grep -n "_document" src/PrtScr.App/Views/NativePinWindow*.cs` → 0 命中（未接线）。

风险：`EditDocument` 依赖 `Annotation`（App 类型），不能放进 Core；可测的只有 revision/序号两个纯规则，其余靠步骤 3 的行为矩阵。这是本设计的已知测试边界。

### 步骤 2：接线（唯一有意行为变化在此步）

改动：

- `NativePinWindow` 增加 `EditDocument? _document`；`EnterEditMode` 构造 `EditDocument.Start(new EditBase(_pngBytes, _pixelWidth, _pixelHeight, _revision))`；`ExitEditMode` 置 null。
- 所有写点改为 document 操作：`EditInput.cs:69-76/:88-109/:226-228`、`EditApply.cs:37-45/:196/:222`、`EditPaint.cs:151-156`；`EditRender` 读 `doc.Ops`/`doc.Draft`/`doc.NextNumber`；`RebuildBaseScaled` 改读 `doc.Base`。
- `ApplyEditCore` 改调 `EditCommit.TryCompose`，按 3.2 的固定顺序写回。
- `TryComposeCurrentPng` 改委托 `_document.ComposePng()`（非编辑态仍直出 `_pngBytes`）。
- `PinRecord` 增加 `EditRevision Revision`；`PinExportResult` 增加 `Revision`；`TryGetCurrentPng`/`CompleteExportRequest` 带上。
- 有意行为变化（需在发布说明/实测清单里写明）：撤销尾号复用、清空可撤销；重入仍从 1 开始（与现状一致，未修重号）。

判据：

- 三条门禁同上（build / tests / format）。
- `grep -n "_annotations\|_draft\|_numberCounter" src/PrtScr.App/Views/NativePinWindow.Edit*.cs` → 只允许出现在 `EditDocument.cs` 的适配代码；旧字段声明删除后应为 0。
- 行为矩阵（人工，贴图窗实测）：①画矩形/箭头/画笔/文字/马赛克/序号各一条 → 完成，结果与预览一致（重点单点画笔与马赛克）；②撤销最后一条、撤销清空、清空后可撤销；③序号 1,2,3 → 撤销 3 → 新序号应为 3；④编辑中复制/OCR/裁剪/另存各一次，均取当前画面；⑤compose 失败注入（临时改坏 Base 解码）→ 提示保留编辑；⑥编辑中关闭 → 仍会丢（D1 未修，步骤 3 处理）。

风险：一次性替换四个字段，diff 面大；建议按「document 字段先并存、逐出口切换」的提交粒度，但对外仍算步骤 2 一个交付。

### 步骤 3：删旧 API、合并渲染、补关窗确认

改动：

- 删除 `_annotations` / `_draft` / `_numberCounter` / `_baseScaled` 直写路径与 `UndoEdit` 旧实现。
- 抽 `AnnotationGeometry`（矩形/箭头头/笔宽/文字位置/马赛克块换算）供 `DrawAnnotationShape` 与 `AnnotationRenderer.DrawAnnotation` 共用，删掉重复几何；保留两套光栅化（预览在缩放位图、导出在原图）。
- D1 关窗确认：`WM_CLOSE`（Window.cs:352-356）与菜单 CmdClose（Interaction.cs:199-201）在 `_editMode && doc.Ops.Count>0` 时弹确认；若 lead 要求本批最小，可拆 P-13b，仅留接口。
- 管理器消费 `Revision`：`TryGetLivePng` 返回 `(bytes, revision)`，`PinManagerWindow` 对过期结果重取/忽略。

判据：

- 三条门禁同上。
- `grep -n "_annotations\|_draft\|_numberCounter\|_undoStack" src/PrtScr.App/Views/NativePinWindow*.cs` → 0 命中（旧字段清零）。
- `grep -n "DrawAnnotationShape" src/PrtScr.App/Views/` → 只剩一个实现；几何函数被两处引用。
- 行为矩阵扩展：单点画笔预览=烘焙；马赛克颗粒在 100%/50%/200% 缩放下观感一致（几何同源）；文字抗锯齿一致；关窗确认三条路径（有标注/无标注/提交中不可关）；管理器动作取到最新 revision。

风险：合并几何会触及渲染热路径，需用截图对比（同图同参数）验证像素差异可接受；关窗确认涉及 UI 线程与贴图线程交互（`MessageBox`/`TaskDialog` 在贴图线程弹会阻塞消息循环，需用 `ShowHint` 式非阻塞或 `MessageBox` + 确认后关闭）。

---

## 5. 未决问题（6 条，每条给推荐答案）

1. **关窗确认算不算 P-13a 范围？** 推荐：不算，另立 P-13b 最小单（`WM_CLOSE` + 菜单关闭两处，`_editMode && Ops.Count>0` 弹确认）。理由：D1 是独立用户可见行为，和内部重构混在一起会让评审无法区分回归来源。
2. **撤销粒度到哪？** 推荐：操作级 + 「清空」作为一条可撤销 Op；提交不可撤销，但提交前/关窗前确认；不引入完整快照栈（内存与复杂度不值得）。
3. **重入时的序号怎么办？** 推荐：新编辑会话从 1 开始（与现状一致，因为无法可靠解析已烘焙序号）；在提示/文档写明「已烘焙编号不会被新会话延续」。若要连续编号，另立单加「重编号」工具。
4. **预览与导出是否合并成一套渲染？** 推荐：抽共用几何（`AnnotationGeometry`）+ 共用 `AnnotationRenderer` 的绘制规则，但保留预览的缩放位图光栅化；不为了预览把整图按原尺寸重渲染（大图性能不可接受）。
5. **`EditRevision` 是否进 `PinRecord` / `PinExportResult`？** 推荐：进，类型 `uint`；发布 `PngBytes` 与 `Revision` 必须同一次 `UpsertPin`；管理器/OCR 拿到旧 revision 时丢弃并重取。
6. **工具/颜色/字号是否持久化？** 推荐：本批不做（现状每次进入默认矩形/红/18）；若主人要求，另立设置单（`editTool`/`editColor`/`editTextSize` 进 `AppConfig`，需 ConfigService + 设置页改动，超出本设计）。

---

## 附：证据索引（本设计引用到的文件）

- src/PrtScr.App/Views/NativePinWindow.cs（`_pngBytes` :268、PublishPinState :336-357、Navigate :412-476、PinClipboardHelper :481-514）
- src/PrtScr.App/Views/NativePinWindow.Edit.cs（状态字段 :60-100、EnterEditMode :113-179、ExitEditMode :181-217、RebuildBaseScaled :374-386）
- src/PrtScr.App/Views/NativePinWindow.EditApply.cs（ApplyEdit :61-75、ApplyEditCore :77-106、文字提交 :202-227、DestroyTextEditor :229-244）
- src/PrtScr.App/Views/NativePinWindow.EditRender.cs（RenderEditMode :17-82、DrawAnnotationShape :84-149、工具栏 :151-341）
- src/PrtScr.App/Views/NativePinWindow.EditInput.cs（指针处理 :15-230）
- src/PrtScr.App/Views/NativePinWindow.EditPaint.cs（撤销 :151-156、马赛克 :174-225、调色盘 :26-97）
- src/PrtScr.App/Views/NativePinWindow.Export.cs（TryGetCurrentPng :54-68、RequestCurrentPng :70-99、TryComposeCurrentPng :130-159）
- src/PrtScr.App/Views/NativePinWindow.Interaction.cs（菜单 :136-203、裁剪 :209-234、另存 :287-298、OCR :301-311）
- src/PrtScr.App/Views/NativePinWindow.Render.cs（RenderAndPresent :22-47、DecodeSource :250-265、Present :278-296）
- src/PrtScr.App/Views/NativePinWindow.Window.cs（WndProc :148-365、WM_CLOSE :352-356）
- src/PrtScr.App/Services/AnnotationRenderer.cs（Render :12-41、DrawAnnotation :43-108、ApplyMosaic :124 起）
- src/PrtScr.App/Services/PinService.cs（PinRecord :25-41、UpsertPin :81-86）
- src/PrtScr.App/Views/PinManagerWindow.cs（TryGetLivePng :176-188）

（完）

---

## 追记 · 2026-10-30 22:2x（lead 拍板，覆盖 §3.2 的落点）

P-13c 第一步（task-31）实测后，§3.2 原定的「EditDocument 放 App 并直接持有 Annotation」被否，理由与替代方案如下：

| 原设计 | 定稿实现 | 理由 |
| --- | --- | --- |
| EditDocument 在 App，直持 Annotation | `EditDocument<TAnnotation> where TAnnotation : class, IEditNumbered` 放 Core | Core 不得依赖 App 类型；Annotation 只需实现 IEditNumbered 即可闭合 |
| Ops 单列表兼作绘制顺序与撤销历史 | 「可见标注序列 Annotations」+「撤销历史 UndoHistory」分离 | 不变量 3（草稿最后画、不进 Ops）与撤销语义解耦；绘制顺序由 GetDrawOrder() 给出 |
| ComposePng/EditCommit 属 EditDocument | 纯状态 Commit/TryCommit 在 Core，合成留 App（依赖 AnnotationRenderer/System.Drawing） | 合成涉及 GDI 与视图，不进 Core |

定稿类型（7 个，见 src/PrtScr.Core/Edit/）：EditRevision、EditNumbering、IEditNumbered、EditBase、EditOperation<TAnnotation>+EditOpKind、EditDocument<TAnnotation>、EditPublishedSnapshot。62 条用例；Core.Tests 268。
步骤 2（接线）另立 task-33：Annotation:IEditNumbered、NativePinWindow 改走 EditDocument<T>、删掉第二套撤销语义。
Redo 未实现（设计稿与本阶段均未要求）。

# C-残2 · 标注工具栏「取消」按条件变色（P2-5）

- 日期：2026-11-21
- 单号：C-残2（P2-5）
- 执行：maid-c2-cancel（子代理）
- 写域：src/PrtScr.App/Views/NativePinWindow.EditRender.cs（仅此一文件；另加本报告）

## 一、做了什么

1. 改 src/PrtScr.App/Views/NativePinWindow.EditRender.cs 的 DrawToolbar / ToolbarItemKind.Command / TextActions 文字分支：`item.Id == 23`（「取消」）不再无条件走 `cancelBrush`（#FFFFA8A0 危险红），改为仅在 `HasPendingAnnotations` 为真时走 `cancelBrush`，否则落到中性 `commandBrush`（#FFE8E8E8，与「撤销」「清空」「另存」一致）。
2. 判定复用视图层既有属性 `HasPendingAnnotations`（NativePinWindow.EditHistory.cs:26），未新增字段、辅助方法或依赖；该属性映射 Core 的 `EditDocument.HasPendingWork`（src/PrtScr.Core/Edit/EditDocument.cs:52）= `_annotations.Length > 0 || Draft is not null`，正好是「有未保存标注（已提交进 _annotations 的一笔，或正在画的 _draft）」。
3. 只动 id==23 分支的 brush 选择，并加一行中文注释说明理由；「完成」(22 白字 + 强调色底)、「撤销」(20)、「清空」(21)、「另存」(默认) 的颜色与 disabled 逻辑未动；DrawIcon 图标分支（无文字版工具栏）未动。

## 二、证据（改动行号与片段）

文件：src/PrtScr.App/Views/NativePinWindow.EditRender.cs（改动后共 518 行）

L247-251：
```csharp
                            // 「取消」只在有未保存标注时用危险色，平时与其它命令同为中性色，避免抢「完成」的视线。
                            var brush = disabled ? disabledBrush
                                : complete ? whiteBrush
                                : item.Id == 23 && HasPendingAnnotations ? cancelBrush
                                : commandBrush;
```

改动前（同位置 L247-250）：
```csharp
                            var brush = disabled ? disabledBrush
                                : complete ? whiteBrush
                                : item.Id == 23 ? cancelBrush
                                : commandBrush;
```

判定链证据：
- NativePinWindow.EditHistory.cs:26 `private bool HasPendingAnnotations => _document?.HasPendingWork == true;`
- src/PrtScr.Core/Edit/EditDocument.cs:52 `public bool HasPendingWork => _annotations.Length > 0 || Draft is not null;`
- 同一属性已被 NativePinWindow.CloseGuard.cs:23 用作「关窗前确认未应用标注」的判定，语义一致，未引入第二套口径。
- 「清空」disabled 判定仍在 L229-232（`21 => _annotations.Count == 0`），未改。

行尾/编码核对（PowerShell 读原始字节）：
- 改动前：len=22949，BOM 无（首字节 117,115,105 = `usi`），CRLF=517，孤立 LF=0，末尾 CRLF。
- 改动后：len=23133，BOM 无，CRLF=518，孤立 LF=0，末尾 CRLF。
- `git diff --ignore-cr-at-eol --numstat` = `2 1`（+1 行注释、+1 改行、-1 旧行），其余全部为行尾差异。

## 三、风险与遗留

- 未跑 `dotnet build`（按单要求：并发 build 会争抢 obj 中间产物，由女仆长串行复跑三绿）。编译正确性只做了静态核对：`HasPendingAnnotations` 是同类 private 属性（同一 partial class），表达式中使用无需额外 using。
- 未做真机视觉确认：无标注时「取消」应为 #FFE8E8E8，有标注或正在画一笔时变 #FFA8A0；需真机或截图验证。
- 状态切换预期：刚进标注（_annotations 空、无 _draft）→ 中性；画下任意一笔（草稿已存在即算）→ 红；撤销到空 / 清空 → 回中性；「完成」直接退出标注（NativePinWindow.Edit.cs:176 ExitEditMode → :200 EndEditDocument），下次进入是新文档，仍为中性。
- git diff 会把本文件整篇显示为改写：HEAD 中该 .cs 存的是 LF，工作树是 CRLF（本仓库 .cs 标准，与本单无关，我改动前该文件已是 CRLF 517 行）。女仆长复核请用 `git diff --ignore-cr-at-eol` 看真实内容差异。
- 未新增文件依赖、未改 .gitignore、未提交 git、未改 README；临时探针只写在 .tmp\ 下并已清理。

## 四、待确认

1. 口径确认：「有未保存标注」按本单文字取 `_annotations.Count > 0 || _draft != null`。若主人希望「撤销后只剩已提交/已发布内容」也视为未保存，需要额外比较 `_publishedRevision`，本单未要求，未实现。
2. 空闲态「取消」与「撤销/清空/另存」同为 #FFE8E8E8，完全等同，不含任何红调；若希望保留一点区分（例如略暗），需要主人给数值。

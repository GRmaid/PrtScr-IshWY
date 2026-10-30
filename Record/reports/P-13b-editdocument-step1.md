# P-13b：EditDocument 第 1 步 + 编辑中关窗确认（D1）

- 单号：task-24（rev2 → completed），负责人 maid-check，日期 2026-10-30
- 基线：HEAD `69616cc`（chore(release-prep): 版本号 1.3.1 与 README 更新）；工作树未提交
- 写域：`src/PrtScr.App/Views/NativePinWindow*.cs` 与 `Record/reports/**`；未动 Core/Capture，未 commit

## 一、做了什么

1. **D1 关窗确认**：新增 `src/PrtScr.App/Views/NativePinWindow.CloseGuard.cs`，把三个关闭入口（WM_CLOSE、贴图右键菜单、`PinService` 的 `PinCommand.Close`）统一收敛到 `TryClosePinWindow(string source)`。编辑中且仍有未应用标注时弹 MessageBox「放弃标注？」：点「是」销毁，点「否」/Esc/右上角取消则留在编辑态并 `ShowHint`，不丢标注；无标注或未进入编辑则保持原样直接关闭。
2. **撤销栈改为操作级**：新增 `src/PrtScr.App/Views/NativePinWindow.EditHistory.cs`，维护 `_editUndoStack`（`Add` / `Clear` 两种操作）；新增标注与清空全部标注都走统一入口记一步，撤销后按现存最大序号重算 `_numberCounter`（尾号可复用）。离开/重新进入编辑清空撤销栈；提交（烘焙）不可撤销。
3. **接线既有代码**：所有 `_annotations.Add` 改走 `AddAnnotation`；工具栏「撤销」按 `CanUndo` 启用、「清空」按画布是否为空启用；三处关闭入口改走 `TryClosePinWindow`；删除 `EditPaint.cs` 旧的 `UndoEdit`（仅 `RemoveAt`）。
4. **行尾修正**：两个新文件首次落盘为 LF，`dotnet format` 报 ENDOFLINE；已重写为 CRLF，全量 format 复跑 rc=0。
5. **范围说明（重要）**：设计 §4 步骤 1/2 的 `EditRevision` / `EditNumbering` / `EditDocument` / `EditCommit` 类型抽取需要写 `src/PrtScr.Core` 与 `src/PrtScr.App/Services`，本单写域只含 `src/PrtScr.App/Views`，故**未交付类型抽取**；本单只交付 D1 与已拍板的撤销语义。建议另派 P-13c 或扩写域（见 §五）。

## 二、证据

### 2.1 改动清单（文件:行）

| 文件 | 关键行 | 内容 |
| --- | --- | --- |
| `src/PrtScr.App/Views/NativePinWindow.CloseGuard.cs` | :17-67 | `TryClosePinWindow`：:20 `_closeConfirming` 防叠框；:23 `pending = _editMode ? _annotations.Count + (_draft is null ? 0 : 1) : 0`；:24-30 `pending==0` 直接关闭并记 `Pin: close source=... edit=... pending=0`；:40-46 MessageBox `MB_YESNO\|MB_ICONWARNING\|MB_DEFBUTTON2\|MB_SETFOREGROUND`；:54-61 取消 → `Pin: close canceled source=... pending=N` + `ShowHint("已取消关闭，标注仍在编辑中", 1800)` + SetFocus；:63-66 确认 → `Pin: close discarded ...` + `DestroyWindow` |
| `src/PrtScr.App/Views/NativePinWindow.EditHistory.cs` | :12-87 | :12-16 `EditUndoKind{Add,Clear}`；:18 `EditUndoEntry`；:20 `_editUndoStack`；:22 `CanUndo`；:24 `ResetEditUndo`；:27-31 `AddAnnotation`；:34-42 `ClearAnnotations`（快照后清空）；:45-75 `UndoEdit`（:56 `FindLastIndex(ReferenceEquals)`、:62-68 恢复清空快照、:72 `SyncNumberCounter`、:74 日志 `Pin: undo remaining=... annotations=...`）；:78-87 `SyncNumberCounter` 取现存 Number 最大值 |
| `src/PrtScr.App/Views/NativePinWindow.Edit.cs` | :128、:208 | 进入/退出编辑清空撤销栈 |
| `src/PrtScr.App/Views/NativePinWindow.EditApply.cs` | :37-42、:219 | 撤销改 `if (CanUndo) UndoEdit()`；清空改 `ClearAnnotations()`；文字提交改 `AddAnnotation(annotation)` |
| `src/PrtScr.App/Views/NativePinWindow.EditInput.cs` | :77、:228 | 序号标注、指针抬起提交均改走 `AddAnnotation` |
| `src/PrtScr.App/Views/NativePinWindow.EditRender.cs` | :226-232 | 撤销/清空按钮启用条件：`20 => !CanUndo`、`21 => _annotations.Count == 0` |
| `src/PrtScr.App/Views/NativePinWindow.EditPaint.cs` | 原 :151-156 删除 | 移除旧的 `UndoEdit`（仅 `RemoveAt(Count-1)`） |
| `src/PrtScr.App/Views/NativePinWindow.Window.cs` | :352-356 | `WM_CLOSE` → `self.TryClosePinWindow("wm-close")` |
| `src/PrtScr.App/Views/NativePinWindow.Interaction.cs` | :199-201 | 菜单 `CmdClose` → `TryClosePinWindow("menu")` |
| `src/PrtScr.App/Views/NativePinWindow.Commands.cs` | :94-96 | `PinCommand.Close` → `TryClosePinWindow("command")` |

### 2.2 关窗三路径行为表（验收项 5）

| 路径 | 触发 | 有未应用标注时的行为 | 日志 | 窗口 |
| --- | --- | --- | --- | --- |
| 确认放弃 | 上述三入口 → MessageBox 点「是」（默认按钮是「否」） | `PinSnapGuide.Hide()` + `DestroyWindow` | `Pin: close discarded source=<wm-close\|menu\|command> pending=N` | 销毁 |
| 取消 | 点「否」/ Esc / 点对话框右上角 | 保留编辑态，标注与草稿不动，`ShowHint("已取消关闭，标注仍在编辑中", 1800)`，焦点回贴图窗 | `Pin: close canceled source=... pending=N` | 保留 |
| 无标注 / 未进入编辑 | 任意入口 | 不弹框，直接关闭 | `Pin: close source=... edit=<True\|False> pending=0` | 销毁 |

补充：双击关窗（`Window.cs:178-181`）已有 `when !self._editMode` 守卫，编辑中双击不会关闭，因此无需二次确认。退出编辑但保留窗口的路径（完成/取消标注）不经过本守卫，行为不变。

### 2.3 撤销栈语义变化表（验收项 5）

| 场景 | 改前 | 改后 |
| --- | --- | --- |
| 撤销一次 | `_annotations.RemoveAt(Count-1)`，无栈 | 操作级栈：撤销最后一次「新增」或「清空」 |
| 清空全部后撤销 | 不可撤销（标注已丢） | 可撤销，快照整体恢复 |
| 撤销序号标注后的编号 | `_numberCounter` 只增不减，继续跳号 | `SyncNumberCounter()` 取现存最大编号，尾号可复用 |
| 提交（完成/另存） | 不可撤销 | 仍不可撤销（按 lead 拍板） |
| 离开编辑 / 重新进入 | 无栈 | `ResetEditUndo()` 清空，不跨会话复活；重入序号仍从 1（按拍板不修重号） |
| 撤销按钮可用性 | 仅看 `_annotations.Count` | `CanUndo`（撤销栈非空）；清空按钮看画布是否为空 |

### 2.4 `grep -n "DestroyWindow" src/PrtScr.App/Views/NativePinWindow*.cs`（验收项 4）

```
NativePinWindow.CloseGuard.cs:28:            _ = PInvoke.DestroyWindow(_hwnd);   // pending==0 的直关路径
NativePinWindow.CloseGuard.cs:65:        _ = PInvoke.DestroyWindow(_hwnd);       // MessageBox 点「是」后
NativePinWindow.EditApply.cs:232:            _ = PInvoke.DestroyWindow(new HWND(_editControl));  // 文字编辑框，不是贴图窗
NativePinWindow.Window.cs:180:                        _ = PInvoke.DestroyWindow(hwnd);              // 双击关窗，前置 when !self._editMode
```

结论：贴图窗的销毁只剩 `CloseGuard.cs:28`（无标注，无可丢内容）与 `:65`（用户确认放弃）两处；`EditApply.cs:232` 是文本编辑子窗口；`Window.cs:180` 被 `when !self._editMode` 守卫（编辑中不触发）。

### 2.5 门禁（2026-10-30 22:0x，工作树含并行任务已完成的改动，未提交）

1. 整包 build（`.tmp/build-guard.ps1 -Tag p13b4`，OutDir `.tmp/out-p13b/`）：
   ```
   PrtScr.App -> E:\Explore\GoodGood\PrtScr\.tmp\out-p13b\IshWY.dll
       已成功生成。
       0 个警告
       0 个错误
   已用时间 00:00:16.02
   BUILD-GUARD: EXIT=0 elapsed=16s tag=p13b4
   ```
2. Core.Tests（`-Tag p13btest2`，`dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo`）：
   ```
   已通过! - 失败: 0，通过: 206，已跳过: 0，总计: 206，持续时间: 99 ms - PrtScr.Core.Tests.dll (net10.0)
   BUILD-GUARD: EXIT=0 elapsed=2s tag=p13btest2
   ```
3. 全量格式（`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore`）：`FORMAT-RC=0`（无输出）。
4. 未跑 `--smoke`（会写主人真实 data/，按纪律禁止）；未 commit/push。

### 2.6 行尾检查

`NativePinWindow.CloseGuard.cs` 69 行、`NativePinWindow.EditHistory.cs` 89 行，均为 CRLF（`fs.readFileSync` 校验 `\r\n` 存在，逐行重写后 format rc=0）。

## 三、进行中

无。本单代码与门禁已完成，报告落盘后即 complete。工作树另有 task-26（B-3b，`ScreenCapturer.cs`）、task-27 在并行推进，不在本单写域。

## 四、风险与遗留

1. **未做真机交互验证**：MessageBox 的三条出口（是/否/Esc）与撤销栈按钮均只做了静态审查与编译验证，未实际点击贴图窗；建议主人实测按 §2.2 三条路径各走一遍。
2. **确认框阻塞贴图线程**：`MessageBox` 是模态且会泵消息，确认框打开期间贴图窗自身的绘制/命令消息被延后（与现有其它 MessageBox 用法一致）；`_closeConfirming` 只防叠框，不防确认框期间收到的 `WM_APP` 命令排队。可接受，但若后续出现「确认框未关时贴图状态变了」类问题，应把命令处理改为确认后再消费。
3. **编辑中但零标注时直接关（已知行为，lead 2026-10-30 裁定可接受）**：当前 `pending==0` 不弹框——没有可丢的数据，弹框反而添噪；判据见 `CloseGuard.cs:23-30`。
4. **D2 提交非原子仍在**：`ApplyEditCore`（`EditApply.cs` 原 :93 一带）仍是「换 `_pngBytes` → 复制 → 退出 → 发布 → 另存」顺序执行，失败不回滚；本单未触碰，属设计 §4 步骤 2/3。
5. **类型抽取顺延**：`EditRevision`/`EditNumbering`/`EditDocument`/`EditCommit` 未创建，撤销栈仍是 `NativePinWindow` 的私有实现；后续步骤 2 接线时需要迁移，届时以 `EditHistory.cs` 的行为为兼容基线。
6. **门禁快照非冻结树**：上述三门禁跑在含 task-23/task-25 已完成改动的工作树上；task-26/task-27 仍在并行改文件，最终裁决需 lead 在工作树冻结后同 SHA 复跑。
7. **没有新增自动化测试**：写域只含 Views 与 Record/reports，Core.Tests 无法覆盖私有 Win32 窗口逻辑；撤销语义的纯逻辑部分可在 P-13c 抽到 Core 后补测（序号重算、清空恢复）。

## 五、待确认

1. **类型抽取是否另派 P-13c / 扩写域**：设计 §4 步骤 1 要求新建 `src/PrtScr.Core/EditRevision.cs`、`EditNumbering.cs` 与 `src/PrtScr.App/Services/EditDocument.cs`、`EditCommit.cs`，并加 Core 测试；本单写域不含 Core/Services，故顺延。请 lead 决定：另立 P-13c（推荐，保持单子写域清晰），还是扩 task-24 写域后由我补。
2. **零标注编辑态关窗**是否需要确认：当前不弹框直接关（`CloseGuard.cs:23-30`），请确认可接受，或改为「编辑态一律确认」。
3. **确认框文案**是否符合产品语气：现为「这张贴图正在标注，还有 N 条标注尚未应用。放弃这些标注并关闭贴图吗？」，按钮「是/否」，默认「否」。
4. **门禁终值**：请 lead 在 task-26/task-27 落定、工作树冻结后统一复跑；本报告记录的是 2026-10-30 22:0x 的快照。
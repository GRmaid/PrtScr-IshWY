# H-1 取景器拖拽中按 Tab 打断选区跟随 · 处置记录

日期：2026-11-07　来源：主人现场报告（m01579）

## 现象
取景器里按住左键拖框选区的过程中按 Tab，选区停在原地不再跟手；松手后再拖仍可继续，但一次拖拽被打断。

## 根因
- Tab 三条入口最终都调 `src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs:28` 的 `ToggleCaptureMode()`：
  - `CaptureOverlayWindow.Capture.cs:87`（低级钩子 HandleOverlayHotkey，case 0x09）
  - `CaptureOverlayWindow.Capture.cs:128`（FocusSink_KeyDown 的 VirtualKey.Tab）
  - `CaptureOverlayWindow.xaml.cs:147`（KeyInterceptor 消息层）
- `Mode.cs:37-42` 无条件重置 `_dragging/_dragMoved/_movingSelection/_moveWholeWindow/_resizingSelection/_resizeEdge/_wheelAccum`。
- 左键仍按下、指针捕获仍在（`Selection.cs:61` CapturePointer），但 `HandleMoved`（`Selection.cs:140-244`）看不到 `_dragging` ⇒ 只走 UpdateHover，选区冻结。
- 次生：`HandleReleased`（`Selection.cs:258-261`）首行 `if (!_dragging) return;` 早退，跳过 ReleasePointerCapture。

## 裁决
1. 拖拽中按 Tab：直接忽略并提示「松开鼠标后再按 Tab 切换模式」，不排队到松手再切模式（误触不应改变模式）。
2. 守卫只加在 `ToggleCaptureMode()` 首部，三条入口共用：`_closed` 检查之后、任何状态赋值之前。
3. 日志 token：`Overlay: mode switch ignored (dragging)`。
4. `HandleReleased` 的 `Layer.ReleasePointerCapture(e.Pointer);` 提到早退之前（幂等，防捕获泄漏）。
5. 非拖拽状态下的 Tab 行为（保留选区切模式、无窗口包住选区时保持常规）零改动。

## 验收
- grep 两处命中；守卫行号在首个状态赋值之前；`Mode.cs` 内 `_dragging = false` 归零；
- `ReleasePointerCapture` 行号小于早退行号；三条入口未改，仍调 `ToggleCaptureMode`；
- 门禁：整包 build 0 错 0 警、Core.Tests ≥268 全过、format rc=0；
- 主人手测：拖拽中 Tab 选区继续跟手并出现提示；松开后 Tab 正常切模式且选区保留。

## 派单
task-41（maid-impl），写域仅 `CaptureOverlayWindow.Mode.cs`、`CaptureOverlayWindow.Selection.cs`、`Record/reports/`。

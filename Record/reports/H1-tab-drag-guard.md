# H1 · 取景器拖拽中按 Tab 打断选区跟随（修复报告）

- 单号：task-41（H-1）
- 仓库：`E:\Explore\GoodGood\PrtScr`（HEAD `dc7b6a2`，未提交本次改动）
- 日期：2026-11-07
- 写域：`src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs`、`src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs`、`Record/reports/**`

## 1 做了什么

### 1.1 根因（复核 lead 定位，成立）

取景器里拖拽框选时按 Tab，选区停止跟随鼠标。三条 Tab 入口最终都调用 `src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs:28` 的 `ToggleCaptureMode()`：

| 入口 | 位置 | 说明 |
| --- | --- | --- |
| 低级键盘钩子 | `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:88` | `HandleOverlayHotkey` 的 Tab 分支 |
| FocusSink 按键 | `src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:130` | `FocusSink_KeyDown` 的 Tab 分支 |
| KeyInterceptor 消息层 | `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:148` | 窗口消息阶段拦截 |

`ToggleCaptureMode()` 原先无条件执行到拖拽状态重置（改后仍在 `:50-56`：`_dragging = false; _dragMoved = false; _movingSelection = false; _moveWholeWindow = false; _resizingSelection = false; _resizeEdge = ResizeEdge.None;`）。一旦 `_dragging` 被清零，`HandleMoved`（`src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs:144` 的 `if (_dragging)` 分支）就不再更新选区，转为走悬停/预高亮逻辑，表现即「选区冻在原地、鼠标继续移动也不跟」。

次生问题：`HandleReleased`（`src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs:258`）首行是 `if (!_dragging) return;`，指针捕获的释放写在该早退之后。拖拽状态被清掉后松手会跳过 `Layer.ReleasePointerCapture(e.Pointer)`，窗口可能一直抓着指针，后续悬停/点击判定异常。

### 1.2 改动一：`ToggleCaptureMode()` 首部加拖拽守卫

`src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs`（152 → 165 行）：

- `:32-33` `if (_closed) return;`（窗口已隐藏时直接返回，历史清理动作不再对已关闭窗口执行）
- `:35-37` 三行注释说明「拖拽中忽略，保留指针捕获与选区跟随」「三条入口都走这里」
- `:38-43` `if (_dragging) { App.Log("Overlay: mode switch ignored (dragging)"); ShowCopyToast("松开鼠标后再按 Tab 切换模式"); return; }`
- `:45` 起才是首个状态赋值 `var hadSelection = _hasSelection;`，`:48` 换模式、`:50-56` 原拖拽状态重置**原样保留**（非拖拽时行为不变）
- 守卫只加在这一处，三条入口共用，未改动任何入口本身

提示用的是取景器已有的 `ShowCopyToast(string text)`（`src/PrtScr.App/Views/CaptureOverlayWindow.Hud.cs:33`，同 partial class），与「已复制到剪贴板」等提示同一呈现位置，未新增 UI 元素。

### 1.3 改动二：`HandleReleased` 提前释放指针捕获

`src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs`：

- `:260-261` 两行注释说明「先幂等释放，再判断是否在拖拽」
- `:262` `Layer.ReleasePointerCapture(e.Pointer);`（从原 `_dragging = false;` 之前移到早退之前）
- `:264-265` `if (!_dragging) return;`
- `:267` `_dragging = false;`，其后原有逻辑（焦点回收、缩放归一、更新可视）未动

`ReleasePointerCapture` 对未捕获指针调用是幂等空操作，因此正常拖拽路径多调用一次不影响结果，只消除早退漏释放。

### 1.4 未改动项（守边界）

- 未改键位分派：三入口的 `ToggleCaptureMode();` 调用行原样（`Capture.cs:88`、`Capture.cs:130`、`xaml.cs:148`）
- 未改 XAML、未新增依赖、未改 .gitignore、未 commit
- 未改 `CaptureOverlayWindow.Capture.cs` 其余部分、`src/PrtScr.Core/**`、`CaptureHistoryService.cs`（同事在改）
- 未实现「拖拽中排队、松手后自动切模式」——按 lead 裁决，本轮为拖拽中直接忽略并提示

## 2 证据

### 2.1 门禁（全部实跑，2026-11-07 11:2x）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag tabdrag -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-tabdrag/"` | **EXIT=0**，`已成功生成。0 个警告 0 个错误`，20.52s |
| 单元测试 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag tabdragtest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` | **EXIT=0**，`失败: 0，通过: 268，已跳过: 0，总计: 268`（99ms） |
| 格式 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | **rc=0**，无输出 |

未使用 `-t:Compile`（不跑 WinUI XAML 代码生成，属假绿），未跑 `--smoke`（会写主人真实 data/）。

### 2.2 行号顺序判据（守卫在状态赋值之前，释放在早退之前）

| 判据 | 事实 |
| --- | --- |
| Mode.cs 守卫行号 < 首个状态赋值 | `:38`（`if (_dragging)`）< `:45`（`var hadSelection = _hasSelection;`）< `:50`（`_dragging = false;`） |
| Mode.cs 守卫行号 < 模式赋值 | `:38` < `:48`（`_captureMode = ...`） |
| Selection.cs 释放行号 < 早退 | `:262`（`Layer.ReleasePointerCapture`）< `:264`（`if (!_dragging) return;`） |

`try {` 在 `:30`、`_closed` 判断在 `:32-33`，二者都早于守卫；守卫返回前未触碰任何字段。

### 2.3 grep 判据（两条串各 1 处）

```
$ grep -rn "mode switch ignored (dragging)" src/PrtScr.App/Views/ | wc -l
1
$ grep -rn "松开鼠标后再按 Tab 切换模式" src/PrtScr.App/Views/ | wc -l
1
```

两条分别只出现在 `src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs:40` 与 `:41`。

### 2.4 三入口未被改动，仍调用 `ToggleCaptureMode()`

```
src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:88:                ToggleCaptureMode();
src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:130:                ToggleCaptureMode();
src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:148:                    ToggleCaptureMode();
```

### 2.5 改动量（忽略行尾差异后的真实 diff）

```
$ git diff --ignore-cr-at-eol --stat -- src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs
 .../Views/CaptureOverlayWindow.Mode.cs      | 13 +++++++++++++
 .../Views/CaptureOverlayWindow.Selection.cs |  5 ++++-
 2 files changed, 17 insertions(+), 1 deletion(-)
```

diff 内容即 §1.2/§1.3 所述：Mode.cs 新增 13 行守卫块，Selection.cs 新增 4 行（2 行注释 + 空行 + 释放语句）、删除 1 行旧释放语句。

### 2.6 行尾与 git 呈现（提交前必读）

- 两文件均为 UTF-8 + CRLF（`file` 报 `with CRLF line terminators`；Mode.cs 165 行对 165 个 CRLF，无双 CR、无裸 LF），符合 `.editorconfig:5` 的 `end_of_line = crlf`，故 `dotnet format` 可过。
- 仓库 `core.autocrlf=false`、无 `.gitattributes`，而 HEAD blob 是 LF（`git ls-files --eol` 显示 `i/lf w/crlf`），所以**普通 `git diff` 会把两文件显示为整文件重写**（Mode.cs 317 行、Selection.cs 1235 行），这是行尾差异，不是内容差异。
- 提交时请用 `git -c core.autocrlf=true add -- src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs`，可把 blob 归一化回 LF；已实测等价：`git -c core.autocrlf=true hash-object --path=…` 与「把工作树转成 LF 后再 hash」得到同一 blob（Mode.cs `a5717098…`、Selection.cs `a9aee772…`），HEAD 对应 blob 为 `fc233c8…` / `7aae697…`。

## 3 进行中（交接用）

### 已完成

1. 复核 lead 根因，确认三条 Tab 入口都收敛到 `ToggleCaptureMode()`。
2. `Mode.cs` 加 `_closed` + `_dragging` 守卫（`:32-43`），保留原重置块。
3. `Selection.cs` 把 `Layer.ReleasePointerCapture` 提到早退之前（`:260-262`）。
4. 三门禁全绿（§2.1），两条串各 1 处（§2.3），三入口未变（§2.4）。

### 下一步第一件事

按路径提交这两文件 + 本报告（命令见 §2.6；不要 `git add -A`，工作树里有同事在改的 `CaptureOverlayWindow.Capture.cs`、`src/PrtScr.Core/**`、`CaptureHistoryService.cs` 与 lead 的 `Record/ProjectLog.md`）。

### 下一步该读的文件

- `src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs:28-56`：守卫与保留的重置块
- `src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs:258-271`：释放顺序与后续焦点回收
- `Record/plans/H1-tab-drag-fix.md`（lead 的根因分析稿，未跟踪）

## 4 风险与遗留

1. **交互验收未做**：结论来自编译 + 代码走查 + grep，未在真机拖拽中实测（禁 `--smoke`，交互验收归主人）。
2. **提示条触发频度**：拖拽中连按 Tab 会每次都刷新提示条计时器（`ShowCopyToast`），行为与其他 toast 一致，未做额外节流；若觉得吵可后续加 300ms 去重。
3. **Tab 语义变化**：拖拽中 Tab 从此前的「切模式并打断拖拽」改为「忽略」，属于有意的行为变更；松手后 Tab 仍即时生效且保留选区。
4. **`_closed` 早退属新增语义**：此前窗口已隐藏时仍会执行整段重置；现在直接返回。该路径本就不会有可见效果，但严格说非零变化。
5. **指针捕获释放变为每次松手都调用**：`ReleasePointerCapture` 幂等，正常拖拽路径多一次空操作，开销可忽略。
6. **行尾差异**：见 §2.6。若 lead 用普通 `git add`，提交后 blob 会是 CRLF，下一轮 diff 会出现整文件噪声（内容仍正确）。
7. **无新增自动化测试**：Core.Tests 是纯 Core 层，取景窗指针/键盘路径无法覆盖，268 条未减少。

## 5 待确认

1. 拖拽中按 Tab 的提示文案「松开鼠标后再按 Tab 切换模式」是否需要改成更短的版本（当前 14 字，提示条宽度按内容自适应）。
2. 是否需要在松手后「补一次」被忽略的模式切换（本轮按 lead 裁决不做；若主人实测觉得别扭可另立单）。

## 主人实测步骤

1. 按截图热键（默认 F1）起取景，按住左键拖出一个选区，**拖拽过程中**按 Tab：预期选区继续跟随鼠标、不再冻结，底部出现「松开鼠标后再按 Tab 切换模式」提示，模式徽标不变。
2. 保持拖拽状态不松手，连续按 Tab 3 次：预期每次都被忽略并刷新提示，选区仍跟手。
3. 松开鼠标，再按 Tab：预期正常在「窗口 / 常规」间切换，已画选区与位置保留，可继续拖拽或回车确认。
4. 选中一个已有选区后拖动它（移动选区）过程中按 Tab，再在右下角拉伸缩放过程中按 Tab：预期同样被忽略、拖动/缩放跟手。
5. 取景中不放鼠标直接按 Esc：预期照旧退出取景（Esc 走取消路径，不受本次守卫影响）。

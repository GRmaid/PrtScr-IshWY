# H-2 · H1 Tab 拖拽守卫独立审查（只读）

- 角色：H-2 独立只读审查员（不使用 agent_teams_*，不派子代理）
- 审查对象：`E:\Explore\GoodGood\PrtScr` HEAD `5e74f62`，H1 改动提交 `52c5565`（2026-11-07 11:55:33）
- 审查时间：2026-11-07 13:4x
- 审查范围：`Record/reports/H1-tab-drag-guard.md`、`src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs`、`CaptureOverlayWindow.Selection.cs`、`CaptureOverlayWindow.Capture.cs`、`CaptureOverlayWindow.xaml.cs`；按需扩展 `CaptureOverlayWindow.xaml`、`CaptureOverlayWindow.Hud.cs`、`Services/KeyInterceptor.cs`、`Services/EscOverPinHook.cs`；只读核对 `Record/tools/H2-ArtifactProof.ps1`
- 工具限制（需 lead 知悉）：本会话工具集**不含** `team_task_list/get/update` 共享任务 API（仅有 `agent_teams_*`，按派单要求不使用），因此**未能 claim / 回写 task-46**；仍按已明确派单完成审查，任务状态与结论请由 lead 在共享任务面板更新。

## 1 结论

1. **H1 修复在源码层覆盖真实事件链，未发现可解释「拖拽中按 Tab 仍打断选区」的代码漏洞。** 三条 Tab 入口确实全部收敛到 `ToggleCaptureMode()`，守卫位于任何状态赋值之前；新框选 / 移动 / 缩放三种拖拽都置 `_dragging = true`；`HandleReleased` 提前释放指针捕获未发现副作用。
2. **主人 13:35 的失败实测不构成对 H1 的反证：实测对象是 2026-10-30 22:46 构建的旧二进制，早于 H1 提交 8 天。** 默认 bin 的 `IshWY.dll`（966144 字节 / SHA256 `CB3218…3E8C`）不含任何 H1 标记串；运行中实例 PID 8156（10:53:30 启动，早于 11:55:33 的提交 62 分钟）加载的正是该文件。owner 实测的是「没有 H1 的版本」。
3. 隔离基线 `.tmp/out-h2baseline/IshWY.dll`（967168 字节 / SHA256 `755C…EDB6`，13:36 产出）含 H1 的两条标记串——英文日志与中文提示各 1 处，且都落在**奇数字节偏移**，因而 `H2-ArtifactProof.ps1` 的 UTF-16 双对齐扫描是必要且正确的：只扫偶数偏移会假阴性。
4. **未发现需要修改产品代码的具体漏洞，本报告不提出代码改动。** 下面 §4 的风险项均为「既有行为（非 H1 引入）」或「验收环境/方法」问题；其中 R2、R3 若要处理属另一张单，不应混入本次返修。
5. 唯一阻断项是环境：必须**先关闭旧实例、重建、再重启**，否则任何实测都会再次评审到旧二进制（R1）。

## 2 证据

### 2.1 二进制与运行实例（本人独立实测，非引用他人报告）

| 产物 | 大小 | 修改时间 | SHA256 | H1 标记 |
| --- | --- | --- | --- | --- |
| `src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.dll` | 966144 | 2026-10-30 22:46 | `CB3218C50577FFE535CBD5A34451E3C6947F754BB6573F0101735C4C65393E8C` | **0 处** |
| `.tmp/out-h2baseline/IshWY.dll` | 967168 | 2026-11-07 13:36 | `755C8D615233C3027CDDDC0A2883B2EC87553F864240538744100A0AA087EDB6` | 英文 `mode switch ignored (dragging)` @606725（奇）；中文 `松开鼠标后再按 Tab 切换模式` @606787（奇） |

- 三条标记串在两个产物中都做了 UTF-16LE 任意偏移字节搜索与 ASCII/UTF-8 搜索：默认 DLL 三条全 0，ASCII 也 0；基线 DLL 两条 UTF-16LE 命中、ASCII 0（符合 .NET 字面量存 #US 堆 UTF-16LE 的预期）。
- 运行实例：`Get-Process IshWY` → PID 8156，exe `…\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\IshWY.exe`，StartTime 2026-11-07 10:53:30，其加载模块 `IshWY.dll` 的 `FileName` 与上表第一行**同一路径**。即「实测时进程里跑的 DLL = 无 H1 标记的 966144 字节文件」。
- 时间线：DLL 构建 10-30 22:46 → 进程启动 11-07 10:53:30 → H1 提交 11-07 11:55:33 → 主人实测 13:35。**实测晚于提交，但进程与 DLL 都早于提交。**

### 2.2 源码事件链（行号）

- 三条入口，全部只调用 `ToggleCaptureMode()`，无第 4 处（全仓 grep 仅 1 处定义 + 3 处调用）：
  - 低级键盘钩子：`src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:87-89` → `HandleOverlayHotkey` 的 `case 0x09`；入口前有 `_closed` 早退（`Capture.cs:76`）。钩子回调先 `DispatcherQueue.TryEnqueue`（`Capture.cs:68-71`），仍回到 UI 线程。
  - FocusSink：`Capture.cs:128-131`，且 `e.Handled = true`（Tab 不再进焦点导航）。
  - KeyInterceptor 消息层：`src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs:147-149`；拦截发生在 `Services/KeyInterceptor.cs:104-119`（`WM_KEYDOWN` 的 `0x09` 直接 `KeyDown?.Invoke` 并 `return 0`，不再下发）。
- 守卫位置与顺序：`src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs`——`:30` `try {`；`:32-33` `if (_closed) return;`；`:35-37` 三行注释；`:38-43` `if (_dragging) { App.Log(...); ShowCopyToast(...); return; }`；`:45` 才是首个状态赋值 `var hadSelection = _hasSelection;`；`:48` 换模式；`:50-56` 原拖拽状态重置块原样保留（非拖拽路径行为不变）。守卫返回前不触碰任何字段，不存在 TOCTOU（同线程同步执行）。
- 三种拖拽的 `_dragging` 置位齐全：`CaptureOverlayWindow.Selection.cs:94`（按住选区内部 = 移动）、`:108`（空白处 = 新框选）、`:391`（`BeginResize` = 缩放）。全仓 `_dragging` 赋值共 6 处属于本窗口，其中 false 只有 3 处：`xaml.cs:264`（`ResetSessionState`，仅 `Prepare` 时）、`Selection.cs:267`（`HandleReleased`）、`Mode.cs:50`（现在被守卫挡住）。**没有任何 PointerCaptureLost 处理会清 `_dragging`**（全仓 `PointerCaptureLost` 0 命中）。
- `HandleReleased`（`Selection.cs:258-321`）：`:260-261` 注释；`:262` `Layer.ReleasePointerCapture(e.Pointer);`；`:264-265` `if (!_dragging) return;`；`:267` `_dragging = false;`；`:270` 焦点回收。释放与捕获同元素（`Selection.cs:61` `Layer.CapturePointer(e.Pointer)`）。
- 焦点链：XAML `Root` 挂 `PointerPressed/Moved/Released/WheelChanged`（`CaptureOverlayWindow.xaml:7-14`）；Tab 语义只由 `FocusSink`（`xaml:237-244`）与消息层承担，XAML 里没有 Tab 的 `KeyboardAccelerator`（`xaml:246-251` 仅 Esc/Enter/Ctrl+S/Ctrl+C）；其余可交互控件均 `IsTabStop="False"`（`xaml:90/106/118/132`），**焦点导航没有第二个 Tab 停靠点**，即使 Tab 漏到 XAML 默认导航也不会移走焦点。
- 提示条不干扰拖拽：`CopyToast` 为 `IsHitTestVisible="False"`（`xaml:174-186`）；`ShowCopyToast`（`CaptureOverlayWindow.Hud.cs:33-66`）只改文本/位置/计时器，不碰 `_dragging`、不重设指针捕获、不聚焦。

### 2.3 `H2-ArtifactProof.ps1` 双对齐逻辑核对

- `:11` 从字节 0 起按 UTF-16LE 解码，`:12` 从字节 1 起解码（长度 `n-1`），`:18` 两段 `Contains` 取或。**对 UTF-16LE 起点在偶/奇任一字节的情形均无漏检**；本机实测 H1 两串都在奇数偏移（606725 / 606787），若删掉 `:12` 就会把有 H1 的基线误判成无 H1——双对齐不是冗余，是必需。
- .NET 编译器把字符串字面量放 #US 堆（UTF-16LE），所以对这类产物不需要 UTF-8 / UTF-16BE 扫描；脚本的编码假设成立。前提是单文件、未压缩、非 R2R（当前 Debug 产物满足）。
- 两处工具层缺口（不影响本次结论）：
  1. `:18` 只认 1 条标记串。建议 OR 上中文串 `松开鼠标后再按 Tab 切换模式`（本机已证基线含它），避免单串被裁剪/改写时出现假阴性。
  2. `:21-22` 只列出进程，未把进程**实际加载的模块**与 `artifacts` 关联。建议补 `(Get-Process IshWY).Modules | ? ModuleName -eq 'IshWY.dll' | % FileName`，并把该路径与 artifacts 的 Path 比对，否则「有 H1 的 DLL」与「进程在跑的 DLL」只是并列陈列，不构成同一性证明。
- 脚本本身正确，无需改写；以上是审计强度建议。

## 3 验证

**实际执行（只读）**：
- `git log/status/diff HEAD`：HEAD `5e74f62`；`git diff HEAD -- src/` **零差异**（工作树仅两份 docs 被改）；两个 H1 文件历史上只被 `6fb4d09`、`52c5565` 触碰；`git branch -a --contains 52c5565` 含 `main` 与 `origin/main`。→ 本次审查的源码就是当前最终版本，不存在「后来改了、评的是旧 diff」。
- 逐行读取 §2.2 所列 5 个主文件 + `Hud.cs` + `KeyInterceptor.cs` + `EscOverPinHook.cs`（`:390-470`）+ XAML 全链；全仓 grep `ToggleCaptureMode` / `Tab` / `_dragging` / `PointerCaptureLost` / `CapturePointer`。
- 用 Node 独立实现字节级扫描（`Buffer.indexOf(Buffer.from(s,'utf16le'))`，任意偏移 + sha256）：得到 §2.1 全部数字，与 owner 提供的两个大小/哈希一致。
- 读取 `Record/tools/H2-ArtifactProof.ps1` 全文（22 行）并核对逻辑。

**未执行（受只读约束，勿据此认为已通过）**：`dotnet build/test`、`--smoke`、`-t:Compile`、合成键鼠 / UIA、杀进程、commit/push。因此本报告**不声称构建通过，也不声称交互修复已生效**。
**未验证**：`.tmp/out-h2baseline` 是否由 HEAD 树精确重建（只证明它含 H1 源码，不证明与 HEAD 字节一致）；重建后的新哈希需由 lead 在关实例后重新记录。
**共享任务**：`team_task_list/get/update` 在本会话不存在（实测三者均非可调用函数），未 claim task-46；未使用 `agent_teams_*` 替代。

## 4 风险

- **R1（阻断验收，环境）**：旧实例 PID 8156 加载 10-30 的 DLL 并占着 `src/PrtScr.App/bin/x64/Debug`。不关实例就重建，要么构建失败，要么产出一个没被运行的 DLL，实测会再次得出「没修好」的假结论。**这是本次唯一的阻断项。**
- **R2（低，既有，非 H1 引入）**：`EscOverPinHook.cs:432-437` 只消费无修饰 Tab，注释写明 Shift+Tab 交给焦点导航；但 `KeyInterceptor.cs:107` 的 `0x09` 分支不区分修饰键，仍会吞掉并走 `ToggleCaptureMode()`（`xaml.cs:148`）。拖拽中按 Shift+Tab 会被守卫拦下并弹出「按 Tab」文案，语义略不贴。若后续处理：在 `KeyInterceptor.cs:107` 用 `GetKeyState(0x10)` 放行 Shift+Tab，或把守卫提示改成「松开鼠标后再切换模式」。**不建议并入本次返修。**
- **R3（低，既有）**：Enter 在拖拽中没有守卫——`Capture.cs:85/125/144` → `ConfirmCurrentTargetAsync`（`Capture.cs:535-564`，只判断 `_closed || _confirming`）→ `ConfirmAsync` 在 `Capture.cs:596` 直接 `HideForCapture()`。按住左键按 Enter 会隐藏取景并提交当前瞬时选区；Esc/右键取消同属「键盘中断拖拽」。是否为期望行为需产品裁决；若判定非期望，最小改动是在 `ConfirmCurrentTargetAsync` 首部加 `if (_dragging) return;`。**属另一张单，不宜混入 H1。**
- **R4（低）**：按住 Tab 连发会每次刷提示并各建一个 `DispatcherQueueTimer`（`Mode.cs:40-41`、`Hud.cs:50-60`）并各写一行日志。旧 timer 会 Stop、类型无 IDisposable，**无泄漏**，仅是日志/计时器噪声；可留待需要时加 300ms 节流。
- **R5（中，方法论）**：二进制标记只能证明「DLL 由含 H1 的源码构建」，**不能证明 UI 行为正确**。交互验收仍必须真机做；且每次验收前都要记录 DLL 哈希/模块路径/进程启动时间，否则会重演这次「评审旧版本」。
- **R6（低）**：`_closed` 早退是新增语义（`Mode.cs:32-33`）。窗口隐藏期间若仍有排队消息到达，不再执行原重置块；该路径本无可见效果，且 `ResetSessionState`（`xaml.cs:259-294`）会在下次 `Prepare` 全量复位，无状态残留风险。
- **R7（设计取舍）**：拖拽中被忽略的 Tab **不会在松手后补切**（有意）。若主人实测觉得别扭，属新单，不是缺陷。
- 未发现：守卫被绕过、`_dragging` 覆盖遗漏、提前释放导致 `PointerCaptureLost` 反噬、`e.GetCurrentPoint` 在释放后取值炸异常（`:320` 只在拖拽路径执行，且旧代码同样在释放之后取值，行为未变）。

## 5 真实用户验收步骤

前置：**由主人本人操作**（代理不注入键鼠、不杀进程）。

1. **关闭旧实例**：结束托盘/主窗（当前 PID 8156）。确认 `Get-Process IshWY` 为空——否则下一步会构建失败或产物不被加载。
2. **重建**：`dotnet build PrtScr.sln -c Debug`（或 lead 的 build-guard 输出到独立 `OutDir`）。不要用 `-t:Compile`（不跑 XAML 代码生成，属假绿）。
3. **产物自证**（三项都要，缺一不可）：
   - 新 `IshWY.dll` 大小 **≠ 966144**、SHA256 **≠** `CB3218…3E8C`；
   - 用 `Record/tools/H2-ArtifactProof.ps1`（或等价 UTF-16 双对齐扫描）确认 `H1Marker=True`；
   - `(Get-Process IshWY).Modules | ? ModuleName -eq 'IshWY.dll' | % FileName` 与刚构建的路径一致，且进程 StartTime 晚于构建完成时间。
4. **取景 → 新框选拖拽中按 Tab ×3，再按住 Tab ≥1 秒**：预期选区继续跟手、模式徽标不变、底部出现「松开鼠标后再按 Tab 切换模式」；日志出现 ≥3 行 `Overlay: mode switch ignored (dragging)`，且期间**没有** `Overlay: mode=` 切换行。
5. **保持按住不松手连续 Tab，然后松手**：预期不再补切；随后按 Tab 正常切模式且选区保留；移动鼠标时悬停高亮恢复更新；在空白处单击能清掉选区——这条同时验证「没有残留指针捕获」。
6. **移动中按 Tab**：已有选区内部按住拖动 → 按 Tab → 预期跟手；窗口模式下整窗选区只在真正移动时出现「已移动整窗选区，改为常规截图」，Tab 不触发它。
7. **缩放中按 Tab**：右下角拉伸 → 按 Tab → 预期跟手；松手后短边 ≥ 12 DIP（`MinSelectionSize`）。
8. **Shift+Tab / Ctrl+Tab**：Ctrl+Tab 放行给系统；Shift+Tab 当前会走 Tab 切换（拖拽中被忽略），与 R2 描述一致——**不作为本次验收的失败判据**。
9. **若仍失败**：先固定证据再定性——新 DLL 的 SHA256/大小/修改时间、进程 StartTime、模块路径、日志中 `mode switch` 与 `mode=` 行、Tab 序列（拖拽/移动/缩放、是否连按）。只有在「第 3 步三项自证齐全的新二进制」上复现，才可判定 H1 未生效。

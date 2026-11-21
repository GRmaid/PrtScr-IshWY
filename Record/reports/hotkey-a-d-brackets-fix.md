# 热键修复单：裸键 A/D 误触截图 + 取景器之外 [ ] 被吞

单号 hotkey-a-d-brackets-fix · 2026-11-21 · 写域仅两个 .cs · HEAD 5e74f62（未提交、未打 tag、未改 .gitignore、未新增依赖）

## 做了什么

### Bug 1 单独按 a/d 误触发截图/全屏截图 —— `src/PrtScr.App/Services/EscOverPinHook.cs`

根因：低级键盘钩子（WH_KEYBOARD_LL）用静态字段 `_ctrlDown/_altDown/_shiftDown/_winDown` 自记账修饰键状态，`ModifiersDown()`、`DescribeModifiers()`、`TryHandleOverlayKey()` 用 `_altDown || Down(0x12)` 的 OR 语义判定。钩子被系统静默摘除或漏掉修饰键 keyup 时 `_altDown` 残留为 true，此后裸键 A(0x41，Alt+A 的键位) 被当成 Alt+A 触发截图，裸键 D(0x44) 被当成 Alt+D 触发全屏截图。记账残留是误触根源，不是 GetAsyncKeyState 不可靠。

改动（570 → 550 行）：

1. 删除静态字段 `_ctrlDown`/`_altDown`/`_shiftDown`/`_winDown`。
2. 删除 `TrackModifier(uint vk, bool down)` 整个方法，以及它在 `HookProc` 里的唯一调用点。
3. 三处修饰键判定改为纯实时 `GetAsyncKeyState`：`var ctrl = Down(0x11); var alt = Down(0x12); var shift = Down(0x10); var win = Down(0x5B) || Down(0x5C);`（共 3 处 × 4 行）。
4. `HookProc` 去掉 keyup 分支与 `isUp` 变量，只处理 `WM_KEYDOWN`/`WM_SYSKEYDOWN`；随之失去引用的 `WM_KEYUP`/`WM_SYSKEYUP` 常量一并删除，并在回调内加两行注释说明修饰键只认 `GetAsyncKeyState`。
5. 类注释「回调只做记账和投递」改为「回调只做判定和投递」。

触发链路未变：钩子按 vkCode 命中 → `ModifiersDown()` → `TryClaim()`（300ms 去抖）→ `RaiseDeferred()`。改后关键行号：`Down` L345、`ModifiersDown` L351、`DescribeModifiers` L366、`TryHandleOverlayKey` L379、`HookProc` L516。

### Bug 2 取景器之外 [ ] 键被吞 —— `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs`

根因：`_closed` 初值为 false。`CaptureService.EnsureOverlay()` 在构造后立即 `WarmUpHidden()`（L300-313，`AppWindow.MoveAndResize(-10000,-10000,1,1)` + `Show()` + `Hide()`），`Hide()` 触发 Activated 处理器（L177-194）的 Deactivated 分支，因 `!_closed` 成立而 `SetOverlayKeysEscOnly(true)`，把 `EscOverPinHook._overlayKeysActive` 置为 2 并残留。预热结束取景窗已隐藏，低级钩子却仍在 escOnly 模式，取景器之外持续吞默认切窗键 `[`(0xDB) / `]`(0xDD)。

改动（465 → 468 行）：`_closed` 初值改为 true（原 L97 声明，现 L100 `private volatile bool _closed = true;`），上方 3 行注释记录原因。取景真正开始由 `Prepare` → `ResetSessionState()`（现 L264 `_closed = false;`）置 false，该路径未改；`CaptureOverlayWindow.Capture.cs` 的 `HandleOverlayHotkey` 首行 `if (_closed) return;` 因此在预热期正确短路，`HideForCapture` 的 `_closed = true` 语义不变。

## 证据

完整构建包含两个文件所在程序集，构建产物写入隔离目录 `.tmp/out-hotfix/`（不污染默认 out）。

| # | 判据 | 命令 | 结果 |
| --- | --- | --- | --- |
| 1 | 整包构建 0 错误 0 警告 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag hotfix -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-hotfix/"` | 「已成功生成。0 个警告 0 个错误」；dotnet 计时 45.57s；`BUILD-GUARD: EXIT=0 elapsed=47s tag=hotfix` |
| 2 | Core.Tests 不退化 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag hotfixtest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo"` | 「已通过! - 失败: 0，通过: 417，已跳过: 0，总计: 417」，190 ms；`BUILD-GUARD: EXIT=0 elapsed=6s tag=hotfixtest` |
| 3 | format 退出 0 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag hotfixfmt -Cmd "dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore"` | `BUILD-GUARD: EXIT=0 elapsed=16s tag=hotfixfmt`，无任何 diff 输出 |
| 4 | 静态核对 | 见下方脚本 | `1) 0  2) 0  3) 12  4) L100 true  VERDICT=PASS`，exit 0 |

判据 2 与单上写明的基线 327 存在差异：本仓库当前 `PrtScr.Core.Tests` 实有 417 条用例（与 P14b 报告的 417/417 一致），计数口径变化来自此前阶段新增用例，不是本单引入。417 条全部通过、0 失败 0 跳过，即无退化。

判据 4 静态核对脚本（`Select-String` 必须 `-CaseSensitive`，否则 `_winDown` 会误命中 `_windowNextVk`/`_windowNextMods` 之类的标识符）：

```powershell
$f1 = 'src/PrtScr.App/Services/EscOverPinHook.cs'
$f2 = 'src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs'
$legacy = @(Select-String -Path $f1 -Pattern '_ctrlDown|_altDown|_shiftDown|_winDown' -CaseSensitive)
$track  = @(Select-String -Path $f1 -Pattern 'TrackModifier' -CaseSensitive)
$down   = @(Select-String -Path $f1 -Pattern 'var (ctrl|alt|shift|win) = Down\(' -CaseSensitive)
$closed = @(Select-String -Path $f2 -Pattern 'private volatile bool _closed = true;' -CaseSensitive)
"1) {0}  2) {1}  3) {2}  4) {3}" -f $legacy.Count, $track.Count, $down.Count, ($closed | ForEach-Object { "L$($_.LineNumber)" })
```

输出 `1) 0  2) 0  3) 12  4) L100`：遗留字段引用 0 处（`ModifiersDown`/`DescribeModifiers`/`TryHandleOverlayKey` 均无 `_ctrlDown`/`_altDown`/`_shiftDown`/`_winDown`），`TrackModifier` 0 处（方法与调用点均已删除），实时判定 12 行（3 处 × 4 行），`_closed` 声明为 true 且位于 L100。

编码与行尾（本轮改写的两个 .cs 均为 CRLF、无 BOM、文件末尾带换行）：

- `EscOverPinHook.cs`：bytes=21238，CRLF=550，bareLF=0，BOM=False，endCRLF=True。
- `CaptureOverlayWindow.xaml.cs`：bytes=19667，CRLF=468，bareLF=0，BOM=False，endCRLF=True。

审查注意：`git ls-files --eol` 显示全仓库索引为 LF、工作树为 CRLF，因此 `git diff` 默认把整文件视为重写（未改动的文件同样出现全量 numstat）。看真实改动用 `git diff --ignore-cr-at-eol`；本次语义 diff 只有上述增删，无其他修改。

## 改动文件

| 文件 | 行数变化 | 内容 |
| --- | --- | --- |
| `src/PrtScr.App/Services/EscOverPinHook.cs` | 570 → 550 | 删 4 个修饰键记账字段、删 `TrackModifier` 及其调用点、三处判定改实时 `GetAsyncKeyState`、`HookProc` 去掉 keyup 分支与两个 keyup 常量 |
| `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs` | 465 → 468 | `_closed` 初值 `false` → `true`（+3 行原因注释），预热期不再把 `_overlayKeysActive` 置 2 |
| `Record/reports/hotkey-a-d-brackets-fix.md` | 新建 | 本报告 |

本单只改上述两个 .cs 与这份报告：`CaptureOverlayWindow.Capture.cs`、`CaptureService.cs` 仅阅读未改；工作树里其他文件的改动来自并行任务，不是本单产物。

## 风险与遗留

- 未做实机按键验证。纪律禁止合成热键、`--smoke`、后台 UIA 交互和启停 IshWY 实例，本单的验证只有静态核对 + 构建/测试/format 三项门禁。裸键 a/d 是否真的不再触发截图、取景器之外 `[`/`]` 是否恢复输入法/切窗功能，需要真实按键手测确认。
- 修饰键判定改为只查通用 VK（0x11/0x12/0x10/0x5B/0x5C），不再区分左右。`GetAsyncKeyState(VK_MENU)` 的高位对左右 Alt 均置位（与 `GetKeyState(VK_MENU)` 语义一致），但若某些环境右 Alt 不置通用位，右 Alt 组合将不再触发——这是方案指定的取舍（用实时状态换掉可残留的记账），未实机验证。
- 钩子回调现在每次修饰键判定做 4 次 `GetAsyncKeyState`。该调用是微秒级、远低于 `LowLevelHooksTimeout`（默认 5s），但未做耗时测量；若后续在低端机观察到输入延迟，可改为每次回调采样一次并缓存到本次回调内使用。
- `_overlayKeysActive` 的状态机未重写。预热路径现在只会写入 0；取景会话激活期间进程异常退出等路径理论上仍可能残留非 0 值，本单按最小修复未扩展。
- 两文件改动未提交、未打 tag、未发布；工作树基线 dirty 123 项未清理（复查时 126 项，差额来自本报告文件与并行任务）。构建与测试是全解决方案级，运行时会包含并行任务的在飞改动，因此这三条证据证明的是「运行时刻整包绿」，不是仅本单两文件的隔离验证。

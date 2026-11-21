# P10 第 2 轮返工报告（task-57，托盘失败关闭与半构造退化）

工位：DeepSeek Flash 子代理（本工位不派子代理）。工作区 E:/Explore/GoodGood/PrtScr，HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`（工作树含前几轮未提交改动，本轮未 commit/push/tag）。日期 2026-11-07。

## 一、范围与裁决

返工对象是 P10 第 1 轮独立复核（Record/reports/P10-round1-independent-review.md）的阻断项 R1b/S09：`InitTray` 的 catch 吞掉 `CreateTrayIcon` 创建/入树异常后，五个托盘字段停在 `null!`，窗口仍返回并继续 `InitHotkey` 与正常 `Loaded`，形成半构造。冻结裁决要求（Record/plans/P10-round2-repair.md、Record/plans/P10-P11-safe-gates.md、Record/plans/P10-P11-acceptance-matrix.md）：

- F1：先局部完整构造，再一次性发布字段（或对每个引用点加守卫）。
- F2：创建/入树失败必须向 `MainWindow` 构造调用者传播，由既有关闭漏斗失败关闭；不得吞异常继续启动。
- 保留 HEAD 已有容错：`ForceCreate`、左右/双击命令、`ResetTrayToolTip`、`Opening` 日志的 try-catch 降级语义不变。
- 正常路径 10 个菜单项/文案/顺序/事件/初值/图标/提示/退出语义不变；冒烟不得构造 `TaskbarIcon`。

本轮唯一产品写域为 `src/PrtScr.App/MainWindow.Windows.cs`；本报告为新增文件。未触碰其它 src/scripts/CI/docs/README/旧报告/.gitignore/依赖，未删除任何文件。

## 二、改动文件与行为

唯一改动文件：`src/PrtScr.App/MainWindow.Windows.cs`。

| 项 | 改前（第 1 轮产物，即本轮基线） | 改后（本轮） |
| --- | --- | --- |
| SHA-256 | `fab7eabb5aae2c8e1d3ef4d2b5bf49c1e1f404755e93df4efc7fabb32f9ba65f` | `2641d3b64bef678afe901dd87b6a8f9c36657bc5f0a0e60d415cb21f44ccda20` |
| 字节 / 行 | 16415 B / 439 行 | 17772 B / 458 行 |
| 换行 | 全 CRLF | 全 CRLF（CRLF 458、孤立 LF 0、末尾 CRLF） |

两处行为改动，均在同一文件内：

1. `InitTray()`（改动后 `MainWindow.Windows.cs:211-235`）：`CreateTrayIcon();` 从原 try 内移到 try 之前（`MainWindow.Windows.cs:218`，新增四行注释 213-217 说明失败关闭理由）。try 块（222-230）内容与 HEAD 完全一致，仍只有 `TrayIcon.ForceCreate();`、左右键命令、双击命令、`ResetTrayToolTip();`、`TrayMenu.Opening` 日志、`App.Log` 完成日志六条语句；`catch (Exception ex)`（231-234）继续只记日志。结果是：托盘创建/入树失败上抛 `InitTray` → `MainWindow` 构造函数（`MainWindow.xaml.cs:48-103` 无 try/catch）→ `App.OnLaunched` 既有 catch（`App.xaml.cs:176`）→ 非冒烟走 `ShowFatal(ex.ToString())`（`App.xaml.cs:186`）+ `Exit()`（`App.xaml.cs:187`）。窗口不会返回，`InitHotkey();`（`MainWindow.xaml.cs:92`）与正常 `Loaded` 都不会执行。
2. `CreateTrayIcon()`（`MainWindow.Windows.cs:244-278`）：菜单、两个菜单项、取消项与图标先全部构造在局部变量 `menu`/`pinItem`/`delayItem`/`cancelItem`/`icon`（246-268），成功 `RootGrid.Children.Add(icon);`（270）之后才一次性发布 `TrayMenu`/`TrayPinItem`/`DelayCaptureItem`/`CancelDelayItem`/`TrayIcon` 五个字段（273-277）。方法内无 try/catch，异常原样上抛。字段声明仍是 `= null!`（205-209）；全文件对五个字段的赋值点各只有 1 处，且都在 273-277。

其它五个文件与第 1 轮复核记录逐字节一致（SHA 见 §三 A9）：`App.xaml.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`src/PrtScr.Core/SmokeProtocol.cs`、`src/PrtScr.Core.Tests/SmokeProtocolTests.cs`。

## 三、验证命令、退出码与数字

全部命令在 `E:/Explore/GoodGood/PrtScr` 下串行执行；构建经 `.tmp/build-guard.ps1` 加锁，主程序集输出到 `.tmp/out-p10r2/`，未使用 `-t:Compile`。**lead 2026-11-08 纠正原报告“未写默认 bin（App 侧）”推断：仅设置 OutDir 不隔离 WinUI 的 OutputPath/XBF，不能证明默认 bin 未被写入；构建/测试数字保留，输出隔离结论撤回。** 依据见[启动输出目标核查](<Record/reports/startup-20261108-source-audit.md>)。

| # | 命令 | 结果 | 原始日志 |
| --- | --- | --- | --- |
| 1 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p10r2 -Cmd 'dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p10r2/'` | `BUILD-GUARD: EXIT=0`，0 警告 0 错误，30.07s（含 XAML 代码生成） | `.tmp/p10r2/build.log` |
| 2 | `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p10r2test -Cmd 'dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo'` | `BUILD-GUARD: EXIT=0`，失败 0 / 通过 **327** / 跳过 0，78 ms | `.tmp/p10r2/test.log` |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | `FORMAT_EXIT=0`（无输出） | `.tmp/p10r2/format.log` |
| 4 | `python .tmp/p10r2/verify_tray_structure.py` | `EXIT=0`，16 项检查全过（A1-A15） | `.tmp/p10r2/verify-tray-structure.log` |
| 5 | `git diff --no-index -- .tmp/p10r2/pre-round2-MainWindow.Windows.cs src/PrtScr.App/MainWindow.Windows.cs` | `DIFF_EXIT=1`（预期：有差异），产出 87 行 diff | `.tmp/p10r2/round2.diff` |

说明：`dotnet test` 按项目本征输出到 `src/PrtScr.Core/bin|Tests/bin/Debug`（与历次门禁同一口径）；隔离 OutDir 只用于整包 build，未用 `-t:Compile`。

### 静态检查（A1-A15，脚本 `.tmp/p10r2/verify_tray_structure.py`，纯 ASCII 文本分析、不启动 App）

| 检查 | 结论 | 关键数字 |
| --- | --- | --- |
| A1 | `CreateTrayIcon();` 全文件仅 1 次调用，在 `InitTray` 顶层且位于 try 之前 | InitTray 211、调用 218、try 222、定义 244 |
| A2 | try 体逐语句等于 HEAD 五步 + 完成日志，顺序一致 | try 222-230，6 条语句 |
| A3 | `new TaskbarIcon` 全文件仅 1 处，在 `CreateTrayIcon` 内 | 262 行；方法体 244-280 |
| A4 | `CreateTrayIcon` 内无 `try`/`catch` | 244-280 |
| A5 | 入树（270）先于五个字段发布；五条发布各 1 次且来自局部量 | 273/274/275/276/277 |
| A6 | 五个 `= null!` 声明（205-209）；全文件每字段仅 1 次赋值，均在 273-277 | 5 声明 / 5 赋值 |
| A7 | `InitTray()` 仅 1 次调用，在 `if (!SmokeSession.IsRequested)` 块内（`MainWindow.xaml.cs:89-100`），且先于 `InitHotkey`；构造函数体（48-105）无 catch | 调用 91、`InitHotkey` 92 |
| A8 | 构造失败可达 `App.OnLaunched` 的 `ShowFatal` + `Exit` | new MainWindow 153、catch 176、ShowFatal 186、Exit 187 |
| A9 | 第 1 轮复核记录的五文件 SHA 全部一致（本轮未改） | 5/5 same |
| A10 | 五个托盘字段的引用点仍全部存在（Capture.cs 取消项显隐与倒计时提示、CaptureSettings.cs UpdateCaptureOptions、Hotkeys.cs ResetTrayToolTip、PinSettings.cs 既有 null 守卫、Exit_Click Dispose） | 7/7 ok |
| A11 | 延时路径返回：`_delayCts` 置位（177）→ `CancelDelayItem.Visibility`（178）→ try（181）；唯一清空点在 finally 内（214/218），与复核描述的楔死机制一致且未被改动 | set 177 / reveal 178 / try 181 / finally 214 / clear 218 |
| A11b | `_delayCts` 出现点 8 处（107/110/122/125/170/177/216/218），本轮未改 `MainWindow.Capture.cs` | 8 |
| A12 | `MainWindow.Windows.cs` 全 CRLF、末尾 CRLF | 17772 B，CRLF 458，孤立 LF 0 |
| A13 | 构造函数只注册 `Loaded += OnLoaded`，无直接调用 | registration=True，direct=False |
| A14 | 正常路径托盘构造与改前逐 token 等价（局部量→字段名归一后比较，含菜单文案/顺序/事件/图标属性） | 归一长度 new=1052 / pre=1052，相等 |
| A15 | 改前快照即复核基线：反向替换恢复件 SHA = `fab7eabb…` | 16415 B |

A15 与 §二 的恢复件共同证明：本轮相对第 1 轮产物的差异只有上述一处替换（`InitTray` 调用位置 + `CreateTrayIcon` 局部构造/发布），没有夹带其它改动；`.tmp/p10r2/round2.diff` 为同源 diff。

## 四、逐项闭环

- **F1（局部完整构造再发布 / 引用点守卫）**：A5+A6+A14。五个对象先在局部构造，入树成功后一次性发布；字段声明 `null!`，全文件无第二处赋值；正常路径构造序列与改前逐 token 一致。
- **F2（失败关闭，禁吞异常继续启动）**：A1+A4+A7+A8。`CreateTrayIcon` 在 try 外、方法内不捕获；构造函数无 catch；异常沿 `InitTray → ctor → App.OnLaunched catch` 到 `ShowFatal`+`Exit`（非冒烟）。窗口不会以半构造状态返回，因此 `MainWindow.xaml.cs:154 → CaptureSettings.cs:277`（路径 A）与 `MainWindow.Capture.cs:178`（路径 B）在字段缺失时不可达。
- **保留 HEAD 容错**：A2。`ForceCreate`/左右键/双击/`ResetTrayToolTip`/`Opening` 日志仍在 try 内，catch 仍只记日志——托盘运行期失败继续降级为“可启动但无托盘”，与 HEAD 语义一致。
- **正常 10 菜单等语义**：A14 归一化逐 token 相等（1052 字符），菜单项文案、顺序、事件处理器、`MenuFlyoutSeparator`、图标属性（`ToolTipText`/`ContextMenuMode`/`IconSource`/`ContextFlyout`）均未变。
- **冒烟不可达**：A3 全文件唯一 `new TaskbarIcon` 在 `CreateTrayIcon` 内；A7 该路径唯一入口 `InitTray()` 位于 `!SmokeSession.IsRequested` 块（`MainWindow.xaml.cs:89-100`），冒烟分支（同文件 105 起 `OnLoaded` 首行即返回）不会触达。本轮未运行 `--smoke`（禁止项），故为静态闭环。

## 五、局限与未覆盖（未做清单）

1. **真实 WinUI 构造失败未动态验证**：无法在不加生产故障开关、不启动 App、不注入 UI 的前提下让 `new MenuFlyout()`/`new TaskbarIcon` 或 `RootGrid.Children.Add` 真实抛异常。F2 的“失败即关闭”只有静态链路证据（A1/A4/A7/A8），没有一次真实的失败启动观测；不声称已实测。
2. **未运行 App、未 `--smoke`、未 publish/打包、未跑 Actions/UIA、未注入合成输入、未改动或终止主人正在运行的实例、未改 ACL**：均为本工位禁止项。
3. **被动快照（复核 S07）与动态失败注入（S06）未做**：需要真实运行实例，超出本工位边界，留给 lead/受控验证单。
4. **`dotnet test` 使用测试项目默认输出目录**（历次门禁同口径），整包 build 才用隔离 OutDir；未使用 `-t:Compile`、未写默认 `bin`。
5. 工作树在 HEAD 之外仍含有前几轮（含 P11）的未提交改动；本轮未提交、未暂存、未打 tag，`git status` 中本轮只新增 `Record/reports/P10-round2-repair.md` 与 `.tmp/p10r2/` 证据。

**结论**：本工位（task-57）绿——F1/F2、HEAD 容错保留、正常菜单语义、冒烟不可达均有可重复静态证据，build/Core.Tests/format 全绿（0 警告 0 错误、327/327、rc=0）。但按冻结裁决，工位绿不单独解除 hold，仍需 task-58 独立复核与 P11 task-56 同 pass；本工位不自行第 3 轮，若第 2 轮被判定再次失败即上报 lead。

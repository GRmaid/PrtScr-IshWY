# P10 第 2 轮独立安全复核（task-58）

- 日期：2026-11-07 22:48–23:12（Asia/Shanghai）
- 复核对象：P10 第 2 轮返工（task-57：托盘创建失败关闭与半构造退化）后的工作树；基线 HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`（全部相关改动未提交）
- 工位性质：独立复核工位，只读审查；本单只写本文件；未改产品/脚本/其他记录，未 commit/push/tag，未派子代理，未运行任何产品
- 任务工具：本工位按派单以静态复核直交报告，未调用 `agent_teams_claim_task` 或其它任务写工具（派单明确：claim 不可用则直接交付）
- 关联契约：task-58（本工位）；上游 `Record/reports/P10-round2-repair.md`、`Record/plans/P10-round2-repair.md`；第 1 轮 `Record/reports/P10-round1-independent-review.md`；`Record/plans/P10-P11-safe-gates.md`；`Record/plans/P10-P11-acceptance-matrix.md`
- P11 状态：task-56 已最终 **needs_revision**（唯一阻断 BLK-1：ignored 的 Assets 字节未进入构建输入指纹），因此本轮即使 pass 也**不解除联合 hold**，更不是运行/发布许可。

## 总判定

| 维度 | 判定 |
|---|---|
| F1 托盘菜单/菜单项/图标先局部完整构造，入树后一次性发布五字段 | **pass** |
| F2 创建/入树异常向 MainWindow 构造调用者传播，走既有 ShowFatal + Exit 关闭 | **pass**（静态） |
| 不继续 InitHotkey / 正常 Loaded / 留下 _delayCts | **pass**（静态；运行未验，见 R2/R9） |
| HEAD 原 ForceCreate 等 try-catch 降级语义不变 | **pass** |
| 正常模式 10 菜单项文案/顺序/事件/初值/退出语义不变 | **pass** |
| 冒烟不构造托盘、不装钩子、不触系统能力 | **pass**（静态） |
| S01–S05、S08 | pass（静态；S02 附 R6） |
| S06 | **未测**（本单禁止运行） |
| S07 | 静态 pass；被动前后快照未做 |
| S09 正常启动语义不变 | 静态 pass；真实交互未验 |
| **P10 整体（本工位）** | **静态安全 pass**（两个原始阻断 F1/F2 已关闭；无新增阻断）；**未完成运行验收** |

即使本轮 pass，P-10 也不能单独解除 hold：safe-gates:31-34 与 acceptance-matrix:7 要求 P-11 同 pass；P-11 task-56 已最终 needs_revision，故联合 hold 继续。本工位按派单**不自动 R3**。

---

## 一、复核范围、方法与证据边界

### 1.1 输入（只读，按派单最多 5 项 + 定点链路）

1. `Record/reports/P10-round2-repair.md`（86 行，返工自述与工位数字）。
2. `Record/plans/P10-round2-repair.md`（24 行，F1/F2 冻结裁决与写域）。
3. `Record/reports/P10-round1-independent-review.md`（238 行，R1b/S09 阻断与 S01–S09 基线）。
4. `Record/plans/P10-P11-safe-gates.md`（38 行，接口 v1 与许可边界）。
5. `Record/plans/P10-P11-acceptance-matrix.md`（53 行，S01–S09 与启动接线清单）。
6. 定点产品链路（不扫无关目录）：`src/PrtScr.App/MainWindow.Windows.cs`、`MainWindow.xaml.cs`、`App.xaml.cs`、`MainWindow.xaml`、`MainWindow.Capture.cs`、`MainWindow.CaptureSettings.cs`、`MainWindow.Hotkeys.cs`、`MainWindow.PinSettings.cs`、`Services/SmokeSession.cs`、`Services/CaptureHistoryService.cs`、`Services/StartupService.cs`、`src/PrtScr.Core/SmokeProtocol.cs`、`SmokeRunState.cs`、`src/PrtScr.Core.Tests/SmokeProtocolTests.cs`、`SmokeRunStateTests.cs`。
7. 抽查 `.tmp/p10r2/` 原始产物：pre-round2 快照、`round2.diff`、`verify_tray_structure.py`（231 行）、`verify-tray-structure.log`、`build.log`、`test.log`、`format.log`。

### 1.2 做了什么

- 独立复算被审文件 SHA-256、字节数、行数与换行（不采信返工报告的哈希表）；`MainWindow.Windows.cs` 当前 SHA 与返工报告一致（2641d3b6…），且与 `.tmp/p10r2/round2.diff` 的 `+` 侧文件一致。
- `git show HEAD:<path>` 逐段比对 HEAD 的 `InitTray` 与 XAML 托盘块，判定「既有容错语义」是否保留、菜单/事件/树位置是否等价。
- grep 调用点与字段引用：`CreateTrayIcon` 全仓 1 处定义 + 1 处调用；`InitTray` 1 处定义 + 1 处调用；`new TaskbarIcon` 全仓仅 1 处；五个托盘字段各仅 1 处赋值；当前 XAML 无 `TaskbarIcon`/`tb:` 引用（0 命中）；XAML 无初始 `SelectedIndex`/`IsOn="True"`/`SelectedItem`（0/0/0 命中），故 InitializeComponent 阶段不存在选中/勾选事件依赖托盘字段的路径。
- 逐条阅读结构脚本，判断哪些检查是文本自证、哪些依赖独立基线：A2 硬编码 HEAD 五步 + 完成日志；A5 要求入树行之后各字段恰好 1 条 `字段 = 局部变量;`；A6 要求每字段仅 1 处赋值且落在 `CreateTrayIcon` 内；A14 仅比较构造文本（本地名归一化后 1052=1052），**不证明顺序**——顺序由 A2/A5 提供；A9 的五个期望 SHA 与第 1 轮独立复核报告一致，且本轮已独立复算相同；A11 对未修改的 `MainWindow.Capture.cs` 硬编码行号，该文件本轮 SHA/mtime 未变，可作参照而非自证。
- 抽查工位日志：build 0 警 0 错、30.07 s、EXIT=0；test 327 通过 0 失败、78 ms、EXIT=0；format 空日志 EXIT=0；verify 16/16。**这些是工位过程数字，不是 lead 冻结树验收**（safe-gates:31-33），本报告不采信为验收证据。
- 核对 `round2.diff` 仅 2 个 hunk（`-210,9 +210,17`、`-229,33 +237,44`，87 行），与当前源码一致；`git diff --ignore-space-at-eol HEAD -- MainWindow.Windows.cs` 为 **67 insertions / 0 deletions**，即相对 HEAD 本轮及第 1 轮的净语义增量全是新增（含第 1 轮迁出 XAML 的部分）；未用 `-w` 的 `--stat` 显示整文件换行 churn（849 行）属第 1 轮 F7 已知非阻断项。

### 1.3 没做什么（硬边界，如实声明）

- 未 `dotnet build`、未跑 `Core.Tests`、未跑 format、未运行 App、未跑 `--smoke`、未 publish/打包/产品 ZIP、未触发 GitHub Actions。
- 未做真实键鼠/UIA/托盘交互/Esc 实测；未激活或终止任何用户实例；未改 ACL/未提权；未新增依赖；未 commit/push/tag。
- 未做 S07 的被动前后快照（`Record/tools/P10-SideEffectSnapshot.ps1` 未运行，无本轮运行证据）。
- 未读 H.NotifyIcon 包内实现（见 R1），因此「TaskbarIcon 构造函数在 ForceCreate 之前是否已产生系统副作用」不在本报告断言范围。

### 1.4 被审文件身份（本轮时点，工作树）

| 文件 | SHA-256（前 12 位） | 行数 | 说明 |
|---|---|---|---|
| src/PrtScr.App/MainWindow.Windows.cs | 2641d3b6…（全 2641d3b64bef678afe901dd87b6a8f9c36657bc5f0a0e60d415cb21f44ccda20） | 458 | 本轮唯一产品改动；17772 B；全 CRLF（CRLF 458、孤立 LF 0、末尾 CRLF）；mtime 22:37 |
| .tmp/p10r2/pre-round2-MainWindow.Windows.cs | fab7eabb…（fab7eabb5aae2c8e1d3ef4d2b5bf49c1e1f404755e93df4efc7fabb32f9ba65f） | 439 | 第 1 轮基线快照；16415 B |
| src/PrtScr.App/App.xaml.cs | 53135f2d… | 307 | 与第 1 轮同 SHA，本轮未改 |
| src/PrtScr.App/MainWindow.xaml | b46701af… | 1308 | 与第 1 轮同 SHA，本轮未改；已无托盘元素 |
| src/PrtScr.App/MainWindow.xaml.cs | 723dda7b… | 379 | 与第 1 轮同 SHA，本轮未改 |
| src/PrtScr.Core/SmokeProtocol.cs | 973a27ae… | 243 | 与第 1 轮同 SHA，本轮未改 |
| src/PrtScr.Core.Tests/SmokeProtocolTests.cs | fb3dccb6… | 239 | 与第 1 轮同 SHA，本轮未改 |
| src/PrtScr.App/MainWindow.Capture.cs | 3a82d7b8… | 228 | 本轮未改（延时截图链路参照） |
| src/PrtScr.App/Services/SmokeSession.cs | b5236be9… | 230 | untracked，本轮未改 |
| src/PrtScr.Core/SmokeRunState.cs | 30d94bcd… | 52 | untracked，本轮未改 |
| src/PrtScr.App/Services/CaptureHistoryService.cs | 2146d8e4… | — | M（第 1 轮隔离改动），本轮未改 |
| src/PrtScr.App/Services/StartupService.cs | 3ff644a6… | — | M（第 1 轮隔离改动），本轮未改 |

时间线自洽：产品文件 mtime 22:37，build/test/format/verify 日志 22:43–22:45，均晚于改动；`.tmp/p10r2/` 无其它过期文件。

---

## 二、F1 / F2 与既有语义逐项

### 2.1 F1：先局部完整构造，再一次性发布五字段 —— **pass**

- 五个字段仍是 `= null!` 声明（`MainWindow.Windows.cs:205-209`），全文件各仅 1 处赋值，均在 `CreateTrayIcon` 内（A6 通过；调用点 grep 亦一致）。
- `CreateTrayIcon`（244-278）先在局部变量完整构造：
  - 菜单 `menu`（246）；
  - 10 个菜单项按 HEAD XAML 顺序与文案（247-260）：设置 / 从剪贴板贴图 / 从剪贴板识别文字 / 全屏截图到剪贴板 / 延时截图 / 取消延时截图（`Visibility = Collapsed`，255）/ 截图历史… / 屏幕贴图管理… / 分隔符 / 退出；
  - 图标 `icon`（262-268）：`ToolTipText` 与 HEAD 同文案、`ContextMenuMode = SecondWindow`、`IconSource = new BitmapImage(new Uri("ms-appx:///Assets/app.ico"))`、`ContextFlyout = menu`；
  - `RootGrid.Children.Add(icon)`（270）成功之后才发布 `TrayMenu`（273）、`TrayPinItem`（274）、`DelayCaptureItem`（275）、`CancelDelayItem`（276）、`TrayIcon`（277）。
- 方法内无 try/catch（A4 通过；244-278 无 `try`/`catch`）。因此若菜单构造、图标构造或入树任一环节抛异常，**五个字段都尚未发布**，不会出现「字段看似有效但对象半构造」的状态，也不存在「先发布再补树」的反序。
- 树位置与 HEAD 一致：HEAD XAML 的 `tb:TaskbarIcon` 是根 Grid 末尾子元素（`HEAD` MainWindow.xaml:1308-1328，`</Grid>` 在 1329），当前代码 `Add` 到 `RootGrid` 末尾，等价。
- 残余（非阻断）：`RootGrid.Children.Add` 若因重复元素抛异常，字段保持 null 并上抛，属失败关闭，不会误用；此处无「回滚已加元素」需求，因为异常后进程走关闭漏斗。

### 2.2 F2：创建/入树异常向构造调用者传播并失败关闭 —— **pass**（静态；运行未验）

传播链（逐行）：

1. `InitTray` 的 `CreateTrayIcon();` 在 try **之前**（`MainWindow.Windows.cs:218`；新增注释 213-217 说明理由）；try 体 222-230 只剩 HEAD 五步 + 完成日志；`catch`（231-234）只记日志。
2. 因此 `CreateTrayIcon` 的异常**不被 InitTray 的 catch 吞掉**，直接抛向 `InitTray` 调用者。
3. `InitTray();` 位于 `MainWindow.xaml.cs:91`，构造函数 48-103 **无 try/catch**（A7 通过：ctor 区段无 catch），异常继续抛向 `new MainWindow(...)`。
4. `App.OnLaunched` 的 `try` 从 151 起包住 `_window = new MainWindow(startHidden)`（153），`catch (Exception ex)` 在 176-188：非冒烟走 `ShowFatal(ex.ToString())`（186）+ `Exit()`（187）；冒烟走 `SmokeSession.Fail("on-launched-failed: ...")` + return（180-184）。
5. 于是托盘创建/入树失败 = 启动失败：非冒烟弹出既有启动异常框并退出；冒烟写失败结果、非 0 退出。与第 1 轮要求「向 MainWindow 构造调用者传播、由既有关闭漏斗失败关闭」一致。

「不继续 Hotkey / Loaded / _delayCts」的证明：

- `InitHotkey();` 在 `InitTray();` 之后（`MainWindow.xaml.cs:92`），构造函数已抛出，92-99 全部不执行；`CaptureHistoryService` 订阅（98）与 `PinService.HistoryRequested`（99）同样不执行。
- `Loaded += OnLoaded` 虽在构造早段（84，早于 91）注册，但窗口从未 `Activate`（`App.xaml.cs:172` 在 ctor 成功后才执行），未激活窗口中 `Loaded` 不会触发；因此正常 `OnLoaded` 分支（105-177，含 154 `UpdateCaptureOptions`→`CaptureSettings.cs:277` 对 `DelayCaptureItem.Text` 的解引用）不会运行。
- `_delayCts` 只在 `StartDelayedCaptureAsync` 首次执行时赋值（`MainWindow.Capture.cs:176-177`），而该路径由托盘/菜单点击进入；托盘菜单未建立且窗口未激活，故不会留下非空 `_delayCts`，延时截图链路不会「楔死」。

非阻断残余：构造早段已安装的 `KeyInterceptor`（`MainWindow.xaml.cs:59-69`）与字段初始化器 `_hotkey`（`:21`）在托盘失败时不会逐个 Dispose，而是依赖 `Exit()` 结束进程；若 `Application.Exit` 实际不终止进程，则可能留下钩子/对象（见 R2/R9）。这是既有失败漏斗的共性问题，非本轮引入。

### 2.3 HEAD 原 ForceCreate 等 try-catch 语义 —— **pass**

- `git show HEAD:src/PrtScr.App/MainWindow.Windows.cs` 的 `InitTray`（203-218）：`try` 内五条语句 `ForceCreate` / `LeftClickCommand` / `DoubleClickCommand` / `ResetTrayToolTip` / `TrayMenu.Opening`，外加 `App.Log("Tray: 初始化完成")`；`catch (Exception ex)` 只 `App.Log($...失败...)`。
- 当前 222-230 的六条语句与 HEAD 顺序逐字一致（A2：statements=6、order_ok=True、log_ok=True），catch（231-234）仍只记日志。
- 语义结论：`ForceCreate` 失败仍被降级为「记日志、程序继续」（HEAD 行为），**没有**把 `ForceCreate` 也改成失败关闭；本轮只把「对象创建/入树」移出 try。这与派单「HEAD 原 ForceCreate 等 catch 语义不变」相符。
- 同理，正常路径下 `TrayIcon` 的 `LeftClickCommand`/`DoubleClickCommand`/提示复位/`Opening` 日志不变；`Exit_Click`（449-456）仍是 `Dispose()` → `_hotkey.Dispose()` → `PInvoke.ExitProcess(0)` 的顺序，未改。

### 2.4 正常 10 菜单、事件与初值 —— **pass**

- HEAD XAML 托盘块（1308-1328）与当前代码逐项对照：10 个条目文案/顺序/Hander 名（`ShowSettings_Click`、`PastePin_Click`、`OcrClipboard_Click`、`FullScreen_Click`、`DelayCapture_Click`、`CancelDelay_Click`、`History_Click`、`OpenPinManager_Click`、`Exit_Click`）一致；分隔符位于「屏幕贴图管理…」与「退出」之间一致；`CancelDelayItem` 初始 `Collapsed` 一致。
- 图标属性等价：XAML `IconSource="ms-appx:///Assets/app.ico"` 经类型转换生成 `BitmapImage`，代码显式 `new BitmapImage(new Uri(...))`；其余 `ToolTipText`/`ContextMenuMode`/@ContextFlyout` 同一语义。
- 引用点未变：`Capture.cs:178`/`188`/`219`（取消项可见性、倒计时提示、隐藏）、`CaptureSettings.cs:277`（`DelayCaptureItem.Text`）、`Hotkeys.cs:174-175`（`ResetTrayToolTip`）、`PinSettings.cs:133-135`（`TrayPinItem is null` 守卫）、`Windows.cs:449-456`（`Dispose`）。这些文件本轮 SHA/mtime 未变，A10 通过。
- 残余（非阻断）：XAML 字符串→代码 `BitmapImage` 的加载时机略有差异（异步解码 vs XAML 类型转换），不影响菜单/事件语义；无证据表明有行为变化。

### 2.5 冒烟不创建托盘/钩子/系统能力 —— **pass**（静态）

- `MainWindow` 构造函数只在 `if (!SmokeSession.IsRequested)`（`MainWindow.xaml.cs:89`）内调用 `InitTray`（91）与热键/历史/贴图订阅（92-99）；冒烟分支整块跳过（A7：guard 89-100、InitTray call [91]）。
- 全仓唯一 `new TaskbarIcon` 在 `Windows.cs:262`，只被 `CreateTrayIcon`（244）调用，而 `CreateTrayIcon` 只被 `InitTray`（218）调用；`InitTray` 只被 `MainWindow.xaml.cs:91` 调用。当前 `MainWindow.xaml` 已无 `tb:TaskbarIcon`/`TrayIcon`/`TrayMenu` 元素（grep 0 命中），因此冒烟启动路径**没有**托盘对象构造点。
- 同类系统能力同样被短路：`EscOverPinHook.Install` 仅非冒烟（`App.xaml.cs:61-72`）；`StartActivationListener` 仅非冒烟（`App.xaml.cs:158-159`）；冒烟 `OnLoaded` 在 107-113 记录事件并 return，其后的正常分支（含 119 启动项读取、159-173 抓屏预热、175 OCR 状态、176 内存计时器）不执行。
- 该结论为静态可达性；运行层「冒烟进程确实未创建托盘/钩子」仍属 S06/S07 未测范围，不得据此宣称已动态验证。

---

## 三、S01–S09 逐项（静态；运行类标注「未测」）

### S01 缺 nonce/root → 非 0，禁止回落真实 data 或正常启动 —— **pass（静态）**

- `SmokeProtocol.Parse`（41-101）先判 `"--smoke"` 模式：参数缺失直接 `NotRequested`（不启用冒烟，不伪造失败）；启用后缺 nonce → `missing-nonce`、缺 root → `missing-root`，均 Invalid。
- `SmokeSession.Begin`（48-72）：Invalid 分支在 **DataRoot 赋值（69）与任何日志/配置之前** 打印 `ISHWY_SMOKE_INVALID_CONTRACT` 并 `Exit(2)`（62-66）；`DataRoot` 默认是程序目录（41），仅在 Valid 时才改为隔离根（69）。
- App 侧：`SmokeSession.Begin()` 在构造最前（`App.xaml.cs:28`），早于配置创建（17 按需）与 ctor 日志（46）；冒烟时配置加载被跳过（87-97）。无「失败后回落到真实配置」路径。

### S02 nonce 非 Guid N / root 不匹配 / 非新空 / 重解析点 → 失败关闭，不清理不覆盖用户目录 —— **pass（静态；重解析点测试为条件早退，见 R6）**

- `Parse` 校验：nonce 为 Guid "N" 格式（`nonce-not-guid-n`）；root 必须绝对、无 `.`/`..` 段（`root-has-dot-segments`）、存在且为目录；叶子名 == nonce、父目录名 == `IshWY-smoke`、祖父 == 系统临时目录（`OrdinalIgnoreCase`），否则 Invalid。
- 目录安全检查顺序：`Exists` → `HasReparsePoint`（root 及祖先）→ `IsEmpty` → `CanWrite`；`CanWrite` 用 `CreateNew + DeleteOnClose` 探针（181-196），只在「新空目录」前提下创建自己的临时文件，**不删除/不清空调用者已有内容**；失败归 `root-unreadable`（捕获 UnauthorizedAccess/IO/Security）。
- 残余 R6：`SmokeProtocolTests.ReparsePointRootIsRejectedWhenConstructible`（209-238）在无法创建符号链接时 `catch ... { return; }`，无 skip 记录、无断言，**属条件早退而非已断言**；本机未证明该分支被执行。生产代码的 `HasReparsePoint` 检查存在且静态看是失败关闭，但「重解析点被实际拒绝」缺动态证据。

### S03 已有正常实例 → 非 0，不向既有激活事件 Set —— **pass（静态）**

- `App.xaml.cs:99-134`：`TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested)`（104）——冒烟传 `false`，不向既有实例发送激活；冒烟检查异常 → `Fail("single-instance-check-failed")`（109-114）；已有实例 → `SmokeSession.FailInstanceConflict()`（123-128）→ 退出码 3（`SmokeSession.cs:109-112`），不 Set 事件、不建主窗。
- 正常分支保留原语义：检查异常 fail-open（116-118），冲突时 `PInvoke.ExitProcess(0)`（131-133）。与 HEAD 一致（第 1 轮 R2 已核，SHA 未变）。

### S04 XAML 或 Loaded 初始化抛异常 → 非 0，不得 success=true —— **pass（静态）**

- XAML 初始化：`App.xaml.cs:47-59` try InitializeComponent，catch 记日志 + `SmokeSession.Fail("xaml-initialization-failed: ...")` + `ShowFatal` + throw；`Fail` 走终态失败（`SmokeSession.cs:114-117`→`Complete` 126-164），成功只可能来自三个检查齐全（见 S05）。
- Loaded 内异常：冒烟里 `NotifyMainWindowLoaded`（`SmokeSession.cs:87-106`）只记录事件/排队第三检查；异常由 `App.UnhandledException`（32-44）捕获，冒烟 `Fail("unhandled-exception: ...")` + `ShowFatal` + `e.Handled = true`；不会写 success。
- 构造失败同样走 `OnLaunched` catch（176-188）失败关闭。

### S05 UI 未 Loaded 或未完成队列往返 → 超时仅失败 —— **pass（静态）**

- 成功终态只由 `SmokeRunState.TryRecordCheck` 在三个 canonical 检查齐全时产生（`SmokeRunState.cs:18-32`；`HasExactSuccessChecks`），`RecordCheck` 拿到 Success 才 `Complete(0,...)`（`SmokeSession.cs:119-124`）。
- 20 s 看门狗（`SmokeSession.cs:215-227`）到点只调 `Fail("timeout-waiting-for-main-window-roundtrip")`；没有任何「计时到点即成功」的路径（App 注释 161-163 也明确旧 3 秒成功已删除）。
- 第三检查必须真正排进 UI 队列并执行（`queue.TryEnqueue`，101）；排队失败 → `Fail("dispatcher-queue-unavailable")`（104-105）。

### S06 成功路径三检查齐全、exit0 与结果身份一致 —— **未测（本单禁止运行；不记 pass）**

- 静态读码：三检查常量在 `SmokeProtocol.cs:26-31`，Begin/MarkXamlInitialized/NotifyMainWindowLoaded 分别记录，成功写 `<root>/smoke-result.json`（`TryWriteResult` 166-196，含 schemaVersion/nonce/processId/executablePath/dataRoot/success/checks/error），写失败退出码 6。
- 但本单未运行 `--smoke`，无本进程 `smoke-result.json` 与 exit code 证据；工位 327/327 是 Core 规则测试，不等于运行冒烟。S06 保持未测。

### S07 隔离副作用：文件只落隔离根，系统/网络能力跳过 —— **静态 pass；被动快照未测**

- 配置：`App.Config => new(SmokeSession.DataRoot, Log)`（`App.xaml.cs:17`）；冒烟 `Config.Load` 被跳过（87-97）。
- 日志：`App.Log` 写 `SmokeSession.LogDirectory`（=DataRoot/data/logs，`SmokeSession.cs:43-45`；`App.xaml.cs:280-306`）。
- 历史：`CaptureHistoryService.StorageDirectory = storageDirectory ?? SmokeSession.HistoryDirectory`（`CaptureHistoryService.cs:18-26`）。
- 启动项：`StartupService.TrySetEnabled` 与 `RefreshIfEnabled` 均先 `if (SmokeSession.IsRequested)` 短路（`StartupService.cs:46-51`、`89-90`），冒烟不读写注册表启动项。
- 托盘/钩子/热键/Esc/激活监听/抓屏预热：见 §2.5，均静态短路。
- 本单未运行时，无法给出「文件确实只在 root 内产生」的被动前后快照；`Record/tools/P10-SideEffectSnapshot.ps1` 无本轮运行证据。故 S07 只能按静态接线判 pass，动态部分未测。

### S08 success 与异常/退出并发 → 单终态，失败不可被成功覆盖 —— **pass（规则测试 + 静态）**

- `SmokeRunState` 单锁、`_completed` 终态：`TryRecordCheck` 在 completed/未知/重复时返回 null（18-32）；`TryFail` 在 completed 后返回 null（36-47）。
- `Complete`（`SmokeSession.cs:126-164`）失败分支在 `TryFail` 返回 null 时直接 return，不写结果；成功只在 exitCode==0 时 `success=true`。写入用临时文件 + `File.Move` 覆盖，`_resultWritten` 只写一次。
- 测试 `SmokeRunStateTests.cs` 6 条含 `Failure_IsTerminal_AndLateSuccessCannotOverride`（40-54）与 `Success_IsTerminal_AndFailureCannotOverride`（56-67），与本轮源码 SHA 对应（未改）。
- 残余：测试是规则层，不含真实线程/退出竞态；运行层未验（S06 未跑），但不构成新阻断。

### S09 正常启动非 smoke 原配置/托盘/热键/启动项语义不变 —— **静态 pass；真实交互未测**

- 配置/日志/历史/启动项：正常路径仍用程序目录与注册表（`SmokeSession.DataRoot` 默认 `AppContext.BaseDirectory`，`SmokeSession.cs:41`；`StartupService` 无冒烟时不短路）；`Config.Load` 仅冒烟跳过（`App.xaml.cs:87-97`）。
- 托盘：正常路径代码重建与 HEAD XAML 等价（§2.4），`ForceCreate` 失败仍降级（§2.3），`Exit_Click` 语义不变（449-456）。
- 热键/钩子：`InitHotkey` 及后续调用点在托盘之后（`MainWindow.xaml.cs:92-99`），正常成功路径顺序与 HEAD 相同；`KeyInterceptor` 安装点（59-69）未变，正常 Esc 语义未动。
- 启动项/预热：`OnLoaded` 正常分支 115-176 未改（文件 SHA 未变），`StartupToggle.IsOn = StartupService.IsEnabled()`（119）、`UpdateCaptureOptions`（154）与 HEAD 相同。
- 残余：无真实启动/托盘/热键交互证据；若 `Application.Exit` 实为「关窗」而非「终止」，托盘失败路径与正常退出路径的进程终止语义尚缺运行证明（R2）。

---

## 四、阻断、残余与未测

### 4.1 阻断（本轮无）

- 第 1 轮的 R1b/S09 阻断（InitTray 吞创建异常 + 五字段 null! 半构造）已由 F1+F2 关闭：创建在 try 外、入树后发布、方法无 catch、ctor 无 catch、OnLaunched 既有失败漏斗承接（§2.1/2.2）。
- 本工位未发现新的 P10 静态阻断项；不因「工位 327/327 + 16/16」而升级为验收。

### 4.2 残余（非阻断，按影响排序）

- R1：`new TaskbarIcon`（`Windows.cs:262`）在 `ForceCreate` 之前是否已分配系统资源/窗口，未读 H.NotifyIcon 包实现，未验证；若其构造即创建隐藏窗口，失败关闭时该对象只在进程退出时释放（可接受，但未证明）。
- R2：`Application.Exit()` 的终止语义未验证（`App.xaml.cs:187` 为继承成员，无本地定义）；若仅关闭窗口而进程常驻，托盘失败路径可能残留 KeyInterceptor 钩子/字段对象。属既有漏斗风险，非本轮引入。
- R3：S06 成功路径未运行；三事件/单终态仅规则测试与静态读码，无本进程 `smoke-result.json` 证据。
- R4：S07 被动前后快照未运行；隔离接线为静态。
- R5：S09 真实交互（托盘、热键、启动项、静默启动、预热）未验；仅静态/差异审查。
- R6：重解析点规则测试为条件早退（`SmokeProtocolTests.cs:209-238` 的 catch-return），不是断言；本机未证明「重解析点 root 被拒」动态成立。静态生产检查存在（`SmokeProtocol` 的 `HasReparsePoint` 链）。
- R7：`src/PrtScr.App/obj/Release/` 旧生成文件（Oct 30）仍含已删除的 `TrayIcon`/`TrayMenu` 字段；Release/publish 时会被重新生成，本单 publish 被 hold，未清理也未验证；不得用旧中间产物判断当前语义。
- R8：相对 HEAD 的整文件换行 churn（第 1 轮 F7）：`git diff --stat HEAD` 显示 Windows.cs 849 行变化，但 `--ignore-space-at-eol` 仅 67 insertions/0 deletions；round2 自身 diff 2 hunks/87 行。非语义回归，但会让常规 diff review 噪声大。
- R9：构造早段已安装的 `KeyInterceptor`（`MainWindow.xaml.cs:59-69`）与 `_hotkey` 字段初始化（:21）在托盘失败时未逐个 Dispose，依赖进程退出；与 R2 同源。
- R10：`ForceCreate` 失败仍是「托盘不可用但程序继续」（HEAD 语义保留）。这是**有意保留**，不是缺陷；但意味着「托盘失败关闭」只覆盖对象创建/入树，不覆盖 ForceCreate——后续读者不得误解为全链路 fail-closed。

### 4.3 未做/未测（不得记为通过）

- 未运行 App、未跑 `--smoke`、未 publish/产品 ZIP、未触发 Actions、未做真实键鼠/UIA/托盘/Esc 交互、未做被动副作用快照。
- 工位 build 0 警 0 错 / 327 通过 / format 0 / 16 项结构检查均为**过程证据**，不是 lead 冻结树验收（safe-gates:31-33）；本报告未复跑这些命令，只核对原始日志与文件身份。

### 4.4 证据索引

- 结构检查：`.tmp/p10r2/verify-tray-structure.log`（16/16；A1-A15/A11b；MWW sha 2641d3b6…）。
- 改动差异：`.tmp/p10r2/round2.diff`（87 行，2 hunks）；pre 基线 `.tmp/p10r2/pre-round2-MainWindow.Windows.cs`（fab7eabb…）。
- 工位日志：`.tmp/p10r2/build.log`（30.07 s，EXIT=0）、`test.log`（327/0/0，78 ms，EXIT=0）、`format.log`（0 B，EXIT=0）。
- 独立复算：被审文件 SHA/行数/CRLF 如 §1.4；HEAD `InitTray` 203-218 与 XAML 1308-1328 由 `git show` 核对。

---

## 五、结论与许可边界

1. **P10 本轮静态安全复核：pass（无阻断）**。F1（局部完整构造 + 入树后一次发布）与 F2（创建/入树异常经 ctor → OnLaunched 既有 ShowFatal+Exit 失败关闭；不继续 InitHotkey/正常 Loaded/_delayCts）均已按行号核实；HEAD 的 ForceCreate 等 try-catch 降级、正常 10 菜单/事件/初值与退出语义保持不变；冒烟静态不构造托盘/钩子/系统能力。S01–S05、S08 静态 pass；S07/S09 静态 pass。
2. **不等于验收**：S06 成功路径、S07 被动快照、S09 真实交互均未运行；工位 327/327 与 16/16 只是过程证据，不是 lead 冻结树验收。P10 的「运行验收」仍缺证据。
3. **联合 hold 继续**：P11 task-56 已最终 needs_revision（BLK-1：ignored Assets 字节未进入构建输入指纹），按 safe-gates:31-34 与 acceptance-matrix:7，P10 单侧 pass 不能解除 hold。真实 App 冒烟、产品打包/ZIP、GitHub Actions、commit/tag/Release 继续冻结；tag/Release 须主人明确点头（用户级铁律）。
4. **无第 3 轮**：P10 为最后第 2 轮；本报告不发起 R3、不改修、不自动恢复 P11。
5. 本工位交付物仅本文件；产品/脚本/记录其它文件未改。若 lead 需要运行层证据，须另行授权并按 safe-gates 的受控执行约束进行。

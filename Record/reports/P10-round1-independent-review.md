# P-10 第 1 轮独立安全复核（task-54）

- 日期：2026-11-07 22:2x（Asia/Shanghai）
- 复核对象：P-10 冒烟可信化 + task-53 第 1 轮返工（R1–R4）后的工作树；基线 HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`（全部相关改动**未提交**）
- 工位性质：独立复核工位，只读审查；**本单只写本文件**，未改产品/脚本/其他记录，未 commit/push/tag，未派子代理
- 关联契约：task-54（本工位）；上游实现 task-53 及其返工记录 `Record/reports/P10-round1-repair.md`、`Record/plans/P10-round1-repair.md`
- 契约工具可用性：本工位工具集内**没有** `team_task_get`/任务工具，无法读取 task-54 契约正文；按派单 prompt 与下列五文件执行，未将工具缺失视为卡死：
  `Record/reports/P10-round1-repair.md`、`Record/plans/P10-round1-repair.md`、`Record/reports/P10-P11-independent-review.md`、`Record/plans/P10-P11-safe-gates.md`、`Record/plans/P10-P11-acceptance-matrix.md`

## 总判定

| 维度 | 判定 |
|---|---|
| R1 托盘迁出 XAML（结构、唯一入口、冒烟不可达） | **pass** |
| R1 正常模式等价与失败面（新 null 字段） | **needs_revision**（唯一阻断，见 §5 F1） |
| R2 冒烟单实例 fail-closed / 正常容错 | pass |
| R3 冒烟根读取异常归 Invalid | pass（附非阻断残留） |
| R4 KeyInterceptor 入 guard、正常 Esc 不变 | pass |
| S01–S05、S08 | pass（静态；S02 部分测试） |
| S06 | 未做（必须运行，本单禁止） |
| S07 | 静态 pass；被动前后快照未做 |
| S09 正常启动语义不变 | **needs_revision**（失败路径语义被改变） |
| **P-10 整体** | **needs_revision** |

即使本轮为 pass，P-10 也**不能单独解除 hold**：必须 P-11 task-56 同 pass（safe-gates:34，acceptance-matrix:7）。

---

## 一、复核范围、方法与证据边界

### 1.1 做了什么

1. 读上述五文件全文（safe-gates 38 行、acceptance-matrix 53 行）。
2. 用 `git diff --no-index --ignore-space-at-eol` 将 21:48 的工位备份（`.tmp/p10r1-backup/`）与现文件逐文件比对，确认本轮返工净差异**只落在宣称的六个文件**：`src/PrtScr.App/App.xaml.cs`、`MainWindow.Windows.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`src/PrtScr.Core/SmokeProtocol.cs`、`src/PrtScr.Core.Tests/SmokeProtocolTests.cs`。CRLF/LF 噪声已用 `--ignore-space-at-eol` 消除。
3. 用 `git diff --ignore-space-at-eol HEAD -- <file>` 复核相对基线的净语义差异（排除整文件换行 churn）。
4. 逐个方法阅读源码、grep 调用点与字段引用（调用图、字段引用清单见各节）。
5. 核对 HEAD 原实现（`git show HEAD:<path>`）以判定"正常模式语义不变"。
6. 只读核对纯 Core 规则与测试文件的行号/内容（**未执行**测试）。

### 1.2 没做什么（硬边界，如实声明）

- 未 `dotnet build`、未跑 `Core.Tests`、未跑 format；因此 `P10-round1-repair.md:72-78` 的 **327 通过 / build 0 警 0 错 / format 0 是工位过程数字，不是本轮验收**（safe-gates:31-33：子工位门禁仅过程证据，lead 冻结树门禁才是验收）。本报告不采信其为证据。
- 未运行任何 App smoke（`--smoke`）、未 publish/打包/ZIP、未触发 GitHub Actions。
- 未做真实键鼠 / UIA / 托盘交互 / Esc 实测；未激活或终止任何用户实例。
- 未新增依赖、未改 ACL、未提权。
- 未做矩阵 S07 要求的**被动前后快照**（`Record/tools/P10-SideEffectSnapshot.ps1`）；S07 仅静态复核。

### 1.3 被审文件身份（本轮复核时点，工作树）

| 文件 | 行数 | SHA-256 |
|---|---|---|
| `src/PrtScr.App/App.xaml.cs` | 307 | `53135f2d0436d2017f4d234237d228df9cdcabb419022f4b409f8c76ab6f62c7` |
| `src/PrtScr.App/MainWindow.Windows.cs` | 439 | `fab7eabb5aae2c8e1d3ef4d2b5bf49c1e1f404755e93df4efc7fabb32f9ba65f` |
| `src/PrtScr.App/MainWindow.xaml` | 1308 | `b46701af41e971294e4099a225161a6c72bcaeef9c461d766aa4fd311c02a8cb` |
| `src/PrtScr.App/MainWindow.xaml.cs` | 379 | `723dda7b52755e7a03e1cc0a59b606adb295f30fb302e235b4d87917a8ab8de0` |
| `src/PrtScr.Core/SmokeProtocol.cs` | 243 | `973a27ae2295830a5362839bbeda7be81dbb0fee890b0614f1ffe83430f1f201` |
| `src/PrtScr.Core.Tests/SmokeProtocolTests.cs` | 239 | `fb3dccb6d2da9ef8aaddbbe405e0285bdf65bc95966cf177b2c7f57c32b9801b` |

补记：HEAD 中 `App.xaml.cs` 与 `MainWindow.xaml` 为 LF，工作树六文件为 CRLF；仓库无 `.gitattributes` 且 `core.autocrlf=false`，故 `git diff HEAD --numstat` 显示整文件增删（`App.xaml.cs` 307/270、`Windows.cs` 439/391、`MainWindow.xaml` 1308/1330、`MainWindow.xaml.cs` 379/362）；加 `-w` 后为 69/32、48/0、1/23、19/2。**这不影响语义结论**，但换行 churn 会污染后续审计，建议本轮冻结后统一归一（非阻断，见 §5 F7）。

---

## 二、R1–R4 逐项独立复核

### R1a 托盘改为正常模式代码创建：唯一入口 + 冒烟不可达 —— **pass**

**唯一入口链（grep 全 `src`，权威）**

- `InitTray()` 唯一调用点：`src/PrtScr.App/MainWindow.xaml.cs:91`，位于 `if (!SmokeSession.IsRequested)`（:89）块内。
- `CreateTrayIcon()` 唯一调用点：`src/PrtScr.App/MainWindow.Windows.cs:215`（`InitTray` 的 try 内第一句）。
- `new TaskbarIcon` 唯一出现：`src/PrtScr.App/MainWindow.Windows.cs:251`（`CreateTrayIcon` 内）。
- 由此 `TaskbarIcon` 在整个程序里**只可能**经 `MainWindow.xaml.cs:91 → Windows.cs:215 → Windows.cs:251` 产生；冒烟分支 :89 为假时不进入，无第二条构造路径。

**冒烟不可达的完整依据（不是"只把元素留在树里"）**

1. `SmokeSession.Begin()` 在 `src/PrtScr.App/App.xaml.cs:28`（App 构造函数第一句），早于 `InitializeComponent`（:47）、早于 `new MainWindow`（:153），故 `IsRequested` 在窗口构造前已定。
2. 冒烟下 `MainWindow` 构造体 :89 的 guard 为假 → 不调用 `InitTray` → 不调用 `CreateTrayIcon` → `TaskbarIcon` 实例从未被创建。
3. 因此 H.NotifyIcon 自身在 `Loaded` 里调 `ForceCreate` 的那条路径**没有对象可触发**（XAML 元素已删除，见下）。`Windows.cs:203-204` 的注释所述前提与实际调用图一致。
4. 冒烟的 `OnLoaded` 走 `MainWindow.xaml.cs:107-113` 提前 return，不触任何托盘字段（`DelayCaptureItem`/`CancelDelayItem`/`TrayIcon` 在冒烟下均为 `null!`，但冒烟路径上的引用点全部不可达——逐点见 R1b 引用清单）。

**XAML 侧干净**

- `MainWindow.xaml` 由 1330 行降到 1308 行（`wc -l`）；`grep -n 'TrayIcon\|TrayMenu\|TrayPinItem\|DelayCaptureItem\|CancelDelayItem\|tb:' src/PrtScr.App/MainWindow.xaml` → **0 命中**（无残留命名引用、无 `tb:` 前缀）。
- 当前尾部结构：:1303 `</Grid>`（内容区）、:1304 `</ScrollViewer>`、:1305 `</NavigationView>`、:1307 `</Grid>`（`RootGrid`，:8 声明）、:1308 `</Window>`。原托盘元素是 `RootGrid` 最后一个子元素（HEAD :1308-1328），现在由 `Windows.cs:258` `RootGrid.Children.Add(TrayIcon)` 加在末尾，**树位置一致**。

**等价性逐项（对照 HEAD `MainWindow.xaml:1308-1328` 原块与当前 `Windows.cs:233-259`）**

| 维度 | HEAD（XAML） | 当前（代码） | 结论 |
|---|---|---|---|
| 菜单项顺序与文本 | 设置 / 从剪贴板贴图 / 从剪贴板识别文字 / 全屏截图到剪贴板 / 延时截图 / 取消延时截图 / 截图历史… / 屏幕贴图管理… / 分隔线 / 退出 | `Windows.cs:236-249` 同序同文案 | 一致 |
| 事件绑定 | XAML `Click` 属性 | `:236-249` 传 `ShowSettings_Click`/`PastePin_Click`/`OcrClipboard_Click`/`FullScreen_Click`/`DelayCapture_Click`/`CancelDelay_Click`/`History_Click`/`OpenPinManager_Click`/`Exit_Click` | 同名同义 |
| 命名项 | `TrayPinItem`/`DelayCaptureItem`/`CancelDelayItem` | `:237`/`:241`/`:243` 同名字段 | 一致 |
| `CancelDelayItem` 初值 | `Visibility="Collapsed"` | `:244` `= Visibility.Collapsed` | 一致 |
| `ToolTipText` | `实践小梅IshWY — 截图工具` | `:253` 同字符串 | 一致 |
| `ContextMenuMode` | `SecondWindow` | `:254` `ContextMenuMode.SecondWindow` | 一致 |
| `IconSource` | `ms-appx:///Assets/app.ico`（字符串→type converter） | `:255` `new BitmapImage(new Uri("ms-appx:///Assets/app.ico"))` | 等价；`src/PrtScr.App/Assets/app.ico` 存在（155941 B，2026-10-20） |
| `ContextFlyout` | 子元素 `MenuFlyout x:Name="TrayMenu"` | `:256` `ContextFlyout = TrayMenu`（`:235` 创建） | 一致 |
| 初始化后的公共动作 | `InitTray`：`ForceCreate`、左右双击命令、`ResetTrayToolTip`、`Opening` 日志 | `:216-220` 同序同动作 | 一致 |
| 退出语义 | `Exit_Click`：Log→`TrayIcon.Dispose()`→`_hotkey.Dispose()`→`ExitProcess(0)` | `Windows.cs:430-437` 逐句相同 | 一致 |
| 悬停/单击提示复位 | `ResetTrayToolTip()` 文案 | `MainWindow.Hotkeys.cs:174-175` 与 HEAD 同字符串 | 一致 |

**顺序差异（非阻断，已判为无行为影响）**：HEAD 中 `InitTray()` 在 `MainWindow.xaml.cs:80`，早于 `SettingsNav.SelectedItem`(:81)、`Loaded` 订阅(:82)、`ApplyBackgroundFromConfig`(:84)；当前 `InitTray()` 在 :91，位于三者之后。逐项核查：`SettingsNav_SelectionChanged` → `ShowSection` 只切 `Visibility` 与 `ContentScroll.ChangeView`，不触托盘字段；`ApplyBackgroundFromConfig`（`MainWindow.Appearance.cs:57-74`）只读 `App.Config.Current` 并设 `BackgroundImage`/`BackgroundPreviewImage`，不触托盘；构造体内无消息泵，`Loaded` 不可能在构造函数返回前触发。故不构成语义变化。

**另核对（矩阵 :48 要求的构造期副作用）**：`MainWindow.xaml.cs:21` `private readonly HotkeyService _hotkey = new();` 是字段初始化器，早于构造体执行，冒烟下同样会执行；但 `HotkeyService` 的构造函数为空实现（`Services/HotkeyService.cs:81` `public HotkeyService() { }`），线程与 `RegisterHotKey` 只在 `Start`( :84-93 )/`Restart`( :96-100 ) 中产生。故"跳过 InitHotkey 调用"在构造期确无全局副作用，矩阵 :48 的核查点成立。

### R1b 正常模式失败面：`InitTray` 吞异常 + `null!` 字段 —— **needs_revision**

**事实**：`Windows.cs:211-227` `InitTray` 的 `catch` 只 `App.Log("Tray: 初始化失败 {ex}")`（:225），异常被吞后函数正常返回；而 `CreateTrayIcon`（:233-259）是**逐段赋值**：`TrayMenu`(:235) → `TrayPinItem`(:237) → `DelayCaptureItem`(:241) → `CancelDelayItem`(:243) → `TrayIcon`(:251)。五个字段声明为 `null!`（:205-209），无任何 null 守卫（除 `TrayPinItem`，见下）。

**托盘字段全部引用点（`grep -rn` 全 `src/**/*.cs`，共 38 处，逐一核过）**

| 引用点 | 是否有 null 守卫 | 冒烟可达 | 正常模式可达 |
|---|---|---|---|
| `MainWindow.Capture.cs:178` `CancelDelayItem.Visibility = Visible`（在 :181 `try` **之外**） | 无 | 否（热键未装） | 是（热键/托盘延时入口） |
| `MainWindow.Capture.cs:188` `TrayIcon.ToolTipText`（倒计时循环，`try` 内） | 无 | 否 | 是 |
| `MainWindow.Capture.cs:219` `CancelDelayItem.Visibility = Collapsed`（`finally`） | 无 | 否 | 是 |
| `MainWindow.Capture.cs:220` `ResetTrayToolTip()`（`finally`） | 无 | 否 | 是 |
| `MainWindow.Hotkeys.cs:174-175` `ResetTrayToolTip` → `TrayIcon.ToolTipText` | 无 | 否 | 是（`InitTray`:219 与 `Capture.cs:220` 两处调用） |
| `MainWindow.CaptureSettings.cs:277` `DelayCaptureItem.Text`（`UpdateCaptureOptions`） | 无 | 否（`OnLoaded` 冒烟分支先 return） | 是（`:154` `OnLoaded` 必走一次） |
| `MainWindow.PinSettings.cs:133` `if (TrayPinItem is not null)` | **有**（:135 才写 `Text`） | 否 | 是 |
| `Windows.cs:249` 退出项绑定、`:433` `TrayIcon.Dispose()` | 无 | 否 | 仅托盘菜单可达 |

**逐路径判阻断性**（`try` 起始点决定"半构造"到哪一段）

- **路径 A（异常抛在 :241 之前，即 `TrayMenu`/`TrayPinItem` 段）**：`DelayCaptureItem` 仍为 `null`。`OnLoaded` 正常分支 `:154 UpdateCaptureOptions()` → `CaptureSettings.cs:277` **必 NRE**；`OnLoaded` 是 `async void`（`MainWindow.xaml.cs:105`），异常经 Dispatcher → `App.UnhandledException`（`App.xaml.cs:32-44`）→ 非冒烟下 `ShowFatal` 弹框 + `e.Handled=true`；**之后的预热/捕获、`StartMemoryTimer`、`RefreshOcrStatusAsync` 全部不执行**，且每次启动复现。→ **阻断性：可复现、用户可见、破坏正常启动初始化链**。
- **路径 B（异常抛在 :243-244，即 `CancelDelayItem` 段）**：`DelayCaptureItem` 已非空（路径 A 不触发），`CancelDelayItem` 为 `null`。任何延时截图（托盘 `DelayCapture_Click` 或全局热键 `OnHotkeyPressed`→`Capture.cs:139`）进入 `StartDelayedCaptureAsync`：`:176-177` 先 `new CTS` 并赋 `_delayCts`，随后 `:178` **在 `try` 之外** NRE → 异常冒泡，`:214` 的 `finally` **不会执行** → `_delayCts` 永不清空。此后每次按截图热键都会在 `Capture.cs:122-127` 命中"再按一次 = 取消"分支并 `return`；`_delayCts` 只能由 `:218` 置空（`grep -rn '_delayCts'` 确认无其他写点）→ **截图热键与延时截图永久楔死，只能重启进程恢复**，且只有一行日志。→ **阻断性：静默、永久、直接废掉主功能**。
- **路径 C（异常抛在 :251-257，`TrayIcon` 构造/初始化器）**：`DelayCaptureItem`/`CancelDelayItem` 均已非空，路径 A 不触发；延时截图时 `:188` 在 `try` 内 NRE → 被 `:210` 捕获记日志；`finally` `:218` 先置空 → `:220` `ResetTrayToolTip` NRE 逃出 `finally`（`cts.Dispose()` 未执行，泄漏一个 CTS）；从热键入口时被 `OnHotkeyPressed:161` 捕获，从托盘入口时（不成立：托盘菜单本身不可见）无入口。→ **非阻断**（状态可恢复，仅资源泄漏）。
- **路径 D（异常抛在 :258 `RootGrid.Children.Add`）**：`TrayIcon` 非空但未入树，`InitTray` 后续 `:216-220` 全部不执行 → 托盘不可见、左右键命令未接。无 NRE。→ 非阻断（降级为无托盘）。
- **`TrayIcon.Dispose()`（`:433`）单独判**：唯一入口是托盘菜单"退出"项（`:249` 绑定），托盘未建成则该菜单不可达 → **不存在独立 NRE 路径**，但代价是用户失去"退出"菜单项（只能关窗/任务管理器），属降级。

**与基线的对比（这是判 needs_revision 的关键，而不是"概率低所以放过"）**：HEAD 中 `TaskbarIcon` 由 `InitializeComponent` 构造，同一类失败（资源/PRI/XAML 对象构造异常）会从 `MainWindow` 构造函数抛出 → `App.xaml.cs:47-59` `xaml-initialization-failed` → **失败关闭**（明确弹框并终止）。当前实现把同类失败变成"启动成功但降级"，并在路径 A/B 上演化为启动即报错或热键永久失效。P-10 这一轮的目标正是"失败必须可见、不能假绿"，此处把**失败静默降级**，与本轮取向相反。返工报告也自认这是"本轮引入的新残余风险"（`Record/reports/P10-round1-repair.md:105`），但仅列为"待确认"，未给守卫。

**修复要求见 §5 F1**（可只做其一：局部构造后整体赋值，或对 4 个引用点 + `Dispose` 加守卫；前者更小且能同时消除全部 NRE 与半构造状态）。

### R2 `App.xaml.cs` 冒烟单实例异常 fail-closed、正常 fail-open —— **pass**

- `App.xaml.cs:101-119`：`isFirstInstance = TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested)`（:104）；`catch`（:106）内先判冒烟 → `:111 Log` → `:112 SmokeSession.Fail("single-instance-check-failed")` → `:113 return`；非冒烟走 `:117-118` 原 fail-open（`isFirstInstance = true`）并保留原注释。
- `TryAcquireSingleInstance`（:195-220）：`createdNew` 为真返回 true；否则**仅当 `activateExisting`** 才 `try { _activateEvent.Set(); } catch {}`（:207-215 区域）→ 冒烟传 false，**绝不 Set 命名事件**，不会激活用户既有实例 ✓。
- 既有实例时（:121-134）：冒烟 → `Log` + `FailInstanceConflict()` + return（:126-128，退出码 3，`SmokeSession.cs:109-112`）；正常 → `Log` + `PInvoke.ExitProcess(0)`（:131-133）语义与 HEAD 一致。
- 正常容错链无 NRE：fail-open 后 `_activateEvent` 保持 `null`，`StartActivationListener` 首句 `var activateEvent = _activateEvent; if (activateEvent is null) return;`（:224-226）→ 不启动监听线程、不抛异常 ✓。这是"正常容错不因新分支退化"的直接证据。
- 冒烟失败结果不可被后续成功覆盖：`Fail` → `Complete(1,...)`（`SmokeSession.cs:114-117`）先把 `_completed` 置真并写盘后 `ExitProcess`；`App.Log` 与后续代码不再有执行机会。
- 与 S03 对应：非 0 退出 + 不 Set ✓（静态）。

### R3 `SmokeProtocol` 读取异常归 `Invalid` + 局部 `KeyInterceptor` guard —— **pass**（附非阻断残留）

- `src/PrtScr.Core/SmokeProtocol.cs:69-98`：目录段判定（`DirectoryInfo` → 叶子名==nonce → 父名==`IshWY-smoke` → `TryNormalizeDirectory(temp)` 比对 → `Exists` → `HasReparsePoint` → `IsEmpty` → `CanWrite`）整体包在 `try` 内；`:93-98` `catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)` → `Invalid("root-unreadable")`。三条契约错误码（`root-reparse-point`/`root-not-empty`/`root-not-writable`）仍由显式分支返回，未被 catch 吞成含糊错误 ✓。
- 路径段 `:55-67`：`try` 内 `IsPathFullyQualified`→`root-not-absolute`、`HasDotSegments`→`root-has-dot-segments`、`GetFullPath`；`catch (Exception)`（无过滤器）→ `root-not-a-path`。这条 catch 面较宽，但覆盖的是纯字符串/路径解析，副作用为零，可接受。
- 非断言级残留（非阻断，见 §5 F5）：目录段的 catch **过滤器**只含三类异常，理论上的 `NullReferenceException`/`ArgumentException`/`NotSupportedException` 仍会穿透为未处理异常。逐点推演后可达面极窄：`leaf==nonce`（32 hex）与 `smokeRoot.Parent.FullName == declaredTemp` 两个前置判定已排除盘根（`Parent` 为空）与 `\\?\` 长路径前缀（父串不等 → `root-outside-temp`），`Exists` 先于 `File.GetAttributes` 排除不存在，超长路径属 `IOException` 子类已被过滤。故判为"设计可接受、建议注释说明为何不再扩大过滤器"，不列为阻断。
- `CanWrite`（:181-196）自身 `catch` 返回 false → `root-not-writable`，不从内部抛出；`TryNormalizeDirectory`（:142-156）`catch` 返回 null → `temp-unavailable`。二者都把异常内化了 ✓。
- 测试侧（`SmokeProtocolTests.cs`）：`:186-195` `RootOutsideDeclaredTempIsRejected`（断言 `root-outside-temp`）、`:197-206` `MalformedRootPathIsRejected`（路径含 `\0`，断言 `root-not-a-path`）为**真断言**；`:208-238` `ReparsePointRootIsRejectedWhenConstructible` 在 `:221-224` `catch (UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }` —— **提前 return 的用例仍计 pass，`:228-229` 的断言不执行**。故"重解析点已测"**只在本机具备建符号链接权限时成立**；本工位未运行、未构造，按不可断言处理（矩阵 S02 的"带重解析点"证据仍以静态 + 条件性用例计）。返工报告 `:32` 已要求"建不了直接 return 记未测"，与其 `:121-124` 的勘误一致 ✓。
- 结论：R3 的代码改动本身 pass；不可读/不可写两类环境未实测，属"未验证"而非"通过"。

### R4 `KeyInterceptor` 移入冒烟 guard、正常 Esc 语义不变 —— **pass**

- `MainWindow.xaml.cs:59-69`：`if (!SmokeSession.IsRequested)` 内 `var interceptor = new Services.KeyInterceptor(hwnd)` + `KeyDown += vk => { if (vk == 0x1B && !_clearHistoryBusy && Environment.TickCount64 - Services.OcrUiService.LastWindowClosedTick > 2500) AppWindow.Hide(); }`。
- 与 HEAD `:60-66` 逐 token 相同（`0x1B`、`!_clearHistoryBusy`、2500 ms 阈值、`AppWindow.Hide()`），仅外层加了 guard ✓（正常模式完全等价）。
- `KeyInterceptor` 侧效应核对（`Services/KeyInterceptor.cs:53-61`）：构造函数只做 `Map[hwnd] = this` 与 `SetWindowLongPtr(hwnd, GWLP_WNDPROC, ...)` —— **是窗口过程子类化，不是全局键盘钩子**，且 `Map` 静态持有自身（:36-37, :57-58），不存在"局部变量被 GC 回收导致拦截失效"的问题（HEAD 同样写法，非本轮引入；本工位核查后不列为缺陷）。
- 冒烟下不子类化主窗、不订阅 Esc，冒烟不依赖 Esc（无交互），正常模式行为不变 ✓。`MakeClientAreaFullWindow`（:128）的调用方只有取景窗/`PinMenuWindow`/`Toast*`（`grep` 5 处），与主设置窗无关，故迁移不改变主窗外观 ✓。
- 观察（非阻断）：该局部变量模式依赖 `Map` 保活，属既有设计；若将来有人改为"不放入 `Map`"，会退化成静默失效。本轮未改，建议注释固定（§5 F6）。

---

## 三、S01–S09 逐项（静态复核；运行类标注"未做"）

| ID | 判定 | 证据与精确位置 | 未做/残留 |
|---|---|---|---|
| S01 `--smoke` 缺 nonce/root → 非 0、禁止回落真实 data | **pass（静态）** | `App.xaml.cs:28` `SmokeSession.Begin()` 是 App 构造函数第一句；`SmokeProtocol.Parse` :43 无 `--smoke` → `NotRequested`、:46-53 nonce/root 缺失或 nonce 非 Guid-N → `Invalid`；`SmokeSession.cs:48-72`：`Invalid → Console.Error "ISHWY_SMOKE_INVALID_CONTRACT: {error}" + Exit(2)`；`App.Config` 懒创建（`App.xaml.cs:11-17` `_config ??= new(SmokeSession.DataRoot, Log)`），故非法上下文下无任何 `data/` 写入。 | 未运行进程；`Exit(2)` 是否被 WinUI 启动壳延迟不可观测（静态判直连 `PInvoke.ExitProcess`，`SmokeSession.cs:229`）。 |
| S02 非 Guid-N / root 不匹配 / 非新空目录 / 带重解析点 → 失败关闭、不清理用户目录 | **pass（静态；测试部分）** | `SmokeProtocol.cs:46-53`（nonce 格式）、:72（叶子==nonce）、:75（父名==`IshWY-smoke`）、:78-82（父目录==temp，`OrdinalIgnoreCase`）、:84（`Exists`）、:86（`HasReparsePoint` 自身+祖先至盘根，:159-172）、:88（`IsEmpty`）、:90（`CanWrite` 用 `CreateNew`+`DeleteOnClose` 探针，失败即 false）→ 全部返回 `Invalid`，**不删除、不清空、不覆盖**（探针仅在判定为可写时创建并即删；`FileMode.CreateNew` 保证不覆盖既有文件）。 | 不可读（`root-unreadable`）与不可写（`root-not-writable`）**未实测**（返工报告 :107 已如实记其尝试失败：`FileOptions.BackupSemantics` 不存在，error CS0117，已撤回）；reparse 用例为条件性。 |
| S03 已有正常实例 → 非 0、不向既有激活事件 Set | **pass（静态）** | `App.xaml.cs:104` `activateExisting: !SmokeSession.IsRequested`；`TryAcquireSingleInstance` 仅 `activateExisting` 为真才 `_activateEvent.Set()`；冒烟走 :123-128 `FailInstanceConflict()`（`SmokeSession.cs:109-112` 退出码 3）。 | 未用真实并发实例做负例（本单禁止激活/终止用户实例）。 |
| S04 XAML/Loaded 抛异常 → 非 0、不得生成 success=true | **pass（静态）** | 三条漏斗：① XAML：`App.xaml.cs:47-59` `catch` → `Log` → `:56 Fail("xaml-initialization-failed: {T}")` → 非冒烟才 `ShowFatal`；② Loaded/异步：`:32-44` `UnhandledException` → `Log` → 冒烟 `Fail("unhandled-exception: {T}")`（`Fail` 内 `ExitProcess`，其后的 `ShowFatal` 冒烟不可达）；③ 构造/启动：`:176-188` `Fail("on-launched-failed: {T}")`。成功判定唯一来源 `Complete`：`success = exitCode == 0`（`SmokeSession.cs:126-164`），`Fail` 恒传 1 → 失败结果里 `success=false`。 | 未做注入式运行；"结果写失败时 Exit(6) 且无结果"（`SmokeSession.cs:166-196`）静态成立。 |
| S05 未 Loaded / 未完成队列往返 → 超时仅失败 | **pass（静态）** | 三项检查只由真实事件提交：`MarkXamlInitialized`（`App.xaml.cs:78`）、`OnLoaded`→`NotifyMainWindowLoaded`（`MainWindow.xaml.cs:111`）、`TryEnqueue` 回调（`SmokeSession.cs:87-106`，`!queued` → `Fail("dispatcher-queue-unavailable")`）。`StartWatchdog`（`SmokeSession.cs:215-227`）睡 20 s 后**只调 `Fail("timeout-waiting-for-main-window-roundtrip")`**，无任何计时→成功的路径；旧"睡 3 秒 ExitProcess(0)"已删除（`App.xaml.cs:161-163` 注释留证）。`SuccessChecks` 顺序固定（`SmokeProtocol.cs:26-31`），乱序不成功（`SmokeRunState.TryRecordCheck` 仅在 `HasExactSuccessChecks` 时置终态）。 | 未运行 20 s 超时实测。 |
| S06 成功路径三检查 + exit0 身份一致 | **未做** | 静态链路完整：`SmokeResult` 字段齐全（`SmokeProtocol.cs:233-243` camelCase；`Record` 由 :116 `Serialize` 决定），`ProcessId/ExecutablePath/Nonce/DataRoot` 由 `SmokeSession.cs:198-212` 解析；三检查值取固定常量。 | 必须安全放行后的本地隔离冒烟；本单禁止。 |
| S07 文件只落隔离根、系统/网络能力跳过 | **静态 pass；快照未做** | 写点全集：`App.Log`（`App.xaml.cs:280-306`，`Directory.CreateDirectory(SmokeSession.LogDirectory)`+`app.log`）与 `TryWriteResult`（`<root>/smoke-result.json`）；`ConfigService` 构造函数无 IO（`src/PrtScr.Core/ConfigService.cs:200-219`），`Load` 被冒烟跳过（`App.xaml.cs:84-97`）；`CaptureHistoryService` 的 `StorageDirectory = SmokeSession.HistoryDirectory`（`Services/CaptureHistoryService.cs:18-26`），且 `AddAsync` 才建目录（:69-77，冒烟不截图）；`StartupService` 双入口硬防护（`Services/StartupService.cs:46-51`、:89-90）+ 调用点 `App.xaml.cs:139-140` 冒烟不调用；托盘/窗口钩子/全局热键/预热/OCR 网络/内存计时全在正常分支（`MainWindow.xaml.cs:89-100`、`:115-176`）；`EscOverPinHook.Install` 冒烟跳过（`App.xaml.cs:61-72`）；激活监听跳过（`:158-159`）；`RefreshIfEnabled` 跳过（`:139-140`）。冒烟读取用户配置为**零**（`Current` 为默认实例，`Load` 未调）。 | 矩阵 S07 要求"所有接线静态复核 + **被动前后快照**"：快照未做（本单未运行快照脚本）；`Record/reports/P10-observation-before.json` 存在但非本轮生成，不采信。 |
| S08 只允许一次终态、失败不被成功覆盖 | **pass（静态 + 规则用例）** | `SmokeRunState`：`_completed` + `_gate` 锁（:12-15），`TryFail` 见终态返回 null（:36-47），`TryRecordCheck` 见终态/未知/重复返回 null（:18-33）；`SmokeSession.Complete`（:126-164）失败先 `TryFail`，返回 null 即 return（不重写结果）；`TryWriteResult` 用 `_resultWritten` 去重（:166-196）。用例：`SmokeRunStateTests` 6 条（`PartialChecks_DoNotComplete` / `AllThreeChecks_CompleteWithCanonicalOrder` / `UnknownOrDuplicateChecks_AreIgnored` / `Failure_IsTerminal_AndLateSuccessCannotOverride` / `Success_IsTerminal_AndFailureCannotOverride` / `EmptyFailureReason_IsNormalized`）。 | 进程级并发（`ExitProcess` 与写结果竞态）只能静态判；结果写失败 → Exit(6) 的路径未运行。 |
| S09 正常启动语义不被改变 | **needs_revision** | 正常路径逐项等价成立：托盘 10 项/文案/事件/初值/悬停文案/图标/树位置/退出语义（§R1a 表）；配置与历史存储路径不变（`ConfigService` `BaseDirectory` 非冒烟 = 程序目录）；`StartupService` 正常分支未改；`HotkeyService` 构造路径未改。**但失败面语义被改变**：HEAD 的托盘构造失败 = `xaml-initialization-failed` 干净终止；当前 = 吞异常降级，且路径 A 使 `OnLoaded` 在 :154 中断（弹框、后续初始化缺失）、路径 B 使截图热键永久失效（§R1b）。 | 正常托盘交互/真实 Esc 未实测（需主人授权，属运行项）。 |

**补充：三真实事件 / 20 s 失败看门狗 / 单终态未回归**（prompt 指定重点）——静态核对后结论为"未回归"：三事件仍由 `MarkXamlInitialized`(:78)、`NotifyMainWindowLoaded`(MainWindow :111)、`TryEnqueue` 回调产生（`SmokeSession.cs:87-106`），顺序固定且由 `App` 构造函数先于 `OnLoaded` 保证；看门狗仍只失败（20 s，`:215-227`）；单终态由 `_completed` + `TryFail/TryRecordCheck` 双重返回 null 实现（`:18-47`、`:126-164`）。R1 的托盘迁移与 R4 的 guard 均未触碰这三条链。

---

## 四、静态安全许可 / 未做运行项（分列）

### 4.1 静态安全许可（仅就"隔离与失败关闭"而言）

1. **冒烟路径无系统副作用**：托盘（`InitTray` 不可达）、窗口过程子类化（`KeyInterceptor` guard）、全局热键（`InitHotkey` 等不可达）、`EscOverPinHook`（`App.xaml.cs:61-72`）、启动项写（双防护）、预热（`WriteableBitmap`/`CaptureWindow`/`WarmUpNative`/`EnsureOverlay` 全在 `OnLoaded` 正常分支）、OCR 网络（`RefreshOcrStatusAsync` 同）、历史目录创建（`AddAsync` 不触发）、激活监听（`:158-159`）——全部跳过。
2. **写点仅两处，且都在隔离根内**：`<root>/data/logs/app.log`（`App.Log`）与 `<root>/smoke-result.json`（`TryWriteResult`，先写 `.tmp` 再 `Move(overwrite:true)`）。隔离根由 `Begin` 前的 `Parse` 强校验（非空、非共享父、父名固定、空、非重解析、可写）。
3. **失败关闭面完整**：非法契约 Exit(2)、已有实例 Exit(3)、写结果失败 Exit(6)、看门狗/异常 Exit(1)；成功只能来自三真实事件。
4. 因此，**就冒烟执行的安全性**，本工位认为静态条件已具备；但按 safe-gates:34，这只允许 lead 受控执行，不等于验收；且 **P-10 自身仍有阻断项（§5 F1）需先修**。

### 4.2 未做运行项（不得记为通过）

1. App `--smoke` 实跑（S06 三检查 + exit0 + 身份一致；含"用户实例在跑时应非 0"的负例）。
2. 20 s 看门狗实跑（S05）、写入失败 Exit(6) 实跑。
3. 矩阵 S07 的被动前后快照（本单未运行 `Record/tools/P10-SideEffectSnapshot.ps1`）。
4. 不可读根（`root-unreadable`）与不可写根（`root-not-writable`）实测——未构造，未经 ACL/提权。
5. 重解析根用例的**真断言**（本机是否具备建符号链接权限未知；条件性 return 不计通过）。
6. 正常模式托盘真实交互（左/双击、10 项菜单、延时截图、取消、退出）、真实 Esc、真实截图热键。
7. build / Core.Tests / format / publish / ZIP / 远端 Actions —— 本单一律未做；**327、0 警 0 错、format 0 均为工位过程数字，不是 lead 冻结验收**。
8. `dataRoot` 命名风险（结果字段 `dataRoot` 实为冒烟根而非 app data 目录）：静态确认 `SmokeSession.cs` 里 `DataRoot` = `launch.Root`，而数据目录是 `<root>/data`；字段名与语义有歧义，**未改**，按"命名风险"记录并由 lead 裁决（非阻断，§5 F4）。

---

## 五、阻断项与修复要求（一次列齐）

### 阻断（修复前 P-10 不得判 pass）

**F1（阻断，唯一实质缺陷）— 消除 `InitTray` 失败后的 `null!` 半构造状态**。任选其一，推荐 (a)：

- (a) 局部构造后整体赋值：`CreateTrayIcon` 内先用局部变量 `var menu/new items/var icon = new TaskbarIcon{...}`，全部成功后再一次性赋给五个字段并 `RootGrid.Children.Add(icon)`；这样失败时五个字段要么全为空、要么全非空，**且在创建阶段不对外暴露半构造字段**。随后仍需 (b) 的兜底守卫。
- (b) 在 `InitTray` 失败时把五个字段显式置为可判定状态，并在全部引用点加守卫：`Capture.cs:178`/`:188`/`:219`/`:220`、`Hotkeys.cs:174-175`（`ResetTrayToolTip` 内判 `TrayIcon`）、`CaptureSettings.cs:277`、`Windows.cs:433`（`Exit_Click` 判 `TrayIcon`）。最小可行是"`TrayIcon` 与 `CancelDelayItem`/`DelayCaptureItem` 各判一次 null"，因为路径 A/B 都由这两个字段触发。
- 验收要求：`UpdateCaptureOptions`（`CaptureSettings.cs:272-277`）与 `StartDelayedCaptureAsync`（`Capture.cs:168-181`）在托盘字段为空时**不抛异常**，且 `_delayCts` 不会因异常卡在非空（即 `:178` 移入 `try` 或前置判空）。
- 复核方式：修复后本工位只需复核该单点（`git diff --ignore-space-at-eol` 该文件 + 上述引用点），R2–R4、S01–S08 无需重做，除非 diff 触及。

**F2（阻断，同 F1 的验收表述）— S09 的"正常语义不变"在失败路径需成立**：即修复后，托盘创建失败要么恢复 HEAD 式失败关闭（明确弹框并终止），要么降级但**不破坏截图热键、不中断 `OnLoaded` 初始化链**。二选一需在返工报告写明所选策略与理由。

### 非阻断（建议，可与 F1 同批或延后）

- **F3**：`SmokeProtocol.cs:93-98` 的异常过滤器建议加一行注释说明"为何只过滤三类"，并把 `INVALID` 契约码清单（`root-unreadable` 等）登记进 `Record/reports/P10-smoke-app.md` 的失败码表（返工报告 :107 第 5 条已自认未登记）。
- **F4**：`SmokeResult.DataRoot` 语义歧义（冒烟根 vs 应用 data 目录）——建议改注释或改名为 `smokeRoot`，需与 P-11 的读取方（`scripts/smoke.ps1` 校验字段）同步；因涉及 P-11 接口，本单不要求，交 task-56 一并裁。
- **F5**：`SmokeProtocolTests.ReparsePointRootIsRejectedWhenConstructible` 的条件性 return 使"未测"与"通过"不可区分；建议在 return 前 `Assert.Skip`（若测试框架支持）或输出显式标记，否则门禁里这条用例的绿灯不可作为覆盖证据。
- **F6**：`KeyInterceptor` 的局部变量依赖静态 `Map` 保活（`Services/KeyInterceptor.cs:36-37,57-58`），建议加注释固定该不变量，避免后续重构静默失效。
- **F7**：LF→CRLF 整文件 churn（无 `.gitattributes`）。建议本轮冻结后统一归一，否则后续每个审计都要靠 `-w` 才看得清真实差异。

### `--smoke` 接口约束（本单只指出，不重复 P-11 复核）

- App 侧事实：冒烟**只认 argv 字面 `--smoke`**（`SmokeProtocol.HasModeArgument`，`SmokeProtocol.cs:118-127`，逐项 `Trim` + `OrdinalIgnoreCase` 相等）；环境变量单独存在永不开冒烟（`Parse` 先判 args 再判 env，`:43`）。
- 因此 `scripts/smoke.ps1` 必须在子进程命令行里真实传 `--smoke`（该脚本 `:35` `$modeArgument`、`:71` 唯一构造点；其正确性由 P-11 task-55/56 审，本单不重复）。

---

## 结论

- **P-10 第 1 轮判定：needs_revision**。R2/R3/R4 与 S01–S05、S08 静态成立；R1a（托盘迁出 XAML 的结构、唯一入口、冒烟不可达）与正常路径等价性成立；**唯一阻断是 R1b/S09：`InitTray` 吞异常后五个 `null!` 字段带来的半构造状态**，其中路径 A 破坏每次启动的 `OnLoaded` 初始化链，路径 B 使截图热键**静默永久失效**，与 HEAD 的失败关闭语义相比是本轮引入的退化。
- 本判定**不解除任何 hold**；即使修复后 P-10 判 pass，也**必须 P-11 task-56 同 pass** 才可由 lead 决定受控执行（safe-gates:34、acceptance-matrix:7）。
- 本工位未运行 App smoke/build/publish/ZIP/Actions、未键鼠/UIA、未激活或终止用户实例、未新增依赖、未改 ACL；除本文件外未写任何文件，未 commit。

---
复核工位：P-10 独立复核（task-54）｜复核时点：2026-11-07 22:2x +08:00｜基线 HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`

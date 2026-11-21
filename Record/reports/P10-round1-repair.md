# P10 第 1 轮返工报告（task-53）

| 项 | 值 |
| --- | --- |
| 单号 | task-53（P10 产品返工，第 1 轮 / 最多两轮） |
| 契约 | `Record/plans/P10-round1-repair.md`（R1–R5 + 验证约束） |
| 前置审查 | `Record/reports/P10-P11-independent-review.md`（静态安全未放行） |
| 基线 | HEAD `5e74f62`，工作树含未提交用户变更；测试基线 324 |
| 工位 | 唯一 P10 返工工位，Flash；未派子代理 |
| 日期 | 2026-11-07 |
| 门禁输出目录 | `.tmp/out-p10r1/`（独立 OutDir，未写默认 bin） |
| 结论 | R1–R4 静态已落地，整包 build / Core.Tests / format 全绿；**App smoke hold 未解除、未运行任何 App smoke**，正常模式托盘行为未实测 |

## 一、做了什么

### R1 托盘改为正常 InitTray 代码创建（S07-1）

- `src/PrtScr.App/MainWindow.xaml`：删除 `xmlns:tb="using:H.NotifyIcon"`（第 6 行）与根 Grid 内整个 `tb:TaskbarIcon x:Name="TrayIcon"` / `MenuFlyout x:Name="TrayMenu"` 块（原 1308–1328 行）。文件从 1330 行降到 1308 行。
- `src/PrtScr.App/MainWindow.Windows.cs`：新增五个字段 `TrayIcon` / `TrayMenu` / `TrayPinItem` / `DelayCaptureItem` / `CancelDelayItem`（原为 XAML `x:Name` 生成字段，:205-209），新增 `CreateTrayIcon()`（:233-258）与静态助手 `TrayMenuItem(string, RoutedEventHandler)`（:261-266）；`InitTray()`（:211-227）首行调用 `CreateTrayIcon()`，其余 `ForceCreate()`、左/双击命令、`ResetTrayToolTip()`、`TrayMenu.Opening` 日志订阅保持原顺序。
- 托盘对象只在 `InitTray()` 内构造，而 `InitTray()` 只被 `MainWindow` 构造函数中 `!SmokeSession.IsRequested` 的 guard 块调用（`MainWindow.xaml.cs:88-100`）。冒烟模式因此**根本不构造 `TaskbarIcon` 对象**，H.NotifyIcon 构造函数内订阅的 `Loaded → ForceCreate` 不可能被触发，窗口 `Loaded` 事件（`MainWindow.xaml.cs:81`）也就没有可创建托盘的路径。
- 菜单/命名/事件/延时状态**逐项保持**：菜单文本与顺序 10 项不变（设置 / 从剪贴板贴图 `TrayPinItem` / 从剪贴板识别文字 / 全屏截图到剪贴板 / 延时截图 `DelayCaptureItem` / 取消延时截图 `CancelDelayItem`（初值 `Collapsed`）/ 截图历史… / 屏幕贴图管理… / 分隔线 / 退出），每个项绑定的 `Click` 处理器与原 XAML `Click` 完全同名同目标；`ToolTipText`、`ContextMenuMode=SecondWindow`、`IconSource=ms-appx:///Assets/app.ico`、`ContextFlyout=TrayMenu` 全部保留；`RootGrid.Children.Add(TrayIcon)` 保持它仍是根 Grid 最后一个子元素。

### R2 冒烟下单实例检查异常 fail-closed（S03）

- `src/PrtScr.App/App.xaml.cs:106-119`：`catch` 内先判 `SmokeSession.IsRequested`，是则记日志、`SmokeSession.Fail("single-instance-check-failed")` 并 `return`；正常模式落到原有 fail-open（`isFirstInstance = true`）不变。
- `Fail` 内部经 `Complete(1, reason)` 写失败结果后 `ExitProcess`，不会继续走到正常启动，失败结果也不会被后续成功覆盖（`SmokeSession.cs:126-164` 的 `TryFail` 保护）。

### R3 SmokeProtocol 目录/环境异常归一为 Invalid（S02）

- `src/PrtScr.Core/SmokeProtocol.cs:69-98`：目录存在性、重解析点、空目录、可写探测整段放入 `try`，新增 `catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)` → `Invalid("root-unreadable")`（:93-97）。外部 `Parse` 不再让环境读取异常穿透成非契约异常。
- 合法路径的判定顺序（leaf / parent / outside-temp / exists / reparse / empty / writable）未变。
- `src/PrtScr.Core.Tests/SmokeProtocolTests.cs` 补 3 条：`RootOutsideDeclaredTempIsRejected`（:186-195，`%TEMP%/IshWY-elsewhere/IshWY-smoke/<nonce>` → `root-outside-temp`）、`MalformedRootPathIsRejected`（:197-206，内嵌 NUL → `root-not-a-path`）、`ReparsePointRootIsRejectedWhenConstructible`（:208-238，能建符号链接才断言 `root-reparse-point`，建不了直接 return 记未测）。

### R4 窗口局部 KeyInterceptor 只正常模式构造（R4）

- `src/PrtScr.App/MainWindow.xaml.cs:59-69`：局部 `KeyInterceptor` 的构造与 `KeyDown` 订阅整体移入 `if (!SmokeSession.IsRequested)`；正常模式 Esc（`vk == 0x1B`）的 `AppWindow.Hide()` 路径与去抖条件未改。

### 验证

- 全部命令经 `.tmp/build-guard.ps1` 串行（同一时刻唯一，锁 `CreateNew` 独占），独立 `OutDir=.tmp/out-p10r1/`，未使用默认 bin、未用 `-t:Compile`。
- 未触碰运行中的正常实例；未执行任何 App smoke、publish、ZIP、Actions、键鼠/UIA、commit/push/tag/Release。

## 二、证据

### R1 证据

- XAML 托盘子元素已完全移除：`MainWindow.xaml:1-9` 头部为 `xmlns:x` 行结尾（第 4 行），根 Grid 在 :7-9；`:1305` `</NavigationView>`、`:1306` 空行、`:1307` `</Grid>`、`:1308` `</Window>`。全文件 grep `tb:|TrayIcon|TrayMenu|TrayPinItem|DelayCaptureItem|CancelDelayItem` 无命中。
- 代码重建与删除前 XAML 逐项一致（比对本仓库备份 `.tmp/p10r1-backup/src__PrtScr.App__MainWindow.xaml` 原 1308–1329 行）：10 个菜单项文本/顺序/事件同名，`CancelDelayItem.Visibility=Collapsed` 初值一致，`ToolTipText="实践小梅IshWY — 截图工具"`、`ContextMenuMode.SecondWindow`、`IconSource=ms-appx:///Assets/app.ico`、`ContextFlyout=TrayMenu` 一致。
- `MainWindow.Windows.cs:211-227` `InitTray()` 先 `CreateTrayIcon()`（:215）再 `ForceCreate()`（:216）、`LeftClickCommand`/`DoubleClickCommand = new DelegateCommand(ShowFromTray)`（:217-218）、`ResetTrayToolTip()`（:219）、`TrayMenu.Opening +=` 日志（:220）；`try/catch` 与失败日志行为未改。
- `:251-257` `new TaskbarIcon { ToolTipText=..., ContextMenuMode=ContextMenuMode.SecondWindow, IconSource=new BitmapImage(new Uri("ms-appx:///Assets/app.ico")), ContextFlyout=TrayMenu }`；`:258` `RootGrid.Children.Add(TrayIcon);`。
- 跨文件引用无需改动且仍指向同一字段（均属正常模式路径）：`MainWindow.Capture.cs:178`/`:219` 写 `CancelDelayItem.Visibility`、`:188` 与 `MainWindow.Hotkeys.cs:175` 写 `TrayIcon.ToolTipText`、`MainWindow.CaptureSettings.cs:277` 写 `DelayCaptureItem.Text`、`MainWindow.PinSettings.cs:133-135` 读 `TrayPinItem`（自带 null 守卫）。
- 退出行为未改：`MainWindow.Windows.cs:433` `Exit_Click` 内 `TrayIcon.Dispose()` 原样保留；`MainWindow.xaml.cs:74-78` 的 `Closing → Cancel + Hide` 原样保留。
- 冒烟不构造托盘的依据链：冒烟 guard（`MainWindow.xaml.cs:88-100`）不调用 `InitTray()` → 全程序再无 `new TaskbarIcon` / `TrayIcon` 初始化点（grep 确认）；`MainWindow.xaml` 不再有 `tb:` 元素，`InitializeComponent()` 不会实例化任何托盘对象；H.NotifyIcon 的 `Loaded→ForceCreate` 只在构造时订阅，对象不存在即不可达。XAML 中不再有 `TaskbarIcon` 类型引用，故无需 `xmlns:tb`。

### R2 证据

- `App.xaml.cs:104` `isFirstInstance = TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested);`；`:109-113` 冒烟分支 `Log` → `SmokeSession.Fail("single-instance-check-failed")` → `return`；`:116-118` 保留原注释与 `isFirstInstance = true`（正常模式容错）。
- 非首实例分支未改：`:123-129` 冒烟 `SmokeSession.FailInstanceConflict(); return;`（`Complete(3, "another-instance-is-running")`，`SmokeSession.cs:109-112`），`:131-133` 正常 `PInvoke.ExitProcess(0)`。
- 失败结果不可被覆盖：`SmokeSession.cs:126-164` 失败分支先 `TryFail`，返回 null 即 return；`TryWriteResult` 用 `_resultWritten` + lock 只写一次。

### R3 证据

- `SmokeProtocol.cs:69-98` 新增 try/catch（:93 catch 过滤器列三类异常，:97 `Invalid("root-unreadable")`）；内层 :71-91 为原判定链；`:100` 合法返回 `Valid`。
- 原有 `:55-67` 的 Path try/catch 保留，NUL 非法路径仍归 `root-not-a-path`（:66）。
- 新增用例行号：`SmokeProtocolTests.cs:186-195`、`:197-206`、`:208-238`；既有 11 个用例在 `:44-184`；类定义 `:9`，`Env` 助手 `:34`，`ParseValid` 助手 `:41`。
- 测试结果：327 通过，即 324 基线 + 本轮 3 条。

### R4 证据

- `MainWindow.xaml.cs:59-69`：注释说明冒烟不需要窗口局部钩子，`if (!SmokeSession.IsRequested)` 包住 `new Services.KeyInterceptor(WinRT.Interop.WindowNative.GetWindowHandle(this))`（:62）与 `KeyDown` lambda（:63-68，条件 `vk == 0x1B && !_clearHistoryBusy && Environment.TickCount64 - LastWindowClosedTick > 2500` → `AppWindow.Hide()`）。:70 之后的正常窗口初始化不变。

### 门禁原始结果（串行 build-guard）

| # | 命令（经 `.tmp/build-guard.ps1 -Tag … -Cmd …`） | 退出码 | 结果 |
| --- | --- | --- | --- |
| 1 | `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p10r1/`（tag `p10r1-final-build`） | 0 | 已成功生成，0 警告 0 错误，10.34s |
| 2 | `dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --no-build --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p10r1/`（tag `p10r1-final-test`） | 0 | **327 通过 / 0 失败 / 0 跳过 / 共 327**，66ms |
| 3 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore`（tag `p10r1-final-format`） | 0 | 无输出 |

- 过程态：首跑 `p10r1-build` 亦 0 警 0 错（25s）。`p10r1-format` 首跑退出 2，报 `src/PrtScr.Core.Tests/SmokeProtocolTests.cs(239,2): error FINALNEWLINE`（补写用例时丢了文件末尾换行），补回结尾 CRLF 后通过，非格式规则回归。
- 每条命令由 `.tmp/build-guard.ps1` 打印 `BUILD-GUARD: EXIT=<rc> elapsed=<n>s`；标签（`p10r1-final-build` / `-test` / `-format`）可原样复现。工位控制台原文未落盘，不冒充统一门禁证据。

### 文件落盘状态

| 文件 | 字节 | 行数 | 行尾 |
| --- | --- | --- | --- |
| `src/PrtScr.App/MainWindow.xaml` | 87379 | 1308 | CRLF，无 lone LF，末尾 CRLF |
| `src/PrtScr.App/MainWindow.Windows.cs` | 16415 | 439 | 同上 |
| `src/PrtScr.App/MainWindow.xaml.cs` | 15996 | 379 | 同上 |
| `src/PrtScr.App/App.xaml.cs` | 11160 | 307 | 同上 |
| `src/PrtScr.Core/SmokeProtocol.cs` | 9491 | 243 | 同上 |
| `src/PrtScr.Core.Tests/SmokeProtocolTests.cs` | 8729 | 239 | 同上 |

- 改动前原文备份在 `.tmp/p10r1-backup/`（按相对路径扁平命名），用于证明只改这六个文件；未改 `scripts/`、`scripts/CI`、`.github/workflows/` 与其他 `src` 文件，未撤回他人未提交工作。
- `git diff --stat` 对本轮文件显示整文件级增删（如 `MainWindow.xaml` 2638 行），系 HEAD 为 LF、工作树为 CRLF 的换行噪声，不代表内容级改动；本轮内容改动以上述备份为对照。

## 三、进行中

- 无。R1–R4 静态修改已完成，构建/测试/格式门禁已通过。
- 待独立复核（lead 指派）：R1 需复核「冒烟下 `Loaded` 不可达」的依据链与托盘语义等价性；R2/R3/R4 需复核失败关闭路径与错误码命名。**在独立静态放行前，App smoke hold 保持**，本工位不自行解除。

## 四、风险与遗留

1. **正常模式托盘行为未实测**（契约禁止 App smoke / 实际运行）：代码创建路径的图标是否真实出现在通知区、右键菜单在 `SecondWindow` 模式下是否正常弹出、延时截图菜单项文案刷新、退出项清理，均无运行证据。静态等价性只能证明结构/文本/事件绑定一致。
2. **托盘创建失败后的 null 残余风险**：`InitTray()` 的 `try/catch` 会吞掉异常；若 `CreateTrayIcon()` 在赋值前抛异常，`TrayIcon`/`TrayMenu` 保持 `null!`，之后 `MainWindow.Capture.cs:178/188/219`、`Hotkeys.cs:175`、`CaptureSettings.cs:277` 等正常模式写入会 NRE。原 XAML 方案下这些字段由 `InitializeComponent` 保证非空，因此这是本轮引入的新残余风险（正常路径仅在托盘初始化失败时暴露）。`PinSettings.cs:133` 已有 null 守卫。
3. **reparse 用例是条件性的**：`ReparsePointRootIsRejectedWhenConstructible` 在无符号链接权限时直接 `return`（其后的断言不执行），报告不能据此宣称本机已验证重解析点行为；本轮测试输出未标明该用例是「已断言」还是「构造失败提前返回」，属未定。
4. **不可读目录 / 不可写目录未测**：尝试用 `File.OpenHandle(..., FileOptions.BackupSemantics)` 独占打开目录以构造不可读场景，编译失败（`error CS0117: “FileOptions”未包含“BackupSemantics”的定义`，build `p10r1-build3` 退出 1）；真正构造需 P/Invoke `CreateFileW` 或修改真实目录 ACL/提权，均超出契约与本工位权限，故整体撤回该用例，`root-unreadable` 分支与 `root-not-writable` 行为无运行证据。
5. **新错误码 `root-unreadable` 未登记**：`Record/reports/P10-smoke-app.md:16` 的失败码清单没有它；本轮按「不改历史报告」处理，是否补入契约/文档由 lead 决定。
6. **`dataRoot` 命名风险（契约 R5 要求如实记录）**：`SmokeSession` 写入结果的 `DataRoot` 实际是 `launch.Root`（smoke 根），不是应用数据目录；名称易被误读。本轮不修改协议字段。
7. **XAML 不再声明托盘元素**：未来若需要在 XAML 中使用 `tb:` 前缀须重新加回 `xmlns:tb`；这是有意的隔离代价。
8. **过程门禁绿 ≠ lead 冻结统一门禁**：本报告 327/327 与 0 警 0 错只绑定 `.tmp/out-p10r1/` 快照，不作为放行依据。
9. **未触碰他人的未提交工作**：`src/PrtScr.App/Services/CaptureHistoryService.cs`、`Services/StartupService.cs`、`scripts/publish.ps1`、`.github/workflows/*`、`scripts/gate.ps1`、`scripts/lib/*`、`scripts/smoke.ps1` 等仍为原状。

## 五、待确认

1. 独立复核是否接受「冒烟不构造 `TaskbarIcon` 对象 ⇒ H.NotifyIcon 的 `Loaded→ForceCreate` 不可达」作为 `Loaded` 自动创建的排除依据；若不接受，需改为在构造后移除元素或更小生命周期闸门。
2. `root-unreadable` 错误码命名与是否补入失败码文档/测试映射表。
3. 托盘初始化失败时是否需要一个显式的「托盘不可用」保护（例如 `null` 检查或延迟创建），还是接受当前残余风险并在后续单处理。
4. 本轮不允许运行 App smoke；正常模式托盘回归需在获得许可后由 lead 安排真实运行验证，本工位不自行解除 hold。

## 六、勘误（补充，不覆盖原历史报告）

- `Record/reports/P10-smoke-app.md:56` 声称新增 19 条用例「覆盖……写探测、重解析点」，与 `src/PrtScr.Core.Tests/SmokeProtocolTests.cs` 的实际用例集合不符。19 条的总数本身成立：`SmokeProtocolTests` 贡献 13 个用例（修复前 10 个 `[Fact]` + 1 个含 3 组 `InlineData` 的 `[Theory]`，行号 :44、:52、:65、:82、:98、:115、:123、:131、:138、:146、:171）加 `SmokeRunStateTests` 的 6 个 `[Fact]`；报告同段简写为「10 个 `[Fact]`、6 个 `[Fact]`」不准确。实质问题是覆盖面：原 `SmokeProtocolTests` 用例中**没有** `root-reparse-point`、`root-not-writable`、`root-outside-temp`、`root-not-a-path`，也没有任何写探测用例，故「覆盖 reparse/write-probe」不成立。独立审查 `Record/reports/P10-P11-independent-review.md:36` 已指出此点。
- 本轮补充：`root-outside-temp`、`root-not-a-path` 已由新增用例覆盖；`root-reparse-point` 为条件性覆盖（权限允许时才断言）；写探测/不可写仍无测试。原件不改，以本段为准。

## 七、未做清单

- 未运行任何 App smoke（含 `--smoke`），未启动/未触碰运行中的正常实例。
- 未执行 publish / 打包 / ZIP / GitHub Actions；未 commit / push / tag / Release。
- 未执行真实键鼠输入或 UIA 自动化。
- 未派子代理；未修改 `scripts/`、`scripts/CI`、`.github/workflows/`、README、docs、ProjectLog 或配置字段。
- 未新增或升级任何依赖。
- 未实测正常模式托盘（图标/菜单/延时项/退出）与 Esc 行为回归。
- 未构造不可读/不可写目录（理由见风险 4）；未修改真实目录 ACL、未提权。
- 未把本轮门禁当作 lead 统一门禁，未解除安全 hold。


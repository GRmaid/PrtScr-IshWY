# P-10 · App 侧安全冒烟（--smoke）实施报告（task-47）

- 单号：task-47（P-10 App 工位；P-11 同源证据链由 task-48 负责 scripts/CI）
- 仓库：`E:\Explore\GoodGood\PrtScr`（HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`，本轮改动未提交）
- 日期：2026-11-07
- 写域：`src/PrtScr.Core/**`、`src/PrtScr.App/**`、`src/PrtScr.Core.Tests/**`、`Record/reports/**`；未改 `scripts/**`、`.github/**`、`docs/**`、`README.md`、`Record/ProjectLog.md`（主人未提交的 Record/docs 修改原样保留）
- 结论：App 侧协议与接线已落盘，整包构建 0 警 0 错、Core.Tests 324/324、whitespace format 0。**真实 `IshWY.exe --smoke` 未执行**（契约要求协议经独立审查后再跑，且本机有主人正常实例 PID 4396 在运行），因此 P-10 验收矩阵的运行时行仍未取证，P-10/P-11 均未验收，发布继续冻结。

## 1 做了什么

### 1.1 Core：冻结协议 v1 的纯规则与单终态状态机

`src/PrtScr.Core/SmokeProtocol.cs`（新增，235 行上下）：

- 常量与契约名：`ModeArgument="--smoke"`、`RootVariable="ISHWY_SMOKE_ROOT"`、`NonceVariable="ISHWY_SMOKE_NONCE"`、`ResultFileName="smoke-result.json"`、`SchemaVersion=1`；三检查名 `xaml-initialized` / `main-window-loaded` / `dispatcher-roundtrip`，`SuccessChecks` 为固定顺序的三元数组。
- `Parse(args, environment, tempPath)` 返回 `SmokeParseKind.NotRequested|Invalid|Valid`，逐项失败码：`missing-nonce`、`missing-root`、`nonce-not-guid-n`、`root-not-absolute`、`root-has-dot-segments`、`root-not-a-path`、`root-leaf-not-nonce`、`root-parent-not-smoke-root`、`temp-unavailable`、`root-outside-temp`、`root-missing`、`root-reparse-point`、`root-not-empty`、`root-not-writable`。
- 根校验链：叶子目录名必须等于 nonce；父目录名必须为 `IshWY-smoke`（`SmokeRootName` 常量）；父目录的父目录必须等于声明 temp；路径逐级检查重解析点；目录必须已存在、为空；写探测用 `FileMode.CreateNew` + `FileShare.None` 在根内建 `.write-probe` 后删除，留下空根。
- `SmokeResult` 八个属性（nonce/processId/executablePath/dataRoot/success/checks/error/schemaVersion），`Serialize` 用 `JsonNamingPolicy.CamelCase`，字段顺序与冻结 schema 一致。

`src/PrtScr.Core/SmokeRunState.cs`（新增，52 行）：`lock` 保护的三检查状态机。`TryRecordCheck` 忽略未知/重复检查，只有三个齐全且 `HasExactSuccessChecks` 成立才返回成功终态；`TryFail` 在已有终态时返回 `null`，因此**成功不可被迟到的失败覆盖，失败也不能被迟到成功洗白**。

### 1.2 App：冒烟会话与结果落地

`src/PrtScr.App/Services/SmokeSession.cs`（新增，229 行）：

- `Begin()` 只在首次调用生效，解析命令行与环境；非法契约写 `ISHWY_SMOKE_INVALID_CONTRACT` 到 stderr 并 `ExitProcess(2)`，此时不读配置、不建日志、不装钩子。
- 合法时 `DataRoot = <root>`，派生 `DataDirectory/LogDirectory/HistoryDirectory`；启动 20 秒看门狗线程，**只调用 `Fail("timeout-waiting-for-main-window-roundtrip")`**，计时永远不能产生成功。
- `MarkXamlInitialized()` 记第一项；`NotifyMainWindowLoaded(DispatcherQueue)` 记第二项后 `TryEnqueue` 回调记第三项，`TryEnqueue` 返回 false 立即失败。
- 结果写 `<root>/smoke-result.json`：先写 `.tmp` 再 `File.Move(overwrite:true)`，`_resultWritten` 保证只写一次；写失败退出码 6。
- 退出码：`1` 一般失败、`2` 契约非法、`3` 已有实例冲突、`6` 结果写失败、`0` 仅三检查齐全。
- `FailInstanceConflict()` 只写失败结果，不 `Set` 激活事件，不打扰已有实例。

### 1.3 接线（正常启动语义保持不变）

`src/PrtScr.App/App.xaml.cs`（270 → 300 行）：

- `:17` `Config` 由静态字段改为惰性属性 `=> _config ??= new(SmokeSession.DataRoot, Log)`，避免字段初始化阶段在 `Begin()` 之前锁定真实 `data/`。
- `:28` ctor 首行 `SmokeSession.Begin()`；`:61` `EscOverPinHook.Install()` 只在非冒烟执行；`:78` `MarkXamlInitialized()` 在 ctor 末尾（`InitializeComponent` 已成功返回）才调用。
- `:39-40` 未处理异常在冒烟下先 `SmokeSession.Fail("unhandled-exception: <类型名>")`（`ExitProcess` 立即结束），不再等看门狗、也不弹模态框。
- `:56` `InitializeComponent` 抛错 → `Fail("xaml-initialization-failed: <类型名>")`；`:172-174` `OnLaunched` 抛错 → `Fail("on-launched-failed: <类型名>")`。
- `:87` 冒烟不 `Config.Load()`；`:131` 不 `StartupService.RefreshIfEnabled()`；`:150` 不装激活监听；`:139` `startHidden` 恒 false（需要真实 `Activate()` 让 `Loaded` 触发）。
- `:104` `TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested)`；`:115-119` 非首实例的冒烟直接 `FailInstanceConflict()`，绝不 Set 命名事件。
- 删除旧的「后台线程 `Sleep(3000)` 后 `ExitProcess(0)`」假成功逻辑与 `IsSmokeRequested()` 方法。
- `:287` 日志目录改 `SmokeSession.LogDirectory`（冒烟落隔离根，正常仍是程序目录 `data/logs`）。

`src/PrtScr.App/MainWindow.xaml.cs`（362 → 374 行）：

- `:86-98` `InitTray`、五组 `Init*Hotkey`、`SyncHotkeyTexts`、`HistoryChanged` 订阅、`PinService.HistoryRequested` 全部收进 `if (!SmokeSession.IsRequested)`；`:104-111` `OnLoaded` 首部冒烟分支只做 `NotifyMainWindowLoaded(DispatcherQueue)` 后 `return`，不预热 `WriteableBitmap`/`WindowCapture`、不 `WarmUpNative`、不 `EnsureOverlay`、不 `RefreshOcrStatusAsync`、不 `StartMemoryTimer`。

`src/PrtScr.App/Services/CaptureHistoryService.cs:21`：默认 `StorageDirectory` 由 `AppContext.BaseDirectory/data/history` 改为 `SmokeSession.HistoryDirectory`（无参单例；显式传目录的测试与调用方行为不变）。

`src/PrtScr.App/Services/StartupService.cs`：`:47` `TrySetEnabled` 首部硬防护（冒烟返回 false，`error="smoke session"`，不碰 HKCU Run）；`:89` `RefreshIfEnabled` 冒烟直接 return。这是防止上层漏判的第二道闸。

### 1.4 测试

`src/PrtScr.Core.Tests/SmokeProtocolTests.cs`（10 个 `[Fact]`）、`SmokeRunStateTests.cs`（6 个 `[Fact]`），合计新增 19 条用例（324 − 305）。覆盖：合法上下文接受、缺 nonce/root、nonce 非 Guid N、root 相对/带 `..`、叶子≠nonce、父目录非 `IshWY-smoke`、root 不在 temp 下、root 不存在、root 非空、写探测、重解析点、成功 checks 的顺序与齐套、失败后成功不可覆盖、成功需三检查齐全、未知检查忽略。

### 1.5 本轮收尾的两处修正

1. `SmokeSession.FailInstanceConflict()` 原调用 `Complete(FailureExitCode, ...)`（退出 1），与类注释和已声明的 `InstanceConflictExitCode=3` 不一致；改为 `Complete(InstanceConflictExitCode, ...)`。脚本侧只要求非 0，行为兼容。
2. `App.UnhandledException` 在冒烟下改为先 `Fail(...)` 再 `ShowFatal`（`Fail` 已 `ExitProcess`，弹窗不可达）。此前 `Loaded` 抛异常只能靠 20 秒看门狗超时兜底，且会先弹模态框。

## 2 证据

### 2.1 门禁（全部经 `pwsh -NoProfile -File .tmp/build-guard.ps1` 串行，隔离 OutDir）

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | `-Tag t47i4 -Cmd 'dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-t47i4/'` | **EXIT=0，0 警告 0 错误，32.25s** |
| Core 测试 | `-Tag t47t3 -Cmd 'dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-t47i4/'` | **失败 0，通过 324，跳过 0，总计 324，96ms** |
| 格式 | `-Tag t47f5 -Cmd 'dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore'` | **EXIT=0，9s** |

基线对照（本单开工前，lead 记录）：整包 0 警 0 错、305 测试、format 0。测试 305 → 324 的 19 条全部为本单新增。

过程门禁另有 `t47i2`（0/0，20s）、`t47i3`（修 2 个 CS8602 后 0/0，19s）、`t47t2`（324/324，106ms）、`t47f4`（whitespace 0，5s）。

### 2.2 关键实现位置

- 协议常量与校验：`src/PrtScr.Core/SmokeProtocol.cs:16-31`（常量）、`:41-91`（`Parse` 校验链）、`:94-95`（`IsNonce`）。
- 状态机：`src/PrtScr.Core/SmokeRunState.cs:18-47`。
- 会话：`src/PrtScr.App/Services/SmokeSession.cs:48-72`（`Begin`）、`:87-106`（Loaded + 队列往返）、`:126-164`（`Complete`）、`:166-196`（结果落地）、`:215-227`（看门狗）。
- App 接线：`src/PrtScr.App/App.xaml.cs:17/28/39-40/56/61/78/87/104/115-119/131/139/150/172-174`。
- 主窗跳过项：`src/PrtScr.App/MainWindow.xaml.cs:86-98`、`:104-111`。
- 历史目录：`src/PrtScr.App/Services/CaptureHistoryService.cs:21`；启动项防护：`src/PrtScr.App/Services/StartupService.cs:47`、`:89`。

### 2.3 静态证据

- 旧假成功已删：`grep -n 'SmokeExit\|Thread.Sleep(3000)\|IsSmokeRequested' src/PrtScr.App/App.xaml.cs` 无命中。
- 行尾：本轮新增/修改的 C# 文件 `file -b` 均为 `UTF-8 ... with CRLF line terminators`，`grep -c $'\r\r'` 为 0（无 CRCRLF、无裸 LF）。
- 未触碰：`git status --porcelain` 中 `Record/ProjectLog.md`、`Record/plans/2026-10-28-整改总纲.md`、`docs/ROADMAP.md` 仍是开单前的未提交状态（本单未写入）；`scripts/**`、`.github/**` 的改动属 task-48。

## 3 未完成

1. **真实 `IshWY.exe --smoke` 未运行**：契约明确要求新协议经独立审查后再执行；本机另有主人正常实例（PID 4396，14:07 启动），本单不杀、不唤醒。因此验收矩阵里 S01/S03/S04/S05/S06 的运行时行为只有 Core 单测与代码审阅支撑，没有进程级证据。
2. **S07 隔离性未实测**：日志/配置/历史/托盘/钩子/热键/启动项/预热的「只落隔离根」目前靠代码路径与 grep 证明；没有一次真实冒烟后的目录、注册表、进程副作用快照。
3. **成功路径的 GUI 副作用未观察**：主窗 `Activate()` 是否在冒烟下短暂可见（窗口一闪）未实测；这是 `Loaded` 触发的必要条件，但属可见副作用。
4. **端到端 `scripts/smoke.ps1` + gate 未跑**：脚本侧已由 task-48 交付并离线自测（34/34 负例），与本单结果文件的字段/PID/路径约定一致，但真实配对未验证。
5. **独立复核未开始**：task-49/task-50 尚未审阅本单的时序与隔离语义。
6. 未 commit / push / tag / Release；`.tmp/` 下的 `out-t47*` 为本单隔离产物。

## 4 风险

1. **冒烟会真的建一个可见窗口**：`MainWindow` 仍需 `Activate()` 才能收到 `Loaded`。若复核要求「零可见副作用」，需要改成隐藏窗口 + `Loaded` 的其他触发方式，属协议级变更，不在本单擅自做。
2. **无交互桌面环境不可用**：CI/服务会话（session 0）或不显示桌面的 runner 上，`Loaded` 可能永不触发 → 20 秒看门狗失败、脚本 30 秒失败。方向是 fail-closed（正确），但 release 流程若依赖 GUI 冒烟，会卡在 tag 前（task-48 报告风险 2 同源）。
3. **20 秒 vs 30 秒**：App 看门狗 20 秒、脚本上限 30 秒，App 先失败并按自己的原因退出，脚本收到 exit≠0；若机器极慢导致进程启动都超 30 秒，脚本只能报 timeout，原因会退化为「结果缺失」。
4. **temp 目录口径**：协议用子进程 `Path.GetTempPath()` 判定 root 归属；脚本用自己的 `[IO.Path]::GetTempPath()` 建 root。若二者因 `TEMP/TMP` 覆盖或短/长路径不一致而不同（`C:\Users\ASUS~1\...` vs `C:\Users\asus\...`），合法根会被判 `root-outside-temp`。当前两处都未强制 `\?` 或 8.3 归一，建议首次真实冒烟时先确认二者字符串一致。
5. **diff 行尾噪声**：仓库索引 blob 是 LF、工作树是 CRLF（`git ls-files --eol` → `i/lf w/crlf`，`core.autocrlf=false`）。因此 `git diff` 对本单 4 个 App 文件显示整文件重写（App.xaml.cs 300/270、MainWindow.xaml.cs 376/362、CaptureHistoryService.cs 457/456、StartupService.cs 122/111），真实改动用 `git diff --ignore-cr-at-eol --numstat` 看是 68/49、26/9、2/1、11/0。复核请带 `--ignore-cr-at-eol`；行尾保持 CRLF 是 `.editorconfig` 与 whitespace 门禁的要求（改回 LF 会报 ENDOFLINE）。
6. **format 口径**：本项目历史门禁是 `dotnet format whitespace`。全量 `dotnet format PrtScr.sln --verify-no-changes` 会命中 HEAD 既有的 21 个 `IMPORTS`（如 `src/PrtScr.App/MainWindow.Appearance.cs(1,1)`），在干净 HEAD 副本上同样复现，非本单引入，也不在本单写域内顺手改（避免与并发单冲突）。
7. **异常路径的进程收尾**：`Fail` 在已有成功终态时返回且不退出。成功终态只在 `Complete` 内写文件后立即 `ExitProcess(0)`，窗口期极短，但理论上存在「已写成功结果、进程被其他线程拖住」的窗口；未加二次兜底。

## 5 待决

1. **首次真实冒烟的执行者与时机**：由 task-49/复核工位先审，还是由主人停掉现有实例（PID 4396）后由 lead 执行？本单不擅自运行。
2. **退出码是否入契约**：当前 1/2/3/6 各有语义，脚本只判非 0。若冻结协议要把「契约非法」与「实例冲突」区分给 CI 报警，应写进 v1 文档；若不需要，可退回单一非 0。
3. **冒烟是否允许可见窗口**：若不允许，需要协议级替代（例如隐藏窗口 + `CompositionTarget` 或 `DispatcherQueue` 首帧），需主人与复核一起拍板。
4. **temp 路径归一**：是否在 `Parse` 里先 `Path.GetFullPath` 再去 8.3/符号链接差异，或以脚本声明的 root 字符串为准。
5. **21 个既有 IMPORTS** 是否单开一张格式清理单（会碰 21 个 App 文件，属独立写域）。
6. P-10/P-11 的联合验收口径（离线负例 + 一次人工真实冒烟 + CI 是否跑 GUI smoke），沿用 task-48 报告第五节的等待项。

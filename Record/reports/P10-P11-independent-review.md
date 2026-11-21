# P10-P11 独立审查报告（Flash 审查工位 · task-49）

> 审查对象：P-10 冒烟静态安全；P-11 smoke v1 薄接口兼容性（不含 E01-E10 全审）。
> 审查方式：只读源码/测试/契约/报告，无 build、无 App 冒烟、无脚本执行、无 UIA/合成输入、无进程操作。
> 基线：HEAD 5e74f62，工作树含未提交差异（App.xaml.cs、MainWindow.xaml.cs、CaptureHistoryService.cs、StartupService.cs、scripts/publish.ps1、workflows 等）。
> 总判定：**静态安全未放行**。S07 存在一处可静态证实的系统副作用（XAML 托盘图标在 Loaded 时自动创建），直接违反 safe-gates:23。修补并复核前，lead 不得受控运行新 smoke；即使产品侧修补，P-11 当前 smoke.ps1 不追加 --smoke，脚本入口仍会启动正常模式，必须先返工。

## 做了什么

- 逐行只读 Core 协议与状态机：src/PrtScr.Core/SmokeProtocol.cs、SmokeRunState.cs，及 src/PrtScr.Core.Tests/SmokeProtocolTests.cs、SmokeRunStateTests.cs。
- 逐行只读 App 接线：src/PrtScr.App/App.xaml.cs、MainWindow.xaml.cs、MainWindow.Windows.cs、MainWindow.CaptureSettings.cs、Services/SmokeSession.cs、Services/StartupService.cs、Services/CaptureHistoryService.cs、Services/KeyInterceptor.cs、Services/EscOverPinHook.cs、Services/HotkeyService.cs，以及 src/PrtScr.Capture/WgcWindowCapture.cs、DesktopDuplicationCapture.cs 的静态项。
- 核对 XAML 默认值与事件触发：src/PrtScr.App/MainWindow.xaml 的 ComboBox/ToggleSwitch 初值、SettingsNav、TrayIcon；确认无 x:Bind 命中。
- 核对 H.NotifyIcon.WinUI 2.4.1 的 TaskbarIcon 生命周期（REA 托管元数据）。
- 只读 P-10/P-11 契约与矩阵：Record/plans/P10-P11-safe-gates.md、Record/plans/P10-P11-acceptance-matrix.md；只读 Record/reports/P10-smoke-app.md。
- 只读 P-11 smoke v1 接线：scripts/smoke.ps1、scripts/gate.ps1、scripts/publish.ps1、scripts/lib/SmokeResult.ps1；未审 E01-E10 全部实现，未批准脚本最终版本。
- 未修改产品/脚本/工作流，未运行任何验证命令；仅新增本报告。

## 证据

### S01 — 初始化前非法上下文阻断：静态通过

- src/PrtScr.App/App.xaml.cs:25-28：App 构造体首条语句即 SmokeSession.Begin()；早于 Config 属性、Log、InitializeComponent（:11/:17、:46-59）。
- src/PrtScr.App/Services/SmokeSession.cs:48-72：Begin 用 Interlocked.Exchange 保证只执行一次；解析环境/命令行在 :53-56；NotRequested 直接 return :58-59；非法契约在 :62-66 只写 stderr 的 ISHWY_SMOKE_INVALID_CONTRACT 并 ExitProcess(2)，此时尚未创建日志、配置或任何服务。
- src/PrtScr.Core/SmokeProtocol.cs:41-92：无 --smoke 返回 NotRequested :43-44；缺 nonce/root 返回 Invalid :48-51；root 非绝对/含点段 :58-61；非法路径 :64-66；均不触碰根目录。
- 运行未验证：未执行缺 nonce/root 的真实进程；退出码与 stderr 为静态结论。
- 残余：WinUI 自身启动（Application.Start 与 XAML runtime）不可避免，但不属于 App 的 Config/日志/服务初始化。

### S02 — 临时根与写路径校验：静态通过，测试覆盖有缺口

- SmokeProtocol.cs:52-53、:94-95：nonce 必须为 32 位 Guid N 格式。
- :69-74：root 叶名必须等于 nonce，父目录名必须为 IshWY-smoke。
- :76-80：声明 temp 必须可归一化；root 的父父目录必须与 temp 一致（OrdinalIgnoreCase）。
- :82-89：root 必须已存在、非重解析点（HasReparsePoint :150-163，自 root 逐级检查到驱动器根）、必须为空（:165-169）、必须可写（:172-187 用 .write-probe，CreateNew + DeleteOnClose，异常返回 false）。
- 全部写路径均从隔离根推导：SmokeSession.cs:41-45（DataRoot/DataDirectory/LogDirectory/HistoryDirectory）；SmokeProtocol.cs:216-221（SmokeLaunch.DataDirectory/LogDirectory/HistoryDirectory）；App 日志 src/PrtScr.App/App.xaml.cs:287-291 写 SmokeSession.LogDirectory；ConfigService.cs:217/260-271 写 <root>/data/config.json；CaptureHistoryService.cs:18-24 默认 StorageDirectory=SmokeSession.HistoryDirectory。
- 运行未验证：未构造合法/非法根实测；未验证重解析点目录、只读目录、非空目录的真实行为。
- 测试缺口（与 P10 报告 :56 不符）：src/PrtScr.Core.Tests/SmokeProtocolTests.cs 仅覆盖 no-mode :44-50、valid :52-63、缺 nonce/root :65-80、叶名 :82-96、父名 :98-113、非空 :115-121、缺失 :123-129、相对路径 :131-136、点段 :138-144、序列化 :146-169、检查集合 :171-184；没有 root-reparse-point、root-not-writable、root-outside-temp、root-not-a-path 用例。P10 报告 :56 声称“19 条用例覆盖 reparse/write-probe”不实。
- 残余（低危）：SmokeProtocol.cs 的 try/catch 只包住 :56-67 的 Path 运算；:82-89 的 DirectoryInfo.Exists / HasReparsePoint / IsEmpty 在目录不可读时可能抛 UnauthorizedAccessException/IOException，未被 Parse 捕获，会以 App 构造异常而非契约退出码 2 结束（仍非0，失败关闭，但不符合约定出口）。

### S03 — 既有实例不激活：主路径静态通过，存在 fail-open 残余

- src/PrtScr.App/App.xaml.cs:104：TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested)；冒烟下传 false。
- App.xaml.cs:187-212：createdNew 为 false 时仅当 activateExisting 为 true 才 Set；冒烟下不 Set，直接返回 false。
- App.xaml.cs:113-121：非首实例且冒烟时 Log 后 FailInstanceConflict() 并 return；SmokeSession.cs:109-112 写 Exit(3) 与 another-instance-is-running。正常分支 :123-125 才 ExitProcess(0)。
- **残余风险（建议修）**：App.xaml.cs:106-111 的单实例异常 catch 把 isFirstInstance 置为 true（正常模式的“宁可允许启动”）。冒烟下若命名事件因权限/会话隔离抛异常，会绕过实例冲突检测并按首实例继续，理论上可在主人实例运行时生成 success=true。需要冒烟分支在 catch 中失败关闭，而不是复用正常模式的 fail-open。
- 运行未验证：本机主人实例 PID 4396 未触碰；未执行“有实例时冒烟”的负例，未观察是否 Set 激活事件、是否弹设置窗。

### S04 — XAML/Loaded 异常非0且不得 success：静态通过

- XAML：App.xaml.cs:47-59 的 InitializeComponent 在 try/catch 中；catch 在 :56 调 SmokeSession.Fail("xaml-initialization-failed: ...")，随后 ShowFatal/throw；Fail 在 SmokeSession.cs:114-117→ Complete 中 Exit(1)，冒烟下 ShowFatal（:247-268 MessageBox）不可达。
- 全局异常：App.xaml.cs:32-44 的 UnhandledException 在 IsRequested 时 Log 后 Fail("unhandled-exception: ...")，再 ShowFatal 并 e.Handled=true。
- OnLaunched 整体：App.xaml.cs:168-180 catch 中冒烟调 Fail("on-launched-failed: ...") 后 return。
- MainWindow 构造异常会落上述 OnLaunched catch；OnLoaded 冒烟分支若 NotifyMainWindowLoaded 内 TryEnqueue 抛异常，会进 UnhandledException。
- 运行未验证：未注入 XAML/Loaded 异常；异常漏斗的退出码与结果文件内容为静态结论。

### S05 — 未 Loaded/未往返只超时失败：静态通过

- src/PrtScr.App/Services/SmokeSession.cs:24 WatchdogSeconds=20；:215-227 看门狗线程 Sleep 后只调 Fail("timeout-waiting-for-main-window-roundtrip")，不写 success、不伪造检查。
- 三检查只能由真实事件写入：xaml-initialized 在 App.xaml.cs:78（构造体末尾）；main-window-loaded 在 SmokeSession.cs:88-92（OnLoaded 冒烟分支经 :104-110 调用）；dispatcher-roundtrip 在 :95-101 的 TryEnqueue 回调内。
- 运行未验证：未制造“未 Loaded/队列不执行”场景；20s 与脚本 30s 的先后关系未实测。
- 风险（非安全）：20s 对慢机/首次 WinUI 启动可能偏紧，会以 exit1 假失败；P10 报告 :105 已承认 20s 与脚本 30s 不一致。

### S06 — 成功路径三检查、exit0、身份一致：静态通过，运行未验证

- 成功判定：src/PrtScr.Core/SmokeRunState.cs:18-33（未知/重复返回 null；加入后必须 HasExactSuccessChecks），:26-31；SmokeProtocol.cs:98-105 要求恰好三项且顺序固定。
- 结果写入：SmokeSession.cs:126-164（成功时 Checks=SuccessChecks；ProcessId=Environment.ProcessId；ExecutablePath=ResolveExecutablePath :198-212；DataRoot=launch.Root；Success=true），:166-196 先写 .tmp 再 File.Move(overwrite:true)，失败 Exit(6)；成功 Exit(0)。
- 与脚本校验一致：scripts/lib/SmokeResult.ps1:35-137 严格 8 字段、schemaVersion=1 :76-79、nonce :81-84、processId :86-89、executablePath 归一化 :91-95、dataRoot=Root :97-101、success=true :103-106、checks 集合（不要求顺序）:108-124、error=null :126-129。
- 运行未验证：未产生真实 smoke-result.json；未比对 PID/exe/root 的真实值。
- 残余（低危）：SmokeRunState.TryRecordCheck 对“到达顺序”使用 HasExactSuccessChecks；当前 App 的写入顺序固定（xaml→loaded→roundtrip），但若未来顺序变化会永远无法完成（fail-closed）。非阻断，建议状态机改为集合判定或显式顺序断言。

### S07 — 全局钩子/热键/托盘/预热/网络确实跳过：**阻断（托盘未跳过）**

已证实的跳过项：

- 全局键盘钩子：App.xaml.cs:61-72 的 EscOverPinHook.Install() 仅在 !IsRequested 时执行；EscOverPinHook.cs 无静态构造器，Install() 只在 :215 定义、EnsureOverlayHook :173-184 才调用；App.Log :276 只读 IsHookThread（:266），不会安装。未安装 SetWindowsHookEx。
- 五组热键：MainWindow.xaml.cs:85-97 的 if (!SmokeSession.IsRequested) 包住 InitHotkey/InitFullHotkey/InitPinOcrHotkeys/InitModeHotkey/InitWindowSwitchHotkeys；HotkeyService.cs:81 的构造为空，字段 _hotkey=new() 在 MainWindow.xaml.cs:21 不启动线程，Start/RegisterHotKey 只在 Init* 后发生。
- 预热：OnLoaded 冒烟分支 MainWindow.xaml.cs:104-110 只 Notify 后 return，跳过 WriteableBitmap/WindowCapture 预热（非冒烟 :156-167）、WarmUpNative（:168）、EnsureOverlay（:170）、RefreshOcrStatusAsync（:172）、StartMemoryTimer（:173）。WgcWindowCapture.cs:79/81-83/123-126/337/346 与 DesktopDuplicationCapture.cs:21-25 的类型静态项只有 GUID/lock/null，无静态构造器或 COM 初始化；App.xaml.cs:74-75 赋 Log 不会触发捕获预热。
- 历史/启动项/网络：CaptureHistoryService.Instance 在冒烟静态路径不被触碰（MainWindow.xaml.cs:96 订阅也在 guard 内）；StartupService.TrySetEnabled 在 :47-51 冒烟硬防护返回 false，RefreshIfEnabled 在 :89-90 冒烟 return，IsEnabled 只读 HKCU 且冒烟不调用；Toast/OCR/PinService 均未在冒烟静态路径触碰；无网络调用路径（RefreshOcrStatusAsync 被跳过）。
- 配置写入：XAML 设置控件无初值触发（见 S09 证据），设置处理器不执行；ConfigService 构造无 IO（ConfigService.cs:208-212），Save 才写 <root>/data/config.json。日志只写 <root>/data/logs/app.log（App.xaml.cs:287-291）。
- 窗口级 KeyInterceptor：MainWindow.xaml.cs:60-66 在 guard 之前构造；KeyInterceptor.cs:53-61 只做 SetWindowLongPtr(GWLP_WNDPROC) 子类化 + Map 注册，无 SetWindowsHookEx/RegisterHotKey。它作用于冒烟窗口自身，非全局钩子，但属于“构造期窗口钩子”，需 lead 认可其归类。

**阻断项 S07-1（托盘）**：

- src/PrtScr.App/MainWindow.xaml:1309-1328 在窗口根 Grid 内直接声明 <tb:TaskbarIcon x:Name="TrayIcon" ...>（含 :1314-1327 的 ContextFlyout）。
- MainWindow.xaml.cs:85-97 的 guard 只跳过显式 InitTray()（MainWindow.Windows.cs:203-218，:207 调 ForceCreate）及五组热键/历史订阅/PinService，无法阻止 XAML 元素自身在窗口 Loaded 时创建托盘图标。
- H.NotifyIcon.WinUI 2.4.1（C:/Users/asus/.nuget/packages/h.notifyicon.winui/2.4.1/lib/net10.0-windows10.0.17763/H.NotifyIcon.WinUI.dll，SHA-256 5f9befc63a787908a95bc216ddf28f97e97ca06cfdced829edd70e706f6ac1d1，MVID 58c65019-55f4-44f1-9593-5738e7a83c3e）REA 托管元数据（parent evidence ev_2c222ec1698303a7230ad054808517475f179f936c6cd5e982ff8588dd7c8651）显示：TaskbarIcon..ctor 订阅 Loaded → <.ctor>b__32_0 调 ForceCreate；ForceCreate 调 TrayIcon.Create + SetEfficiencyMode；无 OnLoaded override。
- 结论：冒烟窗口必须 Activate 才能得到真实 Loaded（MainWindow.xaml.cs:157-166），所以 Loaded 时必然创建托盘图标并设置效率模式；这违反 safe-gates:23 “smoke 短路托盘”与矩阵 S07:21，属于未声明的系统副作用。P10 报告 :48 的“托盘已收入 if”只覆盖 InitTray，覆盖不了 XAML 自动创建；:94-98 的“S07 未实测”未识别此静态可证项。
- 另：可见窗口本身已由 P10 报告 :103 承认；托盘图标是额外的用户可见副作用（进程退出后消失，但运行期间存在）。

### S08 — 单终态竞态：静态通过，有明确残余

- SmokeRunState.cs:18-33：TryRecordCheck 锁内先拒绝已终态/未知/重复；满足三检查时置 _completed=true、_error=null 并返回 success。
- SmokeRunState.cs:36-47：TryFail 锁内若 _completed 已 true 返回 null（成功不可被迟到失败覆盖），否则置终态。
- SmokeSession.cs:126-164：Complete 失败分支先 TryFail，返回 null 即 return（不重复写）；成功分支构造结果并 TryWriteResult；TryWriteResult :166-196 用 _resultWritten + lock 保证只写一次；成功/失败都走 ExitProcess。
- 残余：Complete 自身没有单一入口锁；并发路径靠 state 的锁与 _resultWritten 收敛，静态上成立，但没有单元测试/并发测试；退出码 1/3/6 的实际竞态未运行验证。

### S09 — 正常启动语义不变：静态通过，运行未验证

- 冒烟专用调用在正常模式均为 no-op：App.xaml.cs:28 Begin（无 --smoke → NotRequested）、:78 MarkXamlInitialized（_launch null 直接 return）、Log 目录默认值（SmokeProtocol.cs:216-221 与 SmokeSession.cs:41-45 默认 AppContext.BaseDirectory/data，与原路径一致）。
- 正常分支保留：App.xaml.cs:87-97 Config.Load、:104 activateExisting=true、:123-125 已有实例退出、:131-137 StartupService.RefreshIfEnabled、:150-151 StartActivationListener、:157-166 正常 Activate；MainWindow.xaml.cs:85-97 guard 为 false 时原初始化全执行；OnLoaded :112-173 原逻辑全执行；StartupService 的两个 guard 在正常模式均不触发；CaptureHistoryService 默认 StorageDirectory 在正常模式仍为 BaseDirectory/data/history。
- 未发现正常分支被改坏的静态证据；但未 build、未做正常启动/托盘/热键/启动项回归，不能声称 S09 运行通过。
- 细节证据：MainWindow.xaml 的 CaptureProfile/DefaultMode/Delay 等 ComboBox 无 SelectedIndex，ToggleSwitch 无 IsOn（grep 无 IsOn=True），InitializeComponent 不触发设置处理器；唯一 ctor 触发是 MainWindow.xaml.cs:80 SettingsNav.SelectedItem=HomeNavItem → :176-180 ShowSection → :183-199 仅 Visibility + ContentScroll.ChangeView，不写配置、不碰服务。处理器（MainWindow.xaml.cs:214-243；MainWindow.CaptureSettings.cs:22-35/174-192/221-234/236-264）均走 App.Config.Current + Save，冒烟静态不达。MainWindow.xaml 无 x:Bind（grep 无命中）。

### P-11 smoke v1 薄接口（仅兼容性，非 E01-E10 全审）

- 结果契约字段层：**兼容**。SmokeSession 写 8 字段 camelCase、success=true、error=null、PID/exe/root 与脚本校验一致（证据见 S06）；SmokeResult.ps1 严格字段集合与归一化路径比较能接受该结果。
- 调用入口层：**不兼容（阻断）**。scripts/smoke.ps1:16-27 定义 -ExePath/-TimeoutSeconds/-KeepRoot/-ProcessArguments；:40-49 建 temp/IshWY-smoke/<nonce>；:51-62 只设置 ISHWY_SMOKE_ROOT/NONCE，只把 -ProcessArguments 加入 ArgumentList，脚本自身从不追加 --smoke。scripts/gate.ps1:175-178 调用 pwsh -File scripts/smoke.ps1 -ExePath $outDir/IshWY.exe -KeepRoot，未传 --smoke，也未传 -ProcessArguments；scripts/publish.ps1:244-248 同样只传 -ExePath。结果：当前 v1 接线启动的是正常模式 App。
  - 若主人实例在运行：正常分支 activateExisting=true 会 Set 激活事件并 ExitProcess(0)（App.xaml.cs:104、:123-125），随后脚本因 result-missing 失败（不是假绿，但会激活用户实例，违反“不打扰用户实例”）。
  - 若无实例：正常窗口跑到 30s 超时被脚本 Kill，且可能写真实 data（非隔离根），属实际副作用。
- 结论：P-11 第 1 轮返工尚未完成，不能批准脚本最终版本；返工后必须独立复核（至少复核 --smoke 注入、正常模式永不被启动、E01/E02/E09 仍成立）。E01-E10 的其余实现本轮未审，不因本报告视为通过。
- 兼容性注意（低危）：dataRoot 语义是“smoke 根”而非“<root>/data”，两边实现一致但命名易误读，需在 P-11 文档/报告明确。App 看门狗 20s 早于脚本 30s，正常兼容，但慢机会先失败。

## 进行中

- 本报告已完成 S01-S09 静态逐条结论与 P-11 薄接口兼容性核对；无任何运行验证（未 build、未 App smoke、未 publish/打包、未 Actions、未 UIA/合成输入、未动主人实例 PID 4396、未 commit/push）。
- 待 lead 决策与后续返工（不在本工位写域）：产品侧 S07-1 修补、S03 fail-open 修补、P-11 --smoke 注入返工；以上均需修改 src/ 或 scripts/，本工位无权改动。
- 报告落盘路径：Record/reports/P10-P11-independent-review.md（本文件）。

## 风险与遗留

1. **S07-1 托盘自动创建（阻断，必须修）**：XAML TaskbarIcon 的 Loaded→ForceCreate 在冒烟中仍执行，违反 safe-gates:23；同时设置效率模式。最小修补建议（任选其一，需再静态复核）：
   - 首选：把 <tb:TaskbarIcon> 从 MainWindow.xaml 移出，改由 InitTray（MainWindow.Windows.cs:203-218）在 !SmokeSession.IsRequested 时用代码创建；保持正常模式外观/行为不变。
   - 次选：在 MainWindow 构造体 InitializeComponent 之后、Activate 之前，冒烟分支把 TrayIcon 从父容器移除（例如 ((Panel)TrayIcon.Parent).Children.Remove(TrayIcon)），确保 Loaded 不会到达该元素；需确认冒烟路径无其它 TrayIcon 引用。
   - 不应采用仅设置 Visibility=Collapsed 的方案（元素仍可能 Loaded，无法证明不创建）。
2. **S03 fail-open（应修）**：App.xaml.cs:106-111 在冒烟下应改为持久失败（如 SmokeSession.Fail("single-instance-check-failed")），不得沿用正常模式的 isFirstInstance=true。
3. **P-11 --smoke 缺失（阻断脚本使用）**：smoke.ps1 必须无条件追加 --smoke（并校验调用方不得覆盖为正常模式）；gate.ps1:175-178、publish.ps1:244-248 的调用需同步；返工后独立复核。
4. **S02 残余**：Parse 的目录枚举异常未捕获，可能以未处理异常结束（非0但非契约 2）；建议把 :82-89 包进 try/catch 返回 Invalid("root-unreadable")。测试缺口与 P10:56 的失实覆盖声明须更正或补测。
5. **S05/S06 残余**：20s 看门狗对慢机偏紧；TryRecordCheck 依赖到达顺序（当前安全，未来易碎）；建议显式记录为非阻断项。
6. **窗口级 KeyInterceptor**：非全局钩子，但确实在冒烟构造期生效；需 lead 明确是否归入“钩子必须跳过”的范畴；若要求严格，可把其构造移入 !IsRequested。
7. **未验证项**：真实 smoke 结果文件、退出码、托盘不存在、窗口可见性、正常启动回归、脚本负例；不得以静态结论替代运行验收。
8. **P-11 未批准**：当前脚本不能用于受控 smoke；E01-E10 未全审；P10 报告 :56 的覆盖声明不实，需在 P-11 返工报告中更正。

## 待确认

1. lead 是否接受 S07-1 的“移出 XAML / 代码创建”方案，还是采用“冒烟分支移除 TrayIcon”方案；修补后是否由本工位或另一独立工位复核差异。
2. lead 是否要求把 KeyInterceptor（窗口级子类化）也归入“钩子”并强制跳过。
3. 受控运行前置条件：是否要求主人实例 4396 由主人自行关闭（代理不得结束）；是否要求运行前/后 SideEffectSnapshot + 隔离根证据；是否只允许在 S07-1、S03、P-11 --smoke 三项修补并复核后执行。
4. 本次静态审查是否可作为“lead 受控运行”的放行依据：**结论是否定的**——当前不允许；修补并复核后，才可讨论受控运行，且不等同整体验收。
5. P-11 返工责任与时间点（task-51 并行中），以及返工后是否要求本工位再次只读复核薄接口/E01-E02。

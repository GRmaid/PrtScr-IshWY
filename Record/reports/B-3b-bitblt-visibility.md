# B-3b · BitBlt 失败可见性（task-26）

状态：代码实现完成，三条门禁全绿；已用临时反射探针跑通「所有引擎失败」分支并核对历史目录；真实 BitBlt 失败注入与 GUI 热键未执行（受主人实测纪律限制）。本报告落盘后收单 task-26。

## 一、做了什么

只改三个文件，均在 task-26 写域内：

1. src/PrtScr.Capture/ScreenCapturer.cs
   - BitBlt 不再丢弃返回值：失败时抛 Win32Exception(Marshal.GetLastWin32Error())，消息含「BitBlt」字样与宽高、坐标、includeLayered（:59-64）。
   - 修正既有缺陷：includeLayered 参数原先被忽略，BitBlt 永远带 CAPTUREBLT；现在按参数选择 ROP_SRCCOPY|ROP_CAPTUREBLT 或 ROP_SRCCOPY（:54-56）。这使 CaptureService 的第二次「普通 BitBlt」回退真正成立。
   - 失败路径仍由原 catch 先 bitmap.Dispose() 再 rethrow（:71-75），不返回未初始化位图。

2. src/PrtScr.App/Services/CaptureService.cs
   - CaptureMonitorByBitBlt 改为返回 Bitmap?，内部 try/catch：Win32Exception 记错误码与消息后返回 null（:581-585），其它异常记类型与消息后返回 null（:586-590）。CaptureVirtualScreen 的完整截图对象仍在 finally Dispose（:576-579）。
   - CaptureMonitorBestAsync 的 BitBlt 段重写（:471-527）：fallback 可为 null；只有非黑才作为成功返回；CAPTUREBLT 黑屏时释放后重试普通 BitBlt；仍失败再试前景窗口 WGC；全黑/全失败统一记「所有采集引擎均失败或返回疑似黑屏」并返回 (null, true)（:520-521），不再把全黑 fallback 当成功。
   - CaptureFullScreenPngAsync 在 bitmap 为 null 时记「QuickCapture: 无可用帧 black=...」并返回 QuickCaptureResult(null, likelyBlack)（:609-613），让上层走失败提示而不是编码黑图。
   - 未改 StartCaptureAsync 的会话状态机、EndCaptureSession 漏斗、既有五条日志关键字。

3. src/PrtScr.App/MainWindow.Capture.cs
   - 只改一行失败文案：:51 由「全屏截图失败」改为「采集失败，请重试」。
   - 该文件逻辑零改动；:65 ClipboardService.SetImageAsync 与 :66 CaptureHistoryService.AddAsync 仍只在失败 return 之后执行。

范围声明：未改 src/PrtScr.Capture/WgcWindowCapture.cs、未改 csproj/Directory.Build.props、未新增依赖、未 git commit/push、未跑 --smoke、未启动 GUI 或合成热键。

## 二、证据

### 2.1 失败路径 → 用户可见结果

| 失败路径 | 取景器（F1） | 保存 | 历史 | 剪贴板 | 日志 |
|---|---|---|---|---|---|
| BitBlt 返回 0（ScreenCapturer.cs:59-64） | 不打开取景器（异常向上） | 无保存入口 | 无 | 无 | CaptureMonitorByBitBlt 捕获 Win32Exception，记 Capture: BitBlt 失败 error=0x... includeLayered=...: ...（CaptureService.cs:581-585） |
| BitBlt 其它异常（如裁剪越界） | 不打开取景器 | 无保存入口 | 无 | 无 | Capture: BitBlt 异常 类型: 消息（CaptureService.cs:586-590） |
| BitBlt 成功但整帧疑似黑屏 | 不打开取景器 | 无保存入口 | 无 | 无 | Capture: CAPTUREBLT 黑屏，尝试普通 BitBlt → 仍黑则继续；最终 Capture: 所有采集引擎均失败或返回疑似黑屏（:476-484、:520） |
| 所有引擎都失败或全黑 | CaptureMonitorBestAsync 返回 (null,true) | 无保存入口 | 无 | 无 | Capture: 所有采集引擎均失败或返回疑似黑屏（:520）；StartCaptureAsync 另记 Capture: 所有采集引擎均失败（:386） |
| 一键全屏仍无帧 | 不涉及 | 不保存 | 无 | 无 | QuickCapture: 无可用帧 black=True（:611）；MainWindow 记 QuickCapture: 失败，无图像（:52） |
| 一键全屏两次都无帧 | 不涉及 | 不保存 | captureHistory 不执行 | Clipboard 不执行 | MainWindow.Capture.cs:49-55：SetStatus 新文案 + Toast + return，:65/:66 在 return 之后 |

结论：黑图或全失败时不会生成 PNG、不会打开取景器、不会进入历史或剪贴板；唯一用户可见结果是状态栏「采集失败，请重试」+ 失败 Toast（一键全屏路径）。

### 2.2 代码级释放与唯一性

- ScreenCapturer 失败时 bitmap 在 catch 内 Dispose（:71-75），ReleaseDC 在外层 finally（:79-82），不泄漏 GDI 资源。
- CaptureMonitorByBitBlt 的 full 在 finally Dispose（:576-579）；返回 null 时无 bitmap 交付。
- CaptureMonitorBestAsync 的 fallback 由 fallbackOwned 管理：成功交出前置 false，否则 finally Dispose（:473、:489-490、:523-527）；全黑重试路径先 Dispose 旧图再取新图（:480-483）。
- 全仓 CaptureVirtualScreen 调用点只有两处：src/PrtScr.App/Services/CaptureService.cs:569（新逻辑捕获并记日志）与 :749（WarmUpNative，外层 catch 忽略）。新抛的 Win32Exception 不会逃到用户路径。

### 2.3 运行时探针：所有引擎失败分支

临时探针（.tmp/probe-b3b，未入库）在 .tmp/out-b3b（B-3b 构建输出，IshWY.dll 时间 2026-10-30T22:12:39）内反射调用 private static CaptureMonitorBestAsync(HMONITOR=-1, WindowRect(0,0,0,0))，结果：

    BASE=E:\Explore\GoodGood\PrtScr\.tmp\out-b3b\
    RESULT item1_null=True item2=True elapsed_ms=555

同次运行写出的日志（.tmp/out-b3b/data/logs/app.log）：

    2026-10-30 22:17:12.452 Capture: BitBlt 异常 ArgumentException: Rectangle '{X=0,Y=0,Width=0,Height=0}' cannot have a width or height equal to 0.
    2026-10-30 22:17:12.463 Capture: 所有采集引擎均失败或返回疑似黑屏

说明：本次探针走的是「无效显示器 + 零矩形」路径，触发的是 Capture: BitBlt 异常（裁剪抛 ArgumentException），不是 BitBlt 返回 0 的 Win32Exception 分支；后者仍只有代码级证据。探针后 .tmp/out-b3b/data/history 不存在，没有生成任何历史文件。

### 2.4 历史目录前后对比

真实运行目录：src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/data/history

| 时点 | 文件数 | 最新 mtime | 最新文件 |
|---|---:|---|---|
| 探针前 | 240 | 2026-10-30T21:39:13.9872296+08:00 | 20261030-213913-984-normal-3184c2.png.json |
| 探针后 | 240 | 2026-10-30T21:39:13.9872296+08:00 | 20261030-213913-984-normal-3184c2.png.json |

结论：探针的失败路径没有写入真实历史目录。该对比是「失败分支不写历史」的辅助证据；主证据仍是 2.1 的代码控制流：MainWindow 的历史/剪贴板写入位于 :65/:66，失败 return 在 :49-55。

### 2.5 BitBlt 错误码探针

用 PowerShell Add-Type 直接调用 gdi32!BitBlt(null, null)：

    BitBlt(null,null) ok=False lastError=6 hex=0x00000006

因此 Win32Exception 日志的实际格式样例（错误码 6 来自探针，宽高/坐标为格式示例）为：

    Capture: BitBlt 失败 error=0x00000006 (6) includeLayered=True: BitBlt 失败（2560×1600 @ 0,0，includeLayered=True）

标注：该行是按代码插值构造的格式样例，未在真实 BitBlt 失败中触发；真实触发需在显示模式切换/安全桌面/RDP 等场景复现。

### 2.6 门禁

| 门禁 | 命令 | 结果 |
|---|---|---|
| 单项目编译 | dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo | 0 警告 0 错误，2.09s |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag b3b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-b3b/" | 0 警告 0 错误，16.62s，BUILD-GUARD: EXIT=0 |
| 测试 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag b3btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | 通过 206 / 失败 0 / 跳过 0，82ms，BUILD-GUARD: EXIT=0 |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | rc=0 |

### 2.7 P-14b 不变量复核

    Select-String src/PrtScr.App/Services/CaptureService.cs -Pattern '_isCapturing ='
    104:_isCapturing = false;
    402:_isCapturing = true;

说明：新增 using System.ComponentModel 后，P-14b 的 false 赋值从 :103 下移到 :104，全仓仍只有这一处 false 赋值；EndCaptureSession 唯一性未动：

    CaptureService.cs:98   internal void EndCaptureSession(Guid sessionId, string reason)
    CaptureOverlayWindow.Capture.cs:236   CaptureService.Instance.EndCaptureSession(session, reason);
    CaptureService.cs:417   EndCaptureSession(CurrentCaptureSessionId, "start-failed");

MainWindow.Capture.cs 的写入点：

    65: var copyTask = Services.ClipboardService.SetImageAsync(result.Png);
    66: var historyTask = await Services.CaptureHistoryService.Instance.AddAsync(

均在失败 return（:49-55）之后，未因 B-3b 提前。

### 2.8 grep -n BitBlt 输出（验收第 4 条）

    Select-String src/PrtScr.Capture/ScreenCapturer.cs -Pattern 'BitBlt'
    49:                // （InvalidEnumArgumentException，这正是上一版 F1 崩溃的元凶），必须原生 BitBlt。
    57:                    var succeeded = PInvoke.BitBlt(
    63:                            $"BitBlt 失败（{width}×{height} @ {x},{y}，includeLayered={includeLayered}）");

失败分支在 :57-64：:57 调 PInvoke.BitBlt，:59-64 判返回值并抛 Win32Exception，:63 的消息含「BitBlt 失败」。

## 三、进行中（交接用）

- 已完成：三文件代码、单项目编译、整包门禁、206 条测试、format、运行时全失败探针、历史目录前后对比、本报告。
- 未完成：lead 验收；主人实测（真实热键与真实 BitBlt 失败场景）。
- 下一步第一件事（若本单因验收失败被 reopen）：读本报告 2.3 与 2.5，然后在 src/PrtScr.App/Services/CaptureService.cs:564-591 复核 CaptureMonitorByBitBlt 的 catch 与日志，在 src/PrtScr.Capture/ScreenCapturer.cs:54-64 复核 BitBlt 抛错；若日志没有出现，先确认运行的是 .tmp/out-b3b 或最新 App 构建，而不是旧的 bin/x64/Debug 输出。
- 下一步该读的文件：src/PrtScr.App/MainWindow.Capture.cs:49-66（失败与写入顺序）、src/PrtScr.App/Services/CaptureService.cs:471-527（Best 失败链）。
- 临时物：.tmp/probe-b3b、.tmp/out-b3b 均为 .tmp 下构建/探针产物，不入 git；可在实测后清理。

## 四、风险与遗留

1. 真实 BitBlt 失败（返回 0）未在运行时注入。当前 Win32Exception 分支只有代码审查 + Add-Type 错误码探针；实际触发条件（安全桌面、显示模式切换、RDP、驱动限制）需主人在真机验证。探针覆盖的是「裁剪异常 → 所有引擎失败」路径，不等于 BitBlt 返回 0。
2. 一键全屏的「全黑」语义发生变化：Best 不再把全黑图当成功返回，因此 MainWindow.Capture.cs:57-63 的「当前画面像是独占全屏游戏」分支对当前实现不可达；全黑与全失败统一显示「采集失败，请重试」。这是 B-3b 的直接后果，需 lead 确认文案是否接受。
3. F1 取景路径失败时仍只有日志（Capture: 所有采集引擎均失败），没有 Toast/状态栏提示。取景器不打开，用户可能误以为热键没响应。是否补用户提示需另立单，避免扩大 task-26 写域。
4. CaptureMonitorByBitBlt 的 catch (Exception) 会把编程错误也折叠成 null，只保留类型与消息日志。当前目的是失败可见性，但长期看应把非预期异常区分出来（可留给后续可观测性单）。
5. MainWindow.Capture.cs 只改了一行文案；该文件其余逻辑未动，符合 lead 的「只准两行失败文案」约束。:59 的独占全屏文案保留未改，但如上所述当前不可达。
6. 仓库工作树包含 P-14b/P-13b 等他人并行改动，git diff --stat 会显示 CaptureService.cs 大改动；B-3b 的实际新增集中在 :471-527、:581-590、:609-613 与 ScreenCapturer.cs:54-75。提交时请按写域拆分，不要把他人改动混入本单说明。

## 五、待确认

1. 全黑统一为「采集失败，请重试」是否可接受？若必须保留「独占全屏游戏」提示，需要把 LikelyBlack 从 Best 层继续上报，不能只靠 Png null。
2. F1 取景失败是否要加用户提示（Toast/状态栏）？若加，属于 App 层新行为，需扩写域或另派单。
3. 本次探针使用反射调用 App 私有方法，未启动 GUI、未按热键；该证据形式是否满足本单验收？
4. includeLayered 修复超出原三条口径但同属 ScreenCapturer.cs 写域，是否随本单提交？它使「普通 BitBlt」回退从无效变为有效，建议保留。
5. 真实 BitBlt 失败场景由主人哪台机器/哪种显示配置复现？若无法稳定复现，本单以代码级 + 探针证据收口是否可接受？

## 主人实测步骤

1. 正常 F1：在桌面按 F1，选区确认后保存；预期取景器正常出现，历史目录新增 1 组 png/json，剪贴板有图。
2. 正常一键全屏：在普通桌面按全屏截图热键；预期剪贴板有图、历史新增 1 组，状态栏为成功文案。
3. 全黑/全失败场景：打开独占全屏游戏或切到安全桌面/RDP 后按全屏热键；预期状态栏「采集失败，请重试」+ 失败 Toast，历史与剪贴板均无新增；随后查看 data/logs/app.log 是否有 Capture: 所有采集引擎均失败或返回疑似黑屏 与 QuickCapture: 无可用帧。
4. 连续失败后恢复：回到普通桌面再按一次全屏热键；预期恢复正常截图，历史只新增成功的一次，之前的失败没有留下黑图。
5. 若能在显示模式切换/远程桌面复现 BitBlt 失败：查看日志是否出现 Capture: BitBlt 失败 error=0x...；同时确认历史目录数量不变、剪贴板未被覆盖。
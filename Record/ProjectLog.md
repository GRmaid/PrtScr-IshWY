# ProjectLog · IshWY

只追加阶段边界；一条 ≤5 行；超过 200 行时把最旧一段移入 `Record/archive/`。

## 2026-10-28

- 21:35 接手：仓库 `E:\Explore\GoodGood\PrtScr`，HEAD `0b1ca41`=`v1.3.0-2-g0b1ca41`，代码与 v1.3.0 标签一致；来源 `docs/HANDOFF-2026-10-28.md`。
- 21:40 基线：compile 0 错 0 警，test 77/77 过，format 退出 0；整包 build 被运行中的 `IshWY.exe`(PID 29836) 占用输出目录阻塞，非代码问题 → 待主人关掉实例后复跑整包。
- 21:45 读数：读 review.docx 55 项（A15/B14/C8/D7/E9/F2，高 11 / 中 40 / 低 4）、摘要 md 与 UI 建议校正版（P0 6 项 + P1 + P2）；评审为 Linux 静态阅读，未在 Windows 复现。
- 21:50 编制：拟 maid-impl / maid-check 起步，报主人待点头；写域见 `Record/plans/2026-10-28-整改总纲.md` §7。
- 21:55 待确认：6 项产品语义（B01 出口 / C04 配额 / D01 Umi / 编辑是否持久 / UI 与骨架谁先 / 版本切分）→ 见总纲 §5。

## 2026-10-28（续）

- 21:47 主人批准：按总纲开工，五项目语义按建议执行（B01 取当前合成 / C04 只影响后续 / D01 关闭即不连 / 编辑另存新历史项 / 三次独立发版）。
- 21:48 编制：maid-impl（实现 P-01/P-04/P-02/P-03/P-06）+ maid-check（先 A-2 前置确认，后 P-12 核查），并发 2 / 上限 3，总编制 2 / 上限 5。
- 21:48 派单：task-1 P-01、task-5 P-04、task-2 P-02、task-4 P-03（依赖 task-2）、task-3 P-06、task-7 A-2 前置确认、task-6 P-12（依赖前五张）。
- 21:49 环境解法：整包 build 改用 `-p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out/` 绕开运行实例占用，实测 0 错 0 警；门禁命令已写入每张单。

## 2026-10-28（续 2）

- 21:58 验收：task-7 A-2 前置确认通过（A03/A04/A10 均仍成立，含行号证据与最小改法）→ 结论写入总纲 §11。
- 21:58 拍板：A03 2000ms 超时 + 单飞、晚到帧释放；A04 走 interop CreateForWindow 保留 19041；A10 拆 P-09a（Capture 库）与 P-09b（CaptureService 两处，等 P-04）。
- 21:59 编制：加 maid-capture（P-07/P-08/P-09a），并发 3 / 上限 3，编制 3 / 上限 5。
- 21:59 派单：task-8 P-07、task-9 P-08、task-10 P-09a（依赖 task-8/9）。
- 22:05 环境：主人退出 `IshWY.exe`，`Get-Process IshWY` 确认为 NOT RUNNING，默认 bin 解锁；整包 build 与 smoke 恢复可跑，但当前工作树有 P-01/P-07 在改，暂不抢跑。

## 2026-10-28（续 3 · 收工）

- 23:05 交付：task-1 P-01（B01/B02/B03，贴图编辑出口统一）完成——新增 `NativePinWindow.Export.cs` 作为唯一「当前画面」出口，复制/OCR/裁剪/另存/管理器全部改走它，取不到就提示且绝不回退旧图；`EnterEditMode` 幂等；合成失败保留编辑内容并提示。报告 `Record/reports/P-01-pin-edit-export.md`。
- 23:10 交付：task-8 P-07（A03，PrintWindow 期限）完成——2000ms 受控期限、晚到帧一次性释放、按 HWND 单飞、`WindowCaptureResult` 可判别失败原因；给 P-04 的契约是改用 `CaptureDetailedAsync`。报告 `Record/reports/P-07-printwindow-timeout.md`。
- 23:25 门禁更正：`-t:Compile` 是假绿（不跑 WinUI XAML 代码生成，冷态 624 CS0103），编译门禁改为整包 `Build` + `-p:OutDir=.tmp/out-verify/`；lead 复验 0 错 0 警 12.86s、Core.Tests 77/77、format rc=0。基线报告与总纲 §8 已更正。
- 23:30 收工（主人 m00208）：停止派单，三名女仆 idle；task-2/3/4/5/6、task-9/10 保持 pending；工作树未提交（HEAD 仍 `0b1ca41`）。交接 `Record/reports/2026-10-28-session-handoff.md`。

## 2026-10-30

- 20:25 诊断：主人 m00346 的三条贴图症状归因于运行中的 10-26 旧构建（bin 下 IshWY.dll 939520 字节、缺 PinExportResult 与「贴图正在标注」），HEAD 86d3541 代码侧无成因；另产出 .tmp/run-p01/（0 错 0 警 29.41s）备用。
- 20:34 更正：上一节「工作树未提交、HEAD 仍 0b1ca41」已过时——P-01/P-07 改动、Record/ 与 HANDOFF 文档（含 docs/HANDOFF.md 8 行修改）已本地提交 86d3541，尚未推送。
- 20:36 环境：主人退出旧实例（Get-Process IshWY = NOT RUNNING）后原地整包重建默认 bin → 0 错 0 警 17.63s；IshWY.dll 945152 字节、IshWY.exe 455168 字节，均为 2026-10-30T12:25:41Z；data/ 保留 226 文件 122,922,279 字节（history、logs）。
- 20:37 待验收：请主人用默认 bin 复测四条（标注中 Ctrl+C 得带标注图 / 裁剪选区不含工具栏 / 裁剪不拉伸 / 管理器复制不拿旧图）；通过后推送 86d3541 并派 P-04/P-08/P-09a。

- 20:44 验收：主人 m00464「都没问题」——P-01 四条实测全过（标注中 Ctrl+C 带标注、裁剪不含工具栏、不拉伸、管理器复制拿新图）。
- 20:45 推送：main 0b1ca41 → 9c81858（86d3541 P-01/P-07 + 9c81858 记录提交），与 origin/main 一致，工作树只剩 .tmp/ 与主人材料。
- 20:47 派单：task-5 P-04 → maid-impl（补入 P-07 契约：CaptureDetailedAsync/WindowCaptureResult，Busy 不静默降级），task-9 P-08 → maid-capture；并发 2 / 上限 3，maid-check 待命做独立核查。新增共享构建锁 .tmp/build-guard.ps1（两名女仆并行构建时串行化，避免 obj 抢写）。

- 20:38 验收：task-9 P-08 通过（maid-capture 自报 + lead 抽查：CreateForWindow :591 已用、TryCreateFromWindowId 仅存注释、失败枚举与 DetailedAsync 到位）；门禁数字待统一复跑。
- 20:40 复核：task-11 P-08 独立复核（maid-check）7 条全过、0 返工；提出新 API 无消费者、item 与晚到帧泄漏、19041 无实机三项。
- 20:43 派单：task-10 P-09a → maid-capture（描述追加 item RCW 与晚到首帧两处确定泄漏点）；task-12 P-07b 建立（WindowCapture 接线 WGC 详细结果，blocked_by task-10）。
- 20:45 派单：task-2 P-02 → maid-check（并发 3/3）；拍板写入总纲 §11 续（P-07b、19041 转待验证、门禁统一复跑口径、P-02 双轨裁决）。

- 20:58 验收：task-5 P-04 通过抽查（session 闸门 6 处 await 后写入、来源取 result.Source、Busy/Canceled/Timeout 分类、CaptureService 提供 CurrentCaptureSessionId）；最终门禁待统一复跑。
- 21:00 派单：task-3 P-06（A01 钩子回调去阻塞）继续由 maid-impl 做（她已 claim）；P-02 明确归 maid-check，不交叉。

- 21:05 验收：task-10 P-09a 通过抽查（ReleaseCaptureItem :287-300、CaptureCoreAsync pool finally :749、晚到帧 ContinueWith :729-737、DXGI ReleaseComObject :157/:380-384）；门禁自报 0/0 14.67s、77/77、format 0。
- 21:06 派单：task-12 P-07b（WindowCapture.cs 接 CaptureWindowDetailedAsync + Canceled 归一）→ maid-capture；新建 task-13 P-09b（CaptureService 两处释放）待派。

- 21:20 验收：task-3 P-06 通过抽查（RaiseDeferred :275-292/:492/:507/:518、App.Log 钩子线程转 LogDeferred :243-251、Hotkeys 日志进 UI 队列 :59-63）；DispatchOverlayKey 保留同步返回的理由成立。
- 21:21 派单：task-13 P-09b（CaptureService 两处释放）→ maid-impl；门禁同前。

- 21:35 验收：task-2 P-02 通过抽查（配额变更零删除 MainWindow.xaml.cs:302-307、PrunePlan 预演 + ConfirmDialog 确认、PruneNow 返回删除数）；拍板：保存时只修剪本分类（不做全局静默修剪）、Config 夹取保留、预演不下沉 Core。
- 21:36 派单：task-4 P-03（D01 Umi 闸门）→ maid-check；P-12 待本单落定后解锁。

- 21:52 验收：task-12 P-07b 通过（WindowCapture.cs 466 行、Wgc* 三字段、Canceled 归一）；裁决「粗粒度 Failure 不变、WGC 原因入 Wgc* 字段」，不改 App 的 ToAbort。
- 21:53 验收：task-13 P-09b 通过（frozenHandedOff 交接 + finally 释放、会话身份三件套、末尾 CRLF），整解 format 转绿。
- 21:55 派单：task-14 A 批次 App 层独立复核 → maid-capture；task-15 A 批次采集层独立复核 → maid-impl（均只读代码、只写 Record/reports）。

- 22:06 裁决：P-07b 验收 #1 按实现方取舍——WindowCaptureFailure 维持粗粒度，WGC 真实原因入 Wgc* 字段，不改 P-04 的 ToAbort；task-12 立即收单。纪律：收单前须先获 lead 验收，不得先 complete 再 reopen。

- 22:24 验收：task-4 P-03 通过（OcrService 关闭态不构造 Umi :15、probeUmi 只在点击时为 true :36/53/71-76、文案明示不连接、端口夹取 1-65535）。裁定：启用态也不自动探测；一次点击=一次 ProbeAsync（内部最多 3 GET 属既有实现）；不加 PrtScr.Ocr 项目引用。
- 22:26 派单：task-6 P-12（回归测试 + 十单裁决）→ maid-check，描述已扩写（只测可达纯逻辑、必测 Core 默认值/夹取、十条行为检查表、可放行/返工结论）。

- 22:44 复核：task-15 采集层独立复核结论「可放行、0 阻断」，1 项真实遗漏（WgcWindowCapture.cs:150-171 graphicsDeviceAbi 异常出口未 Release）、2 项需裁决、1 项文档补注、1 项清单外（共用 D3D11 immediate context 跨线程）。
- 22:46 裁决：①设备链遗漏修 → 新 task-16 P-09c（maid-impl）；②EnsureMonitorPool 并发覆盖本轮不改，记「已知并发前提（当前不可达）」，归 B 批次资源生命周期；③入口 token 预判修 → 新 task-17 P-07c（maid-capture，含矩阵行 4/6 补注）；④共用 context 记 B 批次评估项。

- 23:05 复核：task-14 App 层结论——会话闸门通过、双重释放通过、钩子语义通过、Wgc* 未接线非缺陷；但判 1 项阻断：取景中再按 F1/Esc 致 _isCapturing 永久为真、Esc 热键残留（A03 遗留未闭环，P-04 只做晚到丢弃）。
- 23:07 派单：新建 task-18 P-04b（唯一收尾漏斗 + ConfirmAsync 局部 owned 标志 + Dismiss 不早退 + 补 session 日志）→ 待 maid-impl 完成 task-16 后接；P-12 行为检查表追加该场景。
- [时间戳为估算] 事故：误用 tools.write 传空字符串占位，把 Record/plans/2026-10-28-整改总纲.md（176 行）截断为空；用 git show HEAD 版打底 + 补回 §11 续两节恢复为 197 行 / 14 个二级节。教训：不用空内容占位，长文本追加用全文重写。
- [时间戳为估算] 校准：P-04b 日志关键字定稿并发 maid-check，P-12 报告 G1 行（2.4 表 :159 + 待确认 #2）改为可判别判据。
- [时间戳为估算] 派单：task-19 B-1 设计（maid-check）进行中；task-18 P-04b 代码改完、构建绿，Core.Tests/format 与报告在跑。

- 21:03 验收：task-18 P-04b 通过（grep 证明 _isCapturing 唯一清零 :80、FinishCaptureSession/Dismiss 全部带 session+reason、无裸 Dismiss()），lead 在冻结树复跑三门禁——build 0 错 0 警 14.38s（OutDir .tmp/out-a-final）、Core.Tests 89/89 87ms、format rc=0 → A 批次放行。
- 21:05 提交：efd4028（18 文件 +1473/-304 + 16 份报告），已推送 origin/main（9c81858..efd4028）。工作树仅 .tmp/ 与 大佬修改材料/ 未跟踪。
- 21:07 环境：默认 bin/x64/Debug 原地重建失败（8 错误 / 40 警告），因主人 20:27 启动的 IshWY.exe（PID 48348）锁住 bin 下 dll；不改默认 bin，改为把 efd4028 的可用构建放 .tmp/out-a-final/（主人关掉实例后再原地重建，或直接跑该目录 IshWY.exe）。

- 21:07 更正：本日 22:00 之后的若干条目时间戳由代理估算（写日志时未取系统时钟），真实整改工作时段为 20:19–21:07；条目内容与先后顺序有效，时间以本行与 21:03/21:05/21:07 三条为准。今后写时间戳前先执行 date。
- 21:12 设计：task-19 B-1 交付 `Record/plans/B1-capture-session-design.md`（五节齐备，14 个状态变量写/读/线程表、CaptureResult 所有权、8 状态机 8 条不变量、迁移三步判据、12 行结果矩阵、8 项未决）；四项待拍板（session 不持有 overlay、快捷全屏另立 P-14b、历史元数据另立单、是否新建 Capture.Tests）。
- 21:24 环境：主人关闭运行实例（`Get-Process IshWY` = NOT RUNNING）→ 默认 bin 阻塞解除。
- 21:26 环境：`dotnet build PrtScr.sln -c Debug` 原地重建成功——0 警告 0 错误 25.21s；`src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.dll` 970752 字节、`IshWY.exe` 455168，UTC 2026-10-30T13:25:54Z；`data/` 保留 232 文件 126,109,645 字节（config.json 1679 字节 mtime 20:27、history、logs）；`strings -el IshWY.dll` 命中 4 处 P-04b 日志标记（already capturing, cancel session= / finished reason= / finish ignored）⇒ 构建含 `efd4028` 全部改动。
- 21:26 待验收：交主人按 7 条清单实测 `efd4028`（重点：取景中连按两次 F1 仍能截图、Esc 后再按 F1 不被吞）；通过后拍板 B 批次四项（session 不持有 overlay / 快捷全屏另立 P-14b / 历史元数据另立单 / 是否新建 Capture.Tests）。
- 21:44 验收：主人回「全过」——七条实测（标注 Ctrl+C、裁剪不含工具栏不拉伸、管理器复制、取景中连按两次 F1、Esc 后 F1、卡死窗口约 2s、配额不删旧图）全部通过 ⇒ A 批次放行确认。
- 21:44 发布准备：v1.3.1 版本号——`src/PrtScr.App/PrtScr.App.csproj` `<Version>` 1.3.0→1.3.1；`README.md` 三处（当前最新版本 v1.3.1 + `IshWY-1.3.1-win-x64.zip` 下载名、打包示例命令、产物目录名）。README 第 8 行主人致谢 v1.3.0 的原文未动。
- 21:46 门禁：整包 build 0 错 0 警 30.42s（OutDir .tmp/out-v131）、Core.Tests 89/89 84ms、format rc=0。
- 21:48 打包：`pwsh scripts/publish.ps1 -Version 1.3.1 -SkipSmoke` 退出 0——`artifacts/IshWY-1.3.1-win-x64/` 131 文件 77,817,294 字节（74.2 MB），`IshWY.pri` 2,231,656 字节 + 9 个 .xbf，PDB 0、winmd 0、`Microsoft.UI.Xaml.Phone.dll` 在；zip `artifacts/IshWY-1.3.1-win-x64.zip` 31,161,751 字节 sha256 `9a1ce092797dedea26354a5129fef213e18bd9d16b5171194e2c644aeefd2445`。trim 分析警告 IL2026/IL2104 为既有（JSON 反射与第三方库），不阻断。
- 21:49 冒烟：解压到 `.tmp/smoke-131/` 跑 `--smoke` 退出 0，但日志为「已有实例，已请求其显示设置窗，本进程退出」——主人实例（PID 24184）仍在运行，属假成功，不计入放行；须主人关闭实例后重跑，验收主窗创建与五热键 register=0x1。
- 21:50 验收（冒烟重跑）：主人关闭实例后解压 `.tmp/smoke-131b/` 跑 `--smoke` 退出 0；日志有 `MainWindow: ctor start` → `window created` → `window activated`、五条热键 `register=0x1`（screenshot/fullscreen/pin/ocr/mode）、`OnLaunched: smoke exiting`；进程退出后无残留 ⇒ 启动门禁通过。
- 21:58 验收 task-20（P-14a）：lead 独立复跑 build 0/0 16.44s（out-p14a-verify）、Core.Tests 206/206、format rc=0；交付 Core 5 文件（CaptureSessionState/CaptureScenario/CaptureResult + 2 个测试文件 117 条用例），矩阵 12/12。裁三项：CaptureResult 走方案 B、Origin 用 Window/Normal/Screen、矩阵第 12 行断言层拆 OCR/另存。
- 21:59 验收 task-21（P-13a）：`Record/plans/B1-editdocument-design.md` 389 行五节齐（现状逐变量带文件:行、D1–D9、类型草案与 8 条不变量、迁移三步、未决 6 条）。修正前提：HEAD 无 `_undoStack`，撤销实为 `_annotations.RemoveAt`（EditPaint.cs:151-156）。裁四项：关窗确认单独立项 P-13b、撤销粒度操作级+清空可撤销+提交不可撤销、重入序号维持从 1、步骤 2 行为变化放行。
- 22:06 收单 task-25（B-3a，maid-capture rev3）：WgcWindowCapture.cs +83/-32（914 行全 CRLF）——EnsureMonitorPool 锁内二次检查 :409-431 + 冲突 finally 逆序释放 :438-447；_contextLock 叶子锁 :346 包住 CopyResource→Map→像素拷贝→Unmap :851-878，Bitmap/LockBits 在锁外。四裁：CRLF 归写域所有者、合并树门禁 lead 统一复跑、冲突日志 log-only 不升计数、device 方法不加锁。
- 22:08 收单 task-23（P-14b，maid-impl rev3）：CaptureService.cs +236——EndCaptureSession 成唯一漏斗（_isCapturing=false 全仓仅 :103），新增 BeginCaptureSession/MarkCapturePrepared/BeginCaptureAttempt/MarkCaptureAttemptSucceeded/MarkCaptureAttemptFailed/ResetCaptureAttempt :157/:187/:193/:199/:230/:250，迁移全走 CaptureRules.CanTransition；CaptureOverlayWindow.Capture.cs +111 改为产出 CaptureResult（Origin/Engine/Failure/PrintWindowTimedOut），五条既有日志关键字未改。风险：GUI 未实测、result 日志 HResult 固定 0。
- 22:11 收单 task-24（P-13b，maid-check rev3）：新增 src/PrtScr.App/Views/NativePinWindow.CloseGuard.cs（WM_CLOSE/菜单/命令三入口收敛，编辑中有未应用标注先确认：是=销毁记 close discarded，否/Esc=留编辑态记 close canceled）与 NativePinWindow.EditHistory.cs（操作级撤销栈：新增/清空各一步、清空可撤销、撤销后序号复用现存最大值、离开/重入清栈）；DestroyWindow 贴图窗仅剩 CloseGuard.cs:28/:65。门禁 build 0/0 16.02s、206/206、format rc=0。裁：编辑态零标注直关可接受（无可丢数据）。遗留：EditDocument 类型抽取（EditRevision/EditNumbering/EditDocument/EditCommit）另立 P-13c。
- 22:12 派单：task-26 B-3b → maid-capture（CaptureService.cs 刚被 P-14b 大改，要求保住 _isCapturing=false 唯一赋值与五条日志关键字）；task-27 P-14b 独立复核 → maid-impl（自写自判禁止）；task-28 P-14c 设计 → maid-check。并发 3/3。
- 22:14 复核 task-27（P-14b 独立复核，maid-impl）：五条保护日志 git show HEAD 逐字 diff 全 IDENTICAL；_isCapturing false 仅 :103、true 仅 :401；EndCaptureSession 二次调用 :107-111 早退；用户可见面与 HEAD 零差异。裁：F4（一条非保护日志文本 本次来源=Window）可接受不兼容；F2（未来传 token 时 Canceled 会落 Failed）当前不可达记已知；**F1 判真缺陷**——旧会话晚到 Finish 会清掉新会话 _isCapturing 并注销其 Esc（HEAD 亦存在），立 task-29（stale 判定在 lock 内、不清标志、不 EndCaptureMode、Guid.Empty 除外），blocked_by task-26。复核人与实现同人，独立性不成立，改由 lead 在冻结树 grep 抽签。
- 22:15 派单：task-30 P-14b' 设计（快捷全屏并入会话）→ maid-impl（只写 Record/plans/）。并发 3/3（task-26 B-3b、task-28 P-14c 设计、task-30）。
- 22:16 收单 task-28（P-14c 设计，maid-check）：Record/plans/P14c-history-metadata-design.md 五节 15,926 字符；5 字段 source/engine/fallbackReason/printWindowTimedOut/sessionId（version 1→2），source 与 CaptureRules.ToHistoryMode/IsDowngrade 同构；四个提交点（Capture.cs:638/:773、Interaction.cs:264、MainWindow.Capture.cs:66）逐条「现在→改为」；旧 JSON 按 mode 推导默认、不回填。四裁：sessionId 不入库 / fallbackReason 存纯原因 token / 旧 JSON 不回填 / 本阶段不露出降级标记。改动面估 155~230 行。
- 22:17 收单 task-30（P-14b' 设计，maid-impl）：Record/plans/P14b-quick-capture-session-design.md 五节；关键更正——快捷全屏**不经过取景窗**（链：EscOverPinHook.cs:511-519 → MainWindow.Hotkeys.cs:108/:110-119 → MainWindow.Capture.cs:22-88 → CaptureService.cs:599-633），故 _quickCaptureBusy（Hotkeys.cs:171）与 _isCapturing（CaptureService.cs:23/:402/:103）是两套互斥；给 7 行交错时序表 + 6 条不变量；方案 A（复用漏斗，5 文件 +197/−118，EndCaptureSession 加 surface 跳过取景专属动作）推荐，方案 B（独立 QuickCaptureSession，6 文件 +300/−83）不推荐。
- 22:18 收单 task-26（B-3b，maid-capture rev5）：ScreenCapturer.cs BitBlt 失败抛 Win32Exception(:59-64) 且**修正 includeLayered 被忽略**（:54-56，此前普通 BitBlt 回退从未成立）；CaptureService.cs CaptureMonitorByBitBlt→Bitmap?(:581-590)、CaptureMonitorBestAsync 重写(:471-527，CAPTUREBLT 黑屏→普通 BitBlt→前景 WGC→统一「所有采集引擎均失败或返回疑似黑屏」)、CaptureFullScreenPngAsync null 分支(:609-613)；MainWindow.Capture.cs:51 文案改「采集失败，请重试」。派 task-29 F1 修复 → maid-capture。并发 3/3。
- 22:09 改派：task-26 B-3b → maid-capture（已解锁）；新建 task-27 P-14b 独立复核 → maid-impl（自写自判禁止）。并发 2/3。
- 22:02 验收 task-22（B-3 审计）：`Record/reports/B-3-capture-concurrency-audit.md` 21KB，三项均判可达——① EnsureMonitorPool 并发首调覆盖 state（P2，WgcWindowCapture.cs:401-405）；② 窗口捕获与显示器池共用 D3D11 immediate context 无锁（P2，:801/:804/:845）；③ ScreenCapturer BitBlt 丢弃返回值（P1，ScreenCapturer.cs:52-55，静默黑图可落历史）。裁：③ 按 P1 必修；拆 task-25 B-3a（①②，只改 WgcWindowCapture.cs，① 用锁内 ContainsKey 二次检查、② 用静态 context 锁不用 ID3D11Multithread）与 task-26 B-3b（③，ScreenCapturer + CaptureService，blocked_by task-23/25 串行同文件）；task-25 → maid-capture。
- 22:01 派单：task-23 P-14b 取景会话状态机接线 → maid-impl（写域 Services/ 与 CaptureOverlayWindow.Capture.cs）；task-24 P-13b EditDocument 第 1 步 + 关窗确认 → maid-check（写域 NativePinWindow*.cs）；task-22 B-3 审计仍在 maid-capture 手上。并发 3/3。
- 21:51 事故与修复：该次冒烟在便携副本里跑，`Startup: 已更新登录启动路径` 把 HKCU Run 的 IshWY 值改成了 `.tmp\smoke-131b\...`；已用 `Set-ItemProperty` 恢复为 `src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe --startup`，并删除 `.tmp/smoke-131*`。教训：冒烟若在副本里跑，必须在配置里先关开机自启，或跑完显式恢复 Run 键。
- 21:52 决策（主人）：直到整改目标全部完成之前不考虑发布——不打 tag、不建 GitHub Release；版本号 1.3.1 先随代码留在 main，最终发版版本号届时重定。
- 21:53 B 批次开工：总纲 §11 续记「冒烟口径/发布冻结/B 四项拍板/B 第一批三单」；派 task-20 P-14a → maid-impl（Core 类型与结果矩阵）、task-21 P-13a → maid-check（EditDocument 设计）、task-22 B-3 → maid-capture（采集层并发审计）；并发 3/3，编制不变 3/5。
- 22:28 C 阶段勘察：UI 建议校正版严重滞后于 HEAD 69616cc——P0-1/3/4/5 已实现、P0-2 半实现、P0-6 仅剩副标题折行；照文档重做会返工 → 派 C1-A 验证表（task-34）与 C1-B 决策清单（task-35），实现类 C1-C/D/E 待决策后派
- 22:28 拍板：接受 P-13c 泛型 EditDocument<TAnnotation> 与「可见序列+撤销历史」拆分（Core 不得依赖 App 类型），追记写入 Record/plans/B1-editdocument-design.md；步骤 2 接线立 task-33（Annotation:IEditNumbered + NativePinWindow 改走 Core，Core.Tests 必须仍 268）
- 22:44 验收 task-29（F1，maid-capture rev5）：两层归属闸门落地——CaptureService.IsOwnedCaptureSession(:78)（Guid.Empty 放行）+ CaptureOverlayWindow.Capture.cs FinishCaptureSession 入口 :204-208 先挡；_sessionFinished 改名 _currentSessionFinished 收窄为「本会话」，Prepare :263 复位。全屏三态 QuickCaptureStatus{Success,LikelyBlack,AllEnginesFailed}（CaptureService.cs:633-638），CaptureMonitorBestAsync 用 sawFrame 区分「看过帧但全黑」与「一帧未得」(:557)，MainWindow.Capture.cs 三态分流 :39/:49/:51/:58，失败态不写历史不写剪贴板。lead 抽检五条保护日志关键字命中数 1/2/2/1/1。
- 22:45 冻结树门禁（lead 自跑，不采信自评）：build-guard tag=bfinal 整包 0 警告 0 错误 15.65s、EXIT=0；Core.Tests 268/268（74ms）；dotnet format whitespace rc=0。
- 22:46 提交 B 批次：7985f6c「feat(capture,edit): B 批次收口」，56 文件 +5806/−1062（19 M + 2 D + 31 A，含 16 份新报告、5 份计划稿）；已推送 origin/main（6eff7c0..7985f6c）。工作树只剩未跟踪 .tmp/ 与 大佬修改材料/。
- 22:46 重建默认 bin：无运行实例，dotnet build PrtScr.sln -c Debug → 0 警告 0 错误 16.82s；IshWY.dll 966144 字节、22:46；UTF-16LE 校验命中「采集失败，请重试」「独占全屏游戏」「已保留编辑内容」「close discarded」「本次来源」。
- 22:47 待主人实测 B 批次（关窗确认、操作级撤销、全屏三态、F1 连按、WGC 并发），通过后进 C 阶段实现（W-1…W-6）与 B 余项（P-14c 实现、P-14b' 方案 A）。
- 22:55 验收（主人）B 批次六项手测全过（关窗改用右键菜单「关闭贴图」入口实测）；发版仍冻结。
- 22:56 派单 C 阶段三单：task-38 C-W1/W3/W4/W5 → maid-impl（MainWindow.xaml/HistoryWindow.xaml(.cs)/PinManagerWindow.xaml/OcrResultWindow.xaml）、task-39 C-W2/W6 → maid-check（OcrResultWindow.xaml.cs/PinCropWindow.cs）、task-40 C 复核 → maid-capture（blocked_by 38/39，只读+报告）。并发 3/3、编制 3/5。按文件簇切分，四份 xaml 零重叠。
- 23:03 验收 task-38（C-W1/W3/W4/W5，maid-impl rev5）与 task-39（C-W2/W6，maid-check rev3）：lead 抽检判据全过——四份 XAML 文字类 Opacity 归零（只剩 MainWindow.xaml:89 背景图 0.25 功能位）、AutomationProperties.Name 命中 27/2/5、完整快捷键目录 0 命中、HistoryWindow MaxLines=1/Trimming 7 处、Settings*HotkeyText 仍 7 张、PinCardView 4 个逐卡名属性、HistoryWindow.xaml.cs 含「每类最多」。裁定：task-39 三项待拍板按实现保留（复制反馈不随编辑清空、构造期播报一次、位置在标题下第三行），记已知行为；W-5 采用方案 A 结构判据（PrintScreen/F1 文本层不可比）。
- 23:03 冻结树门禁（lead 自跑）：build-guard cfinal 整包 0 警 0 错 13.89s、EXIT=0；Core.Tests 268/268（64ms）；format rc=0。
- 23:03 提交 C 批次：99ecf96「feat(ui): C 批次视觉收口」，11 文件 +426/−101（含两份报告）；已推 origin/main（58eed3b..99ecf96）。
- 23:03 task-40 C 独立复核仍未做（maid-capture，pending）——主人要求本轮收工，复核顺延到下一轮开工第一件事；默认 bin 未重建（IshWY 22:49 启动的实例 PID 18676 占用 bin/x64/Debug），C 批视觉需下次关实例后重建才可实测。
- 23:03 收工：A/B/C 三批已提交推送（86d3541 → 9c81858 → 6eff7c0 → 7985f6c → 58eed3b → 99ecf96）；遗留 C 复核 task-40、B 余项 P-14c 历史元数据与 P-14b' 快捷全屏、D 阶段功能路线；发版继续冻结。
- 11:16 缺陷定位（主人 m01579 报告）：拖拽中按 Tab 选区冻结。三条 Tab 入口（CaptureOverlayWindow.Capture.cs:87 / :128、CaptureOverlayWindow.xaml.cs:147）都调 Mode.cs:28 ToggleCaptureMode，后者在 :37-42 无条件把 _dragging 等拖拽状态清零，而左键与指针捕获仍在 ⇒ HandleMoved 退回 UpdateHover、选区停住；次生 HandleReleased（Selection.cs:258-261）首行早退，跳过 ReleasePointerCapture。裁决：拖拽中直接忽略 Tab 并提示（不排队到松手），守卫加在 ToggleCaptureMode 首部（三条入口共用），ReleasePointerCapture 提到早退之前；派 task-41 H-1 → maid-impl。
- 11:17 P-14c 拍板补充（可测性）：元数据 DTO 与 JSON 选项/序列化下沉到 src/PrtScr.Core（HistoryMetadata.cs / CaptureHistoryRules.cs / HistoryCaptureMeta.cs），CaptureHistoryService 改用 Core 类型且 ToItem 语义逐字保持；sessionId 不入库；fallbackReason 存纯原因（PrintWindow 超时写 "printwindow-timeout"）；旧 JSON 不回填；全屏 source=normal/engine=null；贴图裁剪 meta=null。派 task-42 → maid-check，要求 ≥12 条 Core 用例。
- 11:19 task-40（C 独立复核）description 改到 rev3：第 5 条判据由「PrintScreen/F1 出现次数」改为结构判据（「完整快捷键目录」0 命中、原十行表位置变 3 张卡、7 张 Settings*HotkeyText 仍在、可编辑表格未删），owner 重派 maid-capture。三单并发 3/3、编制 3/5；工作树 HEAD dc7b6a2 未提交改动由三单各自负责的文件簇隔离。
- 11:28 验收 task-40 C 独立复核（maid-capture rev5）：结论「C 可放行、0 项返工」，六项 W-1…W-6 在 HEAD dc7b6a2 命中，报告 Record/reports/C-review-visual.md。lead 抽检判据全过：HistoryWindow.xaml:272/279 与 PinManagerWindow.xaml:47 可访问名、文字类 Opacity 只剩 MainWindow.xaml:89 背景功能位、HistoryWindow.xaml:31 MaxLines=1、HistoryWindow.xaml.cs:280「每类最多」、MainWindow.xaml「完整快捷键目录」0 命中、Settings*HotkeyText 仍 7 张（677/687/697/707/717/727/737）、PinCropWindow.cs:408「Esc 退出」。裁定：第 4 条判据文本写 AnnounceStatus 属任务书笔误，实际实现 AnnounceCopyStatus 在复制路径四处（OcrResultWindow.xaml.cs:222/240/242/244）且与 C2 原始判据一致，按通过处理、不返工。门禁数字（非冻结树，含 maid-impl 的 H1 改动）：build 0/0 44.97s、Core.Tests 268/268、format rc=0；冻结树以合并后 lead 复跑为准。
- 11:56 冻结树门禁与提交：整包 build 0 警 0 错 17.63s（tag=merge17，.tmp/out-merge17）、Core.Tests 305/305 81ms、format rc=0；提交 52c5565「feat(history,overlay): 拖拽中 Tab 守卫与历史元数据 version 2」，18 文件 +1303/−37（源码 11 + 报告 6 + ProjectLog），已推 origin/main（dc7b6a2..52c5565）。行尾处理记录：本轮 11 个源文件在 maids 编辑后为 CRLF（.editorconfig 要求），临时转 LF 会让 format rc=2；直接 commit 后 HEAD blob 为 LF、工作树 CRLF、git status 干净，无需手工归一。
- 11:57 task-43 D-2B 设计验收（maid-capture rev3）：Record/plans/D2B-pin-persistence-design.md 八节，核心取舍 data/pins + index.json + 版本化 PNG、flattened 标注、PinRecord 增 Id/EditRevision、PublishPinState 单入口、750ms 去抖 + 退出 Flush、50 张/128 MiB 超限只停写不删；5 条未决待主人拍板（再编辑/导航/隐藏穿透/pinPersist 开关/配额淘汰）。裁定按设计稿推荐执行，等主人确认。
- 11:59 环境：默认 bin 未重建（运行实例 PID 8156，10:53 启动，占 src/PrtScr.App/bin/x64/Debug）；已请主人关闭后重建并手测 Tab 拖拽与 history JSON。
- 14:00 H-2 返修阶段交付：主人再次反馈 Tab 无效后重开；Flash 诊断 task-44 与独立复核 task-46 均交付，三条 Tab 入口/框选移动缩放共用守卫的静态覆盖通过，未发现必须追加补丁的具体遗漏，本轮未改 src。重建前实际运行的默认 DLL 无 H1 特征，证实交付缺口但不据此否定反馈或宣称 H1 有效。主人确认退出后 13:54 核对无实例，默认整包 build 0 警 0 错 30.83s、Core.Tests 305/305（67ms）、format rc=0，build-guard h2default EXIT=0。重建后默认 DLL SHA256=7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C，含 H1 日志/提示，13:57 尚无运行实例；实际加载身份与真实拖拽仍待验，H-2 不关闭。证据 Record/reports/H2-artifact-evidence.md。
- 14:00 规划对齐阶段交付：总纲 §0/§8/§12、docs/ROADMAP.md 当前状态块与本日志一致，区分源码/独立复核/自动门禁/用户实测，保留历史验收；task-45 文档对齐已完成。真实 Tab 验收后按 P-10→P-11→B 余项/P-14b′→C 剩余→D；D-2B 五项未决仍待主人确认。当前 HEAD 仍为 5e74f62，本轮记录/路线图未提交；未 --smoke、未合成输入/UIA、未改真实数据/HKCU、未发版。
- 17:45 阶段切换：主人 17:41 明确验收新版 Tab 通过并授权继续，H-2 收口（不外推 C/P-14c）。P-10 task-47（App/Core/测试）与 P-11 task-48（scripts/CI）分域并行，均显式 deepseek-account/deepseek-flash；第三 Flash 工位待实现后独立复核。修改前 build-guard p10base 整包 0 警 0 错（27.70s）、Core.Tests 305/305（91ms）、format 0，EXIT=0；HEAD 5e74f62，既有记录/路线图未提交。冻结 smoke v1 nonce/PID/路径/Loaded+UI队列协议，未审查前不运行新 smoke，禁止旧 smoke/真实数据及 HKCU 副作用；发布冻结，D-2B 五项仍未拍板。
- 18:34 实施收件与复核切换：P11 task-48 交付脚本/CI 与报告（34/34 离线负例、完整构建0/0及324测试为工位过程证据，非lead冻结树验收）。此前 task-48 越界写src已撤回，18:07 src暂回HEAD；当前 task-47 的隔离实现已重新出现，正式报告仍待收件。纠正见 Record/plans/P10-P11-ownership-correction.md。task-50 显式 Flash 先行独立复核 P11，重点核CI跳过smoke、Release输入插值与全输入指纹；task-49联合安全审查待P10交付。不运行App smoke/实际打包/Actions，不发版。
- 18:41 收取P10 task-47正式报告 Record/reports/P10-smoke-app.md：协议v1、单终态和隔离接线、新增19例；工位整包0/0 32.25s、324测试96ms、format0，未运行App冒烟。18:42 task-49显式Flash并行只读复核S01–S09及薄接口；task-50继续P11 E01–E10。两单收件不等于验收，安全结论到齐前不跑App/打包。
- 21:24 恢复：主人21:21明确继续，核对HEAD仍5e74f62、未提交工作树保留。停工时P10审查与P11返工均只读、无新增写入；恢复原task-49静态安全审查（Flash 7033ddb6-a410-4a01-bed9-596de2bf937d）与task-51第1轮scripts/CI返工（Flash fbe751f0-0790-40e1-8922-51dac06d8e8f），写域不重叠。P11独立needs_revision的B1–B6及严格CI裁决不变；静态安全结论到齐前不App冒烟/实际打包/Actions。自动goal恢复被宿主拒绝，仍paused；本轮按主人明确授权执行，不另建goal绕过。
- 21:38 安全审查转返工：P10 task-49独立needs_revision，XAML托盘Loaded自动创建使S07未隔离；单实例异常fail-open、目录异常/测试覆盖亦待修。task-53显式Flash首轮返工App/Core/Tests，与P11 task-51 scripts/CI分域。新增P11 B7入口未保证--smoke，已纳入返工契约与task-52复核；未确认运行工位已收到补充，不起重叠写工。真实App smoke/publish/ZIP/Actions仍hold，修后task-52/task-54分别独立复核再合并静态安全结论。
- 21:55 P11首轮复核needs_revision：task-52报告已收件，B1–B6静态通过但B7缺--smoke仍阻断；lead按E09/E08归并B8输出与ZIP条目绑定、B9 ignored未知目录覆盖。task-55显式Flash 40090370-a653-4646-ba8d-802d38f23ff2开始第2轮最终返工，task-56已建依赖后独立复核；P10 task-53继续首轮、task-54待交付。契约Record/plans/P11-round2-repair.md；两侧静态许可前不App/publish/实际包/Actions，不第三轮自动返工，不发版。
- 22:00 P10首轮返工task-53已收件（仅行政收件非安全通过）：报告R1正常模式代码建托盘/R2单实例异常smoke关闭/R3环境异常归Invalid并补3例/R4局部钩子guard；工位整包0/0、327/327、format0。task-54独立只读复核已派Flash 46692ed0-7aef-4720-b880-042f409d8d48，重点审新托盘字段null异常路径、Loaded不可达及测试真实覆盖；P11 task55最终返工仍并行，hold不解除。
- 22:20 P11第二轮最终返工task-55已正式收件（非安全pass）：工位旧离线136/136、新行为137/137、8文件解析通过；只用替身/合成ZIP/临时git。task-56独立只读复核已派Flash 3811f7a2-c97c-45cb-a5d0-9b50857e0200，逐项B1–B9/E01–E10，重点真实argv、输出与ZIP绑定、ignored豁免；P10 task54仍审。任一未通过不App/publish/实际包/Actions，P11最终轮失败上报而非第3轮。
- 22:34 P10首轮task-54独立needs_revision：托盘半构造异常被吞，可能导致Loaded中断或_delayCts楔死，F1/F2未闭环。task-57显式Flash bc769937-95d0-4c44-ac2a-3bd5b55dcb36最后第2轮最小修补，唯一产品写域MainWindow.Windows.cs；选择恢复构造失败关闭、局部完成再发布，保留HEAD原ForceCreate容错。task-58已设依赖后独立复核；P11 task56并行，两侧最终pass前hold不解除，最后轮再失败上报、不重置返工次数。
- 22:49 最终P11复核升级：task56 needs_revision，唯一BLK-1为全局图片/ico/log后缀豁免漏构建输入（B9/E08）；两轮用尽，不自动R3，主人决定前不App/publish/产品ZIP/Actions。协调裁决Record/plans/P11-round2-escalation.md纠正“候选0=既有产物无错绑”的过度结论及旧task54引用。P10 task57最后修补已收件（工位0/0、327/327、format0、结构16项，非验收），既有task58已派Flash 842a7c56-a10b-434f-8056-f9065a892b05只读最终复核。未提交/发布。
- 22:53 主人决定：p11-final-exception选择“暂缓P11”，未批准超限修补；P11不派R3、不降标准，保留未提交代码与证据，后续需明确恢复与超限边界。P10 task58已派最终独立复核照常收件，不因P11暂缓取消；真实App/publish/产品ZIP/Actions及提交发布继续hold。
- 23:14 P10最终独立复核收件：task58静态安全pass、F1/F2关闭，无新增阻断；lead核对构造发布/关闭漏斗与MainWindow.Windows.cs SHA256=2641D3B64BEF678AFE901DD87B6A8F9C36657BC5F0A0E60D415CB21F44CCDA20，与报告一致。task58仅行政收件完成，非运行验收；S06、S07前后快照、S09真实交互、lead冻结树门禁仍未做，327/327与16项结构检查仍为过程证据。P11按22:52主人决定暂缓，B9/E08阻断与联合hold不变，不派R3、不App/实际publish/产品ZIP/Actions/commit或发布；已同步总纲/执行单/升级裁决与交接。

## 2026-11-08
- 11:04 启动故障接手：主人要求解决开机自启与手动打不开；真实日志两模式均MainWindow.InitializeComponent XamlParseException，Run路径/批准态正常、无残留实例。task59与独立Flash只读诊断核对DLL/XBF连接器，保全17项产物与399份配置/历史哈希；327/327及format基线绿。新故障不恢复P11，未App smoke/打包/Actions/提交发布。
- 11:11 恢复阶段：无实例下默认整包Rebuild 0警0错26.34s、327/327 90ms、format0（lead，guard EXIT0/36s）；DLL 7FA245…→ED35D…，主XBF/PRI哈希不变。11:07两次全新--startup/手动启动均主窗、托盘与热键初始化成功，手动PID39628保留运行；399份非日志data与Run/StartupApproved两次校验未变。仅普通启动恢复，独立诊断待回件、实际重启登录待主人验收；P11/真实smoke/打包/Actions/提交发布继续hold。报告Record/reports/startup-20261108-diagnosis.md，HeadMaid已纠正仅OutDir不足以隔离WinUI产物的工作纪律。
- 11:44 启动故障诊断收口：两路独立Flash回件2/2，lead核对WinUI XBF→OutputPath、程序集→OutDir及R1/R2仅OutDir命令，定位混装入口；撤回DLL锁竞争解释并纠正R2“未写默认bin”推断，旧7FA2 DLL完整备份与哈希保留。task59由lead行政收件关闭，非工位自行验收；本地成套恢复及两种新进程启动已验证，真实登录待主人确认。P11暂缓、未提交/推送/发布，证据见[源码输出核查](<Record/reports/startup-20261108-source-audit.md>)。
| 2026-11-08 12:55 | 启动故障用户验收与 B 续工 | 主人确认真实重启成功，登录启动故障关闭；总纲 §12 允许 P11 明确暂缓期间推进独立 B/P14b′，不恢复第三轮/联合 smoke/publish/ZIP/Actions/提交发布。总纲 §8 撤下 OutDir-only 命令，恢复测试基线为327；task60/61/62分派既有 Flash 工位只读刷新接线、构建隔离和取消副作用。自动goal恢复被宿主拒绝，保持paused，不新建目标绕过；无新产品修改或构建、未操作用户实例。 |
| 2026-11-08 13:21 | B/P14b′ A1最小批次验收 | task60/61/62只读预检收件；task63追加Core QuickSuccess7/QuickFailed8及16用例，task64独立PASS，两文件哈希与lead门禁后身份一致。完整隔离Build0警0错、343/343跳过0、format0；默认bin/obj1931文件前后变化0，独立产物含PRI/8XBF/六项目assets。仅A1通过，App入口/取消提交租约/旁路所有权仍未接线；五项产品建议待主人确认。未重建默认bin、未启动或操作用户实例、P11暂缓与所有联合执行/提交发布hold不变。流程作为候选记入技能库，不激活旧smoke或新流程。 |
| 2026-11-08 13:27 | P14b′ 五项产品契约批准 | 主人通过进展页选择「全部按建议」；先更新总纲与决策稿为已批准，task65/66分别冻结统一会话/不可逆提交与旁路/退出接口，再划互斥写域实施。P11暂停、无App启动/默认产物覆盖/提交发布许可不变；批准只针对P14b′五项，不涉及D-2B。 |
| 2026-11-08 14:07 | P14b′ 实施与独立复核边界 | task65/66接口、task68/69补核收件；task67初版End清身份破坏旧Overlay先Finish后写入，独立needs_revision后R1交付（唯一active、保最近Owner/Current/meta、确定性竞争测试），task74核销中。task70旁路owner/token/创建线程清理交付，task73复核中；task72 Record门禁工具needs_revision，R1补全集合冻结、build收据、拒绝证据复用、精确EXIT/TRX与format有效属性预检，四脚本只做语法解析exit0，task75复核中。服务/UI未实施，新增测试未运行，实跑基线仍343；128源码与原默认bin/obj Before保留，未操作用户实例/默认产物，P11及提交发布hold不变。 |
- 2026-11-08 14:18 P14b′中间里程碑：task74 Core R1和task73旁路均独立静态PASS、lead核对三hash一致；尚无新门禁/运行验收。task76服务唯一写域已派、task78 UI路由/真实退出只读补核；UI待服务接口最终冻结后派。Record门禁工具R1两必修（UTC原始字段/各项目bin/obj归属）R2已修并Parser0，task77末轮静态复核中，未经通过不跑实际门禁。保留128源码dirty基线及1931默认产物Before；74新增规则测试未执行，已执行基线343。P11暂停/不运行App/不覆盖默认产物/不提交发布不变。
| 2026-11-08 14:30 | P14b′服务首交及UI接线边界 | task77 Record工具R2末轮独立静态PASS、四hash复核，19纯辅助fixture通过（非App门禁）；task76仅两服务文件首交，lead发现Quick.cs:91引用不存在Coordinator.BeginCaptureAttempt，未验收。task79核Core/服务、task81核Quick IO/旁路；公共API冻结给task80独写UI三文件，保四处Overlay忙语义与单次用户退出预算。task78补核归档并纠正callee ConfigureAwait/已完成Delay误推断，未经申请扩读如实记流程违例。原Before、dirty基线、P11暂停及App/默认产物/提交发布hold不变；无实际新门禁。 |
| 2026-11-08 14:40 | P14b′独立复核与服务R1 | task79服务首审needs_revision（不存在成员/预留异常释放），task81 IO范围静态PASS、task82 UI三文件静态PASS；三回件按各自范围行政关闭，四实现单未验收。原maid-impl工位仅服务两文件R1，兼修Ignored伪成功/旁路真实Activated/晚到重试检查，不变更Core/Bypass/UI；所有实际门禁待最终冻结。P11暂停、App/默认产物/提交发布hold不变。 |
| 2026-11-08 14:46 | P14b′最终源码候选冻结 | 服务task76 R1两文件回件并结束写入，修类型/预留异常释放/日志与取消回调隔离、Ignored与旁路晚到。lead Freeze131源码，相对原128 dirty基线增量8项，实施八hash与回件/此前审查一致；task83服务/Core及task84服务/UI最终只读复核中。尚未执行本轮Build/test/format，原1931默认Before保留；实现单未验收、P11及App/默认覆盖/提交发布hold不变。 |
| 2026-11-08 14:55 | 主人暂停与隔离门禁失败留证 | task83/84最终静态PASS、hash与Freeze匹配；实际Build exit1、2警1错（XAML参数并吞及两处xUnit2031），后续test/format/ArtifactCheck未跑。原1931默认Before/After变化0、Source Verify131/8增量/变化0。主人要求先停，停止派工/修复/门禁并保存失败与交接；四实现单未验收，工具不自动R3，P11/App/默认覆盖/提交发布hold不变。 |
| 2026-11-08 17:55 | 主人回来明确续工 | 按原失败交接恢复P14b′：task67同单R2仅等价修两处xUnit2031，Flash原实现工位写Tests单文件，不运行门禁。门禁工具已耗两轮，追加一轮仍待单独确认；旧失败快照/收据不覆盖，P11及App/默认覆盖/提交发布hold不变。 |
| 2026-11-08 18:04 | P14b′额外R3许可与根因实证 | 17:59主人显式批准门禁工具额外R3（不恢复P11）。同链属性实验已复现旧尾反斜杠引号吞后续参数，正斜杠后路径和tail正确；Core R2两断言等价修复，逆转两行hash精确还原旧文件，其余源码不变。新轮p14b-app-20261108-1800-r3已Freeze131文件/dirty增量8，默认新Before1931与旧After逐项一致；原失败证据保留。task85独立只读复核四工具中，尚未执行实际新Build/test/format/ArtifactCheck。 |
| 2026-11-08 18:17 | P14b′源码与隔离门禁验收 | 工具task85 R3独立PASS；实际完整Build0警0错、417/417零跳过、format0；产物绑定与Source Verify131/8增量/变化0，默认1931文件Before/After变化0。四实现单按源码/隔离范围验收，旧失败证据保留；未启动App/更新默认产物，真实F1/F2、焦点/退出及JSON仍待实测，P11暂停与提交发布hold不变。 |
| 2026-11-08 18:32 | P14b′默认运行版更新交付 | 主人18:24退出并授权后确认无实例，完整solution默认成对Rebuild0警0错；默认Core.Tests417/417零跳过，131冻结源码变化0、401非日志数据和自启动两项不变、8XBF匹配隔离产物。默认DLL/EXE/PRI身份及旧12项备份已留证；未启动App，真实交互/JSON仍待主人手测，P11与提交发布hold保持。 |
| 2026-11-08 18:47 | P14b′用户使用验收通过 | 主人完成默认运行版手测并反馈效果很好、没有问题；本轮交互收口，不外推为独立JSON或精确退出时长测量。哈希用于源码/产物身份与写入范围核对，不替代功能测试；后续避免同一状态下重复、信息增益低的扫描。P11暂停及提交发布hold保持。
| 2026-11-10 20:05 | 换会话交接 | 主人准备换会话，新增 [交接文档](<docs/HANDOFF-2026-11-10.md>)（八节：工作树 121 项 dirty 与 3 个未跟踪新源码、P14b′ 四层验收口径、D-2B 待拍板、P11 暂缓、硬约束、开工前十分钟、文件索引）。本轮只写文档与记录：未改产品源码、未跑新门禁、未提交/未发布；工作树与 HEAD 5e74f62 未动。→ 新会话先问主人选 D-2B（需先拍板五条）还是 C 遗留真机验收，不自行改序。 | |
| 2026-11-21 13:2x | 热键修复派单 | 主人派活修两个热键 bug：①单独按 a/d 误触发截图/全屏（EscOverPinHook 修饰键 _altDown 等静态记账残留，被 OR 语义误判为 Alt+A/D）；②取景器外 [ ] 键被吞（_closed 初值 false + WarmUpHidden 预热把 _overlayKeysActive 残留为 escOnly）。lead 只读定位根因后派 maid-hotkey-fix（deepseek-flash）修 EscOverPinHook.cs + CaptureOverlayWindow.xaml.cs，验收判据三绿 build/test/format + 静态核对。 |
| 2026-11-21 14:2x | C 遗留精确盘点落盘 | lead 产出 Record/reports/C-遗留精确盘点.md：补齐仓库缺失的「四项 P1 + 九项 P2」权威逐条清单（带 文件:行 证据），核对 W-1…W-6 六项修复单全部落地、从遗留排除；给出编制表（C-残1 女仆A 改 MainWindow.xaml / C-残2 女仆B 改 NativePinWindow.EditRender.cs，可并行）与 3 个待拍板项+推荐默认。未派单、未改产品源码。 |
| 2026-11-21 13:3x | 热键修复验收通过 | maid-hotkey-fix 回件经 lead 亲自复跑门禁验收：build 0 警 0 错 EXIT=0、Core.Tests 417/417 通过、format EXIT=0、静态核对四项 PASS（遗留字段 0 / TrackModifier 0 / 实时判定 12 行 / _closed=true@L100）、两 .cs CRLF 无 BOM 末尾换行。改动仅 EscOverPinHook.cs + CaptureOverlayWindow.xaml.cs。未做实机按键验证（纪律禁交互测试）、未提交/未打 tag。报告 Record/reports/hotkey-a-d-brackets-fix.md。 |
| 2026-11-21 14:3x | C 遗留拍板与派单 | 主人 m00573 确认按推荐默认走：P1-15 不做、V-1（P1-14+P2-8）不做、P1-3 用 16px、P2-7 不改。派 C-残1（maid-c1-xaml 改 MainWindow.xaml：P1-1 四页包卡片 + P1-3 上边距 16px + P2-3 目录表键帽）与 C-残2（maid-c2-cancel 改 NativePinWindow.EditRender.cs：P2-5 取消按条件变色），两单不同文件可并行。 |
| 2026-11-21 14:5x | C 遗留两单验收通过 | lead 亲自串行复跑三绿：build 0 警 0 错 EXIT=0、Core.Tests 417/417、format EXIT=0。C-残1 改 MainWindow.xaml（SectionTitleStyle Margin→0,16,0,0；四页 9 张 SettingsCardStyle 卡片；目录表 11 个按键 TextBlock 包 HotkeyChipStyle，含第三张「截图历史」卡整表一致；未删任何控件——diff 中托盘块消失是 P14b′ 既有代码接管，非本单）。C-残2 改 NativePinWindow.EditRender.cs（取消 item.Id==23 改为 HasPendingAnnotations 才 cancelBrush 否则 commandBrush，复用 EditHistory.cs:26）。两报告 Record/reports/c1-xaml-card.md、c2-cancel-conditional.md。未做实机视觉验收、未提交/未打 tag。 |

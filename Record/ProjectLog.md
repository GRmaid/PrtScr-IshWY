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

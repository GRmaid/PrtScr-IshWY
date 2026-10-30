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
- 23:18 事故：误用 tools.write 传空字符串占位，把 Record/plans/2026-10-28-整改总纲.md（176 行）截断为空；用 git show HEAD 版打底 + 补回 §11 续两节恢复为 197 行 / 14 个二级节。教训：不用空内容占位，长文本追加用全文重写。
- 23:20 校准：P-04b 日志关键字定稿并发 maid-check，P-12 报告 G1 行（2.4 表 :159 + 待确认 #2）改为可判别判据。
- 23:26 派单：task-19 B-1 设计（maid-check）进行中；task-18 P-04b 代码改完、构建绿，Core.Tests/format 与报告在跑。

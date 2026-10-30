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

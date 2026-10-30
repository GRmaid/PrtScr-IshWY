# P-07c（task-17）：CaptureDetailedAsync 入口取消预判

- 状态：代码与报告完成，三条门禁全绿；未 commit（提交由 lead 统一分批）。
- 写域：仅 src/PrtScr.Capture/WindowCapture.cs（入口约 6 行）与 Record/reports/**；未改 App 层、未加依赖、未跑 --smoke、未杀运行实例。

## 一、做了什么

在 WindowCapture.CaptureDetailedAsync 函数最前加入 token 预判（WindowCapture.cs:215-222）：

    // 入口预判：token 已取消时立即脱身——不占单飞门闩、不起 PrintWindow、不产生晚到帧。
    // 已经起跑之后再取消仍走「调用方脱身 + 晚到帧一次性后继释放」，这里不放大既有竞态。
    if (cancellationToken.IsCancellationRequested)
    {
        Trace("取消：进入捕获前 token 已取消，不发起 PrintWindow");
        return WindowCaptureResult.Failed(
            WindowCaptureFailure.Canceled, false, "捕获已取消（进入捕获前 token 已取消）");
    }

并在方法摘要补一行契约（:210）：进入时已取消 → 立即 Canceled，不占门闩、不发起 PrintWindow。

**位置语义**：预判在 InvalidBounds 校验（:224-231）与 TryEnterFlight（:233）之前。即「token 已取消 + 句柄/尺寸非法」时返回 Canceled 而非 InvalidBounds——取消优先，符合调用方已不再需要结果的语义。

## 二、证据

### 2.1 改前 / 改后调用序列

| 步骤 | 改前（P-07b 收尾树） | 改后（P-07c） |
| --- | --- | --- |
| 1 | 校验 InvalidBounds | **预判 token：已取消 → 立即 Canceled，直接 return** |
| 2 | TryEnterFlight 占单飞门闩 | 未取消才继续：InvalidBounds |
| 3 | StartPrintWindow 起专用线程、真正调用 PrintWindow | 未取消才 TryEnterFlight |
| 4 | WhenAny(printTask, Task.Delay(2000, ct))；token 已取消时 Delay 立即完成 | 未取消才起 PrintWindow |
| 5 | 若 printTask 已先完成 → ReferenceEquals(winner, printTask) → **返回 Success**（已取消却被标成功）；否则走 ct.IsCancellationRequested → Canceled，但门闩与原生线程已占用 | — |

结论：改前「已取消」仍会占用门闩并起一次 PrintWindow；当原生调用比 WhenAny 判定更快时直接返回 Success，与失败矩阵行 4「取消 → Canceled」不符。改后入口即 Canceled，门闩、线程、晚到帧都不产生。

### 2.2 失败矩阵补注（Record/reports/P-07b-wgc-detailed.md 2.4）

1. 行 4 的 PrintWindowTimedOut 列：false → false；超时后再取消 → **true**。依据：PrintWindow 超期后 printWindowTimedOut=true（:269），随后在 WGC 前 :295 或 WGC 报取消 :308 返回 Failed(Canceled, printWindowTimedOut, …)，此时为 true。
2. 行 6 追加：超时原因在**顶层 Detail**（WindowCapture.cs:332，P-07c 前为 :322）——即「PrintWindow 超过 2000ms，已改用 WGC」；WgcDetail 仍是 WGC 自己的细节（成功时为 null）。
3. 矩阵后新增说明段：入口预判（WindowCapture.cs:216-222）不占门闩、不起 PrintWindow，先于 InvalidBounds/Busy；起跑后取消的竞态不变。

### 2.3 门禁（全绿，可复核）

| # | 命令 | 结果 |
| --- | --- | --- |
| 0 | dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo | 0 警告 0 错误，0.95s |
| 1 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p07c -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p07c/" | 0 警告 0 错误，14.51s，BUILD-GUARD: EXIT=0 elapsed=15s |
| 2 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p07ctest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | 通过 **89** / 失败 0 / 跳过 0，93ms，EXIT=0（基线 77，队友新增 12 条，无减少） |
| 3 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | **rc=0** |

构建均经 .tmp/build-guard.ps1 共享锁串行；未使用 -t:Compile（已知假绿，见 P-07 报告）。

### 2.4 改动范围

- git diff --stat -- src/PrtScr.Capture/WindowCapture.cs（对 HEAD 累计，含已验收的 P-07b）：+99 / −16；其中 P-07c 本次净增 +10 / −0（摘要 1 行、预判 6 行、注释 2 行、空行 1）。
- 未触及其他源码文件；Record/reports/P-07b-wgc-detailed.md 仅补注矩阵。

## 三、进行中（交接用）

- 本单代码与报告已完成；无剩余代码动作。
- 下一步第一件事（若 lead 要收尾）：team_task_update task-17 completed，三行 output；无需再改代码。
- 下一步该读的文件：本报告 2.1/2.2；src/PrtScr.Capture/WindowCapture.cs:212-233。
- 关联单：task-18 P-04b（App 层收尾漏斗，maid-impl）落地后，App 才会把会话 token 传入本入口；届时入口预判才在 GUI 可达。

## 四、风险与遗留

1. **入口预判当前 GUI 不可达**：App 两处窗口调用（CaptureOverlayWindow.Capture.cs:297/:339）仍未传 CancellationToken，本改动是防御性契约修正，需等 B 批次/P-04 接线后才真正生效。
2. **起跑后取消的行为不变**：若 PrintWindow 已开始、且在取消前已完成，仍返回 Success（ReferenceEquals(winner, printTask)），由 App 会话闸门丢弃晚到结果。按 lead 边界「不放大既有竞态」处理。
3. **取消优先于 InvalidBounds**：已取消 + 非法句柄返回 Canceled。现有调用方未传 token，无行为变化；接线后需确认上层不依赖 InvalidBounds 做特殊提示。
4. **无新增自动化测试**：入口预判依赖 WindowCaptureResult 工厂，属纯托管判断，理论上可测；但本单边界为「约 5 行」且 Core.Tests 不引用 Capture（WinUI 依赖），未扩测试工程。
5. 未跑 GUI/--smoke/真机取消链路；矩阵补注为文档修正，不改变 P-07b 已验收行为。

## 五、待确认

1. P-07b 报告行号引用采用「当前树 :332 / P-07c 前 :322」双写；若 lead 要求只保留当前树行号，可再改。
2. Core.Tests 从 77 增至 89 来自队友新增测试；本单未新增/删除测试，是否需要在 P-07c 报告外单独记录由 lead 决定。
3. P-07b 矩阵行 4 的「超时后再取消」是否还需要在 P-04b 落地后配合会话 token 做一次真机验证（当前不可达）。

## 主人实测步骤

1. **正常窗口截图回归**：按 F1 → 选一个窗口 → Enter。预期：正常出图并 Toast，日志无 WindowCapture 异常。
2. **超时降级**：找一个响应慢的窗口（或大窗口），Enter 后等 2 秒以上。预期：取景不再无限等待，日志出现「PrintWindow 超过 2000ms…回退 WGC」，最终出图或给出可判别失败提示。
3. **取景中取消**：按 F1 出取景后立刻 Esc。预期：取景窗关闭、无失败弹窗；再按 F1 仍能出取景。
4. **日志抽查**：data/logs/app.log 搜索 WindowCapture: 取消：进入捕获前 token 已取消。当前 App 未传 token，预期**看不到**这条——看到即说明 P-04b/B 批次接线已生效，可进一步验证取消不占门闩。
5. **构建自检（主人可选）**：按本报告 2.3 的三条命令复跑，预期 0 错 0 警、89/89、format rc=0。

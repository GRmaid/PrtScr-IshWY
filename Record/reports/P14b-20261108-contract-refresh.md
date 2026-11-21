# P14b′ 当前接线契约刷新

- 单号：task-60；工位：maid-impl（Flash，只读）；状态：回件已收，lead补充核验。
- 原始回件：2026-11-08 13:01；源文件边界5份，未构建、未运行App、未写产品代码。
- 本报告是继续施工的输入，不是App接线或交互验收。P11保持暂缓。

## 当前接线证据

| 事项 | 证据（此次修改前行号） | 判断 |
|---|---|---|
| overlay单槽 | src/PrtScr.App/Services/CaptureService.cs:23,42,112–178,383,429 | _isCapturing入口与唯一End漏斗 |
| quick独立忙标 | src/PrtScr.App/MainWindow.Hotkeys.cs:171；MainWindow.Capture.cs:26,88 | _quickCaptureBusy与overlay槽互不相识 |
| quick原始采集 | src/PrtScr.App/Services/CaptureService.cs:633–676 | static路径无会话，结果只有Success/LikelyBlack/AllEnginesFailed |
| 历史兼容 | src/PrtScr.App/MainWindow.Capture.cs:67–68 | HistoryCaptureMode.Normal + HistoryCaptureMeta.ScreenQuick必须保留 |
| End映射 | src/PrtScr.App/Services/CaptureService.cs:288–310 | ParseEndReason与TerminalStateFor均需未来接quick分支，只有EndReasonText还不够 |
| 取景专属收尾 | src/PrtScr.App/Services/CaptureService.cs:171–173,418 | quick必须按surface跳过，不可凭_currentMonitor非零推断 |

入口共6处：MainWindow.Capture.cs:92菜单；MainWindow.Hotkeys.cs:108钩子、110–119注册热键兜底；MainWindow.Capture.cs:143–150全屏游戏改道、152–157 quick档位、193–198延时到点。旧设计漏菜单入口。

## 保护日志与lead补核

工位已读范围发现以下5串；其回件开头“4条”与实际枚举数量不一致，不能据此声称全部基线集合已覆盖。lead另读取景文件补入隐藏Dismiss串，后续保守保护以下6串及finished→state→result顺序：

```csharp
$"Capture: session {sessionId:N} invalidated"
$"Capture: session {sessionText} finish ignored (not capturing, reason={reason})"
$"Capture: session {sessionText} finish ignored (stale session, current={SessionText(lastSessionId)}, reason={reason})"
$"Capture: session {sessionText} finished reason={reason}"
$"Capture: already capturing, cancel session={activeText}"
$"Overlay: dismiss while hidden session={SessionText(session)} reason={reason}"
```

最后一串由lead在src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:254–264补核；隐藏时仍调用FinishCaptureSession，不能恢复早退。lead另读Core/CaptureResult.cs及CaptureScenario.cs：结果Failure字段不等于会话终态；既有ClipboardFailed/HistoryFailed矩阵为Completed。这是已有纯规则，不代表quick失败的新产品口径已经获批。

## 最小批次裁决

1. **A1现在实施**：仅src/PrtScr.Core/CaptureSessionState.cs追加QuickSuccess=7/QuickFailed=8及日志映射；src/PrtScr.Core.Tests/CaptureRulesTests.cs补数值、文本、Idle、活动态、终态幂等测试。旧规则不改；不添加Parse/Terminal公共API；App入口不动。新单task-63，仍由maid-impl执行；lead负责行政收件。
2. **A2暂未派实现**：未来服务内部独立管道，需先把取消/不可撤销写入契约及构建隔离核清；不为“先写不可达代码”绕过这些约束。
3. **A3暂不切换**：MainWindow瘦身与移除_quickCaptureBusy改变互斥/抢占行为，须先明确产品规则，再按新契约接线。

## 未决与不得夸大的结论

- 旧设计5条仍为推荐，不冒充用户批准：F1/F2优先级、失败留痕、退出收尾、Origin口径、附属写入失败口径。OriginNormal有旧稿lead裁决记载，但本轮只读工位未交叉核实。新增可见提示文案同样不擅定。
- 原建议F1取消quick并立即打开overlay并非现状的纯重构：当前IsCapturing分支只取消_overlay，quick时可能为空。
- 提交前身份检查不是覆盖异步写入全程的取消保证；晚到bypass焦点与提交边界须结合第二份审查。
- App静态Parse/Terminal与Core映射未来必须一致；本轮A1未接App，因此不能把Core新增原因称为完整会话统一。
- 构建门禁以真实隔离验证为准，旧OutDir-only命令不再可执行。

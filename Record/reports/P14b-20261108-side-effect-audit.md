# P14b′ 取消、旁路与不可撤销写入审查

- 单号task-62；maid-capture/Flash只读回件，2026-11-08 13:02。
- 已读5份：src/PrtScr.App/Services/{CaptureService,FullscreenBypass,ClipboardService,CaptureHistoryService}.cs及src/PrtScr.Core/CaptureSessionState.cs。未写盘/构建/运行App。
- 状态：边界审查已收到；下列问题是未来统一会话的设计风险/静态候选，不是本轮实机复现的用户故障。P11不恢复。

## 可核对的机制与限制

1. **quick调用方缺口**：CaptureService.cs:642–676静态quick采集无会话身份；这5份文件没有MainWindow编排与BeginAsync调用，因此无法单凭本回件裁定当前F1是否取消quick、调用方是否finally Dispose。task-60已补上入口枚举，但完整交错时序仍未验证。
2. **所有权不等于未取消**：CaptureService.cs:95–100的Invalidate清_captureSessionId并记录Canceled，不清_lastSessionId；End:123–169按owner防旧会话收尾清新槽。IsOwnedCaptureSession(Guid.Empty)放行，匹配已失效owner也可为true；IsCurrent是时间点检查。不能把旧owner防护改成无条件Current拦收尾，否则可能重引取消后永久忙。
3. **终态分类候选**：TerminalStateFor:302–310按reason决定终态，End:163重置Idle。若调用方取消后仍以confirm-success结束，存在state=Completed/failure=Canceled组合的可能；但当前取景Cancel/Finish调用链未在工位边界内核实，不能宣称已复现。需要后续用实际编排/测试区分故障与不可达路径。
4. **启动并发候选**：StartCaptureAsync:383检查_isCapturing，:409/:421等待后才:429占槽；两个异步入口可能同时越过检查。是否真的双Prepare，取决于调用方与线程/续体，尚无运行证据；未来quick共槽必须在await前原子占位。

## FullscreenBypass边界

- BeginAsync:105无CancellationToken；:159第一次SetForegroundWindow早于首个await:160；:165–174有60ms重试及500ms不可取消等待。检查不能只放在await后。
- Dispose:252–282无并发保护；若与Begin续体并发，可能过早清helper句柄。调用方应等待Begin返回后统一finally Dispose，不得取消方另行并发Dispose或丢弃对象。
- :260–271还原旧前台没有“当前前台仍是helper”条件；取消后用户已切窗时，晚Dispose可能抢回旧窗口。应在未来单独实现安全守卫；本轮未改此文件。
- helper创建:190–197为1×1置顶窗；不能靠丢弃对象“取消”，DestroyWindow线程亲和也不宜用finalizer掩盖。

## 外部写入的真实提交边界

- ClipboardService.SetImageAsync:15–43无CT/回滚；SetContent+Flush:30–31已提交；6×80ms重试可能延长窗口。SetText同理。
- CaptureHistoryService.AddAsync:62–119无CT；:89落PNG，:92–99写元数据，:104–108调用PruneLocked（:253–285可能删旧图），:111发事件。catch返回null不等于回滚此前写入。
- 因此“写前校验”只能决定是否开始；不能保证写期间取消零副作用。历史/剪贴板不是原子事务，不为新统一会话承诺撤销。

## 建议，尚非已批准产品语义

- 技术候选：在_sessionGate内以TryBeginCommit一次性lease裁决身份与取消；编码/配置/元数据准备在lease前，副作用在lease后恰一次。
- 产品契约需明确：取消在lease前赢→零外部写；lease后取消→已开始提交不可撤销；旧提交完成与新会话开始如何排序；两通道失败如何提示及记终态。当前不能默认为用户已批准。
- 建议测试：lease前取消、lease后取消、check-then-cancel、旧会话被取代、重复End、剪贴板重试耗尽与历史独立结果；使用纯规则/受控IO而非合成热键。
- bypass候选：创建/首次抢焦点前检查取消，可取消delay；返回对象或内部可靠清理；Dispose仅在helper仍是前台时恢复，否则只销毁，不抢用户新前台。

## lead裁决与下一步

本轮先落Core A1常量/文本/幂等回归，不改取消和外部写入行为；不把B2/B6静态候选升级为新已复现故障，不扩写历史/旁路文件。App接线前需要有边界的调用链复核和确定的提交/优先级合同。原先保护stale End的理由继续有效。

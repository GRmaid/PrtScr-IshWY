# P14b′ task79 服务首交独立静态审查
- 工位：maid-check，child 2bfae118-e643-4960-ae59-58ff14f70698；14:32回件。只读五输入，未运行build/test/App。
- 判定：needs_revision。task79行政收件关闭不代表task76实现通过；第1轮R1已派。
## 必修与观察
1. F1 blocker：src/PrtScr.App/Services/CaptureService.Quick.cs:91调用不存在的Coordinator.BeginCaptureAttempt。改实际TryTransition(Capturing,attempt:quick)并处理bool拒绝，不新增Core公共API。
2. F2 medium：src/PrtScr.App/Services/CaptureService.cs:238–263预留后CancelQuickWork/App.Log/Begin之前不受try/finally保护，取消回调AggregateException或日志异常可遗留reservation。按准确reservationId/generation全异常释放；Begin已生效后只End本代。
3. F3 low：IgnoredOutcome用Failure.None使Result.IsSuccess=true；R1改非成功，UI仍Ignored仅记录。
## 已核与父级补核
开槽在首await前，Current/Owner分离，Empty/stale End、OverlayCompleted保最近Current，Quick不拆Overlay；真实引擎元数据、granted-only并行双写、关闭历史null不算失败、单300ms均静态符合。父级读Overlay:630确认实际confirm-success，关闭猜测confirm-end映射问题。Core预抢占End先于旧Quick异步Dispose是静态事实，非正常本地End顺序通过的证明。
父级核Core Flush:509–512不吞logger异常，服务logger需非破坏性；CancelQuickWork两调用均在预抢占失效或Shutdown封口之后。
## 版本绑定
Service 8dcc31cbadc593b8c6047c779d0fdb8ea3a770866ed50cf706ebca6cfc681ea0；Quick 0552c7ba518a32182ff02b71da8ff2dd8479c8a778036d1a45775fc4e4652a09。Core 2AD66727D4ED6151191E420B8F4907B7AA4782219A5E0C2CA7748C6A3A2367F5。首末hash稳定；R1后旧结论不得自动沿用。

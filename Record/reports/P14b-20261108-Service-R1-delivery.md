# P14b′ 服务R1回件归档（2026-11-08）

## 状态与边界

task76 R1于14:45回件，14:46写者结束。只交付两个服务文件，未通过最终独立复核或构建门禁。task83服务/Core与task84服务/UI独立复核已派，均限五输入只读。

源码Freeze为131文件、相对13:30 dirty基线增量8项；lead已核对八个实施文件SHA256与回件/此前独立审查一致。禁止把冻结当放行。

## R1源码身份

| 文件 | R1前 | R1后 |
|---|---|---|
| src/PrtScr.App/Services/CaptureService.cs | 8dcc31cbadc593b8c6047c779d0fdb8ea3a770866ed50cf706ebca6cfc681ea0 | 5151eaa08bf3b9644958c9afb96415415696b03f9d1ea752bdba0eb9faf54a1c |
| src/PrtScr.App/Services/CaptureService.Quick.cs | 0552c7ba518a32182ff02b71da8ff2dd8479c8a778036d1a45775fc4e4652a09 | 04421cc9e5744892c33924388609362db1c078d568bc1793cc850f1b24995ab8 |

写者报告811/399行，CRLF无BOM；未改Core、Bypass、UI和公共API签名，未新增文件。报告原8dcc31…前的误留字符已由写者更正。

## 交付内容（写者主张，待独立核验）

- F1：Quick进入Capturing使用实际TryTransition(..., attempt:quick)，拒绝时按本代cancel收尾再取消；Overlay原void wrapper保持。
- F2：Overlay reservation至Begin消费放入try/finally；未消费按reservationId与generation释放，消费后异常只End本代；Core构造委托SafeLog隔离Flush日志异常，CancelQuickWork捕获全部Exception。
- F3：IgnoredOutcome保留Ignored disposition但Failure改Canceled，避免IsSuccess误真。
- O2/O3：旁路Activated读真实值，150ms等待前后各校验Current/token。
- 保持Normal+ScreenQuick、仅Granted latch发写入、双附属写入独立catch、正常Dispose早于本地End、单300ms退出预算及Core finished→state→result。

## 必须保留的判断边界

- 写者关于Failure枚举“唯一合理选项”的措辞不是枚举完整性证据；以实际代码及独立复核为准。
- 写者长报告“历史null”一句措辞不完整：只有关闭SaveHistory时null不应计失败，启用历史的null/异常仍需保留HistoryFailed；不能笼统声称null成功。
- generation在TryReserve后读取，写者登记并发风险；最终复核须按实际reservation互斥与ABA保护判断，不靠通常UI顺序外推。
- 未执行build/test/format/App/smoke/UIA、未动默认bin/obj、未提交。原1931文件Before保留；后续仍需原After。

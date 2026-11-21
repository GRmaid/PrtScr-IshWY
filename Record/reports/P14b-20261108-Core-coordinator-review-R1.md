# P14b′ Core独立复核与R1返工契约（task71→task67）

2026-11-08 13:51收到maid-check/a90c4ed5-8f91-4248-9c6f-e6bfb53f7c37：needs_revision，仅原5文件只读，未运行门禁/产品。工位已结束。

## 被审字节与证据

- Coordinator SHA256 F5261E03A41790A988668784FEE6C4E081FB4973942F4C4DA66B73AF494F894E，36109B；Tests B1A1A35F220155C34381888505CB03343F9F82B05AAE5552755C208C642CC9CC，38542B。
- CaptureService SHA256 69201B83821B4CC0AF67A1F6D35BE3668519695CEDDC09E08F79607AF7BEE81B；Overlay.Capture SHA256 0DA9240B1CDCB5640E14FA203C49428A53DC859BD56F4CF2B6C17045F7BF2206。
- Coordinator:885–898 End清current/owner/metadata；:112/501 IsBusy据owner判定；:531 Begin、:599 Reserve、:805 End、:872 Shutdown亦依赖owner。
- 旧服务:107–110/163 End仅释放占用/StateIdle而保留身份及元数据；Overlay:630先End、:637编码、:640查current、:646–654写copy/history、:662再查current。候选清身份会直接导致返回，无写入。

## R1必修（同单首轮，不改既有文件）

### B1/B2/H3 活动真源与最近身份

新增单一_active布尔（或等价活动字段）在同锁内，Snapshot明确Active事实，IsBusy=active或reservation。Begin/Decide/Reserve/End/Transition/Mark/latch/note/shutdown全部审计，不可继续把owner非空当活动。End清active/commit/lease、State=Idle，但保留最近owner/kind、结果元数据。Overlay成功End保留current至Invalidate或新Begin；Quick End可以清current，Owner仍供代际判断。下一代Begin覆盖最近身份并重置metadata。End返回释放前终态快照，之后Snapshot.State=Idle+原结果字段；无需再添一套公共TerminalState状态机。已结束同owner再次End只写not-capturing，不重复终态日志。

### H1 Empty启动期收尾不能拆新槽（技术合同收紧）

Guid.Empty的IsOwner=true、IsCurrent=false查询兼容保留。但**End(Guid.Empty)有活动非空owner时不清槽**，只记录忽略；仅无活动owner的启动期可按原无会话语义收尾。原合同“Empty兼容”在此明确收紧，优先满足旧失败不得清新会话的不变量；未来App仅用已分配的局部sessionId收尾，不用全局Current或default。替换原532–544固定相反行为的测试，不篡改旧CaptureRulesTests或CaptureRules。

### H2/M1 失效与已结束后禁止复活

Invalidate活跃未latch会话清current并记Canceled，保留owner供End；已结束的最近身份也可清current/打印invalidated，但不改变已结束metadata。MarkSucceeded/Failed及普通Transition必须active+current+owner且未latch；失效后拒绝且不能覆盖Canceled为None/Busy。latch后普通Transition拒绝，唯一收尾走End。

BeginShutdown始终须永久封口，即使没有active也可以成功封口；不是review中“End后BeginShutdown必须返回false”的字面建议。不得因最近owner存在把已结束metadata改成Canceled；可以失效最近未latchcurrent身份以阻止晚到写入，保留终态字段。latched活动保留到End。相关测试须明确查询身份变化与结果字段不污染的区别。

### M3/M4 API一致性

MarkSucceeded/Failed先验证CaptureRules.CanTransition目标，非法时返回false且不写metadata，不得准备态伪报成功；保留合法旧调用日志。Decide(StartOverlay)在实际有reservation但调用未给/给错时与Begin拒绝理由一致；无任何reservation而意图先申请预留时Decide只作advisory，不可用于绕过原子Try方法。所有正确性仍在Try锁内。shutdown后重复已latch可保持AlreadyLatched，但文档/测试明确幂等不是新发写入许可。

### 必补确定性测试

1. 成功Overlay End之后IsCurrent/IsOwner=true、Busy=false、StateIdle且metadata保留；Invalidate只清current；下一代Quick及Overlay替换身份，旧End/旧Release不改新代。
2. End后迟到Mark*/Transition/latch/note被拒，第二次End不重写结果。失效未End时Mark不能抹Canceled。
3. Empty End不能清活动新owner，保留查询兼容。
4. Quick在Capturing及已MarkSucceeded但未latch的Committing可原子抢占；latched不可抢。
5. commit-v-preempt、shutdown-v-commit分别做Barrier确定性竞争且断言互斥合法结果，无Thread.Sleep/Task.Delay猜时序。原Reserve-v-Quick测试保留。使用线程安全日志容器或安全收集，不引并发List写入。
6. 非法准备态MarkSucceeded返回false且metadata不变；上述shutdown空槽/结束后/latched三个分支明确。

## 未判作运行验收的通过项

单锁、预留ABA和消费、latch、双附属Failure主值+Detail、锁外日志buffer、原服务六类日志文本及finished→state→result时序在初交静态可核。旧already capturing与hidden Dismiss仍留App/Overlay，不能宣称Core全部包含。

## 限制与交付

R1只写src/PrtScr.Core/CaptureSessionCoordinator.cs与src/PrtScr.Core.Tests/CaptureSessionCoordinatorTests.cs，CRLF无BOM。不改App/CaptureRules/既有Tests/Smoke/依赖/Record/scripts，不build/test/format/App/UIA/P11/commit/HTML。先读本报告+两实施文件+CaptureSessionState.cs+CaptureResult.cs共5文件，额外输入先报。lead统一门禁，R1后按新hash独立复核；最多两轮，不靠降低验收过关。

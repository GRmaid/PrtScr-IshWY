# P14b′ Core协调器首次交付（task67）

- 工位：maid-impl / 924d1c73-cde1-4953-9b59-7934f4f4a6b0，13:49交付。
- 状态：已收件，**未验收**；未build/test/format/App运行。只新增Coordinator及Tests，现有dirty文件未改。

## 身份

| 文件 | SHA256 | 大小/行数 |
|---|---|---|
| src/PrtScr.Core/CaptureSessionCoordinator.cs | F5261E03A41790A988668784FEE6C4E081FB4973942F4C4DA66B73AF494F894E | 36109 / 980 |
| src/PrtScr.Core.Tests/CaptureSessionCoordinatorTests.cs | B1A1A35F220155C34381888505CB03343F9F82B05AAE5552755C208C642CC9CC | 38542 / 904 |

工位报告CRLF无BOM；完整API及逐项测试说明保留在本会话13:49成员消息。该消息中“旧Owned/Current语义不变”尚未成立，lead已发现成功End后的身份兼容缺口，待独立task71和同单返工。

## 已实现候选

单锁Begin、原子Quick失效+End+Overlay预留、代际释放、current/owner判断、state/metadata、提交latch与双附属失败、End终态映射和BeginShutdown。Snapshot含Kind/state/result/lease/reservation/generation/shutdown。日志锁外buffer输出，单次End保持finished→state→result。ShutdownGrace=300ms仅常量不等待线程。

主要API：Begin(Guid,CaptureSessionKind,Guid=default)；TryReserveOverlay(out Guid,out Guid)；ReleaseOverlayReservation(Guid[,long])；IsCurrent/IsOwner/Invalidate(Guid)；TryTransition(Guid,CaptureSessionState,string)；TryMarkSucceeded(Guid,CaptureOrigin,CaptureEngine,bool=false)；TryMarkFailed(Guid,CaptureFailure,string?)；TryLatchCommit(Guid,out CaptureCommitLease)；TryNoteCommitFailure(Guid,CaptureFailure)；End(Guid,string/CaptureEndReason)；BeginShutdown()；Snapshot(Guid)/Snapshot()。

## 初交测试与未覆盖项

工位说明包含Begin/reserve/release/latch/Invalidate/End/shutdown/metadata/Decide/附属失败的规则测试；一个32轮Barrier测试竞争ReserveOverlay与BeginQuick，不sleep。所述“33个测试”与分组计数并非可靠总数；未执行discover/test，不记作通过数。原343为最后实际门禁基线。

冻结契约还要求commit-v-preempt、shutdown-v-commit的二选一确定性证据；交付摘要未列出，应由独立复核确认并补齐，不能用上述Begin-v-reserve替代。

## lead已发现必修兼容

旧CaptureService.cs:107–110明确成功End不清current；Overlay Confirm:630提前Finish而:637/640/646/662仍编码/检查/写入。候选ReleaseSlotLocked:885–898清Owner与Current，且IsBusy用Owner非空推导，无法保留此契约。需活动槽标志与最近身份分离，不能在App另存Current真源；补结束后身份/Busy释放/下一代替换/旧End安全测试。详见接口冻结补充。

给原一次性工位的send_message未送达：active teammate not found。未据此声称对方已收到修订。待其结束及task71回件后发同单R1，最多两轮；不在其尚写时另开重叠写者。

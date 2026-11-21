# P14b′ App接口综合裁定（task65/66，2026-11-08）

## 收件与边界

13:34收到 task65（58f519b5-25b6-47d4-a931-1a9545173541）及 task66（41af45b0-0277-47ea-a600-06f41877f682）只读报告；各读原指定5文件，未改源码/运行门禁或App。报告是静态设计建议，不是代码验收。原始报告保留于本会话13:34两条成员消息。

## 已核对批准原文

Record/plans/P14b-20261108-commit-decisions.md:15–19是五项原文。300ms为退出最多额外等待的总预算，不是托盘Dispose/Win32调用的硬实时上限。F1指真正进入取景的意图；现有Quick配置/游戏自动路由不被此次统一会话暗改为强制取景。用户已在不可逆提交前批准取消，取消后仅日志、不发成功提示，不新增提示文案。

## 合并决定

1. 采用task65 Core CaptureSessionCoordinator：唯一会话槽、Current/Owner分离、surface、commit latch、reservation、终态与元数据。拒绝task66另建QuickCaptureCommitGate，拒绝保留独立_quickCaptureBusy真源。
2. 开槽/预提交取消/提交权/抢占/退出封口在coordinator同一同步边界。Decide仅提示，不能把Decide后另一次检查当原子动作；增加原子抢占Quick并预留Overlay能力，禁止取消与预留间被F2抢槽。退出封口对所有新入口生效。
3. 六个入口最终汇入服务；App保持Normal分类与HistoryCaptureMeta.ScreenQuick，不改历史/剪贴板服务。Quick的结果engine应沿用真实引擎元数据，不臆造引擎；详细落点等调用链补核。
4. 无图失败沿用Unavailable与detail=LikelyBlack/AllEnginesFailed，不扩CaptureFailure。Completed保留ClipboardFailed/HistoryFailed；同时失败在detail保留两个原因，Failure单值优先级需明确且有测试。
5. Quick收尾绕开Overlay专属Hotkey.EndCaptureMode/NotifyOverlayClosed；原CaptureService对外Overlay接口保持兼容，hidden Dismiss六日志保护纳入复核。
6. FullscreenBypass采用逐焦点变更的owner/取消保护，不采用只在入口检查一次；旧重载保留。task66的15ms排水、Park表是候选，不当作已批准/已证安全。必须补核线程亲和、前台helper销毁、停放回收边界；不承诺同步Win32硬超时。
7. 退出只接用户退出漏斗，致命启动/非首实例/P10 SmokeSession出口不改；同步失效先于await，单个总额300ms额外等待，不等待不可控采集线程。额外入口需补核，不假设App.xaml.cs所有Exit都属用户退出。

## 实施工序

- Core工位先新增Coordinator及确定性测试，不执行构建；原343基线和A1两枚举保持。
- 旁路工位只读补核5个额外文件，返回焦点/退出可执行契约。
- 接线工位只读补核日志、写入异常与UI路由5文件，返回精确服务到UI签名。Core完成后再派单一App实现写域，旁路实现另单互不重叠。
- 所有报告由lead归档Record；源码由Flash工位，门禁由lead冻结树后统一跑。P11仍暂缓，不运行App/smoke，不覆盖默认产物，不提交发布。

## Core实施接口约束（task67）

采用task65的CaptureSessionKind、CaptureIntent、CaptureArbitration、CaptureCommitLease和CaptureSessionSnapshot；Snapshot必须同时表示Current/Owner/surface/state/result字段/commit/reservation/shutdown事实。命名可增补但须在交付中逐个报告最终签名，App实施尚未派出。

- 一个CaptureSessionCoordinator(Action<string> log)为唯一槽/状态/元数据真源；公开IsBusy、State、CurrentSessionId、OwnerSessionId、OwnerKind、IsCommitLatched、IsShuttingDown，读写都受同一同步边界。原服务五保护日志的原文/finished→state→result顺序应逐字保持；UI teardown不进入Core。
- Begin(Guid sessionId, CaptureSessionKind kind, Guid reservationId=default)返回快照或null；Quick仅空槽无预留且未shutdown可开。Overlay必须消费本次有效预留，不允许任意Guid抢槽。
- TryReserveOverlay(out Guid reservationId, out Guid preemptedQuickId)把判断、未latch Quick失效/End(Cancel)、空槽预留原子完成；遇活动Overlay/已latch Quick/其他预留/shutdown返回false。ReleaseOverlayReservation(Guid reservationId)按代际匹配释放，旧释放不能清新预留；reservation本身使IsBusy为true。Decide只能做提示，正确性不可依赖先Decide再另一次未校验变更。
- 保留IsCurrent/IsOwner、Invalidate(Guid)、TryTransition(Guid,to,context)、TryMarkSucceeded(Guid,origin,engine,timedOut)、TryMarkFailed(Guid,failure,detail)、Snapshot(Guid)、End(Guid,reason)。Owner与Current的不同、Guid.Empty兼容以及原Duplicate/Invalid决策从现服务和CaptureRules保留；晚到操作不能改新槽。
- TryLatchCommit(Guid)：只有current+owner+Committing返回Granted；第二次AlreadyLatched，其他Denied。latch后Invalidate/抢占不得取消；收尾仍由owner完成。
- TryNoteCommitFailure只接受ClipboardFailed/HistoryFailed，拒绝None及不属本owner/current或未latch；首次失败为Failure主值，同时失败用Detail保留两项且不覆盖第一项，不发中间result；图像Completed的语义不等于Failure==None。
- BeginShutdown()原子永久封口所有新开槽/预留并使未latch活动会话失效，已latch保留至收尾；旧预留释放不能重新开放shutdown。仅纯规则不等待线程；静态ShutdownGrace=300ms为协作等待单总预算。
- 不能增加第二套QuickCaptureCommitGate或_quickCaptureBusy；不改既有CaptureRules/枚举/场景/Smoke协议，不给Core引UI、Drawing、Capture或OCR。
- 测试必须覆盖ABA旧reservation release、原子preempt+reserve期间F2拒绝、commit-vs-preempt与shutdown-vs-commit二选一、旧End不清新槽、Invalidated仍可正常End，以及terminal/日志/附属双失败；使用确定性屏障，不sleep猜时间。

## 13:49补核澄清：活动槽与结束后身份不可混同

CaptureService.cs:107–110明确成功End不清current，Overlay Confirm:630先End后编码/写入:637–662，因此结束后的IsCurrent/IsOwner必须继续代表最近一代身份，直到Invalidate或下一代Begin替换。不能用owner非空直接表示活动忙，也不能为兼容在App另存第二current真源。协调器须另区分活动槽标志与最近身份；End释放活动占用/commit并置Idle但保留最近Overlay身份，旧End见无活动或新owner时不改新槽；Quick结束可以失效current，owner仍作代际归属。快照IsBusy必须读活动/预留而非从owner推导。补确定性用例：成功Overlay End后IsCurrent/IsOwner仍真且IsBusy=false、Invalidate后owner仍可辨、下一代Quick/Overlay开槽替换身份并拒绝旧End。原task67报件后若未满足，按同单返工不另建第二仲裁器。

## 目前保留的未知项

- 不能用readonly建议或纯规则测试宣称真实F1/F2交错、焦点恢复和退出时长已通过。
- task65把旧日志计数与顺序写成L1…L5并混列八模板，须以实际六保护文本复核；task66退出位置449可信于其读取，task65的260为未读猜测，撤回后者锚点。
- task66建议测试目录tests/PrtScr.Core.Tests及<capturedir>是未读占位，实际只使用src/PrtScr.Core和src/PrtScr.Core.Tests。

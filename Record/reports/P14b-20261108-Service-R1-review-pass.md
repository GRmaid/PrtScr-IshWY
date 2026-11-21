# P14b′ 服务R1独立复核（task83）

## 判定

14:50回件：五输入限定静态PASS，0必修；首末hash一致，lead核对五hash与131源码Freeze完全匹配。只关闭本次只读审查，不完成实现单或运行验收。

| 路径 | SHA256 |
|---|---|
| src/PrtScr.App/Services/CaptureService.cs | 5151eaa08bf3b9644958c9afb96415415696b03f9d1ea752bdba0eb9faf54a1c |
| src/PrtScr.App/Services/CaptureService.Quick.cs | 04421cc9e5744892c33924388609362db1c078d568bc1793cc850f1b24995ab8 |
| src/PrtScr.Core/CaptureSessionCoordinator.cs | 2ad66727d4ed6151191e420b8f4907b7aa4782219a5e0c2ca7748c6a3a2367f5 |
| src/PrtScr.Core/CaptureSessionState.cs | b091836af9bb4698131f136326733daafb22852ee12a618aec8445f2931a4b0b |
| src/PrtScr.Core/CaptureResult.cs | cdcea01635e0e5cb5e7d0c1477845f3761330aa84239e0eb64db71dd9608a75c |

## 核验闭环

- Quick:88/94使用真实TryTransition；Capturing拒绝:96→98→99本代End/cancel/Canceled，无新增Core API。
- Service:246–295保留精确reservationId/generation/consumed；:360–367 finally精确释放，Core:634–657核Guid与generation拒ABA旧释放。new(SafeLog)使Core Flush日志异常不外抛，CancelQuickWork全异常隔离。
- Ignored用Canceled而非None，旁路.Activated真实值与Begin/Delay/retry/encode后身份检查保留。
- Overlay Completed End保留Current、Quick End清Current；仅Overlay terminal拆热键/通知；Empty不End活动槽；仅Granted latch写入；并行写各自捕获；退出单300ms额外等待。
- 正常Dispose早于Quick本地End；抢占End在Core原子区，晚到Dispose只作旧代清理，两者不混淆。

## 非必修与边界

非Granted latch返回无End、取消catch依赖取消方先Core封口、最新CTS字段取消等依赖当前无await/UI入口及唯一Quick不变式；未来扩线程或插await须重审。单参reservation Release无本次消费者，不改变冻结Core。generation另取值在SafeLog非抛出合同下未发现必修；退出等待日志仍可fault ShutdownAsync，但UI finally保留。

Bypass、Clipboard、History、Overlay取消实现不在本次五输入内；须结合此前task73/81及task84与lead已读Overlay的补核，不据此报告声称外部实现已验证。审查[I]标题“历史null非失败”不可笼统使用：启用历史null记HistoryFailed，关闭历史不误判失败。

未编辑、build/test/format/App/smoke/UIA或提交；未扩读。原始回件见当前会话m04024。

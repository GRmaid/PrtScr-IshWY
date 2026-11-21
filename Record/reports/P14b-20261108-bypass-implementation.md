# P14b′ FullscreenBypass 实施收件（task70）

- 状态：实施已交付，尚未独立验收/编译/运行。
- 工位：maid-capture，4caf5b9f-6dd2-4c70-bbe9-4b299fcb51bb；13:53交付。
- 唯一写域：src/PrtScr.App/Services/FullscreenBypass.cs。

## 交付与身份
新增 owner Guid + Func<bool> + CancellationToken 重载，保留 BeginAsync(bool force=true)，新增 IsCanceled。每个等待结束与显式焦点操作前检查所有权/取消；保留60/60/500ms正常算法。失效只清helper，不显式恢复旧前台。Dispose幂等，记录创建线程/DispatcherQueue，跨线程派发回创建线程清理；派发失败保留句柄并记录残留。

- 修改前：11037B/283行，SHA256 75FA8C223A31C4AF342C8E98478DF5156B7D28295509455550944FBBA8405689。
- 修改后：20184B/545行，SHA256 993B88F028FF1DD74D59513BFED16AE058E10364983B50E916FE0871986CAB0B。
- 工位报告CRLF无BOM，无其他源码修改、无门禁/App操作。

## 独立复核入口
核对GetForCurrentThread允许null与字段可空性；成功Dispose恢复在服务End之前；失效/派发后的重新校验；DestroyWindow线程亲和；异常与入队失败后句柄不能被伪清。不得加入第二个会话门/15ms排水/Park表。

## 未验证边界
尚无实际编译、线程/前台运行时证据；helper销毁可能触发系统自动激活，不能把无显式恢复等同于完全不改变前台。队列不可用、销毁失败可能留helper，必须保留真实日志。新增300ms仅属于后续用户退出的额外合作等待，不是本文件或现有Dispose硬实时承诺。

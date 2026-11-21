# task73 / FullscreenBypass 独立静态PASS

14:11收到bf32b6c6-30bf-48bb-9d67-f61918ff4be5只读五输入报告，0必修。源码20184B/545行、CRLF无BOM，首末SHA256 993B88F028FF1DD74D59513BFED16AE058E10364983B50E916FE0871986CAB0B，lead再核对一致。

## 核对
旧BeginAsync(bool force=true) API面保留；owner/token新重载逐等待及显式焦点操作前检查，60/60/500结构保留。取消清理allowRestore:false；DestroyWindow只在创建线程Cleanup，跨线程TryEnqueue回调现场重判归属；队列缺失/入队失败/Destroy失败保留真实handle并日志，无伪清。Dispose Interlocked幂等并吞内部异常。

## 必须落实的接线前置
1. 六条入口实际在带DispatcherQueue的UI线程创建helper，不能外推所有后台调用安全。
2. 正常成功Dispose在创建线程执行且位于服务End之前；跨线程排队返回不表示恢复已完成。
3. 没有硬实时保证。系统销毁前台helper后的自动激活、SetForeground实际成败、异常残余句柄均待用户实测。取消不显式恢复不等前台完全不变。

## 非阻断观察
ForceForeground未消费每项Win32返回，成功归还日志可能偏乐观；GetLastWin32Error可能陈旧；HandleOf未使用。DispatcherQueue声明非nullable但GetForCurrentThread可能为null，当前五输入未证编译警告，统一0warning门禁必须核查，不提前宣称编译绿。旧字节与旧调用链不在五输入，只证明结构/API面，不证明完全逐字行为兼容；完整dirty范围由lead最终源清单核对。无build/test/format/App运行证据，P11不恢复。

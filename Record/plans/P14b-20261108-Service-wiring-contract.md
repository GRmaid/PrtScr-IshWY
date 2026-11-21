# P14b′ 服务接线实施合同（Core/旁路/UI静态PASS，服务R1已交待最终复核）

状态：五项产品已批准，此页限定技术接线，不代表整体验收。14:46服务task76 R1回件并结束写入，task83服务/Core和task84服务/UI最终哈希复核中；Core R1/task74、Bypass/task73与UI三文件/task82已有各自独立静态PASS，task81仅IO范围静态PASS。131源码Freeze、8项dirty增量匹配交付；实际完整隔离门禁尚未运行。Core/Bypass/UI不由服务R1抢写。P11暂缓、不启动App/覆盖默认产物/提交发布保持。

## 服务写域与最小接口

- 唯一服务工位写src/PrtScr.App/Services/CaptureService.cs；如单文件过长可声明partial并新增CaptureService.Quick.cs，但先固定写域，不得改MainWindow或Overlay。
- 保留现有公共/内部Overlay API签名，由单个CaptureSessionCoordinator字段承载活动/Current/Owner/元数据/状态，删除重复_isCapturing/_captureSessionId/_lastSessionId/_captureState等真源。CTS、正在运行Task、WGC monitor属于资源/等待句柄，不得据其决定业务忙。
- 给UI显式只读IsOverlayCapturing（活动Overlay）及IsCaptureBusy（活动或reservation），IsCapturing的既有调用需逐项映射，不让Quick占槽把Quick-profile主键误改为强制Overlay。
- 新Task<QuickCaptureOutcome> StartQuickCaptureAsync()在首个await前原子开槽，不新增取图前await/Task.Run。Outcome区分Ignored/Canceled/Success/Failed，携带Png字节数、QuickCaptureStatus、Copied、HistorySaved/Path、BypassActivated、CaptureResult供UI沿用提示；UI不直接采集/复制/写历史。
- BeginShutdown()同步封口、取消预提交；ShutdownAsync()只做本轮新增长任务的合计最多ShutdownGrace=300ms等待，不等待底层不可控线程。UI退出在await之前同步调用BeginShutdown，真正Exit_Click之后仍保留现有TrayIcon/Hotkey Dispose/ExitProcess。

## Overlay兼容与预留

- StartCaptureAsync仅代表真正Overlay意图：活动Overlay按旧文案记录already capturing并取消；latchedQuick忽略不排队。Quick未latch时原子preempt+reserve后取消旧CTS，Overlay消费reservation，在任何取图await前成为当前活动Preparing。
- 提前按F1取消仍在Preparing的Overlay也要使当前owner失效和End；不能仅调用尚持有旧session的_overlay.Cancel导致新Preparing永远忙。所有catch/finally用局部Guid，不用此时的全局Current清新槽。
- 每个await后核当前/owner，再交frozen帧、Prepare或注册Esc；失败释放reservation/Bitmap且End只作用本代。无效/已抢占工作不发成功提示。
- End先保存ownerKind和本次是否实际完成的事实；仅成功结束活动Overlay才调用Hotkey.EndCaptureMode与NotifyOverlayClosed；Quick/旧End不得触碰。Core保留invalidated、finish ignored/not-capturing、finish ignored/stale、finished四类服务保护文本；服务保留already capturing文本，第六类Overlay hidden Dismiss完全不改。终态finished→state→result顺序保持。
- 旧Overlay Confirm先Finish再编码/写入，Core必须保留最近Overlay current身份直到下一代/Invalidate；不能为本次Quick合同偷改Overlay提前End、Save、OCR语义。

## Quick采集、提交、附属失败

1. BeginQuick→MarkPrepared(Selecting)→BeginAttempt(Capturing)，沿用首次直接抓→不可用才旁路→150ms重试的算法，不加取图前thread-hop。owner失效后只释放载荷。
2. CaptureMonitorBestAsync需要随真实成功分支返回engine：WGC→Wgc、DXGI→Dxgi、BitBlt CAPTUREBLT/普通→BitBlt，前景WindowCapture结果不得硬写成Wgc：lead已读WindowCapture.cs:12–18/42–83/195–198，旧CaptureAsync只是CaptureDetailedAsync取Bitmap，真实Source只有PrintWindow/Wgc/None；用详细结果将PrintWindow→CaptureEngine.PrintWindow、Wgc→Wgc、未知→Fallback，并传递PrintWindowTimedOut。Bitmap所有权由返回方转交，黑帧/取消晚到必须释放。不得改引擎顺序/阈值、给Capture引Core引用或改WindowCapture.cs。
3. 静态CaptureFullScreenPngAsync旧调用只由服务复用；CT不能终止底层抓取。取得晚到Bitmap必须Dispose；编码前后再校验。无图Unavailable+LikelyBlack/AllEnginesFailed，QuickFailed不写历史。取消结果不显示新提示，不发成功。
4. 成功MarkSucceeded(Normal,actualEngine)到Committing后TryLatchCommit，仅Granted启动不可逆写；AlreadyLatched不能重复执行。latch通过后保持槽直到clipboard/history与旁路归还清理结束。
5. 仅服务调用ClipboardService.SetImageAsync(png)与CaptureHistoryService.Instance.AddAsync(png,HistoryCaptureMode.Normal,meta:HistoryCaptureMeta.ScreenQuick)。正常仍沿用并行copy/history流程；每项wrapper独立捕获异常、记录ClipboardFailed/HistoryFailed，不因另一项抛异常提前End遗漏正进行的写入。首次记录为主Failure，双失败Detail都有。
6. SaveHistory=false的null不是HistoryFailed；尝试历史前读取启用状态，不修改HistoryService。已启用null也无法知道PNG是否已落盘，记通用HistoryFailed，不声称回滚。元数据内部吞异常仍按现服务观察到的结果，不扩历史JSON。
7. Completed是采集完成，保留附属Failure/Detail，不依据CaptureResult.IsSuccess给用户所有步骤成功的错觉。旁路Dispose需在当前Quick End之前正常还焦点，已取消/新代则只能清helper。Finally必须晚到安全、不清新槽、不注册/注销Overlay热键。

## 后续UI单

服务冻结后，另一单仅写MainWindow.Capture.cs、MainWindow.Hotkeys.cs、MainWindow.Windows.cs。六入口最终进入新服务，删_quickCaptureBusy字段及Interlocked/Volatile真源；保留已有文案、300ms去抖和fallback先TryClaim再Notify。OnHotkeyPressed先处理延时取消、已有Overlay取消，再按既有游戏/Quick配置路由（Quick占用不得强制取景）；只有真正Overlay意图能抢占Quick。主窗关闭仍隐藏，App.xaml.cs所有出口不改。

## 交付与门禁

先报告最终签名/各触及文件hash、资源所有权、await时序、未知；不由工位build/test/format/App。lead冻结源后独立复核、完整隔离Build+343及新增测试+format，原Before快照保护默认bin/obj；保持P11暂停，无发布、真实数据或实例操作。

# P14b′ UI路由与用户退出只读补核：收件与lead裁定

## 来源与流程边界

14:25收到task78（765ac34a-9d00-4958-b7f9-208a6d7c3186）只读回件，未写或执行App/门禁。初始五输入为服务合同、App矩阵及MainWindow.Capture.cs/Hotkeys.cs/Windows.cs。工位自述未事先申请即扩读MainWindow.xaml.cs、App.xaml.cs、MainWindow.CaptureSettings.cs、ToastService.cs、HotkeyService.cs、EscOverPinHook.cs并grep Core两文件，违反本单“更多输入先申请”的流程约束；据实保留，不将扩读倒签为已授权。技术材料可供lead核验，不能以本回件声称通过最终实现验收。后续单必须遇缺输入先停报。

## 可采用的接线映射（仍待最终服务签名）

- 四处旧IsCapturing均只表达Overlay：MainWindow.Capture.cs:130主键取消、MainWindow.Hotkeys.cs:44 Esc、MainWindow.Windows.cs:388 Pin/:419 OCR，均映射IsOverlayCapturing；本单不改成IsCaptureBusy，避免把Quick占槽误当取景已开或扩大Pin/OCR禁用。
- 六用户触发链：截图hook/截图fallback、全屏hook/全屏fallback、菜单/首页、延时；直接服务调用点为三Overlay加一Quick。保留hook/fallback的claim→Notify→Dispatcher顺序和300ms去抖，Quick-profile/游戏路由保持。
- OnFullScreenPressed仅await服务一次Outcome后做UI；删独立busy/直接取图/剪贴板/历史/旁路。Ignored/Canceled静默，Failed按既有LikelyBlack或采集失败文案；图像完成仍按Copied用现有复制成功/失败文案。Copied=false用原失败toast，Copied=true但HistoryFailed抑制成功toast并保留Failure/Detail日志，不新增提示文案或新增Info面。历史禁用/null不能由HistorySaved==false直接判失败。
- 真实Exit_Click加一次性_exitStarted；首await前同步服务BeginShutdown和delay取消，随后仅一次await ShutdownAsync。finally保持TrayIcon/Hotkey.Dispose/ExitProcess原漏斗；不改关闭隐藏/App致命/单实例/Smoke出口。delay/主键/全屏入口加退出守卫，不新增第二预算，Hotkey内部Join1500仍不在额外300ms内。

## lead纠正与保留未知

1. 工位称“被调用静态方法内部ConfigureAwait(false)会使UI调用者await后跑池线程”，此推断不成立：调用者自己的await默认捕获其上下文，callee内部false不自动传播到caller。真实WinUI同步上下文仍需运行验证。
2. 不采用“为了线程保证把Bypass Begin挪到首await前”：首次直接抓→仅不可用才旁路的算法必须不变。正确约束是服务在UI入口且自身后续流程保持UI上下文；Bypass实际Begin/正常Dispose在创建UI线程，Dispose在本Quick End前。不得因此在首次抓取前新增旁路、await或thread hop。
3. 工位称“Delay已完成但continuation未执行时Cancel仍必抛OCE”不准确。已完成Task.Delay不保证再取消改结果；_exitStarted与服务同步shutdown封口必须兜住已排队晚到，不依赖该说法。
4. _hotkey.Dispose Join1500与新增300ms相加只说明额外等待的组成，不能给出整个退出1.8s硬上限；TrayIcon/Win32/线程调度仍未知。
5. 最终Outcome枚举/字段、History失败观察、Preparing Overlay取消以及服务内部同步封口仍待task76实交；菜单线程与焦点/退出真实时序未验。不把只读建议或旧行号直接认定新代码已满足。

## 后续写域

等task76服务冻结后派单，只写MainWindow.Capture.cs、MainWindow.Hotkeys.cs、MainWindow.Windows.cs；初始五输入须包含实际冻结Outcome及必要合同，不抢写服务/Bypass/Core/Overlay，不扩读不构建。整体审查与冻结门禁前不操作用户App/默认产物，P11暂停与提交发布hold不变。

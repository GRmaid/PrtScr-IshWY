# P14b′ 旁路与退出补核（task68）

- 工位：maid-capture / a7ee4da4-9749-4520-9dd0-f5bc09d6b6ab。13:44交付并结束。
- 范围：原指定5文件，只读、未运行构建/测试/App；这是静态补核，不是产品通过。

## 补核结论

- MainWindow.xaml.cs:77–81关闭仅隐藏，不接用户退出收尾；HotkeyService.cs:397–411现Dispose含Join(1500)，Restart:96–100亦可能等待。现有同步耗时不能被新的300ms协作等待掩盖。
- HotkeyService.cs:84–93/118–127/325–347事件在热键线程触发。MainWindow.Hotkeys.cs:108–119由lead另读确认已有DispatcherQueue转UI；不能据前一事实误判现有所有调用都未转线程。
- HotkeyService.cs:163–175的Begin/EndCaptureMode是消息式调用，Quick收尾必须完全绕开，不能晚到注销新Overlay的Esc。
- CaptureOverlayWindow.xaml.cs的Closed、定时器/focusRetry存在原有代际候选；本单未读Cancel/Finish，不能判定完整行为、不得借此次Quick接线顺手重构。
- 工位建议保留旧BeginAsync，并加Guid owner、Func<bool>当前所有权、CancellationToken；每次await后及每次焦点调用前检查。helper必须在创建线程销毁；撤销后不得恢复旧前台。

## lead裁定与范围限制

1. 采纳旁路owner+token逐操作保护与线程亲和约束。不采纳未证实的15ms排水、Park表或第二Quick gate。销毁前台helper导致的系统自动焦点选择仍须真实验收。
2. 退出只接MainWindow.Windows.cs:449–456的真实Exit_Click。该位置由task66和lead另读确认，task68未读，不能归作本单直接证据。
3. 300ms是**新增协作等待的一个总预算**，不是现有TrayIcon/Hotkey Dispose或所有Win32的总硬时限。因此不采纳本单要求顺改HotkeyService.Dispose的建议；其原有1500ms、消息查表/异常候选转作范围外风险，不扩本轮写域。
4. App.xaml.cs非首实例/致命启动/Smoke出口不变，关闭主窗仍隐藏。不得用静态补核或Core测试冒充焦点/退出时长实测。

## 下一步

旁路实施单只写FullscreenBypass.cs；统一会话实施单只写服务与MainWindow入口，互不抢写。最终独立复核再核线程亲和、撤销后无显式旧焦点恢复及单预算用法。

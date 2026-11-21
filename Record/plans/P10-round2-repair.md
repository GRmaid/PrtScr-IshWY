# P10 第2轮最终返工裁决

2026-11-07 22:34。依据 Record/reports/P10-round1-independent-review.md 的 F1/F2，首轮 verdict=needs_revision。第2轮为本P10单最后一次返工；再未通过须上报，不自动第3轮，不换人重置次数。真实App、publish/产品ZIP及Actions仍hold。

## 目标与裁决

- 两个阻断是同一半构造根因的两个验收面：InitTray吞CreateTrayIcon异常后继续返回；五字段null!会让OnLoaded中断，或在StartDelayedCaptureAsync的try外抛异常，令_delayCts永久非空。
- 采用恢复构造失败关闭的最小方案，不选择全程序nullable降级。CreateTrayIcon创建/入树阶段失败必须向MainWindow构造调用者传播，禁止窗口返回后继续InitHotkey/正常Loaded。先局部完整构造，再发布字段，避免逐段赋值的半成品。应对构造/入树失败提供有诊断的日志与既有关闭漏斗，必要资源清理不得掩盖原异常。
- 保留HEAD原有ForceCreate及后续命令设置失败的catch容错，不把已存在的初始化容错一并改成退出：HEAD InitTray只将ForceCreate、左右键命令、ResetTrayToolTip、Opening日志放在try内。新增CreateTrayIcon应在这条既有catch之外，或用等价、可证明的异常分界。正常成功路径10菜单/文案/事件/初值/图标/提示/退出不变。
- lead校正报告引用：从MainWindow构造传播的异常实际由src/PrtScr.App/App.xaml.cs:151-188 OnLaunched漏斗处理（new MainWindow在153；正常ShowFatal/Exit在186-187）。报告所称App:47-59是App自身InitializeComponent，不是MainWindow的调用漏斗；保留历史报告，不覆写。
- 冒烟仍绝不创建TaskbarIcon，不恢复XAML托盘；三真实事件、20s只失败看门狗、单实例策略、协议schema1/8字段保持。dataRoot语义仅记录，不改字段名或与P11争写接口。

## 写域与硬边界

唯一产品写域：src/PrtScr.App/MainWindow.Windows.cs。必要说明写自己的Record/reports/P10-round2-repair.md；验证临时文件仅.tmp/p10r2*/，不可运行产品。若必须扩大到其他产品文件先报告，不能擅改。
禁止其他src、scripts/CI、docs/README、旧报告、ProjectLog、.gitignore、依赖/配置字段名、删除既有文件、子代理、commit/push/tag/Release、真实App/--smoke、publish/产品ZIP/Actions、UIA/合成输入、激活或终止用户实例、ACL/提权。C#保持CRLF，不为了消除diff噪声手改全仓行尾。

## 验收与证据

1. 逐段证明菜单/TaskbarIcon构造或RootGrid入树任意抛异常时，不可能返回半构造MainWindow，不会进入全局热键初始化或正常Loaded/延时截图；故UpdateCaptureOptions/StartDelayedCaptureAsync不会对该半成品访问，_delayCts不会因此楔死。仅局部构造而仍吞异常继续启动不合格。
2. 正常构造成功时五字段全部有效，所有既有菜单、托盘/延时/退出语义相同；ForceCreate原容错边界不变。冒烟guard/真实事件/单终态不可回归。
3. 新修复给出可重复的非GUI回归检查/静态异常分界证据；不为可测性重构无关Core，不新增生产故障开关，不用真实托盘或合成输入做故障注入。若无法在纯测试环境构造WinUI失败，如实列静态证据及运行缺口，不伪称执行过。
4. 允许经.tmp/build-guard.ps1串行整包Build（隔离OutDir，不默认bin、不-t:Compile）、Core.Tests、format whitespace verify；报告准确命令/退出码/测试总数/原始日志位置。工位三绿不是lead冻结树验收。
5. 五段式报告：F1/F2闭环、精确路径行号、HEAD和本轮diff范围、实际命令/原始证据、未测及残余风险。F3 root-unreadable等非阻断仅在本报告补充，不写旧报告、不改接口。交付后独立task58复核；两侧安全pass才可能受控运行。

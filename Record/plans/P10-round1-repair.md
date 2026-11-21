# P10 第1轮返工裁决

2026-11-07 21:37，依据 Record/reports/P10-P11-independent-review.md。静态安全needs_revision，尚未运行App smoke/实际publish/ZIP。此前停工仅只读准备，不占本轮返工次数。

## 写域与禁止项

唯一产品返工工位：src/PrtScr.App/App.xaml.cs、MainWindow.xaml/MainWindow.xaml.cs/MainWindow.Windows.cs（仅必要托盘生命周期接线），src/PrtScr.Core/SmokeProtocol.cs及Core.Tests相应最小补测，自己的Record/reports/P10-round1-repair.md。其他src保留原样；scripts/CI由并行task51负责，不得写入/撤回。无新依赖、README/docs/ProjectLog修改、commit/push/tag/Release、子代理、App smoke、publish/打包、合成输入/UIA、杀用户实例。修改C#保持CRLF。

## 必修与lead裁决

1. P10-R1/S07-1：优先采用托盘由正常InitTray代码创建、冒烟根本不创建TaskbarIcon对象的方案。完整保持原托盘图标、提示、菜单各项/名称/绑定事件/延时状态及退出行为，不把Visibility=Collapsed当隔离。若改动规模会迫使跨本单写域，则先报告，不能半实现或擅自扩域。可以提出更小但同等可证明的生命周期闸门，必须给Loaded不可达依据并交独立复核，不能只断言InitTray已guard。
2. P10-R2/S03：单实例检查异常在smoke下失败关闭（single-instance-check-failed），不得catch后isFirstInstance=true；正常模式既有容错策略保持。所有catch后的不可继续路径明确return，失败结果/非0漏斗不得被正常启动覆盖。
3. P10-R3/S02：目录/重解析点/可写探测等环境读取异常归一成Invalid，不能穿透App构造异常。补outside-temp、非法路径和可控不可读等必要测试；reparse/不可写若机器权限无法稳定构造，不用修改真实目录ACL或提权，明确未测，并更正原报告“19例覆盖reparse/write-probe”的失实表述（用本单补充勘误，不覆盖原历史报告）。
4. P10-R4：窗口局部KeyInterceptor不属于全局钩子安全阻断，但smoke无需它；将其构造/事件订阅也置正常模式guard内，减少未覆盖副作用。不得更改正常Esc行为。
5. 保留20s只失败看门狗、现有三项真实事件顺序与单终态语义；本轮不顺手改协议字段/超时/功能。dataRoot指smoke根的命名风险如实记录。

## 验证与交付

先读独立报告与本单，再读所有目标文件；不重复初次实现。允许构建/纯Core测试/format，经.tmp/build-guard.ps1串行，独立OutDir（不要默认bin，不用-t:Compile）。正常运行实例不得触碰。报告逐项闭环、精确路径行号、所有实际命令和退出码/测试总数、未运行项与正常托盘行为回归缺口。工位过程绿不能冒充lead冻结统一门禁；交付后独立复核，任何人不得自行解除App smoke hold。

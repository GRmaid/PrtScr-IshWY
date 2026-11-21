# P14b′ App候选验收证据表（2026-11-08）

## 冻结身份与状态

源码Freeze：131文件，相对实施前128文件dirty基线8项预期增量；八实施hash匹配交付。task74 Core R1、task73旁路、task83服务R1/Core、task84服务R1/UI均各自范围独立静态PASS，task81补IO范围。task83/84五hash与最终Freeze匹配。

2026-11-08 18:17 当前判定：P14b′源码与隔离门禁验收通过：task73/74/83/84及工具task85独立静态PASS；R3冻结131源码、相对原dirty128基线8项增量。完整solution Build 0警0错，Core.Tests 417/417（343+74）、跳过0，format退出0；产物绑定、Source Verify及默认1931文件Before/After变化0均通过。没有启动App或更新默认运行产物，真实F1/F2、旁路焦点、历史JSON及退出验收仍待主人实测；P11继续暂缓，不提交发布。

14:55失败与暂停为历史，原证据保留；额外R3已获主人明确批准并完成。

## S1–S12证据归属

| 编号 | 静态/自动证据入口 | 当前限制 |
|---|---|---|
| S1 唯一槽/原子仲裁 | Core R1/task74；服务R1/task83；最终八hash与Freeze | 74新增用例已随417/417通过；App真实交错未实测 |
| S2 Overlay优先/busy不排队 | Core preempt+reserve同锁；task83；task84核六入口与四Overlay忙判断 | 不把所有F1路由改成Overlay；真实按键未测 |
| S3 latch后不抢占/不重复写 | task83核Granted限定、WhenAll后End；Core latch规则/task74 | 无写入回滚保证 |
| S4 stale/Owner/Current | Core R1保Overlay成功身份且释放active；task83核Quick清Current和精确代际释放 | 实际晚到采集未人为触发 |
| S5 无图不写历史 | task81 IO链；task83无图Unavailable/状态保留、仅latch写 | 黑屏阈值与引擎算法未改变/未实测 |
| S6 Normal/ScreenQuick | task81、task83、task84写参数与路由 | 真实JSON未验；旧数据不回填 |
| S7 Completed附属失败 | Core TryNoteCommitFailure/task74；task83独立wrapper；task84抑制成功toast | 关闭历史不启动wrapper；启用null计HistoryFailed；底层吞异常仅按已观察结果 |
| S8 三方文本/终态 | A1已过343测试；task83/Core文本与App解析核验 | 本轮完整编译0警0错、417/417通过；不是App运行验收 |
| S9 surface专属收尾 | task83核仅Overlay terminal拆热键/通知，Quick不触碰 | Core原子抢占End先于晚到旁路清理，不冒充正常本地Dispose顺序 |
| S10 旁路owner/token/线程 | task73 ByPass静态PASS，task81 IO调用链，R1/task83真实Activated及延时前后检查，task84 UI上下文 | OS自动激活、queue/Destroy失败及真实焦点仍未验证 |
| S11 退出单预算 | task83实读Core ShutdownGrace300ms；task84真实Exit_Click先同步封口、唯一等待、finally原出口 | 300ms只新增合作等待；Hotkey旧Join/Dispose、Win32不受总硬上限保证 |
| S12 六日志/顺序 | Record/reports/P14b-20261108-protected-log-crosscheck.md；Core880→882→884 | 静态模板/次序，不保证App.Log抛错时仍落盘 |

## 判定原则

本表先登记跨报告证据拼接，只有同一Freeze实际全Build0/0、Core所有测试零跳过、format0、产物绑定、Source Verify和原1931默认After通过，才可关闭本轮源码与隔离门禁实现单。

P11继续暂停；本次源身份不包含Assets或根props/targets，不解决P11 B9/E08；不App/smoke/UIA/默认产物更新/提交发布。未来真实热键、焦点、退出验收须用户许可与实测。

## R3实际门禁（18:14完成）

| 门禁 | 实际结果 |
|---|---|
| 完整Build | 0警0错；40.61s；guard退出0 |
| Core.Tests | 417通过、0失败、0跳过；93ms；同ArtifactsPath，no-build/no-restore |
| format whitespace | 退出0；guard退出0 |
| 产物与环境 | 7项主产物、8份XBF、6项目assets和独立bin/obj路径通过；applicationExecuted=false |
| 源码核验 | 131文件、dirty增量8、冻结后变化0 |
| 默认目录核验 | 1931文件的路径/长度/mtime/hash/根存在性变化0；范围排除data与log |

完整收据、产物哈希与边界见[本轮验收报告](<Record/reports/P14b-20261108-App-isolated-acceptance.md>)。源码与隔离门禁实现单可关闭，产品实机验收继续保留。

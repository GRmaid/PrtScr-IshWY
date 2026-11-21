# P14b′ A1 冻结写集验收（通过）

## 结论边界

2026-11-08，lead在实施者退出、A1两文件写集冻结后，统一运行现有dirty工作树的完整solution Build、Core.Tests与全量format whitespace验证。三项通过，默认bin/obj守卫1931文件零变化；13:20收task-64独立PASS，13:21工位退出，lead最终哈希与被审、被测身份一致，task-63/64可行政收件完成。此门禁不恢复P11，不代表App已接统一会话，不是GUI或smoke验收。独立审查详见[回件](<Record/reports/P14b-20261108-A1-review.md>)；未审全部既有dirty代码，不将A1验收扩大为整个P10/P11通过。

## 实际门禁

| 门禁 | 结果 | 原始证据 |
|---|---|---|
| 完整PrtScr.sln Build | 0警0错，31.92s，guard EXIT0/32s | P14b-isolation-evidence/p14b-a1-build.log；p14b-a1-build.binlog |
| Core.Tests | 343/343，跳过0，98ms；327基线+16 | P14b-isolation-evidence/p14b-a1-test.log；test-results下TRX |
| format whitespace全solution | verify-no-changes/no-restore，EXIT0/8s | P14b-isolation-evidence/p14b-a1-format.log |
| 默认bin/obj前后比较 | 1931文件，路径/长度/SHA256/mtime及根存在性变化0 | P14b-isolation-evidence/p14b-a1-20261108-1312-before.json和after.json |
| 成套隔离产物 | DLL/EXE/PRI及8个XBF齐全，6项目独立assets与App intermediatexaml存在 | P14b-isolation-evidence/p14b-a1-artifacts.json |

隔离根：E:/Explore/GoodGood/PrtScr/.tmp/p14b-a1-20261108-1315。使用新建且事前不存在的根；未删除旧产物、不运行App、不关闭用户实例。全部构建/测试/format均经.tmp/build-guard.ps1串行，不使用Compile。

## 候选命令与实际配置

实际执行由Record/tools/P14b-A1-Build.ps1、P14b-A1-TestFormat.ps1固定。核心build参数为 --artifacts-path <新根> 与 -p:XamlGeneratedOutputPath=<新根>/obj/PrtScr.App/debug_win-x64/；逐项目预检确认所有输出、中间及extensions根隔离。

测试加--no-build/--no-restore并使用同一artifacts根；format通过进程级ArtifactsPath及XamlGeneratedOutputPath环境传给MSBuildWorkspace，不把无属性的默认format当隔离。真实后守卫已通过。

## 被测源码身份

- src/PrtScr.Core/CaptureSessionState.cs SHA256 B091836AF9BB4698131F136326733DAAFB22852EE12A618AEC8445F2931A4B0B。
- src/PrtScr.Core.Tests/CaptureRulesTests.cs SHA256 742C82CF8D7FA3F8862336B45B842EE71AB726387980E018834F70198D0BC823。
- 与实施回件一致；语义增量仅+70/-0。普通git diff的整文件变化来自既有LF index/CRLF工作树差异，不能据此推断新增整篇语义或擅改EOL。

## 产物一致性与不可外推项

- 新根IshWY.dll SHA256 813A01F4BFE3F01905F6D9F4A3780E7FB253EFA737287B3AC2EDF6381E6E3E34，IshWY.pri C1B7B27EC66528AB9B6A57E6CAD5B4E955F3550F3448D3F330DDF2A7D2162715。MainWindow.xbf仍为6A8029E9D01EA2996FA6164A21689926BFF3A1E0440F810EFC0DB11CD8EC2C25。新根成套生成于13:14:33–39，不覆盖默认运行目录。
- 默认bin/obj守卫排除data目录与.log，未证明真实数据没有正常运行写入；不覆盖瞬态修改后恢复或空嵌套目录。不得宣称全磁盘零写入。
- src下XBF/PRI搜索均位于既有bin/obj，未发现项目源根资源泄漏；不据此追溯历史Copy执行。
- 静态与构建只证明A1。六入口、App Parse/Terminal、surface、互斥/抢占、旁路所有权、不可撤销提交仍未接线；下一步决策见Record/plans/P14b-20261108-commit-decisions.md。
- 不执行smoke/publish/产品ZIP/Actions，不commit/push/tag/Release；P11继续明确暂缓。

# P14b′ A1 Core 最小增量实施回件

- 单号 task-63；maid-impl；Flash 子任务 8d2eab89-8d96-475c-b34a-bfaf2c130408。2026-11-08 13:11 收件。
- 最终状态（2026-11-08 13:21）：独立task-64静态PASS且lead哈希绑定一致；完整Build 0警0错、Core.Tests 343/343、format0，默认bin/obj1931文件变化0，A1验收通过。无App接线或运行行为变化，未部署新产物。见[验收](<Record/reports/P14b-20261108-A1-acceptance.md>)与[独立复核](<Record/reports/P14b-20261108-A1-review.md>)。

## 写集与契约

| 文件 | 增量 | 锚点 |
|---|---|---|
| src/PrtScr.Core/CaptureSessionState.cs | 追加 QuickSuccess=7、QuickFailed=8 与两字符串映射，语义+8/-0 | 54–59，225–226 |
| src/PrtScr.Core.Tests/CaptureRulesTests.cs | 5个测试方法，语义+62/-0，旧测试不删除/改写 | 132–192 |

测试覆盖：旧0..6与新7/8数值、两日志字符串、Idle两原因Invalid、四活动态×两原因Allowed、三终态×两原因Duplicate。预计独立测试用例增加16，实际总数以lead门禁为准。未改CanTransition/TerminateDecision/NoticeFor等既有规则；未新增Parse/Terminal公共API，未改App/scripts/CI/依赖。

## 实施者提供的字节身份

| 文件 | 修改前 SHA256 | 交付 SHA256 |
|---|---|---|
| CaptureSessionState.cs | FEBB20D7B78BBF0490CEE7F05F0C1795A62D70922EA305E2BF0FF99FA0BF7A83 | B091836AF9BB4698131F136326733DAAFB22852EE12A618AEC8445F2931A4B0B |
| CaptureRulesTests.cs | 2ABFF9CB44D64941E06EF688F8BF08DF1BD11E972B4C40F9E78015A9881CCDC9 | 742C82CF8D7FA3F8862336B45B842EE71AB726387980E018834F70198D0BC823 |

工位自检：无BOM、loneLf=0、末尾CRLF，无行尾空格/制表；state265→273行，tests299→361行。曾缺测试成员间空行，提交回件前已补，lead先前读到的360行候选不是最终身份。

## EOL 与工作树口径

实施前字节已为CRLF，HEAD/index为LF，core.autocrlf=false且无.gitattributes。state原11593=HEAD11328+265个CR；tests原14594=HEAD14294+299个CR。普通diff显示整篇增删，不等于本次重写语义。语义比对使用git diff --ignore-cr-at-eol；不得为缩diff而改EOL或丢弃已有dirty工作。

工位仅检查两写域路径，不能由“范围内diff仅两项”推断整树没有他人改动。整树原有P10/P11等未提交内容保留。lead须核对最终哈希与独立审查回件一致。

## 未运行与验收边界

- 工位未运行build/test/format/App，未add/commit/push，不写Record或代claim任务板。
- App枚举消费点不在本次5文件读取范围；完整Build补足编译兼容性，但不替代App交互测试。
- 本次仅原因与测试；统一会话、互斥/抢占、旁路与不可撤销提交仍未实现。P11仍暂缓。

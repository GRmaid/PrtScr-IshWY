# P10/P11 写入域纠正

## 事实与证据
- task-47：35516d47-598b-4bff-b5d0-bca46f82867b，原定 App/Core/Tests 唯一写入者。
- task-48：57f93748-d054-4942-b7ce-d51fde3a5ff5，原定 scripts/.github 唯一写入者。
- task-48 的 session event 295/296（17:58:49）明确写入 App.xaml.cs；event 397（18:03:37）记录误入 src 并准备撤回。没有派生代理。
- lead 未写 src。不能将误写者视为第三个获授权实现工位。
- 18:07 git status 未显示 src 修改，glob src/**/*Smoke*.cs 为零。两套新协议文件已被各自删除；这是撤回后的现场，不是 P10 已交付。

## 恢复分工
- task-47 必须重新读取当前树，保留用户既有修改；继续完成原 P10 最小实现。不能只读评审已被撤回的 SmokeSession。
- task-48 只继续 scripts/.github；不得写或删除 src，即使为修复编译。其下一份报告必须列出误写/撤回路径、依据及保留的脚本范围。
- 当前混写期间的构建只能记为过程记录，不作冻结树验收。
- 不运行 App 冒烟或实际打包。两份交付到齐、独立静态审查通过后，再由 lead 统一门禁。

## 协调渠道限制
- 原 subagent ID 不在 list_agents 地址簿。send_message(target=<id>) 返回 active teammate not found；agent_id 参数形式被当前工具拒绝。不得宣称暂停指令已送达。
- 本纠正已同步到共享任务说明与交接账本；不唤醒旧 Sol 队友，不再复制派发相同实现。

## 18:34 后续收件（不改写历史现场）
- 当前 src 已存在 task-47 的 SmokeProtocol / SmokeRunState / SmokeSession 及对应接线和测试；18:07 的“src 干净、Smoke*.cs 为零”仅是撤回后的历史快照，不能当作当前状态。task-47 正式报告尚未收件。
- task-48 已结束并交付 Record/reports/P10-P11-smoke-gates-task48-20261107.md；共享任务完成仅表示实施报告收件，不表示 P11 安全验收通过。报告首部“未改 src”只可理解为最终保留写域，不否认此前越界试验及撤回。
- task-50 已交给显式 deepseek-account/deepseek-flash 工位先行只读复核 P11；只写自己的审查报告，不与 task-47 重叠写入。task-49 仍等待 P10 交付，随后联合核对 P10 与 P11 接口。
- lead 发现需重点复核的两个疑点：CI 仍 -SkipSmoke -NoZip；Release 的 Resolve version 仍直接插入输入。疑点不替代独立结论。未运行 App 冒烟、实际打包或远端工作流。

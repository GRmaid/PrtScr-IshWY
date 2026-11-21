# P14b′ Record门禁辅助函数自检

## 范围与结果

lead执行 `pwsh -NoProfile -File Record/tools/P14b-App-RunnerSelfCheck.ps1`，exit 0、stderr空，输出 `PURE-RUNNER-HELPERS: PASS assertions=19; no build/test/format/App/default-output workload`。此测试仅从Parser AST提取三个纯辅助函数，不dot-source门禁主体；没有调用MSBuild/build/test/format、没有启动App、没有建立或改写源码冻结/实际门禁收据/default bin/obj。

## 19项断言覆盖

- UTC原始JSON字符串：正确roundtrip UTC及ticks保留2项；缺字段/null/数字/空/日期简写/无时区/显式+08:00/坏字符串8项均拒绝。
- 路径：单反斜杠TrimEnd可执行、根相等、合法子路径、Windows大小写、前缀相邻项目拒绝、其他项目拒绝、obj不能冒充bin、先normalize后拒绝跨项目traversal、空值拒绝9项。

## 边界

这是Record工具辅助函数fixture，不是App门禁、不是实际MSBuild属性隔离证明，也不是P11输入身份收口。工具task77仍须末轮独立静态审查；服务/UI冻结及整体独立验收后才能执行实际Freeze→Build→TestFormat→ArtifactCheck→Verify→原After。原1931默认产物Before及128源码dirty基线不重建。

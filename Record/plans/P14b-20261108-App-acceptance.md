# P14b′ App接线验收矩阵（2026-11-08）

状态（18:47）：主人完成本轮默认运行版手测，反馈“效果很好，没有问题”（会话m04313）；P14b′本轮用户使用验收通过。既有完整构建0警0错、417/417测试和format通过。本次反馈不新增精确时长测量或独立JSON取证；P11仍暂缓，不提交发布。以下旧状态为历史记录。

状态（18:32）：默认运行版已获18:24主人许可并完成完整成对Rebuild，0警0错；默认Core.Tests417/417零跳过，401非日志数据及自启动设置不变，8XBF与隔离验收产物一致。未启动App，真实交互仍待主人手测。以下18:17隔离证据保留为历史：P14b′源码与隔离门禁验收通过：task73/74/83/84及工具task85独立静态PASS；R3冻结131源码、相对原dirty128基线8项增量。完整solution Build 0警0错，Core.Tests 417/417（343+74）、跳过0，format退出0；产物绑定、Source Verify及默认1931文件Before/After变化0均通过。没有启动App或更新默认运行产物，真实F1/F2、旁路焦点、历史JSON及退出验收仍待主人实测；P11继续暂缓，不提交发布。

## 1. 批准范围与冻结要求

以 [产品契约](<Record/plans/P14b-20261108-commit-decisions.md>) 为准；A1仅提供Core QuickSuccess=7/QuickFailed=8及文本映射。现有dirty P10/P11文件必须保留，不回退至HEAD，不运行P11第三轮。实现分域应等两份接口报告合并后再开工，同一文件只给一个工位。

## 2. 自动与静态验收矩阵

| 编号 | 场景/契约 | 最低证据 | 不可外推 |
|---|---|---|---|
| S1 | Quick与Overlay只有一个业务会话槽，开槽/预提交取消/授权提交在同一同步边界仲裁 | Core纯规则确定性用例+App调用链，含两个入口竞争 | 不能用UI线程的通常顺序代替同步合同 |
| S2 | Overlay存在时F2忽略且不排队；F1可使预提交Quick失效并进入Overlay | 状态矩阵及所有六入口汇入同一闸门的静态证据 | 不能把一次IsCurrent检查当整个await生命周期有效 |
| S3 | 不可逆commit已获准时F1忽略、不排队；保留会话到副作用完成 | commit lease/slot所有权用例及释放异常路径 | 不保证取消回滚已发出的剪贴板/历史写入 |
| S4 | 旧会话晚到不写入新会话、不发成功；Owned与Current作用不同 | stale/invalidated/new-owner用例与finally收尾链 | 不能无条件用Current挡End导致正常取消永久忙 |
| S5 | 无图失败仅Result/log，不落空图或黑图历史 | AllEnginesFailed/LikelyBlack/取消映射、写入前置条件 | 不改变既有引擎顺序、黑屏阈值或正常捕获算法 |
| S6 | Normal来源与历史分类、HistoryCaptureMeta.ScreenQuick保持 | 六入口到新服务和唯一AddAsync参数核对 | 不声称已检查真实JSON；旧数据不回填 |
| S7 | 图像Completed但附属写入失败保留ClipboardFailed/HistoryFailed | 结果终态/失败字段用例，App错误分支静态证据 | 不将失败覆盖为None；不新增提示文案 |
| S8 | QuickSuccess/QuickFailed解析及终态映射完整 | Core文本+App Parse/Terminal三方映射一一对应 | A1映射不能视为App已接线 |
| S9 | Quick surface不调用Overlay特有EndCaptureMode/NotifyOverlayClosed | 双surface收尾调用链与六条保护日志原文/顺序核对 | 不顺手改Overlay取消/延时/冻屏产品行为 |
| S10 | FullscreenBypass begin/等待/释放受取消和所有权保护 | await前后闸门、owner同步及晚到Dispose不抢焦点证据 | 不仅测开头一次CancellationToken；焦点真实行为仍待实测 |
| S11 | 退出使预提交失效；尽力清理使用一个总计≤300ms预算 | 实际退出入口、单预算传递/超时链与纯规则用例 | 不保证终止底层线程/撤销已开始写入；不操作用户实例 |
| S12 | 原保护日志与finished→state→result顺序保持 | 新旧diff与精确文本/行号对照 | 不把原五条计数当覆盖全部，含hidden Dismiss第六条 |

## 3. 冻结树隔离门禁

- 先确认工位停止写入并记录全部触及源码SHA256；独立复核必须绑定最终哈希。基线为当前dirty树A1的343用例，不是HEAD单独可重现结果。
- 完整PrtScr.sln Build必须0警0错，禁止-t:Compile；Core.Tests需原343及新增用例全过且跳过0，之后format whitespace验证退出0。
- 使用fresh ArtifactsPath、各项目独立obj/extensions/bin、显式App XamlGeneratedOutputPath；test同root且no-build/no-restore，format继承两项路径环境。所有命令经.tmp/build-guard.ps1串行。
- 默认src/*/bin与obj前后1931文件快照须路径/长度/mtime/SHA256/根存在性无变化；范围排除data与.log，不涵盖恢复原样的瞬态写入或空子目录。产物验证PRI、XBF和六项目assets齐全，不启动这些产物。
- 任一门禁失败保存证据、最多两轮返工；不删旧产物、不自动回滚、不覆盖运行目录。
- 实施前128个源码文件的dirty基线已保存于Record/reports/P14b-isolation-evidence/p14b-app-source-before.json；最终增量需对照此基线与HEAD两份事实，避免把旧P10/P11改动算作本轮或误覆盖。

## 4. 交互验收清单（不由代理执行）

默认运行版已于18:32按主人许可重建；由主人自行启动并检查：普通/延时/游戏绕行三类快捷全屏成功；取景期间F2不排队；Quick预提交时F1转取景；不可逆写入期间F1忽略；取消晚到不抢焦点/不改新图；退出后无迟到焦点恢复。启动或重建运行目录前必须另行确认，源码和隔离门禁不等于这些手测通过。

## 5. 持续限制

不停止/启动/激活用户IshWY，不运行smoke或键鼠/UIA，不改真实配置/注册表，不改依赖/README/docs，不提交推送/tag/Release。P11仍明确暂缓；本次批准不涉及D-2B五项。

## 2026-11-08 18:04 续工覆盖状态（历史）

17:59主人显式批准门禁工具额外R3（不恢复P11）。同链属性实验已复现旧尾反斜杠引号吞后续参数，正斜杠后路径和tail正确；Core R2两断言等价修复，逆转两行hash精确还原旧文件，其余源码不变。新轮p14b-app-20261108-1800-r3已Freeze131文件/dirty增量8，默认新Before1931与旧After逐项一致；原失败证据保留。task85独立只读复核四工具中，尚未执行实际新Build/test/format/ArtifactCheck。

## 2026-11-08 14:55 暂停覆盖状态（历史）

主人明确要求先停，等待回来后续工；不再派工/修复/运行门禁。task83/84最终静态PASS并匹配Freeze；实际完整Build exit1（2警1错，XAML参数并吞及两处xUnit2031），后续test/format/ArtifactCheck未跑。原1931默认Before/After变化0、Source Verify131文件/8增量/变化0，四实现单未验收。工具两轮额度耗尽，不自动第三轮；P11暂停及App/默认产物/提交发布hold保持。详见Record/reports/P14b-20261108-App-gate-failure-handoff.md。

## 2026-11-08 18:17 源码与隔离门禁收口

P14b′源码与隔离门禁验收通过：task73/74/83/84及工具task85独立静态PASS；R3冻结131源码、相对原dirty128基线8项增量。完整solution Build 0警0错，Core.Tests 417/417（343+74）、跳过0，format退出0；产物绑定、Source Verify及默认1931文件Before/After变化0均通过。没有启动App或更新默认运行产物，真实F1/F2、旁路焦点、历史JSON及退出验收仍待主人实测；P11继续暂缓，不提交发布。

验收与证据索引：[R3源码与隔离验收](<Record/reports/P14b-20261108-App-isolated-acceptance.md>)。18:32默认更新已完成，见[默认重建与手测清单](<Record/reports/P14b-20261108-default-rebuild.md>)；下一步交由主人自行启动并进行真实按键/焦点/退出验收，不用隔离或重建通过替代实机通过。

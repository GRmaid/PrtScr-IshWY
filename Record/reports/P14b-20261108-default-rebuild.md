# P14b′ 默认运行版完整重建与交付（2026-11-08）

## 1. 结论

主人18:24确认已退出并允许重建。已在无IshWY实例时完整重建默认运行目录；未启动App、smoke或合成输入。默认成对产物已更新，真实按键、焦点与退出验收仍待主人执行。

- 完整solution Rebuild：0警告、0错误；33.68秒，BUILD-GUARD退出0。
- 默认新产物Core.Tests：417/417，失败0、跳过0；91ms，使用no-build/no-restore。
- 冻结源码：131文件，相对原dirty128基线8项增量，重建前后变化0。
- 401个非日志数据文件：路径、长度、SHA256不变；HKCU Run IshWY及StartupApproved字节不变。
- 默认8份XBF逐份与已验收隔离产物SHA256相同。
- 本轮未改业务源码；先前隔离format已通过，源码未变，不重复format。

## 2. 默认运行版身份

运行文件：[IshWY.exe](<E:/Explore/GoodGood/PrtScr/src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe>)。

| 文件 | SHA256 |
|---|---|
| DLL | 65663D4E67F8F25EF33B9F3D2C8C3679B7905A028051FB1F4C1895A32D9C02F6 |
| EXE | 8795710DC5D49848D871E88334DE6AC5C4E350E2AE372AA38EAF324DFA2ACEFD |
| PRI | D8035935D668BB0F3C54E6BE16A66330BD63F18CE50D87F45D07DF75114B00E2 |

DLL/PRI默认哈希与隔离哈希分别记录，不据此宣称二者逐字节相同。完整默认Rebuild、131源码冻结一致与8份XBF比对为本次交付证据，不是已运行进程身份或真实交互验收。

## 3. 实际命令与证据

完整构建通过共享build-guard执行：

	dotnet build PrtScr.sln -t:Rebuild -c Debug --nologo -warnaserror -v:minimal -bl:<本轮rebuild.binlog>

完整构建使用默认OutputPath及OutDir，不传隔离重定向。solution默认Any CPU映射App为Debug/x64；属性探针确认XAML中间路径位于本项目默认obj。首次预检将绝对TargetPath再次Join导致断言失败；修正为直接规范化绝对路径后预检通过，该失败发生在创建快照或构建之前。

- [属性](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/properties-before.json>)
- [重建前快照](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/before.json>)，含旧12项DLL/EXE/PRI/Core/XBF备份索引。
- [重建日志](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/rebuild.log>)、[binlog](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/rebuild.binlog>)、[完成收据](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/build-complete.json>)。
- [测试日志](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/core-tests.log>)、[417项TRX](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/core-tests.trx>)。
- [重建后校验](<E:/Explore/GoodGood/PrtScr/Record/reports/P14b-default-rebuild-20261108-1825/after.json>)。

## 4. 主人手测清单

1. 从原快捷方式或上方EXE自行启动，确认能打开设置；普通快捷截图能复制，历史能看到新图。
2. 打开真实取景后按F2，再取消取景；应忽略F2，不在稍后补拍，下一次F1/F2仍可用。
3. F2后紧接真正的取景入口：提交前应转取景并抛弃旧Quick；已获提交权则忽略本次取景意图，不排队。快速成功时未命中抢占窗口不能单独判失败。
4. 按实际使用环境检查延时/游戏绕行截图；取消后不出现旧成功提示、不抢回旧焦点，历史不被旧结果覆盖。
5. 用托盘“退出”结束应用；截图未完成时也不出现迟到成功或焦点恢复。300ms仅为新增合作等待预算，不以肉眼声称整个退出硬时限通过。

新图JSON契约与真实焦点可在反馈后按日志/数据只读核对。未以源码、Core测试或重建替代真实运行结论。P11继续暂缓，不提交、推送或发布。

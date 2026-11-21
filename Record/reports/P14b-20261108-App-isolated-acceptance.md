# P14b′ App源码与隔离门禁验收（2026-11-08 18:17）

## 结论与验收范围

P14b′方案A的Core原子仲裁、服务六入口、UI路由、旁路取消和真实用户退出接线已完成源码与隔离门禁验收。task67/70/76/80按此范围行政关闭。运行目录未更新，App未启动，真实F1/F2、焦点、JSON与退出行为尚未验收。本结论不关闭完整B/C/D，也不恢复P11。

五项产品契约依主人批准实施；单一CaptureSessionCoordinator替代独立_quickCaptureBusy，避免双真源；Current/Owner分离兼容Overlay先End后写入。预提交Quick可被真正Overlay意图原子抢占并预留，commit latch后忽略且不排队；仅获得提交权后写入。无图不写历史；Normal与ScreenQuick不变；Completed仍保留附属失败；用户退出只有一个额外300ms合作等待预算。

## 最终独立审查

- Core R1/task74、Bypass/task73、服务R1/task83、UI兼容/task84均静态PASS；IO/task81补核。
- 工具R3/task85独立静态PASS且lead匹配最终五hash；Core R2仅两处等价Assert.Single重载修复，逆转两行可精确还原原测试hash。
- 只读工位未经批准扩读的流程问题已保留于各报告，不追认扩读权限。
- [S1–S12证据表](<Record/reports/P14b-20261108-App-acceptance-evidence.md>)区分规则/静态证据与真实交互缺口。

## 实际门禁

| 检查 | 结果 | 证据 |
|---|---|---|
| 完整solution Build | 0警0错，40.61s；guard退出0 | [Build收据](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-build-complete.json>)、[完整日志](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-build.log>) |
| Core.Tests | 417/417，失败0、跳过0，93ms；343基线+74新增 | [TRX](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-test-results/asus_历翼的青鸟酥_2026-11-08_18_12_52_net10.0.trx>) |
| format whitespace | 退出0；guard退出0 | [TestFormat执行器](<Record/tools/P14b-App-TestFormat.ps1>)及本轮收据 |
| 产物与路径闭环 | 7项主产物、8XBF、6项目assets；各自bin/obj；WinUI XAML等于Intermediate | [产物清单](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-artifacts.json>) |
| 源码冻结校验 | 131文件，实施前dirty128基线增量8，冻结后变化0 | [Freeze](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-source-frozen.json>)、[Verify](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3/p14b-app-source-verified.json>) |
| 默认目录不变 | 1931文件，路径/长度/mtime/hash/根存在性变化0 | [Before](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3-before.json>)、[After](<Record/reports/P14b-isolation-evidence/p14b-app-20261108-1800-r3-after.json>) |

Build、测试、format按共享guard串行执行；test在相同ArtifactsPath执行no-build/no-restore。全部命令已结束，非计划或工位自报。旧失败轮的日志、Freeze、Verify、Before/After均未覆盖。

## 最终身份

隔离根为 [.tmp/p14b-app-20261108-1800-r3](<.tmp/p14b-app-20261108-1800-r3/>)；test/产物身份以JSON收据中的绝对路径为准。

| 身份 | SHA256 |
|---|---|
| 源清单 | 9F2080F6D43A4D1756BE4E05AA512E9B06250D7E22844C6AB945E5AEF8D23209 |
| IshWY.dll（978432字节） | 1AB7E986AD6D571E83B7D474261F58E786252E509844D52F78B2A1693AB2712B |
| IshWY.exe | 8795710DC5D49848D871E88334DE6AC5C4E350E2AE372AA38EAF324DFA2ACEFD |
| IshWY.pri | C1B7B27EC66528AB9B6A57E6CAD5B4E955F3550F3448D3F330DDF2A7D2162715 |
| Build log | 8D29039A298C118E7997C1FD5C063C0718B701A9688EE5882D5689E72324B4F0 |
| Binlog | 228F397BB2189B1D7AA58A9E0A99C92027643F0D7AA5932510EDB4246E157B89 |
| TRX | 833F30E1BFCB3839D34CF216201C7B0AF0EE0156C6DBDB8E51544B9F2C1C80DF |

## 边界与下一步

- 默认快照排除data与log，不检测恢复原样的瞬态写入或空子目录变化。源码范围仅cs/csproj/xaml，不解决P11构建输入完整性问题。
- 未运行App、smoke、UIA或合成按键，未修改真实配置/注册表，未提交/推送/tag/Release。P11 B9/E08仍明确暂缓。
- 下一步须先取得默认运行目录更新许可，由主人自行退出实例；确认无实例后完整成对Rebuild，核实Path/StartTime/module与hash，再由主人按[交互清单](<Record/plans/P14b-20261108-App-acceptance.md>)实测。

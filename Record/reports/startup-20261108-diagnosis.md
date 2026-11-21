# IshWY 开机自启动及手动无法打开（2026-11-08）

## 状态
启动运行产物已恢复；11:07 两次全新进程启动均成功，11:16 再次普通启动及后续截图/复制日志正常。独立诊断两路均已收件（2/2），lead 已核对 SDK 输出目标与备份哈希：OutDir/OutputPath 分流是混装入口。主人于 2026-11-08 12:55 明确确认“真实重启成功”，实际登录启动验收通过，启动故障关闭；该验收不外推为完整功能或 P10/P11 联合门禁。本轮未改产品源码、构建脚本或注册项，未提交、推送或发布；P11 继续暂缓。具体历史 Copy 执行与 XAML 内部失败索引不作已观测事实。

## 1. 故障证据与定位
- 10:57 无 IshWY 残留进程。HKCU Run 中 IshWY 路径正确，参数为 --startup；StartupApproved 的 Run/IshWY 首字节为 2（启用）。开机启动并非完全未执行。
- 默认运行日志 app.log:73231–73272：10:43:36 开机模式（startHidden=True）以及 10:44:05、10:44:11 手动模式（False）均成功完成 App.InitializeComponent 与配置加载，随后在 MainWindow.InitializeComponent 抛出 Microsoft.UI.Xaml.Markup.XamlParseException: XAML parsing failed.
- 堆栈为 MainWindow.g.i.cs:279 → MainWindow.xaml.cs:52 → App.xaml.cs:109。两种启动共用同一失败点，没有证据要求重写登录注册逻辑。
- 运行目录是 src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64。历史 R1/R2 构建仅设置 OutDir；当前 WinUI CopyGeneratedXaml 的目的路径实际使用 OutputPath，而主程序集使用 OutDir。该输出分流会让新 XBF 进入默认目录而默认旧 DLL 不更新，不需要“DLL 被锁、只拷 XBF”的假设。目标与历史命令证据见[第二路核查](<Record/reports/startup-20261108-source-audit.md>)。

## 2. 已实施的恢复
1. 保全旧 EXE/DLL/PDB/PRI/XBF 等 17 项产物于 Record/reports/startup-20261108-evidence/runtime-before。对配置/历史等 399 个非日志数据文件计算 SHA256，并保存 HKCU Run/StartupApproved 值。
2. 再次确认没有 IshWY 实例，在默认目录执行整包 Rebuild，而非只替换 DLL 或 XBF。未删除 data，未运行 --smoke，未修改注册项。
3. 对整包结果运行 Core.Tests 与全解 whitespace 验证；恢复后再次核对数据/注册项。
4. 从 Windows 工作目录启动全新 --startup 进程；6 秒观察后只结束本次创建的测试 PID 38124，再启动全新普通进程 PID 39628。没有唤醒或结束用户原有实例，普通进程保持运行。

## 3. 恢复前后身份
| 文件 | 故障时 SHA256 | 恢复后 SHA256 | 判定 |
|---|---|---|---|
| IshWY.dll | 7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C | ED35D1329C93A2482059872C7B66106A920CFDC7CC631EC32B712A581609B9FF | 主DLL更新，967168→955904字节 |
| MainWindow.xbf | 6A8029E9D01EA2996FA6164A21689926BFF3A1E0440F810EFC0DB11CD8EC2C25 | 同左 | 字节未变 |
| IshWY.pri | D8035935D668BB0F3C54E6BE16A66330BD63F18CE50D87F45D07DF75114B00E2 | 同左 | 字节未变 |

成套重建后 DLL 更新、主窗口 XBF 与 PRI 字节不变，两种启动由失败转为成功。结合独立核查及 SDK 输出分流，证据支持旧主 DLL 与新 XAML 资源混装，不支持 PRI 文件损坏或注册项失效。本次整包 Rebuild 不是仅替换一个 DLL 的单变量实验，不能据此排除其他应用程序集变化；某个连接器/类型表失败索引、每次历史 Copy 的执行或跳过仍未知。

## 4. lead 实测门禁
通过 .tmp/build-guard.ps1 串行执行（tag startup-recovery）：

```powershell
dotnet build PrtScr.sln -t:Rebuild -c Debug --nologo
dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
```

- Build：0 警告、0 错误，26.34 秒；Core.Tests：327/327，0 失败、0 跳过，90ms；format：退出 0；build-guard：EXIT=0，elapsed=36s。
- --startup：11:07:08 创建 PID 38124；app.log:73279 MainWindow.InitializeComponent done，73280 托盘初始化完成，73294 window created startHidden=True，73295 静默驻留托盘，73297 预热完成。
- 手动启动：11:07:14 创建 PID 39628；app.log:73305 MainWindow.InitializeComponent done，73306 托盘初始化完成，73320 startHidden=False，73321 window activated，73322 overlay precreated。
- H2-ArtifactProof 观察到 PID 39628 实际加载默认运行目录 IshWY.dll，RuntimeObserved=true，SHA256 为 ED35D1329C93A2482059872C7B66106A920CFDC7CC631EC32B712A581609B9FF；非仅依据修改时间判断。
- 重建后及两次启动后各核对一次：PRESERVED data=399 Run=unchanged StartupApproved=unchanged。未将可正常追加的日志纳入“数据未变”哈希口径。

## 5. 防复发与验收边界
- Record/HeadMaid.md 已补充：OutDir 单独设置不等于 WinUI 隔离；后续验证构建须隔离实际 OutputPath 与中间产物并核对默认 DLL/XBF/PRI 哈希不变。默认运行目录仅在无实例时成套整包重建，不混拷资源。
- 不将本轮普通启动恢复外推为 P10 smoke 协议、S06/S07/S09、UIA/读屏或完整功能验收。P11 BLK-1 B9/E08 与联合 smoke/publish/产品 ZIP/Actions hold 均保留；无 R3、提交或发布。
- 2026-11-08 12:55 主人确认真实重启成功，并要求继续整改；启动问题已用户验收。本轮不新增启动测试、不强制重启、不合成输入代测。
- 两路独立诊断均已收件（2/2）。lead 直接核对 WinUI/.NET 输出目标、R1/R2 命令及六项哈希；旧 DLL 完整备份存在。接受文件级混装及 OutDir/OutputPath 分流；撤回早期锁竞争解释，不接受“旧 DLL 未备份”“单变量实验”“具体失败索引已排除”等过强断言。见[资源核查](<Record/reports/startup-20261108-resource-audit.md>)、[源码输出核查](<Record/reports/startup-20261108-source-audit.md>)。
- 11:24只读观察：原11:07普通试验实例已在11:16:05由托盘退出，11:16:09新PID38668完成普通启动；11:16:23日志出现窗口捕获成功、复制成功与历史保存。只记为后续运行证据，不冒充用户视觉验收或实际登录验收，见startup-20261108-evidence/later-runtime.txt。399份data未变仅指11:07恢复前后校验，后续正常截图写入历史另计。

## 证据索引
- 前后产物清单：startup-20261108-artifact-before.json / startup-20261108-artifact-after.json。
- 用户数据与注册项：startup-20261108-evidence/before.json / after-data.json。
- 真实进程：startup-20261108-evidence/startup-process.json / manual-process.json。
- 真实成功日志片段：startup-20261108-evidence/launch-log.txt，原日志行号保留。
- 保全/校验/启动脚本：Record/tools/Startup-20261108-Snapshot.ps1、Startup-20261108-Verify.ps1、Startup-20261108-Launch.ps1。

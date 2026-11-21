# 启动故障第二路源码与输出目标核查（2026-11-08）

- 工位：30522673-2d9a-44c9-af0f-7f0b65466a35，显式 deepseek-account/deepseek-flash，只读；11:38 回件、11:39 完成通知。
- 状态：两路独立诊断均已收件（2/2）。本文件是第二路回件的摘要及 lead 抽查裁决，不冒充完整原始工具转储。
- task-59 为父会话原生共享任务；一次性工位未能访问该看板，收件与关闭由 lead 行政登记，不伪造工位 claim 或自行通过。
- 范围：本地普通启动故障；不是 P10 smoke、P11、发布或真实登录验收。

## 1. 结论与证据链

文件级结论：故障目录存在旧主程序集与 P10 新主窗口 XBF 混装。成套 Rebuild 已恢复普通及 --startup 两条启动路径；未发现需要为本次故障追加产品源码补丁的证据。

混装入口已定位：历史隔离构建仅设置 OutDir，然而本仓库当前 WinUI 目标将 XBF 写入 OutputPath，主程序集则写入 OutDir。两者不相等时，即使构建成功且不存在 DLL 复制锁，也会留下旧默认 DLL 与更新的默认 XBF。

| 证据 | lead 直接核对 |
|---|---|
| WinUI 导入版本 | [nuget.g.targets:11–19](<src/PrtScr.App/obj/PrtScr.App.csproj.nuget.g.targets#L11-L19>) 导入 WinUI 2.3.9、runtime/SDK 2.5.1 |
| XBF 目的路径 | [WinUI targets:908–919](<C:/Users/asus/.nuget/packages/microsoft.windowsappsdk.winui/2.3.9/buildTransitive/Microsoft.UI.Xaml.Markup.Compiler.interop.targets#L908-L919>)：GeneratedXamlDest0 使用 $(OutputPath)，后续改为 xbf 后缀 |
| XBF 复制 | [WinUI targets:950–955](<C:/Users/asus/.nuget/packages/microsoft.windowsappsdk.winui/2.3.9/buildTransitive/Microsoft.UI.Xaml.Markup.Compiler.interop.targets#L950-L955>)：CopyGeneratedXaml 在 CopyFilesToOutputDirectory 前运行，DestinationFiles 为 GeneratedXamlDest |
| 主程序集复制 | [.NET targets:4918–4929](<C:/Program Files/dotnet/sdk/10.0.402/Microsoft.Common.CurrentVersion.targets#L4918-L4929>)：IntermediateAssembly 的 DestinationFolder 为 $(OutDir) |
| 两个属性并非别名 | [.NET targets:154–156](<C:/Program Files/dotnet/sdk/10.0.402/Microsoft.Common.CurrentVersion.targets#L154-L156>) 计算 OutputPath；[227行](<C:/Program Files/dotnet/sdk/10.0.402/Microsoft.Common.CurrentVersion.targets#L227>) 仅在 OutDir 为空时从 OutputPath 默认赋值，不逆向覆盖 OutputPath |
| 历史 R1/R2 命令 | [R1报告:76](<Record/reports/P10-round1-repair.md#L76>)、[R2报告:39](<Record/reports/P10-round2-repair.md#L39>) 均只设置 OutDir；[R2日志:9–16](<.tmp/p10r2/build.log#L9-L16>) 记录 DLL 输出到隔离目录及构建 0/0 |
| 项目无另行隔离 | 已读 [Directory.Build.props](<Directory.Build.props>) 与 [App 项目](<src/PrtScr.App/PrtScr.App.csproj>)，未发现将 OutputPath 随 OutDir 重定向的覆盖 |

故障主 XBF 的时间为 11-07 21:50:02，与 R1 时段一致；更重要的是，备份故障 XBF、R1、R2 和恢复后 XBF 的 SHA256 完全一致。默认故障 DLL 则为先前 H2 版本。历史没有 binlog/逐次复制跟踪，因此不把某一次 Copy 的实际执行、跳过、锁定或连接器内部失败索引写成已观测事实。

## 2. 备份与哈希复核

[lead 哈希复核清单](<Record/reports/startup-20261108-evidence/lead-hash-verification.json>) 保存六项直接复核结果：

- 故障 DLL 完整副本确实存在于 [runtime-before/IshWY.dll](<Record/reports/startup-20261108-evidence/runtime-before/IshWY.dll>)，967168 字节，SHA256 为 7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C。
- 故障备份、R1、R2、当前主 XBF 均为 32096 字节，SHA256 为 6A8029E9D01EA2996FA6164A21689926BFF3A1E0440F810EFC0DB11CD8EC2C25。
- 恢复后主 DLL 为 955904 字节，SHA256 为 ED35D1329C93A2482059872C7B66106A920CFDC7CC631EC32B712A581609B9FF。
- 恢复前后主 PRI 的 SHA256 均为 D8035935D668BB0F3C54E6BE16A66330BD63F18CE50D87F45D07DF75114B00E2；详见[前清单](<Record/reports/startup-20261108-artifact-before.json>)与[后清单](<Record/reports/startup-20261108-artifact-after.json>)。

## 3. 对回件及早期解释的裁决

1. 接受 OutDir/OutputPath 分流机制。撤回“obj 新 DLL 被旧实例锁住、仅 XBF 拷入”的早期因果解释；当前目标不需要该假设即可产生混装。不能据构建成功反推默认运行目录成套一致。
2. 第二路回件称旧 7FA2 DLL 未保全，不成立；上述完整副本与哈希已由 lead 直接核对。
3. 两路报告的“只改程序集的单变量实验”表述过强。本次是当前工作树整包 Rebuild，其他应用程序集也可变化；只保留文件级混装诊断和成套恢复有效的结论。
4. Connect case 或 GetXamlType 的源码形式不足以证明内部类型索引绝不出错；未获得本次失败的具体 HRESULT/InnerException/WER，不裁定某个连接器、类型表或资源表索引。
5. PRI 前后相同、不支持 PRI 文件损坏，但不能扩展为 PRI 从未参与装载。
6. SkipCopyUnchangedFiles 默认 true，可解释相同资源保留旧时间；无历史 binlog，不宣称 R2 的某次 Copy 已确认跳过。
7. [R2报告:35](<Record/reports/P10-round2-repair.md#L35>)“未写默认 bin”推断错误，已在原报告增加纠正说明；其构建、单测数字不因此作废，但不能证明输出隔离。

## 4. 恢复、验证与边界

恢复前确认无实例，保全 17 项产物与 399 个非日志数据文件；默认整包 Rebuild 0 警 0 错、327/327、format 0。11:07 全新 --startup 与普通启动均成功；11:16 新普通进程日志续有截图、复制、历史保存成功，见[主诊断报告](<Record/reports/startup-20261108-diagnosis.md>)和[后续运行日志](<Record/reports/startup-20261108-evidence/later-runtime.txt>)。

本轮只恢复运行产物并纠正构建纪律，未改产品源码、构建脚本、依赖或注册项。399 份数据未变仅覆盖 11:07 恢复校验窗口，之后正常截图写入另计。未重启/注销用户会话、未做合成输入或 UIA；真实登录启动仍待用户验收。P11 暂缓及联合 smoke/publish/产品 ZIP/Actions/提交发布 hold 保留。

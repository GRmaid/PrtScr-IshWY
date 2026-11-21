# 启动故障独立资源核查（2026-11-08）

- 工位：e8ad0baa-e011-4835-9fc4-f7bda99bb08d，显式 deepseek-account/deepseek-flash，只读。
- 回件时间：工位报告11:20，lead收到11:23；本文件由lead据回件归档，不代表原始工具全量转储。
- 验收范围：运行产物一致性、XAML生成代码耦合、恢复对照；非P10/P11发布门禁。

## 工位观察
1. 故障主DLL为967168B、SHA256 `7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C`，主XBF为32096B、SHA256 `6A8029E9D01EA2996FA6164A21689926BFF3A1E0440F810EFC0DB11CD8EC2C25`；与lead保全清单相符。
2. 工位比对主XBF与旧obj、.tmp/out-p10r1、.tmp/out-p10r2，认为后者均与故障时主XBF逐字节相同，属于P10托盘移出XAML之后的资源；旧obj主DLL则为955904B、SHA前缀dc93658a，与out-p10r2相同，非故障bin旧DLL。历史隔离构建目录完整哈希为工位观察，本报告不补造被截断值。
3. 工位比对多个历史输出的PRI与Microsoft.UI.Xaml.dll均相同；lead直接核对恢复前后主XBF/PRI哈希不变，主DLL更新为955904B、SHA256 `ED35D1329C93A2482059872C7B66106A920CFDC7CC631EC32B712A581609B9FF`。恢复后普通与--startup均完成初始化。
4. 工位观察旧DLL中UTF16 TaskbarIcon为31次、新DLL为0；旧异常堆栈LoadComponent在g.i.cs:279，当前生成文件为269。与P10从XAML删除托盘字段方向相符。
5. 当前生成Connect(int, object)连接表据工位为131个case、ID2..132，类型/属性由生成IXamlMetadataProvider提供；XBF与程序集存在生成契约。工位未成功解出旧DLL的cid→字段IL映射，未定位具体失败表或索引。
6. 当前运行PID据工位为38668、11:16:09，非11:07恢复试验PID39628。工位未停进程、构建、写文件、改data/HKCU或提交。

## lead抽查与裁决
- 已直接核对恢复前产物清单、恢复后数据快照及启动日志；两种启动从同一XAML失败点转为成功，文件级证据支持旧程序集与新XBF混装。
- 不采纳“唯一变量只有程序集”的严格表述：本次是当前工作树整包Rebuild，不是仅替换一个DLL的单变量受控实验；其他应用程序集也可能更新。结合XBF/PRI不变及旧/新托盘字段差异，结论仍有充分支持。
- 第二路回件后纠正：撤回“旧实例锁定 DLL 导致只拷 XBF”的早期因果解释。lead 直接核对当前 WinUI 目标将 XBF 写入 OutputPath，主程序集写入 OutDir；历史 R1/R2 只设置后者，无需复制锁竞争即可混装。无历史 binlog，不宣称每次 Copy 的执行/跳过已被直接观测。详见[输出目标核查](<Record/reports/startup-20261108-source-audit.md>)。
- 不据二进制字符串缺失单独证明XBF装载机制；不据普通堆栈断言Connect从未进入。具体失败索引仍未知，无须为已恢复的启动故障继续反编译。
- 不将时间先后或固定PRI大小作为有效运行产物判据；以本次成套构建身份、obj/输出一致性和真实新进程日志为准。

## 防复发
- 不边运行边更新默认运行目录；隔离OutDir时还须隔离实际OutputPath与中间产物，完成后比对默认DLL/XBF/PRI哈希不变。
- 不单独混拷XBF/DLL；故障产物保留作证据，不回装。
- 恢复时先保全产物/data/HKCU，确认无实例再整包Rebuild；不运行publish脚本/旧smoke。
- 第二路源码诊断已收件，两路均由 lead 核对并附争议裁决，不代表报告每句话均被接受；登录启动仍待用户真实注销或重启验收。

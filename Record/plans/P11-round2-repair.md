# P11 第2轮（最后一轮自动返工）契约

日期：2026-11-07 21:54。依据 task-52 的 needs_revision、冻结 E01–E10 与 lead 对当前脚本的抽查。本轮不是新增发布授权；P10 task-53 仍独立进行。本文件不追改第一轮评审历史。

## 目标与裁决

- B7 / BLK1、BLK1a 是真实运行阻断：启动器只注入环境但没有保证 --smoke，136/136 离线测试未覆盖实际 argv。必须修入口并增加行为夹具。
- B8 是 E09 同一产物证据的闭环：当前 artifact-files.sha256 在 smoke 前生成，后续仅比较源码与 ZIP 整包 hash，未证明 smoke 前后输出不变或 ZIP 条目等于该输出。不能以源码稳定代替输出稳定。
- B9 是 E08 的默认纳入原则：tracked/untracked 默认纳入，但 ignored 检测仍是目录/后缀白名单；未知顶层目录下的 ignored 文件会漏报。未显式排除者必须纳入指纹或列为缺口并失败关闭。
- 第一轮 B1–B6、M2/M3/M6 的已通过静态结论须保留；本轮不重构协议、不扩功能。第2轮仍失败则落盘并上报，不自动开启第3轮或以换人重置轮次。

## 输入与写域

工作区 E:/Explore/GoodGood/PrtScr。先读：
1. Record/reports/P11-round1-independent-review.md（上一轮完整评审，最终 verdict=needs_revision）
2. 本契约
3. scripts/smoke.ps1
4. scripts/gate.ps1
5. scripts/lib/SourceIdentity.ps1

随后仅按需读取/修改本单脚本和测试，不扫无关目录。允许写：scripts/smoke.ps1、scripts/gate.ps1、scripts/publish.ps1（仅必要接口）、scripts/lib/SourceIdentity.ps1、scripts/lib/GateSupport.ps1、scripts/tests/**，以及唯一报告 Record/reports/P11-round2-repair.md。不写 src/**、.github/**、docs/**、README、其他 Record、真实 data、.gitignore；不回滚并行改动。若确需新 helper 或其他路径，先报告缺口，不越界。

## B7：真实命令行保证

- 在 ProcessStartInfo.ArgumentList 构造处无条件保证且只保证一个有效 --smoke 参数；不得依赖调用者、env 或显示字符串。ProcessArguments 供 pwsh -File 测试时须仍正确传递；重复 --smoke 明确拒绝或规范化，不可重复注入。
- 日志/记录必须描述真实构造参数，不把未启动/未成功写成通过；不改 schemaVersion=1 的 App 结果协议。
- 离线 pwsh 替身实际捕获 argv、ISHWY_SMOKE_ROOT/NONCE，并按既有结果协议输出。正例检查三个值及 exactly-one；显式重复参数例；env-only / 无结果 exit0 负例必须拒绝。
- 必须有行为型变异验证：把受控临时副本中实际追加 --smoke 的逻辑去掉，运行相同入口夹具应失败，不能仅搜索源码字符串/注释证明测试有效。不得启动真实 IshWY。

## B8：冒烟输出与 ZIP 绑定

- smoke 前保存确定性相对路径、长度、SHA256 清单；成功后、ZIP 前再次采样，任一增/删/改或读取错误必须失败关闭并阻止生成新包。
- ZIP 后独立枚举实际条目并逐文件计算内容 SHA256/长度，对照受验输出清单。拒绝额外、缺失、重复、路径歧义/穿越及内容变化；根目录前缀规则必须明确，与既有压缩布局保持一致。不得只比较 ZIP 自己的 hash 或发布脚本返回的标记。
- 输出/ZIP 两侧清单及自身 hash、前后比较结论、ZIP 条目核验结论写入 gate 证据/manifest；稳定性失败不能出现 GATE PASS 或 createdThisRun=true 的已验包结论。
- 离线纯临时假文件/小 ZIP 夹具覆盖：相等、smoke 后改文件、增/删文件、ZIP 条目内容改/增/缺失/重复/非法路径、hash/读取失败。小 ZIP 只含合成文本数据，不是实际发布包；允许此类离线夹具，不运行 publish 或真实打包。
- 证据边界必须明确：前后采样/ZIP 对照并非对恶意瞬时替换再还原或并发写入的全程证明；不声称 App 已加载所有磁盘文件或全局行为等价。本轮不做操作系统沙箱/原子快照。

## B9 与进程夹具

- ignored 检测与默认纳入规则一致：未知目录/未知扩展名不得静默漏过；可选择纳入 hash 或列出 uncovered 并拒绝。显式非构建排除保持可读、可审查，不删除用户文件消噪。
- 临时 git 夹具含 newtop/src/a.cs、newtop/payload.unknown-extension 被忽略、常规 NativeMethods.txt/xml、tracked/untracked 增删改；生成物/已声明非构建排除仍正确。
- M2/M3 小范围行为补测：替身同时输出大于管道缓冲区的 stdout/stderr（建议各512KiB）后正常写结果，不死锁；替身等待触发2秒超时，非0且只处置本次持有的进程对象，不按名称/PID历史值杀用户程序。清理异常只警告的语义不得回归。

## 验收与报告

- PowerShell parser 所有改动脚本通过；原离线136条不得降低强度，新增行为夹具必须单独列数、命令、退出码、预期与实测。
- 模拟测试仅启动自身 pwsh/临时 git；不得 App smoke、dotnet build/publish、实际产物 ZIP、Actions、UIA/合成输入、用户进程终止；不得再派子代理、新增依赖、commit/push/tag/Release。
- 分段落盘五段式报告，保留未知与失败原文。同处失败3次/输入缺失/无法完成先落盘停手。
- 交付不等于放行。task-52评审已否决第一轮；本轮交付后由独立 Flash 全面复核 B1–B9 及 E01–E10，再与 P10 task-54 合并静态安全结论。仅两侧通过后才由 lead 按受控执行单运行，E09 实际样本仍另验。

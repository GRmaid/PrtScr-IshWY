# P11 第1轮返工裁决与实施边界

日期：2026-11-07 18:50。依据独立报告 Record/reports/P11-independent-review.md，P11 needs_revision；P10独立审查另行并行。lead已抽查B1–B5现有位置，承认缺口，不把离线34/34当作全链路通过。

## 写域与安全前置
- 唯一P11返工工位 task-51：scripts/**、.github/workflows/ci.yml/release.yml，以及自己的 Record/reports/P11-round1-repair.md；绝不写、删除或撤回src。原P11工位已结束，不存在并发写工。
- 实施前先完整读本文件、独立报告、safe-gates与验收矩阵，再读将编辑的现有文件。保留用户与P10全部变更；不改README/docs或ProjectLog，不commit/push/tag/Release，不再派子代理。
- 本轮仅代码修补、PowerShell解析、纯离线/临时fixture负例；不运行App smoke、不实际dotnet publish/打包、不触发Actions，不杀用户实例或合成输入。不要为脚本修补重跑C# build。

## B4 书面裁决：保持严格CI，不接受SkipSmoke绿
- 选择独立报告方案A。CI复用完整gate，不再传-SkipSmoke/-NoZip；真实smoke未跑/失败不得GATE PASS、不得CI通过。CI与release都上传完整evidence（含失败证据，always）。
- gate的-SkipSmoke保留作显式未验证的诊断路径，但必须输出LIMITED/UNVERIFIED且非0，不能绿；-NoZip同样不是完整P11通过。ci允许dirty本地指纹验证，但releaseReady仅clean release模式为true，二者不能混称。
- 不假定windows-latest有交互桌面，不触发远端演练。无桌面按超时失败关闭；远端可运行性保留“未验证”，不是靠SkipSmoke改绿。这是技术门禁决定，不构成发版授权。

## 必修闭环（B1–B5）
1. B1：所有不可信event/input/ref/tag只通过env进入PowerShell；使用共享严格版本规则（可先规范一次前导v但拒绝换行/空白/任意shell字符）后才写GITHUB_OUTPUT。tag校验git check-ref-format；gh release命令只读env/参数，run脚本体禁止直接GitHub表达式输入插值；修正不实注释。增加注入/换行/非法tag负例，不真的创建tag或Release。
2. B2：dotnet publish透传-p:Version，必要时确定FileVersion；gate独立检查IshWY.exe与实际托管IshWY.dll版本（若apphost不携版本则以dll为权威，显式记录）与manifest规范化版本精确一致，允许SDK添加+commit但不能用不严谨前缀1.3.1接受1.3.10。验证失败必须fail。
3. B3：指纹改默认纳入、显式排除生成物/非构建资料；src内.txt/.xml/config及未来类型不得再静默漏掉。生成物bin/obj/.tmp/artifacts/data等和Record/docs/README/LICENSE/主人材料可明确排除并说明；ignored/uncovered输入应列证据，无法解释的构建输入缺口使完整gate失败。增加NativeMethods.txt与xml/new后缀变动指纹敏感、删除/新增/忽略覆盖的离线用例。不得把仅扩展白名单称全覆盖。
4. B5代码子项：stability比较HEAD+fingerprint+Dirty；断言输出非空、exe/dll存在与版本一致；manifest记录原始smoke结果与source-inputs/artifact-files清单自身SHA256，并保留对应文件、失败日志、源身份。证据目录唯一防同秒覆盖。CI上传完整evidence且失败也留存。
5. B5运行子项：真实gate/ZIP/manifest样本仍由lead在修补经安全复核后取得，当前绝不自报E09通过；返工报告分别写“静态代码已修”与“运行证据未取”。

## lead新增B6：验证后才打ZIP
当前gate调用publish -SkipSmoke且未强制-NoZip，因此ZIP先于外层smoke产生，违反原始任务“最终裁剪内容通过后才zip”。修为gate始终先stage publish（-SkipSmoke -NoZip），检查输出/版本→受控外层smoke成功并核验源码稳定→才生成本次ZIP和SHA256。Skip/失败不能把旧ZIP当本次证据；manifest写zip生成/跳过原因。保留失败证据，任何删除仅限已核对的本次受控输出，不清理用户data。

## 建议同轮小修（不扩大功能）
- M2先异步读取stdout/stderr再等待，避免管道缓冲死锁；保持仅终止自己Process对象。用可控fixture大输出与超时负例，不启动App。
- M3临时根清理失败只警告，不先PASS再因清理异常变非0；失败根留证。M6唯一evidence目录随B5修。checks原协议要求恰含三个检查，不要求数组顺序，M1不升阻断；若改顺序校验须说明兼容性。

## 安全复核与验收的两层门槛
静态安全复核明确通过，只表示允许lead受控执行新smoke/gate，不等于P11完整验收；E09真实样本和正例必须运行后再独立核验。不能要求“先有真实样本才允许安全执行”形成死锁，也不能取消样本要求。
修后报告列每条B1–B6证据、所有离线命令/退出码、仍未运行范围和残余风险，交回lead再独立复核；不得自行解除hold。最多两轮返工，本轮为1。

## 21:38 新收 P10 独立接口阻断 B7

Record/reports/P10-P11-independent-review.md 发现 scripts/smoke.ps1 未无条件追加 --smoke，gate/publish 调用也未保证此参数。仅注入 ISHWY_SMOKE_ROOT/NONCE 不能选择 App 的 smoke 分支；有正常实例会激活它，无实例会进入正常启动并可能写真实 data。当前绝不允许运行该脚本。最终 smoke.ps1 必须自行确保恰有有效 --smoke 参数（不依赖调用者），gate/publish 接口一致，并以离线替身进程记录 argv+env 的正反例证明；不启动真实 App。独立复核 task-52 必须检查 B7。

此为对当前工位契约的记录补充，不宣称运行中的 one-shot 已收到消息；不另起重叠写工位。task-51 正式交付后核对实际是否已修，未修则计入下一轮返工，不直接执行或由 lead 越域代改。

# P11 第 2 轮独立最终安全复核（task-56）

- 仓库：E:/Explore/GoodGood/PrtScr；HEAD：`5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`
- 复核时间：2026-11-07 22:19–22:50 +08:00（独立只读工位，唯一写域为本文件）
- 复核对象：P11 第 2 轮返工（scripts/smoke.ps1、scripts/gate.ps1、scripts/lib/{SourceIdentity,GateSupport,SmokeResult}.ps1、scripts/tests/*、.github/workflows/*、publish.ps1 及 App csproj 构建项）
- 结论：**needs_revision**。阻断 1 项（BLK-1，B9/E08）；B1–B8、M2、M3、E01–E07、E09、E10 静态通过。

## 一、总判定

1. 本轮返工把第 1 轮的两处实质缺陷修掉了：B7 的 `--smoke` 现在由 smoke.ps1 自己追加并拒绝调用方重复传入（scripts/smoke.ps1:34-40、71-84），B8 新增 smoke 前后输出清单比对与 ZIP 逐条目绑定（scripts/gate.ps1:184-207、286-305、358-425）。这两项在静态接线与独立负例上均成立。
2. **唯一致命缺口在 B9**：SourceIdentity 把 `.png/.jpg/.jpeg/.gif/.bmp/.webp/.ico/.log` 一律视为“被忽略且非构建”（scripts/lib/SourceIdentity.ps1:41-46、88-109），但项目自身的构建会把 `src/PrtScr.App/Assets/**` 全部复制进输出（src/PrtScr.App/PrtScr.App.csproj:39），且 `.ico` 是 ApplicationIcon（同文件:17）。因此 `src/PrtScr.App/Assets/` 下被忽略的文件（现实例：被 .gitignore:11 的 `*.log` 命中的日志、被 :12 的 `clip_probe*.png` 命中的截图；.ico/png 在用户全局 ignore 或新增 ignore 规则时同理）会被复制进发布目录乃至 ZIP，却既不进 source-inputs，也不进 ignored 候选、uncovered 或 excluded 证据文件（scripts/gate.ps1:435-440）。实测 ignored 的 `Assets/logo.png` 与 `Assets/icon2.ico` 在 identity 中完全消失（见第四节 B9 探针）。
3. 该缺口当前实例数为 0（真仓库有 0 个 ignored 候选、0 个 uncovered；唯一的 tracked 图片 `src/PrtScr.App/Assets/app.ico` 已纳入输入），所以**现有产物没有被错绑**；但“构建输入全覆盖”的保证不成立，E08 的“不得声称同 SHA 证明”在受控本地执行场景下会被触发。按冻结判据判 needs_revision，不按新需求加码。
4. 放行边界：即使 BLK-1 修复并再复核通过，也只是与 P10 task54 双 pass 后允许 lead **受控执行**；不是运行验收、不是发布许可，不解除 tag/Release 冻结。E09 的真实样本采集仍须另行放行。

## 二、复核范围与方法

**输入（全部实读）**：Record/reports/P11-round2-repair.md（60 行）、Record/plans/P11-round2-repair.md（B7–B9 契约）、Record/reports/P11-round1-independent-review.md、Record/plans/P10-P11-acceptance-matrix.md、Record/plans/P10-P11-safe-gates.md；scripts/smoke.ps1（211 行）、scripts/gate.ps1（595 行）、scripts/lib/SourceIdentity.ps1（368 行）、scripts/lib/GateSupport.ps1（728 行）、scripts/lib/SmokeResult.ps1（137 行）、scripts/publish.ps1、scripts/tests/offline-negatives.ps1、scripts/tests/p11-behavior-fixtures.ps1、scripts/tests/fixtures/smoke-stub.ps1、.github/workflows/ci.yml、release.yml、src/PrtScr.App/PrtScr.App.csproj、.gitignore、git 状态。

**工作树状态**：`git status --porcelain` 51 项（12 modified + 39 untracked）；其中 src 下 6 个 modified 属 P10 并行修改，本工位未触碰、未回退。当前 identity：inputs=163、excluded=23814、ignored=3546、ignored 候选=0、uncovered=0、dirty=True。

**方法**：先静态审 gate→smoke→GateSupport 的实际调用链与分支接线，再跑独立负例（纯临时 git 仓库/文本 ZIP/替身 stub），最后整包复跑既有两套离线夹具。未运行真实 App、未 dotnet build/test/publish、未生成产品 ZIP、未触发 Actions/UIA/键鼠、未杀任何非自有进程、未改 ACL、未提交。

**边界**：P10 task54 的 src/Core 实现不在本工位范围；仅按冻结协议核对了结果校验器字段（scripts/lib/SmokeResult.ps1:49-129），未重审 Core/SmokeSession.cs。真实 App 行为、桌面窗口、ZIP 实际出包仍未验证。

## 三、逐项判据

### B1–B6 / M6 原闭环：pass

- B1 环境变量注入：release.yml 中不可信输入只经 env 传入（release.yml:41-44），PowerShell 从 `$env:` 读取（:50），未插入脚本正文。
- B2/B5 版本与产物真实绑定：gate 先解析/严格校验版本（gate.ps1:41-46），再 build/publish（:134、152-163），产物版本断言在 smoke 之前（:165-182）；非法版本在外部命令前抛出（SourceIdentity.ps1:111-230 的 `^...$` 严格形状，gate.ps1:36-45 还限制 Runtime/Mode 形状）。
- B3/B4 输入身份与严格 skip：身份含 HEAD+dirty+确定性指纹（gate.ps1:49、502-534；SourceIdentity.ps1:242-334）；`-SkipSmoke/-NoZip` 只能 LIMITED exit 2，绝不 PASS（gate.ps1:9-12、585-595；ci.yml:4 明令禁止 CI 传这两个开关）。
- B6 自有清理：smoke PASS 才清理自己的临时根，失败保留取证（smoke.ps1:191-211）；gate 侧只在 smoke 通过且路径位于 temp 下才清理，异常仅告警（gate.ps1:267-283）。
- M6 不在本单实现改动内；旧离线 136/136 与 P11 137/137 均通过，未观察到原闭环回归（详见第四节）。

### B7（--smoke 唯一注入与真实 argv）：pass

- 构造点唯一：smoke.ps1:71-77 复制调用方参数后只 Add 一次 `$modeArgument`；:78-81 用 Ordinal 计数，不恰为 1 即抛错；:82-84 才写入 ArgumentList。
- 调用方重复传入 `--smoke` 在建根之前拒绝（:34-40），不会先产生副作用。
- 真实 argv 记录：:112-114 打印实际 argv 与 `SMOKE_ARGV_JSON=`；:168-175 才打印子进程输出，故真实标记先落盘；gate 用 Get-LogMarker 取最后一条（gate.ps1:106-115）并要求恰一个 `--smoke`（:233-254）。
- gate 侧 smoke 是独立子进程：`& pwsh -NoProfile -File smoke.ps1 -ExePath <outDir>/IshWY.exe -KeepRoot`（gate.ps1:87-104、229-230），smoke 退出不会带走 gate，gate 读取 `$LASTEXITCODE` 后判失败。
- 独立实跑：正向 exit 0、argv 4 项、modeCount=1、nonce 32、success=true；调用方重复 exit 1（“不得包含”）；把 :73 的 Append 行替换为注释的变异副本 exit 1，内部计数守卫触发且**子进程未启动**（证明不是靠后续校验兜底）。env-only/no-result 由 137 夹具覆盖（p11-behavior-fixtures.ps1:170-183），整包通过。

### B8（输出/ZIP 逐项绑定与 fail-closed）：pass（有已声明的非原子边界）

- 前置清单：publish stage 之后、smoke 之前生成 path/length/SHA256 清单（gate.ps1:184-207；GateSupport.ps1:206-286），清单不 Ok 直接置 outputStable=false 并 fail（:196-207）。
- 后置重采样与比较：smoke 之后重采样并 Compare-OutputManifests（gate.ps1:286-305；GateSupport.ps1:288-380），新增/删除/内容变化/目录变化任一即失败，gate 置 `$script:failed`。不稳定时 ZIP 被跳过（gate.ps1:353-357），verdict 有 `OutputStable` 硬失败位（:445-459、665-729），不会出现“不稳定仍 createdThisRun=true”。
- ZIP 逐条目：创建后取实际 SHA256 并与 marker 对照（gate.ps1:374-389），再 Get-ZipManifest + Test-ZipMatchesOutput（:394-395；GateSupport.ps1:405-504、506-663）；覆盖缺失/额外/内容变化/重复条目（OrdinalIgnoreCase）/路径歧义（空段、`..`、反斜杠、盘符冒号、双斜杠、NUL、超长）/读取错误；`entriesVerified` 与 `createdThisRun` 仅在 Ok 后置 true（gate.ps1:402-414），并由 verdict 的 ZipCreated 消费（:451）。
- 前缀：`RootPrefix = Split-Path -Leaf $outDir`（gate.ps1:394），空前缀显式失败（GateSupport.ps1:506-513 附近，实测 reason=root-prefix-empty），不靠“按名称猜”。
- 真实 publish ZIP 形态核对：publish.ps1:50-51 用 `CreateFromDirectory(..., includeBaseDirectory: true)`；本机 pwsh7/.NET 实测只产生 `IshWY-1.3.1-win-x64/a.dll`、`.../sub/b.json`，无基目录条目，配合 leaf 前缀 compOk=True、entries=2，因此该接线在真实出包路径上成立。
- 独立负例（文本 ZIP + 真实 GateSupport 函数）：合法 Ok=True；多文件 `zip-file-added:extra.txt`；缺文件 `zip-file-missing:sub/b.json`；改内容 `zip-file-changed:a.dll`；重复条目 `zip-entry-duplicate:...` 且 manifest 不 Ok；`../evil.txt` 与 `sub\\b.json` 均 `zip-entry-unsafe-path`；空 ZIP `zip-entries-empty`；额外目录 `zip-entry-extra-directory:extra`；改文件后重采样 `file-changed:sub/b.json`（Compare 参数为 Before,After）。全部 fail-closed。
- 已声明的边界：gate 自己在 notes 写明“不防同权限恶意进程的瞬时替换/并发写入，不提供 OS 沙箱或原子快照”（gate.ps1:482）；清单→ZIP 之间仍存在 TOCTOU 窗口。这属已披露限制，不据此判失败，但不得对外宣称原子或抗瞬时换包。

### B9（ignored/未知目录/未知扩展默认纳入）：**needs_revision（BLK-1）**

- 机制现状：未命中排除规则的 ignored 文件默认成为候选（SourceIdentity.ps1:88-109）；未知顶层目录、未知扩展、无扩展名实测都会成为候选（见第四节探针：`src/x.unknown`、`newtop/payload.dat`），这部分通过。
- 缺口：8 个后缀被无条件豁免（SourceIdentity.ps1:41-46、104-107），理由是“截图/日志是本仓库已知的取证与噪声文件”。该理由**不成立于构建图**：
  - `src/PrtScr.App/PrtScr.App.csproj:39` 的 `<Content Include="Assets\**\*" CopyToOutputDirectory="PreserveNewest" />` 会把 Assets 下任意后缀（含 png/jpg/gif/webp/ico/log）复制进输出；
  - `src/PrtScr.App/PrtScr.App.csproj:17` 的 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` 直接证明 `.ico` 是构建输入；
  - 仓库 .gitignore:11 忽略 `*.log`、:12 忽略 `clip_probe*.png`，因此 Assets 下出现同名日志/探针截图时会被 git 标为 ignored，却仍被上面的 Content glob 复制进输出；
  - 任意新 csproj 的 Content/EmbeddedResource/None-CopyToOutput 都可使其它后缀成为输入，扩展名无法决定“是否构建输入”。
- 后果：ignored 的 `src/PrtScr.App/Assets/logo.png` / `icon2.ico` 会进发布目录和 ZIP，但 `Get-SourceIdentity` 既不将其纳入 Inputs，也不列为 ignored 候选/uncovered；gate 只写“候选”到 source-ignored-inputs.txt（gate.ps1:437-438）、只写路径形状排除到 source-excluded-inputs.txt（:435-436），被后缀豁免的文件在两份证据里都不出现。于是 `source.covered=true` 可以与“输出含未绑定源码哈希的字节”并存，B9 的“显式非构建排除必须可审计”失败。
- 当前实例数 0（真仓库候选=0、uncovered=0，唯一 tracked 图片 app.ico 已被纳入），故不是“现在已有输入漏掉”；但受控执行恰好是在本地 dirty 树上构建，机制缺口必须先在门禁里修掉，否则“同 SHA 源码→产物”的证明在本地不成立。
- 返工方向（属 B9/E08 原判据，不是新增需求）：改为从构建项（csproj/props/targets 的 Content/EmbeddedResource/ApplicationIcon 等）推导输入，或对被忽略文件按“路径+是否位于源目录”判定；至少把 `src/**/Assets/**` 下所有 ignored 文件按候选处理，并把每个豁免写成带 per-file reason 的可审计记录。

### M2（双流 ≥512KiB 与 2 秒自有超时）：pass

- 读取顺序正确：先 ReadToEndAsync 再 WaitForExit（smoke.ps1:117-118、120），等待读任务结束（:130-141），不会因管道写满死锁。
- 独立实跑：large-output 场景 stdout=538639 字符、stderr=538639 字符（均 ≥512KiB=524288），两端 END 标记均到达，exit 0；2 秒超时场景 wall≈2.58s、reason=timeout-2s、exit 1、无结果文件、子进程已不在。
- 只处置自有子进程：只对 `$process` 调 Kill（:120-125），未按名称/PID 扫杀；孙进程不保证随杀，属已声明限制。

### M3（清理失败仅告警）：pass

- smoke PASS 路径的清理在 try/catch 中，失败仅 Write-Warning 且仍 exit 0（smoke.ps1:191-203）；gate 侧同样只在 temp 前缀下清理并告警（gate.ps1:267-283）。夹具 `M3-cleanup-warning-only` 在 137 中通过（needle=临时冒烟根清理失败）。

### E01–E10

| ID | 判定 | 依据与行号 |
|---|---|---|
| E01 | pass | 8 字段严格集合（SmokeResult.ps1:49、70-74）；缺文件/坏 JSON/schemaVersion/旧 nonce/错 PID/错 exe 路径/错 dataRoot/success=false/checks 缺失或多余或重复/error 非空全部有独立 reason（:55-129）；offline-negatives.ps1:104-113 逐项变异断言 |
| E02 | pass | 有界超时 + 只杀自有 `$process`（smoke.ps1:120-141、149-166）；实测 2.58s 退出 1、无结果；孙进程限制已声明 |
| E03 | pass | `-SkipSmoke` 只能 LIMITED exit 2（gate.ps1:9-12、461-472、585-595）；CI 明令不传（ci.yml:4、34），Release 不传（release.yml:65-69） |
| E04 | pass | 版本/模式/Runtime 在外部命令前严格校验（gate.ps1:33-46；SourceIdentity.ps1:111-230），非法形状/字符直接抛；release 侧另经 ConvertTo-StrictVersion（release.yml:50-51） |
| E05 | pass | smoke 前后 HEAD+指纹+dirty 全等才稳定（gate.ps1:307-318），不稳定置 hard fail 并跳过 ZIP（:348-357）；manifest 同时记 before/after（:515-534） |
| E06 | pass | 指纹为排序后的 path NUL sha LF（SourceIdentity.ps1:242-309），dirty 计数与状态入 manifest（:517-533）；本轮 dirty=51 被如实记录，不冒称纯 HEAD |
| E07 | pass | release 模式 dirty 直接抛（gate.ps1:49-53；SourceIdentity.ps1:336-349），工作流只经同一 gate 入口（release.yml:65-69） |
| E08 | **needs_revision** | 同 BLK-1：ignored 图片/ico 可成为构建输入却完全不入证据；当前实例 0，但“构建输入全覆盖”的机制不成立。未用 137 夹具替代该判断 |
| E09 | pass（本轮未出真实包） | 每文件 SHA256（gate.ps1:428-433）、ZIP 文件 SHA256（:374-389）与逐条目哈希（GateSupport.ps1:405-663）；manifest 记 gates/版本/SDK/运行参数（gate.ps1:502-570；环境含 os/pwsh/dotnet/culture，:514 + SourceIdentity.ps1:351-367）；本工位未运行真实包，故 E09 只能判“实现静态通过、实际包未验证” |
| E10 | pass（静态） | 不可信输入仅 env（release.yml:41-44）；先校验后写 GITHUB_OUTPUT（:50-62）；tag 与版本一致性校验（:53-60）；gate 为唯一入口（ci.yml:34、release.yml:65-69）；gh release 用 shell 变量与 env token（:89-108），run 正文无表达式拼接；本轮无远端执行 |

## 四、实跑证据（命令、退出码、计数）

1. 解析（不执行）9 个 ps1：smoke.ps1、gate.ps1、GateSupport.ps1、SourceIdentity.ps1、SmokeResult.ps1、offline-negatives.ps1、p11-behavior-fixtures.ps1、smoke-stub.ps1、publish.ps1 → `PARSE OK x9, PARSE_BAD=0`，exit 0。
2. `pwsh -NoProfile -File scripts/tests/offline-negatives.ps1` → `OFFLINE NEGATIVES PASS：136/136`，exit 0。
3. `pwsh -NoProfile -File scripts/tests/p11-behavior-fixtures.ps1` → `P11 BEHAVIOR PASS：137/137`，exit 0；M3 行 `ok M3-cleanup-warning-only`。分组计数 32/77/15/12/1 为返工报告自报，本工位复跑只独立确认了总数 137/137 与逐条 ok 行。
4. B7 独立运行（stub 替身，真实 smoke.ps1）：
   - 正向：exit 0，argv=`-NoProfile|-File|<stub>|--smoke`（4 项），modeCount=1，success=true，nonce 长度 32。
   - 调用方重复 `--smoke`：exit 1，抛“ProcessArguments 不得包含”。
   - 变异副本删除 `[void]$effectiveArguments.Add($modeArgument)`：exit 1，内部计数守卫触发，argv-capture 未生成（子进程未启动）。
5. M2：large-output stdout/stderr 各 538639 字符，双 END 到达，exit 0；2s 超时 wall 2.58s，exit 1，reason timeout-2s，子进程已退出，无 result 文件。
6. B8（临时文本 ZIP/临时输出树，调用真实 GateSupport）：manifest Ok=True files=2；合法 ZIP compOk=True entries=2；多文件/缺文件/改内容/重复条目/遍历路径/反斜杠/空 ZIP/额外目录/空前缀各命中对应 reason（见 B8 节）；改文件后 Compare Ok=False reason `file-changed:sub/b.json`。真实 `CreateFromDirectory` 输出配合 leaf 前缀 compOk=True（无基目录条目）。
7. B9 探针（临时 git 仓库，真实 SourceIdentity；已删除）：
   - 真仓库：inputs=163 excluded=23814 ignored=3546 candidates=0 uncovered=0 dirty=True。
   - 探针 A（.gitignore 为 `*.png/*.log/*.unknown/newtop/`）：tracked `src/Code.cs`、`Assets/app.ico`、csproj；ignored `Assets/logo.png`、`notes.log`、`src/x.unknown`、`newtop/payload.dat`；未忽略的 `Assets/icon2.ico` 作为普通 untracked 输入出现。结果 Inputs 含 3 个 tracked 与 icon2.ico；ignored 候选只有 `payload.dat` 与 `src/x.unknown`；`Assets/logo.png` 既不在 Inputs 也不在候选/uncovered，`Test-IgnoredBuildInputCandidate` 返回 False。
   - 探针 B（另加 `*.ico` 忽略，`app.ico` 用 `git add -f` 保持 tracked）：Inputs 只剩 3 个 tracked，ignored `Assets/icon2.ico` 同样不在候选也不在 uncovered，`Test-IgnoredBuildInputCandidate` 返回 False。即 Assets 下的 png 与 ico 都被后缀豁免静默吞掉。
8. 清理：探针使用 %TEMP%，已删除；仓库内仅本报告为新增，未留其它产物，未动他人 dirty。

## 五、阻断、残留与许可边界

**阻断（必须返工后才可能 pass）**

- **BLK-1（B9/E08）**：ignored 后缀豁免对 png/jpg/jpeg/gif/bmp/webp/ico/log 无条件生效（SourceIdentity.ps1:41-46、104-107），与 App csproj 的 Assets 全量 Content 复制（PrtScr.App.csproj:39）和 ApplicationIcon（:17）冲突；此类文件可进发布目录/ZIP 却不入 source-inputs、不进候选/uncovered，gate 证据中完全不可见（gate.ps1:435-440）。修法见 B9 节；修复后需重新独立复核，本轮不自动开第 3 轮。

**非阻断残留（已披露或低风险，不改变本轮判定）**

- R-1 非原子/瞬时替换：清单→ZIP 存在 TOCTOU，无 OS 快照/原子发布保证（gate.ps1:482 已明示）。
- R-2 smoke 日志标记可被恶意子进程伪造 `SMOKE_ARGV_JSON=`（smoke.ps1:112-114 先于 :168-175 的子输出；gate 取最后一条，gate.ps1:106-115）。真实 argv 由构造保证，伪造最多致误拒，不能造成假通过；建议改为子进程退出后打印标记或绑定 PID 校验。
- R-3 超时只杀直接子进程，孙进程不保证清理（smoke.ps1:120-125）。
- R-4 manifest 有 mode（gate.ps1:506），但 `-SkipSmoke/-NoZip` 无显式布尔字段，仅由 unverified/notes 侧证（:461-472、565）；建议补显式运行参数，便于 E09 机械核验。
- R-5 tag push（`v*`）会自动触发 Release 工作流（release.yml:5-7）；创建 tag 即接近创建 Release，按用户铁律必须先获明确同意。另托管 runner 的桌面 smoke 可行性本轮未验证（release 不传 -SkipSmoke）。
- R-6 publish 固定 `win-x64`（publish.ps1:28；gate.ps1:20），arm64 路径未验证。
- R-7 外部进程 stderr 在本机按 GBK 写出，父进程按 UTF-8 读会产生替换字符；夹具已改用同进程异常原文（repair 报告已披露），不影响门禁逻辑。
- R-8 输出清单/裁剪对 reparse point（符号链接/联接）未做专门负例；S02 类条件性 reparse 未在本轮断言。
- R-9 排除段用大小写不敏感 `-match`（SourceIdentity.ps1:62-65），未来若出现 `src/Data/` 会被当作 `generated:data` 排除；当前无此路径，且会出现在 source-excluded-inputs.txt（可审计），仍建议改大小写敏感或路径前缀规则。

**局限**

- 未运行真实 App/真实 smoke、未 build/test/publish、未生成产品 ZIP、未触发 Actions/UIA/键鼠/杀用户进程；B7/M2 用替身 stub，B8 用文本 ZIP 与真实 helper，不能替代真实出包验证。
- 未重审 P10 Core/SmokeSession 实现（task54 范围）；本报告不评价 P10 侧结论。
- 未验证真实 Windows 桌面会话下 smoke 的时序/窗口行为；E09 的真实样本与 manifest 需在受控执行时另行核对。

**最终许可表述**

- 本判定为 **needs_revision**：在 BLK-1 修复并经独立复核通过之前，不建议 lead 开受控执行；即使修复通过且 P10 task54 也 pass，也只授权 lead 受控执行（真实 smoke/出包观察），仍不构成运行验收或发布许可，不解除 tag/Release 冻结，不得自行打 tag 或创建 Release。

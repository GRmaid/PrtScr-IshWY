# P11 第 1 轮返工独立复核（task-52）

- 复核时间：2026-11-07 21:40–21:55（Asia/Shanghai）
- 复核对象：HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7` 工作树（dirty 43 项）
- 契约：`Record/plans/P10-P11-safe-gates.md`、`Record/plans/P11-round1-repair.md`（含 21:38 B7 增补）、`Record/plans/P10-P11-acceptance-matrix.md`
- 复核方式：只读静态审查 + 离线用例复跑 + 只读源指纹探针；**未**执行 build/publish/smoke/ZIP/Actions/UIA，未修改产品、scripts、CI，未 commit/push，未派子代理，未调用 agent_teams_*。
- 本工位唯一写入：本文件。
- 结论：**needs_revision**（B7 仍为硬阻断；B1–B6、M2/M3/M6 静态通过但带残余风险；E09 真实样本未验收，静态安全许可不成立，禁止真实执行）。

---

## 一、结论与门禁判定

### 1.1 总结论

| 项 | 判定 | 摘要 |
|---|---|---|
| B1 版本来源只走 env + 严格校验 | 静态通过 | `release.yml` 的 run 体内无 `${{ }}` 插值；版本先 `ConvertTo-StrictVersion` 再写 GITHUB_OUTPUT |
| B2 版本透传与精确比较 | 静态通过（运行态未验） | `-p:Version`/`-p:FileVersion` 透传 + 发布后自检 + gate 前置断言 + Ordinal 精确比较 |
| B3 默认纳入 + ignored/未覆盖缺口 | 静态通过 | 默认纳入指纹、排除清单有据、覆盖缺口硬失败；真机探针 161 输入/0 缺口 |
| B4 严格 CI + limited 非 0 | 静态通过 | CI 只用 `-Mode ci`，无跳过开关；LIMITED 退 2 |
| B5 证据闭合/哈希/Dirty/存在性 | 静态通过（有残余） | 唯一证据目录、源身份前后比对、工件与结果哈希；ZIP 与工件内容未做运行后重绑 |
| B6 ZIP 仅在 smoke 通过且稳定后创建 | 静态通过（有残余） | 阶段发布强制 `-SkipSmoke -NoZip`；ZIP 分支三重前置；ZipOnly 受 gate 约束 |
| **B7 smoke 启动必须带 `--smoke`** | **不通过（硬阻断）** | `scripts/smoke.ps1` 从不追加 `--smoke`，只注入 env |
| M2 子进程读取/超时/退出码 | 静态通过（用例不足） | 先异步读、再限时等待、只杀自有进程；缺受控大输出/超时夹具 |
| M3 失败保留根目录、清理不阻断判定 | 静态通过 | 失败保留；清理异常只 warning |
| M6 证据目录唯一性 | 静态通过 | 时间戳毫秒 + 8 位随机，拒绝复用已有目录 |

### 1.2 为什么现在必须判 needs_revision

1. `scripts/smoke.ps1:51-62` 只设置 `ISHWY_SMOKE_ROOT`（58）与 `ISHWY_SMOKE_NONCE`（59），随后仅把调用方 `$ProcessArguments`（默认空数组，26 行注释明确「gate/publish 从不传」）加入 argv（60-62），**全脚本没有任何位置追加 `--smoke`**；81 行日志却打印 `==> --smoke pid={0}`，属误导性文本。
2. 两处唯一调用方同样不传该参数：`scripts/gate.ps1:216`、`scripts/publish.ps1:245`；gate 的跳过命令展示串（`gate.ps1:207`、`212`）也不含 `--smoke`。
3. App 侧以 argv 为唯一开关：`src/PrtScr.Core/SmokeProtocol.cs:15` `ModeArgument = "--smoke"`；`Parse` 41-44 在无该参数时直接 `NotRequested()`，env 只在 46-47 才读取；`src/PrtScr.App/Services/SmokeSession.cs:36/58-61` 仅当解析成功才置 `IsRequested=true`。
4. 因此当前 smoke 启动等价于**普通启动**：`App.xaml.cs:104` 以 `activateExisting: !SmokeSession.IsRequested` 调用单实例；已有用户实例时走 123-124 激活旧实例并 `ExitProcess(0)`，无新窗口、无结果文件；无用户实例时走正常初始化（`App.xaml.cs:61/87/131/139/150/172` 的 guard 全部不生效），会加载真实配置、注册热键、写真实数据，30s 后被 `smoke.ps1:88/91` 杀掉，最终 `result-missing`。
5. 契约明确要求程序以 `IshWY.exe --smoke` 启动（`P10-P11-safe-gates.md:14`），且 21:38 增补要求 `smoke.ps1` **自行**保证恰有一个有效 `--smoke`、不依赖调用者，并用离线替身进程记录 argv+env 的正反例（`P11-round1-repair.md:33-35`）。返工报告（`Record/reports/P11-round1-repair.md`）全文 101 行无一处提及 B7，说明该报告成文早于 B7 增补，**不能视为已修**。
6. 后果不是「冒烟可能失败」，而是**门禁 PASS 路径不可达 + 存在用户可见副作用**：只要运行 smoke 就会激活/干扰用户正在使用的实例，或写入真实 data 目录。故在 B7 修复并由离线正反例证明前，禁止任何真实 smoke/gate/publish，E09 亦不得采样本。

### 1.3 本轮不成立的许可

- **静态安全许可不成立**：B7 未修复，脚本层的「安全启动」前提缺失。
- **E09 真实样本验收不成立**：未运行、也无权运行；报告只覆盖静态与离线层。
- 旧 `Record/reports/P11-independent-review.md` 的五项阻断中，B1–B6 在本轮已修补且静态可核；但该报告的 B7 缺项正是本轮新增阻断，不能用旧结论代替。

---

## 二、逐条独立复核（判据 / 证据 / 命令 / 判定）

### B1 版本输入只经 env 且先校验后输出 —— 静态通过

判据（safe-gates 与 B1 契约）：workflow run 体内不得出现 `${{ }}` 表达式插值；dispatch 的 `inputs.version` 与 tag 的 `REF_NAME` 只经 env 进入脚本；版本必须先严格校验，校验失败不得写出 GITHUB_OUTPUT。

证据：
- `.github/workflows/release.yml:41-44` 步骤级 env `EVENT_NAME/INPUT_VERSION/REF_NAME` 均由 `${{ }}` 赋值（允许位置）；`45-63` run 体无任何 `${{ }}`。
- `50`：`$raw = if ($env:EVENT_NAME -eq 'workflow_dispatch') { $env:INPUT_VERSION } else { $env:REF_NAME }`。
- `51`：`$version = ConvertTo-StrictVersion $raw`；`53-57` tag 路径先 `$tag='v'+$version` 再 `-cne` 比对；`58-59` `git check-ref-format` 并检查 LASTEXITCODE；`62` 校验全部通过后才 `Out-File -FilePath $env:GITHUB_OUTPUT -Append`。
- `ConvertTo-StrictVersion`（`scripts/lib/SourceIdentity.ps1:127-151`）：拒空、拒空白、拒控制字符、只剥一个前导 `v`；`Test-VersionString`（117）正则 `^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$`；`Assert-VersionString`（120-125）兜底。
- `65-69` gate 步骤以 `steps.version.outputs.version` 作 env `VERSION`；`89-108` gh release 步骤 env `GH_TOKEN/VERSION/IS_TAG_PUSH`，body 内 `$tag='v'+$env:VERSION`（97）、ZIP 路径由 VERSION 组装（98）、存在性检查（99-101），无 `${{ }}`。
- 全文件 11 处 `${{ }}` 均位于 name/group/env/metadata（19、42、43、44、68、76、84、92、93、94），无一在 run 体。

独立命令：pyyaml `safe_load` 两 workflow → `YAML OK`，EXIT=0（仅语法，未做 Actions schema 校验）。

判定：通过。残余：release 触发后自动创建 Release（见 R-7）。

### B2 版本透传与精确比较 —— 静态通过（运行态未验）

判据：`-p:Version` 与 `-p:FileVersion` 必须真正透传；发布产物、gate、ZIP 名称与 manifest 记录同一版本；比较必须是精确串比较，`1.3.1` 不得被 `1.3.10` 命中。

证据：
- `scripts/publish.ps1:108-121` dotnet publish 参数含 `"-p:Version=$Version"`（114）与 `"-p:FileVersion=$($shape.FileVersion)"`（115）；`$shape` 来自 `Get-VersionShape`（79）。
- `publish.ps1:225-234` 发布后自检：要求 `IshWY.dll` 存在，读取 `FileVersionInfo`，调用 `Test-ArtifactVersionMatch -Expected $Version -ProductVersion $dllInfo.ProductVersion -FileVersion $dllInfo.FileVersion`，不匹配抛错。
- `SourceIdentity.ps1:187-225`：ProductVersion 去 `+metadata`（169-185）后 `[string]::Equals(..., Ordinal)`（208）；FileVersion 必须等于 `X.Y.Z.0`（215）；失败原因 `product-version-missing/mismatch`、`file-version-missing/mismatch`。
- `GateSupport.ps1:106-200` `Test-PublishArtifact`：outDir 存在且非空（136-147）、exe/dll/pri/*.xbf 存在（149-160）、dll Product/File 精确匹配（162-175）、exe apphost 版本为空或 `^0\.0\.0(\.0)?$` 时记 `dll-authoritative`（183-185），否则精确匹配（192-196）。
- `publish.ps1:236-249` 在 smoke 前已完成上述自检；ZIP 名称与 manifest 的 `version` 全部取自同一 `$Version`。

独立命令：静态阅读 + Parser 解析通过（见 §5）。未运行 dotnet publish，故 `exeVersionSource=dll-authoritative` 的真实走向未证实（见 R-2、§4 未运行项）。

判定：通过（脚本语义）。残余：apphost 为 `0.0.0.0` 的分支只能在真实 publish 下确认；旧报告已列为风险，本轮维持。

### B3 默认纳入 + ignored 覆盖缺口 —— 静态通过（真机探针通过）

判据：指纹必须默认纳入所有构建输入（而非白名单），排除项须有明确、可解释的规则；`git status --ignored` 揭示且看起来像构建输入的未覆盖项必须报缺口；缺口必须使 gate 硬失败。

证据：
- `SourceIdentity.ps1:29-38` 排除段：`bin,obj,.tmp,artifacts,data,node_modules,publish,.vs,.vscode,.idea,TestResults`；顶层目录 `Record,docs,handoff,.dsh-memory,.agent-teams`；顶层文件 `README.md,LICENSE,LICENSE.txt,.gitignore,.gitattributes`；`40-70` `Get-BuildInputExclusionReason` 另有 `^README\.[A-Za-z0-9]+$ / ^LICENSE\.[A-Za-z0-9]+$`（66）。
- `72-78` `Test-BuildInputPath` = 默认纳入；`80-104` `Test-IgnoredBuildInputCandidate` 对 `^(src|tests|test|scripts|tools|build|eng)/`、`^\.github/` 或根级构建后缀（sln/slnx/csproj/props/targets/json/ps1/psm1/yml/yaml/cs/xml/txt/config/manifest）的 ignored 文件报候选。
- `237-304` `Get-SourceFingerprint`：以 `git ls-files -co --exclude-standard` 与 `-oi --exclude-standard` 取文件，逐文件 SHA256，不可读/哈希失败进 `UncoveredInputs`，指纹 = 排序后 `path\0sha256\n` 的 SHA256；`IgnoredInputCandidates` 排序记录。
- `306-329` `Get-SourceIdentity` 另取 `git status --porcelain --untracked-files=normal` 得 `Dirty/DirtyFileCount`。
- `gate.ps1:55-59` 覆盖判定：`IgnoredInputCandidates` 与 `UncoveredInputs` 均须为 0；`130-132` 打印缺口；缺口进 `hardFailures`（`GateSupport.ps1:228-233`），使 gate FAIL。

独立命令（只读，EXIT=0）：`Get-SourceIdentity -RepoRoot .` →
`HEAD=5e74f62…, Dirty=True, DirtyFileCount=43, InputCount=161, IgnoredInputCandidates=0, UncoveredInputs=0, ExcludedInputs=23356, TmpInputs=0, NativeMethodsInputs=6`。
`RootInputsTop` = `.editorconfig, .github, 大佬修改材料, Directory.Build.props, Directory.Packages.props, global.json, PrtScr.sln, scripts, src, tools`。
排除明细抽样以 generated:`.tmp`、`analysis/`、`lite-publish/` 为主（23356 条），另有 `.gitignore` 标为非构建。

判定：通过。残余：见 R-3（用户材料目录 `大佬修改材料/` 被计入 CI 指纹）与 R-4（ignored 候选启发式仅覆盖已知前缀）。

### B4 严格 CI 与 LIMITED 非 0 —— 静态通过

判据：CI 不使用任何跳过开关；evidence 必须 always 上传；gate 的 LIMITED 分支不得退 0；凡跳过 smoke/ZIP 或缺证据必须落入 LIMITED/FAIL。

证据：
- `.github/workflows/ci.yml:32-34` `run: ./scripts/gate.ps1 -Mode ci -Version 0.0.0-ci`，无 `-SkipSmoke`/`-NoZip`；`37-44` `if: always()` 上传 `artifacts/evidence`，retention 14；`5-9` 触发 push main / pull_request / workflow_dispatch；`11-12` `contents: read`；`14` timeout 60。
- `gate.ps1:205-213` `-SkipSmoke` → smoke `not-run`/`skipped-by-request`，并打印 LIMITED 警告；`275-289` ZIP 跳过原因 `skipped-by-request`/`smoke-<status>`/`source-unstable`；`482-490` 仅 verdict `passed` 打 GATE PASS，否则 LIMITED/FAIL；`492` `exit $verdict.ExitCode`。
- `GateSupport.ps1:202-264` `Get-GateVerdict`：硬失败 `core-gates-not-passed/artifact-not-verified/source-input-coverage-gap/source-changed-during-gate/smoke-failed/zip-failed`（228-233）；LIMITED 原因 `smoke-skipped-by-request/smoke-not-run/zip-skipped-by-request/zip-not-created-this-run`（235-238）；`limited=2 / failed=1 / passed=0`（240-251）；`releaseReady` 需 passed + release + clean（254）。
- 全局 grep：`-SkipSmoke`/`-NoZip` 只出现在 gate/publish 的**文档注释**与离线用例的断言文本中，CI 实际命令无跳过。

判定：通过。残余：CI 依赖 windows-latest 桌面能力，真实运行大概率 fail-closed（R-6），属契约接受的「宁可红」。

### B5 证据闭合 / 哈希 / Dirty / 存在性 —— 静态通过（有残余）

判据：证据目录唯一；源身份在 build 前后比对（HEAD+fingerprint+Dirty）且变化则硬失败；工件与 smoke 结果必须落哈希；manifest 必须记录版本、源身份、门禁结果、工件、冒烟、ZIP、哈希与 verdict，并声明未验证项。

证据：
- `gate.ps1:249-260` 前后比对 `Head/Fingerprint/Dirty`，差异 → `source-changed-during-gate`（硬失败）。
- `184-196` 工件文件清单与 `artifact-files.sha256`（仅当清单非空）；`117-126` `New-EvidenceFileRecord` 产 `{path,sha256,bytes,exists}`。
- `198-247` smoke 步骤：参数 216；从日志取 `RESULT_PATH/SMOKE_ROOT`（219-220）；把结果复制进 `evidence/smoke/smoke-result.json`（221-228）；仅通过时清理根目录（230-246）。
- `373-402` unverified 列表、notes、hashRecords（sourceInputs/excludedInputs/artifactFiles/smokeResult）。
- `405-467` manifest `schemaVersion:2`：version/runtime/environment/source(before+after)/gates/artifact(exeVersion,dllVersion,exeVersionSource,filesFile)/smoke(status,skippedReason,resultFile,resultSha256,root)/zip/hashes/verdict/complete/exitCode/unverified/releaseReady/hardFailures/reasons/notes；`469-470` 落盘。
- `lib/SmokeResult.ps1:35-137` 校验：8 字段精确集合（70-74）、schemaVersion=1（76-79）、nonce 忽略大小写相等（81-84）、pid（86-89）、exe 绝对路径（91-95）、dataRoot（97-101）、success 严格 bool true（103-106）、checks 恰 3 且集合相等（108-124）、error null（126-129）。

独立命令：Parser 解析 `gate.ps1`/`SmokeResult.ps1` 通过；离线用例覆盖唯一证据目录、跳过语义与顺序（见 §3）。未运行真实 gate（会 build/publish/smoke），故哈希链只在静态层核对。

判定：通过。残余见 R-1（工件哈希在 smoke 前算，ZIP 在 smoke 后建，二者无内容重绑；smoke 结果不含 exe 哈希），R-2（早抛路径无 manifest）。

### B6 ZIP 仅在 smoke 通过且源稳定后创建 —— 静态通过（有残余）

判据：阶段发布不得产出 ZIP；ZIP 必须在 smoke=passed 且 stability 通过后创建；ZIP 的路径/存在性/哈希必须实测并与本 run 绑定；ZipOnly 不得成为绕过路径。

证据：
- `gate.ps1:152-163` 阶段发布恒带 `'-SkipSmoke','-NoZip'`（157）→ `publish.ps1:102-257` 不建 ZIP（254 判断）。
- `gate.ps1:262-338`：`-NoZip` → skipped（275-276）；`$smokeStatus -ne 'passed'` → `smoke-<status>`（277-279）；`-not $stable` → `source-unstable`（280-282）；否则调 `publish.ps1 -ZipOnly`（291），解析 `ZIP_PATH/ZIP_SHA256/ZIP_BYTES`（294-296），校验步骤通过、路径已报、规范化路径等于 `"$outDir.zip"`（306）、文件实际存在（310）、实测 SHA256 等于上报值（314-321），通过才置 `createdThisRun=true`（322-327）；失败 → `zip-failed` 硬失败（334-337）。
- `publish.ps1:85-100` `-ZipOnly` 要求 outDir 为目录且 `IshWY.exe`/`IshWY.pri` 存在，随后 `New-PortableZip`（40-61，先删旧目标再 `CreateFromDirectory`），不自行复验版本/smoke —— 因为 gate 已在 291 行前完成全部前置；直接手工 `-ZipOnly` 属契约外路径，无证据、无 manifest。
- `GateSupport.ps1:235-238` ZIP 未创建（非跳过原因）→ LIMITED，不会 PASS。

判定：通过（语法/顺序/绑定）。残余同 R-1：ZIP 与 `artifact-files.sha256` 之间无逐条目比对，manifest 无 ZIP 条目清单；另手工 `publish.ps1 -ZipOnly` 可绕开 gate（无证据），属流程风险而非 gate 漏洞。

### B7 smoke 启动必须恰有 `--smoke`（且 env 有效）—— **不通过（硬阻断）**

判据：
1. `smoke.ps1` 自行保证传给 App 的 argv 恰含一个 `--smoke`，不依赖 gate/publish 传参；
2. env `ISHWY_SMOKE_ROOT`/`ISHWY_SMOKE_NONCE` 与 argv 同源、同次、可校验；
3. gate/publish 两处调用接口保持一致（无需各自补参数）；
4. 离线替身进程记录 argv+env 的正反例：正例恰有 `--smoke`，反例（仅 env、无参数）证明不会进入 smoke 模式；全程不启动真实 App。

证据（逐项）：
- `scripts/smoke.ps1:16-27` 参数：`ExePath`（必填）、`TimeoutSeconds`（30）、`KeepRoot`、`[string[]]$ProcessArguments = @()`（26，注释「仅离线用例/诊断使用；gate/publish 从不传」）。
- `scripts/smoke.ps1:51-62`：`$startInfo.Environment['ISHWY_SMOKE_ROOT']=...`（58）、`...['ISHWY_SMOKE_NONCE']=...`（59）、`foreach ($argument in $ProcessArguments) { [void]$startInfo.ArgumentList.Add($argument) }`（60-62）。**无 `--smoke`**。
- `scripts/smoke.ps1:81` 日志文本 `==> --smoke pid={0}` 与实际 argv 不符，会误导审查者与日志读者。
- 调用方：`gate.ps1:216`（`-ExePath $exePath -KeepRoot`）、`publish.ps1:238/245`（`-ExePath $exe`）；`gate.ps1:207/212` 的跳过命令串亦无 `--smoke`。
- App 侧：`SmokeProtocol.cs:15` `ModeArgument="--smoke"`；`Parse` 41-44 无参即 `NotRequested()`；`SmokeSession.cs:36/58-61` 只有解析通过才 `IsRequested=true`；`SmokeSession.cs:48-72` 还会校验 root（leaf==nonce、父目录名 `IshWY-smoke`、祖父为 temp、存在/非重解析/空/可写），所以 env 只有在 flag 存在时才被消费。
- 反向证明（不运行 App，只读代码）：`App.xaml.cs:104` `activateExisting: !SmokeSession.IsRequested` → 无 flag 时 true；`App.xaml.cs:113-124` 已有实例则激活并 `ExitProcess(0)`；无实例则 `61/87/131/139/150/172` 的 smoke guard 全部不生效，走正常启动。故「env-only」不仅不进入 smoke，还会产生真实副作用。
- 离线用例：`scripts/tests/offline-negatives.ps1` 全文无 `--smoke` 断言（grep 无命中），也无替身进程夹具；这正是 136/136 全绿却仍存 B7 的原因（R-10）。
- 契约：`P10-P11-safe-gates.md:14` 要求 `IshWY.exe --smoke` + env nonce/root；`P11-round1-repair.md:33-35` 要求 smoke.ps1 自行保证、接口一致、离线 argv+env 正反例。返工报告无 B7 章节。

判定：**不通过**。返工最小集（供第 2 轮）：
1. 在 `smoke.ps1` 构造 argv 时**无条件**追加一次 `--smoke`（建议放在 `$ProcessArguments` **之后**，以便用 `pwsh -File stub.ps1 --smoke` 做替身；且重复传入时去重/拒绝，保证「恰一个」）；
2. 修正 `smoke.ps1:81` 日志与 `gate.ps1:207/212` 展示串，避免再次误导；
3. gate/publish 调用接口保持不变（它们本就不该传）；
4. 新增离线正反例：以 `pwsh.exe` 作替身 `-ExePath`、`-ProcessArguments @('-NoProfile','-File',<stub.ps1>)`，stub 把 `$args` 与两个 env 写入 JSON；正例断言恰一次 `--smoke` 且 root/nonce 同时存在，反例（把追加逻辑短路或仅注入 env 的等价场景）断言无 `--smoke` 且 stub 不会被当作 smoke；
5. 修后重跑 136 用例并新增断言，再交第 2 轮独立复核（契约上限 2 轮）。

### M2 子进程读取/超时/退出码 —— 静态通过（用例不足）

判据：读取不得死锁；超时必须限时并给出可区分原因；只终止自有进程；读任务未完成要显式失败。

证据：
- `smoke.ps1:84-86` 先 `ReadToEndAsync()` 取 stdout/stderr，再 `88` `WaitForExit($TimeoutSeconds*1000)`；`91` 仅 Kill 自己创建的 `$process`；`98-109` 读任务 `Wait(5000)`，非 `RanToCompletion` 则 `stdout-read-incomplete`；`117-134` 原因区分超时/退出码/读失败。
- `gate.ps1:87-104` `Invoke-GateStep` 用文件重定向 `*> $logPath`，非管道，避免父进程读阻塞。

独立命令：Parser 通过；离线用例有静态顺序断言（`offline-negatives.ps1:346-349` 检查 `ReadToEndAsync` 先于 `WaitForExit` 及 `stdout-read-incomplete` 字样）。未运行大输出/超时夹具（R-5）。

判定：通过（静态）。残余：缺受控大输出与超时用例，判定依据仍是子串/顺序而非行为。

### M3 失败保留根目录、清理不阻断判定 —— 静态通过

判据：失败必须保留证据根；清理异常不得把 PASS 变 FAIL 或掩盖 FAIL；清理需防越界。

证据：
- `smoke.ps1:159-172` 成功路径在 `try/catch` 内删除根目录，异常仅 `Write-Warning`，仍 `exit 0`；`174-179` 失败路径保留根并 `exit 1`。
- `gate.ps1:230-246` 仅在 smoke passed 时清理，带 temp 基址包含性检查，异常 `Write-Warning` 不改变判定。

判定：通过。

### M6 证据目录唯一性 —— 静态通过

判据：每次 run 证据目录不得复用；确定性路径已存在必须拒绝；重试有上限。

证据：
- `GateSupport.ps1:17-54` `New-EvidenceDirectory`：`yyyyMMdd-HHmmss-fff-<mode>-<8hex>`；`37-39` 确定性路径存在即 throw；`44-52` 最多 5 次，`Test-Path` 命中则继续，创建后再查空；`53` 超限 throw。
- `gate.ps1:61-66` 使用该函数；离线用例 `evidence-directories-unique` 覆盖。

判定：通过。

---

## 三、136 条离线用例独立复跑与覆盖盲区

命令（原样）：
```
pwsh -NoProfile -File scripts/tests/offline-negatives.ps1
```
实际输出末行：`==> OFFLINE NEGATIVES PASS：136/136`，EXIT=0。

用例构成（静态阅读 `scripts/tests/offline-negatives.ps1`，364 行，9 组）：
- 版本/源身份/指纹夹具（含 207-249 的临时 git 仓库）与缺口判定；
- B1/B4 的 workflow 契约断言：CI 无跳过（316-318，剔除 `#` 注释行）、release 只用 env（321-322）、tag 严格校验且先校验后写（323-325）、gh 步骤无插值（326-329）、evidence always（330）；
- gate 契约：唯一证据目录、`-SkipSmoke`/`-NoZip` 语义、`-ZipOnly`、`createdThisRun`、smoke 先于 ZIP（332-337）、Dirty 比对（338）、LIMITED 文案（339）；
- publish：`-p:Version`/`-p:FileVersion`/`-ZipOnly`/`ZIP_SHA256=`（341-344）；
- smoke：`ReadToEndAsync` 顺序与 `stdout-read-incomplete`/`KeepRoot`（346-349）；
- 其余为指纹、排除、版本形状、SmokeResult 校验等。

覆盖盲区（本轮独立确认）：
1. **无 B7 断言**：全文件无 `--smoke` 字样，无替身进程，故 136/136 与 B7 阻断可同时成立。
2. 断言以子串/顺序为主（`Assert-Order` 50-55 只比较「两处文本均存在且前者行号更小」），无法发现「参数缺失但注释/日志含该词」这类语义缺口——B7 恰是这种形态（81 行日志有 `--smoke`，argv 无）。
3. 无 M2 行为夹具（大输出/超时），无 gate 端到端夹具（受限于不得 build/publish/smoke）；这与安全边界一致，但意味着脚本行为正确性仍依赖真实运行（E09 阶段）。
4. 未做 Actions schema 校验（pyyaml 仅语法）；workflow 的真实可执行性未证。

---

## 四、剩余阻断、残余风险与未运行项

### 4.1 阻断（必须修后才能进入受控运行）

| 编号 | 阻断 | 位置 | 影响 |
|---|---|---|---|
| BLK-1 | smoke 启动缺 `--smoke` | `scripts/smoke.ps1:51-62`；调用方 `gate.ps1:216`、`publish.ps1:245` | smoke 永远 result-missing；gate PASS 不可达；有激活用户实例/写真实数据的副作用 |
| BLK-1a | 离线用例无 B7 正反例 | `scripts/tests/offline-negatives.ps1` 全文 | 修复无回归保护，136/136 不能证明 B7 已修 |

### 4.2 残余风险（非阻断，但应在后续轮次收敛）

- **R-1 工件/结果/ZIP 的内容绑定不闭合**：`gate.ps1:184-196` 在 smoke 前算 `artifact-files.sha256`；ZIP 在 smoke 后由 `publish.ps1 -ZipOnly` 重建（291），中间无 outDir 重哈希、无 ZIP 条目与清单逐条比对，manifest 无 ZIP 条目清单；`smoke-result.json` 不含 exe 哈希，故 smoke→工件的绑定只是路径级。旧 P11 报告第 158 行已指出同类 TOCTOU。建议后续加「smoke 后重算 outDir 哈希并要求等于运行前」「ZIP 解包后逐条目比对」。
- **R-2 早抛路径无证据**：release 模式下 dirty（`gate.ps1:50-53`）、版本非法（45）、mode/runtime 非法（33-38）均在创建证据目录（61-66）之前 throw，Actions 的 always 上传会得到「无文件」警告，失败原因只留在控制台。
- **R-3 用户材料目录计入 CI 指纹**：`大佬修改材料/` 为未跟踪目录且无排除规则，真机探针确认被纳入；CI 会因此对用户材料敏感（git-aware 哈希，可接受），但会拉高输入数、且用户新增材料会改变指纹。release 需 clean，不受影响。契约「可排除」未强制，属可接受噪声。
- **R-4 ignored 候选启发式有盲区**：只覆盖 `src|tests|test|scripts|tools|build|eng|\.github` 前缀与根级构建后缀；新的顶层 ignored 构建目录（如 `myapp/src/*.cs` 且其父目录不在清单）不会报缺口，而会被排除段/默认纳入逻辑静默放过。建议后续把「ignored 且扩展名属构建集合」泛化到全仓库（可按目录白名单降噪）。
- **R-5 M2 缺行为夹具**：仅有静态顺序/子串断言，无大输出与超时夹具（`grep 'timeout|large' scripts/tests` 无命中断言）；建议与 B7 修复一并补 2 个受控夹具（不启动 App）。
- **R-6 CI 桌面可用性未证**：windows-latest 上 WinUI 构建/桌面会话可行性未验证，严格 CI 首跑可能红；契约明确接受 fail-closed，不因此放宽。
- **R-7 Release 自动创建**：release.yml 在 tag push 时直接 `gh release create`（`release.yml:104`），无 environment 保护/二次人工审批。与「打 tag 与 Release 须主人明确同意」的铁律相容的前提是「只有主人能推 tag」；一旦代理误推 tag，Release 会自动生成。建议后续给 release job 加 `environment` 或改用手动审批。
- **R-8 ZIP 文件名硬编码平台**：`release.yml:98` 写死 `win-x64`，与 gate 默认 Runtime 重复；若默认 Runtime 变更会漂移。建议由 `gate.ps1` 的 `-Runtime` 默认值派生或显式传参。
- **R-9 YAML 仅语法校验**：pyyaml 通过不代表 Actions schema/表达式正确；未运行 Actions。
- **R-10 离线用例与 B7 的错位**：见 BLK-1a 与 §3。

### 4.3 未运行项（本工位刻意不执行；需在第 2 轮修复后由 lead 授权的受控环境补）

| 项 | 原因 |
|---|---|
| `dotnet build` / `dotnet publish` | 会写 artifacts、拉依赖，超本工位权限 |
| `scripts/gate.ps1` 真实运行 | 会 build/publish/smoke，且 B7 未修必然副作用 |
| `scripts/smoke.ps1` 对真实 App | 会激活用户实例或写真实 data（BLK-1） |
| `scripts/publish.ps1`（含默认 smoke） | 同上；即使 `-ZipOnly` 也不在本轮范围 |
| ZIP 创建/解包校验 | 需 publish 产物 |
| GitHub Actions 真实执行 | 需 push/tag，明令禁止 |
| UIA / 真实按键 / 杀用户程序 | 明令禁止，且合成 Ctrl 在本机不可验证 |
| E09 真实样本（每文件+ZIP SHA256、manifest 记录 gates/version/SDK/run 参数） | 依赖受控运行；本轮只有静态与离线证据 |

### 4.4 对「静态安全许可 vs E09 验收」的边界声明

- 本轮能证明的：脚本/workflow 在**读代码**意义上的顺序、校验、fail-closed 语义，以及 136 条离线用例在此环境可复现通过。
- 本轮不能证明的：真实 App 在 `--smoke` 下是否按协议写结果、YAML 在 Actions 上是否可执行、apphost 版本分支走向、ZIP 内容与工件一致、CI 桌面可用性。
- 因此：即使 B7 修复，**静态通过也只是「允许进入受控运行」的必要条件**，不等于 E09 验收；e09 必须另跑真实样本并逐文件/ZIP 核哈希。

---

## 五、复核命令记录与本工位自证

| # | 命令 | 结果 | 退出码 |
|---|---|---|---|
| 1 | `pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'` | 7.6.9 | 0 |
| 2 | 遍历 `gate.ps1/publish.ps1/smoke.ps1/lib/GateSupport.ps1/lib/SmokeResult.ps1/lib/SourceIdentity.ps1/tests/offline-negatives.ps1` 执行 `[Parser]::ParseFile` | 7 个文件全部 PARSE OK | 0 |
| 3 | `pwsh -NoProfile -File scripts/tests/offline-negatives.ps1` | `==> OFFLINE NEGATIVES PASS：136/136` | 0 |
| 4 | `Import-Module scripts/lib/SourceIdentity.ps1; Get-SourceIdentity -RepoRoot .` | HEAD=5e74f62…, Dirty=True/43, Inputs=161, Ignored=0, Uncovered=0, Excluded=23356, NativeMethods=6 | 0 |
| 5 | `python -c "import yaml; yaml.safe_load(...)"` 对两 workflow | `YAML OK` | 0 |
| 6 | grep `--smoke` / `smoke\.ps1` 于 `scripts/`、`.github/` | 无 `--smoke` 字面量；调用方仅 gate:207/212/216、publish:238/245、tests:313（读文本） | 0 |

自证边界：
- 除本文件外未写入任何文件；未修改产品/scripts/CI/Record 其他文件；未 commit/push；未执行 build/publish/smoke/ZIP/Actions/UIA。
- 未调用 `agent_teams_*`（按指令直接落报告并 send_message 给 lead）；未新建任务；未派子代理。
- 因共享工具无法 claim task-52，本报告按指令直接落盘，结论以 `send_message` 送达 lead。

---

## 六、给 lead 的处置建议（不代为执行）

1. 判定 task-52 = **needs_revision**（唯一硬阻断 BLK-1/BLK-1a）；不要放行真实 smoke/gate/publish/ZIP/Actions，不要采 E09 样本。
2. 第 2 轮返工只动 `scripts/smoke.ps1`（追加一次 `--smoke`、修正日志）与 `scripts/tests/offline-negatives.ps1`（正反例+断言）；gate/publish 接口保持不变。
3. 第 2 轮复核时以「替身进程记录的 argv+env JSON」为唯一证据，不接受日志文本或注释作为已传参证明；同时复跑 136 用例并核对新增断言确实在无 `--smoke` 时失败（变异自检）。
4. B7 修复并经第 2 轮静态复核通过后，才可由 lead 在受控环境授权一次离线/隔离 smoke；E09 真实样本另立验收，逐文件+ZIP SHA256 并确认 manifest 记录 gates/version/SDK/运行参数。
5. R-7 建议在发版前补 environment 审批，避免「tag push 即自动 Release」。

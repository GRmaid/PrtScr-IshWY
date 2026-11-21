# P-11 独立复核报告（第三工位 / task-50）

日期：2026-11-07
工作区：E:/Explore/GoodGood/PrtScr
基线：HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`（main），复核时工作树 dirty（用户变更与 task-47/48 未提交内容均保留，本报告未改动任何 src/scripts/YAML）
复核方式：只读静态审查 + 只读/临时目录探针；未 build、未 publish、未运行 App smoke、未执行工作流。
说明：`team_task_get` 在本环境不存在，按提示与冻结文档继续工作，未改用 agent_teams API。

## 结论

**总体判定：needs_revision。P-11 不通过；P-10 未验收（本单只做协议形状交叉核对，不评 App 时序）；发布冻结继续，本单不联合放行。**

阻断项 5 条：

| 编号 | 级别 | 摘要 | 主要位置 |
| --- | --- | --- | --- |
| B1 | 高（安全） | release 工作流把不可信版本/标签直接插入 PowerShell 单引号字符串；GITHUB_OUTPUT 先写后校验；gh release 再插值标签 | `.github/workflows/release.yml:32-39, 68` |
| B2 | 高 | 合法版本只进目录/zip 名，未透传给 `dotnet publish`；产物版本与 manifest 版本不一致 | `scripts/publish.ps1:57-59`、`scripts/gate.ps1:145, 284` |
| B3 | 高 | 源指纹白名单漏掉 `src/**/*.txt`（CsWin32 NativeMethods.txt 等构建输入）；既不入指纹，也不进 ignoredBuildInputs，无缺失输入告警 | `scripts/lib/SourceIdentity.ps1:26-42`（关键行 38） |
| B4 | 高（口径冲突） | CI 以 `-SkipSmoke -NoZip` 运行 gate，仍退出 0 并打印 `GATE PASS`；与原始任务「CI/Release 不可 SkipSmoke 绿」冲突 | `.github/workflows/ci.yml:26`、`scripts/gate.ps1:166-168, 335` |
| B5 | 中高 | 证据未实际闭环：仓库无任何 manifest/哈希样本；CI 不上传 evidence；stability 不比较 dirty；无 `fileCount>0`/exe 存在断言；manifest 未哈希 smoke 结果 | `scripts/gate.ps1:204-217, 235-255`、`.github/workflows/ci.yml` |

另需明确记录：**task-48 此前越界改 src 是发生过、后经 `git checkout --` 与删除新增文件撤回的，不是「从未发生」**；见 `Record/plans/P10-P11-ownership-correction.md` 与 task-48 报告第 19 条。当前树中 src 的未提交变更属 task-47，不应记到 task-48 名下。

## 一、E01-E10 逐项判定

| 编号 | 判定 | 依据（当前树行号） | 说明 |
| --- | --- | --- | --- |
| E01 | pass | `scripts/lib/SmokeResult.ps1:35-137`；离线负例 34/34 | 8 字段严格比对（70-74）、schema（76-79）、nonce（81-84）、PID（86-89）、exe 绝对路径（91-95）、dataRoot（97-101）、success 必须布尔 true（103-106）、checks 集合（108-124）、error 必须 null（126-129）。缺结果/坏 JSON 均拒。 |
| E02 | pass | `scripts/smoke.ps1:13-22, 72-80` | 超时有界（1–120s，默认 30s），仅 `$process.Kill()` 自己持有的 Process 对象，无按进程名终止。注：82-83 先 WaitForExit 再 ReadToEnd，子进程输出超过管道缓冲时可能死锁→表现为超时失败（fail-closed，误导性，见 M2）。 |
| E03 | needs_revision | `ci.yml:26`、`gate.ps1:166-168, 257-262, 335` | manifest 层诚实（unverified=smoke、note 明说不是发布验收）；但 CI 是唯一自动门禁，跳过 smoke 仍 `GATE PASS`+exit 0，正是原始任务禁止的「SkipSmoke 绿」。见 B4（含契约冲突说明）。 |
| E04 | needs_revision | `SourceIdentity.ps1:7-24`、`publish.ps1:57-59` | 非法/含 shell 字符版本在任何外部命令前拒绝（实测 `v1.3.1` 在 `SourceIdentity.ps1:22` throw）；但「合法版本透传项目」未做到：`-Version` 只用于 `IshWY-<Version>-<Runtime>` 目录/zip（publish.ps1:47-49）与 manifest（gate.ps1:284），`dotnet publish` 未带 `-p:Version`。见 B2。 |
| E05 | needs_revision | `gate.ps1:204-217`、`SourceIdentity.ps1:26-42` | 已覆盖输入在门禁中途变化会因 fingerprint 不等而 failed；但 NativeMethods.txt 这类构建输入不在指纹内，改动不会使 `stable=false`（实测：临时 git 仓库改 NativeMethods.txt 指纹不变）。与 B3 同源。 |
| E06 | pass | `gate.ps1:52, 267-269, 289-298` | dirty 构建记录 HEAD+dirty+fingerprint，manifest 明确「不是纯 HEAD」。 |
| E07 | pass | `gate.ps1:52-56`、`release.yml:45-48` | release 模式脏树在创建 evidence 目录与任何构建前 throw（实测无副作用）；工作流唯一入口是 gate，releaseReady=false 时 gate 非 0，后续步骤不执行。 |
| E08 | needs_revision | `SourceIdentity.ps1:26-42`、`gate.ps1:270-272` | 构建输入未全覆盖且**无缺失清单**：真实仓库 InputCount=146、NativeMethods.txt 数量 0、ignoredBuildInputs=0；manifest 不会提示。task-48 报告 4.5 也自述该风险，但实现未落「列出缺失输入并返工」。见 B3。 |
| E09 | needs_revision | `gate.ps1:225-255, 257-265, 280-315` | 代码路径会算逐文件 SHA256 与 zip SHA256、manifest 记门禁/版本/SDK/命令；但仓库内 `artifacts/evidence` 不存在，没有任何一次完整 manifest/zip 证据可供审计；无 `fileCount>0`/exe 断言；manifest 不哈希 smoke-result.json。见 B5。 |
| E10 | needs_revision | `release.yml:28-48, 62-68`、`ci.yml:26` | CI 与 Release 复用 gate 入口、gate 内不拼接输入（正确）；但 release.yml 的 Resolve 步骤与 gh release 命令直接插值不可信输入，违反「输入不直接拼接 shell」。见 B1。 |

## 二、阻断项与最小修补建议

### B1（高，安全）release.yml 输入注入

证据（当前树）：

- `release.yml:32-36`：`$raw = if ('${{ github.event_name }}' -eq 'workflow_dispatch') { '${{ inputs.version }}' } else { '${{ github.ref_name }}' }`。`inputs.version` 与 `github.ref_name` 被直接放进 PowerShell **单引号字符串**。workflow_dispatch 的 version 是自由文本；tag 名允许包含单引号与 `$`（实测 `git check-ref-format "refs/tags/v1.0.0'$(whoami)'"` 通过）。
- `release.yml:39`：`"version=$version" >> $env:GITHUB_OUTPUT` 在**任何校验之前**写 output；换行可注入额外 output 行。
- `release.yml:68`：`gh release create '${{ github.ref_name }}' ... --title "实践小梅IshWY ${{ github.ref_name }}"`，同类插值；该 job 带 `permissions: contents: write`（13-14）。
- 特别说明：`release.yml:44` 注释声称「不在命令中拼接 inputs.version，版本经 env 传入」，与 32-36 的实际写法矛盾；这是一处会误导后续维护者的注释。

最小修补：

1. 28-40 改为 env 传入、脚本内校验：
   ```yaml
   env:
     RAW_INPUT_VERSION: ${{ inputs.version }}
     RAW_REF_NAME: ${{ github.ref_name }}
     EVENT_NAME: ${{ github.event_name }}
   run: |
     $raw = if ($env:EVENT_NAME -eq 'workflow_dispatch') { $env:RAW_INPUT_VERSION } else { $env:RAW_REF_NAME }
     if ($raw -match "[`r`n]") { throw '版本/标签含换行' }
     $version = $raw.Trim().TrimStart('v')
     if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$') { throw "版本号非法：$version" }
     "version=$version" >> $env:GITHUB_OUTPUT
   ```
2. tag 场景再补 `git check-ref-format "refs/tags/$raw"`（或 `git describe --tags --exact-match`）并把 tag 名经 env 传给 68 行；68 行改为 `gh release create $env:TAG_NAME ... --title "实践小梅IshWY $env:TAG_NAME"`。删除 44 行不实注释或使其与实现一致。

### B2（高）版本未透传产物

证据：

- `scripts/publish.ps1:57-59` 的 `dotnet publish` 参数只有 `-c/-r/--self-contained/-o/-p:DebugType/-p:DebugSymbols/-p:PublishTrimmed/-p:TrimMode`，**没有 `-p:Version=$Version`**。
- `scripts/gate.ps1:145` 把 `-Version $Version` 传给 publish，但该值只影响 `publish.ps1:47-49` 的目录/zip 名；`gate.ps1:284` manifest.version 记录用户输入，二进制仍是 csproj 的 `<Version>`（当前 1.3.1）。
- 结果：release vX 的 zip 里 `IshWY.exe` 的 Product/FileVersion 与 manifest.version 可长期不一致；E04「合法版本透传项目」与 E09 版本身份均不成立。

最小修补：

1. publish.ps1:57-59 增加 `-p:Version=$Version`（预发布后缀由 SDK 归入 InformationalVersion；FileVersion 若需严格数值，另派生 `X.Y.Z.0` 传 `-p:FileVersion`）。
2. gate.ps1 在 235-255 的 artifactInfo 之后增加断言：`(Get-Item (Join-Path $outDir 'IshWY.exe')).VersionInfo.ProductVersion`（或 InformationalVersion）以 `$Version` 开头，否则把 publish 判失败/使 releaseReady=false；这样 manifest.version 与产物强绑定。

### B3（高）构建输入漏指纹（E05/E08 同源）

证据：

- `SourceIdentity.ps1:38` 的 src 扩展名白名单为 `cs|csproj|props|targets|xaml|resw|resx|json|manifest|pri|ico|png|bmp|jpe?g|gif|ttf|otf`，**不含 `txt`、`xml`**；第 41 行对未匹配文件直接 `return $false`，且 gate.ps1:58 只把「被 gitignore 但形似构建输入」的文件收进 ignoredBuildInputs，未被白名单命中的普通 tracked 文件两边都不出现。
- 真实仓库探针：`Get-SourceIdentity` → InputCount=146，路径含 NativeMethods.txt 的输入 0 个，ignoredBuildInputs=0；`Test-BuildInputPath('src/PrtScr.App/NativeMethods.txt')` = False。
- 临时 git 仓库探针：`{src/App/a.cs, src/App/NativeMethods.txt}` 的指纹只含 a.cs；编辑 NativeMethods.txt 前后 fingerprint 相同。
- 项目 csproj 使用 Microsoft.Windows.CsWin32，`src/*/NativeMethods.txt` 是真实构建输入。因此「同 SHA」声明在 E08 意义上不成立，且中途替换该文件不会被 E05 稳定性检查发现。

最小修补（推荐默认拒绝）：

1. 把 `Test-BuildInputPath` 改成「默认纳入、显式排除」：仅排除 `bin|obj|.tmp|artifacts|data|node_modules|publish` 与 `Record|docs|README|LICENSE|.gitignore|.gitattributes`；其余 tracked/untracked 文件全部入指纹。这样 .txt/.xml 及未来新类型自动覆盖。
2. 保守替代：白名单补 `txt|xml|config`，同时在 gate 里计算 `uncoveredInputs`（tracked 但未入指纹且非白名单前缀的文件），写入 manifest；release 模式下 `uncoveredInputs.Count -gt 0` 直接失败（不给「静默漏检」留口子）。

### B4（高，口径冲突）CI 的 SkipSmoke 绿灯

证据：

- `ci.yml:26`：`pwsh -NoProfile -File scripts/gate.ps1 -Mode ci -Version 0.0.0-ci -SkipSmoke -NoZip`，随后作业即成功；该 job 无 smoke、无 zip、无证据上传。
- `gate.ps1:166-168` 仅 `Add-GateSkipped` 加黄字提示；`gate.ps1:335` 在 ci 模式下仍打印 `==> GATE PASS（未运行的 smoke/zip 已在 manifest 标注，未计为通过）` 并 exit 0。
- 冻结契约 `Record/plans/P10-P11-safe-gates.md:24` 允许「SkipSmoke 显式未验证」，与原始任务「CI/Release 不可 SkipSmoke 绿」**直接冲突**；E03 矩阵要求「不冒充发布验收通过」。实现选择了契约口径，但输出的 `GATE PASS` 与 job 名 `Trusted same-source gate` 仍容易被读成门禁通过。

处置建议（需主人/lead 拍板，二选一，不能默认混过）：

- 方案 A（严格按原始任务）：CI 也跑 smoke（windows-latest 是否具备可交互桌面需一次真实演练；若不可用则此方案不成立），并上传 evidence；只保留 `-NoZip` 或也打包。
- 方案 B（按冻结契约，需书面确认）：CI 作业改名为「build/package only (smoke not run)」，gate 在 `-SkipSmoke` 时输出改为 `GATE PASS (limited: smoke not run)`，并在分支保护/发布流程中把「带 smoke 的 release gate」设为唯一发布依据；同时把该决定写入验收记录，明确 E03 按契约解释。
- 无论哪种，CI 都应上传 `artifacts/evidence/**`（见 B5），否则 CI 侧没有任何可审计证据。

### B5（中高）证据未闭环

证据：

- 仓库内 `artifacts/evidence` 不存在（task-48 报告第 70 行也承认「无 artifacts/evidence 产物」）；因此 `releaseReady` 分支、manifest 结构、哈希清单均**从未端到端执行**，E09 无验收样本。
- `gate.ps1:204-217`：`stable` 只比较 `Head` 与 `Fingerprint`，不比较 `Dirty`；release 起始 clean，但门禁中途新增一个不在指纹内的未跟踪文件（如 .txt）不会使 stability 失败。
- `gate.ps1:235-255`：只要 `publishStatus -eq 'passed'` 且 outDir 存在就计算哈希，未断言 `fileCount -gt 0`、`Test-Path $outDir/IshWY.exe`；若 publish 出现「exit 0 但空输出」的回归，artifactInfo 仍可为全零，releaseReady 只依赖 zipSha256 是否为 null。
- `gate.ps1:301-306`：smoke.evidence 只记相对路径，manifest 没有 smoke-result.json 的 SHA256，也没有 source-inputs/artifact-files 两个哈希文件自身的哈希。
- `ci.yml` 无 `actions/upload-artifact`；CI 运行结束后日志之外无证据留存。

最小修补：

1. `gate.ps1:205` 增加 `-and ($identityAfter.Dirty -eq $identityBefore.Dirty)`（或直接要求 after.Dirty=false 才 stable）。
2. 235 之后加：`if ($artifactFiles.Count -eq 0 -or -not (Test-Path (Join-Path $outDir 'IshWY.exe') -PathType Leaf)) { publish 判失败 / releaseReady=false }`。
3. manifest 增加 `smoke.resultSha256`、`hashes.sourceInputsSha256`、`hashes.artifactFilesSha256`；或在 hashes/ 下再存一份 hash-of-hashes。
4. ci.yml 增加 `actions/upload-artifact`（`artifacts/evidence/**`，保留 30 天）；release.yml 已有上传，保持。
5. 由 lead 在主人同意、实例停止/避开的时间窗内完成**一次真实 gate**（ci 或 release），把 manifest+哈希+smoke 结果作为 E09 验收证据；在此之前不得写「P11 通过」。

## 三、非阻断问题（建议随 B 项一并修）

- M1 结果校验对 checks 顺序不敏感：`SmokeResult.ps1:108-124` 用 Sort-Object 做集合比较；实测把 checks 逆序写入仍 `Ok=True`。App 侧按规范顺序产生，不构成假成功，但若协议要求顺序，应改为有序比较。
- M2 `smoke.ps1:82-83` 在 `WaitForExit` 之后才 `ReadToEnd`；子进程输出超过管道缓冲（约 4–64KB）会互相阻塞，30s 后误判超时。建议先起异步读取（`BeginOutputReadLine` 或 `ReadToEndAsync`）再 WaitForExit。
- M3 `smoke.ps1:127-134` 先打印 PASS 再删除临时根；删除失败会以未处理异常非 0 退出，日志出现「PASS 但失败」，建议删除放在最终判定之后并捕获为 warning。
- M4 `Test-VersionString` 接受前导零（`01.3.1`）与 `1.3.1+meta` 被拒；前者不产生注入风险，但会生成 `IshWY-01.3.1-...`，建议按 SemVer 收紧数值段（`0|[1-9]\d*`）。
- M5 `release.yml:53-54, 59-60` 的 artifact name/path 仍来自步骤 output；B1 修复后 output 已校验，风险消除，但建议直接用校验后的 env 值。
- M6 evidence 目录时间戳精确到秒（gate.ps1:61），并发/重跑同秒会共用目录；建议加短随机后缀或失败即拒绝已存在目录。
- M7 gate 记录的 `$publishCommand`（gate.ps1:149）是未加引号的拼接串，路径含空格时不可复现；仅审计可读性问题，建议用 `-join` 时对参数加引号。
- M8 工作流 actions 使用 `@v4` 可移动标签而非 SHA 固定；供应链加固项，不阻断本轮。
- M9 manifest 只记 dirty 布尔，未记 `git status --porcelain` 文件列表；E06 可满足，但审计时无法定位脏文件。
- M10 task-48 报告的证据索引个别行号有漂移（如把结果字段核对写成 `SmokeResult.ps1:51-99`，实际为 35-137）；复核以当前树行号为准，不影响结论。

## 四、证据（本次实际执行）

1. `pwsh -NoProfile -File scripts/tests/offline-negatives.ps1` → `==> OFFLINE NEGATIVES PASS：34/34`，exit 0。
2. 真实仓库 `Get-SourceIdentity` → InputCount=146；含 NativeMethods.txt 的输入 0；app.manifest=1；PrtScr.sln=1；ignoredBuildInputs=0；`Test-BuildInputPath('src/PrtScr.App/NativeMethods.txt')`=False。
3. 临时 git 仓库 `{src/App/a.cs, src/App/NativeMethods.txt}` → Inputs 仅 `src/App/a.cs`；修改 NativeMethods.txt 前后 fingerprint 相同（False）。
4. 逆序 checks 的结果文件 → `REVERSED_CHECKS_ORDER_ACCEPTED=True`。
5. `gate.ps1 -Mode release -Version 1.3.1`（dirty）→ 在 `gate.ps1:55` throw，未创建 artifacts/evidence；`gate.ps1 -Version v1.3.1` → `SourceIdentity.ps1:22` throw；`gate.ps1 -Runtime x64` → `gate.ps1:37` throw。三次均无副作用。
6. `git check-ref-format "refs/tags/v1.0.0'$(whoami)'"` → valid（tag 名可含单引号与 `$`），佐证 B1 非理论风险。
7. `ls artifacts/evidence` → No such file or directory（无 manifest/zip 证据）。
8. 静态通读：`scripts/gate.ps1`(336)、`scripts/smoke.ps1`(141)、`scripts/lib/SmokeResult.ps1`(137)、`scripts/lib/SourceIdentity.ps1`(149)、`scripts/tests/offline-negatives.ps1`(156)、`scripts/publish.ps1`(191)、`.github/workflows/ci.yml`(26)、`.github/workflows/release.yml`(68)。
9. 协议形状交叉核对（只读）：`src/PrtScr.Core/SmokeProtocol.cs:21-31, 35-38, 224-234` 与 `src/PrtScr.App/Services/SmokeSession.cs:149, 166-212, 215-227` 的 8 字段、camelCase、三个 check 名称、退出码与脚本期望一致。

## 五、未验范围（明确未覆盖，不得记通过）

1. 未运行 build/test/format/publish/gate，未验证 `releaseReady` 路径、manifest 实际内容与 zip 内容；task-48 的 0 警 0 错、324 测试、format 0 属其过程证据，本单未复现。
2. 未运行真实 `IshWY.exe --smoke`（需可交互桌面且主人实例 PID 4396 在运行），App 侧 XAML 初始化顺序、主窗 Loaded、Dispatcher 往返、隔离写路径、既有实例拒绝均属 P-10 审查范围，本单未验收。
3. 未执行 GitHub Actions；windows-latest 上 GUI smoke 是否可行未验证（B4 方案 A 的前提）。
4. 未验证 `--smoke` 在锁屏/无桌面/RDP 会话下的行为，未验证临时根重解析点拒绝（S02）与只写隔离根（S07）——属 P-10。
5. 未做威胁建模外的攻击面验证（如 artifacts 目录被本地第三方替换、TOCTOU 换 exe）；smoke 结果不含 exe 哈希，gate 在 smoke 后才哈希产物，存在时间窗，属加固建议而非本轮阻断。
6. 未复现 task-48 的 `.tmp` 构建护栏与 sleep/hostname 启动器负例；其报告描述与本单静态审查一致，但未独立重跑。

## 六、给 lead 的处置建议（顺序）

1. 先修 B1（安全），再修 B2、B3（身份/版本正确性），随 B5 的 1/2/3/4 一起修；B4 需主人或 lead 书面选择口径后再定稿。
2. 修复后在隔离 OutDir、实例停止/避开的时间窗内跑一次完整 `gate.ps1 -Mode release`（或经批准的 ci 全量路径），把 manifest、source-inputs、artifact-files、smoke-result 的 SHA256 与 zip 一并归档为 E09 证据。
3. 在此之前 P-11 保持 needs_revision，不解除发布冻结；打 tag/Release 仍需主人明确点头。
4. 本报告只覆盖 P-11 脚本/CI；P-10 需其独立报告与安全复核结论，二者不得互相替代。

## 七、声明

- 本单未写 src/scripts/YAML，未 commit/push，未打 tag/Release，未运行 App、未打包、未触发远端 Actions、未使用合成输入/UIA、未结束任何实例。
- 仅新增本文件 `Record/reports/P11-independent-review.md`；工作区其余变更（含用户变更与 task-47/48 未提交内容）原样保留。
- task-48 的 src 越界试验系「发生并已撤回」，不是「从未发生」；撤回动作见其报告第 19 条与 `Record/plans/P10-P11-ownership-correction.md`，不得据「未改 src」的表述抹掉这段历史。

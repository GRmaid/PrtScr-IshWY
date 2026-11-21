# P11 第 1 轮返工报告（scripts/CI 严格化）

- 日期：2026-11-07
- 执行：Flash 工位（第 1 轮返工，round 1，未发生第 2 轮）
- 基线：HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`；工作树原有未提交变更全部保留
- 依据：`Record/plans/P11-round1-repair.md`（书面契约）、`Record/plans/P10-P11-safe-gates.md`、`Record/plans/P10-P11-acceptance-matrix.md`、`Record/HeadMaid.md`、`SubMaid.md`、`Record/reports/P11-independent-review.md`
- 结论：B1–B6 与 M2/M3/M6 在**脚本/工作流层**已落实并通过离线验证；真实构建、发布、冒烟、ZIP、GitHub Actions 与 E09 真机采集均**未运行**，因此本轮**不构成发布放行**。

## 一、契约与边界

- 唯一写域：`scripts/**`、`.github/workflows/ci.yml`、`.github/workflows/release.yml`、本报告。实际改动即此范围（`git status` 仅显示 `M ci.yml`、`M release.yml`、`M scripts/publish.ps1` 与 `?? scripts/gate.ps1`、`?? scripts/lib/`、`?? scripts/smoke.ps1`、`?? scripts/tests/`）。
- 未触碰：`src/**`（无写/删/撤回）、`README.md`、`docs/`、`ProjectLog`、依赖文件、`.gitignore`、`handoff/`；未 commit/push/tag/Release；未派子代理；未解除 hold；未渲染 HTML。
- 未运行（按契约禁止）：App smoke、真实 `dotnet publish`、真实打包、GitHub Actions、C# build、杀用户实例、合成输入/UIA。离线仅用 PowerShell 解析与纯临时 fixture。
- P10 独立审查为并行只读工作，未读取/修改其文件，未以其结果为前置。

## 二、改动清单

| 文件 | 行数 | 改动 |
| --- | --- | --- |
| `scripts/lib/SourceIdentity.ps1` | 363 | 构建输入「默认纳入 + 显式排除」、ignored/uncovered 缺口探测、严格版本规则、版本形状、产物版本精确比较 |
| `scripts/lib/GateSupport.ps1` | 263（新） | 唯一证据目录、哈希、产物断言、门禁判决（passed/limited/failed） |
| `scripts/gate.ps1` | 492（重写） | stage → 产物核验 → smoke → 稳定性 → ZIP 的严格顺序，manifest v2 |
| `scripts/publish.ps1` | 259 | 透传 `-p:Version`/`-p:FileVersion`；新增 `-ZipOnly`；发布后 dll 版本自检 |
| `scripts/smoke.ps1` | 179 | 先异步读取再 WaitForExit；清理失败只警告；`-ProcessArguments` 仅诊断用 |
| `scripts/tests/offline-negatives.ps1` | 363 | 9 组 136 条离线断言（含静态契约回归） |
| `.github/workflows/ci.yml` | 44 | 严格 CI（无 Skip/NoZip），always 上传证据 |
| `.github/workflows/release.yml` | 108 | 不可信输入经 env、先严格校验后写 GITHUB_OUTPUT、tag 校验、gh 不插值、always 上传 |

## 三、逐条闭环

### B1 不可信输入（workflow → PowerShell）
- `release.yml` 的 `EVENT_NAME`/`INPUT_VERSION`/`REF_NAME` 全部经 `env:` 传入，脚本正文不含 `${{ }}` 插值。
- 取值后先 `ConvertTo-StrictVersion`（只允许可选单个前导 `v`、SemVer 三段数值、可选 prerelease；空白/控制字符/多前缀/分号/换行一律抛错），**之后**才 `Out-File -FilePath $env:GITHUB_OUTPUT`。
- tag push 额外断言 `REF_NAME -eq ('v' + $version)` 并 `git check-ref-format "refs/tags/$tag"`，防止 `v1.3.1` 与 `v1.3.10` 类前缀混淆。
- `gh release create` 只引用 `$env:VERSION`/`$tag`，无 `${{ }}`；原第 44 行与实现矛盾的注释已改为与实现一致的说明。
- 离线证据：`release-passes-event-name-via-env`、`release-passes-input-via-env`、`release-uses-strict-version`、`release-validates-before-writing-output`、`release-validates-tag-ref`、`release-gh-command-has-no-interpolation`、`strict-version-*`。

### B2 版本透传与产物精确核验
- `publish.ps1` 对 `dotnet publish` 传 `-p:Version=<Version>` 与 `-p:FileVersion=<X.Y.Z.0>`（由 `Get-VersionShape` 得出），发布后回读托管 dll 版本，不一致即失败。
- `gate.ps1` 在 smoke 之前调用 `Test-PublishArtifact`：输出目录存在且非空、`IshWY.exe`、`IshWY.dll`、`IshWY.pri`、`*.xbf` 存在；dll 的 ProductVersion（去 `+metadata`）与 FileVersion 必须与规范化版本精确相等（Ordinal，不做前缀匹配）；exe 作为 apphost 若版本资源为空或 `0.0.0` 则记为 `dll-authoritative`，否则必须同样匹配并在 manifest 记 `exeVersionSource`。
- 任一项失败 → gate 名 `artifact-identity` 失败 → 硬失败 `artifact-not-verified`，不进入 smoke。
- 离线证据：`artifact-version-rejects-1.3.10-for-1.3.1`、`artifact-version-rejects-3-part-file-version`、`artifact-version-accepts-metadata-suffix`、`artifact-empty-reports-exe/dll/pri-missing`、`verdict-unverified-artifact-failed`。

### B3 构建输入默认纳入、显式排除
- `Get-BuildInputExclusionReason`：按路径段排除 `bin obj .tmp artifacts data node_modules publish .vs .vscode .idea TestResults`、顶层 `Record docs handoff .dsh-memory .agent-teams`、顶层 `README*/LICENSE*/.gitignore/.gitattributes`、`.git`；其余一律视为构建输入（`NativeMethods.txt`、`*.xml`、`*.razor`、`*.manifest`、`.github/**`、`Directory.Build.props`、`global.json` 均纳入）。
- `Get-SourceFingerprint` 同时产出 `IgnoredInputs`、`IgnoredInputCandidates`（被 gitignore 且形似构建输入）、`UncoveredInputs`（列出但读不到/哈希失败）；gate 据此计算 `sourceCovered`，任何缺口 → 硬失败 `source-input-coverage-gap`，并把 `source-ignored-inputs.txt`/`source-uncovered-inputs.txt` 写入证据目录。
- 离线证据：8 条纳入、13 条排除、ignored 缺口判定、指纹对新增/删除/内容变化敏感、对 docs 不敏感、`.gitignore` 覆盖的 `src/generated/NativeMethods.txt` 列为缺口而 `build.log` 不算。

### B4 严格 CI 方案 A
- `ci.yml` 单步 `./scripts/gate.ps1 -Mode ci -Version 0.0.0-ci`，**无** `-SkipSmoke`/`-NoZip`；`if: always()` 上传 `artifacts/evidence`（run_id + run_attempt 命名，14 天）。
- `gate.ps1` 中 `-SkipSmoke` 仍保留为诊断路径，但 smoke 记 `not-run`，判决为 `limited`（exit 2），并打印 `GATE LIMITED`；`-NoZip` 同样 `limited`。二者都不会输出 `GATE PASS`。
- 离线证据：`ci-has-no-skipsmoke`、`ci-has-no-nozip`（注释行不计）、`verdict-skip-smoke-limited-2`、`verdict-no-zip-limited-2`、`verdict-skip-smoke-never-complete`。

### B5 稳定性、输出断言与证据闭环
- 稳定性比较升级为 **HEAD + 构建输入指纹 + `git status --porcelain` 的 Dirty 状态** 三者同时不变（gate 名 `stability`），任一变化即硬失败 `source-changed-during-gate`。
- smoke 之前已断言 exe/dll 存在且版本一致；smoke 结束后把 `smoke-result.json` 复制进 `evidence/smoke/`，manifest 记其 SHA256。
- manifest v2 记录：`source.head/dirty/dirtyFileCount/fingerprint/inputCount/inputsFile/excludedInputsFile/ignoredBuildInputs/uncoveredInputs/covered/after{...stable}`、`artifact.{exeVersion,dllVersion,exeVersionSource,filesFile,fileCount,totalBytes}`、`hashes.{sourceInputs,excludedInputs,artifactFiles,smokeResult}`（各自 path+sha256+bytes），即清单自身 hash。
- 证据目录改为 `yyyyMMdd-HHmmss-fff-<mode>-<8hex>`，毫秒+随机后缀且已存在即抛错，杜绝同秒覆盖；`ci.yml`/`release.yml` 均 `if: always()` 上传。
- 离线证据：`gate-compares-dirty`、`evidence-directories-unique`、`evidence-refuses-existing-directory`、`gate-uses-unique-evidence-directory`、`gate-tracks-zip-created-this-run`。

### B6 stage → 核验 → smoke → 稳定 → 才 ZIP
- gate 的 publish 步骤**始终**带 `-SkipSmoke -NoZip`，只 stage 到 `artifacts/IshWY-<version>-<runtime>/`，不产生任何 ZIP。
- 顺序固定：stage → `Test-PublishArtifact` → 产物 hash 清单 → 受控外层 smoke → 稳定性 → 仅当 `smoke=passed` 且 `stable` 才调用 `publish.ps1 -ZipOnly`。
- ZIP 核验：解析 `ZIP_PATH`/`ZIP_SHA256`/`ZIP_BYTES`，要求路径与期望一致、文件确实存在、报告哈希与实际 SHA256 一致；只有全通过才置 `zip.createdThisRun=true`，否则 `zip-failed` 硬失败。
- 未生成 ZIP 时 manifest 的 `zip.reason` 明确记录 `skipped-by-request` / `smoke-<status>` / `source-unstable`，不会把旧 ZIP 当本次证据；`releaseReady` 仅 `release + passed + clean`。
- 离线证据：`gate-stages-without-zip`、`gate-zips-via-ziponly-after-smoke`、`gate-smoke-before-zip`、`verdict-zip-failed-hard`、`verdict-release-clean-ready`、`verdict-release-dirty-not-ready`、`release-dirty-refused`。

### M2 / M3 / M6
- M2：`smoke.ps1` 先对 stdout/stderr 调 `ReadToEndAsync`，再 `WaitForExit`，读取异常记 `stdout-read-incomplete`/`stderr-read-incomplete`，避免大输出管道死锁；`-ProcessArguments` 仅用于离线诊断（不进入 CI 契约）。
- M3：smoke 成功后的临时根清理包在 try/catch，失败只 `Write-Warning`；gate 侧清理同样只警告，smoke 失败时保留临时根并打印路径，供排查。
- M6：唯一证据目录（毫秒+随机后缀，见 B5）。
- M1/M9 未升级为阻断（checks 集合仍按 Sort-Object 顺序不敏感比较），本轮未改 `SmokeResult.ps1` 语义。

## 四、离线验证命令与退出码

```text
# 1) 全部 PowerShell 脚本语法解析（Parser.ParseFile，逐文件收集错误）
pwsh -NoProfile -Command '... Parser::ParseFile ...'
→ PARSE OK 7
→ exit 0

# 2) 离线负例（纯临时目录 + 纯函数；不启动 App、不构建、不打包）
pwsh -NoProfile -File scripts/tests/offline-negatives.ps1
→ ==> OFFLINE NEGATIVES PASS：136/136
→ exit 0
```

136 条覆盖：冒烟结果校验器 15 条、版本规则 28 条、产物版本匹配 6 条、release 源码策略 3 条、构建输入覆盖规则 25 条、源码指纹 fixture 11 条、证据目录/产物断言 8 条、门禁判决 13 条、静态契约 27 条（合计 136）。

## 五、风险与未运行项

1. **真实工具链未验证（最大风险）**：受契约限制未运行 `dotnet build/test/format`、真实 `publish`、`smoke.ps1`、ZIP 与 GitHub Actions。`gate.ps1` 的真实 PASS 路径、smoke 死锁修复在大输出下的表现、ZIP 字节/哈希核验均只有静态与离线证明。
2. **apphost 版本资源未知**：真实发布的 `IshWY.exe` 可能无版本资源或为 `0.0.0.0`，此时按 dll 权威并记 `exeVersionSource`；若 SDK 给 apphost 写入 csproj 的 `1.3.1` 而本次 `-p:Version` 为 `0.0.0-ci`，gate 会判失败。需要一次真实 publish 才能确认该分支，当前不猜测。
3. **YAML 仅人工审阅**：本地无 YAML 解析器与 Actions 运行，`ci.yml`/`release.yml` 的缩进/表达式合法性未经机器验证。
4. **release 产物上传通配**：`artifacts/IshWY-*.zip` 在干净 checkout 下安全；若将来允许脏工作区 dispatch，可能匹配历史 ZIP。当前 gate 的 `release+dirty` 直接抛错，且 `createdThisRun` 只在本次生成时为 true，风险受控但未在 Actions 实测。
5. **覆盖规则严格性**：任何新增的被 gitignore 构建输入（如 `src/generated/**`、根级 props）都会让 gate 硬失败，这是 B3 的预期行为；当前仓库被忽略项仅各项目 `bin/`/`obj/`（已显式排除），但该结论来自静态 `git status --ignored`，未跑完整 gate。
6. **Dirty 比较范围**：稳定性对整棵工作树的 `git status --porcelain` 敏感，构建过程若写入被跟踪路径会判不稳定（当前生成物均在 ignored 目录，风险低）。
7. **E09 真实样本**：本轮**未采集、未验证**，只能由 lead 安全复核放行后采集，不可自报通过。
8. **P10 独立审查**：并行只读，未参与本报告的结论；其 `needs_revision` 是否关闭由 lead 判定。

**放行建议**：脚本/工作流层可进入下一轮独立复核；真实 build/smoke/ZIP/Actions 验证与 E09 仍需 lead 安排，且在解除 hold 之前不构成发布验收。
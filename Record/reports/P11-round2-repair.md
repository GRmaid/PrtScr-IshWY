# P11 第 2 轮返工报告（task-55，scripts/冒烟门禁侧）

日期：2026-11-07 · 工位：IshWY 脚本修补（Flash，单工位，未派子代理）
结论：**交付完成，不等于验收通过**。B7/B8/B9/M2/M3 已按 `Record/plans/P11-round2-repair.md` 落盘行为夹具并通过；B1–B6 的判定语义与 `--smoke` schema v1 保持不变（离线 136/136 复跑通过）。真实 App 冒烟、dotnet build/publish、产品 ZIP、Actions/UIA 均未执行（按契约冻结）。

## 一、范围与判定

- 只改脚本/测试域：`scripts/smoke.ps1`、`scripts/gate.ps1`、`scripts/lib/GateSupport.ps1`、`scripts/lib/SourceIdentity.ps1`、`scripts/tests/**`；未写 `src/`、CI、docs、README、.gitignore，未新增依赖，未提交/推送/tag/Release。
- B7：`--smoke` 由 `smoke.ps1` 唯一构造、无条件追加一次；调用方重复传入直接拒绝（不静默去重）；启动日志与 `SMOKE_ARGV_JSON` 描述真实 argv。外部 `pwsh -File` 替身实测收到恰好一个 `--smoke`、隔离根/nonce 环境变量，并写出 schema v1 结果。
- B8：smoke 前生成确定性输出清单（相对路径/长度/SHA256），smoke 成功后重采样并比对；ZIP 生成后独立枚举实际条目，逐文件绑定 smoke 后的输出清单，额外/缺失/重复/路径歧义/遍历/内容变化/读取失败全部失败关闭；清单不一致时跳过 ZIP 且不产生 `createdThisRun=true`。
- B9：ignored 检测改为“默认纳入”：未知目录、未知扩展、无扩展都算覆盖缺口；只保留显式非构建后缀（截图/日志）与既有生成物/资料排除。真实仓库当前候选缺口 0。
- M2/M3：大输出（两流各约 540KB）先异步读后等待、不互锁；2 秒超时只对自有 Process 对象 `Kill()`；清理失败仅警告的同义保留。

## 二、改动文件与行为

| 文件 | 行数 | 改动 |
| --- | ---: | --- |
| `scripts/smoke.ps1` | 211 | 新增 `$modeArgument='--smoke'` 唯一构造点：校验 `ProcessArguments` 不含该参数→复制→追加→再次核对恰 1 个→写入 `ArgumentList`；日志打印真实参数并输出 `SMOKE_ARGV_JSON`；头注释声明 B7。异常/退出/结果校验语义未变。 |
| `scripts/gate.ps1` | 595 | 产物清单改为 smoke 前 `Get-OutputManifest`（`hashes/artifact-manifest-before.json`）+ 兼容旧 `artifact-files.sha256`；smoke 后重采样并 `Compare-OutputManifests`（after/compare 证据）；输出不稳定时 ZIP 记 skipped(`output-unstable`)；ZIP 分支 `Get-ZipManifest`+`Test-ZipMatchesOutput` 逐条目核验，通过后才置 `entriesVerified=true` 与 `createdThisRun=true`；新增 `smoke-argv`、`output-stability` 门禁结果；verdict 传 `OutputStable`；manifest 增 argv/argvVerified、输出清单文件与 stability 字段；note 写明 B8 证据边界。 |
| `scripts/lib/GateSupport.ps1` | 728 | 新增 B8 纯函数：`Get-OutputManifest`、`Compare-OutputManifests`、`Test-SafeArchiveEntryPath`、`Get-ZipManifest`、`Test-ZipMatchesOutput`（`RootPrefix` 允许空串以返回 `root-prefix-empty`）；`Get-GateVerdict` 增 `[bool]$OutputStable=$false` 与硬失败 `output-changed-during-gate`（默认 false，未显式传 true 不可能 PASS）。 |
| `scripts/lib/SourceIdentity.ps1` | 368 | 新增 `$script:IshWYNonBuildIgnoredExtensions`（png/jpg/jpeg/gif/bmp/webp/ico/log）；`Test-IgnoredBuildInputCandidate` 改为排除规则后默认纳入，未知目录/扩展不再静默漏过。 |
| `scripts/tests/offline-negatives.ps1` | 363 | 11 处 `Get-GateVerdict` 调用补 `-OutputStable $true` 以保持原判定语义；用例数不变 136。 |
| `scripts/tests/p11-behavior-fixtures.ps1` | 519 | 新增行为夹具 137 项（B7 32 / B8 77 / B9 15 / M2 12 / M3 1）。 |
| `scripts/tests/fixtures/smoke-stub.ps1` | 79 | 新增离线冒烟替身：始终写 argv/env 捕获；仅恰有一个 `--smoke` 时写 schema v1 结果；支持 large-output / sleep / no-result / env-only 场景。 |

未改 `scripts/lib/SmokeResult.ps1`、未改 `scripts/publish.ps1`（其 `CreateFromDirectory(..., includeBaseDirectory=true)` 的 `<leaf>/…` 布局由 gate 的 `Split-Path -Leaf $outDir` 对应，本轮只读核对）。

## 三、验证命令、退出码与数字

1. `pwsh -NoProfile -File scripts/tests/offline-negatives.ps1`
   - 退出码 0；`==> OFFLINE NEGATIVES PASS：136/136`（改动前后同为 136，未削弱）。
2. `pwsh -NoProfile -File scripts/tests/p11-behavior-fixtures.ps1`
   - 退出码 0；`==> P11 BEHAVIOR PASS：137/137`。
3. 解析检查（`Parser::ParseFile`，8 个文件）：smoke.ps1、gate.ps1、GateSupport.ps1、SourceIdentity.ps1、offline-negatives.ps1、p11-behavior-fixtures.ps1、fixtures/smoke-stub.ps1、publish.ps1 全部 `PARSE OK`，退出码 0。
4. 真实仓库身份探针（只读，`Get-SourceIdentity`）：
   - `HEAD=5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`；`DIRTY=True DIRTYFILES=48`（含并行 P10 的 src 改动，本轮未触碰）；
   - `INPUT_COUNT=163 EXCLUDED=23809 IGNORED=3544 CANDIDATES=0 UNCOVERED=0`——B9 默认纳入规则在当前仓库**没有新增覆盖缺口**。
5. B7 端到端手测（stub）：`SMOKE_ARGV_JSON=["-NoProfile","-File","…smoke-stub.ps1","--smoke"]`，退出码 0；子进程 `executablePath` 与传入 exe 归一化相等，schema v1 结果通过 `Test-SmokeResultFile`。
6. M2 数字：大输出单次 smoke 捕获文本 `1078558` 字符（>1MB，两流各约 540KB，均出现 END 标记，退出码 0）；2 秒超时用例实测墙钟 `2.5s`，退出码 1，原因 `timeout-2s`，子进程 PID 500ms 后不存在。

## 四、新增行为夹具清单（137 项，按段计数）

运行命令统一为 `pwsh -NoProfile -File scripts/tests/p11-behavior-fixtures.ps1`，总体退出码 0。夹具只用临时目录、离线替身与合成 ZIP。

| 段 | 项数 | 期望与实测（全通过） |
| --- | ---: | --- |
| B7 | 32 | 正向：退出 0；`SMOKE_ARGV_JSON` 恰 4 项且顺序为 `-NoProfile -File <stub> --smoke`，其中 `--smoke` 恰 1 个；日志含真实 stub 路径；`SMOKE_ROOT`/`RESULT_PATH` 标记存在；子进程捕获 `argv=["--smoke"]`、`modeCount=1`、`envRoot`=SMOKE_ROOT、`envNonce`=nonce 叶子、PID>0、进程路径匹配。负向：重复传 `--smoke` 退出非 0、无冒烟根、进程内异常原文含“不得包含”；`no-result` 退出 0 被判 `result-missing`；env-only（只设 `ISHWY_SMOKE_ROOT/NONCE`）只产生 argv 捕获、无结果文件，`Test-SmokeResultFile` 返回 `result-missing`。变异：临时副本删除唯一追加语句 `[void]$effectiveArguments.Add($modeArgument)` 后，同一入口夹具退出非 0 且异常原文含“不恰含一个”（真实行为失败，不是 grep）。 |
| B8 | 77 | 清单：3 文件/2 目录/64 位 digest；相等 Ok；改内容→`file-changed:sub/a.dll`；增/删文件、增/删目录各自被捕获；独占锁文件→`read-error:locked.bin`；根缺失→`root-missing`。ZIP：`CreateFromDirectory(includeBaseDirectory=true)` 等值 ZIP 的 3 条目与相对 digest 完全一致（`44fd446e…b30acc`）；前缀尾斜杠可接受；错前缀/空前缀分别 `zip-entry-outside-root`/`root-prefix-empty`；内容变化→`zip-file-changed`；额外文件→`zip-file-added`；缺文件→`zip-file-missing`；同名重复→`zip-entry-duplicate`；`../`、绝对路径、反斜杠、双斜杠均 `zip-entry-unsafe-path`；前缀外条目、额外目录、损坏 ZIP（`zip-open-failed`）、空 ZIP（`zip-entries-empty`）全部拒绝；安全路径函数 9 项边界（normal/dir/traversal/absolute/backslash/drive/double-slash/超长）。判定与接线：不传 `-OutputStable` 时 `Get-GateVerdict` 为 failed 且硬失败含 `output-changed-during-gate`；传 true 才 passed；`ZipCreated=false` 为 limited(2)；gate.ps1 静态顺序断言“先 Compare 后 Get-ZipManifest”“entriesVerified 先于 createdThisRun”，并含清单/条目/输出不稳定标记。 |
| B9 | 15 | 临时 git 仓库（`newtop/`、`src/generated/`、`*.png`、`*.log`、`*.unknown-extension`）：候选恰 4 项 = `newtop/payload.unknown-extension`、`newtop/src/a.cs`、`src/generated/NativeMethods.txt`、`src/untracked.unknown-extension`；png 与 log 不是候选；跟踪文件在 Inputs、`.gitignore` 在 ExcludedInputs 可读、ignored 生成物不被静默当作输入；跟踪文件改动、未跟踪新增、删除分别改变指纹；变更后候选仍 4 项。 |
| M2 | 12 | 大输出：退出 0，stdout/stderr 两个 END 标记齐全，combined 长度 1078558；超时：2 秒退出 1、原因 `timeout-2s`、墙钟 2.5s（<25s）、PID 解析成功且 500ms 后已不存在；静态：仅 `$process.Kill()`，无 `Stop-Process`/与 `taskkill` 相关清理；先读后等标记存在。 |
| M3 | 1 | 清理失败仅警告文本“临时冒烟根清理失败”仍在，且不被翻成失败。 |

## 五、局限与未覆盖（证据边界）

- 本轮**没有**启动真实 IshWY、没有 `dotnet build/test/publish`、没有实际产品 ZIP、没有 Actions/UIA/合成输入、没有按名称杀任何用户进程；因此 gate 端到端只由“helper 行为夹具 + gate 静态接线断言 + verdict 矩阵”覆盖，未证明真实 App 冒烟链路和发布脚本实际 ZIP 输出。
- B8 只覆盖本次采样到的普通文件与 ZIP 条目：**不防同权限恶意进程的瞬时替换/并发写入，不提供 OS 沙箱或原子快照**；ZIP 条目与目录的相对路径绑定依赖发布脚本保持“以输出目录 leaf 为根前缀”的布局，若 `publish.ps1` 将来改布局，`Split-Path -Leaf` 假设需同步修改。
- 子进程 stderr 的文本编码取决于其控制台代码页（本机 GBK）；B7 的拒绝理由断言改用同进程 catch 捕获原文，外部真实进程只证明“退出码非 0 且未创建冒烟根”，未断言日志中的中文可读性。
- 超时只用不派生子进程的 pwsh 替身验证：`Kill()` 只终止 smoke 直接 Start 的进程对象，未验证“目标进程自行再派生孙进程”时的清理语义（真实 App 冒烟预期不派生，但本轮未证明）。
- 新增夹具依赖 `pwsh` 7 与 PATH 中的 `git`（临时仓库），在无 git 的机器上 B9 段会失败；属测试环境依赖，不是产品依赖。
- 真实仓库探针是只读采样，dirty 48 含并行 P10 写入；本报告不据此判断 P10 侧安全，也不宣称 task-55 已验收——交付不等于验收，后续由独立 Flash 复核裁定。

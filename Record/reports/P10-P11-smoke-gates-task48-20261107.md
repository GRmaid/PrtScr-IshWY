# task-48：P10/P11 脚本与 CI 可信门禁交付

日期：2026-11-07
工作区：E:/Explore/GoodGood/PrtScr
工位：task-48（P11 脚本/CI 写域；未改 src）
HEAD：5e74f6215ee0a03d2bd22ccf7a48cc44811316e7
结论：脚本侧可信入口与离线负例已交付并自测；真实 App 冒烟、端到端门禁、工作流执行均未运行，因此 P10/P11 仍未完成验收，发布继续冻结。

## 一、做了什么

1. 新增 `scripts/lib/SmokeResult.ps1`（137 行）：只读校验 --smoke 结果文件。严格核对 8 个字段齐全、schemaVersion=1、nonce、子进程 PID、可执行文件绝对路径、dataRoot、success 必须为布尔 true、error 必须为 null；checks 必须恰为 `xaml-initialized`、`main-window-loaded`、`dispatcher-roundtrip` 三个且不重复。路径统一规范化后比较。不启动 App。
2. 新增 `scripts/lib/SourceIdentity.ps1`（149 行）：版本号白名单校验 `^[0-9]+.[0-9]+.[0-9]+(?:-[0-9A-Za-z]+(?:.[0-9A-Za-z]+)*)?$`；git 源身份（HEAD、dirty、输入文件逐文件 SHA256 汇总指纹）；release 模式脏树拒绝；环境身份（OS/pwsh/dotnet/culture）。指纹输入覆盖 Directory.Build.props/targets、global.json、NuGet.config、sln、src 下源码/资源、scripts 下 ps1、工作流 yml；显式排除 bin/obj/.tmp/artifacts/data/Record/docs 等。
3. 新增 `scripts/gate.ps1`（336 行）：单一可信入口，按 ci/release 模式串联 build → test → format → 便携发布 → smoke，全部绑定同一源码身份；写 `artifacts/evidence/<时间戳>-<mode>/` 下的日志、逐文件哈希、manifest.json。源码在门禁中途变化则 `source-changed-during-gate` 失败。
4. 重写 `scripts/smoke.ps1`（141 行）：为子进程生成 `<TEMP>/IshWY-smoke/<32位nonce>` 空根，注入 `ISHWY_SMOKE_ROOT` / `ISHWY_SMOKE_NONCE`；默认 30 秒有界等待，超时只 Kill 自己持有的 Process 对象；以脚本侧复算的 PID/路径/根与结果文件核对；失败保留根以便取证，成功默认删除临时根。输出 `SMOKE_ROOT=`、`RESULT_PATH=` 机器可读标记供 gate 归档。
5. 新增 `scripts/tests/offline-negatives.ps1`（156 行）：不启动 App 的离线负例套件，覆盖结果文件 14 种坏形态、版本号 8 拒 3 收、release/ci 脏树策略、指纹 5 种行为。
6. 修改 `scripts/publish.ps1`：在拼接路径/调用外部命令前校验 Runtime 与版本号；旧 `Start-Process --smoke` + 删除 outDir/data 的假成功路径改为子 pwsh 调 `scripts/smoke.ps1`，非 0 即 throw；不再在发布目录生成或清理 data。
7. 修改 `.github/workflows/ci.yml`：改为调用可信入口 `pwsh -NoProfile -File scripts/gate.ps1 -Mode ci -Version 0.0.0-ci -SkipSmoke -NoZip`；manifest 记 smoke=not-run，明确该作业只证明构建与打包，不是发布验收。
8. 修改 `.github/workflows/release.yml`：改为 `gate.ps1 -Mode release -Version $env:VERSION`，**不跳过 smoke**；上传 zip 与 manifest.json；release 模式下 releaseReady=false 时门禁非 0，杜绝“未验证也发版”的绿灯。
9. 未改 src。此前误入 src 的试验性实现已全部 `git checkout --` 撤销并删除新增文件；当前 `git status --porcelain -- src` 的变更全部属于 task-47。

## 二、证据

### 2.1 离线负例（可反复执行，不启动 App）

命令：`pwsh -NoProfile -File scripts/tests/offline-negatives.ps1`
结果：**34/34 PASS，exit 0**。覆盖：
- 正向控制：合法结果被接受；14 种负例被拒（缺失、非法 JSON、schemaVersion=0、nonce/PID/exe/root 不符、success=false、success 为字符串 "true"、checks 缺一项、checks 多一项、checks 重复、error 非 null、多余字段）。
- 版本：接受 1.3.1、0.0.0-ci、1.3.1-beta.1；拒绝空串、1.3、v1.3.1、含空格、含分号命令、含 `$()`、含 ../、含换行。
- 源码策略：release+dirty 拒绝、release+clean 允许、ci+dirty 允许；指纹在 src 变更时变化、docs 变更时不变、新增 src 文件时变化。
- 说明：早期该套件 33/34，唯一失败是 PowerShell 7 把 JSON 整数解析为 long；已在 `Test-IntegerValue` 中同时接受 int/long 后复跑通过。

### 2.2 语法与门禁早失败（均在构建前失败，未产生 artifacts）

- 7 个 ps1 全量解析：`[System.Management.Automation.Language.Parser]::ParseFile` → PARSE OK，failures=0。
- `gate.ps1 -Mode release -Version 1.3.1`（脏树）→ exit 1，"发布模式要求工作树干净（HEAD 5e74f6215ee0a03d2bd22ccf7a48cc44811316e7）；当前 dirty=true…"（gate.ps1:55 附近）。
- `gate.ps1 -Version "v1.3.1"` → exit 1，"版本号非法：'v1.3.1'"（SourceIdentity.ps1:22 附近），证明版本校验发生在任何外部命令之前。
- `gate.ps1 -Runtime "x64"` → exit 1，"Runtime 非法：'x64'"（gate.ps1:37 附近）。
- 三次均未创建 artifacts/evidence，说明拒绝发生在副作用之前。

### 2.3 启动器负例（真实子进程，非 App）

- 自制 sleeper 于 `.tmp/smoke-fake/out/smoke-fake.exe`，`smoke.ps1 -TimeoutSeconds 2` → exit 1，reasons=`timeout-2s`、`result-missing`；PID 51388 在 Kill 后确认消失；临时根 `C:/Users/asus/AppData/Local/Temp/IshWY-smoke/0bc90ee9a99e4075a076249c57b7a5ba` 保留。
- `C:/Windows/System32/hostname.exe`（exit 0 但不写结果）→ exit 1，reason=`result-missing`，根 `.../IshWY-smoke/a046201e657540e89f43bb806df50d66` 保留。
- 不存在的 exe → 脚本在 Resolve-Path 处报错，exit 1。
- 以上证明“计时只能失败不能成功”“缺结果不算通过”“只终止自有子进程”。

### 2.4 构建/测试/格式过程证据（当前树，含 task-47 未提交 src，不构成本单验收）

均经 `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag <tag> -Cmd <...>` 串行执行：
- `s48-proc1`：`dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-s48-proc1/` → exit 0，**0 警告 0 错误**，18.86s。
- `s48-proc2`：`dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --no-build --nologo -p:OutDir=...out-s48-proc1/` → exit 0，**324 passed / 0 failed / 0 skipped**，85ms。324 = 基线 305 + task-47 新增 19 条，非本单产出。
- `s48-proc3`：`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → exit 0，6s。
- 运行前后 src 哈希完全一致，证明三段门禁绑定同一快照：App.xaml.cs `8e0ea4cc…dd58`、SmokeSession.cs `7c628f8e…6634`、SmokeProtocol.cs `412d6e99…f6f0`、SmokeRunState.cs `30d94bcd…7c3`；task-47 最后写入时间 18:19:28，门禁期间无新写入。

### 2.5 关键实现位置

- releaseReady 判定：`scripts/gate.ps1:275`；release 模式不满足即拒绝：`gate.ps1:276-277`、`gate.ps1:325-327`。
- 源码稳定性比对：`scripts/gate.ps1:205-215`；manifest 落盘：`gate.ps1:314-315`。
- 超时与自有子进程终止：`scripts/smoke.ps1:75`、`smoke.ps1:93`；结果复算：`smoke.ps1:122`。
- 结果字段严格核对：`scripts/lib/SmokeResult.ps1:51-99`。
- 版本与 release 脏树策略：`scripts/lib/SourceIdentity.ps1:19`、`:117`。
- 发布脚本冒烟接线：`scripts/publish.ps1:163-172`；zip：`publish.ps1:185`。
- 工作流入口：`.github/workflows/ci.yml:26`、`.github/workflows/release.yml:48`。

## 三、未完成

1. **真实 App 冒烟未运行**：`IshWY.exe --smoke` 会启动 GUI；需在 task-47 运行时代码通过独立审查后，由主人在隔离 OutDir、可控桌面下执行。当前用户已有正常实例（PID 4396，14:07 启动），本单不唤醒、不结束。
2. **端到端 gate 未运行**：完整跑会执行真实 publish 并启动 App，越出本单“不实际打包、不运行 App 冒烟”的边界。
3. **CI/Release 工作流未执行**：本单不触发远端 Actions，wf 语法仅静态检查。
4. **无 artifacts/evidence 产物**：门禁未完整执行，仓库内没有 manifest.json 可审计样本。
5. **App 侧行为未验收**：XAML 初始化、主窗 Loaded、Dispatcher 往返的顺序语义由 task-47 实现；本单只验证结果文件与脚本边界，无法证明其内部时序。
6. **脚本未经第三方独立复核**：尚待 task-49/复核工位审阅。

## 四、风险

1. **默认构建目录冲突**：gate.ps1 的 build/test 使用仓库默认 bin/obj；用户实例正在运行时可能锁定 DLL，导致构建失败或误判。建议门禁在实例停止后执行，或给 gate 增加隔离 OutDir 参数（本单未加，避免扩大接口）。
2. **Release 工作流新增 GUI smoke 依赖**：release.yml 现在强制 smoke；windows-latest 是否有可用交互桌面未验证，若不可用将 30 秒失败并阻断发版。阻断方向正确（fail-closed），但需一次真实演练确认，否则 tag 流程会卡住。
3. **成功语义仍依赖 App 侧**：脚本能核对 success/checks/路径/根，但不能证明三个 checks 的先后顺序（Loaded 完成后才做 Dispatcher 往返），也不能证明 App 未写真实 config/history/log。该风险在 task-47 审查项内。
4. **dataRoot 仅字段核对**：脚本验证 dataRoot 等于临时根，不能独立观测 App 是否另写默认 data 目录；隔离性需由 App 代码与运行后目录比对来证明。
5. **指纹覆盖面**：指纹只涵盖白名单输入类型；未来新增的构建输入类型可能漏检（会在 manifest 的 inputCount 中体现，但不会自动报警）。
6. **PowerShell 版本**：脚本按 pwsh 7 编写（ConvertFrom-Json 行为、三元等），Windows PowerShell 5.1 不支持；工作流与文档均用 pwsh。
7. **标记行在失败时也会打印**：`SMOKE_ROOT=`/`RESULT_PATH=` 在判定前输出，失败时 gate 仍会尝试归档；这是有意的取证行为，但日志读者需以退出码/verdict 为准。
8. **测试数量漂移**：324 条包含 task-47 新测试；若复核按 305 基线比对会误判，应在同一源码快照上重新取基线。

## 五、待决

1. task-47 的 src 实现何时冻结/合并，以及由谁在什么窗口执行首次真实 --smoke（需要主人同意停止或避开现有实例）。
2. Release 工作流是否保留强制 GUI smoke，或改为“tag 前人工同 SHA 冒烟 + 工作流只打包”，需主人按发布流程拍板；当前实现选择 fail-closed。
3. gate 是否增加 `-OutDir` 隔离构建参数，以避免与运行实例的文件锁冲突。
4. P10/P11 的验收口径：是否接受“脚本离线证据 + 一次人工真实冒烟”为最小闭环，还是要求 CI 上也跑 GUI smoke。
5. 最终版本号与发布时机；发布冻结未解除，本单未打 tag、未创建 Release、未 commit/push。

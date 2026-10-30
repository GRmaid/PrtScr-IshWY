# P-03（D01）关闭 Umi-OCR 后彻底不连接 — 实现与核查报告

- 单号：task-4（P-03 / 外部评审 D01）
- 执行基线：HEAD `9c81858`（工作树未提交；本单改动叠在 HEAD 之上）
- 写域：`src/PrtScr.Ocr/OcrService.cs`、`src/PrtScr.App/MainWindow.Ocr.cs`、`src/PrtScr.App/MainWindow.xaml`、`src/PrtScr.Core/ConfigService.cs`、`Record/reports/**`
- 日期：2026-10-30

## 裁决摘要（三行）

1. 关闭 `PreferUmiOcr` 后，Umi-OCR 不会进入候选引擎列表、不会被探测、识别失败也不会回退到它；全仓库唯一 `HttpClient` 只存在于 `UmiOcrClient`，其两个实例化点都带开关闸门。
2. 「测试 Umi-OCR 连接」是唯一联网入口：只在用户点击时执行一次 `ProbeAsync` 并写日志，不改变启用状态；自动路径（启动、切开关、改端口）一律 `probeUmi=false`，不发起任何请求。
3. 门禁全绿：整包构建 0 警 0 错（EXIT=0，15.50s）、Core.Tests 77/77、`dotnet format` rc=0；未 commit、未跑 `--smoke`。

## 一、做了什么

基线缺陷（HEAD `9c81858`）：
- `src/PrtScr.Ocr/OcrService.cs` 改前 14-15 行无条件 `new UmiOcrClient(umiPort)`，且 `preferUmi=false` 时列表为 `[windows, umi]` —— Windows OCR 失败后会回退 Umi。
- `src/PrtScr.App/MainWindow.Ocr.cs` 改前 49-60 行：`RefreshOcrStatusAsync()` 无参数，每次调用都执行 `await new UmiOcrClient(...).ProbeAsync()`；启动、切开关、改端口都会联网，即使开关关闭。

本单改动（4 文件，+98/−31）：

1. `src/PrtScr.Ocr/OcrService.cs:13-15`：关闭时只构造 Windows OCR，Umi 连候选都不构造。
   `_engines = preferUmi ? [new UmiOcrClient(umiPort), windows] : [windows];`
   类注释与 `preferUmi` 参数注释同步改为「false 时完全不使用、不探测」。
2. `src/PrtScr.App/MainWindow.Ocr.cs`：
   - `:36` `TestUmi_Click` 改为唯一传 `probeUmi: true` 的调用点；
   - `:49-53` 方法改为 `RefreshOcrStatusAsync(bool probeUmi = false)`，注释明确「默认不联网」；
   - `:60-65` 仅探测时禁用按钮/显示进度环；`:71-76` 仅探测时构造客户端 + 单次 `ProbeAsync` + 写日志「OCR: 测试 Umi-OCR 端口 {port} → 在线/离线（错误）」；
   - `:78-127` 状态文案按 prefer/umi 探测结果重写，关闭时明示「已关闭（不会连接…）」，探测到在线也标明「仅检测，不参与识别」；
   - `:136-146` 按钮/进度环恢复与 `_ocrProbeBusy` 释放只在探测路径做 UI 恢复。
3. `src/PrtScr.App/MainWindow.xaml:1254` InfoBar 初始文案「检测中…」→「尚未检测（关闭 Umi 时不会自动探测）」；`:1256` 底部说明改为「关闭时程序不会连接 Umi-OCR；点测试按钮做单次检测……未连接则自动回退 Windows OCR」。
4. `src/PrtScr.Core/ConfigService.cs:231-232`：Load 时把 `UmiOcrPort` 夹到 1–65535（防手改配置拼非法 URL），紧接 P-02 的 `HistoryPerCategory` 夹取（`:229`）。`PreferUmiOcr` 默认 false（`:60`）、`UmiOcrPort` 默认 1224（`:64`）均未改。

## 二、证据

### 2.1 全仓库 Umi 连接入口与闸门（grep 清单）

```
$ rg -n "HttpClient" src        → 仅 src/PrtScr.Ocr/UmiOcrClient.cs:19
$ rg -n "127\.0\.0\.1|localhost" src → 仅 UmiOcrClient.cs:11（注释）、:25 BaseUrl
```

| # | 连接入口 | 网络动作 | 关闭时是否可达 | 闸门证据 |
|---|---|---|---|---|
| 1 | `OcrService.cs:15` | 构造 `UmiOcrClient`（识别用候选） | 否 | 三元式只在 `preferUmi=true` 分支构造；false 时列表为 `[windows]` |
| 2 | `OcrService.cs:37` → `UmiOcrClient.cs:85` → `:51-63` | GET `ProbeAsync` | 否 | 只在 `foreach (var engine in _engines)` 内；列表不含 Umi |
| 3 | `OcrService.cs:40` → `UmiOcrClient.cs:87/35` | POST `RecognizeAsync` | 否 | 同上；`preferredEngineId` 分支 `OcrService.cs:25` 也只查同一 `_engines` |
| 4 | `MainWindow.Ocr.cs:73` | GET `ProbeAsync` | 仅用户点「测试 Umi-OCR 连接」 | `probeUmi` 唯一 true 来源是 `:36 TestUmi_Click`；按钮 `MainWindow.xaml:1235-1238` |
| 5 | `OcrUiService.cs:24` | 构造 `OcrService(config)` | 否（只是传参） | 配置关闭时走到 `[windows]` 分支 |

结论：关闭状态下可达的 Umi 连接路径为 **0** 条；不点「测试 Umi-OCR 连接」时不会产生任何 Umi 请求。

### 2.2 自动刷新路径全部不联网

`$ rg -n "RefreshOcrStatusAsync" src` 调用点：

| 调用点 | 触发时机 | probeUmi | 是否联网 |
|---|---|---|---|
| `MainWindow.xaml.cs:158` | 设置页 OnLoaded | false（默认） | 否 |
| `MainWindow.Ocr.cs:33` | 修改 Umi 端口 | false（默认） | 否 |
| `MainWindow.Ocr.cs:45` | 切换 Umi 开关 | false（默认） | 否 |
| `MainWindow.Ocr.cs:36` | 点击「测试 Umi-OCR 连接」 | true | 是，单次 |

### 2.3 「测试连接」单次探测证据

- `:36` → `:53` `probeUmi=true` → `:73` `new UmiOcrClient(port).ProbeAsync()`（唯一探测站点）→ `:74-75` 日志。
- `ProbeAsync`（`UmiOcrClient.cs:51-63`，本单未改）依次尝试 `/api/ocr/get_options`、`/api/ocr/get_version`、`/` 三个本机端点，每次 2 秒超时；即**一次点击 = 一次 `ProbeAsync` = 最多 3 个 loopback GET**。
- 探测不改启用状态：`PreferUmiOcr` 仅在 `MainWindow.Ocr.cs:43` 由开关写回。
- `_ocrProbeBusy`（`MainWindow.xaml.cs:44`）保证并发点击复用同一次刷新，不叠加请求。

### 2.4 关闭时识别路径（失败也不回退 Umi）

`OcrUiService.cs:24` 用当前配置构造 `OcrService` → 关闭时 `_engines=[windows]` → `OcrService.RecognizeAsync` 只探测/调用 Windows OCR；全部失败返回 `OcrResult(false, "", Error:"没有可用的 OCR 引擎")`（`OcrService.cs:45`），不会出现「转 Umi」回退。

### 2.5 门禁（本单最终工作树）

```
1) pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p03 \
     -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p03/"
   → 0 警告 0 错误，已用时间 00:00:15.50
   → BUILD-GUARD: EXIT=0 elapsed=16s tag=p03
2) pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p03test \
     -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo"
   → 失败 0 / 通过 77 / 总计 77，81ms，EXIT=0
3) dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
   → exit 0（无输出）
```

未使用 `-t:Compile`、未跑 `--smoke`（避免写主人真实 `data/`）、未 commit/push。

### 2.6 工作树范围

`git diff --numstat` 本单 4 文件：`MainWindow.Ocr.cs +77/−25`、`MainWindow.xaml +11/−2`（含 P-02 存量 9 行新增）、`ConfigService.cs +6/−0`、`OcrService.cs +4/−4`，合计 +98/−31。
注意：
- `MainWindow.xaml` 的 diff 里另有 P-02（task-2）未提交的截图历史卡 9 行（`PruneHistoryNowButton` + 说明，约 `:1135-1143`），不是本单改动。
- 工作树同时有 P-04/P-06/P-09a/P-07b 等其他单的改动，本单未触碰这些文件；最终门禁需由 lead 在工作树冻结后同 SHA 复跑。

## 三、进行中

本单无进行中项。建议并入 P-12（行为回归清单）：
- 断言 `new OcrService(false, port).Engines` 不含 Umi（需测试项目引用 `PrtScr.Ocr`，或把候选选择抽成纯函数放 `PrtScr.Core`）。
- 断言 `ConfigService.Load` 对 `umiOcrPort=0/70000` 夹取到 1/65535。

## 四、风险与遗留

1. **未在真实网络环境验证「零请求」**：本机未运行 Umi 服务，也未抓包；结论由「全仓唯一 HttpClient + 两个实例化点闸门 + 调用点默认参数」静态证明。主人侧可用 Resource Monitor / 防火墙验证。
2. **状态页不再自动显示「Umi 在线」**：为满足「不点击则无网络请求」，启用状态下也需点「测试 Umi-OCR 连接」才显示在线；这是有意的行为变化（原实现每次刷新都探测）。
3. **一次点击 = 最多 3 个 loopback GET**：`ProbeAsync` 内部按既有实现逐端点探测（2 秒/次）。若验收把「一次请求」严格理解为单个 HTTP 请求，需要改 `UmiOcrClient`（本单写域外）。
4. **无自动化测试覆盖**：Core.Tests 只引用 `PrtScr.Core`，`PrtScr.Ocr`/`PrtScr.App` 无法直接单测；门禁只能证明编译与既有 77 条不回归。
5. **WinUI 未真机点按**：启动、切开关、点测试按钮的实际界面反馈未在本机运行验证（运行实例占用默认 bin），需主人按 §六 实测。
6. **XAML 与他单改动同文件**：最终 diff 会混有 P-02 的 9 行；合并/提交时注意区分。

## 五、待确认（给 lead）

1. 是否接受「启用 Umi 时状态页也不自动探测、显示『已启用，未检测』」这一更严格口径？若要求启用后自动探测，需在开关切换路径放开一次探测，会与「不点击则无网络请求」冲突。
2. 「测试连接」是否必须严格单 HTTP 请求？若必须，需把 `UmiOcrClient.ProbeAsync` 改为单端点（`/api/ocr/get_version`），该文件不在本单写域，请另立单或扩写域。
3. `ConfigService` 的 `UmiOcrPort` 夹取是否保留（与 P-02 的 `HistoryPerCategory` 夹取同风格，属防御性改动）。
4. P-12 是否按 §三 补两条自动化检查（引擎候选断言、端口夹取），以及测试项目是否允许引用 `PrtScr.Ocr`。
5. 本机无 Umi 与断网环境，「关闭时零连接」需主人实测确认；是否接受静态证据作为本轮验收。

## 六、主人实测步骤

1. 关闭「优先使用 Umi-OCR」开关，重启 IshWY → 设置页 OCR 状态应显示「OCR 引擎就绪：使用 Windows OCR」，副文案「Umi-OCR：已关闭（不会连接；点「测试 Umi-OCR 连接」可单次检测）」。
2. 保持关闭，断网（或打开 Resource Monitor / 防火墙日志盯 `IshWY.exe`）→ 依次做：启动程序、切换开关、改 Umi 端口、截图并识别。全程不应出现到 `127.0.0.1:1224` 的连接。
3. 关闭状态下点「测试 Umi-OCR 连接」→ 若 Umi 未运行，约 2–6 秒后状态显示「已关闭，本次探测未连接（端口 1224）」；日志（`data/logs`）出现「OCR: 测试 Umi-OCR 端口 1224 → 离线（…）」。
4. 启动 Umi-OCR server 后再点测试 → 显示「已关闭，但本次探测在线（端口 1224；仅检测，不参与识别）」，开关仍关闭；随后识别结果应仍由 Windows OCR 产出。
5. 打开开关 → 状态显示「已启用，未检测」；点测试 → 显示在线/优先；识别走 Umi。再停掉 Umi 服务并识别 → 自动回退 Windows OCR，不报错、不卡死。

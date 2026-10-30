# P-12 回归测试补强与独立核查（task-6）

- 执行：maid-check（2026-10-30）
- 工作树快照：HEAD `9c81858`（= 0b1ca41 + P-01/P-07 提交 + 记录提交）+ 未提交 A 批次 diff；快照时刻约 21:00，task-17（P-07c）仍在改采集层，工作树未冻结。
- 本单写域：`src/PrtScr.Core.Tests/**`、`Record/reports/**`。未改产品代码，未新增依赖，未 commit。

## 裁决摘要

| 单 | 主题 | 裁决 | 一句话依据 |
|---|---|---|---|
| P-01 | 编辑出口统一 / 进入幂等 / 失败保草稿 | **通过** | 唯一当前画面入口与四出口收敛均已落地，抽查行号与报告一致 |
| P-02 | 配额变更不毁历史 / 清理需确认 | **通过** | NumberBox 已不触发 PruneNow；预演与确认路径成立；Core 夹取有 12 条新用例覆盖 |
| P-03 | 关闭 Umi 即不连接 | **通过** | 关闭时引擎列表不含 Umi；唯一探测点仅在点「测试连接」；调用图静态闭合 |
| P-04 | 捕获来源与会话身份 | **不通过（阻断 1 项，已立 task-18 P-04b）** | A02/A05 验收项本身通过，但取景中取消使 `_isCapturing` 永久为真、Esc 残留，属 P-04 引入的回归 |
| P-06 | 钩子回调去阻塞 | **通过** | 回调内无写盘/锁/await；投递化未破坏去抖、吞键与顺序 |
| — | **A 批次整体** | **不可放行：需 P-04b 修复并复验** | 与 `Record/reports/A-batch-app-layer-review.md` 的 P1 阻断一致；其余四单具备放行条件 |

---

## 一、做了什么

1. **补 Core 回归用例 12 条**（77 → 89），只覆盖本批触达、且可在 `PrtScr.Core` 内直接断言的纯逻辑：配置夹取、默认值、JSON 字段名/值。未新增测试框架依赖，未给 App/Capture 增加项目引用。
2. **独立核查五张实现单**：先读报告的三行摘要与结论段，再抽查证据是否真落地（文件在不在、调用点是否真收敛、行号是否对得上），最后以单子「验收」逐项判。P-02/P-03 为本单执行者本人实现，按同一口径列出证据，不因自报而免检。
3. **产出人工资格行为检查表 14 条**：贴图 3、历史 2、OCR 2、采集 2、热键 3、P-04 降级可见性 1、取景取消收尾 1（lead m00526 追加，判据待 P-04b 校准）。
4. **复跑门禁**：整包 build、Core.Tests、format 三条已执行；smoke 按纪律未执行（见 2.2）。
5. 未运行 GUI、未做性能/资源实测、未抓包（按项目纪律由主人本人实测）。

---

## 二、证据

### 2.1 新增 Core 用例清单（12 条，全部通过）

文件：`src/PrtScr.Core.Tests/ConfigServiceTests.cs`

| 编号 | 用例 | 覆盖规则 | 断言 / InlineData | 位置 |
|---|---|---|---|---|
| T1 | `Load_HistoryPerCategory_IsClampedToAllowedRange`（Theory，5 条） | 配额手改越界夹到 1–200（P-02 的 `ConfigService.cs:229`） | (0→1)(-5→1)(1→1)(200→200)(999→200) | :115-131 |
| T2 | `Load_UmiOcrPort_IsClampedToAllowedRange`（Theory，5 条） | 端口手改越界夹到 1–65535（P-03 的 `ConfigService.cs:232`） | (0→1)(-5→1)(1→1)(65535→65535)(70000→65535) | :132-143 |
| T3 | `Load_MissingFile_KeepsOcrDefaultsOffAndPort1224`（Fact） | OCR 默认值：Umi 默认关闭、端口 1224、配额 30 | `PreferUmiOcr=false`、`UmiOcrPort=1224`、`HistoryPerCategory=30` | :144-155 |
| T4 | `OcrAndHistoryFields_KeepDocumentedJsonNamesAndDefaults`（Fact） | 字段名与默认值契约（防止改名/改默认值悄悄破坏配置兼容） | 序列化 JSON 键 `preferUmiOcr`/`umiOcrPort`/`historyPerCategory`/`historyPinCrop`/`saveHistory` 及值 false/1224/30/true/true | :157-175 |
| — | 私有 helper `WriteConfig(TempDirectory, string json)` | 写入 temp/data/config.json 后 Load | 复用既有 `TestHelpers.TempDirectory` | :177-185 |

基线 77 条全部保留（未改既有用例），故 77 + 12 = 89。

### 2.2 复跑的门禁结果

| 门禁 | 命令 | 结果 |
|---|---|---|
| ① 整包 build | `.tmp/build-guard.ps1 -Tag p12 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p12/"` | **0 警告 0 错误**，16.75s，`BUILD-GUARD: EXIT=0 elapsed=17s tag=p12`；产物 `.tmp/out-p12/IshWY.dll` |
| ② Core.Tests | `.tmp/build-guard.ps1 -Tag p12test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo"` | **通过 89 / 失败 0 / 跳过 0 / 总计 89**，81ms，`BUILD-GUARD: EXIT=0 elapsed=2s tag=p12test`；>77 达标 |
| ③ format | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | `FORMAT-RC=0`，无输出 |
| ④ smoke（可选） | `IshWY.exe --smoke` | **未执行**：会写主人真实 `data/`，项目纪律禁止代理代跑；且工作树含 task-17 并行改动未冻结。由 lead 在冻结后统一裁决 |

说明：`-t:Compile` 已确认会因 XAML 生成不在依赖链而假通过（P-01 报告记录 CS0103），本次一律用整包 build，不再使用。

### 2.3 五单独立核查

#### P-01（B01/B02/B03）——通过

验收：编辑未完成时复制/OCR/裁剪取当前合成；重复点标注不清空；编码失败草稿仍在。

| 判据 | 抽查证据 | 结论 |
|---|---|---|
| 唯一「当前画面」出口 | `src/PrtScr.App/Views/NativePinWindow.Export.cs:54` `TryGetCurrentPng` → `:164` `TryComposeCurrentPng`；调用点 `EditApply.cs:84`、`Interaction.cs:212/289/303`、`Export.cs:162/164` | 落地 |
| 管理器拿新图、Unavailable 不落旧图 | `PinManagerWindow.cs:88/178` 调 `TryGetCurrentPng`；`:176-188` `TryGetLivePng`；`:186` 提示「贴图正在标注且暂未响应…」并 return | 落地 |
| 重复进入标注幂等 | `Edit.cs:113` guard（:116-121），日志「Pin: already in edit mode, ignored」 | 落地 |
| 合成失败保草稿 | `EditApply.cs:77-106`：bytes 为 null → `ShowHint`(:88) + 日志(:89) + return；仅成功分支才替换 `_pngBytes` 并退出标注(:93-105) | 落地 |
| Ctrl+C 标注态带标注图 | `NativePinWindow.Window.cs:235-239` WM_KEYDOWN `0x43` + Ctrl → `CopyCurrentPng`，不退出标注 | 落地 |

遗留：交互未实测（依赖行为表 A1–A3，主人 2026-10-30 已实测通过）；管理器同步等待贴图线程上限 1000ms（`Export.cs:38,88`）。

#### P-02（C04）——通过

验收：改配额后历史文件数不变、后续截图按新配额保留；清理先显示真实张数与分类、确认后删除、取消零删除；PruneNow 仍可显式调用但不再由 NumberBox 触发。

| 判据 | 抽查证据 | 结论 |
|---|---|---|
| NumberBox 不再直接删 | `MainWindow.xaml.cs:293-309` `HistoryLimit_ValueChanged` 仅改配置 + Save + 刷新，无 `PruneNow` | 落地 |
| 已有图不受配额变更影响 | 保存新图时只修剪所属分类：`CaptureHistoryService.cs:111-115` `PruneLocked(PerCategoryLimit, saved.CategoryKey)`；`PruneLocked` 的 `onlyCategoryKey` :260-272 | 落地（lead 已裁定采纳） |
| 预演真实张数/分类 | `GetPrunePlan` :142-173（锁内分组、倒序、只统计）；`PruneNow` 返回实际删除数 :129-136 | 落地 |
| 确认框 + 取消零删除 | `PruneHistoryNow_Click` `MainWindow.xaml.cs:312-360`：`GetPrunePlan` → `ConfirmDialog.ShowAsync`（列出分类明细，最多 8 组）→ 取消设「已取消清理，没有删除任何截图。」并 return | 落地 |
| 状态行提示超出配额 | `RefreshHistoryStatus` `MainWindow.xaml.cs:255-277` 计算 `overLimit` 并追加「其中 N 张超出当前配额…」 | 落地 |
| 配置夹取 | `src/PrtScr.Core/ConfigService.cs:229` | 有 T1 单测 |

#### P-03（D01）——通过

验收：关闭后候选列表不含 Umi、状态页不发起探测、识别不回退 Umi；点「测试连接」可单次探测且不改启用状态。

| 判据 | 抽查证据 | 结论 |
|---|---|---|
| 引擎列表不含 Umi | `src/PrtScr.Ocr/OcrService.cs:15` `_engines = preferUmi ? [new UmiOcrClient(umiPort), windows] : [windows];` | 落地 |
| 探测仅由点击触发 | `MainWindow.Ocr.cs:73` 在 `probeUmi` 分支才 new `UmiOcrClient(...).ProbeAsync()`；唯一 true 调用点 `:36` `TestUmi_Click`（XAML 按钮 :1237） | 落地 |
| 自动路径不探测 | `RefreshOcrStatusAsync` 调用点 `MainWindow.xaml.cs:158`（OnLoaded）、`MainWindow.Ocr.cs:33`（改端口）、`:45`（切开关）均默认 `probeUmi=false` | 落地 |
| 识别不回退 | 识别只遍历 `_engines`（`OcrService.cs:37/40`），关闭时列表无 Umi；App 唯一构造点 `OcrUiService.cs:24` | 落地 |
| 端口夹取 | `ConfigService.cs:232` | 有 T2 单测 |

遗留：一次「测试连接」= 一次 `ProbeAsync`，内部最多 3 个 loopback GET（`UmiOcrClient.cs:51-63` 的探测路径列表），报告按 lead 裁定不写「单 HTTP 请求」；未抓包验证「零请求」，由调用图静态闭合。

#### P-04（A02/A05）——不通过（1 项阻断，已立 task-18）

**先记通过项**（这两条是单子验收）：

| 判据 | 抽查证据 | 结论 |
|---|---|---|
| 结果携带实际来源与降级原因 | `Capture.cs:28-62` `CaptureContentOrigin` / `SelectionCapture` / `CaptureOutcome`；降级 `:374-381`（Origin=Screen、Normal、Notice、日志「Capture: 窗口捕获降级为屏幕区域裁切：{reason}」:379）；Busy 不降级 `:398`；Timeout 文案 `:398` 附近 | 落地 |
| 历史元数据随来源变化 | 确认写入 `Capture.cs:580-581` 用 `selection.Mode/ProcessName/WindowTitle`；另存 `:703-704` 同 | 落地 |
| 旧结果不得改新会话 | `CaptureService.cs:34/44/54-67` 会话 API；校验点 `Capture.cs:537/573/590`（确认）、`:435/:454`（OCR）、`:666/:689/:707`（另存）；`ResolveHistoryTarget` grep 0 命中 | 落地 |

**阻断项（P1）：取景中取消的收尾闭环断裂**

- 现象：Enter 确认取图（窗口捕获最长约 2s）期间再按 F1 → 会话被作废，但收尾被跳过 → `CaptureService._isCapturing` 永久为 true；取景期注册的全局 Esc 永不注销。之后 F1 只写「already capturing, cancel」空转，Esc 在整机范围被吞，直到重启应用。
- 证据链（当前工作树）：
  1. `CaptureService.cs:145` `_isCapturing = true`；`:152` `Hotkey?.BeginCaptureMode()`（注册全局 Esc）。
  2. `Capture.cs:533` `HideForCapture()` → `:168-187`，`:172` `_closed = true`，`:173` `StopEscPolling()`，`:174` `EscOverPinHook.SetOverlayKeysActive(false)`，`:175` `AppWindow.Hide()`；`:534` `await CaptureSelectionAsync(session)`。
  3. 此间 F1 → `CaptureService.cs:101-106` 见 `_isCapturing` 为真 → `_overlay?.Cancel()`；`Capture.cs:148-155` 先 `InvalidateCaptureSession(session)`（`CaptureService.cs:54-67` 置空）再 `Dismiss()`；`Dismiss` 在 `_closed` 时早退（`:228-234`），不触发 `Dismissed`。
  4. 取图返回 → `Capture.cs:537` 会话失效 → `:540` Dispose 后 return；finally `:615-621` 条件 `!ended && !keepOverlay && IsCurrentSession(session)` 为假 → `FinishCaptureSession()` 不执行。
  5. `_isCapturing` 全仓仅三处：`:145` 置真、`:83` 由 `Dismissed` 清、`:157` catch 清；本路径三处都不触发。`Dismissed` 不触发 ⇒ `:84` `Hotkey?.EndCaptureMode()` 不执行 ⇒ Esc 注册残留。
- 回归归属：HEAD 的 finally 是 `if (!ended) FinishCaptureSession();`，同场景取图返回后仍会收尾；P-04 为保护新会话把条件收紧为 `!ended && !keepOverlay && IsCurrentSession(session)`，代价是取消了兜底收尾。**故属 P-04 回归，非 HEAD 既有问题**（与 `A-batch-app-layer-review.md:91` 一致）。
- 交叉确认：`Record/reports/A-batch-app-layer-review.md` Q1 已判 P1 阻断，lead m00645 已开 **task-18 P-04b**（maid-impl，pending）。本轮由代码路径 + App 层复核双证；未构造慢窗口实机复现（纪律内不动 GUI）。

**可勾选缺陷清单（一条一改，交 task-18）**

- [ ] **P-04b-1 收尾所有权**：`src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs` `ConfirmAsync`（:509-621）改为持有一个本次确认的局部 owned 标志（进入 `HideForCapture` 后置真），`finally` 只要 `owned && !ended` 就执行 `FinishCaptureSession()`，不再以全局 `IsCurrentSession(session)` 作为唯一收尾条件；`Dismiss()`（:228-234）在 `_closed && !_sessionFinished` 时也要按同一漏斗收尾（或显式记录并交给 owned 标志），并避免旧会话 finally 关闭新会话。**验收**：确认中按 F1 取消后，再次 F1 立即可正常取景；Esc 不再被整机吞；日志出现带 session 的收尾行；新会话不被旧 finally 关闭。**判据行**：`Capture.cs:509-621`、`:228-234`；`CaptureService.cs:83-84/101-106/145`。

#### P-06（A01）——通过

验收：回调内不写盘、不持锁、不做业务；订阅者只投递轻量事件。

| 判据 | 抽查证据 | 结论 |
|---|---|---|
| 钩子回调不阻塞 | `HookProc` `EscOverPinHook.cs:533-569` 无 `File./Directory./lock/await`；写盘只在 `StartLogWriter` 的日志线程（`:237/:242/:253`） | 落地 |
| 事件投递化 | `RaiseDeferred` `:275-292`（ThreadPool + 订阅者异常 HookLog）；三个事件 `:492/:507/:518` | 落地 |
| 钩子线程日志不写盘 | `App.xaml.cs:247-251` `IsHookThread` → `LogDeferred` 只入 `ConcurrentQueue` 返回；写盘 `:253` | 落地 |
| 订阅点只入队 | `MainWindow.Hotkeys.cs:54-64` 的 `App.Log` + `OnHotkeyPressed` 均在 `DispatcherQueue.TryEnqueue` 内 | 落地 |
| 关键语义未破 | 修饰键自记账 `:354-363`、300ms 去抖（`TryClaim*`）仍在钩子线程、同步吞键 `:551`、`DispatchOverlayKey :456-469` 仅 TryEnqueue | 未破坏 |

遗留：未测回调耗时；`PinCropWindow` 键盘 LL 与 `PinMenuWindow` 鼠标 LL 未纳入；HookLog 满 128 条静默丢弃。

### 2.4 人工资格行为检查表（14 条）

> 判据列给「日志关键字」或「文件:行」；日志在 `data/logs`。状态：已实测=主人 2026-10-30 已验收；待实测=需真机；待校准=依赖 P-04b 最终文案。

| 编号 | 模块 | 前置条件 | 操作 | 期望 | 判据 | 状态 |
|---|---|---|---|---|---|---|
| A1 | 贴图 | 打开贴图并进入标注，画几笔 | 按 Ctrl+C | 剪贴板得到含标注的合成图；仍停留在标注态 | `NativePinWindow.Window.cs:235-239` → `Export.cs:162/164`；剪贴板目视 | 已实测 |
| A2 | 贴图 | 贴图处于标注态（工具栏撑高窗口） | 点裁剪 → 框选 → 确认 | 裁剪图只含图像区域，无工具栏、无拉伸 | `Interaction.cs:212/220-224`；图片尺寸目视 | 已实测 |
| A3 | 贴图 | 贴图处于标注态、不退出 | 打开贴图管理器 → 点复制 | 得到当前合成的新图；若贴图无响应则提示而非复制旧图 | `PinManagerWindow.cs:88/176-188`；`Export.cs:54` | 已实测 |
| B1 | 历史 | `data\history` 已有若干截图 | 把「每类保留」改为 1 | 文件数不变；状态行提示「其中 N 张超出当前配额…」 | `MainWindow.xaml.cs:293-309`（无 PruneNow）+ `:255-277`；目录文件数 | 待实测 |
| B2 | 历史 | B1 后存在超出配额项 | 点「立即清理…」→ 先取消；再点 → 确认 | 取消零删除；确认前列出真实张数与分类；确认后删除数 = 预演数 | `MainWindow.xaml.cs:312-360`；`CaptureHistoryService.cs:142/260-272`；状态行「已按配额清理 N 张」 | 待实测 |
| C1 | OCR | Umi 未运行且 `ocrPreferUmi=false` | 启动、切开关、改端口、识别一张图 | 全程无 `127.0.0.1:1224` 连接；日志无「测试 Umi-OCR 端口」；识别走 Windows OCR 不报 Umi 错误 | `OcrService.cs:15`；`MainWindow.Ocr.cs:73`；Resource Monitor / 日志 | 待实测（本机未装 Umi，未抓包） |
| C2 | OCR | 同上 | 点「测试 Umi-OCR 连接」 | 一次探测后显示在线/离线；开关保持原状，识别仍走 Windows OCR | `MainWindow.Ocr.cs:36/73`；日志「OCR: 测试 Umi-OCR 端口 … → 在线/离线」 | 待实测 |
| D1 | 采集 | 存在响应慢/被挂起的窗口（pssuspend 或大窗口） | 窗口模式 F1 → 选中该窗 → Enter | 最长约 2s 内返回；失败时给出「窗口捕获失败，已按常规截图保存（原因）」或 Busy 文案，不静默卡住 | `WindowCapture.cs:90` `PrintWindowTimeoutMs=2000`；`Capture.cs:379/398` | 待实测 |
| D2 | 采集 | 同一窗口上一张仍在处理中 | 立即再次 Enter | 提示「该窗口上一张截图还在处理中，请稍后重试」；不写历史、不写剪贴板 | `Capture.cs:398`；`data\history` 文件数不变 | 待实测 |
| E1 | 热键 | 无 | 连按 F1→Esc 20 次 | 每次都能出取景并退出；无 busy 残留、无「already capturing, cancel」连发 | `CaptureService.cs:101-106` 日志；20 次「Capture: start」+ 收尾 | 待实测 |
| E2 | 热键 | 全屏游戏（独占或无边框） | 游戏内按 F1 | 正常出取景 | `EscOverPinHook.cs:456-469`；日志 | 待实测 |
| E3 | 热键 | 贴图与 OCR 结果窗/取景窗重叠 | 按 Esc | 只关最上层，其余保留；400ms 内不重复关 | `Services/EscRouter.cs` 去重；窗口可见性 | 待实测 |
| F1 | 采集 | 同 D1 的慢窗口 | 窗口截图直到降级发生 | Toast 显示降级原因；历史按「常规截图」记录；日志有降级行 | `Capture.cs:379/600-604`；`.json` 元数据无原因字段（已知遗留） | 待实测 |
| G1 | 取景 | 任意窗口 F1 → 选窗 → Enter（进入约 2s 捕获） | 捕获飞行期立即再按 F1 一次；另起一轮捕获期间按 Esc | ①再按 F1：日志先出现 `Capture: already capturing, cancel session=<32hex\|none>`，随后 `Capture: session <…> finished reason=cancel`，紧接着一次 F1 能正常出图；②按 Esc：同样出现 `finished reason=cancel`，随后 Esc 不再被全局吞。窗口层对应 `Overlay: session <…> finished reason=…`、`Overlay: dismiss while hidden session=<…> reason=<…>` | 验收序列=`already capturing` 行 → `session … finished reason=cancel` → 下一次 F1 出图；Esc 后同；关键字由 lead m00602 定稿（其余收尾值：confirm-success/confirm-end/ocr/save/start-failed） | 待实测（P-04b 已落地） |

### 2.5 交叉复核输入（A 批次两份独立复核，供裁决参考）

- `Record/reports/A-batch-app-layer-review.md`：结论「A 批次不可放行：P1 阻断（= 本报告 P-04 阻断项，已立 task-18）；其余四项通过」；P2（`SaveSelectionAsync` finally 的会话判断）按 lead 裁决不返工。
- `Record/reports/A-batch-capture-layer-review.md`：采集层「可放行」，0 阻断；Q1（`WgcWindowCapture.cs:148-171` 设备 ABI 异常分支未 Release）已由 task-16 P-09c 修；Q2/Q3 待 task-17 P-07c；Q4/Q6 记录待后续单；`ScreenCapturer.cs:52-55` BitBlt 返回值未检查属既有行为，建议另立单。

---

## 三、进行中

1. **task-18 P-04b（pending，maid-impl）**：修复 P-04 收尾闭环。落地后需按最终日志文案校准本报告行为表 G1，并在新 SHA 上复跑受影响的门禁。
2. **task-17 P-07c（in_progress，maid-capture）**：WindowCapture 入口 token 预判 + 失败矩阵行 4/6 补注；不影响本单对五单的裁决，但工作树冻结前 build/test 结论以本快照为准。
3. **lead 终局裁决**：工作树冻结后同 SHA 复跑整包 build / Core.Tests / format / 可选 smoke；本报告的自报数字只作过程证据。
4. **B 批次接线（建议）**：App 消费 `WgcFailure/WgcHResult/WgcDetail`，两处窗口调用透传 `CancellationToken`（当前 `Capture.cs:297/339` 未传）。

---

## 四、风险与遗留

1. **自动化覆盖的边界**：本单只把纯 Core 逻辑沉成 89 条用例；App/Capture/Ocr 无自动化测试，14 条行为项依赖主人真机实测，代理未运行 GUI、未跑 `--smoke`、未抓包。
2. **P-04b 会改 `CaptureOverlayWindow.Capture.cs`**：本报告 P-04 证据行号（:509-621、:228-234、:615-621）在修复后可能漂移，复验时以修复后代码为准，G1 判据必须用 P-04b 最终日志。
3. **P-01 管理器同步等待上限 1000ms**（`Export.cs:38,88`）：贴图线程繁忙时管理器动作可能延迟到 1s 才给「暂未响应」提示，属已登记风险。
4. **P-02 预演/清理无单测**：`PruneLocked/GetPrunePlan` 依赖真实文件系统，lead 已裁定不下沉 Core，靠行为表 B1/B2 覆盖。
5. **P-03 未实测网络**：关闭态「零请求」由调用图静态证明（引擎列表 + 唯一探测点），未抓包；若主人机器上有 Umi 开机自启残留，需看日志而非仅看连接。
6. **P-06 未测回调耗时**：只保证回调内无 IO/锁/业务，未给出「回调 < X ms」的实测数字。
7. **工作树未冻结**：并行改动包含 task-17 采集层修改；本单 build（16.75s、0/0）与 89/89 是在含他人改动的快照上取得，不代表最终 SHA。
8. **`-t:Compile` 假通过**：已确认不能作为门禁，本单及后续一律整包 build。

---

## 五、待确认

1. **A 批次放行条件**：是否接受「P-04b 修复 + 复验 + lead 同 SHA 四门禁通过」即放行，还是要求行为表 14 条全部实测通过？若前者，请明确 G1 以 P-04b 报告为验收凭据。
2. **G1 判据关键字（已定稿，lead m00602）**：F1 取消序列 = `Capture: already capturing, cancel session=<...>` → `Capture: session <...> finished reason=cancel` → 下一次 F1 正常出图；Esc 取消同样落 `finished reason=cancel`；见 2.4 G1 行。
3. **行为表归属**：本表是否需要在 B 批次/发版前并入正式验收文档？本单写域不含 `docs/**`，如需并入请另派或由 lead 处理。
4. **`ScreenCapturer.cs:52-55` BitBlt 返回值未检查**（采集层复核发现，既有行为）：是否另立单，还是记入 B 批次。
5. **主人实测排期**：B1/B2/C1/C2/D1/D2/E1/E2/E3/F1 共 10 条待实测，G1 待 P-04b；是否随 A 批次收尾包一并交主人执行，由 lead 决定。

---

## 附录：复现命令

```powershell
# 1) Core 回归（89 条）
dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo

# 2) 整包 build（走共享锁，避免并发争 bin）
pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p12 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p12/"

# 3) 格式
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore

# 4) 新增用例定位
rg -n "Load_HistoryPerCategory_IsClampedToAllowedRange|Load_UmiOcrPort_IsClampedToAllowedRange|Load_MissingFile_KeepsOcrDefaultsOffAndPort1224|OcrAndHistoryFields_KeepDocumentedJsonNamesAndDefaults" src/PrtScr.Core.Tests/ConfigServiceTests.cs
```

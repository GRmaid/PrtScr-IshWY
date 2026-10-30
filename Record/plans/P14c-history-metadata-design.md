# P-14c 历史元数据：采集来源 / 引擎 / 降级原因（设计稿）

> 基线：HEAD 69616cc（v1.3.1 release-prep），取样时源码树干净；行号按 2026-10-30 22:1x 工作树（含 P-14b/P-13b 已落地改动）核对。
> 性质：设计稿，不含实现；写域仅 Record/plans/。
> 编号口径：整改总纲 P-14c（本文）；与 lead 派单 task-28 对应。总纲里 B-3 系列是审计/审计修复，本单是其后的历史可追溯设计。

## 0. 一句话结论

在现有 version / mode / processName / windowTitle / capturedAt 五字段上增加 5 个可选字段——source / engine / fallbackReason / printWindowTimedOut / sessionId，元数据版本 1 → 2；四个历史写入点（确认、另存、贴图裁剪、全屏快照）各自在调用点用本地结果构造，不读 CaptureService 单槽；旧 JSON 按 mode 推导默认值、不回填、不迁移配置；本阶段只落盘，不在历史列表露出。

---

## 1. 现状

### 1.1 元数据 JSON 现有字段（逐项）

| JSON 键 | C# 属性 / 类型 | 写入点（文件:行） | 读取点（文件:行） | 缺失或非法时行为 |
| --- | --- | --- | --- | --- |
| version | HistoryMetadata.Version / int | src/PrtScr.App/Services/CaptureHistoryService.cs:320（Version = MetadataVersion），常量在 :14 | CaptureHistoryService.cs:369 反序列化；**不校验版本号** | 不参与读取判定 |
| mode | Mode / string | CaptureHistoryService.cs:321（mode.ToString()） | CaptureHistoryService.cs:342-345（Enum.TryParse，失败或 Legacy → Legacy） | 归入「早期未分类」 |
| processName | ProcessName / string? | CaptureHistoryService.cs:322（NormalizeProcessName，:396 起） | CaptureHistoryService.cs:346 | 空串，显示「未知进程」 |
| windowTitle | WindowTitle / string? | CaptureHistoryService.cs:323（Trim() ?? ""） | CaptureHistoryService.cs:347 | 空串 |
| capturedAt | CapturedAt / DateTime | CaptureHistoryService.cs:324（DateTime.Now） | 读取类保留（:457-458），但 HistoryItem.CreatedAt 实际取文件时间 :352 | 不参与分类，仅记录 |

补充事实：

- 序列化选项：CaptureHistoryService.cs:18-22，WriteIndented=false，DefaultIgnoreCondition = WhenWritingNull——null 字段不会写盘，天然向后兼容。
- 写文件路径：CaptureHistoryService.cs:378（PNG 路径 + .json）；先写 .tmp 再 File.Move(overwrite)，见 :326-329。
- 元数据写失败只记日志、PNG 保留：CaptureHistoryService.cs:98-106；读取端把无元数据的 PNG 归为 Legacy：:337-348。
- 历史分组键：HistoryItem.CategoryKey → src/PrtScr.Core/HistoryItem.cs:37-39（Window 按进程 window:{DisplayProcess}，其余 mode:{Mode}）；配额/清理的分组复用该键，见 CaptureHistoryService.cs:260-292（PruneLocked）与分类标题 :307-313。
- 模式枚举只有四档：HistoryCaptureMode（src/PrtScr.Core/HistoryItem.cs:6-12：Window / Normal / Legacy / PinCrop）。

### 1.2 现有字段表达不了什么

1. **窗口图的引擎不可知**：mode=Window 无法区分 PrintWindow 还是 WGC。两者都在 src/PrtScr.Capture/WindowCapture.cs 的 WindowCaptureSource 里，但历史只收到 HistoryCaptureMode。
2. **常规图混了三种来源**：mode=Normal 同时覆盖
   - 常规模式直接裁冻结帧（CaptureOrigin.Normal，src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:304-311）；
   - 窗口模式自由拖拽、没命中窗口（同为 Normal，:393-401）；
   - 窗口捕获失败后**降级为屏幕裁切**（CaptureOrigin.Screen，:403-407）。
   Core 规则明确把 Screen 映射成 Normal：src/PrtScr.Core/CaptureSessionState.cs:223-224（ToHistoryMode），:227（IsDowngrade(origin) => origin == Screen）。
3. **降级原因只活在当次 toast/日志**：降级 Notice 在 CaptureOverlayWindow.Capture.cs:403-407 生成，确认路径 :624-626 打日志、:658-666 弹 toast；历史 .json 没有。
4. **PrintWindow 超时改走 WGC 不可见**：成功图仍 mode=Window，超时标记 PrintWindowTimedOut 只进本次 SelectionCapture（:31-39）与会话日志。
5. **引擎维度已在 Core 建好、但没进历史**：CaptureEngine（CaptureSessionState.cs:73-80：None/Wgc/PrintWindow/Dxgi/BitBlt/Fallback）只用于 P-14b 的会话结果行（CaptureService.cs:297-303 FormatResultLog）。

### 1.3 历史写入点现状（全仓库 4 处 AddAsync）

| # | 路径 | 调用点（文件:行） | 现在传什么 | 调用点本地已有什么 |
| --- | --- | --- | --- | --- |
| 1 | 确认保存 | src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:638-639 | bytes, selection.Mode, selection.ProcessName, selection.WindowTitle | SelectionCapture 含 Origin / Engine / PrintWindowTimedOut / Notice（:31-39） |
| 2 | 另存为 | CaptureOverlayWindow.Capture.cs:773-774 | 同上 | 同一个 selection |
| 3 | 贴图裁剪 | src/PrtScr.App/Views/NativePinWindow.Interaction.cs:264-265 | bytes, HistoryCaptureMode.PinCrop（仅 mode） | 无采集来源（裁剪自贴图，不是屏幕/窗口采集），线程池里执行（:233、:237） |
| 4 | 一键全屏快照 | src/PrtScr.App/MainWindow.Capture.cs:66-67 | result.Png, HistoryCaptureMode.Normal | QuickCaptureResult 只有 Png/LikelyBlack（CaptureService.cs:597），**没有引擎**；CaptureMonitorBestAsync 只回 (Bitmap?, bool Black)（:433） |

OCR 路径**不写历史**：CaptureOverlayWindow.Capture.cs:466-520（:472 开始取图、:494-495 记会话、:506 记来源日志、:507 Dismiss(ocr)、:508 交给识别窗），因此本设计不需要改 OCR。

### 1.4 会话单槽与可推导规则

- CaptureService 保存会话元数据：src/PrtScr.App/Services/CaptureService.cs:27-35（_sessionOrigin / _sessionEngine / _sessionFailure / _sessionPrintWindowTimedOut / _sessionDetail）。
- 写入：MarkCaptureAttemptSucceeded（CaptureService.cs:200-228）、MarkCaptureAttemptFailed（:231-248）；构造 CaptureResult 的私有方法 BuildResultLocked（:285-295）。
- 公开读取只有状态与会话号：CurrentCaptureSessionState（:44-51）、CurrentCaptureSessionId（:54-61）、IsCurrentCaptureSession（:64-71）、InvalidateCaptureSession（:74-90）——**没有 origin/engine/timedOut/detail 的公开 getter**。
- 收尾漏斗 EndCaptureSession（:98-152）在 :135 立刻把槽位释放回 Idle；确认路径在 :621 已先 FinishCaptureSession(confirm-success)。所以**提交点不能靠读单槽拿元数据**（时机已过、API 也没有）。
- 可推导规则已经存在且语义稳定：CaptureRules.ToHistoryMode（CaptureSessionState.cs:223-224）、IsDowngrade（:227）、ResolveScreenOrigin（:232-233）；CaptureResult.HistoryMode 也复用它（src/PrtScr.Core/CaptureResult.cs:29）。

**结论**：历史元数据应从提交点手里的 SelectionCapture（或全屏采集结果）直接传下去，不新增对单槽的依赖。

---

## 2. 目标字段草案

### 2.1 新字段总表（元数据 version 2）

| JSON 键 | 类型 | 可空 | 取值域 | 写入点（实现时） | 备注 |
| --- | --- | --- | --- | --- | --- |
| source | string | 是 | "window" / "normal" / "screen"；非采集来源为 null | CaptureHistoryService.WriteMetadataAsync（CaptureHistoryService.cs:315-330 扩展）+ 各提交点构造 HistoryCaptureMeta | 必须与 CaptureRules.ToHistoryMode / IsDowngrade 同构，见 §2.2 |
| engine | string | 是 | "wgc" / "printwindow" / "dxgi" / "bitblt" / "fallback"；未知为 null | 同上；源头是 CaptureEngine（CaptureSessionState.cs:73-80） | 见 §2.3 |
| fallbackReason | string | 是 | 自由文本，建议 ≤200 字符；仅 source="screen" 或 source="window" && printWindowTimedOut 时有值 | 同上；屏幕降级取 SelectionCapture.Notice 内原因，超时取固定 token | 见 §2.4 |
| printWindowTimedOut | bool | 否（缺省 false） | true / false | 同上；源头 SelectionCapture.PrintWindowTimedOut（CaptureOverlayWindow.Capture.cs:31-39） | 全屏/贴图恒 false |
| sessionId | string | 是 | GUID N 格式（32 位十六进制，无连字符）；本阶段默认 null 不写盘 | 同上；若拍板开启，取确认路径的 session 参数 | 见 §5-1 |

不变字段：version（1→2）、mode、processName、windowTitle、capturedAt；**CategoryKey 与分类/配额逻辑零改动**（只依赖 mode/processName，HistoryItem.cs:37-39）。

### 2.2 source 映射（覆盖 Core 判定）

| CaptureOrigin | source | HistoryCaptureMode（既有规则） | IsDowngrade |
| --- | --- | --- | --- |
| Window（CaptureSessionState.cs:57-64） | "window" | Window | false |
| Normal | "normal" | Normal | false |
| Screen | "screen" | Normal（:223-224） | true（:227） |
| None | null | 不适用（非采集：贴图裁剪） | — |

不变量（实现后应可断言）：

1. source == "window" ⇔ mode == Window；
2. source == "screen" ⇒ mode == Normal 且可视为降级；
3. mode == Normal 可能是 normal 或 screen，**不能只凭 mode 反推**——这正是新增字段的意义；
4. 贴图裁剪 mode=PinCrop、source=null（不扩域，见 §5-6）。

### 2.3 engine 映射

| CaptureEngine | engine 字符串 |
| --- | --- |
| Wgc | "wgc" |
| PrintWindow | "printwindow" |
| Dxgi | "dxgi" |
| BitBlt | "bitblt" |
| Fallback | "fallback" |
| None | null |

已知映射来源：窗口截图 WindowCaptureSource.PrintWindow/Wgc → MapEngine（CaptureOverlayWindow.Capture.cs:417-425）；常规/降级路径固定 CaptureEngine.Fallback（:309-311、:398-407）。全屏快照本阶段没有引擎信息（§1.3），写 null。

### 2.4 fallbackReason 提取规则

- 屏幕降级（source="screen"）：SelectionCapture.Notice 形如 "窗口捕获失败，已按常规截图保存（{reason}）"（CaptureOverlayWindow.Capture.cs:404-407）。落盘只存 {reason}（剥掉固定前后缀），Trim 后截断到 200 字符；reason 来自 WindowCaptureResult.Detail 或失败枚举文案（:349-350、:381-382）。
- PrintWindow 超时但 WGC 成功（source="window" && printWindowTimedOut）：Notice 是面向用户的 "窗口响应较慢，已改用 WGC 抓取窗口内容"（:414），随文案会漂移；落盘用**固定 token** "printwindow-timeout"，字段里不存中文句子。
- 其余情况 null（不写盘，WhenWritingNull）。

### 2.5 各提交点填写矩阵

| 提交点 | source | engine | fallbackReason | printWindowTimedOut | sessionId |
| --- | --- | --- | --- | --- | --- |
| 确认（窗口图） | window | wgc 或 printwindow | 超时则 printwindow-timeout，否则 null | 取自 selection | null（本阶段） |
| 确认（屏幕降级） | screen | fallback | 提取后的 reason | false | null |
| 确认（常规/无窗口目标） | normal | fallback | null | false | null |
| 另存（任意来源） | 同本次 selection | 同本次 selection | 同本次 selection | 同本次 selection | null |
| 贴图裁剪 | null | null | null | false | null |
| 一键全屏快照 | normal | null（本阶段） | null | false | null |
| OCR | 不写历史 | — | — | — | — |

### 2.6 C# 类型草案（只给签名，不实现）

    // PrtScr.Core/CaptureSessionState.cs（或同文件 CaptureRules 内）：纯映射，便于 Core.Tests 覆盖
    public static class CaptureHistoryRules
    {
        public static string? ToHistorySource(CaptureOrigin origin);        // Window/Normal/Screen/None -> window/normal/screen/null
        public static string? ToHistoryEngine(CaptureEngine engine);        // Wgc/.../None -> wgc/.../null
        public static string? ExtractFallbackReason(string? notice, bool printWindowTimedOut); // 剥壳 + 截断；超时 -> "printwindow-timeout"
    }

    // PrtScr.App/Services/HistoryCaptureMeta.cs：落盘 DTO（不可变，跨线程安全）
    public sealed record HistoryCaptureMeta(
        string? Source,
        string? Engine,
        string? FallbackReason,
        bool PrintWindowTimedOut,
        string? SessionId)
    {
        public static HistoryCaptureMeta FromSelection(SelectionCapture selection); // 或拆参数：origin/engine/notice/timedOut
        public static readonly HistoryCaptureMeta ScreenQuick = new("normal", null, null, false, null);
    }

CaptureHistoryService.HistoryMetadata（CaptureHistoryService.cs:443-459）新增 5 个属性，全部加 [JsonPropertyName]；MetadataVersion 改为 2（:14）。

---

## 3. 兼容性

### 3.1 旧 JSON 缺字段的读取默认值

旧文件（version=1 或无该键）读取时按下表补齐，**绝不因缺字段判定为降级**：

| 字段 | 缺失时默认 | 推导规则 |
| --- | --- | --- |
| source | 由 mode 推导 | Window → "window"；Normal → "normal"；PinCrop/Legacy → null（不回填文件） |
| engine | null | 旧文件无法反推引擎 |
| fallbackReason | null | 旧文件不可知 |
| printWindowTimedOut | false | 缺省非超时 |
| sessionId | null | 不落盘 |

注意：现状 TryReadMetadata（CaptureHistoryService.cs:361-376）只做反序列化，不校验版本；ToItem（:332-359）也不读新字段。默认值规则在实现时放进“读元数据 → 组装内部对象”的辅助方法里，**不改 ToItem 现有行为**（本阶段历史列表不展示）。

### 3.2 version 语义

- 写端永远写 version=2；读端接受任意可反序列化版本（现状无校验，保持）。
- 旧版本程序读 v2 文件：System.Text.Json 默认忽略未知属性，不会抛错；新字段丢失但不如损坏。
- 未来 v3 时沿用同一策略：只加可选字段，不删旧字段。

### 3.3 配置迁移

不需要。历史元数据是**每文件 JSON**，与 config.json 无关（CaptureHistoryService 只读 App.Config.Current.SaveHistory，CaptureHistoryService.cs:76），不涉及配置版本/字段迁移。

### 3.4 历史列表 UI 是否显示

本阶段**只落盘、不显示**，理由：

1. 历史窗分组/分类/配额都建立在 CategoryKey（HistoryItem.cs:37-39）上，展示降级标记需要改 HistoryItem、HistoryWindow 的分组与筛选语义，属视觉/交互批次（P-14d），与 B 批次“先把数据写对”目标不同；
2. 先把字段积累一轮，验证取值域是否够用（尤其是降级原因文本长度与引擎分布），再定 UI 形态，避免字段设计被 UI 反向绑死；
3. 避免历史列表出现“多数旧图没有标记、只有新图有”的割裂感。

### 3.5 旧 JSON 是否回填

不回填。读取按 §3.1 推导即可；批量改写用户历史文件（可能上千个）风险大于收益，且会触发文件时间/清理逻辑的误判。

---

## 4. 写入点改造清单

### 4.1 API 变更（最小）

1. CaptureHistoryService.AddAsync（CaptureHistoryService.cs:70-74）增加**可选**参数 HistoryCaptureMeta? meta = null，保持现有 4 参调用兼容。
2. WriteMetadataAsync（:315-330）透传 meta，映射到 HistoryMetadata 的 5 个新属性。
3. HistoryMetadata（:443-459）加 5 属性；MetadataVersion :14 改 2。
4. 映射/提取纯函数放 Core（CaptureRules 同文件或新建 CaptureHistoryRules），便于单测；meta DTO 留 App。

### 4.2 四个提交点逐条（现在传什么 → 改为传什么）

| # | 提交点（文件:行） | 现在 | 改为 | 行数增量 | 读取时机说明 |
| --- | --- | --- | --- | --- | --- |
| 1 | CaptureOverlayWindow.Capture.cs:638-639（确认） | AddAsync(bytes, selection.Mode, selection.ProcessName, selection.WindowTitle) | 追加 HistoryCaptureMeta.FromSelection(selection) | +2~4 | selection 在 :558 的 await 返回后一直可用；**不要**改成读 CaptureService 单槽（:621 已 FinishCaptureSession，槽位在 CaptureService.cs:135 释放） |
| 2 | CaptureOverlayWindow.Capture.cs:773-774（另存） | 同上 | 同上（同一个 selection） | +2~4 | 另存路径 :718 起已 BeginCaptureAttempt，selection 本地在 :721 起可用 |
| 3 | NativePinWindow.Interaction.cs:264-265（贴图裁剪） | AddAsync(bytes, HistoryCaptureMode.PinCrop) | 传 meta: null 即可（文件仍写 version=2，source 缺省） | 0~2 | 线程池路径只做编码，无采集来源；不要伪造 source |
| 4 | MainWindow.Capture.cs:66-67（全屏快照） | AddAsync(result.Png, HistoryCaptureMode.Normal) | 追加 HistoryCaptureMeta.ScreenQuick（source=normal，engine=null） | +1~3 | 若要把引擎也落盘，需扩展 QuickCaptureResult（CaptureService.cs:597）与 CaptureMonitorBestAsync（:433），见 §5-6，另派小单 |

OCR 不写历史（§1.3），无改动。

### 4.3 读取路径（本阶段不改 HistoryItem）

- 本阶段：只写盘；HistoryItem 不加字段，ToItem（CaptureHistoryService.cs:332-359）保持原样。
- 若 P-14d 要展示：给 HistoryItem 加 5 个可选属性（HistoryItem.cs:15-23），在 ToItem 读 metadata 时按 §3.1 补默认并赋值（约 +10~15 行）；CategoryKey 不动。

### 4.4 并发与一致性

- HistoryCaptureMeta 为不可变 record，提交点构造后传值，不存在跨线程共享可变状态。
- 元数据写入仍与 PNG 写入分离：CaptureHistoryService.cs:98-106 的 try/catch 使元数据失败只丢字段、不丢图。
- 新增字段不参与 PruneLocked（:260-292）与 CategoryKey（HistoryItem.cs:37-39），配额/清理语义零变化。
- 加字段后 AddAsync 内部 :111-115 的“写入后读回 ToItem 再修剪”流程不变；ToItem 不读新字段，行为一致。

### 4.5 若本阶段实现，改动面清单

| 文件 | 改动 | 预估行数 |
| --- | --- | --- |
| src/PrtScr.Core/CaptureSessionState.cs | 新增 CaptureHistoryRules（source/engine 映射 + fallbackReason 提取），或扩展 CaptureRules | +30~45 |
| src/PrtScr.App/Services/HistoryCaptureMeta.cs（新文件，CRLF） | record + FromSelection/ScreenQuick | +25~35 |
| src/PrtScr.App/Services/CaptureHistoryService.cs | HistoryMetadata 5 属性、MetadataVersion=2、AddAsync/WriteMetadataAsync 参数与映射、读取默认值辅助 | +45~70 |
| src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs | 确认/另存 2 处调用点追加 meta | +4~8 |
| src/PrtScr.App/MainWindow.Capture.cs | 全屏快照 1 处调用点追加 meta | +1~3 |
| src/PrtScr.App/Views/NativePinWindow.Interaction.cs | 显式传 meta: null（可省） | 0~2 |
| src/PrtScr.Core.Tests/（新 CaptureHistoryRulesTests.cs） | 映射（Window/Normal/Screen/None）、超时 token、剥壳与截断、旧 JSON 默认值 | +50~70 |
| **合计** | 不含扩展全屏引擎 | **约 155~230 行**（含测试） |

无新依赖；不改 .gitignore/README/docs；不改配置字段；不 commit（实现单再走门禁）。

---

## 5. 未决问题与推荐答案（≤6）

1. **sessionId 是否入库？**
   推荐**本阶段不入**（写 null、不落盘）。日志已经带会话号（CaptureService.cs:297-303 的 result 行、:146 finished 行），入库只增加体积与可移植性负担；排障需要时可从同时间段日志关联。若 lead 要求入库，字段已预留，加一行赋值即可。
2. **fallbackReason 存 Notice 原文还是提取后的原因？**
   推荐**提取后的纯原因**（剥掉“窗口捕获失败，已按常规截图保存（…）”外壳，截断 200 字符）；PrintWindow 超时用固定 token "printwindow-timeout"。原文是面向用户文案，会随 UI 改动漂移，不适合做数据字段。
3. **旧 JSON 是否回填？**
   推荐**不回填**；读取按 mode 推导（§3.1），旧图显示为未知引擎/非降级，符合事实。
4. **历史列表要不要露出降级标记？**
   推荐**本阶段不露出**，P-14d 视觉批次再做；理由见 §3.4。
5. **AddAsync 用参数还是 record？**
   推荐**可选参数 HistoryCaptureMeta? meta = null**（单参数），不把 5 个字段摊平成 9 个参数；映射纯函数放 Core 以便测试（§2.6）。
6. **全屏快照的引擎是否本阶段采集？**
   推荐**本阶段 source=normal、engine=null**；若要 engine，需把 CaptureMonitorBestAsync（CaptureService.cs:433）的实际引擎透传到 QuickCaptureResult（:597），约 +20~40 行，建议另立小单，避免本单扩写域。附：贴图裁剪是否新增 source="pin"？推荐**不扩域**，保持 null；mode=PinCrop 已足够表达来源。

---

## 附录 A：证据索引（文件:行）

- src/PrtScr.App/Services/CaptureHistoryService.cs
  - :14 MetadataVersion；:18-22 JSON 选项；:70-74 AddAsync 签名；:96 写 PNG；:98-106 元数据失败容错；:111-115 写入后修剪；:260-292 PruneLocked 分组；:307-313 CategoryTitle；:315-330 WriteMetadataAsync；:332-359 ToItem；:361-376 TryReadMetadata；:378 MetadataPath；:443-459 HistoryMetadata。
- src/PrtScr.Core/HistoryItem.cs：:6-12 HistoryCaptureMode；:15-23 record 字段；:29-35 Source 标签；:37-39 CategoryKey。
- src/PrtScr.Core/CaptureSessionState.cs：:57-64 CaptureOrigin；:73-80 CaptureEngine；:223-224 ToHistoryMode；:227 IsDowngrade；:232-233 ResolveScreenOrigin。
- src/PrtScr.Core/CaptureResult.cs：:10-17 record；:29 HistoryMode；:32 Notice。
- src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs：:31-39 SelectionCapture；:301-408 CaptureSelectionAsync；:403-407 屏幕降级 Notice；:414 超时 Notice；:417-425 MapEngine；:466-520 OCR（不写历史）；:557-682 ConfirmAsync（:638-639 AddAsync）；:711-814 SaveSelectionAsync（:773-774 AddAsync）。
- src/PrtScr.App/MainWindow.Capture.cs：:66-67 全屏快照 AddAsync。
- src/PrtScr.App/Views/NativePinWindow.Interaction.cs：:233 线程池；:237 ProcessCrop；:264-265 PinCrop AddAsync。
- src/PrtScr.App/Services/CaptureService.cs：:27-35 会话字段；:44-51/:54-61/:64-71/:74-90 公开 API；:98-152 EndCaptureSession（:135 槽位回 Idle）；:200-228 MarkCaptureAttemptSucceeded；:231-248 MarkCaptureAttemptFailed；:285-295 BuildResultLocked；:297-303 FormatResultLog；:433 CaptureMonitorBestAsync；:597 QuickCaptureResult；:599 CaptureFullScreenPngAsync。

## 附录 B：实现阶段的验收建议（供派单用）

1. **Core 单测**：source 映射 4 个分支（Window/Normal/Screen/None）、engine 映射 6 个分支（含 None→null）、ExtractFallbackReason 剥壳/截断/超时 token；纯函数无 IO。
2. **写盘后读回**：临时目录构造四个提交点的 meta，断言 JSON 键名与值、null 不落盘、version=2；旧 JSON（无新字段）读取默认值符合 §3.1。
3. **回归**：CategoryKey、配额清理、历史列表行为与现有 206+ 条测试不变（ToItem 未改）。
4. **人工**：截一张窗口图 / 一张屏幕降级图 / 一次贴图裁剪 / 一次全屏快照，检查 data/history/*.json 的 source/engine/fallbackReason 是否符合 §2.5；旧图 JSON 不被改写。
5. 门禁沿用整包 build 0 警 0 错、Core.Tests 全过、全量 dotnet format whitespace rc=0（禁 -t:Compile、禁 --smoke、不 commit）。

# C-1 视觉整改 P0 实施方案

> **勘误指针（lead，2026-10-30 22:5x）**：本稿 P0-2a / P0-2b（AnnotationWindow 硬编码深色）已作废——该窗已于 task-37 整体删除；离屏窗 Mica 风险条一并作废。现行裁决见 `Record/plans/C2-visual-decisions.md`：Mica 不引入、HUD #E6→#F2 与青色统一记为视觉债 V-1、危险按钮只补 4 个 AutomationProperties.Name、历史副标题改单行省略。

- 基线：HEAD 69616cc（chore(release-prep): 版本号 1.3.1 与 README 更新）；调研日 2026-10-30。
- 范围：src/PrtScr.App 下全部 XAML 与相关 .cs；本单只读，未改任何源码。
- 证据规则：按 task-32 要求，只引用源码 文件:行 与 grep/构建结果，不引用实机截图。输入文档 `大佬修改材料/PrtScr-IshWY-实机UI建议-校正版.md`（173 行）仅作为「问题来源」，其描述不作为现状证据。
- 结论先行：建议文档严重滞后。P0 六项中 **4 项已实现、1 项半实现、1 项仅剩文案收敛**；P1 多数条目也已实现。C-1 的真实工作量是「把已实现项固化成验收判据 + 补两处残留 + 补危险按钮的可访问性名字」，不是照文档重做一遍。

## 0. 建议文档 vs 当前代码（偏差表）

| 建议文档条目 | 文档描述的问题 | 当前代码事实（文件:行） | 状态 |
| --- | --- | --- | --- |
| P0-1 四窗标题栏 | 标题栏白色与深色内容区割裂 | `src/PrtScr.App/Services/WindowAppearance.cs:9-13` ApplyTitleBar 挂 ActualThemeChanged；`:15-27` 设 AppWindow.TitleBar.PreferredTheme；调用点 MainWindow.xaml.cs:57、HistoryWindow.xaml.cs:49、OcrResultWindow.xaml.cs:68、PinManagerWindow.cs:24 | 已实现 |
| P0-2 根背景 | 根 Grid 无背景，浅色主题深字落黑底 | MainWindow.xaml:8-10 RootGrid Background=SolidBackgroundFillColorBaseBrush；HistoryWindow.xaml:8、PinManagerWindow.xaml:8、OcrResultWindow.xaml:7 同；grep MicaBackdrop=0 | 半实现（无 Mica，是有意纯色） |
| P0-3 危险色 | 删除/清空未用危险色 | MainWindow.xaml:77-79 DangerButtonStyle=SystemFillColorCriticalBrush；HistoryWindow.xaml:10-12 定义、:261-266 分隔线、:267-278 两个危险按钮；PinManagerWindow.xaml:10-12、:39-48；使用点 MainWindow.xaml:1154 | 已实现 |
| P0-4 裁剪 HUD 重复提示 | PinCropWindow.xaml:61 与 :64 各一条退出提示 | PinCropWindow.xaml:64-65 仅一条 HudHintText；PinCropWindow.cs:346 设置同一句、:408 选中后改为 Enter/O/Ctrl+S；CenterHintPlate 在 :33-38 | 已实现 |
| P0-5 快捷键页串台 | HotkeyStatusText 显示「全屏截图已复制…」等全局状态 | HotkeyStatusBar 为 InfoBar（MainWindow.xaml:651-655）；SetHotkeyStatus 仅 MainWindow.xaml.cs:286-291；17 个调用点全部在 MainWindow.Hotkeys.cs（:189/:206/:222/:228/:234/:240/:356/:485/:490/:533/:541/:557/:567/:576/:585/:595/:604/:613），均属录制/恢复默认反馈 | 已实现（原 HotkeyStatusText 已不存在） |
| P0-6 历史标题区断行 | 副标题断成「…· 共」+「88 张 / 76.8 MB」，StatusText MaxWidth=420 贴第二行 | StatusText 已移到底部右侧（HistoryWindow.xaml:215-222，MaxWidth=360，Opacity 0.65）；SubtitleText 在 :26-31（12pt、Opacity 0.6），文案为静态两段拼接（HistoryWindow.xaml.cs:280-281），统计数字不再拼进副标题 | 布局已改，仅剩副标题在窄窗可能折行 |
| P1 下拉框居中 | CaptureProfileCombo/DefaultModeCombo Stretch 导致居中 | MainWindow.xaml:828-832 与 :842-846 已是 MinWidth 260 / MaxWidth 420 / HorizontalAlignment Left | 已实现 |
| P1 导航阈值 | 800/宽侧栏 | MainWindow.xaml:95-103 ExpandedModeThresholdWidth=720、OpenPaneLength=168 | 已实现 |
| P1 自定义背景 | 缺百分比/文件名/预览，无图时滑块可用 | MainWindow.xaml:1187-1195 预览 120x68、:1197 BackgroundFileNameText、:1198 BackgroundOpacityText（不透明度 xx%）；MainWindow.Appearance.cs:85-86 无图时禁用滑块并给 BackgroundDisabledHintText | 已实现 |
| P1 贴图管理双层框 | 卡片容器再套背景 | PinManagerWindow.xaml:59-65 ItemContainerStyle Background=Transparent；卡片 :74-144 自带 CardBackgroundFillColorDefaultBrush + CardStrokeColorDefaultBrush | 已实现 |
| P1 右键菜单状态 | 不显示 ✓ / 已锁定 | PinMenuWindow.cs:233-236 不透明度 check、:246 锁定 check=_locked、:247 穿透 check=_clickThrough | 已实现 |
| P1 文字贴图透底 | 文字贴图背景半透明 | src/PrtScr.App/Services/PinTextCardRenderer.cs:70 默认 FromArgb(0xFF,0x1F,0x1F,0x1F)、:86 fill 用 FromArgb(255,…) | 已实现 |
| P1 提示编辑器 | 不能拖动、缺主按钮 | ToastPlacementWindow.cs:93 说明「拖动提示框调整位置」、:136 保存位置按钮（主按钮）、:20 注释 | 已实现 |
| P1 OCR 状态行 | 缺状态行/测试反馈 | MainWindow.xaml:1239-1246 UmiTestRing(ProgressRing)、:1248-1254 OcrStatusBar(InfoBar) | 已实现 |
| P1 OCR 标题与复制状态 | 复制状态覆盖标题 | OcrResultWindow.xaml:14-23 只有 StatusText+EngineText；OcrResultWindow.xaml.cs:171-176 AnnounceStatus 直接写 StatusText；:213/:231/:233/:235 复制反馈全部复用 | **未实现**（唯一确定的 P1 缺口之一） |
| P1 次级文字画刷 | Opacity 压暗 | HintTextStyle 已用 TextFillColorSecondaryBrush（MainWindow.xaml:30-33），但仍有 27 处 inline Opacity（见 §3.1 A-1） | 半实现 |
| P1 HUD 底色/中心底板 | 裁剪 HUD #E6、中心提示无底板 | PinCropWindow.xaml:56-67 HudBar=#F2202020、:33-38 CenterHintPlate=#8C000000；取景器 HUD 仍 #E6202020（CaptureOverlayWindow.xaml:191，另外 :76/:145/:177/:51） | 半实现 |
| P2 主页卡片高度 | MinHeight 112→88 | MainWindow.xaml:12-19 ActionCardButtonStyle MinHeight=88 | 已实现 |
| P2 按键一览重复 | 「按键一览」与「完整快捷键目录」重复 | MainWindow.xaml:660-743（六张卡片）vs :745-800（10 行目录表） | **未实现** |
| P2 HUD 青色标签 | 裁剪 HUD 青色标签需统一 | 取景器青色 #FF8FE3FF 用于 ProcessText(:61)、Tab(:206)、HudModeText(:212)、HoverBorder #FF00C8FF(:30)；PinCrop 全白 | **未实现**（需统一或明确保留两种语义） |

**偏差根因**：提交 319103e（Polish UI theme, feedback, and window consistency）与 5ffd9bd（Merge community UI refresh from Aevrilen）已做过一轮主题/一致性整改；建议文档基于更早的 22 张截图，故多数条目已落地。

## 1. P0 六项逐条（现状 → 目标 → 改动清单 → 风险）

### 1.1 P0-1 四个主窗标题栏跟随主题（状态：已实现，仅需固化验收）

- 现状：`src/PrtScr.App/Services/WindowAppearance.cs:9-13` 在 ApplyTitleBar 里先 Update(ActualTheme) 再订阅 ActualThemeChanged；`:15-27` 设置 `window.AppWindow.TitleBar.PreferredTheme = Light|Dark`，异常被 catch 且写日志 `WindowAppearance: 标题栏主题失败`。调用点四处：MainWindow.xaml.cs:57、Views/HistoryWindow.xaml.cs:49、Views/OcrResultWindow.xaml.cs:68、Views/PinManagerWindow.cs:24。无系统标题栏的窗口（CaptureOverlayWindow.xaml.cs:161、PinCropWindow.cs:149、AnnotationWindow.xaml.cs:81、ToastWindow.cs:57、ToastPlacementWindow.cs:66、PinMenuWindow.cs:121 均 SetBorderAndTitleBar(false,false)）不适用。
- 目标：维持现有实现；把「四窗都调用 ApplyTitleBar」变成可 grep 的验收项；若主人在 Win10 19041 上仍看到浅色 caption，再评估回退（见 §5 风险 4）。
- 改动文件清单：无（验证单 C1-A）。
- 风险：PreferredTheme 只作用于系统标题栏的 caption 按钮/文字配色，不改变标题栏底色材质；Win10 与 Win11 的标题栏外观不同（Win10 无 Mica）。高对比度主题下系统可能忽略 PreferredTheme，需要主人实测确认「不做自定义标题栏颜色」是可接受的。

### 1.2 P0-2 根背景与深色策略（状态：半实现）

- 现状：主窗 RootGrid 已是 `{ThemeResource SolidBackgroundFillColorBaseBrush}`（MainWindow.xaml:8-10）；历史/贴图管理/OCR 结果窗同样使用该主题画刷（HistoryWindow.xaml:8、PinManagerWindow.xaml:8、OcrResultWindow.xaml:7）；自定义背景图叠加层 MainWindow.xaml:86-89（Opacity 0.25，运行时由 MainWindow.Appearance.cs 控制）。全仓无 MicaBackdrop/SystemBackdrop 用于主窗（grep 结果：仅 PinMenuWindow.cs:137 菜单用 DesktopAcrylicBackdrop、ToastWindow.cs:19 注释说明改用屏幕快照 ToastBackdrop）。**硬编码深色的仅有 AnnotationWindow.xaml:7 #FF1E1E1E、:15 #FF262626、:55 #FF111111**（独立标注编辑器，GDI 预览要恒定深底）。取景器 CaptureOverlayWindow.xaml:9 Black、裁剪 PinCropWindow.xaml:6 Black 属功能需要（冻屏/遮罩语义），不算违规。
- 目标：①明确决策：**不引入 Mica**，主窗保留系统主题纯色底（理由见 §5 风险 2）；②把 AnnotationWindow 的「有意固定深色」从隐性变显性：三个颜色抽成局部资源键（AnnotationSurfaceBrush/AnnotationToolbarBrush/AnnotationHostBrush）并加一行注释说明「固定深色：标注预览需恒定对比，浅色系统下也保持深底」；③不为取景/裁剪窗引入主题化（会破坏遮罩与对比语义）。
- 改动文件清单：`src/PrtScr.App/Views/AnnotationWindow.xaml`（资源键 + 3 处引用）；决策记录写入 C 阶段文档（由 lead 决定落在 Record/plans 还是 docs/）。
- 风险：高对比度模式下硬编码深色不跟随系统（可接受，但注释必须写明）；浅色系统下 Annotation 窗与主窗观感割裂（当前已是这样，改资源键不改变观感）；若将来引入 Mica，Win10 19041 会静默失败，须先做 MicaController.IsSupported 判定与纯色回退。

### 1.3 P0-3 危险动作配色与分组（状态：已实现，缺口在可访问性名字）

- 现状：DangerButtonStyle 定义 MainWindow.xaml:77-79（Background=SystemFillColorCriticalBrush，Foreground=TextOnAccentFillColorPrimaryBrush）；使用点 4 处：MainWindow.xaml:1152-1155「清空全部历史…」、HistoryWindow.xaml:270「删除…」、:276「清空分类…」、PinManagerWindow.xaml:48「全部关闭」；两处历史/贴图管理已用 DividerStrokeColorDefaultBrush 分隔线（HistoryWindow.xaml:261-266、PinManagerWindow.xaml:39-44）。**缺口**：这 4 个按钮都没有 AutomationProperties.Name（grep：HistoryWindow.xaml、PinManagerWindow.xaml 全文 0 命中；MainWindow.xaml 的危险按钮 :1151-1155 也没有），讲述人只能读 Content 文本；主窗清空按钮更缺少「不可恢复/先确认」的语义提示。
- 目标：维持配色；四个危险按钮补 AutomationProperties.Name，文案明确后果与确认（例：「永久删除全部截图历史（先确认张数）」）；不改配色、不改分组。
- 改动文件清单：MainWindow.xaml（1 处）、HistoryWindow.xaml（2 处）、PinManagerWindow.xaml（1 处）；纯属性新增。
- 风险：AutomationProperties.Name 会覆盖讲述人对 Content 的朗读，文案必须包含动作与对象，否则可访问性反而变差；高对比度下 SystemFillColorCriticalBrush 会被系统主题覆盖，仍需确保文字用主题前景色而不是硬编码白色（当前 Foreground 是 ThemeResource，安全）。

### 1.4 P0-4 裁剪 HUD 重复退出提示（状态：已实现）

- 现状：PinCropWindow.xaml:56-67 HudBar 内只有一条 `HudHintText`（:64-65），初值「拖拽 框选 · Esc / 右键 退出」；代码 PinCropWindow.cs:346 在无选区时写同一句、:352 隐藏 CenterHintPlate、:408 选中后改为「Enter 贴图 · O 识别 · Ctrl+S 保存」。建议文档说的「:61 与 :64 各一条」在当前文件已不存在（PinCropWindow.xaml 共 69 行，全文仅 1 个 HudHintText）。
- 目标：无代码改动；实测两态文案（无选区/有选区）与 Esc、右键均可退出。
- 改动文件清单：无（验证单 C1-A）。
- 风险：文案切换依赖选区状态机，若 UpdateVisuals 未在某种状态调用会残留旧文案；这是既有行为，仅需实测记录。

### 1.5 P0-5 快捷键页状态隔离（状态：已实现，仅剩「离页折叠」可选）

- 现状：原 HotkeyStatusText 已被 InfoBar `HotkeyStatusBar` 取代（MainWindow.xaml:651-655，IsOpen=False）；写入只在 SetHotkeyStatus（MainWindow.xaml.cs:286-291）；调用点 17 处全部位于 MainWindow.Hotkeys.cs（录制开始 :189、取消 :206、修饰键提示 :222/:228、冲突 :234/:240/:541、取景器键提示 :356、恢复默认 :485/:490/:533、设置成功 :557/:567/:576/:585/:595/:604/:613）。**没有任何调用点写入「全屏截图已复制到剪贴板」等全局运行状态**（建议文档所述串台已修）。
- 目标：维持；可选改进（列为 P1 档 A）：离开快捷键页时把 InfoBar 折叠，避免下次进入看到上次的残留提示（在 MainWindow.xaml.cs 的导航选择变更处加 2 行即可）。
- 改动文件清单：无（验证单 C1-A）；可选改进归 §3.1 A-3。
- 风险：InfoBar 常驻会占行高，若未来把运行状态写进来会再次串台；建议在 SetHotkeyStatus 的 XML 注释里写明「只用于快捷键页反馈」。

### 1.6 P0-6 历史窗标题区（状态：部分实现，仅剩副标题折行）

- 现状：标题区 24-38 由标题(:25)、副标题(:26-31)、操作说明(:32-37) 三行组成；副标题文案在 HistoryWindow.xaml.cs:280-281 由两段字符串拼接（「按截图模式 / 进程分类，每类最多 N 张；」+「仅保存在本机 data\history。」），12pt、Opacity 0.6、默认可换行；统计数字「共 X 张」已不拼进副标题，StatusText 已移到底部右侧（:215-222，MaxWidth=360）。建议文档描述的「MaxWidth=420 的右上 StatusText 贴第二行」在当前代码已不存在。
- 目标：把副标题收敛为单行省略：`TextWrapping=NoWrap`、`TextTrimming=CharacterEllipsis`、`MaxLines=1`，`ToolTipService.ToolTip` 给全文；同时把文案缩短为「每类最多 N 张 · 仅保存在本机」；操作说明 :32-37 保留（它是帮助文本，允许两行）。
- 改动文件清单：`HistoryWindow.xaml:26-31`（3 个属性 + ToolTip）、`HistoryWindow.xaml.cs:280-281`（字符串拼接改为单串）。
- 风险：窄窗 + 150% DPI 下 ToolTip 只能鼠标悬停看到，触摸/键盘用户拿不到全文 → 保留操作说明里的关键信息（本机保存）；TextTrimming 在 TextBlock 上需 MaxLines 同时设置，WinUI 2.5 支持。

## 2. P0 实施顺序与可并行性

### 2.1 拆分（每张 ≤ 半天）

| 单号 | 内容 | 写域 | 估时 | 依赖 | 可并行 |
| --- | --- | --- | --- | --- | --- |
| C1-A | 验证单：P0-1/3/4/5 逐项走查 + 真机确认清单，产出 `Record/reports/C1-p0-verify.md`（不碰源码） | Record/reports | 0.5h | 无 | 可与 B/C/D 并行 |
| C1-B | 决策单：P0-2 固化「不引入 Mica、主窗纯色、Annotation 有意固定深色」并在 C 阶段文档写明理由与回退条件 | Record/plans（或 lead 指定 docs/） | 0.5h | 无 | 可与 A 并行；C/D 需先读它 |
| C1-C | P0-2b：AnnotationWindow.xaml 三色抽资源 + 注释（保持观感不变） | src/PrtScr.App/Views/AnnotationWindow.xaml | 2h | C1-B | 与 C1-D 并行（不同文件） |
| C1-D | P0-6：历史窗副标题单行省略 + 文案缩短（xaml + .cs 各 1 处） | src/PrtScr.App/Views/HistoryWindow.xaml(.cs) | 2h | C1-B | 与 C1-C 并行 |
| C1-E | P0-3 残项：四个危险按钮补 AutomationProperties.Name（含确认语义），并复核危险色分组 | MainWindow.xaml、HistoryWindow.xaml、PinManagerWindow.xaml | 2.5h | C1-B | 与 C1-C 并行；与 C1-D 串行（都要改 HistoryWindow.xaml） |

### 2.2 串行/并行矩阵

| | A | B | C | D | E |
| --- | --- | --- | --- | --- | --- |
| A | — | 可并行 | 可并行 | 可并行 | 可并行 |
| B | 可并行 | — | 先行（C/D/E 先读结论） | 先行 | 先行 |
| C | 可并行 | 依赖 | — | 可并行（不同文件） | 可并行（不同文件） |
| D | 可并行 | 依赖 | 可并行 | — | **必须串行**（同改 HistoryWindow.xaml） |
| E | 可并行 | 依赖 | 可并行（不同文件） | **必须串行**（同改 HistoryWindow.xaml） | — |

- 推荐顺序：**C1-B（决策，0.5h）→ C1-A（验证，可与 B 同时）→ C1-C 与 C1-D 并行 → C1-E**。C1-E 放最后，避免与 C1-D 抢 HistoryWindow.xaml；若人手只够一个队列，则 B → C → D → E → A（A 最后跑一遍统一验收）。
- 每单门禁（C 阶段统一口径）：① `pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag <tag> -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-<tag>/"` 0 错 0 警；② `... -Tag <tag>test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` 通过数 ≥ 206；③ `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` rc=0；④ 禁 `-t:Compile`（不跑 WinUI XAML 代码生成，属假绿——P-01 已定位）；⑤ 禁 `--smoke`（会写主人真实 data/）；⑥ 不 commit，冻结树由 lead 统一提交。
- XAML 改动必须整包构建：XAML 代码生成挂在 PrepareResourcesDependsOn，`-t:Compile` 拿不到 g.cs，只做编译目标会漏掉 XAML 编译错误。

## 3. P1/P2 分档与批次

### 3.1 档 A：改一个常量或一行 XAML（低风险，可 grep 验证）

| 编号 | 内容 | 精确位置 | 验证方式 |
| --- | --- | --- | --- |
| A-1 | 次级文字去 inline Opacity，改 `{ThemeResource TextFillColorSecondaryBrush}`：MainWindow.xaml:175（副标题 0.6）、:357-361（表头 0.55）、:678/:688/:698/:708/:718/:728/:738（键帽 0.7）、:680/:690/:700/:710/:720/:730/:740（描述 0.6）、:766-768（目录表头 0.55）；HistoryWindow.xaml:29/:35/:71/:145/:183/:212/:220；PinManagerWindow.xaml:30/:113/:158/:167；OcrResultWindow.xaml:22 | 同左（27 处；MainWindow.xaml:89 的背景图 Opacity 0.25 是功能值，**不动**；HistoryWindow.xaml:87 的 0.75 属选中态标题，改画刷后需确认视觉） | grep `Opacity="0.` 计数降到只剩功能位；构建 0/0 |
| A-2 | 小节标题上边距 10→16：MainWindow.xaml:25-29 SectionTitleStyle Margin 0,10,0,0 → 0,16,0,0（字号 16 SemiBold 已符合 16–18 要求，不改字号） | MainWindow.xaml:25-29 | grep `SectionTitleStyle` + Margin 值；目视 9 个设置页 |
| A-3 | 离开快捷键页时折叠 InfoBar：MainWindow.xaml.cs 的 NavigationView SelectionChanged（或现有切页方法）加 `HotkeyStatusBar.IsOpen=false;` | MainWindow.xaml.cs 切页处 +1 行 | grep IsOpen=false；切页往返实测 |
| A-4 | 取景器 HUD 底色统一：CaptureOverlayWindow.xaml:191 HudBar #E6202020、:76 SelectionActions、:145 LoupePanel、:177 CopyToast、:51 SizeTip → 是否统一到 #F2 需主人裁定（更实但遮挡更多） | CaptureOverlayWindow.xaml（5 处常量） | 颜色值 grep；主人肉眼 |
| A-5 | PinCrop HUD 已 #F2、CenterHintPlate 已有底板：无需改，只做回归确认 | PinCropWindow.xaml:33-38、:56-67 | grep 值 |

### 3.2 档 B：改布局或交互（需设计确认）

| 编号 | 内容 | 现状证据 | 影响面 |
| --- | --- | --- | --- |
| B-1 | OCR 结果窗「标题」与「复制状态」分离：新增一行 CopyStatusText + AutomationProperties.LiveSetting=Polite，复制反馈改写它；StatusText 只保留标题/编辑状态 | OcrResultWindow.xaml:14-23 只有 StatusText+EngineText；OcrResultWindow.xaml.cs:171-176 AnnounceStatus 写 StatusText、:213/:231/:233/:235 复制反馈复用 | 1 个 XAML + 1 个 .cs；可访问性有收益 |
| B-2 | 快捷键页去重：「按键一览」（MainWindow.xaml:660-743，六张卡片）与「完整快捷键目录」（:745-800，10 行表）内容重复；建议合并为一张表并保留「范围/按键/动作」三列 | MainWindow.xaml:660-743 vs :745-800 | 删除/合并约 80 行 XAML，需主人确认信息取舍 |
| B-3 | 设置页统一卡片分组：CaptureSection（:807-916）的延时/指针/边框颜色、AppearanceSection（:1176-1216）、HistorySection（:1117-1160）、StartupSection（:1163-1173）仍以裸控件平铺，未用 SettingsCardStyle | SettingsCardStyle 已用于 :337/:673/:683/:693/:703/:713/:723/:733/:746/:923/:940/:985/:1181 | 4 页、约 10-14 张卡片；纯 XAML 但行数多 |
| B-4 | 主页突出主操作：四张动作卡同权重；文档建议「截图取景」用 AccentButtonStyle 或更大卡 | MainWindow.xaml:192-285 四张 ActionCardButtonStyle | 1 页布局；需主人确认视觉层级 |
| B-5 | HUD 青色标签统一：取景器用 #FF8FE3FF（ProcessText :61、Tab :206、HudModeText :212）与 HoverBorder #FF00C8FF（:30），PinCrop 全白；统一或明确「取景器青色=模式/候选、裁剪白色=动作」 | 同上 | 颜色语义决策；5-6 处常量 |
| B-6 | 取景器中心提示：文档建议给中心提示加底板；当前无中心提示（HudBar 在底部），若未来恢复中心提示需同时加底板 | CaptureOverlayWindow.xaml 无中心提示 | 仅记录，不实施 |

### 3.3 批次

| 批次 | 内容 | 预估 | 独立性 |
| --- | --- | --- | --- |
| P1-批1 | A-1 + A-2（次级文字画刷 + 小节留白） | 3h | 纯 XAML，独立可验收；机器可验 |
| P1-批2 | B-1（OCR 状态分离）+ B-3（设置页卡片） | 1.5 天 | 两者不同文件，可并行；B-1 可先做 |
| P1-批3 | B-2（按键页去重）+ B-4（主页层级） | 1 天 | 同一 MainWindow.xaml，串行；需主人先定信息取舍 |
| P2-批4 | B-5（HUD 配色统一）+ A-4（取景器 HUD 底色裁定）+ A-3（InfoBar 离页折叠） | 3h | 需主人先裁定颜色语义 |

- 每批独立可回退：A 类只改属性值，B 类改动集中在单文件，回退 = git checkout 对应 XAML（提交前）。
- 顺序建议：P1-批1 → P1-批2 → P2-批4 → P1-批3（把需要主人决策的两项放最后，避免阻塞）。

## 4. 验收方式（每项：肉眼判据 + 客观判据；机器可验 vs 必须主人）

| 项 | 肉眼判据（主人真机） | 客观判据（机器可验） | 归类 |
| --- | --- | --- | --- |
| P0-1 | Win11 深色下四窗标题栏 caption 与内容区同深；浅色下同浅；切换系统主题后不重启即跟随 | grep：`ApplyTitleBar` 调用点 = 4（MainWindow.xaml.cs:57、HistoryWindow.xaml.cs:49、OcrResultWindow.xaml.cs:68、PinManagerWindow.cs:24）；`PreferredTheme` 仅出现于 WindowAppearance.cs:19 | 代码 grep 机器可验；实际观感主人确认 |
| P0-2 | 深/浅/高对比度下主窗底与卡片、文字都可读；Annotation 窗在浅色系统下仍深底且标注预览不变色 | grep：RootGrid 的 Background 为 ThemeResource（MainWindow.xaml:8-10）；`#FF1E1E1E` 只出现在 AnnotationWindow.xaml；`MicaBackdrop` 计数为 0 | grep 机器可验；颜色观感/高对比度主人确认 |
| P0-3 | 四个危险按钮为红色系、与普通操作之间有分隔线；讲述人朗读出「永久删除…」而非只读「删除…」 | grep：`DangerButtonStyle` 使用点 = 4（MainWindow.xaml:1154、HistoryWindow.xaml:270/:276、PinManagerWindow.xaml:48）+ 定义 3 处；四个按钮各有 AutomationProperties.Name | grep 机器可验；讲述人必须主人（禁止代理用 UIA 自动代测） |
| P0-4 | 裁剪窗未选区/已选区两种状态底部只有一条提示；Esc 与右键都能退出 | grep：PinCropWindow.xaml 内 `HudHintText` 计数 = 1；PinCropWindow.cs 赋值点 = 2（:346/:408） | grep 机器可验；两态切换主人确认 |
| P0-5 | 快捷键页只显示录制/恢复相关反馈；切到主页再回来不出现「复制到剪贴板」类文案 | grep：`SetHotkeyStatus` 调用点全部位于 MainWindow.Hotkeys.cs；MainWindow.xaml.cs 中无其他写入 HotkeyStatusBar.Message 的语句 | grep 机器可验 |
| P0-6 | 窗口缩到最窄时标题区仍一行副标题（省略号），不把操作说明挤成两行；悬停副标题能看到全文 | grep：`TextTrimming="CharacterEllipsis"` + `MaxLines="1"` 出现在 HistoryWindow.xaml:26-31 区间；HistoryWindow.xaml.cs:280-281 为单串 | grep 机器可验；窄窗观感主人确认 |
| P1-A-1 | 深/浅两主题下次级文字对比度一致，不再发灰发暗；高对比度下文字可读 | grep `Opacity="0.` 在 4 个 XAML 的计数 = 1（仅 MainWindow.xaml:89 背景图功能值） | 机器可验 + 主人看高对比度 |
| P1-B-1 | 复制后标题仍显示「识别完成 · 用时 xx ms」，复制结果单独一行；读屏播报复制结果 | grep：OcrResultWindow.xaml 新增 CopyStatusText；AnnounceStatus 写 CopyStatusText 而非 StatusText；LiveSetting=Polite 存在 | 代码机器可验；读屏必须主人 |

### 4.2 机器可验命令清单（每单收尾执行）

1. 整包构建（XAML 真编译）：`pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag c1 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-c1/"` → 0 警告 0 错误。
2. 回归测试：`pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag c1test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"` → 通过 ≥ 206。
3. 格式：`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` → rc=0。
4. grep 断言（示例，全部只读）：`rg -c "DangerButtonStyle" src/PrtScr.App`、`rg -c "HudHintText" src/PrtScr.App/Views/PinCropWindow.xaml`、`rg -n "SetHotkeyStatus\(" src/PrtScr.App`、`rg -n "Opacity=\"0\." src/PrtScr.App`。
5. 明确禁止：`dotnet build -t:Compile`（假绿）、`--smoke`（写主人真实 data/）、git commit/push。

### 4.3 必须主人真机确认（代理不得代测）

- 高对比度两套主题（水生、沙漠）逐页检查：文字、危险按钮、贴图徽标、取景 HUD。
- 100% / 125% / 150% 缩放逐页检查：历史标题区（P0-6）、快捷键目录表（B-2 若做）、设置页卡片（B-3 若做）。
- 讲述人朗读：四个危险按钮的可访问名、OCR 复制结果播报。
- 浅色/深色系统主题切换后不重启，四窗标题栏与主窗底是否跟随（P0-1/P0-2）。
- 裁剪窗两态 HUD 文案（P0-4）与取景器 HUD 观感（A-4 若改底色）。

### 4.4 回归确认（C 阶段任何一批都要过）

- 截图取景：F1 → 框选 → Enter，图片进剪贴板与历史；内容/来源标记与 P-04 日志关键字一致。
- 防重复确认：Ctrl+C 连按两次只产生一条历史（P-14b 的 confirm 会话校验）。
- OCR 等待真实结果：结果窗打开前不显示空结果；复制失败有可见提示。
- 贴图编辑出口：标注态复制/OCR/裁剪取当前合成（P-01）。
- 快捷全屏：F2 出图且不打开取景窗；连按两次不并发写两条历史（P-14b' 若已实施）。

## 5. 风险清单

1. **主题跟随系统**：App.xaml 未设 RequestedTheme（App.xaml.cs 也未设置），一切颜色依赖 ThemeResource；一旦某处写死颜色（如 AnnotationWindow.xaml:7）就会在切换主题后不一致。缓解：C1-B 把「固定深色」清单化，新增硬编码颜色必须带注释理由。
2. **Mica 在 Win10 19041 不可用**：引入 MicaBackdrop 需 `MicaController.IsSupported` 判定 + 纯色回退；本项目是截图工具，半透明底还会让取景/贴图对比判断变复杂。缓解：本阶段明确不做 Mica，保持 SolidBackgroundFillColorBaseBrush。
3. **Opacity=0.62 类硬编码在高对比度下压暗系统色**：系统只覆盖颜色，不会取消 Opacity 乘法，导致文字对比度不足。缓解：P1-A-1 全部换 TextFillColorSecondaryBrush；无法换的（如遮罩 #60000000）保持并注释。
4. **四窗标题栏一致**：PreferredTheme 在 Win10 只影响 caption 按钮前景；标题栏底色由系统绘制，深色内容区 + 系统浅色标题栏在 Win10 仍可能割裂。缓解：C1-A 在主人机器上确认；若接受不了，再评估自定义标题栏（工作量大，建议 v1.5.0 另立单）。
5. **字体资源**：按键统一用 Consolas（MainWindow.xaml:678 等），GDI 贴图 chip 用微软雅黑（NativePinWindow.Render 硬编码），XAML 未定义字体资源键；系统缺字体时回退不可控。缓解：本阶段不改字体，P2 再评估统一字体资源与回退链。
6. **危险色与 AccentButtonStyle 冲突**：DangerButtonStyle 的 Foreground 用 TextOnAccentFillColorPrimaryBrush，若将来把危险按钮放进 Accent 背景区域会失去对比。缓解：样式保持独立，禁止把 AccentButtonStyle 叠加到危险按钮。
7. **取景/裁剪 HUD 硬编码深色不随主题**：这是功能需要（遮罩、冻屏对比），但高对比度用户可能看不清白色文字。缓解：HUD 文案加 AutomationProperties.Name（读屏可用），颜色统一到 #F2 需主人裁定（A-4）。
8. **XAML 编译门槛陷阱**：`-t:Compile` 不触发 XAML 代码生成，曾出现 1248 个假错误；XAML 改动必须整包构建 + OutDir 重定向，否则会把假绿当通过。缓解：§2.2 门禁口径写死，禁 -t:Compile。
9. **验证文档与实机不一致**：建议文档基于旧截图，若照做会重复已实现的工作并引入回归。缓解：本文件 §0 偏差表作为唯一对照；任何新条目先跑 grep 确认现状。

## 附录 A：调研证据索引（文件:行 → 结论）

- 标题栏：`Services/WindowAppearance.cs:9-27`；`MainWindow.xaml.cs:57`；`Views/HistoryWindow.xaml.cs:49`；`Views/OcrResultWindow.xaml.cs:68`；`Views/PinManagerWindow.cs:24`。
- 根背景：`MainWindow.xaml:8-10`；`Views/HistoryWindow.xaml:8`；`Views/PinManagerWindow.xaml:8`；`Views/OcrResultWindow.xaml:7`；`Views/AnnotationWindow.xaml:7/:15/:55`。
- 危险按钮：`MainWindow.xaml:77-79`、`:1152-1155`；`Views/HistoryWindow.xaml:10-12`、`:261-278`；`Views/PinManagerWindow.xaml:10-12`、`:39-48`。
- 裁剪 HUD：`Views/PinCropWindow.xaml:33-38`、`:56-67`；`Views/PinCropWindow.cs:345-352`、`:408`。
- 快捷键页：`MainWindow.xaml:651-655`；`MainWindow.xaml.cs:286-291`；`MainWindow.Hotkeys.cs` 17 处 SetHotkeyStatus。
- 历史标题区：`Views/HistoryWindow.xaml:24-38`、`:215-222`；`Views/HistoryWindow.xaml.cs:280-281`。
- OCR：`Views/OcrResultWindow.xaml:14-23`；`Views/OcrResultWindow.xaml.cs:171-176`、`:204-236`。
- 背景外观：`MainWindow.xaml:1187-1215`；`MainWindow.Appearance.cs:85-86`。
- 提示编辑器：`Views/ToastPlacementWindow.cs:20`、`:93`、`:136`。
- 贴图管理：`Views/PinManagerWindow.xaml:59-65`、`:74-144`；`Views/PinMenuWindow.cs:233-247`。
- 文字贴图：`Services/PinTextCardRenderer.cs:70`、`:86`。
- 取景 HUD：`Views/CaptureOverlayWindow.xaml:30`、`:51`、`:61`、`:76`、`:145`、`:177`、`:191`、`:206`、`:212`。

## 附录 B：建议写进 C 阶段的三条硬约束

1. **先 grep 后动手**：任何「建议文档条目」在开工前先跑 grep 确认现状；已实现项只补验收判据，不改代码。
2. **颜色只走主题画刷**：新增/修改的颜色必须用 ThemeResource 或 SystemFillColor*；确需硬编码（遮罩/HUD/标注深底）必须带一行注释说明理由，并加入 C1-B 的固定色清单。
3. **验收双轨**：每项必须同时给出「肉眼判据」和「可 grep/可构建的客观判据」；高对比度、DPI 缩放、讲述人一律主人真机确认，代理不做合成热键或 UIA 自动代测。
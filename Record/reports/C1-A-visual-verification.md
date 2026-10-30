# C1-A：UI 建议逐条对 HEAD 验证（判定表）

- 基线：HEAD 69616cc（v1.3.1 release-prep）；核对时间 2026-10-30 22:4x。
- 工作树状态：含未提交的 task-37（清理死代码）删除 `src/PrtScr.App/Views/AnnotationWindow.xaml` 与 `.xaml.cs`；`git status` 记为 `D`，`src/PrtScr.App` 内已无 `AnnotationWindow` 引用（grep 0 命中）。本表按此工作树判定。
- 方法：只读 grep + 局部读文件；每条给 文件:行；不改任何 .xaml/.cs，不构建。
- 置信度口径：**高** = 直接读到结构/值且无运行时歧义；**中** = 结构确定但观感/折行依赖窗口尺寸与 DPI，需真机确认。

## 表一：P0 六项判定

| # | 建议条目 | 判定 | 证据（文件:行 + 关键结构） | 置信度 |
| --- | --- | --- | --- | --- |
| P0-1 | 四窗标题栏跟随深色主题 | **已实现** | `Services/WindowAppearance.cs:9-13` ApplyTitleBar 先 Update 再订阅 ActualThemeChanged；`:15-27` 设 AppWindow.TitleBar.PreferredTheme，异常写日志。调用点 4 处：MainWindow.xaml.cs:57、Views/HistoryWindow.xaml.cs:49、Views/OcrResultWindow.xaml.cs:68、Views/PinManagerWindow.cs:24 | 高 |
| P0-2 | 根背景/深色底一致性 | **已实现（走主题纯色，未用 Mica）** | MainWindow.xaml:8-10 `RootGrid Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"`；HistoryWindow.xaml:8、PinManagerWindow.xaml:8、OcrResultWindow.xaml:7 同主题底；`MicaBackdrop` 在 src 内 0 命中；唯一的 SystemBackdrop 是 PinMenuWindow.cs:137 `DesktopAcrylicBackdrop`（弹出菜单）。文档所指「硬编码深色的 Annotation 窗」已被 task-37 删除 | 高 |
| P0-3 | 危险动作配色与分组 | **半实现** | 配色/分组已实现：DangerButtonStyle 定义 MainWindow.xaml:77-79、HistoryWindow.xaml:10-12、PinManagerWindow.xaml:10-12；使用点 MainWindow.xaml:1152-1154（ClearHistoryButton）、HistoryWindow.xaml:267-278（删除…/清空分类…）、PinManagerWindow.xaml:45-48（全部关闭）；分隔线 HistoryWindow.xaml:261-266/:282-287、PinManagerWindow.xaml:39-44。**缺口**：四个按钮均无 `AutomationProperties.Name`（HistoryWindow.xaml、PinManagerWindow.xaml 全文 0 处；MainWindow.xaml:1151-1155 也未设），讲述人只能读 Content | 高 |
| P0-4 | 裁剪 HUD 重复退出提示 | **已实现** | PinCropWindow.xaml 仅 `HudHintText` 一处（:64，初值「拖拽 框选 · Esc / 右键 退出」）；PinCropWindow.cs:346 无选区时写同一句、:352 隐藏 CenterHintPlate、:408 选中后改为「Enter 贴图 · O 识别 · Ctrl+S 保存」；中心提示底板 :33-38 Background=#8C000000；HUD 底色 :56 #F2202020 | 高 |
| P0-5 | 快捷键页状态隔离 | **已实现** | 原 `HotkeyStatusText` 已不存在；现为 InfoBar `HotkeyStatusBar`（MainWindow.xaml:651-655，IsOpen=False）；唯一写入方法 MainWindow.xaml.cs:286-291 SetHotkeyStatus；17 个调用点全部在 MainWindow.Hotkeys.cs（:189/:206/:222/:228/:234/:240/:356/:485/:490/:533/:541/:557/:567/:576/:585/:595/:604/:613），均为录制/冲突/恢复默认反馈 | 高 |
| P0-6 | 历史窗标题区断行 | **半实现** | 布局已改：StatusText 已从右上移到标题区同一行的右侧（HistoryWindow.xaml:215-222，Grid.Column=1、MaxWidth=360、Opacity 0.65、TextTrimming CharacterEllipsis）；统计数字不再拼进副标题（HistoryWindow.xaml.cs:280-281 仅两段静态说明）。**残留**：SubtitleText 仍 `TextWrapping="Wrap"` 且无 MaxLines/Trimming（HistoryWindow.xaml:26-31），窄窗或 150% DPI 下会折成两行，把操作说明顶下去 | 中（折行需真机窄窗确认） |

## 表二：P1 各项判定（合并同类）

| # | 建议条目 | 判定 | 证据 | 置信度 |
| --- | --- | --- | --- | --- |
| P1-1 | 设置页统一卡片分组 | **半实现** | 已卡片化：快捷键页 MainWindow.xaml:337、贴图页 :923/:940/:985、外观 :1181；未卡片化（裸控件平铺）：截图行为页 :807-916、提示页 :1004-1114、历史设置页 :1117-1160、启动页 :1163-1173 | 高 |
| P1-2 | 下拉框对齐 | **已实现** | CaptureProfileCombo :828-832、DefaultModeCombo :842-846 均 MinWidth=260 / MaxWidth=420 / HorizontalAlignment=Left | 高 |
| P1-3 | 小节标题 16–18 半粗 + 上留 24px | **半实现** | SectionTitleStyle :25-29 已 16 SemiBold；上边距仍是 `Margin="0,10,0,0"`，未到 24px | 高 |
| P1-4 | 次级文字改主题画刷 | **半实现** | HintTextStyle :30-33 已 `TextFillColorSecondaryBrush`；仍有 36 处文字 inline Opacity（grep `Opacity="0.`：MainWindow 24 处 / HistoryWindow 8 / PinManagerWindow 4 / OcrResultWindow 1，其中 MainWindow.xaml:89 是背景图功能值不属文字） | 高 |
| P1-5 | 导航阈值 800→720、宽 168 | **已实现** | MainWindow.xaml:95-103 ExpandedModeThresholdWidth=720、CompactModeThresholdWidth=0、OpenPaneLength=168 | 高 |
| P1-6 | OCR 状态行/测试反馈 | **已实现** | MainWindow.xaml:1240 UmiTestRing（ProgressRing）、:1249 OcrStatusBar（InfoBar） | 高 |
| P1-7 | OCR 标题与复制状态分开 | **未实现** | OcrResultWindow.xaml:14-23 只有 StatusText(:16) + EngineText(:22)；OcrResultWindow.xaml.cs:171-176 AnnounceStatus 直接写 StatusText；复制反馈 :213/:231/:233/:235 全复用它 → 复制状态覆盖「识别完成 · 用时 xx ms」 | 高 |
| P1-8 | 自定义背景百分比/文件名/预览 + 无图禁用 | **已实现** | MainWindow.xaml:1187-1215（预览、BackgroundFileNameText、BackgroundOpacityText、Slider、BackgroundDisabledHintText）；MainWindow.Appearance.cs:85-86 `BackgroundOpacitySlider.IsEnabled = hasImage` 并写禁用提示 | 高 |
| P1-9 | 关于页加日志/数据文件夹/复制版本 | **已实现** | MainWindow.xaml:1267「打开日志文件夹」、:1268「打开数据文件夹」、:1269「复制版本信息」、:1264 AboutText、:1271 AboutStatusText | 高 |
| P1-10 | 贴图管理去双层框 + 状态标签 | **已实现** | PinManagerWindow.xaml:58-66 ItemContainerStyle Background=Transparent；卡片 :80 CardBackgroundFillColorDefaultBrush；状态标签 :116 StateText、:118 AccentTextFillColorPrimaryBrush | 高 |
| P1-11 | 右键菜单显示开关状态 | **已实现** | PinMenuWindow.cs:233-236 不透明度 check；:246 锁定位置 check:_locked；:247 点击穿透 check:_clickThrough | 高 |
| P1-12 | 提示编辑器可拖动 + 保存主按钮 | **已实现** | ToastPlacementWindow.cs:93 拖动提示说明、:136「保存位置」主按钮、:20 注释说明保存为 custom 矩形 | 高 |
| P1-13 | 文字贴图不透明底 | **已实现** | Services/PinTextCardRenderer.cs:70 默认 FromArgb(0xFF,0x1F,0x1F,0x1F)、:86 fill FromArgb(255,…) | 高 |
| P1-14 | HUD 底色 #E6→#F2、中心提示加底板 | **半实现** | 裁剪窗已达成：PinCropWindow.xaml:33-38 CenterHintPlate、:56 HudBar #F2202020；取景器仍未改：CaptureOverlayWindow.xaml:191 HudBar #E6202020（另有 :51/:76/:145/:177 同色） | 高 |
| P1-15 | 主页突出主操作 | **未实现** | MainWindow.xaml:196/:223/:245/:268 四张卡全部 ActionCardButtonStyle，无主次层级 | 中（层级是否该做需主人判断） |
| P1-16 | 卡片 MinHeight 112→88 | **已实现** | MainWindow.xaml:12-19 ActionCardButtonStyle MinHeight=88、Padding=16、CornerRadius=12 | 高 |
| P1-17 | 目录表改键帽 / 去重复「按键一览」 | **未实现** | MainWindow.xaml:660-743（六张卡片）与 :745-800（十行目录）内容重叠；目录表为纯文本行，无键帽样式 | 高 |
| P1-18 | 标注栏取消条件变色/状态行/尺寸标签 | **不存在该结构** | 独立标注窗已删除（task-37：AnnotationWindow.xaml/.xaml.cs 为 `D`，src 内 0 引用）；标注已并入贴图窗口自绘工具栏（NativePinWindow 系列） | 高 |

## 表三：文档里已不存在于代码的说法（逐条指出当前实际结构）

| # | 文档说法 | 当前实际结构 |
| --- | --- | --- |
| 1 | 「根 Grid 无背景，浅色主题深字落黑底」 | MainWindow.xaml:8-10 RootGrid 已有 `Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"`；卡片另有 CardBackgroundFillColorDefaultBrush（如 PinManagerWindow.xaml:80） |
| 2 | 「PinCropWindow.xaml:61 与 :64 各一条退出提示」 | 只有一条：PinCropWindow.xaml:64-65 `HudHintText`；:33-38 是 CenterHintPlate（中心提示底板），不是第二条提示 |
| 3 | 「HotkeyStatusText 显示『全屏截图已复制到剪贴板』」 | 该控件已不存在；替代为 `InfoBar HotkeyStatusBar`（MainWindow.xaml:651-655）+ SetHotkeyStatus（.xaml.cs:286-291），调用点全在录制反馈 |
| 4 | 「StatusText MaxWidth=420 贴在标题区第二行右上」 | StatusText 在标题区同一行右侧（HistoryWindow.xaml:215-222），MaxWidth=360、Opacity 0.65、CharacterEllipsis；不在第二行 |
| 5 | 「下拉框设 Stretch+MaxWidth 导致居中」 | MainWindow.xaml:828-832/:842-846 已 `HorizontalAlignment="Left"`，MaxWidth=420 仅作上限 |
| 6 | 「导航 800 / 宽侧栏」 | MainWindow.xaml:95-103 已 720 / 168 |
| 7 | 「卡片 MinHeight 112」 | MainWindow.xaml:12-19 已 88 |
| 8 | 「贴图管理卡片双层框」 | PinManagerWindow.xaml:58-66 容器 Background=Transparent；卡片自带 CardBackgroundFillColorDefaultBrush（:80） |
| 9 | 「危险动作未用危险色」 | 4 处 DangerButtonStyle（MainWindow.xaml:1154、HistoryWindow.xaml:270/:276、PinManagerWindow.xaml:48）+ 2 处分隔线；危险色已存在 |
| 10 | 「右键菜单不显示 ✓ / 已锁定」 | PinMenuWindow.cs:246 `check:_locked`、:247 `check:_clickThrough`、:233-236 不透明度 check |
| 11 | 「文字贴图背景半透明」 | PinTextCardRenderer.cs:70/:86 alpha=0xFF，已不透明 |
| 12 | 「提示编辑器不能拖动、缺主按钮」 | ToastPlacementWindow.cs:93 可拖动说明、:136「保存位置」主按钮 |
| 13 | 「AnnotationWindow 深色硬编码（#FF1E1E1E 等）」 | 该窗已删除：`git status` 显示 `D src/PrtScr.App/Views/AnnotationWindow.xaml` 与 `.xaml.cs`；`src/PrtScr.App` 内 `AnnotationWindow`、`OpenEditor` 均 0 命中 |
| 14 | 「裁剪 HUD 标签是青色 #8FE3FF」 | PinCropWindow.xaml 内无 #8FE3FF；青色只在取景器（CaptureOverlayWindow.xaml:61/:206/:212） |

## 表四：读 XAML/代码时发现的真实缺陷（≤8，供 C-2 排序）

| # | 缺陷 | 位置 | 影响 | 建议 |
| --- | --- | --- | --- | --- |
| D-1 | 四个危险按钮无 `AutomationProperties.Name` | MainWindow.xaml:1151-1155；HistoryWindow.xaml:267-278；PinManagerWindow.xaml:45-48 | 讲述人只读「删除…」「清空分类…」「全部关闭」，听不出作用对象与不可恢复性 | C1-E 补名（见 C2 决策 4） |
| D-2 | 贴图管理卡片内四个动作按钮无逐卡可访问名 | PinManagerWindow.xaml:139-142（复制/另存/识别/关闭，位于 DataTemplate 内） | 读屏遍历时连续听到「复制、另存、识别、关闭」×N，无法知道操作的是哪张卡 | 在 DataTemplate 内用 `{x:Bind Caption}` 拼 AutomationProperties.Name（如「复制：{Caption}」） |
| D-3 | 36 处文字用 inline Opacity 压暗 | MainWindow.xaml:175(0.6)/357-361(0.55)/678-738(0.6-0.7)/766-768(0.55)；HistoryWindow.xaml:29(0.6)/35(0.62)/212(0.62)/220(0.65)；PinManagerWindow.xaml:113(0.62)/158(0.62)/167(0.58)；OcrResultWindow.xaml:22(0.6) | 高对比度/浅色主题下次级文字对比度不足，且系统无法覆盖乘法后的颜色 | A-1 换 `TextFillColorSecondaryBrush`；MainWindow.xaml:89 背景图 0.25 保留（功能值） |
| D-4 | OCR 结果窗标题与复制状态共用一个 TextBlock | OcrResultWindow.xaml:14-23；OcrResultWindow.xaml.cs:171-176（AnnounceStatus 写 StatusText）、:213「正在复制…」、:231-235 复制结果 | 复制后标题「识别完成 · 用时 xx ms」被覆盖，读屏也只播报最后一次写入 | B-1：新增 CopyStatusText + LiveSetting=Polite，复制反馈改写它 |
| D-5 | 历史窗副标题可折行 | HistoryWindow.xaml:26-31（TextWrapping=Wrap，无 MaxLines/Trimming）；代码 HistoryWindow.xaml.cs:280-281 | 窄窗/125-150% 下折成两行，把操作说明顶下去，标题区高度跳动 | C1-D：单行省略 + 文案缩短（C2 决策 5） |
| D-6 | 快捷键页两份重复清单 | MainWindow.xaml:660-743「按键一览」六卡 vs :745-800「完整快捷键目录」十行表 | 同一信息两处维护，改热键容易漏改；页面过长 | B-2 合并；短期先在 :745 区块顶部加「以下为完整目录（含取景/贴图内快捷键）」避免误读为重复 |
| D-7 | 取景器 HUD/选区控件硬编码深色，不随主题 | CaptureOverlayWindow.xaml:51/:76/:145/:177/:191（#E6202020）、:61/:206/:212（#FF8FE3FF）、:30（#FF00C8FF） | 高对比度主题下深底+白字/青字可能与系统强制配色冲突；浅色系统观感割裂 | 本阶段不改（遮罩语义，见 C2 决策 3）；只记录为视觉债 V-1 |
| D-8 | 裁剪窗选中后退出提示被替换 | PinCropWindow.xaml:64 初值含「Esc / 右键 退出」；PinCropWindow.cs:408 选中后改为「Enter 贴图 · O 识别 · Ctrl+S 保存」 | 已有选区时主人看不到退出方式，只能凭记忆按 Esc | 在 :408 文案末尾补「 · Esc 退出」（一行改动，列入 C1-D 或单独小单） |

## 结论

1. 建议文档的 P0 六项：**2 项完全已实现（P0-1/P0-2 按另一路径）**、**P0-4/P0-5 已实现**、**P0-3 半实现（只缺可访问名）**、**P0-6 半实现（只缺单行收敛）**。若按文档原文重做，会改掉已正确的结构（尤其根背景、PinCrop 提示、HotkeyStatusText、StatusText 位置）。
2. P1 已实现 10 项、半实现 4 项、未实现 3 项、结构不存在 1 项；真正值得做的集中在：OCR 状态分离、36 处 Opacity、副标题单行、HUD 记录、快捷键页去重。
3. 表四 8 条中，**D-1/D-2 是确定的可访问性缺陷**，D-3/D-4/D-5 是确定的视觉/信息缺陷；D-6/D-7/D-8 属取舍项。
4. 本报告只做判定，不含修复；修复切分见 `Record/plans/C2-visual-decisions.md`。

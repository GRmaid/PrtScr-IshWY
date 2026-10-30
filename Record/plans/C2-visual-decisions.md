# C2：视觉整改决策清单（C-1 阶段）

- 基线：HEAD 69616cc（v1.3.1 release-prep）+ 工作树未提交改动；核对时间 2026-10-30 22:4x。
- 本文件只做**决策**，不改源码；每条给出「做/不做 + 理由 + 验收或记录办法」。
- 判定事实来源：`Record/reports/C1-A-visual-verification.md`（同批落盘，逐条带 文件:行）。
- 重要前提勘误：C-1 计划（`Record/plans/C1-visual-p0-plan.md`）与 UI 建议文档写作时，独立标注窗 `AnnotationWindow` 仍存在；**该文件已在本工作树被删除**（`git status` 记为 `D`，`src/PrtScr.App` 内 `AnnotationWindow`/`OpenEditor` 均 0 命中）。因此 C-1 计划中「AnnotationWindow 硬编码深色三处」「离屏窗不可激活导致 Mica 失效」两段不再适用于当前树，本文件以已删除的事实收口。

## 决策 1：不引入 Mica / 任何窗口级 SystemBackdrop

**决策：不做。** 主窗/历史/贴图管理/OCR 四窗继续用 `SolidBackgroundFillColorBaseBrush`（MainWindow.xaml:8-10 等），本轮不接 Mica、MicaAlt 或 Acrylic。

**理由：**
1. 现状已满足「跟随主题」这一目标：四窗根容器都是主题画刷，浅色/深色切换由系统资源自动完成，不需要额外代码。
2. Mica 在本项目最低支持面（Win10 19041 + WinAppSDK 2.5.1）并非处处可用，需要运行时特性检测 + 回退分支；当前实现零检测、零回退，可靠性更高。
3. 主窗带自定义背景图（MainWindow.xaml:86-89，Opacity 0.25）叠在窗口底上；真接 Mica 后背景图与材质叠加的观感必须逐主题实测，而本项目**代理不得做交互/性能实测**，无法在交付前给出可信结论。
4. 高对比度主题下 SystemBackdrop 常被系统禁用或替换，现有纯色底反而稳定；接材质会新增一类「高对比度下观感退化」风险。
5. 已删除的 AnnotationWindow 才是「离屏不可激活窗」的典型风险点；其删除后，剩下四个窗口都是正常可激活主窗/子窗，材质收益远小于复杂度。

**不做时的替代方案（本轮采用）：** 固定语义色板，全部走系统资源键，不硬编码色值——
- 窗口底：`SolidBackgroundFillColorBaseBrush`；卡片：`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`；
- 设置行/输入：`ControlFillColorDefaultBrush` + `ControlStrokeColorDefaultBrush`（MainWindow.xaml:50-56 SettingsCardStyle 已经是这套）；
- 正文：默认前景；次级文字：`TextFillColorSecondaryBrush`（HintTextStyle，MainWindow.xaml:30-33）；
- 状态色：危险 `SystemFillColorCriticalBrush`（DangerButtonStyle，MainWindow.xaml:77-79）、成功/注意用对应 `SystemFillColor*Brush`；
- 强调色：`AccentTextFillColorPrimaryBrush` / `AccentButtonStyle`（不固定品牌色，跟随系统）。

**验收（机器可验）：** `grep -n "MicaBackdrop\|SystemBackdrop" src/PrtScr.App` 只允许命中 PinMenuWindow.cs:137（菜单弹出层的 DesktopAcrylicBackdrop，与窗口底无关）与注释行；窗口 XAML 内 `SystemBackdrop` 0 命中。
**验收（主人真机）：** 系统切浅色/深色 + 开任一高对比度主题，四窗（主窗、历史、贴图管理、OCR）底色与文字都随系统变化、无「深底深字」。
**记录：** 本决策写入 C2；若后续想接 Mica，须另立单并附「运行时检测 + 回退」与真机四主题截图验收。

## 决策 2：AnnotationWindow 的深色硬编码：不做代码改动，按「已删除」收口

**决策：不改（没有可改对象）。** 该窗已被删除，不做「抽资源键」也不做「固定深色」的二选一。

**理由：** 文件已不存在（`Views/AnnotationWindow.xaml`、`.xaml.cs` 在 `git status` 为 `D`；`src/PrtScr.App` 内 0 引用）。标注能力已并入贴图窗口的自绘工具栏（NativePinWindow 系列，GDI 绘制、无 XAML），因此原「标注窗深色硬编码」缺陷消失，只剩贴图窗 GDI 层的颜色常量问题——那属于 NativePinWindow 绘制域，不在本阶段视觉单范围。

**验收（机器可验）：** `ls src/PrtScr.App/Views/AnnotationWindow*` 返回不存在；`grep -rn "AnnotationWindow" src/PrtScr.App` 0 命中（obj/ 下旧生成文件不计）。
**记录：** 若未来重新引入独立标注窗，强制要求：根容器用 `SolidBackgroundFillColorBaseBrush`，控件色只用系统资源键；并在新单验收里加「浅色主题下无深底深字」一条。

## 决策 3：取景器 HUD #E6→#F2 与青色标签统一（B-5）：本轮不做

**决策：不做。** 取景器 HUD 保持 `#E6202020`（CaptureOverlayWindow.xaml:191，另 :51/:76/:145/:177 同色），青色标签（#FF00C8FF 悬停边 :30、#FF8FE3FF 进程名/模式 :61/:206/:212）保持现状。裁剪窗已完成的那部分（PinCropWindow.xaml:56 #F2202020、:33-38 中心提示底板）保留，不回退。

**理由：**
1. 取景器 HUD 是**遮罩场景**里的浮动条：底越不透明，遮挡冻屏内容越多。裁到 #F2（95%）会明显加重对画面底部的遮挡，而它的可读性问题是「字色与底对比」而非「底不够实」。
2. 青色在本窗承担三种语义（悬停候选边、进程名、当前模式），且悬停边 #00C8FF 与 HUD 标签 #8FE3FF 属同色系梯度；统一成别的强调色会破坏「悬停=青边 + 模式=青字」的一致性，收益仅为观感整齐。
3. 该窗**不随主题**（硬编码深色是刻意的遮罩语义），因此「与主题色板统一」这条理由本身不成立；真要统一，应整体重做取景器配色，属 C 阶段之后的大项。
4. 无代理侧实测手段验证遮罩下的对比度（禁止合成热键截取景器），改了也无法在交付前给出可信判据。

**不做怎么记（视觉债 V-1）：** 在 C1-A 报告表四 D-7 已登记：取景器 HUD/选区控件硬编码深色，不随主题、不随高对比度。**触发复查条件**：主人真机在高对比度主题下反馈取景器文字不可读，或 C-3 阶段决定取景器整体换肤时一并处理；届时判据是「12px 文字在 HUD 底色上对比度 ≥ 4.5:1，且 HUD 高度不超过当前值」。
**若将来要做（写死做法，避免二义）：** 只调 `HudBar` 与四个 `#E6202020` 面板的 alpha（#E6→#F2 只在 HUD 条上做），青色调保持不变；验收 = 主人真机在浅/深/高对比度下各开一次取景，HUD 文字可读且不遮住选区下沿。

## 决策 4：危险按钮清单：做可访问名，配色已就位不再改

**决策：做（仅补 `AutomationProperties.Name`，共 4 个按钮）。** 危险配色与分组已在 HEAD 实现（DangerButtonStyle + 分隔线），本轮不动颜色，只补可访问名——这是 D-1 缺陷，也是讲述人可感知的实际缺陷。

| 按钮 | 位置 | 现 Content | 建议 AutomationProperties.Name |
| --- | --- | --- | --- |
| 清空全部历史 | MainWindow.xaml:1151-1155（ClearHistoryButton） | 清空全部历史… | 清空全部截图历史（会先确认，不可恢复） |
| 删除… | HistoryWindow.xaml:267-272 | 删除… | 删除选中的截图（会先确认，不可恢复） |
| 清空分类… | HistoryWindow.xaml:273-278 | 清空分类… | 清空当前分类的全部截图（会先确认，不可恢复） |
| 全部关闭 | PinManagerWindow.xaml:45-48 | 全部关闭 | 关闭全部贴图窗口（不删除图片文件） |

**理由：** 四个按钮 Content 都带省略号，读屏只念「删除…」「清空分类…」，听不出作用范围与是否可恢复；「全部关闭」虽非破坏性，但与两个破坏性按钮同组且用危险色，更需要名字说清「不删文件」。
**命名规则（后续同类按钮沿用）：** `<动作对象>（<是否可恢复/是否删文件>）`；不写快捷键（会随配置变化），不写「按钮」二字。
**验收（机器可验）：** 四个位置各命中一行 `AutomationProperties.Name=`；`grep -c "AutomationProperties" HistoryWindow.xaml`、`PinManagerWindow.xaml` 从 0 变为 ≥3/≥1。
**验收（主人真机）：** 开讲述人（Win+Ctrl+Enter），Tab 到这四个按钮，能听到作用对象与「不可恢复 / 不删除图片文件」。

## 决策 5：历史窗副标题折行：做「单行省略 + 文案缩短」

**决策：做。** `SubtitleText` 改单行省略，文案模板缩短到固定两段。

**实现点：**
- XAML（HistoryWindow.xaml:26-31）：`TextWrapping="NoWrap"`、`TextTrimming="CharacterEllipsis"`、`MaxLines="1"`；保留 `FontSize=12`、`Opacity` 改 `Foreground="{ThemeResource TextFillColorSecondaryBrush}"`（与 A-1 同批）。
- 代码（HistoryWindow.xaml.cs:274-281 UpdateSummary）：副标题只写「每类最多 {N} 张 · 仅存本机」，把「按截图模式 / 进程分类」「data\history」这类可推断信息从副标题移除；完整说明放 `ToolTipService.ToolTip`。
- 宽度兜底：文本区外层给 `MinWidth` 或让 StatusText 先收缩（StatusText 已 `CharacterEllipsis`，MainWindow 侧无需改）。

**文案规则（3 条示例，覆盖 1/50/200 三档上限）：**
1. 上限 30（默认）：「每类最多 30 张 · 仅存本机」
2. 上限 50：「每类最多 50 张 · 仅存本机」
3. 上限 200（当前允许的最大值）：「每类最多 200 张 · 仅存本机」
补充规则：不出现「按截图模式 / 进程分类」（窗口标题已表达分组语义）、不出现路径（放 ToolTip）、不拼当前张数（张数在列表/状态栏里）；单位统一用「张」。

**理由：** 现文案两段拼接约 30 汉字 + 路径，150% DPI 或窄窗必折行，把操作说明顶下去；缩短后单行可容纳，且信息不丢（ToolTip 兜底）。
**验收（机器可验）：** HistoryWindow.xaml 的 SubtitleText 命中 `MaxLines="1"` 与 `TextTrimming="CharacterEllipsis"`；grep 代码里 `data\\history` 不再出现在 SubtitleText 赋值处。
**验收（主人真机）：** 主窗拉窄到约 760px、DPI 150%，历史窗标题区仍为一行，右侧 StatusText 不换行压到副标题。

## 决策 6：明确不做（本轮拒绝项）

| # | 不建议做 | 理由 | 记录办法 |
| --- | --- | --- | --- |
| R-1 | 接 Mica / 窗口级 SystemBackdrop | 见决策 1：回退复杂度 + 无法真机验收 | 决策 1；后续另立单需带检测与回退 |
| R-2 | 删除 PinCrop「重复退出提示」 | 第二条根本不存在（唯一一条在 :64），按文档改会误删唯一提示 | C1-A 表三 第 2 条 |
| R-3 | 改造 `HotkeyStatusText` | 控件已不存在，现为 InfoBar（:651-655）+ 17 个录制反馈调用点 | C1-A 表三 第 3 条 |
| R-4 | 把 StatusText 挪到标题区第二行右上、MaxWidth 改 420 | 现布局已是同行右侧（:215-222）、MaxWidth 360；再挪会挤压标题 | C1-A 表三 第 4 条 |
| R-5 | 重建独立标注窗（含其深色资源抽取） | 该窗已删除，标注已并入贴图窗口；重建属功能回归 | 决策 2 |
| R-6 | 取景器 HUD 换主题画刷 / 统一青色 | 遮罩语义，见决策 3 | 视觉债 V-1（C1-A D-7） |
| R-7 | 一次性把 36 处 inline Opacity 全改为主题画刷 | 其中 MainWindow.xaml:89 是背景图功能值（0.25），另有个别非文字用途；全量机械替换会改掉功能透明度 | 只改文字类（A-1 单），功能位保留并加注释 |
| R-8 | 主页四卡强行分主次（P1-15） | 四个动作都是高频入口，硬造主次会误导；若要做应基于真实使用频率统计（需主人数据） | 记入 C-2 待主人裁定，不自行改 |

## 修复切分（供 lead 排单，均为小单）

| 单 | 内容 | 触及文件 | 机器可验判据 |
| --- | --- | --- | --- |
| W-1 可访问名 | 决策 4 的 4 个按钮 + D-2 贴图卡四按钮逐卡名（`{x:Bind Caption}`） | MainWindow.xaml、HistoryWindow.xaml、PinManagerWindow.xaml | 4 处 + DataTemplate 内 AutomationProperties.Name 命中 |
| W-2 OCR 状态分离 | 新增 CopyStatusText（LiveSetting=Polite），复制反馈改写它；StatusText 保留「识别完成 · 用时」 | OcrResultWindow.xaml(.cs) | grep CopyStatusText 命中；AnnounceStatus 不再被复制路径调用 |
| W-3 次级文字画刷 | 36 处文字类 Opacity → `TextFillColorSecondaryBrush`（排除 MainWindow.xaml:89） | MainWindow.xaml、HistoryWindow.xaml、PinManagerWindow.xaml、OcrResultWindow.xaml | 文字类 `Opacity="0.` 计数降为 0（功能位保留并在报告注明） |
| W-4 副标题单行 | 决策 5 | HistoryWindow.xaml(.cs) | MaxLines/Trimming 命中；文案模板含「每类最多 {N} 张 · 仅存本机」 |
| W-5 快捷键页去重 | D-6：合并 :660-743 与 :745-800 两份清单（保留卡片、目录只留取景/贴图内部键） | MainWindow.xaml | 页面内「PrintScreen」「F1」出现次数下降；目录区块只剩内部快捷键 |
| W-6 裁剪提示补退出 | D-8：PinCropWindow.cs:408 文案末尾补「 · Esc 退出」 | PinCropWindow.cs | 选中后 HudHintText 含「Esc 退出」 |

W-1 与 W-2/W-3 触及不同文件可并行；W-3 与 W-4/W-5 都改 .xaml，若同期只能串行（同一文件避免冲突的话 W-3 改 MainWindow.xaml、W-5 也改 MainWindow.xaml → 必须串行，建议 W-5 后做）。

## 与 C-1 计划的关系

1. `Record/plans/C1-visual-p0-plan.md` 的 P0-2b（AnnotationWindow 深色资源抽取）与「离屏窗 Mica 风险」两条**作废**（对象已删除），由本文件决策 2 收口。
2. C-1 计划的 P0-2a（Mica 可行性）由本文件决策 1 收口为「不引入」。
3. C-1 计划的 A-1…A-4、B-1…B-5 等条目仍有效，其执行顺序以本文件「修复切分」为准；其中 A-4（HUD）由决策 3 判为不做，仅留视觉债。
4. 建议 lead 在 C-1 计划文件顶部加一行指针「实施裁决见 Record/plans/C2-visual-decisions.md」，避免两文件冲突（由 lead 或文档单执行，本单未改 C1 文件）。

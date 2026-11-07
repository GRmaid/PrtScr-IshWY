# C 复核：六项视觉修复的独立核对

- 单据：task-40 / C 复核（只读代码 + 门禁 + 报告，不改源码，不 commit）
- 复核基线：HEAD dc7b6a2（C 批提交 99ecf96 + 记录提交 dc7b6a2）
- 复核时间：2026-11-07 11:2x–11:3x
- 输入：Record/plans/C2-visual-decisions.md、Record/reports/C1-W1-W3-W4-W5.md、Record/reports/C1-W2-W6.md、C 批触及的 XAML/CS 文件
- 结论：C 可放行（C-W1…W-6 全部落地，代码 0 项返工）。唯一非代码问题是任务书第 4 条把复制路径的 AnnounceCopyStatus 写成了 AnnounceStatus；按 C2 原判据（AnnounceStatus 不得被复制路径调用）通过，按任务书字面不成立，建议 lead 更正该条措辞后收单，不需要返工。

## 一、做了什么

1. 只读核对 C2 修复切分表（W-1…W-6）与两份 C1 实现报告，逐条映射到当前 HEAD 源码。
2. 对六项修复逐项取证：4 个危险按钮与贴图卡四按钮的 AutomationProperties.Name；四份 XAML 的 Opacity 残留；HistoryWindow 副标题单行与代码文案；OcrResultWindow 的 CopyStatusText/AnnounceCopyStatus/StatusText 分工；快捷键页三卡结构与七张 Settings*HotkeyText 卡、可编辑表格；PinCropWindow 选中提示。
3. 跑三条门禁：整包 build（OutDir 隔离，绕开运行实例的默认 bin）、Core.Tests、dotnet format whitespace。
4. 本单只新增本报告；未改任何源码、未改 csproj/Directory.Build.props、未新增依赖、未 commit、未跑 --smoke、未启动/操作 GUI。

## 二、证据

### 2.1 门禁

| 门禁 | 命令（摘要） | 结果 |
| --- | --- | --- |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag c40 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-c40/" | EXIT=0；0 个警告，0 个错误；已用时间 00:00:44.97；BUILD-GUARD: EXIT=0 elapsed=46s tag=c40 |
| 单元测试 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag c40test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | EXIT=0；失败 0，通过 268，已跳过 0，总计 268；持续时间 358 ms；BUILD-GUARD: EXIT=0 elapsed=3s tag=c40test |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | EXIT=0，无输出（rc=0） |

门禁线程说明：复核开始时 git status 只有未跟踪 .tmp/ 与 主人修改素材/；复核过程中 maid-impl 的 H1 并行改动开始出现（M Record/ProjectLog.md、M src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs、M src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs、?? Record/plans/H1-tab-drag-fix.md）。C 批 8 个源码文件在最终工作树中仍无未提交改动（git status --porcelain -- 上述 C 文件为空，输出 C_BATCH_STATUS_END）。因此本次门禁是 C 批提交 + 当时工作树的现场结果，不是冻结树；若 lead 要求在 H1 合并后复核，需在新树上重跑。

最终 HEAD 与工作树（复核结束时刻）：

    dc7b6a2 docs(record): C 批次验收、门禁、提交与收工记录
    99ecf96 feat(ui): C 批次视觉收口——可访问名与逐卡命名、文字画刷统一、历史副标题单行、OCR 复制状态分离、快捷键页三段化、裁剪提示补 Esc
     M Record/ProjectLog.md
     M src/PrtScr.App/Views/CaptureOverlayWindow.Mode.cs
     M src/PrtScr.App/Views/CaptureOverlayWindow.Selection.cs
    ?? .tmp/
    ?? Record/plans/H1-tab-drag-fix.md
    ?? 主人修改素材/

### 2.2 第 1 条：AutomationProperties.Name（通过）

四个危险按钮（均为 DangerButtonStyle，名称与内容对应）：
- src/PrtScr.App/MainWindow.xaml:1184：ClearHistoryButton，AutomationProperties.Name="清空全部截图历史（会先确认，不可恢复）"；Content="清空全部历史…" 在 :1183。
- src/PrtScr.App/Views/HistoryWindow.xaml:272：删除…，Name="删除选中的截图（会先确认，不可恢复）"；Content 在 :271，DangerButtonStyle 在 :274。
- src/PrtScr.App/Views/HistoryWindow.xaml:279：清空分类…，Name="清空当前分类的全部截图（会先确认，不可恢复）"；Content 在 :278，DangerButtonStyle 在 :281。
- src/PrtScr.App/Views/PinManagerWindow.xaml:47：全部关闭，Name="关闭全部贴图窗口（不删除图片文件）"；Content 在 :46，DangerButtonStyle 在 :49。

贴图卡 DataTemplate：
- src/PrtScr.App/Views/PinManagerWindow.xaml:74 仍是 DataTemplate x:DataType="local:PinCardView"。
- 四按钮 :146/:154/:162/:170 分别为 AutomationProperties.Name="{x:Bind CopyCaption}"、"{x:Bind SaveCaption}"、"{x:Bind OcrCaption}"、"{x:Bind CloseCaption}"。
- src/PrtScr.App/Views/PinCardView.cs:36/39/42/45 四个 get-only 属性分别拼「复制：」「另存：」「识别：」「关闭：」+ Caption；Caption 定义在 :30。

结论：4 个危险按钮 + DataTemplate 内 4 个逐卡名称全部命中，读屏不会再只念省略号内容。

### 2.3 第 2 条：文字类 Opacity 归零（通过）

- grep "Opacity="0." 在 src/PrtScr.App 的 XAML 中只有 1 处：MainWindow.xaml:89 Opacity="0.25"。
- 该处上下文：MainWindow.xaml:85 注释「自定义背景层：设置里的背景图铺在最底下」，:86-89 是 x:Name="BackgroundImage" 的 Image，Stretch="UniformToFill"、Opacity="0.25"。这是背景图的功能透明度，不是文字；理由成立。
- 另有一处 Opacity="0" 在 src/PrtScr.App/Views/CaptureOverlayWindow.xaml:242，是 1×1 的隐形 FocusSink 按钮（:237-244），功能位，不是文字且不是 0.x；不影响「文字类 Opacity=0.x 归零」的判据。
- 四份 C 批 XAML 的文字类次级色均已改为 TextFillColorSecondaryBrush（C1-W1-W3-W4-W5.md §2.3 计数 36 处）；本复核只按最终树 grep 复核，不重复实现报告的历史计数。

结论：文字类 Opacity=0.x 为 0；唯一 0.25 是背景图功能位，理由成立。

### 2.4 第 3 条：HistoryWindow 副标题（通过）

- src/PrtScr.App/Views/HistoryWindow.xaml:26-34 的 SubtitleText：FontSize=12、Foreground={ThemeResource TextFillColorSecondaryBrush}、TextWrapping=Wrap、MaxLines=1、TextTrimming=CharacterEllipsis、Text="每类最多 30 张 · 仅存本机"、ToolTipService.ToolTip 承接「按截图模式 / 进程分类；每类保留张数可在设置 → 截图历史里调整，图片只保存在本机 data/history」的完整说明。
- src/PrtScr.App/Views/HistoryWindow.xaml.cs:280：SubtitleText.Text = "每类最多 {CaptureHistoryService.PerCategoryLimit} 张 · 仅存本机"; 代码模板与 XAML 初值一致，配额变化时由 UpdateSummary 覆盖。
- 说明：C2 实现建议写的是 TextWrapping="NoWrap"，当前保留 Wrap + MaxLines=1；在 WinUI 中 MaxLines 需配合换行才生效，保留 Wrap 是让单行限制实际生效，不构成返工；实际省略位置/窄窗观感需主人真机确认。

结论：MaxLines=1、CharacterEllipsis、代码文案三项全部命中，通过。

### 2.5 第 4 条：OCR 复制状态分离（实现通过；任务书字面有措辞冲突）

- CopyStatusText 存在：src/PrtScr.App/Views/OcrResultWindow.xaml:23-28，x:Name="CopyStatusText"，FontSize=12、TextFillColorSecondaryBrush、TextWrapping=Wrap、AutomationProperties.LiveSetting="Polite"；StatusText 在 :15-21，EngineText 在 :22。
- 复制路径四个反馈点全部写独立 CopyStatusText，且均为 AnnounceCopyStatus：
  - :222 AnnounceCopyStatus("正在复制…")
  - :240 AnnounceCopyStatus("复制失败：剪贴板可能被其他程序占用，请稍后再试")
  - :242 AnnounceCopyStatus("已复制点击时的文本；之后的修改还没有复制")
  - :244 AnnounceCopyStatus("已复制到剪贴板（N 个字符）")
- AnnounceCopyStatus 定义在 :180-185：只写 CopyStatusText.Text，并对 CopyStatusText 的 AutomationPeer 发 LiveRegionChanged；AnnounceStatus 定义在 :172-177：只写 StatusText.Text，并对 StatusText 发 LiveRegionChanged。
- AnnounceStatus 的调用点只有构造期 :163 AnnounceStatus(_baseStatus)；复制路径没有 AnnounceStatus。StatusText 的成功文案在 :143 为 "识别完成 · 用时 {elapsedMs} ms"；未识别到文字 :150 与识别失败 :156 是既有状态，不是复制反馈；编辑后 :197-201 追加 "· 已编辑，复制会使用当前文本" 也是既有逻辑。
- Closed 清理 :127 清空 CopyStatusText，和 StatusText/EngineText 的清理一致。

需要 lead 注意的判据冲突：C2 原判据（Record/plans/C2-visual-decisions.md:105）是「AnnounceStatus 不再被复制路径调用」，实现完全符合；task-40 第 4 条写成「AnnounceStatus 调用点在复制路径（截取/复制/另存等四处）」，按字面不成立。实际应写成「AnnounceCopyStatus 在复制路径四处」或保留 C2 的「AnnounceStatus 不在复制路径」。这是验收文本问题，不是代码缺陷；建议 lead 更正判据后收单。

结论：CopyStatusText 命中；复制路径四处反馈调用正确；StatusText 保持识别状态；按 C2 原判据通过，按 task-40 字面措辞不通过（仅措辞）。

### 2.6 第 5 条：快捷键页结构（通过）

- MainWindow.xaml 中「完整快捷键目录」grep 0 命中（本复核直接 grep 源文件；obj 旧生成副本不计）。
- :745 标题为「取景、贴图与历史内固定键」；:746-830 是三张 SettingsCardStyle 卡：:758「取景器」、:787「贴图与裁剪」、:807「截图历史」。原十行目录表位置已不再有该表。
- 七张 Settings*HotkeyText 仍存在且各 1 处：:677 SettingsScreenshotHotkeyText、:687 SettingsFullHotkeyText、:697 SettingsPinHotkeyText、:707 SettingsOcrHotkeyText、:717 SettingsWindowPrevHotkeyText、:727 SettingsWindowNextHotkeyText、:737 SettingsModeHotkeyText（共 7）。
- 可编辑表格未被删：MainWindow.xaml:350-649 仍是七行热键表，表头 :357-361 为操作/当前组合键/修改/删除/重置；每行都有 HotkeyText + Record/Clear/Reset 三个按钮（例如截图行 :363-402）。七张 Settings* 卡位于 :673-742，是表格下方的显示卡；二者不是同一组控件，但都保留。
- 说明：组合键文本由 MainWindow.Hotkeys.cs:376-388 SetSimpleHotkeyText 在运行时写入（:380-386 七个 Settings*HotkeyText），因此 XAML 内 PrintScreen/F1 0 命中是预期；本复核不再使用该计数判据。

结论：完整快捷键目录 0 命中、三卡结构、七张 Settings* 卡、七行可编辑表格全部通过。

### 2.7 第 6 条：PinCropWindow 选中提示（通过）

- src/PrtScr.App/Views/PinCropWindow.cs:330 UpdateVisuals；无选区 :346 为「拖拽 框选 · Esc / 右键 退出」；有选区 :350 起走选中分支，:407-408 在 ActionBar 可见时写 HudHintText.Text = "Enter 贴图 · O 识别 · Ctrl+S 保存 · Esc 退出"。
- 因此选中后提示明确包含「Esc 退出」，通过。

### 2.8 第 7 条：门禁与结论

三条门禁全绿（见 2.1）。C 批 8 个源文件在最终工作树无未提交改动；C 批提交 99ecf96 已包含本单核对的全部六项修复。结论见开头：C 可放行，0 项代码返工；第 4 条验收措辞需更正。

## 三、进行中（交接用）

- 已完成：六项修复逐条取证；三条门禁；工作树/HEAD 记录；结论。
- 本单没有源码动作，报告完成后即可收单。
- 下一步第一件事（给 lead）：把 task-40 第 4 条判据的 AnnounceStatus 改成 AnnounceCopyStatus（或恢复 C2 的「AnnounceStatus 不在复制路径」），然后把 C 批交主人按下方实测步骤手测。
- 下一步该读的文件：本报告；Record/plans/C2-visual-decisions.md（C2 原判据）；Record/reports/C1-W1-W3-W4-W5.md 与 C1-W2-W6.md（实现细节）；git show 99ecf96 -- 上述 8 个 C 文件（若需逐行 diff）。
- 本单未触碰：任何源码、csproj/Directory.Build.props、.gitignore/README、NuGet；未 commit；未跑 --smoke；未操作 GUI。

## 四、风险与遗留

1. 全部判据是静态 grep/编译/测试级；未开 GUI、未开讲述人、未切浅色/深色/高对比度主题，也未在窄窗/150% DPI 下看副标题省略位置。视觉与读屏效果仍需主人真机确认。
2. 门禁不是冻结树结果：复核过程中 H1 并行改动出现（CaptureOverlayWindow.Mode.cs/Selection.cs、Record/ProjectLog.md、H1 计划），C 批文件本身未改。若 lead 要求以冻结树验收，需在 H1 合并后重跑三条门禁。
3. 第 4 条验收文本与 C2 原判据相反（AnnounceStatus vs AnnounceCopyStatus）。代码按 C2 实现；若不改判据，字面验收会误报不通过。
4. W-2 既有风险仍在：复制成功反馈不会随文本编辑自动清空；StatusText 会追加「已编辑，复制会使用当前文本」，两者可能同时出现。属 C1-W2-W6 报告已列风险，不是本单六项修复的回归。
5. HistoryWindow 的 SubtitleText 保留 TextWrapping=Wrap（C2 建议 NoWrap），这是 MaxLines=1 生效所需；若未来有人改成 NoWrap，单行/省略判据会失效。
6. C1-W1-W3-W4-W5 报告记录的 PinManagerWindow.xaml 行尾为 LF、与其余三份 XAML 不一致，本单未处理；dotnet format 只覆盖 C#，该行尾问题不影响本单门禁，但仍是仓库整洁遗留。

## 五、待确认

1. 请 lead 确认第 4 条按 C2 原判据（AnnounceStatus 不在复制路径）验收，并把 task-40 的措辞改为 AnnounceCopyStatus；本报告按「实现通过、字面措辞有误」记录，未自行改任务。
2. 是否需要在本轮 H1 改动合并后，对 C 批再做一次冻结树门禁；本单已给出当前现场数字，但不宣称冻结。
3. 主人是否接受 W-2 复制反馈不随编辑清空（C1-W2-W6 风险 1），还是另开小单在 TextOutput_TextChanged 清空 CopyStatusText；不影响本次 C 可放行结论。

## 主人实测步骤

1. 打开设置 → 快捷键页，滚到「取景、贴图与历史内固定键」：应看到取景器 / 贴图与裁剪 / 截图历史三张卡；页面内不应再有「完整快捷键目录」十行表；上方「按键一览」七张卡与七行可编辑热键表（修改/✕/↻）仍在。
2. 打开截图历史窗，把窗口拉窄或把 DPI 调到 150%：顶部副标题应保持一行、过长以「…」省略；悬停能看到含分类说明与本机保存位置的完整 ToolTip；浅色/深色主题下副标题是次级灰而不是整体半透明。
3. 开讲述人（Win+Ctrl+Enter），聚焦主窗「清空全部历史…」、历史窗「删除…」「清空分类…」、贴图管理器「全部关闭」：应分别读出「清空全部截图历史（会先确认，不可恢复）」等完整名称；再聚焦贴图卡四个按钮，名称应带该贴图 Caption 且随卡片变化。
4. 打开 OCR 结果窗，复制一次：标题行应仍是「识别完成 · 用时 N ms」，复制反馈出现在其下方独立的 CopyStatusText；再改一下文本，确认标题行只追加「已编辑，复制会使用当前文本」，复制反馈不会覆盖识别状态。
5. 打开贴图裁剪，拖出选区：底部提示应为「Enter 贴图 · O 识别 · Ctrl+S 保存 · Esc 退出」；按 Esc 应退出裁剪。再切浅色/深色/高对比度看快捷键页与历史窗的次级文字是否清晰。

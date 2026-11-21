# C-残1 XAML 卡片分组与键帽（P1-1 / P1-3 / P2-3）

单子：C-残1　写域：`src/PrtScr.App/MainWindow.xaml`（唯一被改的产品文件）
判据：`Record/reports/C-遗留精确盘点.md`
结果：三处改动全部落地；文件 1308 → 1358 行、CRLF、无 BOM、末尾换行；未跑 dotnet build（按单子留女仆长串行复跑）。

## 做了什么

1. **P1-1 设置页统一卡片分组**：四个裸控件页的小节各自包进 `<Border Style="{StaticResource SettingsCardStyle}"><StackPanel Spacing="8">…</StackPanel></Border>`，共新增 9 张卡。
   - 截图行为页 4 张（截图热键档位 / 常规取景起始模式 / 延时与指针 / 选区边框颜色）。原「延时与指针」区里散落的延时标签、`DelayCombo`、`CaptureCursorToggle`、`GameFullscreenQuickToggle`、`CaptureOptionsText` 收进同一张卡。
   - 提示页 3 张（外观与位置 / 时长 / 提示时机与文字）；「外观与位置」卡内保留原有横向 `StackPanel Spacing="24"` 作为卡片子项，未拆散两列布局。
   - 历史页 1 张（整页身体）、启动页 1 张（整页身体）。这两页原本没有 `SectionTitleStyle` 小节标题，未凭空新增标题。
2. **P1-3 小节标题上边距**：`SectionTitleStyle` 的 `Margin` 由 `0,10,0,0` 改为 `0,16,0,0`（按盘点报告 m00003 拍板的 16px，不取建议上限 24px）。
3. **P2-3 目录表键帽化**：目录表三张卡内第一列按键 `TextBlock` 全部包进 `HotkeyChipStyle`，共 11 个；第二列说明文字、`Grid` 行列定义、说明文字属性一概未动。

约束遵守：未改任何 `x:Name`、事件处理器、绑定、`Header`/`Content`/文案；未合并、删除或新增任何控件；未改 `SectionTitleStyle` 的标题层级结构；未动 `.gitignore`/README/git。

## 证据（改动行号与片段）

文件：`src/PrtScr.App/MainWindow.xaml`（行号为改后行号，文件共 1358 行）

### P1-3 小节标题上边距

```xml
24: <Style x:Key="SectionTitleStyle" TargetType="TextBlock">
25:     <Setter Property="FontSize" Value="16" />
26:     <Setter Property="FontWeight" Value="SemiBold" />
27:     <Setter Property="Margin" Value="0,16,0,0" />
28: </Style>
```

### P1-1 截图行为页（CaptureSection，L858 起）

```xml
876: <TextBlock Text="截图热键档位" Style="{StaticResource SectionTitleStyle}" />
877: <Border Style="{StaticResource SettingsCardStyle}">
878:     <StackPanel Spacing="8">
879:         <ComboBox
880:             x:Name="CaptureProfileCombo" … />
888:         <TextBlock x:Name="CaptureProfileHintText" Style="{StaticResource HintTextStyle}" />
889:     </StackPanel>
890: </Border>
```

| 小节 | 卡片行号 | 卡内主要内容 |
| --- | --- | --- |
| 截图热键档位 | L876-890 | `CaptureProfileCombo` + `CaptureProfileHintText` |
| 常规取景起始模式 | L892-906 | `DefaultModeCombo` + `CaptureModeHintText` |
| 延时与指针 | L908-931 | 「延时截图」标签 + `DelayCombo` + `CaptureCursorToggle` + `GameFullscreenQuickToggle` + `CaptureOptionsText` |
| 选区边框颜色 | L933-974 | `SelectionColorFollowThemeCheckBox` + `SelectionColorPreview` + `SelectionColorBox` + `SelectionColorPresetCombo` + `SelectionColorHintText` |

### P1-1 提示页（ToastSection，L1064 起）

| 小节 | 卡片行号 | 说明 |
| --- | --- | --- |
| 外观与位置 | L1070-1108 | 保留原横向 `StackPanel Spacing="24"`（字号列 / 材质与不透明度列）作为卡片子项 |
| 时长 | L1110-1149 | `ToastVisibleMsBox` / `ToastFadeInMsBox` / `ToastFadeOutMsBox` |
| 提示时机与文字 | L1151-1184 | 三个 `ToggleSwitch` + 提示文字 `TextBox` + 两个按钮 + `ToastSettingsHintText` |

### P1-1 历史页与启动页（整页单卡）

```xml
1188: <StackPanel x:Name="HistorySection" Style="{StaticResource SettingsPageStyle}" Visibility="Collapsed">
1189:     <TextBlock Text="截图历史" Style="{StaticResource PageTitleStyle}" />
1190:     <Border Style="{StaticResource SettingsCardStyle}">
1191:         <StackPanel Spacing="8">
…
1235:     </Border>
1236: </StackPanel>

1239: <StackPanel x:Name="StartupSection" Style="{StaticResource SettingsPageStyle}" Visibility="Collapsed">
1240:     <TextBlock Text="启动与托盘" Style="{StaticResource PageTitleStyle}" />
1241:     <Border Style="{StaticResource SettingsCardStyle}">
1242:         <StackPanel Spacing="8">
1243:             <ToggleSwitch x:Name="StartupToggle" … />
1247:             <TextBlock x:Name="StartupStatusText" Style="{StaticResource HintTextStyle}" />
1248:             <TextBlock Text="关闭窗口只会收进托盘；要彻底退出，请用托盘右键菜单里的「退出」。" … />
1251:         </StackPanel>
1252:     </Border>
1253: </StackPanel>
```

### P2-3 目录表键帽（L744 标题「取景、贴图与历史内固定键」下的三张卡）

范式（第一张卡首个键帽）：

```xml
770: <Border Style="{StaticResource HotkeyChipStyle}">
771:     <TextBlock Text="Tab" FontFamily="Consolas" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
772: </Border>
773: <TextBlock Grid.Column="1" Text="在窗口截图与常规截图之间切换" FontSize="12" TextWrapping="Wrap" />
```

| 卡片 | 键帽行号 | 按键 |
| --- | --- | --- |
| 取景器（L755） | 770 / 774 / 778 / 782 / 786 | Tab · 滚轮 / 回车 · O / Ctrl+S · Ctrl+C · Esc / 右键 |
| 贴图与裁剪（L794） | 806 / 810 | Ctrl+Z / Ctrl+S · 回车 / Esc |
| 截图历史（L818） | 832 / 836 / 840 / 844 | 双击 / 右键 · Ctrl+A · Delete · Esc |

`Grid.Row` 仍写在键帽内的 `TextBlock` 上（如 L775 `Grid.Row="1"`），`Border` 不参与行列定位，故 `TextBlock` 的原行号归属与三行 RowDefinitions 均未变。

### 自检数据（实测，非推算）

- XML 解析：`XML_OK root=Window class=PrtScr.App.MainWindow`（PowerShell `XmlDocument.Load`）。
- 标签配平：`Border` open=43 / selfClose=2 / close=41；`StackPanel` open=57 / close=57。
- 控件与事件零丢失：`x:Name` 116 → 116 完全一致；事件处理器 81 → 81 完全一致；`Text`/`Content`/`Header`/`PlaceholderText`/`AutomationProperties.Name` 取值集合 240 → 240 完全一致（改前备份 vs 改后文件，多重集合比对，两侧差集均为空）。
- 样式引用：`SettingsCardStyle` 15 → 24（+9）、`HotkeyChipStyle` 4 → 15（+11）、`SectionTitleStyle` 13 与 `SettingsPageStyle` 10 不变。
- 编码/行尾：CRLF 1358 行、孤立 LF 0、行尾空白 0、首字节 `60,63,120`（无 BOM）、末尾换行保留。
- 改动前备份：`E:\Explore\GoodGood\PrtScr\.tmp\MainWindow.xaml.before-c1`。

## 风险与遗留

- **未跑 dotnet build**：按单子约定 build 与静态核对由女仆长串行复跑；本轮只做到 XML 可解析、标签配平、控件/事件/文案集合零变化这一层。XBF 编译期错误（例如某处 `StackPanel` 属性顺序或`StaticResource` 拼写）本机未被排除，属可执行验收项。
- **截图行为页顶部「恢复默认」按钮行（L859-874）未包卡**：它是整页级操作按钮，跟着 `PageTitleStyle` 标题同行，不是可分组设置项；盘点报告也未把它列为裸控件小节。若要求「页内零裸控件」，需再拍板。
- **目录表第三张卡（截图历史 L818）也做了键帽化**：单子点名的是标题下两个 `SettingsCardStyle` 卡片，但该表标题写作「取景、贴图与历史内固定键」，三张卡同属一表；只改前两张会留下半张表无键帽。此处按整表一致性处理，已如实记录，属自觉判断而非单子明文要求。
- **历史页/启动页以整页单卡承载**：两页原本只有 `PageTitleStyle` 标题、没有任何 `SectionTitleStyle` 小节；未新增小节标题（单子禁止改标题层级），因此一张卡包住全部内容。视觉上与其他页「每小节一卡」不完全同构，但保留了原信息层级。
- 缩进：卡片子项按原缩进 +4 / +8 分别重排（被替换的内层 `StackPanel Spacing="4"` 子项原本已在 28，只 +4），已逐段读回核对，无 36 这类过缩进。

## 待确认

1. 截图行为页顶部「恢复默认」按钮行是否也要包卡，还是维持页头操作条？
2. 目录表第三张卡（截图历史）的键帽化是否保留？若只认前两张，可单独回退该卡 4 个键帽。
3. 提示页「外观与位置」卡内两列横向排布在小窗宽度下是否偏挤（卡片 Padding 12 会吃掉约 24px 可用宽度）；若主人在窄窗下觉得挤，可改为竖向 `StackPanel`——本轮未擅自改布局。

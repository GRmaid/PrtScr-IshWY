# C 遗留精确盘点（四项 P1 + 九项 P2 权威逐条清单）

- 产出：lead，2026-11-21。目标 = m00311「先把遗留的 C 解决了吧」。
- 权威口径来源：docs/HANDOFF-2026-11-10.md §5.2 与 Record/plans/2026-10-28-整改总纲.md §12.2 只说「四项 P1 与九项 P2 仍待处置」，仓库此前无逐条清单；本文件补齐，并逐条给 文件:行 证据。
- 基线：HEAD 5e74f62（main，已推送）；工作树 dirty 126 项（原 121 + 11-21 热键修复等）。
- 排除项：P1-18（结构不存在，task-37 已删 AnnotationWindow，标注并入 NativePinWindow GDI 自绘）与 P1-16（已实现）不列。

## 一、四项 P1（残余，逐一可 grep 定位）

| # | 项 | 现状 | 证据（文件:行） | 剩余动作 | 拍板 |
| --- | --- | --- | --- | --- | --- |
| P1-1 | 设置页统一卡片分组 | ✅ 已修复（C-残1） | 截图行为/提示/历史设置/启动四页共 9 张 SettingsCardStyle 卡片；页头「恢复默认」操作条不包卡 | 4 页包 SettingsCardStyle | 已拍板做，已交付 |
| P1-3 | 小节标题上边距 | ✅ 已修复（C-残1） | SectionTitleStyle MainWindow.xaml:27 Margin="0,16,0,0"（拍板 16px） | 上边距 10→16 | 已拍板 16px，已交付 |
| P1-14 | 取景器 HUD 底色 #E6→#F2 | 🔒 记债不做（V-1） | CaptureOverlayWindow.xaml :51/:76/:145/:177/:191 五处 #E6202020 未改（裁剪窗 PinCropWindow.xaml:56 已 #F2） | C2 决策3=本轮不做，记视觉债 V-1 | 已拍板不做 |
| P1-15 | 主页突出主操作 | 🔒 记债不做 | ActionCardButtonStyle 四卡 MainWindow.xaml:195/:222/:244/:267 同权重，无主次 | C2 R-8 待主人裁定 | 已拍板不做 |

## 二、九项 P2（权威逐条，源自建议文档 §5 :157-161 四组）

| # | 项 | 现状 | 证据（文件:行） | 剩余动作 | 拍板 |
| --- | --- | --- | --- | --- | --- |
| P2-1 | 主页突出主操作 | 🔒 记债不做 | = P1-15 | 同 P1-15 | 已拍板不做 |
| P2-2 | 压缩卡片高度 112→88 | 已实现 | ActionCardButtonStyle MainWindow.xaml:15 MinHeight=88 | drop | — |
| P2-3 | 目录表改键帽样式 | ✅ 已修复（C-残1） | 目录表三卡 11 个按键 TextBlock 包 HotkeyChipStyle（含第三张「截图历史」卡，整表一致） | 目录表键帽包 HotkeyChipStyle | 已拍板做，已交付 |
| P2-4 | 去掉重复「按键一览」 | 已实现 | W-5：目录表标题 :744 改「取景、贴图与历史内固定键」，只留内部键，:655-656 补说明 | drop | — |
| P2-5 | 标注「取消」按条件变色 | ✅ 已修复（C-残2） | NativePinWindow.EditRender.cs:250 `item.Id == 23 && HasPendingAnnotations ? cancelBrush : commandBrush`，复用 HasPendingAnnotations（EditHistory.cs:26→EditDocument.HasPendingWork） | 无未保存标注时用中性色 | 已拍板做，已交付 |
| P2-6 | 标注状态行溢出 | 需真机确认 | DrawToolbarMeta EditRender.cs :305-347 已有三级缩短 + :310 空间不足隐藏（<48px return），理论不超工具栏底 | 真机宽贴图确认 | 真机 |
| P2-7 | 标注尺寸标签位置 | 🔒 记债不改 | EditRender.cs :66-71 标签 "{宽}×{高}" 画在左上角 (6,6)，压贴图左上内容 | 改位置（贴图固有语义） | 已拍板不改 |
| P2-8 | HUD 青色标签统一 | 🔒 记债不做 | = V-1（决策3），青色 #FF00C8FF/:30、#FF8FE3FF/:61/:206/:212 承担悬停/进程名/模式三语义 | 记债，不升做 | 已拍板不做 |
| P2-9 | 固定淡紫品牌色 | 未引入 | 全仓无品牌紫常量；强调色已走 Accent 主题资源 | 可选，不引入 | 否（建议不做） |

> 补充：建议文档 §3 各页另有未被 §5 收纳的 P2 tag（:105 右键菜单标快捷键、:113-115 裁剪 HUD 底色/中心提示底板/青色标签），其中「裁剪 HUD #E6→#F2」与 P1-14 同源、「青色标签」与 P2-8 同源，中心提示底板已实现（PinCropWindow.xaml:33-38）；「右键菜单标快捷键」为独立小项，随 P2-9 一并列为可选，不在九项 P2 主清单内。

## 三、W-1…W-6 落地核对（本轮复核，全部已落地，从遗留清单排除）

| 单 | 内容 | 落地证据 |
| --- | --- | --- |
| W-1 | 危险按钮可访问名 | MainWindow.xaml:1183「清空全部截图历史（会先确认，不可恢复）」；HistoryWindow.xaml:272/:279；PinManagerWindow.xaml:47「关闭全部贴图窗口（不删除图片文件）」+ :146/:154/:162/:170 四卡逐卡 {x:Bind ...Caption} |
| W-2 | OCR 状态分离 | OcrResultWindow.xaml:24 CopyStatusText；OcrResultWindow.xaml.cs:127/182/183 |
| W-3 | 次级文字画刷 | inline `Opacity="0.` 全仓仅剩 MainWindow.xaml:88（背景图功能值 0.25，非文字） |
| W-4 | 副标题单行 | HistoryWindow.xaml:31 MaxLines=1、:32 TextTrimming=CharacterEllipsis、:33 文案「每类最多 30 张 · 仅存本机」、:34 ToolTip 兜底 |
| W-5 | 快捷键去重 | MainWindow.xaml:744 目录表标题改「取景、贴图与历史内固定键」 |
| W-6 | 裁剪提示补退出 | PinCropWindow.cs:408 文案「Enter 贴图 · O 识别 · Ctrl+S 保存 · Esc 退出」 |

## 四、编制表（获批后派单，并发≤3、编制≤5，产品源码派子代理）

| 单 | 女仆 | 写域 | 内容 |
| --- | --- | --- | --- |
| C-残1 | 女仆A | src/PrtScr.App/MainWindow.xaml | P1-1（4 页包 SettingsCardStyle）+ P1-3（上边距）+ P2-3（目录表键帽） |
| C-残2 | 女仆B | src/PrtScr.App/Views/NativePinWindow.EditRender.cs | P2-5（取消按条件变色） |

- C-残1 与 C-残2 不同文件，可并行（并发 2 ≤ 3）。
- P1-15 / P1-14 / P2-7 待主人拍板后追加；P2-6 只真机确认；P2-2/P2-4/P1-16 已实现 drop；P2-8/P2-9 建议不做。

## 五、拍板结果（主人 2026-11-21 14:32 确认按推荐默认走，派 C-残1 + C-残2，其余记债）

1. **P1-15 主页四卡分主次**：✅ 已拍板 **不做**（四动作都是高频入口，硬造主次会误导）。
2. **视觉债 V-1（P1-14 取景器 HUD 底色 + P2-8 青色标签）**：✅ 已拍板 **维持不做**（遮罩语义，升做需整体重做取景器配色）。
3. **P1-3 上边距口径 + P2-7 尺寸标签位置**：✅ 已拍板 **P1-3 用 16px**（24 会明显拉长使用点页面）、**P2-7 不改**（贴图尺寸标签是固有语义）。

## 六、真机验收（仅主人本人，代理禁代测）

- 视觉三主题（浅/深/高对比度水生+沙漠）× 三 DPI（100/125/150%）逐页走查。
- 讲述人朗读危险按钮可访问名与 OCR 复制结果（LiveSetting）。
- 仅 Tab/Enter/Space 走完每页。
- P14c 历史元数据 v2 JSON 与视觉一致。

## 七、本轮交付与验收（2026-11-21）

- 派单：C-残1（maid-c1-xaml，改 MainWindow.xaml）+ C-残2（maid-c2-cancel，改 NativePinWindow.EditRender.cs），并行。
- 验收：lead 亲自串行复跑三绿门禁——build 0 警 0 错 EXIT=0（25s）、Core.Tests 417/417、format EXIT=0；静态核对 Margin 0,16,0,0、SettingsCardStyle 24 处、HotkeyChipStyle 15 处、HasPendingAnnotations 1 处均落地。
- 关键澄清：git diff 中托盘块（xmlns:tb + tb:TaskbarIcon 20 行）消失是 P14b′ 既有代码接管（MainWindow.Windows.cs:203-244 CreateTrayIcon），非本单删除；C-残1 留的 .tmp/MainWindow.xaml.before-c1（1308 行）在派单前已无托盘块。
- 遗留：真机视觉/讲述人/键盘/历史 JSON 验收待主人本人（§六）；P2-6 状态行溢出仅真机确认；未提交/未打 tag。
- 报告：Record/reports/c1-xaml-card.md、Record/reports/c2-cancel-conditional.md。

# D-2B 贴图持久化设计摘要（task-43）

- 设计稿：Record/plans/D2B-pin-persistence-design.md（25669 字节，八节齐备）。
- 结论：做「应用重启恢复 + 稳定状态持久化」；存程序目录 data/pins，index.json + 版本化 PNG，标注只存已合成 flattened PNG，不做可再编辑文档。
- 关键取舍：PinRecord 增 Id 与 EditRevision；PublishPinState 单入口 + PinClosed 事件；单写者队列 750ms 去抖、编辑/导航立即写、退出前 1s Flush；上限 50 张/128 MiB，超限只停写不删图。
- 未决 5 条：标注可再编辑（推荐否）、导航持久化（否）、隐藏/穿透原样恢复（是）、pinPersist 开关（默认 true）、配额超限不删（推荐）。
- 门禁（2026-11-07 11:41）：HEAD dc7b6a2；工作树含同事未提交的 H1/历史元数据改动（M MainWindow.Capture.cs、CaptureHistoryService.cs、CaptureOverlayWindow.*、NativePinWindow.Interaction.cs 等）；build-guard d2b 整包 0 警 0 错 46.93s、Core.Tests 305/305（252ms）、format rc=0。

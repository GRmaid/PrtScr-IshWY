# B-1 CaptureSession / CaptureResult 设计（task-19）· 三行摘要

- 做了什么：新增设计草案 `Record/plans/B1-capture-session-design.md`（23,031 字节，五节齐备）：现状清单（热键到落历史全链 + 14 个状态变量的声明/写点/读点/线程 + 5 类现存缺陷）、类型草案（Core 枚举与 CaptureRules、Capture 层 CaptureResult、App 层 CaptureSession + 状态机 + 8 条不变量）、迁移三步（每步独立验收判据与风险）、边界裁决（取消唯一定义、12 行结果矩阵、P-04b 漏斗上升为类型规则、提交点）、8 个未决问题。
- 验证了：只读核对当前工作树（HEAD 0b1ca41 + A 批次未提交改动），所有现状条目带 文件:行；未改任何产品代码，未跑 GUI/--smoke/构建（构建裁决按纪律归 lead 同 SHA）。
- 风险与未做：设计未实现、未编译；编号口径与总纲 B-1=EditDocument 不同，已在文首声明；8 项未决需 lead/主人拍板（尤其 overlay 持有关系、快捷全屏是否会话化、历史元数据 source 字段、是否新建 Capture.Tests）。
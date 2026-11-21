---
title: IshWY｜默认产物已更新，Tab 等待真实拖拽验收
lang: zh
layout: sheet
theme: paper
---
## 本轮结论
> [!important] 交付缺口已处理，Tab 尚不能记为修好
> 旧实例运行的 DLL 不含 H1 守卫。主人退出后，默认目录已完成整包重建。
> 独立源码复核未发现必须追加补丁的遗漏，本轮没有新增产品代码。
> 新版是否继续丢失拖拽，仍须主人真实按键验证。

- 三条 Tab 入口共用守卫。框选、移动、缩放时应忽略 Tab，并提示松手后再切换。
- 旧包只证明交付滞后，不能证明 H1 有效，也不能把用户反馈称为假失败。
- 本轮子代理均使用 DeepSeek-V4.1-Flash。没有运行旧 smoke、注入输入或发布版本。

## 默认产物与门禁证据
| 检查 | 结果 | 边界 |
|---|---|---|
| 完整 solution build | 0 警告、0 错误；30.83s | 写入默认运行目录 |
| Core.Tests | 305/305；67ms | 不代替 WinUI 拖拽验收 |
| format whitespace | 退出 0 | 串行门禁整体 EXIT=0 |
| 产品源码 | 与 HEAD 无差异 | H1 实现来自 52c5565 |
| 新默认 DLL | 日志与中文提示均命中 | 字符串入包不证明行为正确 |
| 实际加载与交互 | 尚未核对 | 未替主人启动或代测 |

源码基线为 5e74f6215ee0a03d2bd22ccf7a48cc44811316e7。新默认 DLL 的 SHA256：


```text
7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C
```

## 请用新版做这组验证
从下面默认目录启动程序。常规、窗口两种模式分别执行三种拖拽测试。

```text
E:/Explore/GoodGood/PrtScr/src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe
```

| 动作 | 按住左键时按 Tab 的预期 |
|---|---|
| 新建框选 | 框选继续跟随鼠标 |
| 拖动已有选区 | 整个选区继续跟随鼠标 |
| 拖边或角缩放 | 对应边角继续跟随鼠标 |

- 三种状态都应出现“松开鼠标后再按 Tab 切换模式”。
- 松手时不能补切模式；松手后另按 Tab，才恢复切换逻辑并保留选区。
- 再拖一次，Esc 退出后重开取景，确认没有鼠标捕获残留。
- 如仍停住，请反馈：哪种模式、哪种拖拽、有没有出现提示。不要只凭窗口外观判断版本。

只读核对实际加载身份可用（PowerShell 7）：

```powershell
pwsh -NoProfile -File Record/tools/H2-ArtifactProof.ps1
```
## 规划已对齐，剩余仍如实保留
| 范围 | 当前口径 |
|---|---|
| A / B 历史修复批 | 用户历史实测通过，不等于整个 A / B 阶段完成 |
| H2、P14c、C W1–W6 | 默认产物已重建；各自的 GUI / 数据 / 读屏验收仍待完成 |
| P10 / P11 | 数据隔离、可信 smoke 与同 SHA 证据链尚未实施 |
| B / C 余项 | 语义矩阵、快捷全屏统一会话等仍需逐项推进 |
| D | 贴图持久化仅设计；五项取舍待确认，M6 与 Phase 3 未实施 |

下一步先取得新版 Tab 实测结果。通过后按 P10 → P11 → B 余项 → C 剩余 → D 推进。
总纲、路线图和项目日志已对齐。所有整改目标完成前继续冻结 tag / Release。

> [!note] 记录与证据
> 规划及报告尚未提交。今日日志、白板和交接账本已更新。
> 运行产物核对流程已存为待审批技能，不等于已经激活。

完整证据与评审裁决见 [H2 运行产物证据](<E:/Explore/GoodGood/PrtScr/Record/reports/H2-artifact-evidence.md>)。
规划口径见 [整改总纲](<E:/Explore/GoodGood/PrtScr/Record/plans/2026-10-28-整改总纲.md>)、[路线图](<E:/Explore/GoodGood/PrtScr/docs/ROADMAP.md>)、[项目日志](<E:/Explore/GoodGood/PrtScr/Record/ProjectLog.md>)。

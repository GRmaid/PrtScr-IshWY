# H-2 运行产物证据（2026-11-07）

## 1. 当前结论与边界（14:00 更新）

默认运行目录已完成受控整包重建，包含 H-1 日志与提示文本；完整门禁通过。H-2 仍待主人真实拖拽验收，不记作交互已修好。

本轮未修改产品源码：HEAD 为 `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`，`git diff --quiet HEAD -- src/` 退出 0。H-1 实现来自 `52c5565`。独立复核确认三条 Tab 入口共用拖拽守卫，框选/移动/缩放均置 `_dragging`；未发现必须追加补丁的静态遗漏，因此先修交付缺口，不增加未经证实的产品改动。

## 2. 历史证据：重建前运行的不是新产物

13:35 核对时，默认实例 PID 8156，自 10:53:30 启动，运行路径为 `E:/Explore/GoodGood/PrtScr/src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe`，加载模块路径匹配该目录。用户确认已退出后，13:54 再查无 IshWY 进程，才开始重建；代理未结束用户程序。

| 属性 | 重建前默认 DLL | 本轮隔离 DLL | 重建后默认 DLL |
|---|---|---|---|
| 大小 | 966144 字节 | 967168 字节 | 967168 字节 |
| 修改时间 | 10-30 22:46:47 | 11-07 13:36:18 | 11-07 13:55:48 |
| SHA256 | `CB3218C50577FFE535CBD5A34451E3C6947F754BB6573F0101735C4C65393E8C` | `755C8D615233C3027CDDDC0A2883B2EC87553F864240538744100A0AA087EDB6` | `7FA2452A29B3D64127B658DF423D63C3560D25542D6089FE971780AB5BE4625C` |
| H-1 日志 / 提示特征 | 无 / 无 | 有 / 有 | 有 / 有 |

默认 DLL 为 `src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.dll`，隔离 DLL 为 `.tmp/out-h2baseline/IshWY.dll`。后两者 ProductVersion 均为 `1.3.1+5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`；输出目录不同，不能要求哈希相同。后续运行验收绑定默认 DLL 的哈希，不把不同 OutDir 的构建声称为同 SHA 证据。

旧产物只证明交付缺口，不能据此驳回用户反馈，也不能证明 H-1 的交互已经有效。mtime 和大小均不能替代源码/产物/实际加载身份核对。

## 3. 默认目录完整门禁（h2default）

在共享 build-guard 中顺序执行：

```text
dotnet build PrtScr.sln -c Debug --nologo
dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
```

- 整包 build：0 警告、0 错误，30.83s，默认 bin 已写入。
- Core.Tests：305/305，通过 67ms。
- format：退出 0；三项通过后 build-guard EXIT=0，elapsed=41s。
- 返修前隔离基线：0 警告/0 错误、34.76s，305/305（81ms），format 0。
- 未运行 `--smoke`，未注入输入或操作 GUI，未执行数据覆盖/清理或 HKCU 修改，未 tag/Release。

## 4. 只读身份证据脚本

`pwsh -NoProfile -File Record/tools/H2-ArtifactProof.ps1` 要求 PowerShell 7。脚本读取进程身份、DLL SHA256/版本并搜索 H-1 的英文日志与中文提示，检查两个 UTF-16 字节对齐；若有进程，还读取已加载 IshWY.dll 模块路径。

单对齐 Unicode.GetString 会漏掉奇偏移字符串。Windows PowerShell 5.1 对无 BOM UTF-8 脚本又会误解中文，导致 ToastMarker 假阴性；现已加 `#requires -Version 7.0`，使用 pwsh 7 双对齐复测，两份新 DLL 的 LogMarker/ToastMarker/H1Marker 均为 true。

13:57 复查 `Processes=[]`、`RuntimeObserved=false`：已证明磁盘产物更新，尚未证明重新启动后加载的就是新 DLL。代理未启动程序代测。

## 5. 独立复核裁决与后续实测

诊断与独立复核已交付，分别见 `Record/reports/H2-tab-drag-repair.md`、`Record/reports/H2-independent-review.md`。其中关于旧实例为“唯一阻断”或用户反馈属于“假失败”的措辞不作为最终裁决：它们仅适用于重建前的交付事实，不是新版实机行为结论。非预期 PointerCaptureLost、修饰键一致性、自动重复和拖拽中 Enter 是未验既有风险，本轮不扩范围。

1. 主人从默认目录启动 IshWY；可再运行上方只读脚本核对模块路径/启动时间/默认 DLL 哈希，不能仅凭窗口外观认版本。
2. 在常规与窗口两种模式分别验证：按住左键新建框选时按 Tab、拖动已有选区时按 Tab、拖边/角缩放时按 Tab。三态均应继续跟手，并显示“松开鼠标后再按 Tab 切换模式”。
3. 松手时不应自动切换；松手后另按 Tab 才切换并保留选区。再拖一次、Esc 退出并重开取景，确认无捕获残留。
4. 如仍停住，请记录哪种模式/哪种拖拽、是否出现提示；日志 token 为 `Overlay: mode switch ignored (dragging)`。由实际事件证据决定下一补丁，不再次仅凭编译判好。
5. P-14c 新 JSON、C 批视觉/讲述人仍未验收；H-2 通过后按总纲 P-10→P-11→B 余项→C 剩余→D 推进。

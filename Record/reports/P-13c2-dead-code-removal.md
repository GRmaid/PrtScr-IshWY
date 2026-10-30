# P-13c2 死代码清理：删除 AnnotationWindow 与 PinClipboardHelper.OpenEditor

- 单号：task-37（revision 2，owner maid-check）
- 执行时间：2026-10-30 22:2x–22:40
- 基线：HEAD 69616cc（工作树含并行任务 P-14b/P-13c 等未提交改动）
- 结论：**通过**——两个源文件与唯一入口 OpenEditor 已删除，源码树 0 残留，三门禁全绿。

## 一、做了什么

1. **删除 AnnotationWindow 全部源码**（全仓仅 2 个源文件，无其他 partial）：

| 文件 | 行数 | 字节 | git 状态 |
| --- | --- | --- | --- |
| src/PrtScr.App/Views/AnnotationWindow.xaml | 74 | 3,919 | D |
| src/PrtScr.App/Views/AnnotationWindow.xaml.cs | 768 | 27,085 | D |
| 合计 | 842 | 31,004 | |

2. **删除 PinClipboardHelper.OpenEditor**（唯一入口）：
   - 原位置 src/PrtScr.App/Views/NativePinWindow.cs:492-506（含尾随空行，15 行）。
   - 删后 PinClipboardHelper（class 声明 :482）只剩 OpenOcr（:484-490）与 Copy（:492-499）。
   - 未改 using：Windows.Graphics.RectInt32 只出现在被删方法签名（原 :493），删除后无需调整；Microsoft.UI.Dispatching 仍被 OpenOcr/Copy 使用。

3. **csproj 未改**：src/PrtScr.App/PrtScr.App.csproj 共 59 行，SDK 通配（UseWinUI），**没有显式列任何 Page/Compile 文件**；只有 PackageReference（:25-29）、ProjectReference（:31-36）、Content Include=Assets（:38-40）与 CopyPriAndXbfToPublish target（:49-57）。按单子第 2 条「通配则不动」，不改。

4. **未删任何资源键**：AnnotationWindow.xaml 内 x:Key 为 0 命中（未定义本地资源）；它唯一引用的 StaticResource 是全局 AccentButtonStyle（原 :50），删除后仍被其他 4 处 XAML 使用，必须保留（证据见 2.4）。

5. 未改其他窗口、未新增依赖、未 commit/push、未跑 --smoke、未做 GUI 点按（删除项为 0 调用入口，运行时不可达）。

## 二、证据

### 2.1 工作树状态（git status --short，仅列本单相关行）

~~~
 D src/PrtScr.App/Views/AnnotationWindow.xaml
 D src/PrtScr.App/Views/AnnotationWindow.xaml.cs
 M src/PrtScr.App/Views/NativePinWindow.cs
~~~

其余 M/?? 条目均为并行任务产物，非本单改动。

### 2.2 删除前的死代码证据（复核 P-13c2-followups.md）

- **OpenEditor 全仓 0 调用**：Record/reports/P-13c2-followups.md:78-83（全仓 grep 只有定义，无调用点）。
- **AnnotationWindow 全部引用点**（P-13c2-followups.md:63-96）：
  - 自身文件：AnnotationWindow.xaml:3（x:Class）、AnnotationWindow.xaml.cs:28（类声明）、:32（OpenWindows 列表）、:66（私有构造）、:143（唯一 new）。
  - 外部唯一引用：NativePinWindow.cs:499（OpenEditor 内调用 AnnotationWindow.Open）。
  - 无第二入口：无 XAML 资源键、无反射、无字符串引用。
- **贴图菜单「编辑」本来就不走旧窗**：NativePinWindow.Interaction.cs:157-164（case PinMenuWindow.CmdEdit → EnterEditMode()）。

### 2.3 删除后源码扫描：0 命中

实际执行的 PowerShell 扫描命令（排除 obj/bin 构建产物）：

~~~
$hits = Get-ChildItem -Recurse -File src -Include *.cs,*.xaml,*.csproj |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
  Select-String -Pattern 'AnnotationWindow|OpenEditor'
if ($hits) { $hits | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" } }
else { 'NO-HITS-IN-SRC' }
"SCAN_FILES=" + (...Count)
~~~

输出：

~~~
NO-HITS-IN-SRC
SCAN_FILES=118
~~~

即 118 个源码文件（.cs/.xaml/.csproj，排除 obj/bin）0 命中，满足验收第 4 条对源码的要求。

**全仓剩余命中只有三类，均非源码、且不在本单写域：**
1. Record/ 下的历史报告与计划（P-13c2-followups.md、P-13c2-editdocument-wiring.md、Record/plans/C1-visual-p0-plan.md 等）——验收已排除 Record/。
2. docs/HANDOFF.md:83（「Views/AnnotationWindow.xaml(.cs) 旧独立标注窗（已不用于贴图，保留）」）与 :679（「旧 AnnotationWindow 待清理」）——历史交接文档，属 docs/**，本单禁止修改（见 §五 待确认 1）。
3. 构建产物副本：.tmp/、artifacts/、src/PrtScr.App/obj|bin 下的旧 .xbf/.g.i.cs；不参与源码构建，重新构建/发布不会再引用。

### 2.4 保留项证明（只删死入口，不误删仍在使用的东西）

- PinClipboardHelper 仍被调用：
  - Copy → NativePinWindow.Export.cs:203、NativePinWindow.EditApply.cs:102、PinManagerWindow.cs:141
  - OpenOcr → NativePinWindow.Interaction.cs:276、:310
  故只删 OpenEditor，类与另两个方法保留。
- 全局样式 AccentButtonStyle 仍被 4 处 XAML 使用：CaptureOverlayWindow.xaml:107、OcrResultWindow.xaml:41、HistoryWindow.xaml:236、PinCropWindow.xaml:45。AnnotationWindow.xaml 未定义本地资源键，故无「专用资源键」可删。
- csproj 通配包含：见 §一.3，显式条目为 0。

### 2.5 删除后的调用链（贴图编辑入口固化）

- **正在使用的路径**：贴图右键菜单 → PinMenuWindow 回传 CmdEdit → NativePinWindow.Interaction.cs:157-164 → EnterEditMode()（NativePinWindow.Edit.cs:110）→ 原位标注（EditDocument<Annotation>，P-13c 第二步接线），标注在贴图窗内完成，不再打开独立窗口。
- **被删除的路径**：PinClipboardHelper.OpenEditor → AnnotationWindow.Open → new AnnotationWindow；该链原本全仓 0 调用，删除不改变任何可达行为。
- 跨线程 OCR / 复制仍走 PinClipboardHelper.OpenOcr / Copy，未受影响。

### 2.6 门禁（原始输出）

1) 整包构建（共享 build-guard，-Tag p37，OutDir=.tmp/out-p37/）：

~~~
BUILD-GUARD: lock acquired by p37
  PrtScr.App -> E:\Explore\GoodGood\PrtScr\.tmp\out-p37\IshWY.dll
已成功生成。
    0 个警告
    0 个错误
已用时间 00:00:18.28
BUILD-GUARD: EXIT=0 elapsed=19s tag=p37
~~~

2) Core.Tests（-Tag p37test）：

~~~
已通过! - 失败:     0，通过:   268，已跳过:     0，总计:   268，持续时间: 75 ms - PrtScr.Core.Tests.dll (net10.0)
BUILD-GUARD: EXIT=0 elapsed=3s tag=p37test
~~~

基线 268，未改测试文件，条数不变。

3) 格式：

~~~
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
→ exit 0，无输出
~~~

未跑 --smoke（会写主人真实 data/，纪律禁止）；未 commit。

### 2.7 写域核对

- 改：src/PrtScr.App/Views/AnnotationWindow.xaml(.cs)（删除）、src/PrtScr.App/Views/NativePinWindow.cs（仅删 OpenEditor）、Record/reports/P-13c2-dead-code-removal.md（本报告）。
- 未改：PrtScr.App.csproj、App.xaml、其他窗口、PrtScr.Core/PrtScr.Capture/PrtScr.Ocr、测试。

## 三、进行中

无。本单可单独验收。

## 四、风险与遗留

1. **docs/HANDOFF.md 仍描述旧窗为「保留/待清理」**（:83、:679），与现状不符；docs/** 不在本单写域，需 lead 决定是否另开文档改单，或作为历史快照保留。
2. **Record/plans/C1-visual-p0-plan.md 的 C1-C（P0-2b）已失效**：该计划要做「AnnotationWindow.xaml 三色抽资源 + 注释」（:47-48、:87、:145、:192），目标文件已删除，C 阶段需作废或改向；建议 lead 更新该计划。
3. **构建产物仍含旧文件名**（.tmp/out-*、artifacts/、src/PrtScr.App/obj|bin 下的 AnnotationWindow.xbf/.g.i.cs）：不参与源码构建，发布脚本按当前 TargetDir 收集 XBF，不会打包已删窗口；本单未清理产物，避免误删并行任务输出。
4. **门禁跑在含并行任务改动的工作树上**（HEAD 69616cc + P-14b/P-13c 等未提交改动），最终裁决由 lead 冻结后同 SHA 复跑。
5. **未做 GUI 点按**：删除的是 0 调用入口，运行期不可达；风险面仅编译与 XAML 资源解析，已由 build 0/0 覆盖。
6. **任务板 writeScopes 路径有误**：写的是 src/PrtScr.App/Services/PinClipboardHelper.cs，该文件不存在；真实类在 src/PrtScr.App/Views/NativePinWindow.cs:482。本单按真实位置执行，请在任务板修正写域（不影响交付）。

## 五、待确认

1. docs/HANDOFF.md:83/:679 是否需要更新为「已删除」？（写法：可留在历史快照，或在下一轮文档整理时统一改；我不改 docs/**。）
2. C1-C（P0-2b，AnnotationWindow 三色抽资源）确认作废，C 阶段计划里改为「窗口已删除」还是整条移除？
3. 任务板 task-37 writeScopes 修正（真实路径 src/PrtScr.App/Views/NativePinWindow.cs）。
4. 是否需要把本次删除写进 Record/ProjectLog.md？（ProjectLog 属 Record/ 但不在本单「Record/reports」写域内，本单未动，等 lead 统一记。）

## 三行摘要

- 做了什么：删除 AnnotationWindow.xaml 与 .xaml.cs（842 行）、删除 PinClipboardHelper.OpenEditor（NativePinWindow.cs:492-506），csproj 为通配故未改，未删任何资源键。
- 验证了：源码扫描 0 命中（118 文件）、整包 build 0 警 0 错、Core.Tests 268/268、format rc=0。
- 风险与未做：docs/HANDOFF.md 与 C1 计划仍引用旧窗（写域外，待 lead 决），未做 GUI 点按，未 commit。

# P-13c 收尾：_publishedRevision 可见性 + AnnotationWindow 去留证据

任务：task-36（rev2 → 完成后 rev3）。写域：src/PrtScr.App/Views/NativePinWindow.Export.cs、Record/reports/。
基线：HEAD 69616cc + 工作树中 P-13b/P-13c2（task-24/task-33）未提交改动。本单未 commit、未跑 --smoke、未做 GUI 运行时验证。

## 一、做了什么

只改 1 个文件：`src/PrtScr.App/Views/NativePinWindow.Export.cs`。

### 1.1 改动清单

1. **把 `_publishedRevision` 字段换成「volatile uint 背书 + 同名私有属性」**（`NativePinWindow.Export.cs:55-70`）：
   - `private uint _publishedRevisionValue = EditRevision.Initial.Value;`（`:64`，初值仍为 `Initial`=1，不改变首版语义）；
   - `private EditRevision _publishedRevision { get => new(Volatile.Read(ref _publishedRevisionValue)); set => Volatile.Write(ref _publishedRevisionValue, value.Value); }`（`:66-70`）。
   - 属性名保持 `_publishedRevision`，**因此写域外的两个发布点（`NativePinWindow.EditApply.cs:100-101`、`NativePinWindow.cs:447/450`）一个字都不用改**，经属性 setter 走 release 写。
2. **读取方先取版本、再取图片字节**（`TryGetCurrentPng`，`:84-90`）：
   ```csharp
   if (!pin._editMode)
   {
       // 先取版本（acquire）再取字节：发布点先写 _pngBytes、再写版本（release），
       // 因此读到新版本时，随后读到的 _pngBytes 不会早于该版本对应的图片。
       var revision = pin._publishedRevision;
       return new PinExportResult(PinExportStatus.NotEditing, pin._pngBytes, revision);
   }
   ```
3. 其余读取点不变（走同一个属性 getter，自动获得 acquire）：`:102`、`:110`、`:117`、`:146`、`:147`。

未新增锁、未改 `_exportLock` 的持有范围与锁序、未改任何其它文件。

### 1.2 发布点 / 读取点 / 同步手段 三者对照

| 角色 | 线程 | 位置 | 顺序 | 同步语义 |
| --- | --- | --- | --- | --- |
| 发布点 1：标注提交 | 贴图线程 | `NativePinWindow.EditApply.cs:100-101` | 先 `_pngBytes = bytes`，再 `_publishedRevision = committed.Base.Revision` | 普通写 → `Volatile.Write`（release）：先前的 `_pngBytes` 写对 acquire 读者可见 |
| 发布点 2：切换图片 | 贴图线程 | `NativePinWindow.cs:447/450` | 先 `_pngBytes = bytes`（:447），再 `_publishedRevision = _publishedRevision.Next()`（:450） | 读改写同一线程；最终 `Volatile.Write`（release） |
| 读取点 1：非标注态 | 管理器/UI 线程 | `NativePinWindow.Export.cs:84-90` | 先读 `_publishedRevision`（acquire），再读 `_pngBytes` | 两值配对，不会出现「新版本 + 旧字节」 |
| 读取点 2：标注态失败路径 | 管理器线程 | `Export.cs:102/110/117` | 只读 `_publishedRevision` | acquire，仅取版本 |
| 读取点 3：标注态成功路径 | 贴图线程 | `Export.cs:145-147` | `CompleteExportRequest` 在贴图线程构造 `Composed` 结果 | 与发布点同线程，无跨线程问题 |
| （未使用）锁 | — | `_exportLock`（`:56`） | 仅保护 `_exportRequest` 单槽 | 未把 revision 塞进该锁，锁序零变化 |

### 1.3 选择理由与内存序

- **为什么不用锁**：两个发布点在 `EditApply.cs` / `NativePinWindow.cs`，都不在本单写域；只给读取方加锁无法与无锁写者形成 happens-before，等于装饰。要把写点也纳入锁，必须扩写域并新增一把跨文件锁，成本和锁序风险都更大。
- **为什么用 Volatile 而不是 Interlocked**：revision 只有贴图线程一个写者，不存在 RMW 竞争；需要的是「写后对读者可见」的 release/acquire，而不是原子读改写。`Volatile.Write` / `Volatile.Read` 正好是这个语义。
- **为什么底层是 `uint`**：`EditRevision` 是只含 `uint Value` 的 `readonly record struct`，C# 不允许对结构体字段声明 `volatile`；因此拆出 `uint` 背书字段，属性在边界处组装/拆解 `EditRevision`。单个 `uint` 的读写本身不撕裂（满足「不会读到半截版本号」）。
- **为什么读取方要先取版本**：`Volatile.Read` 的 acquire 只约束**之后**的读写。若先读 `_pngBytes` 再 acquire 版本，可能拿到「旧字节 + 新版本」；先 acquire 版本再读字节，则由发布点的 release 保证：读到新版本 ⇒ 随后读到的字节至少是该版本发布时的图。

## 二、证据

### 2.1 改动后的关键片段（`NativePinWindow.Export.cs`）

```
:55  /// <remarks>
:59  /// 跨线程读写：贴图线程在提交/切图后写（发布点），管理器线程在 TryGetCurrentPng 里读。
:60  /// 用 Volatile 的 release/acquire 语义保证可见性：底层是单个 uint，不会撕裂；
:61  /// 读取方先取版本、再取图片字节，acquire 之后读到的字节不早于该版本发布时的内容。
:64  private uint _publishedRevisionValue = EditRevision.Initial.Value;
:66  private EditRevision _publishedRevision
:68      get => new(Volatile.Read(ref _publishedRevisionValue));
:69      set => Volatile.Write(ref _publishedRevisionValue, value.Value);
```

### 2.2 grep：AnnotationWindow 的全部引用点

在仓库根执行（含 .cs / .xaml）：

```
AnnotationWindow（*.cs）
  src/PrtScr.App/Views/AnnotationWindow.xaml.cs:28   public sealed partial class AnnotationWindow : Window
  src/PrtScr.App/Views/AnnotationWindow.xaml.cs:32   private static readonly List<AnnotationWindow> OpenWindows = new();
  src/PrtScr.App/Views/AnnotationWindow.xaml.cs:66   private AnnotationWindow(...)
  src/PrtScr.App/Views/AnnotationWindow.xaml.cs:143  var window = new AnnotationWindow(...)
  src/PrtScr.App/Views/NativePinWindow.cs:499        AnnotationWindow.Open(pngBytes, pixelWidth, pixelHeight, anchor);

AnnotationWindow（*.xaml）
  src/PrtScr.App/Views/AnnotationWindow.xaml:3      x:Class="PrtScr.App.Views.AnnotationWindow"

OpenEditor（*.cs，全仓）
  src/PrtScr.App/Views/NativePinWindow.cs:492       public static void OpenEditor(...)   ← 只有定义
  （无任何调用点）
```

调用链只有一条：`PinClipboardHelper.OpenEditor`（`NativePinWindow.cs:492-506`）→ 其内部 `:499` `AnnotationWindow.Open`（`AnnotationWindow.xaml.cs:138-153`）。而 `OpenEditor` 本身在全仓 **0 调用**。

对照：贴图右键菜单的「编辑」走的是原位标注，不是这个窗口——`NativePinWindow.Interaction.cs:162-164` `case PinMenuWindow.CmdEdit: EnterEditMode();`。

### 2.3 AnnotationWindow 结论表

| 问题 | 结论 | 证据 |
| --- | --- | --- |
| 是否还在用？ | **否，无人使用（仅遗留）** | 唯一入口 `PinClipboardHelper.OpenEditor`（`NativePinWindow.cs:492`）全仓 0 调用；`AnnotationWindow.Open` 只被它调用（`:499`） |
| 构造入口 | 私有构造，仅 `Open` 内 new | `AnnotationWindow.xaml.cs:66`、`:143` |
| 是否有第二入口（XAML/反射/字符串） | 未发现 | 全仓 grep `AnnotationWindow` 只命中其自身文件 + `NativePinWindow.cs:499` + `.xaml:3` 的 x:Class；`OpenEditor` 只有定义 |
| 与贴图原位标注的关系 | 两条独立路径 | `Interaction.cs:162-164` 菜单编辑 → `EnterEditMode`（贴图窗内）；`OpenEditor` → 独立 WinUI 窗口 |
| 现状语义 | 旧撤销语义仍在 | `AnnotationWindow.xaml.cs:276` `_numberCounter = 0`、`:284` `_annotations.RemoveAt(_annotations.Count - 1)`（即 P-13b 之前的「只 RemoveAt 最后一条」） |

**建议（不在本单执行）**：二选一，由后续单拍板——
1. 删除：`PinClipboardHelper.OpenEditor`（`NativePinWindow.cs:492-506`）+ `AnnotationWindow.xaml(.cs)`，这是最小清理；
2. 保留：明确它是「独立标注编辑器」，接回某个入口（例如右键菜单新增「在独立窗口编辑」）并把它迁移到 Core `EditDocument`，否则它就会长期带着第二套旧撤销语义。

### 2.4 门禁原文

1. 整包 build（`.tmp/build-guard.ps1`，禁 -t:Compile）：
```
.tmp/build-guard.ps1 -Tag p36 -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p36/"
  已成功生成。
    0 个警告
    0 个错误
  已用时间 00:00:25.48
BUILD-GUARD: EXIT=0 elapsed=26s tag=p36
```

2. Core.Tests：
```
.tmp/build-guard.ps1 -Tag p36test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo"
已通过! - 失败: 0，通过: 268，已跳过: 0，总计: 268，持续时间: 95 ms
BUILD-GUARD: EXIT=0 elapsed=5s tag=p36test
```

3. 全量格式：
```
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
FORMAT-RC=0
```

### 2.5 写域核验

`git status --short` 中本单只触碰 `src/PrtScr.App/Views/NativePinWindow.Export.cs`；其余 M/?? 项来自 task-33 已交付改动、并行任务（MainWindow.Capture.cs、CaptureOverlayWindow.*、CaptureService.cs、ScreenCapturer.cs、WgcWindowCapture.cs 等）与记录文件，本单未改。

## 三、进行中

无。B/C 两项均完成：可见性修改已落地并通过三门禁；AnnotationWindow 去留证据已给结论，未删任何文件（符合边界）。

## 四、风险与遗留

1. **只保证「读到新版本 ⇒ 字节不旧」**：这是一条单向保证；若某读取方只读 `_pngBytes` 不读 revision（例如 `PinRecord.PngBytes` 快照路径），本单没有、也不打算给它加栅栏——那是另一条数据通路，不在本单写域。
2. **Revision 仍无消费者**：`PinExportResult.Revision` 目前没有调用方读取（`PinManagerWindow.cs:88/:178` 只取 Bytes）。本单只解决「读到的值不陈旧」，无法保证将来消费方用对；lead 已裁定 `PinRecord.Revision` 押到 D 阶段贴图持久化一起做。
3. **标注态 Composed 的版本语义未变**：仍是「当前基底版本」，未提交标注不推进（lead 已裁定接受）。本单没有改这条语义，只把它的读取变成 acquire。
4. **`_editMode` 与 revision 的时序**：`_editMode` 已是 volatile；`ExitEditMode` 在贴图线程置 false 后 `_pngBytes` 已是提交图、revision 也是提交后的值，因此「读到非标注态」不会配到旧版本。本单未改这个时序。
5. **未做运行时并发压测**：合成热键/代操作被纪律禁止，Volatile 的正确性来自 C# 内存模型而非实测；本机也无法用 ThreadSanitizer 类工具验证。若主人愿意跑「标注提交 + 管理器连续刷新」的压力场景，可观察 revision 是否单调、缩略图是否与贴图一致。
6. **门禁跑在含并行任务改动的工作树上**：task-35 等仍在改 `CaptureOverlayWindow.xaml.cs` 等文件；最终裁决以 lead 冻结后的同 SHA 复跑为准。
7. **AnnotationWindow 仍是第二套撤销语义**：本单只调查不修；只要它不接回入口，就不会被用户触达；一旦接回就必须先迁移，否则 `NativePinWindow` 与它两套语义会分叉。

## 五、待确认

1. AnnotationWindow 采用「删除」还是「保留并接回」？（本单建议删除，理由：入口已是死代码，代码量不小且带旧语义；若保留需另单迁移到 Core。）
2. 是否同意「本单只做单向保证（新版本 ⇒ 新字节）、不给 PinRecord.PngBytes 通路加栅栏」这一范围界定？
3. 是否需要主人在 v1.3.1 候选包上补一条并发观察项（标注提交同时管理器刷新，看缩略图与贴图是否一致）？

---

三行摘要：做了 `_publishedRevision` 的跨线程可见性（uint 背书 + Volatile 属性，读取方先 acquire 版本再取字节，未新增锁、写点零改动）；验证了整包 build 0 警 0 错 / Core.Tests 268/268 / format rc=0，并给出 AnnotationWindow「无人使用（仅遗留）」的完整调用链证据（OpenEditor 全仓 0 调用）；风险是 Revision 仍无消费者、AnnotationWindow 未清理、未做并发压测。

# P-01 贴图编辑出口统一（B01 / B02 / B03）实施报告

- 单号：task-1 / P-01
- 执行人：maid-impl（女仆）
- 日期：2026-10-28
- HEAD：`0b1ca413f895b1e73beaafd299e5d3812afc4467`（= v1.3.0-2-g0b1ca41，与派单一致，**未提交**）
- 写域：`src/PrtScr.App/Views/NativePinWindow*`（partial 前缀）、`src/PrtScr.App/Views/PinManagerWindow.cs`；`PinMenuWindow.cs` 未改（菜单只回传命令）

## 1. 做了什么

### B01 所有出口改取「当前合成结果」
- 新增 `src/PrtScr.App/Views/NativePinWindow.Export.cs`（183 行，partial class），把「当前画面」收敛为**唯一入口**：
  - `internal static PinExportResult TryGetCurrentPng(nint hwnd)`：按 hwnd 找到贴图实例；非标注态直接返回已提交的 `_pngBytes`；标注态向贴图线程发 `WM_APP_EXPORT_PNG = 0x8000 + 33` 并最多等 1000ms（`ExportTimeout`）。
  - `private byte[]? TryComposeCurrentPng()`：标注态 = `DecodeSource()` 全分辨率图 + `AnnotationRenderer.Render(full.Image, _annotations + _draft)` → PNG 字节。**包含正在画的一笔（_draft）**，保证「复制到的是屏幕上正在看的」。
  - 三态结果 `PinExportStatus.NotEditing / Composed / Unavailable`，调用方无法把「取不到当前合成」误当成旧图。
- 四个出口全部改走该方法，**编辑态不再读标注前的 `_pngBytes`**：
  - 复制：菜单 CmdCopy → `CopyCurrentPng()`（`NativePinWindow.Interaction.cs:160`）；`CopyCurrentPng` 在 `NativePinWindow.Export.cs:162`。
  - OCR：菜单 CmdOcr → `OpenOcrCurrent()`（`Interaction.cs:170`，实现在 `Interaction.cs:301`）。
  - 裁剪：`StartCrop()` 先合成再交给 `PinCropWindow.Show`（`Interaction.cs:209`，合成 212、Show 224）。
  - 另存：`SaveAs()` 先合成再 `FileSaveService.SavePngAsync`（`Interaction.cs:287`，合成在 289）；标注态下菜单「另存为」仍走 `ApplyEdit(saveAs:true)`（= 完成标注并另存，保留原语义）。
- 贴图管理器（`PinManagerWindow.cs`）的复制 / 另存 / OCR / 缩略图都先取实时图：
  - `ReloadAsync` 缩略图用 `NativePinWindow.TryGetCurrentPng(record.Hwnd)`（`PinManagerWindow.cs:88`）；只有缩略图允许在 Unavailable 时退回快照。
  - 三个动作统一走 `TryGetLivePng`（`PinManagerWindow.cs:176`）；取不到时在副标题显示「贴图正在标注且暂未响应，请完成或退出标注后再试」并 **return**，绝不复制/保存标注前旧图。
- 新增 Ctrl+C 出口：HEAD 贴图窗口原本**没有** Ctrl+C 处理（只有 Ctrl+Z / Enter / Ctrl+S），派单把 Ctrl+C 列为出口，故在 WndProc 增加 `WM_KEYDOWN + 0x43 + Ctrl` 分支（`NativePinWindow.Window.cs:235`），走同一个 `CopyCurrentPng`，**标注态不退出标注**；属窗口内快捷键，不涉及全局热键。
- 复制结果反馈：`PinClipboardHelper.Copy` 增加完成回调（`NativePinWindow.cs:507`），结果回贴图线程弹提示条（`WM_APP_COPY_OK / WM_APP_COPY_FAILED`，`Window.cs:322/326`）。

### B02 进入标注幂等
- `EnterEditMode()` 首部加 guard（`NativePinWindow.Edit.cs:113`，guard 在 116-119）：已处于标注态时只写日志 `Pin: already in edit mode, ignored` 并 `SetFocus` 后返回，**不清画布、不重置 `_annotations` / `_numberCounter` / `_toolId`、不重置撤销栈**。菜单「标注…」与 `WM_APP_ENTER_EDIT` 两条入口都受此保护。

### B03 合成 / 编码失败不再当成功
- `ApplyEditCore` 重写（`NativePinWindow.EditApply.cs:77`）：
  1. 先释放并重建基底（避免旧基底与新图不一致）；
  2. `var bytes = TryComposeCurrentPng();`（84）；
  3. **失败分支**（`bytes is null`）：`RebuildBaseScaled()` + `ShowHint("标注合成失败，已保留编辑内容，请重试", 2600)`（88）+ 日志，**直接 return**——不替换 `_pngBytes`、不 `ExitEditMode`、不 `PublishPinState`，草稿与撤销栈原样保留；
  4. 成功分支：才替换 `_pngBytes`、复制到剪贴板、`ExitEditMode(apply:true)` + `PublishPinState()`；`saveAs` 时再另存。
- 剪贴板失败与合成失败**分开处理**：合成成功即提交并退出，剪贴板失败只弹「复制失败：剪贴板被占用，请稍后重试」+ 日志（B03 要求分别处理）。
- 可见错误复用底部提示条：`ShowHint`（`NativePinWindow.Commands.cs:143`），并把提示条绘制抽成 `DrawNavHintChip`（`NativePinWindow.Render.cs:229`），编辑态也能显示（`EditRender.cs:78`，锚定图像区域底边，不被工具栏遮住）。
- 失败后基底可重建：`RebuildBaseScaled()`（`NativePinWindow.Edit.cs:366-378`）即使解码失败也会建出空白基底，因此提示条仍有窗口可画（不会出现「错误提示也画不出来」）。

### 附带修正
- 标注态裁剪的取景区域：标注态窗口被工具栏撑高，若仍传整窗矩形，`PinCropWindow` 会按整窗比例把选区映射到像素，导致偏移。改为标注态传**图像区域**（`cropX/cropY/cropW/cropH`，`Interaction.cs:220-224`）。这是统一出口后在走查中发现的连带问题。

## 2. 证据

### HEAD 是否仍成立
- HEAD = `0b1ca41`，与派单一致；评审列的三条缺陷**在 HEAD 上全部成立**：
  - B01：HEAD 的 `Interaction.cs` 菜单复制/OCR 直接传 `_pngBytes`（HEAD 行号 159-160、169-170），`StartCrop` 直接传 `_pngBytes`，`SaveAs` 直接保存 `_pngBytes`；标注内容只在按 Enter 应用时才烘焙。
  - B02：HEAD `EnterEditMode`（HEAD 行号 113-171）无任何重入保护，重入会 `_annotations.Clear()`。
  - B03：HEAD `ApplyEditCore`（HEAD 行号 77-115）catch 只写日志，随后**无条件** `ExitEditMode(apply:true)` + `PublishPinState()`，失败也当成功。
- 评审给的部分定位有偏差，已按实际代码修正：`_pngBytes` 副本出口在 `NativePinWindow.Interaction.cs`（不是 `Window.cs:207-214`，那里是鼠标处理）；菜单文件实际是 `PinMenuWindow.cs`（不是 `.xaml.cs`）。

### 门禁（本轮实际执行）
| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 编译（派单写法） | `dotnet build PrtScr.sln -c Debug --nologo -t:Compile` | ❌ **命令本身不可用**（见下）；1248 errors / 0 warnings，**0 条落在本单改动文件**，全部是 XAML `x:Name` 的 CS0103 |
| 编译（等价替代） | `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-sln/` | ✅ `已成功生成。0 个警告 0 个错误` |
| 整包 App | `dotnet build src/PrtScr.App/PrtScr.App.csproj -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out/` | ✅ 0 警告 0 错误 |
| 测试 | `dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo` | ✅ `失败: 0，通过: 77，已跳过: 0，总计: 77` |
| 格式 | `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | ✅ 退出 0（本轮最后一次全量检查；中途曾被并发单的 `src/PrtScr.Capture/WindowCapture.cs` 挡住，对方收尾后恢复） |

收工前（主人今晚收工决定后）按要求复跑：完整解决方案 build exit 0（0 警告 0 错误）、Core.Tests 77/77、format exit 0；HEAD 仍 0b1ca41，未提交。

**本单写域确认**：`git diff --name-only` 中本单产出只有 9 个 `src/PrtScr.App/Views/NativePinWindow*.cs`（+ 新增 `NativePinWindow.Export.cs`）与 `PinManagerWindow.cs`，加本报告；同工作树的 `docs/HANDOFF.md`、`src/PrtScr.Capture/WindowCapture.cs` 修改**不是本单所为**（其他并发单/文档维护）。未动 .gitignore、README、NuGet、Record 下他人文件。

**`-t:Compile` 为什么不可用（重要，建议 lead 修改门禁定义）**
- 根因：WinUI 的 XAML 代码生成挂在 `PrepareResourcesDependsOn` 上（`C:/Users/asus/.nuget/packages/microsoft.windowsappsdk.winui/2.3.9/buildTransitive/Microsoft.UI.Xaml.Markup.Compiler.interop.targets` 158-171 行：`MarkupCompilePass1; XamlPreCompile; MarkupCompilePass2`），而 `PrepareResources` 是 `Build` 的依赖，**不在 `-t:Compile` 依赖链里**。因此 `-t:Compile` 永远拿不到 `MainWindow.g.cs` 等生成文件，只要 CoreCompile 真执行就报 CS0103。
- 为什么有时「通过」：上一次完整 build 后 CoreCompile 被判定 up-to-date（增量跳过）时，`-t:Compile` 输出 0 错 0 警——这是**假通过**。实测同一命令先过后败，取决于 obj 增量状态；并发节点会放大这种随机性。
- 这 1248 条错误全部在**本单没碰**的 XAML 窗口文件（`CaptureOverlayWindow.Hud.cs` 182、`MainWindow.Hotkeys.cs` 126、`PinCropWindow.cs` 118、`MainWindow.CaptureSettings.cs` 102、`MainWindow.xaml.cs` 88…），`grep 'Views/NativePinWindow|PinManagerWindow|PinMenuWindow'` 计数 = **0**。
- 建议把门禁 1 换成上表「等价替代」的完整解决方案 build（重定向 OutDir 即可避开 `bin/x64/Debug` 占用），并同步更新 `Record/plans/2026-10-28-整改总纲.md:24`。

### 改了哪些文件（10 个，均在写域）
```
src/PrtScr.App/Views/NativePinWindow.Export.cs   +183（新增）
src/PrtScr.App/Views/NativePinWindow.Commands.cs   20 ++--
src/PrtScr.App/Views/NativePinWindow.Edit.cs        8 ++
src/PrtScr.App/Views/NativePinWindow.EditApply.cs  33 ++-----
src/PrtScr.App/Views/NativePinWindow.EditRender.cs  4 ++
src/PrtScr.App/Views/NativePinWindow.Interaction.cs 43 ++++++---
src/PrtScr.App/Views/NativePinWindow.Render.cs     36 +++++-----
src/PrtScr.App/Views/NativePinWindow.Window.cs     22 +++++-
src/PrtScr.App/Views/NativePinWindow.cs             5 ++-
src/PrtScr.App/Views/PinManagerWindow.cs           46 ++++++---
9 files changed, 164 insertions(+), 53 deletions(-)（+ 新增 Export.cs 183 行）
```

### 关键行号（当前工作树）
- `NativePinWindow.Export.cs`：`TryGetCurrentPng` 54、`RequestCurrentPng` 70、`CompleteExportRequest` 111、`TryComposeCurrentPng` 130、`CopyCurrentPng` 162、`PostCopyResult` 178；常量 33-35、超时 38。
- `NativePinWindow.EditApply.cs`：`ApplyEdit` 61、`ApplyEditCore` 77、合成 84、失败提示 88。
- `NativePinWindow.Edit.cs`：`EnterEditMode` 113、guard 116-119、`ExitEditMode` 181、`RebuildBaseScaled` 366。
- `NativePinWindow.Interaction.cs`：菜单 159-177、`StartCrop` 209（裁剪矩形 220-224）、`SaveAs` 287、`OpenOcrCurrent` 301。
- `NativePinWindow.Window.cs`：Ctrl+C 235、`WM_APP_CLEAR_NAV_HINT` 305、`WM_APP_EXPORT_PNG` 317、`WM_APP_COPY_OK` 322、`WM_APP_COPY_FAILED` 326。
- `NativePinWindow.Render.cs`：调用 225、`DrawNavHintChip` 229；`NativePinWindow.EditRender.cs`：提示条 78。
- `NativePinWindow.Commands.cs`：`RefreshPinOverlays` 113、`ShowNavigationHint` 135、`ShowHint` 143。
- `PinManagerWindow.cs`：缩略图实时取图 88、`CopyCard_Click` 134/139、`SaveCard_Click` 152/157、`OcrCard_Click` 162/167、`TryGetLivePng` 176。
- `NativePinWindow.cs`：`PinClipboardHelper` 481、`Copy(byte[], DispatcherQueue, Action<bool>?)` 507。

### 走查证据（四处出口都收敛到同一方法）
```
$ grep -n "TryComposeCurrentPng\|TryGetCurrentPng" src/PrtScr.App/Views/*.cs
NativePinWindow.Export.cs:54:  internal static PinExportResult TryGetCurrentPng(nint hwnd)
NativePinWindow.Export.cs:130: private byte[]? TryComposeCurrentPng()
NativePinWindow.Export.cs:164: var bytes = TryComposeCurrentPng();          // CopyCurrentPng
NativePinWindow.EditApply.cs:84: var bytes = TryComposeCurrentPng();         // ApplyEditCore
NativePinWindow.Interaction.cs:212: var source = TryComposeCurrentPng();     // StartCrop
NativePinWindow.Interaction.cs:289: var bytes = TryComposeCurrentPng();      // SaveAs
NativePinWindow.Interaction.cs:303: var bytes = TryComposeCurrentPng();      // OpenOcrCurrent
PinManagerWindow.cs:88 / 178: NativePinWindow.TryGetCurrentPng(...)          // 缩略图 / 三个动作
```
- 标注态下这四条出口都不再出现裸 `_pngBytes`：`grep -n "_pngBytes" Interaction.cs` 只剩非出口路径（`ProcessCrop` 之外的保存语义与 hwnd 传递）与本单注释；`SaveAs`/`StartCrop`/`OpenOcrCurrent`/`CopyCurrentPng` 均以 `bytes/source` 命名。

## 3. 进行中（交接用）

### 已完成
- B01 出口统一（复制 / OCR / 裁剪 / 另存 / 管理器三动作 + 缩略图）、新增 Ctrl+C；
- B02 `EnterEditMode` 幂等；
- B03 合成失败保留草稿与编辑态 + 可见提示；剪贴板失败独立提示；
- 标注态裁剪区域修正；
- 四门禁：整包 build / 完整解决方案 build / 77 测试 / 全量 format 全绿（`-t:Compile` 命令本身失效，见上）；
- 本报告落盘。

### 下一步第一件事
- 按「主人实测步骤」做交互验收；如果主人认可，按 task-1 output 三行收单，然后领取 task-5（P-04 捕获来源与会话身份，A02+A05）。

### 下一步该读的文件
- `src/PrtScr.App/Views/NativePinWindow.Export.cs`（唯一取图入口与三态）
- `src/PrtScr.App/Views/NativePinWindow.EditApply.cs:77-110`（失败/成功分支）
- `src/PrtScr.App/Views/NativePinWindow.Interaction.cs:209-230`（裁剪区域）
- `src/PrtScr.App/Views/PinManagerWindow.cs:80-190`（实时取图与错误提示）

## 4. 风险与遗留

1. **`-t:Compile` 门禁定义不成立**（最大风险，影响全队）：它是增量假通过 / 冷编译 1248 错的随机门禁；建议 lead 统一换成完整解决方案 build + 重定向 OutDir，并更新总纲第 24 行。本单以完整解决方案 build 作为编译证据。
2. **交互验收未做**：所有结论来自编译 + 走查 + 未跑 GUI。标注态粘贴、OCR、裁剪、管理器取图都需要主人真实操作确认；尤其**裁剪区域**在标注态工具栏位于顶部/底部两种布局下都要看一次（`_toolbarAtTop` 由光标位置决定）。
3. **Ctrl+C 语义**：标注态且文字工具正在输入文字时，Ctrl+C 应该复制文字而不是图片。当前实现依赖「文字编辑器是独立子窗口、WM_KEYDOWN 不冒泡到贴图窗口」；若实测发现图片被复制，需要在该分支加 `_editControl == null` 条件（本单未加，避免在未验证前引入额外分支）。
4. **管理器等待上限 1000ms**：管理器在 UI 线程同步等贴图线程；若贴图线程正被菜单阻塞，动作会在 1 秒后报错而不落旧图。可接受但会有可感知延迟；后续可改为异步取图。
5. **失败分支提示的极端情况**：若源 PNG 完全无法解码且 `RebuildBaseScaled` 也拿不到任何图像，提示条仍可绘制（空白基底），但用户看到的是空白画布——此时日志 `Pin: edit apply aborted (compose failed)` 是唯一可靠证据。
6. **未加回归测试**：Core.Tests 是纯 Core 层，贴图 Win32 路径无法在其中覆盖；本单以 build + 代码走查 + 主人实测为准，未新增测试（net 77 条不变，未减少）。
7. **并发工作树**：本单期间 `src/PrtScr.Capture/WindowCapture.cs`（另一单）在改，全量 format 曾短暂被他挡住；最终已恢复 0。报告中的门禁结果对应 2026-10-28 22:2x 的最终一次执行。

## 5. 待确认

- **待确认（lead）**：门禁 1 是否正式改为 `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-sln/`？需要有人改 `Record/plans/2026-10-28-整改总纲.md`（不在我的写域）。
- **待确认（主人）**：标注态下「另存为」保持「完成标注并另存（退出标注）」是否可以？本单保留 HEAD 语义；若希望它同复制/OCR 一样**不退出标注**，则改为 `SaveAs()` 并保留编辑态。
- **待确认（主人）**：标注态裁剪现在是「先合成当前画面，再在图像区域上取景」，裁剪结果不会替换当前贴图（沿用 HEAD：Pin 动作生成新贴图 / Ocr / Save）。若期望裁剪直接替换当前贴图，需要另一单。

## 6. 主人实测步骤（3-5 条）

1. **标注后复制**：按 F1 截一块窗口 → 贴图上右键「标注…」→ 画一个矩形或涂鸦（**先不要**按 Enter）→ 按住右键拖一下松开弹出菜单 → 点「复制图片」→ 到画图/微信里 Ctrl+V。预期：粘贴出来的是**带标注**的图；贴图仍停在标注态，画布不清空。
2. **标注后 OCR**：仍在标注态，用文字工具在图上写两个字（如「测试」）→ 右键菜单点「识别文字」。预期：OCR 窗口识别出「测试」，且不退出标注。
3. **标注态裁剪对齐**：仍在标注态，右键菜单点「裁剪…」→ 在图像上框住一个已知小区域 → 选「贴为新贴图」。预期：新贴图内容与框选区域一致，**没有工具栏高度的偏移**；顶部工具栏与底部工具栏两种位置各试一次（把窗口拖到屏幕上半/下半再进标注）。
4. **重进标注不丢画布**：标注态画几笔 → 再点一次右键菜单「标注…」。预期：画布内容、撤销栈都还在，日志出现 `Pin: already in edit mode, ignored`，不会清空。
5. **管理器取当前画面**：打开贴图管理器，对一张**正在标注**的贴图点「复制」。预期：复制到的是含标注的当前画面；若贴图线程无响应，副标题提示「贴图正在标注且暂未响应，请完成或退出标注后再试」，**不会**给出标注前的旧图。

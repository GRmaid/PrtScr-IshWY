# D-2B 贴图持久化设计（task-43）

- 日期：2026-11-07
- 基线：HEAD dc7b6a2（工作树含同事未提交的 H1、历史元数据等改动；本设计只读，不写 src）
- 结论：做「应用重启恢复 + 稳定状态持久化」，不做「可再编辑标注的完整文档持久化」。存储放程序目录 data/pins，索引与 PNG 分离，标注以扁平化 PNG 落盘。

## 1. 现状清单

### 1.1 贴图只在内存

| 事实 | 证据 |
|---|---|
| 贴图注册表是进程内 ConcurrentDictionary，没有磁盘读写 | src/PrtScr.App/Services/PinService.cs:70（Registry）；:78-79（SnapshotPins）；:81-86（UpsertPin）；:88-92（RemovePin） |
| PinRecord 是管理器快照，不是持久化记录 | src/PrtScr.App/Services/PinService.cs:25-41 |
| 全仓没有 LoadPins / SavePins / pins.json 之类的实现；管理器只读 SnapshotPins | PinManagerWindow.cs:83 调 SnapshotPins；MainWindow 只打开管理器 |
| 应用退出直接 ExitProcess(0)，没有贴图保存钩子 | src/PrtScr.App/MainWindow.Windows.cs:382-388 |
| 进程启动只加载配置、创建主窗；没有恢复贴图 | src/PrtScr.App/App.xaml.cs:60-139；src/PrtScr.App/MainWindow.xaml.cs:96-160 |

结论：关掉一张贴图、结束进程或重启应用，贴图全部丢失；崩溃也无法恢复。

### 1.2 PinRecord 现有字段与 Revision 来源

PinRecord 现有字段（src/PrtScr.App/Services/PinService.cs:25-41）：

- Hwnd：运行时窗口句柄，不能持久化，重启后无意义。
- PngBytes：完整 PNG 字节；当前是贴图窗口 _pngBytes 的引用。
- PixelWidth / PixelHeight：原图像素尺寸。
- X / Y / Width / Height：窗口在虚拟屏幕上的物理像素矩形。
- Opacity、Locked、ClickThrough、Hidden：交互状态。
- SourceScope：来源显示名（「贴图」「文字」「全部」或历史分类）。
- NavIndex / NavCount：左右切换浏览的运行时位置。
- CreatedAt：创建时间（DateTime.Now）。

Revision 现状：

- 类型是 src/PrtScr.Core/Edit/EditRevision.cs:7-13，Initial=1，Next 单调 +1。
- 贴图线程的已发布版本是 src/PrtScr.App/Views/NativePinWindow.Export.cs:64-70 的 _publishedRevision；标注提交时由 src/PrtScr.App/Views/NativePinWindow.EditApply.cs:100-101 写成 committed.Base.Revision；左右切换时在 src/PrtScr.App/Views/NativePinWindow.cs:447-450 递增。
- 对外出口 PinExportResult 已带 Revision（src/PrtScr.App/Views/NativePinWindow.Export.cs:29、:77-96、:148-151），但 PinRecord 没有 Revision，PublishPinState 也没有带它（src/PrtScr.App/Views/NativePinWindow.cs:336-357）。

结论：Revision 已有运行时语义，但只服务导出；持久化必须把它补进 PinRecord 和落盘记录（见 §3）。

### 1.3 创建、状态发布与关闭

- 创建：PinService.CreatePin 只取 PNG/尺寸/偏移（PinService.cs:344-375），NativePinWindow.TryCreate 只接收 PNG、尺寸、DispatcherQueue、spawnOffset（NativePinWindow.cs:376-385）。窗口位置由光标与 PinInitialScale 计算（NativePinWindow.Window.cs:27-46），没有从保存矩形恢复的入口。
- 状态发布：PublishPinState 是唯一构造 PinRecord 的点（NativePinWindow.cs:336-357），在创建（Window.cs:83）、拖动结束（Window.cs:281）、缩放结算（Window.cs:302）、透明度（Commands.cs:21-27）、锁定/穿透/隐藏/恢复（Commands.cs:30-98）、导航（NativePinWindow.cs:469）、编辑提交（EditApply.cs:107）、设置来源/导航（NativePinWindow.cs:394-409）时调用。
- 关闭：三个入口都走 TryClosePinWindow（CloseGuard.cs:17-69）：WM_CLOSE（Window.cs:352-353）、双击（Window.cs:178-180）、PinCommand.Close（Commands.cs:94-95）；最终 DestroyWindow，Run 的 finally 调 PinService.RemovePin 并清 ByHwnd（Window.cs:105-111）。没有关闭前的持久化回调。
- 管理器关闭：MainWindow.Windows.cs:94-137 只是把贴图临时降为非置顶，关闭管理器后恢复置顶（:141-152）；管理器生命周期与贴图无关，管理器重开仍从 PinService 读快照。
- 管理器「全部关闭」：PinManagerWindow.cs:219-223 对每张贴图发 PinCommand.Close，等价于逐个关闭，会丢图。

### 1.4 现有存储基线

- 配置：程序目录 data/config.json（src/PrtScr.Core/ConfigService.cs:216-217）。
- 截图历史：程序目录 data/history（src/PrtScr.App/Services/CaptureHistoryService.cs:20）。
- 历史元数据已有「临时文件 + File.Move 覆盖」的原子写样板（CaptureHistoryService.cs:307-331；HistoryMetadata.CurrentVersion=2，src/PrtScr.Core/HistoryMetadata.cs:13）。
- 仓库没有 %LOCALAPPDATA%/IshWY 目录约定；贴图持久化不应引入第二套数据根。

## 2. 语义定义

把「持久化」拆成四件事，分别定取舍：

| 场景 | 是否做 | 推荐语义与理由 |
|---|---|---|
| 应用重启恢复 | **做（2B 核心）** | 启动时恢复上次成功保存的贴图集合；恢复的是已提交 PNG 与窗口状态，不恢复未提交标注。 |
| 崩溃恢复 | **做「最后成功快照」恢复** | 每次状态变化去抖写盘；崩溃最多丢最近一个去抖窗口（默认 750ms）的移动/缩放，PNG 编辑提交立即落盘。不承诺恢复到崩溃瞬间的未提交编辑。 |
| 多显示器布局变化 | **做限制性恢复** | 不把显示器身份当权威；按保存的虚拟屏幕物理矩形找最近工作区并夹取。显示器被拔掉时落到最近/主显示器，尺寸超工作区按比例缩小。 |
| 管理器重开仍可见 | **已有，保持** | 管理器从 PinService 快照读，不管管理器开/关；恢复后的贴图同样出现在 SnapshotPins 中。 |
| 贴图被关闭 | **删除持久化记录** | 关闭是用户的删除意图；下次启动不再恢复。崩溃导致关闭未落盘时可能复活一张，属可接受边界。 |
| 标注进行中 | **不持久化未提交状态** | 与现有 CloseGuard（CloseGuard.cs:17-69）一致；关闭时仍按现有确认框处理，确认放弃才删除记录。 |
| 隐藏/锁定/穿透 | **恢复这些状态** | 它们是用户显式设置的状态；恢复隐藏或穿透后仍可从管理器「恢复」按钮救回（PinManagerWindow.cs:197-205）。 |
| 左右导航列表 | **不持久化** | 导航绑定历史文件列表（PinService.PinNavigation，PinService.cs:291），历史可能被修剪；恢复为静态 PNG，避免恢复出不存在的路径。 |

补充语义：

1. 恢复顺序按 CreatedAt 升序创建；当前没有显式 z-order 持久化，全部 TOPMOST 的窗口按创建顺序叠放，恢复顺序与关闭前不保证完全一致。这是可接受的边界（现有 PinRecord 也没有 z-order）。
2. 恢复时若贴图超出上限或 PNG 不可用，跳过该条并记日志，不阻塞其他贴图启动。
3. 恢复的窗口必须仍可被管理器关闭/恢复交互；不能出现「恢复后无法操作且管理器也看不到」的状态。

## 3. 数据模型

### 3.1 存储形式选择

推荐：**索引 JSON + 每张贴图独立 PNG 文件**，不把 PNG base64 内嵌进 JSON。

理由：

- 当前 PinRecord.PngBytes 已是完整 PNG，写独立文件不需要重新编码；base64 会增大约 33%，且 JSON 原子替换时要把所有大图一起重写。
- 独立 PNG 可以按 Revision 版本化，编辑提交时只替换该贴图的文件，索引保持小文件。
- 与 data/history 的 PNG + sidecar 模式一致，便于人工排查和清理。

### 3.2 目录与文件

~~~
<AppContext.BaseDirectory>/data/pins/
  index.json                 # 索引，小文件，原子替换
  index.json.bak             # 上一次成功索引，损坏时回退
  pin-<id>-r<revision>.png   # 每张贴图当前/上一版图片
~~~

- 目录跟 data/config.json、data/history 同级，保持便携布局（ConfigService.cs:216-217、CaptureHistoryService.cs:20）。
- **不采用 %LOCALAPPDATA%/IshWY**：当前程序没有该约定，配置与历史都在程序目录；引入第二根目录会让「便携包换机」出现两份数据。程序目录不可写时记日志并停用持久化，不做静默回退（见 §5）。
- 文件名只允许 pin-<32hex>-r<uint>.png；加载时用 Path.GetFileName 校验，拒绝绝对路径、..、目录分隔符，防止索引被篡改后越界读文件。

### 3.3 索引 JSON（version 1）

~~~json
{
  "version": 1,
  "savedAt": "2026-11-07T03:20:11.123+08:00",
  "pins": [
    {
      "id": "5f2c9d7a8b4e4c1e9a6f0d3b2c1a8e77",
      "pngFile": "pin-5f2c9d7a8b4e4c1e9a6f0d3b2c1a8e77-r3.png",
      "revision": 3,
      "pixelWidth": 1280,
      "pixelHeight": 720,
      "x": 120,
      "y": 80,
      "width": 640,
      "height": 360,
      "opacity": 1.0,
      "locked": false,
      "clickThrough": false,
      "hidden": false,
      "sourceScope": "全部",
      "sourceKind": "history",
      "sourceHistoryFile": "20261107-031500-123-normal-a1b2c3.png",
      "createdAt": "2026-11-07T03:15:00.000+08:00",
      "pngSha256": "64 hex",
      "editState": "flattened"
    }
  ]
}
~~~

字段与约束：

| 字段 | 类型 | 约束/默认 | 说明 |
|---|---|---|---|
| version | int | 1 | 状态版本；未来递增 |
| savedAt | DateTimeOffset | 写盘时刻 | 仅诊断，不参与恢复逻辑 |
| id | string | 32 位十六进制 Guid | 持久化身份；替代 Hwnd |
| pngFile | string | 仅文件名 | 相对索引目录；防路径穿越 |
| revision | uint | ≥1，默认 1 | EditRevision 的值 |
| pixelWidth/Height | int | >0，否则跳过 | 原图像素尺寸 |
| x/y | int | 虚拟屏幕物理像素 | 允许负值（副屏在左侧） |
| width/height | int | ≥16，恢复时夹取 | 窗口物理像素矩形 |
| opacity | double | 0.25–1.0 | 与现有菜单档位一致 |
| locked/clickThrough/hidden | bool | false | 用户状态 |
| sourceScope | string | 「贴图」 | 管理器标题用；保留现有语义 |
| sourceKind | string | unknown | clipboard/history/text/crop/unknown；现有代码无来源判别，见下 |
| sourceHistoryFile | string? | null | 仅历史来源，文件名，不存绝对路径 |
| createdAt | DateTimeOffset | 创建时刻 | 恢复顺序；不因恢复重置 |
| pngSha256 | string? | null | 完整性校验；为空时只校验 PNG 签名/尺寸 |
| editState | string | flattened | 明确「标注已烘焙」；v1 不支持 replay |

来源 meta 的现状与取舍：PinRecord 只有 SourceScope（PinService.cs:38），CreatePin 没有来源参数（PinService.cs:344-375），所以当前无法可靠区分「剪贴板图片 / 历史 / 文字 / 裁剪」。设计上保留 sourceKind 字段，但第一版实现只写 sourceScope 与 unknown；要写准确来源，需在 CreatePin 增加可选来源枚举，由 PinService 各入口传入，属实现单的可选小步，不从字符串猜。

### 3.4 类型落点

- 不把 PinRecord 搬进 Core：它带 nint Hwnd、窗口状态与 WinUI 语义，搬进 Core 会把窗口概念带进纯测试层。
- 新增 Core DTO：PinPersistedState、PinPersistedEntry（只含 JSON 字段与默认值）和 PinPersistenceRules（校验、夹取、文件名生成、版本判断）。Core.Tests 直接测这些纯函数。
- App 层新增适配：PinPersistenceService 负责读/写/去抖/单写者；NativePinWindow 负责把运行时状态映射成 PinRecord；PinService 负责 Id 与关闭事件。
- PinRecord 增两个字段：
  - string Id：持久化身份，创建时 Guid.NewGuid().ToString("N")；恢复时沿用旧 Id。
  - EditRevision Revision：发布时取 _publishedRevision；PinExportResult 已有 Revision，保持不变。
- 不改 PinExportResult 的签名；管理器目前不消费 Revision（PinManagerWindow.cs:176-188），本设计只要求 PinRecord 有值，管理器消费留实现单的可选步。

### 3.5 「已合成 PNG」与「标注可再编辑」

推荐 v1 只持久化已合成 PNG（flattened），不持久化标注操作：

- 当前提交点 ApplyEditCore 已经把合成结果写回 _pngBytes 并推进 Revision（EditApply.cs:100-101），退出编辑后 _document 不再保留；现有语义本来就是「完成即烘焙」。
- 若要重启后继续编辑旧标注，需要把 Annotation 的类型、几何、文字、马赛克参数序列化，并保证 AnnotationRenderer 的绘制规则跨版本一致；这会把 2B 从「贴图状态持久化」升级成「编辑文档持久化」，风险与工作量都超出本设计。
- 恢复后的图作为新编辑会话的基底：可以继续画新标注，但重启前的标注不可撤销、不可单独选中；editState: "flattened" 明确记录这一点。
- 未来若要做可编辑，新增 editState: "document" 与 annotations 数组，保留 version 迁移空间；v1 不预留复杂结构。

## 4. 存储位置与写入时机

### 4.1 写入者的唯一入口

- PinRecord 的唯一构造点是 PublishPinState（NativePinWindow.cs:336-357），因此所有状态变化都从它进入持久化，不另建第二套状态收集。
- PinService 增加 event Action<PinRecord>? PinClosed（或移除前回调）：RemovePin 时先取旧记录再触发；NativePinWindow.Run finally 已在 RemovePin 前（Window.cs:105-111），可拿到 Id/记录。
- PinPersistenceService 订阅 Publish/Closed 事件；恢复期间设 IsRestoring=true，不把恢复动作反向写盘。

### 4.2 写什么、何时写

| 事件 | 写索引 | 写 PNG | 时机 |
|---|---|---|---|
| 新建贴图（PinRecord 首次发布） | 是 | 是（r1） | 立即；失败不阻塞窗口显示 |
| 编辑提交 ApplyEditCore | 是 | 是（新 revision） | 立即；先 PNG 后索引 |
| 左右导航（_pngBytes 变更 + revision 递增） | 是 | 是（新 revision） | 立即；导航是用户显式换图 |
| 拖动结束 / 缩放结算 | 是 | 否 | 去抖 750ms |
| 透明度 / 锁定 / 穿透 / 隐藏 / 恢复 | 是 | 否 | 去抖 750ms |
| 管理器命令（Focus/Show 等）导致的 Hidden 变化 | 是 | 否 | 去抖 750ms |
| 关闭贴图 | 删除条目 | 延迟删除旧 PNG | 立即写索引；PNG 在索引提交后删 |
| 应用退出 | 刷 pending | 无 | Exit_Click 在 ExitProcess 前同步 Flush（见 4.4） |

- 去抖 750ms：拖动/缩放本身已经在结束点才 PublishPinState（Window.cs:281、:302），不会每帧写盘；750ms 只合并连续操作。
- 图像写入不推迟：编辑提交/导航后 _pngBytes 已是新数组，立即写新版本 PNG；若进程在索引更新前崩溃，旧索引仍指向旧 PNG，不产生半张图。
- 不在 PinManagerWindow 内直接写盘；管理器只发 PinCommand，状态变化仍回贴图线程 PublishPinState。

### 4.3 原子写与版本化

索引写入顺序：

1. 生成新索引 JSON（只含小字段）。
2. 写 index.json.tmp（同目录，FileStream.Flush(true) 后关闭）。
3. 若 index.json 存在，先复制/替换出 index.json.bak；再用 File.Replace(tmp, index, backup) 或 File.Move(tmp, index, overwrite:true)（沿用 CaptureHistoryService.cs:328-331 的临时文件 + Move 样板）。
4. 只有索引提交成功后才删除该贴图不再被引用的旧 PNG。

PNG 写入顺序：

1. 写入 pin-<id>-r<newRevision>.png.tmp。
2. 移动到 pin-<id>-r<newRevision>.png；新 revision 文件名唯一，不覆盖旧文件。
3. 写索引成功后，旧 revision 文件进入延迟清理（保留上一版至少一次成功保存，便于索引回退）。

这样崩溃点只有两类：新 PNG 已写但索引未换（旧状态完整），或索引已换但旧 PNG 未删（孤儿文件，不影响恢复）。启动时清理未被 index.json/index.json.bak 引用的孤儿 PNG，但不删除 data/history。

### 4.4 去抖、串行化与退出

- PinPersistenceService 内部用单写者队列（Channel 或 lock + 最新快照），所有线程只投递快照；同一时刻最多一个索引写入，避免 JSON 竞争。
- 去抖用 Timer/最后一次时间戳；只有最新状态会被写，旧快照自然合并。
- 退出：现有托盘退出是 ExitProcess(0)（MainWindow.Windows.cs:382-388），不会等 async。实现单必须在调用前执行 PinPersistenceService.Flush(TimeSpan.FromSeconds(1))，同步写完 pending；超时只记日志，不阻塞退出超过 1 秒。
- 崩溃：接受最多 750ms 的移动/缩放丢失；编辑提交已经同步写 PNG，最多丢索引指针，旧版仍可恢复。

### 4.5 清理与上限

推荐 v1 上限：50 张贴图、总 PNG 128 MiB（不含 data/history）。策略：

- 达到上限不静默删除用户贴图：新贴图仍可正常显示，但持久化写入跳过并记 Pin persist: quota exceeded；管理器/日志可提示。
- 恢复时按 CreatedAt 升序装入，超过 50 张或累计 128 MiB 的条目跳过并记日志（不删除文件）。
- 关闭贴图后删除其索引条目；对应 PNG 在索引提交后删除。旧 revision 文件在下次成功保存后清理。
- 孤儿 PNG（索引与备份都不引用）在启动时清理；失败不阻塞。
- 不做「按时间自动删除旧贴图」；删除必须来自用户关闭或后续显式清理单。

## 5. 失败模式与降级

| 失败 | 行为 | 用户可见/日志 |
|---|---|---|
| 截图历史目录被删/历史文件被修剪 | 无影响：持久化存的是独立 PNG 副本，不依赖源历史文件 | 无；sourceHistoryFile 仅展示用 |
| PNG 缺失 | 跳过该条，其他贴图继续恢复 | 日志 Pin persist: 跳过 id=... png missing；不弹窗 |
| PNG 解码失败/尺寸非法 | 跳过该条；把坏文件改名为 .bad（不删除），索引下次保存去掉该条 | 日志含 id、异常；不阻塞启动 |
| pngFile 是绝对路径或含 .. | 拒绝该条，记日志 | 安全降级；不读任意文件 |
| 索引 JSON 损坏 | 先读 index.json.bak；两者都坏则本次不恢复，保留 PNG 与坏文件 | 日志；启动空贴图，不崩溃 |
| 索引版本 > 当前 | 不恢复（避免误读未来字段），保留文件 | 日志 unsupported version；不影响其他功能 |
| 字段缺失/版本旧 | 按默认值补齐；version 0/缺失当 1 | 无感；可选诊断日志 |
| 屏幕拓扑变化/矩形越界 | 用 MonitorFromRect(MONITOR_DEFAULTTONEAREST) 找最近工作区，夹取 x/y；宽高超工作区按比例缩小；最小 16×16 | 日志 Pin persist: clamped；贴图可见可操作 |
| 保存磁盘满/目录只读 | 保留旧索引；当前贴图继续用；记一次错误，后续变化重试；不静默删旧数据 | 日志 Pin persist: write failed；不阻塞 UI |
| 索引写成功但 PNG 写失败 | PNG 先写，索引后写；反过来不会发生。若 PNG 失败则不提交索引 | 旧状态保持；日志 |
| 进程崩溃在两次写之间 | 旧索引 + 旧 PNG 完整；新 PNG 成为孤儿，启动清理 | 无用户可见损坏；最多丢一次编辑 |
| 恢复时某条创建窗口失败 | 跳过该条，不影响后续；保留索引记录下次再试 | 日志；管理器显示其余贴图 |

重要边界：恢复失败不做「猜测修复」，不把 PNG 当 JSON、不从历史目录反推贴图；宁可少恢复一张，也不生成错误状态。

## 6. 迁移与兼容

- 旧数据：当前版本没有贴图持久化（§1），首次启用时 data/pins 不存在，行为与现在完全一致：不恢复任何贴图、不创建目录、不迁移内存状态。升级前打开的贴图在进程结束时丢失，这是现状，不是本设计的回归。
- 版本号：索引级 version=1；未来新增字段只追加，旧字段不改名。读取时：
  - version 缺失或 0：按 1 处理（兼容最早手工文件）。
  - version 1：正常读取；缺字段用默认值。
  - version > 1：拒绝整份索引并记日志，不尝试部分读取。
- PinRecord / Revision 的兼容：PinRecord 增 Id/Revision 是 App 内部结构变更；PinExportResult 已有 Revision，不改签名。旧调用点只构造 PinRecord 的 PublishPinState 一处（NativePinWindow.cs:336-357），改动面可控。
- 配置：不改现有 config 字段名，不新增 data 根；若实现单决定加 pinPersist 开关，默认 true，旧 config 缺字段按 true 处理（见 §8 Q4）。
- 降级运行：新版本写出的 data/pins 被旧版本忽略；旧版本不会删除它。新版本再次启动可继续读。没有双向迁移负担。
- 不迁移 data/history：贴图持久化不复制、不删除历史；历史仍按自己的配额清理。

## 7. 验收判据

### 7.1 本设计单（只读）门禁

~~~powershell
pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag d2b -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-d2b/"
pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag d2btest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug"
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
~~~

判据：build 0 错 0 警；Core.Tests 全过（2026-11-07 门禁时 305 条，含同事新增，本设计单不新增用例；只增不减）；format rc=0。报告写明跑门禁时刻的 git status 与 HEAD（可能含同事改动）。

### 7.2 实现单必须新增的 Core 测试

新增 PinPersistenceRulesTests（名称可调），至少覆盖：

1. Serialize_RoundTripsAllFields：v1 JSON 序列化/反序列化字段不丢。
2. Deserialize_MissingOptionalFields_UsesDefaults：缺 opacity/sourceKind/revision 时用默认 1/unknown/1。
3. Deserialize_VersionZeroOrMissing_TreatedAsOne。
4. Deserialize_FutureVersion_Rejects。
5. Validate_RejectsRootedOrTraversalPngPath：绝对路径、..、子目录全部拒绝。
6. Normalize_ClampsOpacityAndWindowSize：opacity 0.1→0.25，宽高 <16→16。
7. Clamp_RestoresOffscreenRectIntoNearestWorkArea：负坐标/右下越界。
8. Clamp_PreservesAspectRatioWhenLargerThanWorkArea。
9. BuildPngFileName_ContainsIdAndRevision：文件名格式固定，revision 变则文件名变。
10. SelectForRestore_StopsAtQuota：超过 50 张/128 MiB 不删除，只跳过。

纯规则不依赖 WinUI/GDI；PNG 实际解码仍由 App 层做，Core 只校验签名/尺寸字段与路径。

### 7.3 主人实测步骤（实现单交付时执行）

1. 贴 3 张：剪贴板图片、历史截图、文字卡片；把其中一张拖到屏幕中部、缩放、调透明度并锁定；完全退出 IshWY 再启动。预期：3 张全部恢复，位置/大小/透明度/锁定状态保留，管理器显示 3 张。
2. 关闭其中一张贴图，再退出重开。预期：只剩 2 张；被关闭的那张不复活（除非关闭时进程被强杀，属 §5 边界）。
3. 对一张贴图做标注并点「完成」，确认图片已复制；退出重开。预期：恢复的是标注后的合成图；再进入标注不能撤销重启前的标注（v1 语义）。
4. 把一张贴图拖到屏幕最右下/最左上，再退出重开；若有第二显示器，拔掉后再启动。预期：贴图仍可见、完整落在最近工作区内，不跑到屏幕外。
5. 手工删除 data/pins 下某张 PNG（或把 index.json 改成非法 JSON），再启动。预期：程序正常启动，其他贴图恢复；日志有明确 Pin persist 失败/跳过记录，不崩溃。
6. 打开管理器再关闭，确认贴图不被关掉；隐藏/点击穿透的贴图能在管理器里恢复。

### 7.4 实现切分建议（供后续拆单）

- 步骤 1：Core DTO + 纯规则 + 测试（不改 App 行为）。
- 步骤 2：PinPersistenceService 写路径 + PinRecord Id/Revision + PublishPinState/PinClosed 接线；只写不读。
- 步骤 3：启动恢复 + NativePinWindow restore 重载（位置/状态）；只读已写数据。
- 步骤 4：退出 Flush、配额/清理、失败路径补日志；主人实测。

## 8. 未决问题（5 条，每条给推荐）

1. 标注是否持久化为可再编辑文档？推荐 v1 不做，只存扁平化 PNG（§3.5）。若主人明确要求「重启后还能撤销旧标注」，另立设计单，2B 只保留 editState 字段。
2. 左右导航列表是否持久化？推荐不持久化；恢复为静态 PNG。若要保留，必须持久化历史文件名列表并在恢复时校验文件存在，复杂度与本批次目标不匹配。
3. 隐藏/点击穿透是否原样恢复？推荐原样恢复，管理器可救回。若主人认为「重启后不应有隐形贴图」，可改为恢复时强制 Hidden=false、ClickThrough=false，但这会丢失用户意图；需主人拍板。
4. 是否加关闭持久化的设置开关？推荐实现单加 pinPersist（默认 true，旧配置缺字段按 true），先只落配置不急着加设置页；理由是贴图可能含敏感内容，至少给配置层关闭能力。设置页 UI 另立小单。
5. 配额与清理策略是否接受「超限不删、只停止新增」？推荐 50 张/128 MiB，超限跳过写入并日志，不静默删除。若主人希望自动滚动清理，必须另立「贴图持久化清理」设计，明确提示与恢复入口，不能在本单偷偷删。

## 附：本设计引用

- src/PrtScr.App/Services/PinService.cs
- src/PrtScr.App/Views/NativePinWindow.cs、Window.cs、Export.cs、EditApply.cs、Commands.cs、CloseGuard.cs
- src/PrtScr.App/Views/PinManagerWindow.cs
- src/PrtScr.App/MainWindow.Windows.cs、MainWindow.xaml.cs；App.xaml.cs
- src/PrtScr.Core/ConfigService.cs、HistoryMetadata.cs；src/PrtScr.App/Services/CaptureHistoryService.cs
- Record/plans/B1-editdocument-design.md（Revision 语义与 PinRecord 建议）
- Record/plans/2026-10-28-整改总纲.md（D 阶段顺序与边界）

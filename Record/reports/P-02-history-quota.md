# P-02（C04）历史配额：改动只影响后续截图，清理需显式确认

- 单号：task-2（P-02 / 评审 C04）
- 交付人：maid-check
- 工作树：基于 0b1ca41 之后的整改工作树（未提交）；P-04/P-06/P-09a 等他人改动同时在树内
- 写域：src/PrtScr.App/MainWindow.xaml、MainWindow.xaml.cs、Services/CaptureHistoryService.cs、src/PrtScr.Core/ConfigService.cs、Record/reports/**

## 结论（三行）

1. 改配额不再触发任何删除：NumberBox 变化只保存配置并刷新状态，删除入口只剩「立即清理…」按钮与「清空全部历史…」。
2. 「立即清理…」先预演（真实张数 + 涉及分类 + 每类删/留），用项目已有 ConfirmDialog 确认；取消零删除，确认后按当前配额删除超出部分并回报实际删除数。
3. 保存新截图时的滚动修剪收窄为「只修剪该截图所属分类」，其他分类超限时等用户显式清理（见 §5 待确认 1）。

## 一、做了什么

| 文件 | 改动 |
| --- | --- |
| src/PrtScr.App/Services/CaptureHistoryService.cs | 新增 `GetPrunePlan()` 预演 API（只统计不删）；`PruneNow()` 改为返回实际删除张数；`PruneLocked()` 增加 `onlyCategoryKey` 可选参数并复用 `EnumerateHistoryLocked()`；AddAsync 保存后只按「该截图自己的分类」修剪；新增 `PruneGroup`/`PrunePlan` 记录与分类标题映射 |
| src/PrtScr.App/MainWindow.xaml.cs | `HistoryLimit_ValueChanged` 删除 `PruneNow(value)` 调用（只保留 clamp/Save/刷新/日志）；新增 `PruneHistoryNow_Click`（预演→确认→执行→回报，`_pruneHistoryBusy` 防连点）；`RefreshHistoryStatus` 状态行追加「N 张超出当前配额，可点立即清理」 |
| src/PrtScr.App/MainWindow.xaml | 截图历史卡新增「立即清理…」按钮与说明文案（数量框之后、贴图开关之前） |
| src/PrtScr.Core/ConfigService.cs | Load 后把 `HistoryPerCategory` 夹到 1–200（防手改 config 越界） |

diffstat：4 files changed, 169 insertions(+), 22 deletions(-)（+9/0 XAML、+62/−4 MainWindow.xaml.cs、+95/−18 CaptureHistoryService.cs、+3/0 ConfigService.cs）。

设计要点：
- 预演与实际删除共用同一套「倒序 + CategoryKey 分组」枚举（EnumerateHistoryLocked），避免两处逻辑漂移导致文案张数与实际删除不一致。
- 删除仍在 `_sync` 锁内、仍逐文件 try/catch（单个文件失败不中断其余），HistoryChanged 事件语义不变（PruneNow 仍无条件 Notify）。
- 分类展示名与历史窗保持一致：`窗口 · {进程}` / `常规截图` / `贴图裁剪` / `早期未分类`。

## 二、证据（文件 + 行号 / 命令 + 结果）

### 2.1 验收项逐条对应

1. **改配额后 data/history 文件数不变**
   - src/PrtScr.App/MainWindow.xaml.cs:294-309 `HistoryLimit_ValueChanged`：:298 clamp、:302-303 只写配置、:305-308 仅刷新状态/历史窗/日志；树内已无 `PruneNow` 的此处调用。
   - `grep -n "PruneNow" src`：调用点只剩 src/PrtScr.App/MainWindow.xaml.cs:347（确认按钮内）与 src/PrtScr.App/Services/CaptureHistoryService.cs:115（保存路径的滚动修剪）、定义 :129。
2. **后续截图按新配额保留**
   - src/PrtScr.App/Services/CaptureHistoryService.cs:108-116：写 PNG/元数据后 `lock (_sync) { var saved = ToItem(new FileInfo(path)); PruneLocked(PerCategoryLimit, saved.CategoryKey); }`——按新截图自己的分类滚动保留；分类键从刚写入的文件读回，元数据写失败时与 PruneLocked 的归类一致（归入早期类）。
3. **「立即清理」确认含真实张数与分类；取消零删除**
   - 预演：CaptureHistoryService.cs:138-173 `GetPrunePlan`（:144 clamp 1–200、:150-160 按 CategoryKey 分组算 DeleteCount/KeepCount、:169-172 按删除数倒序返回）。
   - 按钮：MainWindow.xaml.cs:311-360 `PruneHistoryNow_Click`；:312 忙守卫；:319 取预演；:320-324 无超限时仅提示不弹框；:326-331 明细最多列 8 个分类；:336-340 `Views.ConfirmDialog.ShowAsync((UIElement)Content, "立即清理历史截图", message含「将永久删除 {Groups.Count} 个分类里的 {TotalCount} 张旧截图」, $"删除 {TotalCount} 张")`；:341-345 取消→「已取消清理，没有删除任何截图。」且不调用删除；:347 确认后才 `PruneNow(limit)`；:352-354 回报实际删除数，与预演不一致时提示期间文件变化。
   - ConfirmDialog 为项目已有组件（src/PrtScr.App/Views/ConfirmDialog.cs:12，CloseButtonText「取消」、DefaultButton=Close），未新增依赖。
4. **PruneNow 仍可显式调用，但不再由 NumberBox 触发**
   - CaptureHistoryService.cs:128-136 `public int PruneNow(int? perCategory = null)`，锁内执行 `PruneLocked(perCategory ?? PerCategoryLimit)`，返回删除数；NumberBox 处理器内已无调用。
5. **UI 文案**
   - MainWindow.xaml:1135-1143「立即清理…」按钮与说明：「修改保留张数只影响以后新截的图，已有截图不会被删除；点「立即清理…」才会按当前配额删除超出的旧截图，删除前会先列出张数与分类。」
   - 状态行：MainWindow.xaml.cs:264-273 每次刷新按 CategoryKey 统计超出配额数量，追加「其中 N 张超出当前配额，可点「立即清理…」删除」或「当前没有超出配额的旧截图」。

### 2.2 门禁（最终工作树，最后一次编辑之后复跑）

命令均经 `.tmp/build-guard.ps1`（共享构建锁）执行：

- 整包构建（-p:OutDir 绕开被占用 bin）：
  `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p02/`
  → **0 个警告，0 个错误**，已用时间 00:00:12.99；BUILD-GUARD EXIT=0 tag=p02build2。
- 测试：`dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug --nologo`
  → **失败 0，通过 77，跳过 0，总计 77**，99 ms；EXIT=0 tag=p02test2。
- 格式：`dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore`
  → **rc=0**（无输出）；EXIT=0 tag=p02format2。
- 编译检查（任务书四门禁之一）：整包构建已覆盖全部项目编译（同一解决方案、同一配置），未再单独跑 `-t:Compile`；上一轮同树 `-t:Compile` 亦为 0 错 0 警。

产物：`.tmp/out-p02/IshWY.exe`（20:46 生成，含本次改动）。

### 2.3 越界检查

- `git status --porcelain`：本人只动上述 4 个文件；未改 .gitignore/README/docs，未新增依赖，未 commit。
- 未改历史目录结构、PNG/.json 字段、HistoryChanged 语义（PruneNow/AddAsync/DeleteMany 仍各 Notify 一次）。
- 未跑 `--smoke`（会写真实 data/），未启动 GUI（运行中的 IshWY.exe PID 29836 未触碰）。

## 三、进行中

无。代码与报告已完成，等待 lead 统一复验与主人实测。

## 四、风险与遗留

1. **无自动化测试覆盖**：`GetPrunePlan`/`PruneNow`/`PruneHistoryNow_Click` 位于 PrtScr.App（WinUI），Core.Tests 只引用 PrtScr.Core，无法为 App 服务加单测；本单验证靠编译 + 静态走查 + 主人实测。若后续把历史配额逻辑下沉到 Core（纯 IO 服务），可补「预演不删文件、确认后删数一致、取消零删除」三条单测。
2. **UI 未真机点按**：当前有 IshWY.exe 实例占用全局热键与 bin/x64/Debug，未启动新 GUI 验证；ConfirmDialog 文案换行、按钮位置属视觉项，需主人实测确认。
3. **工作树未冻结**：P-04/P-06/P-09a 并行改动同在树内，本次门禁是「混合树」结果；最终裁决需 lead 在冻结树、同 SHA 下复跑。
4. **保存路径修剪口径变更（需拍板）**：见 §5-1。若 lead 认为应回到「保存时全局修剪」，回退只需把 CaptureHistoryService.cs:113-115 改回 `PruneLocked(PerCategoryLimit)`（保留 `onlyCategoryKey` 参数不影响 PruneNow）。
5. **预演开销**：GetPrunePlan 会读取全部历史项的元数据与 PNG 头（与打开历史窗同量级）；历史文件很多时点击「立即清理…」可能有短暂卡顿（锁内 IO，UI 线程）。如需优化可把预演移到后台线程并加「正在统计」提示，但会扩大改动面。
6. **PruneNow 异常面**：若 StorageDirectory 枚举本身抛异常（如权限问题），PruneNow 无兜底 try/catch（改动前同样存在）；按钮路径下会以 async void 异常结束。建议后续把枚举包进 try/catch 并回退为 0 删除 + 日志，本单未动（避免顺手改语义）。
7. **ConfigService 夹取为额外改动**：任务书未点名，但可防手改 config 写出 0/999 导致 NumberBox 显示非法配额；现有 7 条配置测试均不涉及越界，77/77 不受影响。如认为越界请回退 ConfigService.cs:227-229。

## 五、待确认（给 lead）

1. **保存时是否只修剪本分类？** 本单实现为：调低配额后，后续截图只按新配额滚动修剪「该截图所属分类」，其他分类的超限旧图保留到用户点「立即清理…」。理由：MainWindow.xaml:1141 的文案承诺「已有截图不会被删除」，若保存仍全局修剪，调低配额后随便截一张图就会静默删掉其他分类旧图，与 C04 意图冲突。若要求保持原「保存时全局修剪」，按 §4-4 一行回退。
2. **ConfigService 夹取是否保留**（§4-7）。
3. **是否需要把「预演/清理」做成可单测的 Core 服务**（§4-1），本单未做。
4. **主人实测安排**：本机可运行 `.tmp/out-p02/IshWY.exe`（其 data 目录为 `.tmp/out-p02/data`，与正式数据隔离）；但它与运行中的实例会争抢全局热键，建议先退出正在运行的 IshWY.exe 或仅在设置页操作。

## 六、主人实测步骤（建议 5 分钟）

准备：无需退出正式实例即可先做「纯设置」部分；要测截图滚动保留时，建议先退出正在运行的 IshWY.exe，再双击 `.tmp\out-p02\IshWY.exe`（数据写在 `.tmp\out-p02\data\history`，不碰正式数据）。

1. **改配额不删图**：设置 → 截图历史，记下 `data\history` 当前 PNG 数量（本工作树构建下为 `.tmp\out-p02\data\history`），把「每个分类保留张数」从 30 改成 1。预期：该目录 PNG 数量**一张不少**；状态行出现「其中 N 张超出当前配额，可点「立即清理…」删除」。
2. **确认框内容**：点「立即清理…」。预期：弹出确认框，标题「立即清理历史截图」，正文含真实分类名（如「窗口 · chrome：删 29 张 / 留 1 张」「常规截图：…」）与合计「将永久删除 X 个分类里的 Y 张旧截图」，主按钮写「删除 Y 张」。
3. **取消零删除**：点「取消」（或 Esc）。预期：状态行「已取消清理，没有删除任何截图。」，目录文件数仍不变。
4. **确认才删**：再次点「立即清理…」→「删除 Y 张」。预期：只删除各分类最旧的超出部分，状态行「已按配额清理 Y 张旧截图（每类保留 1 张）。」；打开截图历史看每类只剩 1 张。
5. **后续截图按新配额**：在配额=1 的情况下截一张图（新实例热键可能被占用，可用主界面截图按钮）。预期：该图所属分类保留最新 1 张（新图在、旧图被滚动删除），**其他分类不受影响**；状态行不再提示超出配额。
6. **显式入口仍在**：调回较大配额（如 30）不应恢复已删图片；再改小并点「打开截图历史」确认窗口内容与状态行一致。

## 七、涉及文件与行号索引

- src/PrtScr.App/Services/CaptureHistoryService.cs
  - :108-116 AddAsync 保存后按本分类修剪
  - :128-136 PruneNow（返回删除数，显式入口）
  - :138-173 GetPrunePlan（预演）
  - :259-293 PruneLocked（onlyCategoryKey，返回删除数）
  - :294-305 EnumerateHistoryLocked（共用排序/分类）
  - :307-313 CategoryTitle
  - :461-472 PruneGroup / PrunePlan
- src/PrtScr.App/MainWindow.xaml.cs
  - :30 _pruneHistoryBusy
  - :255-277 RefreshHistoryStatus（超出配额提示）
  - :294-309 HistoryLimit_ValueChanged（不再删除）
  - :311-360 PruneHistoryNow_Click
- src/PrtScr.App/MainWindow.xaml :1135-1143（按钮 + 说明）
- src/PrtScr.Core/ConfigService.cs :227-229（HistoryPerCategory 夹取）

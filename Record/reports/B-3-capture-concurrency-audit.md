# B-3 采集层并发与结果边界审计

- 任务：task-22 / B-3（只读审计，不改源码）
- 日期：2026-10-30
- 工作区：E:/Explore/GoodGood/PrtScr
- 执行 HEAD：69616cc chore(release-prep): 版本号 1.3.1 与 README 更新，补记冒烟通过（不发布）
- 审计人：maid-capture
- 输入：src/PrtScr.Capture/WgcWindowCapture.cs、ScreenCapturer.cs、DesktopDuplicationCapture.cs、WindowCapture.cs；src/PrtScr.App/Services/CaptureService.cs；调用链补充 MainWindow.Capture.cs、MainWindow.Hotkeys.cs、MainWindow.xaml.cs、MainWindow.Windows.cs、App.xaml.cs

## 一、做了什么

1. 读上述 5 个指定输入文件，并顺着调用链补读 App 层热键/启动/取景确认路径，逐项核对三项疑点的线程归属与失败出口。
2. 对三项疑点分别给出「可达/不可达 + 证据（文件:行 + 线程/调用链）+ 最小修法 + 不修风险」。
3. 跑本单三条门禁（见 2.1），全部通过；未修改任何源码、未跑 --smoke、未 git commit/push。
4. 报告落盘本文件；任务在报告写完后收单。

## 二、证据

### 2.1 门禁与工作树

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 整包构建 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p22audit -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-p22/" | 0 警告 0 错误，19.16s，BUILD-GUARD: EXIT=0 |
| 单元测试 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag p22test -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | 通过 184 / 失败 0 / 跳过 0，91ms，EXIT=0 |
| 格式 | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | rc=0 |

工作树没有本单的源码改动：git status 仅显示他人的 Record/ProjectLog.md、Record/plans/2026-10-28-整改总纲.md，以及未跟踪的 .tmp/、src/PrtScr.Core/CaptureResult.cs、CaptureScenario.cs、CaptureSessionState.cs、src/PrtScr.Core.Tests/CaptureResultTests.cs、CaptureRulesTests.cs、主人修改素材/。未运行 --smoke，未启动/终止 IshWY.exe。

### 2.2 审计①：EnsureMonitorPool 并发首调覆盖 state —— 可达（P2）

**判定：可达。** 两个线程都可能在锁内查表时看不到同一个 HMONITOR，随后各自创建 WGC 会话；后注册者无条件覆盖先注册者，先注册的 state 不会进入任何清理分支。

代码事实（src/PrtScr.Capture/WgcWindowCapture.cs）：

- :344 EnsureMonitorPool 入口；:346-350 只在锁内查表：
      lock (_poolLock)
      {
          if (_monitorPools.ContainsKey(hMonitor))
              return true;
      }
- :352-399 全部创建动作在锁外：CreateItemForMonitor(:358)、GetOrCreateDevice(:365)、CreateFreeThreaded(:372)、CreateCaptureSession(:374)、StartCapture(:399)。
- :401-405 再次进锁，但不检查是否已被并发注册，直接覆盖：
      lock (_poolLock)
      {
          _monitorPools[hMonitor] = state;
      }
      registered = true;
- :413-422 finally 仅在 registered == false 时释放 session / pool / item。先注册者 registered 已为 true，finally 不会释放；被覆盖的 state 从此只被其 FrameArrived 回调与 GC 引用。

调用点与线程：

| 调用点 | 所在线程 | 依据 |
| --- | --- | --- |
| src/PrtScr.App/Services/CaptureService.cs:147（StartCaptureAsync） | UI 线程 | OnHotkeyPressed 由 DispatcherQueue.TryEnqueue 投递（MainWindow.Hotkeys.cs:59-63），StartCaptureAsync 在 :131 被 await |
| src/PrtScr.App/Services/CaptureService.cs:348（CaptureFullScreenPngAsync） | UI 线程 | OnFullScreenPressed 由 DispatcherQueue.TryEnqueue 投递（MainWindow.Hotkeys.cs:108），:38/:44 调用 CaptureFullScreenPngAsync |
| src/PrtScr.App/Services/CaptureService.cs:509（WarmUpNative 内） | 线程池 | WarmUpNative 在 :483 起 Task.Run；调用点 MainWindow.xaml.cs:154（OnLoaded）、MainWindow.Windows.cs:227（PrepareHiddenStartup） |
| src/PrtScr.Capture/WgcWindowCapture.cs:450（GetLatestMonitorFrameAsync 内） | UI 线程 | 调用方 CaptureService.cs:203 在 UI；WGC 内部 await 无 ConfigureAwait(false)，续体回 UI |

竞态时序（预热线程 vs 首次 F1）：

1. 预热 Task.Run 进入 EnsureMonitorPool，:346-350 查表为空。
2. UI 的 F1 在预热尚未 :403 注册前进入 EnsureMonitorPool，同样查表为空。
3. 两边各自创建 item/pool/session 并 StartCapture。
4. 先完成者执行 :401-405，registered=true，返回 true；后在 :401-405 覆盖字典。
5. 被覆盖的 state 已经 StartCapture，FrameArrived 仍挂在池上，但字典不再引用；只有 GC/终结器可能回收，时间不确定。
6. 结果：同一 HMONITOR 同时存在两条 WGC 常驻会话与两个帧池，违背「一个显示器一个池」不变式；没有任何冲突日志。

严重度：P2。需要「启动预热线程仍在创建」与「用户立即截图」重叠；不是每次发生，但一旦发生会长期多占 GPU/内存，且现有日志无法归因。

最小修法（只改 WgcWindowCapture.cs:401-405）：

      lock (_poolLock)
      {
          if (_monitorPools.ContainsKey(hMonitor))
          {
              // 另一个线程已完成注册：本次新建的 session/pool/item 不接管
              Log?.Invoke($"WGC: 并发首调 {hMonitor}，复用已注册的常驻池");
              return true;   // registered 仍为 false，finally 会释放本次新建对象
          }

          _monitorPools[hMonitor] = state;
      }
      registered = true;

C# 中 lock 内 return 会先释放锁再执行 finally，因此本次新建的 session/pool/item 仍会走 :413-422 的逆序释放。若希望连「重复创建」都避免，可改为 per-HMONITOR 的 SemaphoreSlim 或把创建链整体串行化；但二次检查已能消除「覆盖 + 孤儿会话」这一实际危害。

不修风险：启动瞬间并发或后续新增后台调用时，泄漏一条常驻捕获会话；WGC 帧池持续收帧，GPU/内存额外占用；被覆盖池的帧无人消费，可能积压；现象隐蔽，只能靠 GPU/内存曲线或额外日志发现。

### 2.3 审计②：WGC 窗口捕获与显示器池共用同一 D3D11 immediate context —— 可达（P2）

**判定：可达。** 两条路径共用同一 ID3D11Device / ID3D11DeviceContext，拷贝路径没有任何互斥；窗口捕获在线程池线程执行，显示器常驻池拷贝在 UI 线程执行，二者可以重叠。

代码事实：

- WgcWindowCapture.cs:122-126 静态缓存字段：
      private static readonly object _deviceLock = new();
      private static IDirect3DDevice? _cachedWinrtDevice;
      private static nint _cachedDevice;
      private static nint _cachedContext;
- :128-173 GetOrCreateDevice 在 _deviceLock 内只执行一次 D3D11CreateDevice；:135-137 创建标志只有 DeviceFlagBgraSupport（:119 = 0x20），没有 D3D11_CREATE_DEVICE_SINGLETHREADED；:171 缓存 context。
- 窗口路径：:199 CaptureWindowDetailedAsync → :218 Task.Run(CaptureWindowCoreAsync, ct)；:248 GetOrCreateDevice 取到同一组 device/context；:256 CaptureCoreAsync；:734 CopyFrameToBitmap(device, context, frame)。
- 显示器路径：:475 CopyFrameToBitmap(state.Device, state.Context, frame)；state.Device/Context 在 :384-391 由同一 GetOrCreateDevice 结果写入。
- 实际调用：:801 CopyResource、:804 Map、:845 Unmap，均在 CopyFrameToBitmap 内（:763 起）。
- 锁范围只有三把：_deviceLock 只保护首次创建（:130），_poolLock 保护字典（:346/:401/:427/:445/:451/:575），state.FrameLock 保护 Latest（:499/:509/:584）；没有任何锁包住 CopyResource/Map/Unmap。
- FrameArrived → RetainLatest(:487-504) 只做 TryGetNextFrame/Dispose，不碰 context，因此冲突只可能发生在两条拷贝路径之间。

线程归属：

- 窗口路径：CaptureWindowDetailedAsync 显式 Task.Run（:218），后续 await 无 ConfigureAwait(false)，续体留在线程池；CopyFrameToBitmap 在线程池线程。
- 显示器路径：CaptureService.cs:203 await GetLatestMonitorFrameAsync（UI 线程发起）；WgcWindowCapture.GetLatestMonitorFrameAsync 的 await 无 ConfigureAwait(false)，续体回 UI；:475 CopyFrameToBitmap 在 UI 线程。

可达链（不是理论上的空想）：

1. 取景窗按 Enter 确认：CaptureOverlayWindow.Capture.cs:557 HideForCapture()，:558 await CaptureSelectionAsync(session)。
2. 窗口捕获分支 :317 / :359 调用 WindowCapture.CaptureDetailedAsync；若 PrintWindow 失败或超过 2000ms，会转入 WGC 窗口捕获，在 Task.Run 中执行（WgcWindowCapture.cs:218），并最终到 :734 用共享 context 拷贝。
3. 取景窗已隐藏但应用仍在运行，全屏快速截图热键仍可用：MainWindow.Capture.cs:22 OnFullScreenPressed，:38 调 CaptureFullScreenPngAsync；:26 的 _quickCaptureBusy 只互斥快捷全屏自身，不检查 _isCapturing，也不与取景确认互斥。
4. CaptureFullScreenPngAsync → CaptureService.cs:348 EnsureMonitorPool、:349 CaptureMonitorBestAsync → :203 GetLatestMonitorFrameAsync → :475 显示器拷贝。
5. 两条拷贝重叠：线程池的窗口 WGC CopyFrameToBitmap 与 UI 的显示器 WGC CopyFrameToBitmap 同时操作同一 immediate context。
6. 另有一条同源路径：CaptureService.cs:305 CaptureForegroundWindowAsync 在 BitBlt 黑屏兜底时也会起窗口 WGC（Task.Run），可能与显示器拷贝或另一次窗口捕获重叠。

澄清「同帧」：两条路径各自 CreateTexture2D staging、各自持有不同帧，不是同一帧重复提交；危险是同一个非线程安全的 immediate context 被两个线程并发调用。D3D11 的 device context 默认不支持多线程并发（Microsoft Learn「Direct3D 11 threading considerations」），需要外部同步或 ID3D11Multithread::SetMultithreadProtected。

严重度：P2。触发需要「取景确认的窗口 WGC 在飞」与「全屏快速截图」重叠；概率低，但失败表现可能是 device removed、黑帧、Map/CopyResource 异常，随后被上游当黑屏走回退链，难复现难归因。

最小修法（二选一，只改 WgcWindowCapture.cs）：

      private static readonly object _contextLock = new();

      // CopyFrameToBitmap 内，包住 CopyResource → Map → 像素拷贝 → Unmap
      lock (_contextLock)
      {
          deviceContext->CopyResource(...);
          deviceContext->Map(...);
          try
          {
              // LockBits/拷贝
          }
          finally
          {
              deviceContext->Unmap(...);
          }
      }

或：首次创建 device 后 QI ID3D11Multithread 并调用 SetMultithreadProtected(TRUE)，让驱动串行化所有 context 调用；这需要新增接口声明与失败回退，代码量大于静态锁。锁的粒度建议只包 context 调用，Bitmap 分配可放锁外以减少 UI 停顿；像素读取若也放锁外，需保证 Map 出来的指针在锁内完成读取后再 Unmap。

不修风险：偶发捕获失败/黑帧；与审计③叠加时，失败会被 IsNearlyBlack 折叠成「黑屏」并继续回退，最终可能把黑图交给用户；没有直接日志能把「D3D 并发」和「窗口本身黑」区分开。

### 2.4 审计③：ScreenCapturer BitBlt 返回值未检查 —— 可达（P1，静默错误结果）

**判定：可达。** BitBlt 失败只返回 FALSE，不会抛异常；当前代码丢弃返回值、不取 GetLastError、不记日志，失败被静默折叠成一张内容未定义的位图。

代码事实（src/PrtScr.Capture/ScreenCapturer.cs:31-72）：

- :36 GetDC(default) 已判空（:37-38），这一条此前 P-09a 已修好。
- :42 创建 Bitmap；:52-55 调用 BitBlt 并丢弃 BOOL：
      _ = PInvoke.BitBlt(
          hdcDest, 0, 0, width, height, hdcScreen, x, y,
          (ROP_CODE)(ROP_SRCCOPY | ROP_CAPTUREBLT));
- :56-59 仅 ReleaseHdc；:61-65 catch 只负责 bitmap.Dispose + throw；BitBlt 返回 0 不会进入 catch。
- :67 正常返回 bitmap；:69-72 finally ReleaseDC。
- 失败时位图内容是「GDI+ 新建位图的未定义内容」，通常是全 0（透明黑），但没有保证；也没有任何错误码可供上层判别。

下游如何被折叠：

- CaptureService.cs:319-332 CaptureMonitorByBitBlt 返回非空 Bitmap；:324-326 直接 Clone，没有失败分支。
- CaptureMonitorBestAsync（CaptureService.cs:198-282）：WGC 黑 → :218 回退 DXGI；DXGI 黑 → :232 回退 BitBlt；BitBlt 黑 → :241-249 再试普通 BitBlt；仍黑 → :259 前台窗口 WGC；前台也失败 → :274-275 把 BitBlt 的 fallback 以 (fallback, true) 交给上层。
- 取景路径：StartCaptureAsync :149 拿到 captured，:151 只在 null 时中止；:157 仅把 black 写进日志（frozenBlack），:163 EnsureOverlay，:172 Prepare 照常展示。黑屏冻结图会进入取景器，可被保存/复制/历史记录。
- 一键全屏路径：MainWindow.Capture.cs:38 拿到 result，:39 在 Png 为 null 或 LikelyBlack 时短暂切前台重试；:57-62 仍黑则提示「当前画面像是独占全屏游戏」，不会保存，但把 BitBlt 失败误诊为游戏保护，用户和日志都拿不到真实原因。
- 若失败位图不是全黑而是未初始化垃圾，IsNearlyBlack（:454-473，16×16 采样，亮度 >24 的像素占比 <2% 才判黑）可能判为「正常」，垃圾图会被当作有效截图保存，问题更隐蔽。

可达条件：任何 BitBlt 返回 0 的场景，例如 UAC/安全桌面切换、锁屏/远程桌面会话、显示模式切换或热插拔、无效源矩形、驱动限制；代码没有任何前置检查或错误分支。与 A 批次「不静默降级、失败要可判别」直接冲突。

严重度：P1（结果正确性，非永久阻断）。概率低于审计①②，但一旦命中会产出错误内容或误导性提示，且没有日志可归因。

最小修法（Capture 层 + App 层配套）：

Capture 层（ScreenCapturer.cs:52-55）：

      var ok = PInvoke.BitBlt(
          hdcDest, 0, 0, width, height, hdcScreen, x, y,
          (ROP_CODE)(ROP_SRCCOPY | ROP_CAPTUREBLT));
      if (!ok)
          throw new Win32Exception(
              Marshal.GetLastWin32Error(), "BitBlt 失败");

现有 :61-65 的 catch 已负责 Dispose，抛异常即可把失败带出 Capture 层；异常类型与错误码也能被 App 记录。

App 层（必须配套，否则异常只会冒到 OnHotkeyPressed 的兜底 catch，MainWindow.Capture.cs:160-163 只记日志）：

      private static Bitmap? CaptureMonitorByBitBlt(WindowRect monitor, bool includeLayered = true)
      {
          try { ... } catch (Exception ex) { App.Log($"Capture: BitBlt 失败 {ex.Message}"); return null; }
      }

并把 CaptureMonitorBestAsync 的 :236-275 改为：BitBlt 返回 null 时不再把它当 fallback；普通 BitBlt 也失败时直接走前台 WGC；前台 WGC 也失败则返回 null（或明确的失败原因），不要把黑图以 (fallback, true) 当成功交出。StartCaptureAsync 已有 :151-155 的 null 分支，可在此基础上加失败提示；QuickCapture 的 LikelyBlack 误诊文案也应改成「采集失败」而不是「独占全屏游戏」。

不修风险：用户可能保存黑图或垃圾图；失败统计不可归因；与审计②的 D3D 并发失败叠加时，上层只会看到「黑屏 → 回退 → 仍黑」，无法判断是 BitBlt 失败、WGC 失败还是窗口本身黑。

### 2.5 交叉影响

- 审计①与②都发生在「启动预热 / 首次截图」附近，共享 WGC 设备与池；①制造多余会话，②制造并发 context 调用，两者都不会被现有日志显式区分。
- 审计③是结果边界的最后一道漏斗：即使②偶发失败，只要回退到 BitBlt 并返回 FALSE，最终仍可能以黑图/垃圾图形式被消费；因此③的修复优先级应高于②的「降噪」价值。
- 三项都不属于 A 批次已修的「永久 busy」类阻断；B 批次可以在不阻断 A 放行的前提下开修补单。

## 三、进行中（交接用）

- 本单审计与报告已完成；尚未修改任何源码，尚未开修补单。
- 任务状态：task-22 / B-3，本报告写完后用最新 revision 收单；没有 attempt_id（claim 时任务已在我名下且 ready=false）。
- 下一步第一件事（交给 lead 决策）：确认是否开 B 批次修补单。若开，建议按「审计③ → 审计② → 审计①」顺序立案：
  1. 审计③：src/PrtScr.Capture/ScreenCapturer.cs:52-55 + src/PrtScr.App/Services/CaptureService.cs:198-282、319-332；需要 App 层写域。
  2. 审计②：src/PrtScr.Capture/WgcWindowCapture.cs:763-853，选静态 context 锁或 ID3D11Multithread。
  3. 审计①：src/PrtScr.Capture/WgcWindowCapture.cs:401-405，二次检查 + 冲突日志。
- 下一步该读的记录：本文件第 2.2-2.4 节；若要动②，先读 WgcWindowCapture.cs:763-853 的完整 CopyFrameToBitmap；若要动③，先读 ScreenCapturer.cs:31-72 与 CaptureService.cs:198-282。
- 本单没有新增临时文件；.tmp/build-guard.ps1 与 .tmp/out-p22/ 是既有构建设施，未清理。

## 四、风险与遗留

1. 本单是静态审计：未跑 GUI、未跑 --smoke、未做故障注入；三项的「可达」由代码路径与线程归属推出，未在实机复现。②的重叠窗口依赖真实时序，③依赖 BitBlt 真实失败，二者都没有现场日志。
2. Core.Tests 184/184 全过，但现有测试不覆盖 WGC 并发、D3D context 共享或 BitBlt 失败；本单没有新增测试（审计单只读）。
3. 审计①中「被覆盖 state 的存活时间」依赖 WGC/CsWinRT 的 RCW 终结与 COM 引用，无法从代码精确断言；结论只到「非确定性释放、可能长期占用」。
4. 审计②引用 D3D11 官方线程模型（非线程安全 immediate context）；本机没有在并发下抓到 device removed，故只判 P2，不判阻断。
5. 审计③的失败位图内容取决于 GDI+ 分配，可能是全黑也可能是未初始化垃圾；报告已按两种结果分别说明，未把「一定黑图」写死。
6. DesktopDuplicationCapture 的线程/资源路径未在本单三项范围内展开；若后续要审 DXGI 与 WGC 的并发，需要另立单。
7. 未 git commit/push；工作树中他人的改动与本单无关。

## 五、待确认

1. 严重度与批次：审计③是否按 P1 进入 B 批次必修？①②是否按 P2 与③同批处理，还是留到 v1.4？
2. 修补单拆分：建议拆两张——B-3a「WGC 并发（①②，只改 WgcWindowCapture.cs）」、B-3b「BitBlt 失败可见性（③，改 ScreenCapturer.cs + CaptureService.cs）」。是否按此拆？还是合成一张 B-3-fix？
3. 写域与归属：③必须改 App 层（CaptureService.cs 及可能的 MainWindow.Capture.cs 文案），是否交给 maid-impl？我只做 Capture 层，还是整单给我？
4. ②的实现选择：静态 context 锁会串行化两条拷贝，最坏增加一次拷贝等待（毫秒级）；ID3D11Multithread 改动更大但更贴近 D3D 官方方案。选哪个？
5. ①的修法：二次检查 + 冲突日志（改动 5 行）是否足够？是否需要额外断言/测试钩子？
6. 验收方式：这三项没有稳定单测，是否接受「代码审查 + 主人实机热键复现 + 日志关键字」作为验收？是否需要在修复单里加一次性调试日志（如 context 并发计数、BitBlt 失败码）供主人复测？

## 六、总评

B 批次需要开修补单。三项均可达：③是结果正确性问题（P1，静默黑图/垃圾图，建议优先），②是共享 D3D immediate context 的并发风险（P2），①是同一 HMONITOR 双常驻池的并发覆盖（P2）。三项都没有 A 批次「永久 busy」级别的阻断，不需要回滚 A 批次；建议以两张修补单收口：①②同属 WGC 并发（单文件），③跨 Capture/App 两层（失败可见性）。

## 七、主人实测步骤（修复后验收，3-5 条）

1. 启动竞态（①）：启动 IshWY 后立刻连续按 Alt+A（默认截图热键）5 次，每次 Esc 取消；预期不再出现「第一次截图黑屏/卡顿」，app.log 中若出现「并发首调 … 复用已注册的常驻池」只应是偶发一条，且不重复出现成对会话。
2. D3D 并发（②）：用 Alt+A 选一个被遮挡的窗口，按 Enter 确认；在确认取图尚未结束时立刻按 Alt+D（默认全屏快速截图）触发一次全屏；重复 10 次。预期两次截图都正常，app.log 无 device removed、无「WGC 帧拷贝返回空」、无黑帧回退记录。
3. BitBlt 失败可见性（③）：在远程桌面/锁屏切换/UAC 提示或切换显示分辨率的瞬间按 Alt+A；预期失败时给明确提示而不是展示黑屏，app.log 含「BitBlt 失败」及 Win32 错误码；不得把黑图写入历史或剪贴板。
4. 正常回归：不并发的情况下，Alt+A 常规模式截一块区域、窗口模式截一个窗口，各保存一次；Alt+D 全屏截图一次；三张图内容、历史记录、剪贴板均正常。
5. 日志抽查：打开 data/logs/app.log，搜索 WGC:、BitBlt、Capture: frozen、engine=；预期无未解释的 WGC: EnsureMonitorPool 失败、无 device removed、无「所有采集引擎均失败」伴随黑图保存。

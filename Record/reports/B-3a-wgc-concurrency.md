# B-3a WGC 并发修复（task-25）

状态：已收单（task-25 completed，revision 3）。门禁全绿：整包 build 0 警 0 错、Core.Tests 206/206、全量 format rc=0。

## 一、做了什么

- 只改 src/PrtScr.Capture/WgcWindowCapture.cs，未改 App 层、未改 csproj/最低版本、未新增依赖、未提交 git。
- 审计①：EnsureMonitorPool 在锁内注册前增加 ContainsKey 二次检查（:409-431）。发现另一线程已注册同一 HMONITOR 时，不覆盖已有 state，输出一条冲突日志，返回 true，并让 finally（:438-447）按 session → pool → item 逆序释放本次新建对象。
- 审计②：新增静态叶子锁 _contextLock（声明与锁序注释 :340-346），把同一帧的 CopyResource → Map → 像素拷贝 → Unmap 整段包进锁（:851-878）。Bitmap 创建与 LockBits 在锁外（:830-842、:882），Unmap 由内层 try/finally 保证（:874-877），异常时 Bitmap 释放（:885-889）。未使用 ID3D11Multithread。
- 报告本文件；未改 ScreenCapturer（审计③归 task-26/B-3b）。

## 二、证据

### 2.1 改动范围

- 修改文件：src/PrtScr.Capture/WgcWindowCapture.cs（git diff：+83 / -32）。
- 关键行：_contextLock :346；EnsureMonitorPool 首次查表 :354-358；创建对象 :360-407；二次注册 :409-420；冲突日志 :422-428；finally 释放 :438-447。
- CopyFrameToBitmap 关键行：Bitmap :830；LockBits :834-836；lock :851；CopyResource :853；Map :854；像素拷贝 :863-871；Unmap :876；UnlockBits :882；异常 Dispose :885-889；Map 失败 Dispose+null :891-895；staging 释放 :899-902。
- 文件为 CRLF，914 行（脚本核对 914 个 CRLF、无 lone LF），35916 字节。

### 2.2 审计①：finally 释放路径对照

| 路径 | registered | finally 动作 | 结果 |
| --- | --- | --- | --- |
| 首次成功注册（无并发） | true | 跳过 | session/pool/item 由常驻表 state 持有 |
| 并发后到（二次检查命中） | false | session?.Dispose → pool?.Dispose → ReleaseCaptureItem(item) | 本次新建对象逆序释放，复用已注册 state |
| CreateItemForMonitor 返回 null | false | 同上（session/pool 为 null） | item 为空，安全返回 false |
| GetOrCreateDevice 返回 null | false | 同上 | 本次 item 被释放，返回 false |
| CreateCaptureSession / StartCapture 抛异常 | false | catch 记日志后 finally 逆序释放 | 不留下半初始化会话 |
| 首次查表已命中 | 未进入 try | 无 | 直接 return true，无新建对象 |

冲突日志在锁外调用（:426），不持 _poolLock 调外部回调。二次检查与写入在同一 lock 内完成，因此注册动作原子。

### 2.3 审计②：锁区间图

窗口捕获（线程池）：
  CaptureWindowDetailedAsync
    → Task.Run(CaptureWindowCoreAsync)
    → CaptureCoreAsync
    → CopyFrameToBitmap
        new Bitmap + LockBits        （锁外）
        lock(_contextLock)
            CopyResource
            Map
            像素拷贝
            Unmap                   （内层 finally）
        unlock
        UnlockBits                    （锁外）

显示器常驻池（UI 线程）：
  GetLatestMonitorFrameAsync
    → CopyFrameToBitmap(state.Device, state.Context, frame)
        new Bitmap + LockBits        （锁外）
        lock(_contextLock)
            CopyResource
            Map
            像素拷贝
            Unmap
        unlock
        UnlockBits                    （锁外）

两条路径使用同一个 GetOrCreateDevice 缓存的 D3D11 device + immediate context；各自持有不同帧与 staging，锁只负责让同一 immediate context 上的 CopyResource/Map/像素拷贝/Unmap 串行，不会把两条路径的帧合并。RetainLatest 只操作帧池与 Latest，不进入 context 锁。

### 2.4 死锁与锁序分析

- _contextLock 是叶子锁：持锁期间只调用 CSWin32 的 CopyResource、Map、Unmap 与 Buffer.MemoryCopy，不获取 _poolLock、_deviceLock、state.FrameLock，不 await，不分配 Bitmap/数组，不调用 Log。
- _poolLock 与 state.FrameLock 都不在 _contextLock 内获取；_contextLock 也不在它们内部获取（EnsureMonitorPool 的 _poolLock 区间只做字典查写，CopyFrameToBitmap 不碰 _poolLock）。因此不存在环。
- 两个捕获线程最多在 _contextLock 上串行等待；窗口捕获先持锁时，UI 线程可能在 CopyFrameToBitmap 处等待数毫秒（拷贝一个 4K 帧），但不会等待 await 或 UI 消息。
- Map 失败由 CsWin32 的 ThrowOnFailure 抛异常；此时 Unmap 不执行是正确的（没有成功映射）。Map 成功后的所有路径（pData null、拷贝异常、正常返回）都会经过 Unmap。

### 2.5 门禁

| 门禁 | 命令 | 结果 |
| --- | --- | --- |
| 单项目编译 | dotnet build src/PrtScr.Capture/PrtScr.Capture.csproj -c Debug --nologo | 0 警告 0 错误，2.34s |
| Gate1 整包 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag b3a -Cmd "dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-b3a/" | 0 警告 0 错误，16.06s，BUILD-GUARD: EXIT=0 |
| Gate2 测试 | pwsh -NoProfile -File .tmp/build-guard.ps1 -Tag b3atest -Cmd "dotnet test src/PrtScr.Core.Tests/PrtScr.Core.Tests.csproj -c Debug" | 通过 206 / 失败 0 / 跳过 0，103ms，EXIT=0 |
| Gate3 格式（全量） | dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore | rc=0（2026-10-30 22:07 最新树复跑） |
| Gate3 格式（历史诊断） | 同上加 --include src/PrtScr.Capture/WgcWindowCapture.cs | rc=0（阻断期间用于证明本单文件干净） |

Gate3 时序说明：本单中途曾看到全量 format rc=2，唯一报错是 src/PrtScr.App/Views/NativePinWindow.CloseGuard.cs 与 src/PrtScr.App/Views/NativePinWindow.EditHistory.cs 全 LF（两文件 git status 均为 ??，不在 task-25 写域，属他人未完成文件）。P-13b 主人归一后，本单于 2026-10-30 22:07 用最新树全量复跑 rc=0；该短暂红不是本单改动导致。

## 三、进行中（交接用）

- 已完成：WGC 两处并发修改、单项目编译、Gate1、Gate2、全量 format、本报告；task-25 已 complete（revision 3，expected_revision 2 收单）。
- 未完成：无。B-3b（BitBlt 静默失败，task-26）不在本单，未动 ScreenCapturer.cs。
- 提交状态：本单未 commit，提交由 lead 统一分批；App 层两个 NativePinWindow 文件未由本单修改。
- 后续接线注意：_contextLock 只覆盖 CopyFrameToBitmap 内的 CopyResource/Map/像素拷贝/Unmap；若以后新增对同一 immediate context 的调用，必须进入同一把锁，否则并发风险回归。

## 四、风险与遗留

- _contextLock 的临界区包含 GPU CopyResource、Map、CPU 像素拷贝与 Unmap，4K 帧可能让 UI 线程等待数毫秒；没有实测帧率/卡顿，需主人在真实机器验证。
- 并发首调冲突路径仍会短暂创建并启动第二条会话再释放（session → pool → item），存在一次启动/停止抖动；它比永久双会话安全，但若预热与 UI 首次 F1 高频重叠，可后续改为 per-HMONITOR 信号量避免创建。
- 并发首调冲突目前只写日志、不计数；若该日志在正常启动时频繁出现，说明预热与 UI 首次 F1 的并发窗口比预期大，后续可考虑把预热改成显式等待注册。
- 锁只覆盖当前唯一的两条 context 拷贝路径；代码演进若新增 context 使用点，需要人工保持锁纪律。
- 本次未做故障注入/压力测试，未跑 GUI 与 --smoke；并发判定仍是代码级推理，未在真实双显示器/4K 环境实测。

## 五、待确认

- 审计①的冲突日志是否要升级为计数/一次性提示，避免正常启动噪声？
- _contextLock 是否也需要覆盖 CreateTexture2D/CreateShaderResourceView 等 device 调用（当前判断 device 方法线程安全，仅 immediate context 需要串行）？
- 4K 下 UI 线程等待数毫秒是否可以接受；若主人实测卡顿，是否需要把像素拷贝移出锁或改为分块/异步？

## 六、主人实测步骤

1. 启动 IshWY 后立刻连按 F1 10 次（并发首调），确认每次都能正常进入取景、不出现永久 busy；检查 data/logs/app.log 中“并发首调 … 复用已注册的常驻池”的出现次数。
2. 按 F1 选一个普通窗口（窗口模式），确认取景层显示该窗口内容，保存后文件不是黑图。
3. 选一个被其他窗口遮挡的窗口，按 F1 用窗口模式捕获，确认抓到的是被遮挡窗口本体；查看 data/logs/app.log，确认没有 WGC 失败或 CreateForWindow 失败。
4. 在取景窗显示期间按全屏截图热键，制造窗口捕获与显示器池并发；重复 10 次，确认应用不崩溃、不出现永久 busy、保存图正常。
5. 在双显示器/4K 缩放环境下重复步骤 1-4，观察取景是否明显卡顿；检查日志中“并发首调”是否持续刷屏，并记录发生时机与显示器配置。

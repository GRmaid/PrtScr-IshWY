# P14b′ UI与服务R1最终兼容复核（task84）

## 判定

14:51独立只读回件：限定五输入静态PASS，0必修；首末五hash一致，lead对照131源码Freeze与写者最终回件全部匹配。只完成本次审查，不代表构建或实机验收。

| 路径 | SHA256 |
|---|---|
| src/PrtScr.App/MainWindow.Capture.cs | 398b617dc26afd1de9631232e8d86ec3ad46be8007a2a5e353fcea15552d2f76 |
| src/PrtScr.App/MainWindow.Hotkeys.cs | 0dcd5b2aeb04336c7bc051af9e64b41ee797e40ab4cfca05c4998a853a1d116f |
| src/PrtScr.App/MainWindow.Windows.cs | b88cbde59462cd834c21c540f4bb3be66a729c93f235adb6f0ed02629049a045 |
| src/PrtScr.App/Services/CaptureService.cs | 5151eaa08bf3b9644958c9afb96415415696b03f9d1ea752bdba0eb9faf54a1c |
| src/PrtScr.App/Services/CaptureService.Quick.cs | 04421cc9e5744892c33924388609362db1c078d568bc1793cc850f1b24995ab8 |

## 核验

UI调用StartQuickCaptureAsync及8字段返回自洽；OnFullScreenPressed仅一次await，首采集前无新增hop。Ignored/Canceled仅log；Copied与Failure控制原提示，关闭历史不误判。四处旧IsCapturing均为IsOverlayCapturing，保留Pin/OCR原忙语义；菜单/兜底/游戏/QuickProfile/延迟路由保留，真正Overlay意图才抢占。

Exit_Click先同步BeginShutdown及delayCancel，仅一次ShutdownAsync，finally保留原退出。服务300ms等待调用/单飞、取消回调异常隔离、reservation本代finally、真实TryTransition与Normal/ScreenQuick接口自洽。

## 观察与补核

非必修S1首个Selecting拒绝无End依赖Core初态；task83实际核Core Begin与迁移，未发现当前可达必修。S2 Snapshot/End理论抛异常可能跳过等待，但SafeLog封装的Core日志不会抛，Win32/Hotkey旧边界仍不保证硬时限；S3成功End-null fallback理论取消误提示，当前latch排他下未证可达；S4共享monitor当前两surface不同时活动。

报告Core Release签名一处写成(Guid,Guid)系未读猜测，实际两参签名(Guid,long)已由task83实读核对，不引用错误占位作证。Core初态、300ms值、Bypass及IO实现仍结合task74/73/81/83和完整构建，不从五输入外推。

## 读取范围与纪律

工位披露一次MainWindow.*.cs grep额外打印另两个partial的9行，未打开/未据其结论；记录为未事先批准的范围外输出，不反向授权。主要判定仅依实际五输入。未编辑、build/test/format/App/smoke/UIA/提交。原始回件当前会话m04034。

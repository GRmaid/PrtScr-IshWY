# P14b′ 六类保护日志核对（lead，2026-11-08）

## 证据范围

在131源码Freeze之后，以git diff --ignore-space-at-eol对照HEAD中的历史服务保护模板，再读取当前Core与Overlay实际行；HEAD不是实施前dirty基线。最终dirty基线增量8项中不含Overlay文件，可证明hidden Dismiss本轮未改。此核对只判静态模板与收尾顺序，不证明运行时日志已触发。

## 六类与落点

| 类别 | 当前路径与行 | 模板/判定 |
|---|---|---|
| invalidated | src/PrtScr.Core/CaptureSessionCoordinator.cs:677 | Capture: session {SessionText(sessionId)} invalidated，保留N格式身份语义 |
| not-capturing | src/PrtScr.Core/CaptureSessionCoordinator.cs:841 | Capture: session {text} finish ignored (not capturing, reason={reason}) |
| stale | src/PrtScr.Core/CaptureSessionCoordinator.cs:848 | Capture: session {text} finish ignored (stale session, current={SessionText(_ownerSessionId)}, reason={reason})，owner替代旧last身份 |
| finished | src/PrtScr.Core/CaptureSessionCoordinator.cs:880 | Capture: session {text} finished reason={reason} |
| already-capturing | src/PrtScr.App/Services/CaptureService.cs:227 | Capture: already capturing, cancel session={activeText}，模板未改 |
| hidden Dismiss | src/PrtScr.App/Views/CaptureOverlayWindow.Capture.cs:258 | Overlay: dismiss while hidden session={SessionText(session)} reason={reason}，本轮文件hash不变 |

Core EndLocked:880先添加finished，:881–882再state，:883–884再result；Flush:509–513按列表次序调用委托。R1服务构造委托SafeLog隔离App.Log异常，正常日志文本不变；若底层日志本身抛错，可能丢行，不承诺持久化零损失。

其他新增日志和真实引擎元数据补充不纳入六保护模板一致性。不得再以“原五条”计数宣称覆盖hidden Dismiss。

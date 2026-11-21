# P14b′ 附属写入与Overlay兼容补核（task69）

- 工位：maid-check / 325501ad-1753-4bd6-8ce8-d2842e380102。13:44交付并结束。
- 只读原指定5文件；未build/test/format/App运行。本报告归档静态发现与lead复核边界。

## 写入返回值与不可逆边界

- ClipboardService.cs:15 SetImageAsync(byte[])返回Task<bool>。SetContent:30在Flush:31之前；false不保证未写入，不能承诺撤销。重试6次、间隔80ms保持不变。
- CaptureHistoryService.cs:62–67 AddAsync返回Task<string?>；null可能为关闭历史:69–70、空输入:72–73、捕获失败:114–118或写入后HistoryChanged异常:111。PNG写:89，元数据异常:96–99内部吞掉，修剪:104–108。不能由null推断精确失败点或回滚。
- Quick在历史启用且已尝试写入的前提下，null/异常才记通用HistoryFailed；历史未启用不是失败。保留首次主Failure，两项都失败时Detail保留两者，不修改历史/剪贴板服务契约。

## Overlay现有时序必须保留

- CaptureOverlayWindow.Capture.cs:566–696 Confirm在MarkSucceeded:625后Finish:630，再Encode:637、current:640、copy/history:646–654和current:662。不可把Quick新latch悄悄套到Overlay，把其提前End改成延后。
- Save:725–833已在write:767之后才current:771；OCR:475–529先Dismiss:516再显示结果UI:517。这些为本轮兼容边界，不声称已解决所有旧晚到写入语义。
- 第六保护文本在:258：`Overlay: dismiss while hidden session={SessionText(session)} reason={reason}`，随后Finish:259。
- Finish:199–248使用IsOwned:204，bool:210防本对象重复，然后End:245。task69曾据未读服务猜测取消后IsOwned=false；lead已另读服务确认Owner在Invalidate后仍保留，撤回该推论。必须保留Current与Owner区别，不能因此永久忙。

## 热键与日志核验

EscOverPinHook.cs:135–146采用300ms CAS去抖（忽略不排队），F2 hook:511–531当前无Overlay占用闸门；服务统一槽负责最终拒绝。MainWindow.Hotkeys.cs:108–119已有UI派发，Fallback先TryClaim再NotifyFallbackHit。

五个服务保护模板为invalidated、finish ignored/not capturing、finish ignored/stale、finished、already capturing/cancel；另加上述hidden Dismiss共六。finished→state→result终态顺序保持。多次忽略日志不等于一次业务收尾，不能强加全部模板只打印一次的判据。

## 未验证与下一步

未运行真实写入、热键、焦点或退出。Core工作完成后由服务工位复用唯一协调器，Quick在不可逆写入前一次latch；保留Normal与HistoryCaptureMeta.ScreenQuick，MainWindow只处理既有提示。

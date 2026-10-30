# P-07b 报告指针

task-12 描述里的报告名是 `P-07b-windowcapture-wgc-bridge.md`，lead 消息指定 `P-07b-wgc-detailed.md`。
正文以 lead 指定为准：[Record/reports/P-07b-wgc-detailed.md](P-07b-wgc-detailed.md)。

内容：WindowCapture 的 WGC 回退改走 `WgcWindowCapture.CaptureWindowDetailedAsync`，
新增 `WgcFailure/WgcHResult/WgcDetail` 三个结果字段，WGC `Canceled` 归一为 `WindowCaptureFailure.Canceled`，
粗粒度 `Failure` 语义保持不变（供 P-04 继续消费）。

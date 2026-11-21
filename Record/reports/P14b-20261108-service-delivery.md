# P14b′ 服务首交收件（task76，未验收）

## 范围与身份

14:26工位7bd5c666-fa5e-45ce-bb3b-13a9b60da90e报告完成，仅改CaptureService.cs为partial并新增CaptureService.Quick.cs；未改Core/Bypass/Overlay/MainWindow/Record/依赖，未构建测试格式或App。交付hash：

- src/PrtScr.App/Services/CaptureService.cs：8DCC31CBADC593B8C6047C779D0FDB8EA3A770866ED50CF706EBCA6CFC681EA0（旧69201B83821B4CC0AF67A1F6D35BE3668519695CEDDC09E08F79607AF7BEE81B），31792B，约780正文行。
- src/PrtScr.App/Services/CaptureService.Quick.cs：0552C7BA518A32182FF02B71DA8FF2DD8479C8A778036D1A45775FC4E4652A09，14372B，378行，全CRLF无BOM（工位自报，最终再核）。

## 已读取的公共签名

- IsOverlayCapturing / IsCaptureBusy；旧IsCapturing兼容指向前者；BeginShutdown() / Task ShutdownAsync()；旧Overlay方法签名保留，StartCaptureAsync()仅真实Overlay意图。
- 新QuickCaptureDisposition为Ignored/Canceled/Success/Failed；Success表示采集Completed，不保证附属步骤成功。
- public sealed record QuickCaptureOutcome(QuickCaptureDisposition Disposition,int PngBytes,CaptureService.QuickCaptureStatus Status,bool Copied,bool HistorySaved,string? HistoryPath,bool BypassActivated,CaptureResult CaptureResult)。
- public Task<QuickCaptureOutcome> StartQuickCaptureAsync()，旧static CaptureFullScreenPngAsync()与QuickCaptureResult仍留给旧UI直调，尚未切换。

## 工位自述与未核销风险

单Coordinator承载业务状态，资源CTS/Task不作busy；Overlay原子抢占/预留后在首await前Begin，Preparing可取消，localGuid收尾；Quick首抓/不可用才旁路/150ms重试，真实engine元数据，latched后并行copy/history分别catch并WhenAll，History禁用不视失败，Bypass正常Dispose在End前。Shutdown只一个300ms额外等待，不join native。上述均待独立源码核验，不是验收结论。

工位严格未扩读5输入，但据合同/旧码猜Core Result/枚举/转移和附属服务类型；明确请求下一步补读定义核验。lead已读CaptureResult.cs与CaptureSessionState.cs部分，Result七参、Wgc/Dxgi/BitBlt/PrintWindow/Fallback存在；全调用/转移表及服务辅助调用仍交独立审查。不得仅用花括号相等宣称编译通过。

## 下一步

先派独立静态审查，绑定两服务+Core定义/Coordinator共五输入；优先查所有调用存在性/状态转移/异常/退出/Overlay兼容。lead已读取公共Outcome/四Disposition及Overlay/Shutdown API，按此冻结UI编译合同（不等于服务内部验收），task80仅写UI三文件与task79内部独立核验并行；后续服务返工保持公共签名，必要变更须先协调UI。未经内部核销及整体冻结绝不开始门禁。P11暂停，不运行App或覆盖默认产物，不提交发布。

# P14b′ UI三文件首交（task80，未验收）

## 收件与身份

14:31工位8bbac7f6-64ff-4a01-9f08-ccddbeafd826报告完成，仅三UI文件，全CRLF无BOM；未构建测试格式/App/依赖/其它写域。

| 文件 | before SHA256 | after SHA256 |
|---|---|---|
| src/PrtScr.App/MainWindow.Capture.cs | 3a82d7b833fc136b592ea7e7dcb88e575c94a8de993211bb8da9b26ade1d90d6 | 398b617dc26afd1de9631232e8d86ec3ad46be8007a2a5e353fcea15552d2f76 |
| src/PrtScr.App/MainWindow.Hotkeys.cs | 1ac9e27c1ef0e7cd4842d3e02f8f0a998e3696e428d79caa0d7e7f588e97f3bd | 0dcd5b2aeb04336c7bc051af9e64b41ee797e40ab4cfca05c4998a853a1d116f |
| src/PrtScr.App/MainWindow.Windows.cs | 2641d3b64bef678afe901dd87b6a8f9c36657bc5f0a0e60d415cb21f44ccda20 | b88cbde59462cd834c21c540f4bb3be66a729c93f235adb6f0ed02629049a045 |

## 工位自述变更（待独立核验）

Capture:23–93单次await StartQuickCaptureAsync，删直接抓取/旁路/剪贴板/历史/150ms和独立busy；Ignored/Canceled静默，Failed保留既有LikelyBlack/采集失败文案，Success按Copied状态，Copied=true且Failure=None才成功toast，HistoryFailed只日志且抑制成功toast，不误把HistorySaved=false视失败。Capture:120–125/:180–185与Quick入口退出守卫；:140用IsOverlayCapturing。Hotkeys:44 Esc及Windows:388 Pin/:419 OCR均仅Overlay语义，Hotkeys:171–172以_exitStarted取代_quickCaptureBusy。

Windows:449–475真实Exit_Click一次Interlocked守卫→同步BeginShutdown/取消delay→唯一await ShutdownAsync→finally原TrayIcon/Hotkey Dispose及ExitProcess。保原hook/fallback claim→Notify→Dispatcher、300ms去抖、游戏/Quick配置、延时取消及关闭隐藏，不动App出口。

## 缺口

- Core CaptureFailure命名空间和Detail由工位按Quick与任务推断、未扩读；lead已读Core实际定义确认PrtScr.Core与Detail，仍由服务/Core独立审查完整交叉核验。
- delay CTS若已Dispose抛ODE会跳过本次ShutdownAsync，但同步封口已调用且finally仍退出；这不是额外等待超300ms，实际场景未验。
- UI独立复核尚未完成；服务仍有已知类型缺陷，整体不能构建验收，不运行App/默认产物、不提交发布/P11。

# IshWY 架构说明

本文只描述当前代码结构，不记录版本历史。

## 1. 项目边界

| 项目 | 职责 | 允许依赖 |
| --- | --- | --- |
| `PrtScr.App` | WinUI 3 应用、设置窗、取景器、贴图、历史、提示、托盘 | Core、Capture、Ocr、Interop |
| `PrtScr.Core` | 纯规则层：配置读写、贴图来源、热键文本、颜色解析、延时档位、历史项模型 | 无 |
| `PrtScr.Core.Tests` | Core 的 xUnit 单元测试 | Core |
| `PrtScr.Capture` | WGC、DXGI Desktop Duplication、BitBlt、光标绘制 | Interop |
| `PrtScr.Ocr` | Windows OCR 与 Umi-OCR 客户端 | 无 |
| `PrtScr.Interop` | CsWin32 生成的 Win32 绑定与少量共享原生声明 | 无 |

`tools/WgcProbe`、`tools/DxgiProbe`、`tools/KbProbe` 是排查用探针，不属于发布产物。

## 2. 一次截图的主路径

1. `HotkeyService` 或 `EscOverPinHook` 收到全局热键。
2. `MainWindow.OnHotkeyPressed` 读取 `captureProfile` 与 `captureDelaySeconds`；快捷档直接走 `OnFullScreenPressed`，常规档延迟结束后启动 `CaptureOverlayWindow`。
3. `CaptureService` 按顺序尝试 WGC 显示器常驻池 → DXGI → BitBlt CAPTUREBLT → 普通 BitBlt → 前景窗口 WGC；每层用 `IsNearlyBlack` 判断是否继续回退。
4. 取景器冻结画面后，用户在 `Window` 或 `Normal` 模式中选窗/框选；`CaptureSelectionAsync` 先按模式分流，再裁切或重建窗口内容。
5. 结果写剪贴板、截图历史，并按设置显示提示。

全屏游戏路径不同：`FullscreenBypass.IsForegroundFullscreenGame` 检测到全屏游戏且开启 `gameFullscreenQuickCapture` 时，常规档直接整屏快照，不弹取景器。

## 3. 关键模块

- `src/PrtScr.App/MainWindow.xaml.cs` + `MainWindow.*.cs`：主文件只留字段、构造、OnLoaded、导航、内存计时与状态；PinSettings / CaptureSettings / ToastSettings / Windows / Ocr / Hotkeys / Capture / Appearance 八个 partial 分别承载设置页事件、截图协调与入口。
- `src/PrtScr.App/Views/CaptureOverlayWindow.xaml.cs` + `CaptureOverlayWindow.Selection/Mode/Capture/Loupe/Hud.cs`：主文件留字段、构造与冻结帧；Selection 负责指针、移动缩放、悬停与滚轮切窗，Mode 负责取景模式与窗口包含，Capture 负责键盘、确认、裁剪与保存，Loupe 负责取色器，Hud 负责提示、可视状态与几何辅助。
- `src/PrtScr.App/Views/NativePinWindow.cs` + `NativePinWindow.Window/Interaction/Commands/Render.cs` 与 `NativePinWindow.Edit.cs` + `NativePinWindow.EditInput/EditApply/EditRender/EditPaint.cs`：Window 负责创建、消息循环与任务栏，Interaction 负责滚轮、吸附、菜单、裁剪与另存，Commands 负责命令、穿透与导航，Render 负责分层窗口与 GDI；EditInput 负责指针绘制，EditApply 负责应用与文字编辑，EditRender 负责合成/形状/工具栏，EditPaint 负责图标、调色盘与马赛克。
- `src/PrtScr.App/Views/HistoryWindow.xaml.cs` + `HistoryWindow.Selection.cs` + `HistoryWindow.Actions.cs`：主文件留分类、缩略图、摘要与刷新；Selection 负责选择/右键/拖出/键盘，Actions 负责复制/贴图/标注/另存为/删除/清空/打开文件夹。
- `src/PrtScr.Core/`：不依赖 WinUI 的纯规则与模型；`ConfigService` 的基准目录与日志可注入，`PinBehaviorRules` / `HotkeyText` / `ColorRules` / `CaptureDelayRules` / `HistoryItem` 可脱离窗口单测。
- `src/PrtScr.App/Services/`：热键注册、采集历史、贴图服务、Esc 仲裁、文件保存、提示背景、启动项等依赖 WinRT/Windows 的服务；可复用规则委托给 Core，不在 App 里复制第二份。
- `src/PrtScr.App/Views/`：WinUI 窗口与卡片视图；窗口只做展示和输入编排，可复用规则优先下沉到 `Services`。

## 4. 运行数据

所有运行数据都在可执行文件旁的 `data/` 下：

- `data/config.json`：用户设置。
- `data/history/`：PNG 与同名 JSON 元数据，按分类保留。
- `data/logs/app.log`：运行日志；热键钩子不直接写盘，日志走队列与专用写线程。

## 5. 不能随手改的约束

- WGC 显示器常驻池约 50 MB 是刻意保留，用于按下热键立即出冻结帧；不要为了省内存直接删池。
- 低层键盘钩子回调里不能做阻塞 I/O；同步写日志会让 Windows 静默卸载钩子。
- 不要手写 COM vtable 槽位；新增 Win32 类型走 CsWin32 的 `NativeMethods.txt`，必要时先用 `tools/` 探针验证参数顺序。
- 贴图是分层窗口，尺寸变化要用 `UpdateLayeredWindow` 的 `psize`，不能只 `MoveWindow`。
- 取景器与贴图窗口的键盘输入不能依赖前台激活；使用 `WH_KEYBOARD_LL` + `DispatcherQueue`。
- 历史删除/清空必须先快照目标并确认，确认后不可恢复。

## 6. 验证方式

`PrtScr.Core.Tests` 覆盖纯规则与配置迁移；窗口、采集引擎与交互仍靠构建、冒烟和主人实机验收。顺序是：

1. `dotnet build PrtScr.sln -c Debug --nologo`：0 错误；警告应为 0。
2. `dotnet test PrtScr.sln -c Debug --no-build --nologo`：全部通过（当前 77 个用例）。
3. `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore`：通过。
4. `IshWY.exe --smoke`：退出码 0，日志里五个热键 `register=0x1`，overlay pre-created。
5. 交互与性能由主人在真实桌面验证；不要用合成按键或后台 UIA 代替。

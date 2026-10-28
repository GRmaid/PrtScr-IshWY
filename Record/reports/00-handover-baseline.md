# 00 接手基线 · IshWY

- 时间：2026-10-28 21:40（Asia/Shanghai）
- 仓库：`E:\Explore\GoodGood\PrtScr`
- HEAD：`0b1ca41`（`git describe` = `v1.3.0-2-g0b1ca41`，37 个提交）；工作区跟踪文件仅 `docs/HANDOFF.md` 显示修改（2026-10-28 追加历史标题与指针），未跟踪目录 `.tmp/`、`大佬修改材料/` 不入库。
- 跟踪文件：135 个。`src/PrtScr.App` 78、`src/PrtScr.Core.Tests` 8、`src/PrtScr.Core` 7、`src/PrtScr.Capture` 7、`src/PrtScr.Ocr` 6、`src/PrtScr.Interop` 5、`tools/*` 6、`.github/workflows` 2、`docs` 5、根文件 7、`scripts` 1。

## 构建与测试

| 命令 | 结果 | 判读 |
|---|---|---|
| ~~`dotnet build PrtScr.sln -c Debug --nologo -t:Compile`~~ | 退出 0，0 错误 0 警告 | ⚠️ **假绿，已废弃**：`-t:Compile` 不跑 WinUI XAML 代码生成（MarkupCompilePass1/XamlPreCompile/MarkupCompilePass2 挂在 PrepareResourcesDependsOn，不在 Compile 链上），热态假绿、冷态 624 条 CS0103。见文末「更正 1」 |
| `dotnet build PrtScr.sln -c Debug --nologo -p:OutDir=E:/Explore/GoodGood/PrtScr/.tmp/out-verify/` | 退出 0，**0 错误 0 警告**，12.86s | 真编译门禁（含 XAML）；用 OutDir 绕开被运行实例占锁的默认 bin |
| `dotnet build PrtScr.sln -c Debug --nologo`（整包） | **8 错误**（MSB3021/MSB3027），40 条重试警告 | 全部为复制输出文件被占用：`实践小梅IshWY (29836)` 锁住 `src/PrtScr.App/bin/x64/Debug/.../PrtScr.Capture.dll`、`PrtScr.Ocr.dll`。属环境占用，不是代码缺陷 |
| `dotnet test PrtScr.sln -c Debug --no-build --nologo` | 退出 0，**77/77 通过**，69 ms | 与送审材料 77 用例一致（19 Fact + 58 InlineData，30 个测试方法） |
| `dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore` | 退出 0 | 格式门禁通过 |

## 阻塞项

- `IshWY.exe` PID 29836（启动于 11:11:58）正在运行，锁住 `bin/x64/Debug` 输出。整包 build 与 `--smoke` 都需要先退出该实例。
- 该实例是主人本人在用的窗口还是遗留进程，需主人确认；**女仆长不主动 kill 主人的程序**。

## 结论

基线为绿（编译 0 错 0 警、测试 77/77、格式 0），唯一红项是运行实例造成的文件占用（22:05 主人已退出 IshWY，该项解除）。复跑整包 build 与 smoke 之前，需要主人关掉正在运行的 IshWY。

**更正 1（2026-10-28 23:2x）**：上表原第一行「`-t:Compile` 0 错 0 警」不构成编译证据。`-t:Compile` 只跑 C# 编译，不跑 WinUI XAML 代码生成，故而在 obj 已热时 0/0 通过、在冷态（清 obj 或改 XAML 后）报 CS0103（App 单工程 624 条、整 sln 1248 条，集中在未改动的 XAML 窗口如 `CaptureOverlayWindow.Hud.cs`、`MainWindow.Hotkeys.cs`、`PinCropWindow.cs`）。P-01(maid-impl) 与 P-07(maid-capture) 两名女仆各自独立撞到同一现象，证据日志 `.tmp/sln-compile.log`。**编译门禁一律改用整包 `Build` + `-p:OutDir=.tmp/out-<单号>/`**（见 `Record/plans/2026-10-28-整改总纲.md` §8 第 1 条）。

## 复跑的完整门禁（主人关实例后）

```
dotnet build PrtScr.sln -c Debug --nologo
dotnet test PrtScr.sln -c Debug --no-build --nologo
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
IshWY.exe --smoke            # 现有 smoke 有假成功路径（E01），暂不能单独作为放行证据
```

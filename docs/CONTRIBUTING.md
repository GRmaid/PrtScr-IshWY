# 参与开发

## 环境

- Windows 10 19041 或更高版本。
- .NET SDK 10.0.100 或更高的 10.0 特性带。
- 不需要 Visual Studio；命令行 `dotnet` 即可构建。

## 构建与运行

```powershell
dotnet build PrtScr.sln -c Debug --nologo
```

Debug 可执行文件：

```
src/PrtScr.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/IshWY.exe
```

运行前先退出已有 IshWY 进程，否则 DLL 会被占用。构建后建议执行：

```powershell
./IshWY.exe --smoke
```

退出码应为 0；运行日志在可执行文件旁的 `data/logs/app.log`。

## 格式与检查

```powershell
dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore
```

CI 会执行同一条检查。提交前保持工作区没有格式差异。

## 代码约定

- 统一使用 4 空格缩进，文件末尾保留一个换行。
- 新增公共构建属性放 `Directory.Build.props`；包版本放 `Directory.Packages.props`，不要在单个 csproj 里再写 `Version`。
- 可复用规则优先放 `Services/`；窗口 code-behind 只保留展示与输入编排。
- 新增 Win32 声明走 CsWin32；不要手写 COM vtable。
- 不在 UI 线程做同步 I/O；日志、解码、编码尽量放后台线程。
- 改动采集、贴图、取景器输入、剪贴板时序时，必须单独说明风险并请主人实机验收。

## 提交前清单

- `dotnet build` 0 错误、0 警告。
- `dotnet format whitespace` 通过。
- `IshWY.exe --smoke` 退出 0。
- 涉及 UI/交互的改动，写明未自动验证的部分，交给主人测试。
- README 只写当前版本功能，不写版本历史或开发过程；不要覆盖主人自己改的内容。

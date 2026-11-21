# P14b′门禁工具显式R3最小变更

许可：Record/reports/P14b-20261108-extra-round-authorization.md。

## 根因实验

Record/tools/P14b-XamlArgv-Probe.ps1用同一build-guard→cmd.exe→dotnet msbuild链，以-getProperty在前保证只评估属性，不运行Build/Restore。legacy尾反斜杠加引号导致XamlGeneratedOutputPath吞掉-p:ProbeTail=tail-marker、ProbeTail为空；fixed将XAML路径统一为正斜杠且保留末尾/，路径完整、ProbeTail独立正确。两组exit0，日志见新轮证据目录legacy/fixed-xaml-properties.log。

## 最小修复

1. Build.ps1只将XAML命令行路径正斜杠化，维持末尾分隔符；-warnaserror/-bl及整包Build不变。
2. 原四脚本均指向明确的新轮p14b-app-20261108-1800-r3证据子目录与新artroot；SourceCheck仍用原128 dirty baseline，不变集合/哈希/拒覆写/精确guard标记/TRX无跳过/UTC/六项目各自bin/obj等判据。
3. 新Before默认1931文件已拍；原13:30失败快照/收据未改。新冻结将在Core R2等价修复完成并核对后进行；比较旧freeze要求仅Tests.cs的两断言差异。

## 未执行

尚未实际Build/test/format/ArtifactCheck；参数属性实验不代表完整门禁或运行验收。P11暂停与App/默认覆盖/提交发布hold保持。

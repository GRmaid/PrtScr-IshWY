# P14b′ 隔离工具R2末轮独立静态复核 PASS

## 收件与身份

14:24收到task77（工位6283390d-2753-48f5-b018-01afafb1a412）回件。结论：PASS、无必修项；仅静态，未执行门禁。lead随后Get-FileHash复核以下四hash全部一致。输入为R2裁定及四工具，未扩读JSON/csproj/sln/build-guard，未写或执行任何脚本/App。

| 文件 | SHA256 |
|---|---|
| Record/tools/P14b-App-SourceCheck.ps1 | 2aa85b57676ea2e8ccfb3e5390e92ecc21f4c1b10af2863e1bb9272678055af2 |
| Record/tools/P14b-App-Build.ps1 | ce518dd55d68646a1071b932a5ba7839c755a8319014dfb9bfbb2c5f6ab887e4 |
| Record/tools/P14b-App-TestFormat.ps1 | 09abee60063d4fbaf7e94c2211e6522025bd334aff00e9d539510364e14a43db |
| Record/tools/P14b-App-ArtifactCheck.ps1 | 8b93219951b2f810da2385219d46b1daa21a63369c9b2128ba7d75ebc1176690 |

## 必修闭环

- ArtifactCheck.ps1:17–32使用System.Text.Json原始token，GetProperty拒缺失，ValueKind拒非String；空白/TryParseExact O/InvariantCulture/RoundtripKind/KindUtc均检查，再比较freeze≤start≤complete。finally释放JsonDocument。
- TestFormat.ps1:21–51规范绝对路径并TrimEnd单字符分隔符，root+separator判子树；六项目五类路径逐项归ownBin/ownObj，按属性键跨项目唯一、UseArtifactsOutput=true；仅实际UseWinUI项目强制XAML等于Intermediate并属于自身obj。环境变量路径预检无global Artifacts/XAML override。
- SourceCheck.ps1:22–52源码全集合added/changed/removed及hash/bytes比较；Check不写，Verify保留失败证据并绑定frozenManifestHash。身份只覆盖src三后缀，不包含Assets/root props/targets，不是P11修补。
- Build.ps1:11–26拒已有root/收据，绑定freeze前后hash、精确EXIT=0 marker、log/binlog hash；TestFormat.ps1:62–66和ArtifactCheck.ps1:57–61断言TRX total>343、passed=executed=total、failed=notExecuted=0。ArtifactCheck三精确marker、formatPropertiesHash及产物/log/TRX绑定齐备。

## 非阻断关注与运行边界

1. 项目单独评估只有Configuration=Debug，而solution可能传Platform/RID；如XAML规范等值不符会失败关闭，首次执行重点检查，不得绕过。
2. frozen清单先hash后read有微小TOCTOU；流程禁止冻结后修改。SourceCheck对后缀/目录段大小写断言可能延后拒绝或扩大范围，不是假通过。
3. 六项目、debug_win-x64、IshWY文件名及guard整数elapsed格式硬编码，必须以实际执行证据确认；src外tools项目是否在sln不属本次工位五输入覆盖。
4. test/format日志没有独立携freeze的收据，但单轮拒覆写、精确marker及最终hash闭包绑定；不解释为任意历史日志有效。
5. 新增辅助函数fixture19项已由lead另行运行并通过，见Record/reports/P14b-20261108-runner-helper-selfcheck.md；这仅补足UTC/TrimEnd等纯行为，不代替MSBuild属性/env继承、构建、TRX、产物、原After。

## 允许进入的下一步

工具R2完成静态验收，不再追加R3。服务/UI仍需交付、独立复核与源码冻结；之后才按Freeze→Build→TestFormat→ArtifactCheck→Verify→原1931文件Before的After执行。失败保现场，不删证据或重建原基线；P11暂停，不运行App/smoke、不覆盖默认产物或提交发布。

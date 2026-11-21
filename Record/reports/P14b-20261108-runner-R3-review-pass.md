# P14b′门禁工具额外R3独立静态复核 PASS

2026-11-08 18:09 收件：task85 / f6c14750-46b9-46bc-abd8-316b52604809（DeepSeek Flash，五文件只读、零写入，无额外扩读）。PASS，0必修；五文件首末SHA256不变，ParseFile 5/5零语法错误；lead 18:10当前五hash逐项核对一致。不是完整门禁通过。

## 复核结论
- Build:17–18正斜杠且末尾/的XAML参数正确，后续-warnaserror/-v:minimal/-bl不再受尾反斜杠引号转义影响。审查为静态；lead另有同guard→cmd→dotnet属性实验legacy吞tail、fixed正确的实际证据。
- 四工具使用同一新证据子目录p14b-app-20261108-1800-r3；三个产物工具同一fresh artroot；SourceCheck仍以原128 dirty baseline为起点，原失败轮次不覆盖。
- 整包Build/warnaserror、精确guard marker、冻结源集合、收据/log/binlog/UTC顺序、六项目独立bin/obj及WinUI XAML等值、同根no-build/no-restore测试、TRX全过且零skip、format验证、产物/PRI/XBF/assets哈希判据保持。
- TestFormat环境变量中的反斜杠不是argv值，无需扩大修复。

## 非阻断边界
未执行Build/test/format/ArtifactCheck；未自行复现实验；未读baseline/旧失败目录/csproj/targets。intermediatexaml布局、IshWY产物名、debug_win-x64轴尚待实际门禁验证，脚本不满足即显式失败。lead六项目属性-only额外检查已通过，但不代替实际构建。Source身份仍不覆盖Assets/根props/targets，不是P11 B9/E08的修复。

## 五文件SHA256
| 文件 | SHA256 |
|---|---|
| Record/tools/P14b-App-Build.ps1 | C2C185849DE3C658F3E7D62BFB813A7AE80FE8B388EDB3521BA2160D9596436D |
| Record/tools/P14b-App-SourceCheck.ps1 | D5CFBF8C8B3CEE8D8A4DE8D1311730BF949A987B21F113021D776330126E6F3D |
| Record/tools/P14b-App-TestFormat.ps1 | 6E8DB6B0C157EC14D13EE24A888EAA9CB50DD05E6C5818F10462C76B72B50487 |
| Record/tools/P14b-App-ArtifactCheck.ps1 | E416B7DCF3777D108BD453C262E895E73BD2A1EF3C7E28C0F6F5E8253B443388 |
| .tmp/build-guard.ps1 | 377F1329FBA6810FEF57569FEE89E8D26EB24A98FB6562D697B769958DD51B2E |

## 放行范围
仅放行既定新轮完整隔离验证，不App/smoke/UIA/默认覆盖/提交发布；P11继续暂停。

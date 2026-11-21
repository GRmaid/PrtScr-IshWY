# Record隔离工具第二轮修补裁定

14:11收到task75（f99ae1d7-1b25-4107-9db4-5ac43c6f388b）needs_revision：6PASS/2PARTIAL。两必修为ArtifactCheck时间字段未显式UTC解析拒缺失，TestFormat有效路径只证明在总root未证明各项目bin/obj独立。R2是第二轮，不追加第三轮。

## 必修与裁定
F1：冻结createdUtc、build startedUtc/completedUtc逐项非空、TryParseExact O + RoundtripKind + Kind UTC，拒缺字段或不可解析再比较顺序。
F2：OutputPath/OutDir/TargetPath属本项目bin前缀，IntermediateOutputPath/Extensions属本项目obj前缀，五类跨项目独立；WinUI XAML属本项目obj且规范化等于Intermediate。只有UseWinUI=true的项目执行XAML断言：非WinUI项目不使用该属性，继承全局env的App XAML路径无构建效果，强制它们拥有自己的XAML路径会制造不可能通过的断言，不以此降低有效WinUI隔离标准。
F3 before只校验128数量是dirty参考而非构建输入证明，原header/前置128身份另有记录，暂不扩展P11；F4建议Verify加freeze hash；F5尾分隔符规范化；F6最终artifact收录环境属性证明hash。后3建议可随局部修改完成。

## 保持边界
先完成R2并语法parse，再独立静态复核；未通过不得跑实际门禁。根props/assets不在源清单，绝不声称P11已修。新root/原Before/证据拒覆写/失败保现场不变。
## R2已保存，等待独立复核
- ArtifactCheck从System.Text.Json原始字符串token取三个时间字段，要求GetProperty存在/ValueKind String，TryParseExact O+RoundtripKind+KindUtc后排序。避开PS7 ConvertFrom-Json可能自动DateTime化造成字符串格式丢失，不依赖PS7.5专用DateKind开关。
- TestFormat规范化去尾分隔符（OrdinalIgnoreCase），按项目BaseName构造bin/obj前缀，五类路径分类型跨项目唯一；只有UseWinUI消费XAML才断言归属与Intermediate规范化相等。仍不传全局Artifacts/XAML属性，核验env-only实际路径。
- Source Verify收录frozenManifestHash；ArtifactCheck收录format-properties hash。源范围仍限src三后缀，不扩P11。
- 四脚本仅PowerShell Parser语法通过；没有Freeze/Build/Test/format/ArtifactCheck/After执行。独立R2未回报前不能声称工具通过或门禁绿。

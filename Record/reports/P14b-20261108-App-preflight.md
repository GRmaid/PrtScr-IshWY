# P14b′ App隔离门禁预检

2026-11-08 13:39，lead只执行项目属性评估，未build/restore/test/运行App。

- `pwsh -NoProfile -File Record/tools/P14b-Validation-Preflight.ps1 -ArtifactsRoot E:/Explore/GoodGood/PrtScr/.tmp/p14b-app-20261108-1330` exit0；全部六项目OutputPath/OutDir/IntermediateOutputPath/TargetPath/extensions都在fresh absent根内，extensions各项目独立，App XamlGeneratedOutputPath等于隔离IntermediateOutputPath。
- App输出bin/PrtScr.App/debug_win-x64、obj/PrtScr.App/debug_win-x64；其余项目各自bin/name/debug与obj/name/debug。这里只是属性证据，最终仍核产物与默认目录前后。
- 再取同名Before快照被保护器拒绝：`Refusing to overwrite snapshot: E:\Explore\GoodGood\PrtScr\Record\reports\P14b-isolation-evidence\p14b-app-20261108-1330-before.json`（exit1，未覆盖）。读取已有header：schema=P14b-default-output-snapshot-v1，repo=E:\Explore\GoodGood\PrtScr，label=p14b-app-20261108-1330，stage=Before，UTC 2026-11-08T05:30:48.5583348Z；沿用原Before，不换名刷掉基线。
- 首次读取JSON超过read的2000行上限被拒，后续2000行拼接JSON解析报Expected double-quoted property name，未宣称完整JSON验证。已改为读取30行核身份；实际After比较由既有验证脚本读取JSON。未以shell文本读取绕过工具。两次读取异常均在写归档前结束，无文件副作用。
- 源码写入前128个源码hash另存p14b-app-source-before.json。源码冻结前不执行Build；artifact源码哈希清单待补实际最终触及集合。
- 已新增Record/tools/P14b-App-SourceCheck.ps1，拟在工位停写后Freeze全部实际源码、对照128文件dirty基线并在门禁后Verify；artifact检查已从A1两文件扩大为冻结清单全部源码。脚本尚未执行，不能声称源冻结/默认目录比较已完成；该清单也不是P11全构建输入身份。
- P11保持暂缓；不停止或更新运行中的IshWY。

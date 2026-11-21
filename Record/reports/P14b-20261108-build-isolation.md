# P14b′ 构建隔离只读预检与lead选型

- task-61，maid-check，Flash 7c98c8a9-37e3-4754-8478-cb2710390bb7；2026-11-08 13:11收件。只读5文件及属性求值，无build/restore/test/format。
- 结论：OutDir-only分离DLL/XBF；全局BaseIntermediateOutputPath会让项目共用MSBuildProjectExtensionsPath。候选使用ArtifactsPath自动按项目分bin/obj，再显式固定WinUI XamlGeneratedOutputPath。候选本身不是构建隔离已证明。

## 原始证据与局限

- SDK 10.0.402 Microsoft.Common.CurrentVersion.targets:227默认OutDir=OutputPath，4919–4932托管复制到OutDir；WinUI2.3.9 interop.targets:905–919、950–960将XBF写OutputPath。
- 同WinUI targets:182将XamlGeneratedOutputPath默认到IntermediateOutputPath，213定位intermediatexaml。工位在重定向obj时得到空XamlGeneratedOutputPath，故不得假定ArtifactsPath单独足够。
- 工位求值App新路径bin/PrtScr.App/debug_win-x64与obj/PrtScr.App/debug_win-x64，Core为bin/PrtScr.Core/debug与obj/PrtScr.Core/debug；各扩展根obj/<项目>/。
- 其余未知：实际restore、PRI/XBF生成、全局XamlGeneratedOutputPath对其他项目的影响；Release路径仅推断；未读Directory.Build.props或PRI生成目标。工位提出副本默认构建和临时props备选，但未实施。

## lead补核及修订（不是照单全收）

1. lead已读Directory.Build.props及全部6个csproj，只有App启用UseWinUI；其余5个无WinUI编译器，因此全局XamlGeneratedOutputPath只用于App。
2. Record/tools/P14b-Validation-Preflight.ps1实际逐项目只读求值，断言OutputPath/OutDir/IntermediateOutputPath/MSBuildProjectExtensionsPath/TargetPath全部在新根，各扩展根不共享；App XamlGeneratedOutputPath非空且与隔离IntermediateOutputPath相同。首次实跑退出0，6/6满足。
3. 本轮新根 E:/Explore/GoodGood/PrtScr/.tmp/p14b-a1-20261108-1315。要求原先不存在，不删除旧目录；拒绝工位“先整体删除”作为通用步骤。
4. 不采用“必须先无IshWY进程”要求：当前用户实例不停止，只有实际隔离可验证才运行门禁，默认bin/obj用哈希/长度/mtime与路径集合守卫。守卫失败即停，不自动覆盖修复。
5. 保留build-guard串行；完整solution Build而非Compile。不运行App/smoke/publish/ZIP，不恢复P11。

## 下一步必须实证

- 实际完整Build的0警0错、Core.Tests、format三项；新根App DLL/EXE/PRI/XBF齐全，App intermediatexaml与6份项目assets各自存在。
- Record/reports/P14b-isolation-evidence/p14b-a1-20261108-1312-before.json已记录1931个默认bin/obj文件。门禁后比较；数据目录与.log排除，瞬态写入恢复及空嵌套目录不在证明范围。
- 本报告此刻仅证明属性预检通过，实际构建隔离结果另记验收报告，不把路径求值当产物验收。

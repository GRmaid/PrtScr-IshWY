# P14b′ Record门禁工具：首轮评审与R1裁定

## 状态
原task72（68075db8-fc2b-471a-993f-b4410f6f8330）13:57返回needs_revision，五文件静态审查、无执行。lead仅修Record工具；待独立R1复核。当前仅PS7解析器验证四脚本语法通过，尚未Freeze/Build/Test/format/ArtifactCheck/After运行。

## 必修与处理
1. ArtifactCheck不绑定完整Build/冻结先后，且仅单向遍历冻结文件→Build先执行只读SourceCheck Check、记录start/complete收据并绑定freeze SHA与log/binlog；ArtifactCheck核对顺序、收据、完整集合/哈希。
2. binlog/TRX可能复用→独立App test-results，Build/测试/format所有相关证据路径拒绝已存在；失败保留现场，不自动删除或重建基线。
3. format只继承环境变量缺有效属性证据→TestFormat对六项目做环境变量-only msbuild getProperty（不传Artifacts/XAML全局属性），校验实际Output/OutDir/Intermediate/Extensions/Target/XAML与项目独立根后才跑测试和format。
4. EXIT=0子串假阳性→按guard实际完整行正则匹配EXIT=0 elapsed=数字s tag=指定任务；TRX必须total>343、executed=passed=total、failed/notExecuted=0。精确新增测试数由最终Core评审再核。
5. 冻结清单路径应fail closed→只允许src下三后缀相对路径，拒绝绝对/冒号/反斜杠/dot segment/重复项/非SHA256。全集合Check覆盖新增/删除，Verify失败保留单次证据。
6. 产物记录→schema、日志/binlog/TRX哈希、全部XBF哈希及deps/runtimeconfig列入记录。
7. PS7显式要求、路径命令引号、源码差异键排序已补。

## 限制裁定（不恢复P11）
- 源清单仅覆盖src .cs/.csproj/.xaml，明确不含Assets/root props/targets/完整构建输入，不声称SourceIdentity/P11问题修复。
- 固定debug_win-x64/IshWY来自已读本仓库SDK/csproj六项目预检；不是通用脚本。默认目录保护仍依原Before/After脚本，不由属性或收据代替。
- 固定流程：独立源码审查通过→SourceCheck Freeze→Build→TestFormat→ArtifactCheck→SourceCheck Verify→原默认bin/obj After比较。遇失败保留证据、重新评审，不删除/relable。
- lead修脚本时JS替换字符串误解释PowerShell的美元号尾部，解析器发现两文件语法损坏；改为字面完整写入后四文件parse exit0。期间没有执行目标脚本或产品门禁，无Before/Freeze/产物副作用。

## 独立复核范围
只读本报告及四工具：SourceCheck/Build/TestFormat/ArtifactCheck。重点检查新增保护是否真成立、PowerShell数据类型/路径/原生命令exit语义，不能将语法parse当作实际门禁。不得运行脚本/造fixture/扩大到scripts或P11。

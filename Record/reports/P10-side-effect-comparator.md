# P10 保存观察比对工具验证

2026-11-07 21:42。只处理已保存 JSON 和纯虚构夹具，不启动 App，不写注册表，不读写真实 data。

## 范围与退出语义

- `Record/tools/P10-CompareSideEffects.ps1` 比较声明程序目录/data 文件相对路径、长度、SHA256、HKCU Run/IshWY 观察值及已记录进程身份。
- exit0：`equal-recorded-fields-only`，仅记录字段相同；exit1：`different`；exit2：`unknown`（观察不完整、读取错误、缺字段/哈希、重复或范围不匹配）。
- 不证明全系统无副作用；观察非原子，差异不自动归因于 App；进程快照不授予任何终止/激活权限。

## 离线测试

命令：`pwsh -NoProfile -File Record/tools/Test-P10-CompareSideEffects.ps1`。退出0，10/10通过。夹具位于 `Record/reports/P10-compare-fixtures/`，所有目录/进程/注册表内容均为虚构输入。

|夹具|期望exit|判定|
|---|---:|---|
|equal|0|equal-recorded-fields-only|
|content（hash变化）|1|different|
|deleted|1|different|
|error|2|unknown|
|missing（无注册表观察）|2|unknown|
|duplicate（重复文件路径）|2|unknown|
|registry|1|different|
|process|1|different|
|roots（声明根不同）|2|unknown|
|hash（hash缺失）|2|unknown|

## 既有记录的格式兼容

将 `Record/reports/P10-observation-resume-before.json` 同自身比较，退出0，`equal-recorded-fields-only`，unknowns为空。该操作仅证明比对器可读取既有快照schema，完全不是一次真实的前后副作用检查。

## 待运行

P10/P11 两侧静态安全独立复核通过后，实际受控运行前后重新采集观察，才可对本次记录字段作比较。用户正常实例可自行产生合法日志变化，应保留差异并单独判断；不得忽略未知项换取成功。

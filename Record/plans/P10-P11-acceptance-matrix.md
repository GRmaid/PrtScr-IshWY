# P-10 / P-11 独立验收矩阵

日期：2026-11-07。适用：本轮安全冒烟与同源产物证据链；不构成发版授权。

## 执行前置

- 产品/脚本工位交付后才开始最终独立复核。复核通过前，不执行 App 冒烟或本地实际打包。
- 所有失败必须保留证据，不能以 SkipSmoke、旧结果、已有实例或计时退出替代。
- 只终止测试启动且仍持有的进程对象；不按进程名终止用户实例，不合成键鼠或使用 UIA。

## P-10 检查矩阵

| ID | 条件 | 预期 | 最低证据方式 |
|---|---|---|---|
| S01 | --smoke 缺少 nonce/root | 非0，禁止回落真实 data 或继续正常启动 | 纯规则测试 + 静态初始化顺序 |
| S02 | nonce 非 Guid N、root 不匹配/非新空目录/带重解析点 | 失败关闭；不清理或覆盖用户目录 | 纯规则测试 + 路径/文件边界复核 |
| S03 | 已有正常实例 | 非0；不向既有激活事件 Set | 单实例分支复核；有实例时可作安全负例 |
| S04 | XAML 或 Loaded 初始化抛异常 | 非0；不得生成 success=true | 异常漏斗与状态机用例 |
| S05 | UI 未 Loaded 或未完成队列往返 | 超时仅失败，不因时间到达成功 | 生命周期/计时器静态检查 |
| S06 | 成功路径 | 本次进程三个检查齐全，exit0 与结果身份一致 | 安全放行后的本地隔离冒烟 |
| S07 | 日志/配置/历史/托盘/钩子/热键/启动项/预热/网络 | 文件只落隔离根；系统/网络能力跳过而非宣称已测 | 所有接线静态复核 + 被动前后快照 |
| S08 | success 与异常/退出并发 | 只允许一次终态；失败不能被成功覆盖 | 纯规则并发/状态转换测试或明确残余风险 |
| S09 | 正常启动非 smoke | 原配置/托盘/热键/启动项语义不被改变 | 正常分支差异审查；真实交互另验 |

## P-11 检查矩阵

| ID | 条件 | 预期 | 最低证据方式 |
|---|---|---|---|
| E01 | exit0，但无结果/旧 nonce/错 PID/错路径/错根/缺检查 | 全部拒绝；不能直接进入打包成功 | 离线结果校验负例 |
| E02 | smoke 超时 | 有界失败，只处置自己创建的子进程 | 启动器静态复核；不启动真实 App 的可控负例 |
| E03 | -SkipSmoke | 明确未验证；完整CI/Release gate非0、无GATE PASS（18:50严格裁决），不冒充发布验收 | 参数/manifest/工作流复核 |
| E04 | 版本号非法或含 shell 字符 | 进入外部命令前拒绝；合法版本透传项目 | 参数负例 + 发布命令复核 |
| E05 | 源码在门禁中途变化 | 身份不稳定即失败，不绑定混杂结果 | 指纹算法复核 + 离线变更用例 |
| E06 | 本地 dirty 构建 | HEAD + dirty + 确定性源码指纹，不冒称纯 HEAD | 指纹对受控修改敏感的负例 |
| E07 | 发布模式 dirty | 拒绝；工作流不能绕过同源门禁 | 发布入口复核 |
| E08 | 构建输入未全覆盖 | 不得声称同 SHA 证明；列出缺失输入并返工 | tracked/untracked 构建输入、props/targets/global.json 等复核 |
| E09 | 裁剪后输出与 ZIP | 每文件及 ZIP SHA256；manifest 明示门禁/版本/SDK/运行参数 | 哈希清单复核；实际包未运行则明确未验 |
| E10 | CI/Release 工作流 | 可信入口复用；输入不直接拼接 shell；无本轮实际远端执行 | YAML/命令审查 |

## 观察证据的局限

Record/tools/P10-SideEffectSnapshot.ps1 只读采样指定程序 data 文件哈希、HKCU Run/IshWY 与进程身份；只向指定的新证据文件写结果。
采样非原子，正常实例也可合法写日志。快照相同只能支持所选路径未观察到变化，不能单独证明所有系统副作用不存在。静态隔离审查仍是前置。

## 启动可达性附加清单（基线定位，最终行号以改后源码为准）

- App 静态配置、构造期日志和 InitializeComponent 早于 OnLaunched，非法 smoke 上下文不能等到 OnLaunched 才阻断。
- MainWindow 的字段构造（含 HotkeyService）早于构造体；只跳过 InitHotkey 调用不足以自动证明构造期无全局副作用，须核对字段类型构造路径。
- MainWindow 构造期依次涉及窗口钩子、托盘、背景、五组热键、历史单例；XAML 初始化期间触发的设置事件也必须落隔离配置。
- OnLoaded 基线包含 StartupService.IsEnabled、RefreshHistoryStatus/GetItems、成像/PrintWindow 预热、WarmUpNative、EnsureOverlay、RefreshOcrStatusAsync、内存定时器；成功检查应在所选初始化分支完成后提交。
- CaptureHistoryService.Instance 的默认 StorageDirectory 必须随隔离上下文改变，不能仅重定向 App.Config。
- StartupService.TrySetEnabled 与 RefreshIfEnabled 两个入口均须短路，不能仅删 App 的一次刷新调用；正常非 smoke 分支保持原意。


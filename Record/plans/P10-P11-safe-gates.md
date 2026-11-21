# P-10 / P-11 可信门禁实施契约

日期：2026-11-07 17:45。前置：主人已验收新版 Tab 并授权继续。源基线 HEAD `5e74f6215ee0a03d2bd22ccf7a48cc44811316e7`；现有 Record/docs 变更保留。

## 目标与非目标

- P-10 让冒烟实际观察本次进程的 XAML 初始化、主窗 Loaded 初始化完成与随后 UI 队列往返；计时不能产生成功。
- 冒烟不得改正常程序的配置/历史/日志或 HKCU Run；已有正常实例不得被激活，也不得由其代答成功。
- P-11 让构建、测试、格式、裁剪后输出、冒烟、包与证据绑定同一源码身份。工作树脏时必须记录 dirty 与源码指纹，不能冒称纯 HEAD。
- 本轮不验证真实键鼠截图/全局热键、托盘交互、OCR 网络；不得将跳过能力记为通过。不执行 GitHub 工作流、不上传、不 commit/push、不打 tag/Release。

## 冻结接口 v1

- 程序调用：`IshWY.exe --smoke`。仅该子进程环境设置 `ISHWY_SMOKE_NONCE=<Guid N 32hex>` 与 `ISHWY_SMOKE_ROOT=<TEMP>/IshWY-smoke/<nonce>`。
- 临时根需新建、空且可写；App 在任何 Config/日志/服务初始化前识别上下文，非法上下文失败关闭，不得回落正常数据目录。
- 结果位置：`<root>/smoke-result.json`。字段 camelCase：`schemaVersion=1, nonce, processId, executablePath, dataRoot, success, checks, error`。
- 三个成功检查：`xaml-initialized`、`main-window-loaded`、`dispatcher-roundtrip`；由真实完成事件产生，而不是睡眠到点。
- 脚本同时核验 exit0、schema、nonce、本次子进程 PID、实际启动 exe 绝对路径、隔离根、success=true、三个检查。缺失/旧结果/计时零退出一律失败。
- 30 秒是失败上限，只可终止脚本自己持有的子进程对象；不得按进程名终止用户实例。

## 分工与顺序

1. task-47：Flash App 工位，仅 App 必要接线、Core 纯协议/规则及其测试、自己的报告。正常启动行为保持；smoke 短路托盘、钩子、全局热键、预热与网络，报告中标未覆盖。所有 StartupService 写路径硬防护。
2. task-48：Flash scripts/CI 工位，不写 src。替换旧脚本假绿；版本参数验证与透传；同源门禁、日志与哈希 manifest；CI/Release 复用可信入口；SkipSmoke 显式未验证且完整gate必须非0，不能GATE PASS或CI绿（18:50裁决，详见P11-round1-repair.md）。离线负例可执行，不运行 App 或实际打包。
3. 第三 Flash 工位独立审查最终差异，重点所有隔离写路径、异常/竞态、既有实例、协议、manifest 与失效条件；只写审查报告。
4. 安全复核通过后 lead 冻结产品/脚本差异，统一整包隔离 Build、Core.Tests、format、脚本负例；再决定本地新 smoke 是否具备安全运行条件。若用户实例仍在，新 smoke 预期非0，不绕过/结束用户程序。

## 证据要求与边界

- 修改前基线：build-guard p10base 全 solution Build 0 警/0 错（27.70s），Core.Tests 305/305（91ms），format 0，guard EXIT=0。
- 子工位门禁仅过程证据；冻结树 lead 门禁才作本轮验收。禁止 `-t:Compile`、旧 `--smoke` 与合成输入/UIA。
- 本地 dirty 产物只能按 dirty 指纹标记；发布模式必须 clean 且源码身份稳定。证据记录 SDK、runtime/config/version、每条门禁命令/退出状态、裁剪后文件 SHA256 与 zip SHA256，不自证循环。
- 新协议未审查前不执行任何 App smoke。失败证据保留；无证据不得记通过。物理交互与发布授权保持独立。
- 18:50澄清：静态安全审查通过只允许lead受控执行，不等于完整验收；E09实际manifest/ZIP/smoke样本必须执行后复核，避免先有样本才准执行的循环前置。P11独立审查needs_revision，B1–B5与lead新增B6交第1轮返工。CI选择严格方案A，未知交互桌面条件不得用跳过smoke换绿；CI远端未演练。

## 后续

P-10/P-11 实现与验收落定后按总纲继续 B 语义/P-14b′、C 剩余、D；D-2B 五条设计待决不因继续授权而默认批准。

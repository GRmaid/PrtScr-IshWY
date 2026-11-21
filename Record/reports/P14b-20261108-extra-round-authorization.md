# P14b′门禁工具额外一轮许可

2026-11-08 17:59，主人对确认问题选择「允许额外一轮（推荐）」。

范围：仅本次P14b′ XAML命令行参数问题，以及同轮新证据目录接续。先最小参数实验，最小修复、独立复核，再跑完整隔离门禁。工具原R2额度耗尽的事实保留，本次为显式追加R3，不改名绕过。

原Record/reports/P14b-isolation-evidence下13:30 Before/After、源码Freeze/Verify、构建start/log均不删除、不覆盖。新轮标签p14b-app-20261108-1800-r3；新证据置同目录该标签子目录，构建置.tmp同标签；新源码Freeze仍引用原128 dirty基线，并在报告比较旧131 freeze。

不恢复P11，不启动App/smoke/UIA，不覆盖默认运行产物，不新增依赖，不提交/push/tag/发布。Core测试R2是独立剩余额度，仅等价修两处xUnit2031。

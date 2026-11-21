# Core Coordinator R1 收件（task67，尚未验收）

14:01收到同一实施单第一轮返工交付。仅新增的Coordinator及其测试两文件被改；未编译、测试、format、运行App。

## 身份
- src/PrtScr.Core/CaptureSessionCoordinator.cs：38484B/1012行，SHA256 2AD66727D4ED6151191E420B8F4907B7AA4782219A5E0C2CA7748C6A3A2367F5。
- src/PrtScr.Core.Tests/CaptureSessionCoordinatorTests.cs：64325B/1416行，SHA256 02924855DFA402686E9816FF628BC6653F43D9813AEBB14E1F06681B62F34677。
- 工位报告两文件CRLF无BOM。

## 工位声明（待独立复核）
新增唯一_active，Snapshot含Active，Busy=active或reservation。End释放活动但保留最近owner/kind/meta；Overlay保current、Quick清current。Empty End不得释放活动槽。Mark/Transition校验active/current/owner/unlatched且先校验状态再改meta。Invalidate/Shutdown不重分类已结束元数据，shutdown永久封口，latched重复仍AlreadyLatched而非新授权。Decide预留一致。

## 测试与签名
工位声明67Fact+7InlineData，即74执行行，尚未运行。新增End身份/meta、invalidated/ended迟到拒绝、预留抢占Capturing/未latch Committing、空槽/已结束shutdown、两组Barrier(2)各32轮latch-v-reserve与latch-v-shutdown。原343基线不变。Snapshot位置参数Active新增在SlotGeneration之后，其他公共签名声明未变。

## 验收边界
原始初版已needs_revision；本次仍需独立复核逐项核销Record/reports/P14b-20261108-Core-coordinator-review-R1.md。源文件完全冻结再接服务；门禁在最终App冻结后统一执行，不以工位静态计数冒充测试结果。P11仍暂停。

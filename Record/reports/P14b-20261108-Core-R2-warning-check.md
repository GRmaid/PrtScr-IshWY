# task67 Core R2 分析器警告修复核对

Flash工位9c44f351-dddb-4817-8577-0840705b4aa4已结束。仅src/PrtScr.Core.Tests/CaptureSessionCoordinatorTests.cs:1095/:1292改Assert.Single(log.Where(predicate))为Assert.Single(log,predicate)，不改谓词或测试数。

旧SHA256 02924855DFA402686E9816FF628BC6653F43D9813AEBB14E1F06681B62F34677；新SHA256 DDA4DD63856F3C322E53B0E6029C04671396E4DC6CC7DF16E820AA7B486AD734。-12字节，1416行CRLF无BOM。

lead已读两处上下文；新旧131文件freeze对比仅Tests文件改变，在内存逆转两断言后SHA256精确回到旧值，证明其它源码/测试字节不变。Record/tools/P14b-R3-SourceDelta.ps1 exit0。静态等价修复核对通过，不代表编译/测试通过；需后续整包门禁。

流程偏差：工位自报额外读取NuGet xunit.assert.xml确认重载，未事前申请，超出五初始文件边界；如实记录，不追认扩读权限，不将其当必需验证依据。lead接受依据是精确字节差异和后续真实编译/测试。未改业务API/Core/Bypass/服务/UI。

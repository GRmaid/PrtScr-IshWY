namespace PrtScr.Core;

/// <summary>截图延时下拉框与配置之间的纯映射。</summary>
public static class CaptureDelayRules
{
    public static int Normalize(int seconds) => seconds is 3 or 5 or 10 ? seconds : 0;

    public static int ToIndex(int seconds) => seconds switch
    {
        3 => 1,
        5 => 2,
        10 => 3,
        _ => 0,
    };

    public static int FromIndex(int index) => index switch
    {
        1 => 3,
        2 => 5,
        3 => 10,
        _ => 0,
    };
}

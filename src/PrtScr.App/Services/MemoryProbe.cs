using System.Diagnostics;

namespace PrtScr.App.Services;

/// <summary>
/// 轻量内存探针：只读进程计数并写日志，不参与回收。
/// 主人长时间截图/裁剪后可按时间线判断是常驻池、DIB 还是 GC 延迟。
/// </summary>
public static class MemoryProbe
{
    private static readonly Process Self = Process.GetCurrentProcess();

    public static void Log(string tag)
    {
        try
        {
            Self.Refresh();
            var ws = Self.WorkingSet64 / 1048576;
            var priv = Self.PrivateMemorySize64 / 1048576;
            var managed = GC.GetTotalMemory(false) / 1048576;
            var gc2 = GC.CollectionCount(2);
            App.Log($"Memory[{tag}]: ws={ws}MB private={priv}MB managed={managed}MB gen2={gc2}");
        }
        catch (Exception ex)
        {
            App.Log($"Memory[{tag}] 读取失败: {ex.Message}");
        }
    }
}

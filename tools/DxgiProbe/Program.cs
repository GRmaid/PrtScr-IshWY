using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.Capture;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int x; public int y; }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Pt pt, uint flags);

    private static void Main()
    {
        DesktopDuplicationCapture.Log = Console.WriteLine;
        WgcWindowCapture.Log = Console.WriteLine;

        var hMon = MonitorFromPoint(new Pt { x = 0, y = 0 }, 1);
        Console.WriteLine($"monitor = 0x{hMon:X}");

        var bmp = DesktopDuplicationCapture.Capture(hMon);
        if (bmp is null)
        {
            Console.WriteLine("DXGI FAILED");
            return;
        }

        using (bmp)
        {
            bmp.Save("probe.png", ImageFormat.Png);
            Console.WriteLine($"DXGI OK {bmp.Width}x{bmp.Height} -> probe.png");
        }
    }
}

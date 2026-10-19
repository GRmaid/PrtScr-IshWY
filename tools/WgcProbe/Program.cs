using System.Runtime.InteropServices;

internal static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int x; public int y; }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Pt pt, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    private static readonly Guid IID_IActivationFactory = new("00000035-0000-0000-C000-000000000046");
    private static readonly Guid IID_Interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static void Main()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr = WindowsCreateString(className, (uint)className.Length, out var hstr);
        Console.WriteLine($"WindowsCreateString hr=0x{hr:X8} hstr=0x{hstr:X}");
        hr = RoGetActivationFactory(hstr, IID_IActivationFactory, out var factory);
        WindowsDeleteString(hstr);
        Console.WriteLine($"RoGetActivationFactory hr=0x{hr:X8} factory=0x{factory:X}");
        if (hr != 0 || factory == 0) return;

        var qi = Marshal.QueryInterface(factory, IID_Interop, out var interop);
        Console.WriteLine($"QI IGraphicsCaptureItemInterop hr=0x{qi:X8} ptr=0x{interop:X}");
        if (qi != 0 || interop == 0) { Marshal.Release(factory); return; }

        var vtbl = *(nint**)interop;
        var createForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[3];
        var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[4];

        var hMon = MonitorFromPoint(new Pt(), 1);
        var fg = GetForegroundWindow();
        Console.WriteLine($"monitor=0x{hMon:X} foreground=0x{fg:X}");

        var iid = IID_IGraphicsCaptureItem;

        // 正确顺序：handle 在前，REFIID 在后
        nint item = 0;
        var r1 = createForMonitor(interop, hMon, &iid, &item);
        Console.WriteLine($"CreateForMonitor(monitor, iid)   hr=0x{r1:X8} item=0x{item:X}");

        nint item2 = 0;
        var r2 = createForWindow(interop, fg, &iid, &item2);
        Console.WriteLine($"CreateForWindow(fg, iid)         hr=0x{r2:X8} item=0x{item2:X}");

        // 错误顺序复现：REFIID 在前（当前生产代码的写法）
        nint item3 = 0;
        var createForMonitorBad = (delegate* unmanaged[Stdcall]<nint, Guid*, nint, nint*, int>)vtbl[4];
        var r3 = createForMonitorBad(interop, &iid, hMon, &item3);
        Console.WriteLine($"CreateForMonitor(iid, monitor)   hr=0x{r3:X8} item=0x{item3:X}");

        if (item != 0) ReleaseItem(item);
        if (item2 != 0) ReleaseItem(item2);
        if (item3 != 0) ReleaseItem(item3);

        Marshal.Release(interop);
        Marshal.Release(factory);
    }

    private static void ReleaseItem(nint item)
    {
        var vtbl = *(nint**)item;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(item);
    }
}

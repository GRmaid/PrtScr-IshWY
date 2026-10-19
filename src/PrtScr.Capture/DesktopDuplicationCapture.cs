using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace PrtScr.Capture;

/// <summary>
/// DXGI Desktop Duplication 显示器捕获（微信 / Snipping Tool / OBS 显示器捕获同源路线）。
/// 对全屏窗口化游戏（终末地、鸣潮等）比 GDI BitBlt 可靠得多，是 WGC 失败后的第二引擎。
/// 每次捕获创建一次 duplication（用后即释放），避免长期占用 GPU 与输出槽位。
/// </summary>
public static unsafe class DesktopDuplicationCapture
{
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_IDXGIOutput1 = new("00CDDEA8-939B-4B83-A340-A685226666CC");

    public static Action<string>? Log;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public NativeRect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long A, B, C, D, E, F, G, H;   // DXGI_OUTDUPL_FRAME_INFO 足够大的占位
    }

    [ComImport, Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIDevice
    {
        void _VtblGap1_4();
        void GetAdapter(out IDXGIAdapter adapter);
    }

    [ComImport, Guid("2411E7E1-12AC-4CCF-BD14-9798E8534DC0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        void _VtblGap1_4();
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    }

    [ComImport, Guid("AE02EEDB-C735-4690-8D52-5A8DC20213AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIOutput
    {
        void _VtblGap1_4();
        void GetDesc(out OutputDesc desc);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter, uint driverType, uint software, uint flags,
        nint featureLevels, uint numFeatureLevels, uint sdkVersion,
        out nint device, nint featureLevel, out nint immediateContext);

    private const uint DriverTypeHardware = 1;
    private const uint DeviceFlagBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    /// <summary>抓取指定显示器的一帧；失败（超时/被占用/不支持）返回 null。</summary>
    public static Bitmap? Capture(nint hMonitor)
    {
        try
        {
            var hr = D3D11CreateDevice(
                0, DriverTypeHardware, 0, DeviceFlagBgraSupport,
                0, 0, D3D11SdkVersion, out var device, 0, out var context);
            if (hr != 0 || device == 0)
            {
                Log?.Invoke($"DXGI: D3D11CreateDevice 失败 0x{hr:X}");
                return null;
            }

            try
            {
                return CaptureCore(device, context, hMonitor);
            }
            finally
            {
                _ = Marshal.Release(device);
                _ = Marshal.Release(context);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"DXGI: 捕获异常 {ex.GetType().Name} 0x{ex.HResult:X}: {ex.Message}");
            return null;
        }
    }

    private static unsafe Bitmap? CaptureCore(nint device, nint context, nint hMonitor)
    {
        object? deviceObject = null;
        object? adapterObject = null;
        object? outputObject = null;
        nint output1Ptr = 0;
        nint dupPtr = 0;

        try
        {
            deviceObject = Marshal.GetObjectForIUnknown(device);
            if (deviceObject is not IDXGIDevice dxgiDevice)
            {
                Log?.Invoke("DXGI: device 不支持 IDXGIDevice");
                return null;
            }

            dxgiDevice.GetAdapter(out var adapter);
            adapterObject = adapter;

            IDXGIOutput? matched = null;
            for (uint i = 0; ; i++)
            {
                var enumHr = adapter.EnumOutputs(i, out var output);
                if (enumHr == DXGI_ERROR_NOT_FOUND)
                    break;
                if (enumHr != 0)
                {
                    Log?.Invoke($"DXGI: EnumOutputs({i}) 失败 0x{enumHr:X}");
                    break;
                }

                output.GetDesc(out var desc);
                if (desc.Monitor == hMonitor)
                {
                    matched = output;
                    break;
                }
            }

            if (matched is null)
            {
                Log?.Invoke("DXGI: 未找到匹配 HMONITOR 的输出");
                return null;
            }

            outputObject = matched;

            // 直接拿 IDXGIOutput1 的原始指针，用 vtable 调用，绕开 COM 封送差异
            var outputUnknown = Marshal.GetIUnknownForObject(matched);
            try
            {
                var qiHr = Marshal.QueryInterface(outputUnknown, IID_IDXGIOutput1, out output1Ptr);
                if (qiHr != 0 || output1Ptr == 0)
                {
                    Log?.Invoke("DXGI: QI IDXGIOutput1 失败");
                    return null;
                }
            }
            finally
            {
                _ = Marshal.Release(outputUnknown);
            }

            // 走 CsWin32 按 Win32 元数据生成的接口，不再手写 vtable 下标。
            // 元数据中 IDXGIOutput1 的 19..22 依次是 GetDisplayModeList1 / FindClosestMatchingMode1 /
            // GetDisplaySurfaceData1 / DuplicateOutput，slot 22 已由 DxgiProbe 实测通过。
            // 旧代码用 slot 16（SetDisplaySurface），“S_OK 但 ptr=0”就是它造成的。
            var output1 = (IDXGIOutput1*)output1Ptr;
            IDXGIOutputDuplication* duplication = null;
            try
            {
                output1->DuplicateOutput((IUnknown*)device, &duplication);
            }
            catch (COMException ex)
            {
                Log?.Invoke($"DXGI: DuplicateOutput 失败 0x{ex.HResult:X}");
                return null;
            }

            if (duplication is null)
            {
                Log?.Invoke("DXGI: DuplicateOutput 返回空指针");
                return null;
            }

            dupPtr = (nint)duplication;

            var frameInfo = new DXGI_OUTDUPL_FRAME_INFO();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                IDXGIResource* resource = null;
                try
                {
                    duplication->AcquireNextFrame(400, &frameInfo, &resource);
                }
                catch (COMException ex) when (ex.HResult == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    Log?.Invoke("DXGI: AcquireNextFrame 超时");
                    continue;
                }
                catch (COMException ex)
                {
                    Log?.Invoke($"DXGI: AcquireNextFrame 失败 0x{ex.HResult:X}");
                    return null;
                }

                if (resource is null)
                {
                    Log?.Invoke("DXGI: AcquireNextFrame 返回空资源");
                    continue;
                }

                try
                {
                    var bitmap = CopyTexturePointer(device, context, (nint)resource);
                    if (bitmap is not null)
                    {
                        Log?.Invoke($"DXGI: 捕获 {bitmap.Width}×{bitmap.Height}");
                        return bitmap;
                    }
                }
                finally
                {
                    TryReleaseFrame(duplication);
                    _ = Marshal.Release((nint)resource);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"DXGI: 核心捕获异常 {ex}");
            return null;
        }
        finally
        {
            if (dupPtr != 0) _ = Marshal.Release(dupPtr);
            if (output1Ptr != 0) _ = Marshal.Release(output1Ptr);
            if (outputObject is not null) Marshal.ReleaseComObject(outputObject);
            if (adapterObject is not null) Marshal.ReleaseComObject(adapterObject);
            if (deviceObject is not null) Marshal.ReleaseComObject(deviceObject);
        }
    }

    private static void TryReleaseFrame(IDXGIOutputDuplication* duplication)
    {
        try
        {
            duplication->ReleaseFrame();
        }
        catch (COMException ex)
        {
            Log?.Invoke($"DXGI: ReleaseFrame 失败 0x{ex.HResult:X}");
        }
    }

    private static Bitmap? CopyTexturePointer(nint device, nint context, nint resourcePtr)
    {
        var hr = Marshal.QueryInterface(resourcePtr, IID_ID3D11Texture2D, out var texturePtr);
        if (hr != 0 || texturePtr == 0)
            return null;

        try
        {
            return CopyTexture(device, context, texturePtr);
        }
        finally
        {
            _ = Marshal.Release(texturePtr);
        }
    }

    private static Bitmap? CopyTexture(nint device, nint context, nint texturePtr)
    {
        var desc = new D3D11_TEXTURE2D_DESC();
        ((ID3D11Texture2D*)texturePtr)->GetDesc(&desc);

        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDesc = desc.SampleDesc,
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            BindFlags = 0,
            MiscFlags = 0,
        };

        ID3D11Texture2D* staging = null;
        ((ID3D11Device*)device)->CreateTexture2D(&stagingDesc, null, &staging);
        if (staging is null)
            return null;

        try
        {
            var deviceContext = (ID3D11DeviceContext*)context;
            deviceContext->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texturePtr);
            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            deviceContext->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            if (mapped.pData is null)
                return null;

            // duplication 刚建立时首帧可能是空表面（整帧 0），丢弃后由外层重试
            var anyNonZero = false;
            var totalBytes = (long)desc.Height * mapped.RowPitch;
            for (long i = 0; i < totalBytes; i += 4)
            {
                var p = (byte*)mapped.pData + (nint)i;
                if (p[0] != 0 || p[1] != 0 || p[2] != 0)
                {
                    anyNonZero = true;
                    break;
                }
            }
            if (!anyNonZero)
            {
                // 交给外层 finally 的 Unmap 释放，避免重复 Unmap
                Log?.Invoke("DXGI: 拷贝后整帧全零，丢弃本帧");
                return null;
            }

            try
            {
                var width = (int)desc.Width;
                var height = (int)desc.Height;
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var source = (byte*)mapped.pData;
                    var target = (byte*)data.Scan0;
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            source + (nint)y * mapped.RowPitch,
                            target + (nint)y * data.Stride,
                            data.Stride, (nint)width * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                return bitmap;
            }
            finally
            {
                deviceContext->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            _ = Marshal.Release((nint)staging);
        }
    }
}

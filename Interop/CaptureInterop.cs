using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowMonitor.Interop;

/// <summary>
/// Windows.Graphics.Capture 與 Direct3D 之間的互通。
/// WGC 是 WinRT API，但建立擷取目標與取回材質都必須經過傳統 COM 介面，
/// 這一層就是把兩邊接起來。
/// </summary>
internal static partial class CaptureInterop
{
    // windows.graphics.capture.interop.h
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    // windows.graphics.directx.direct3d11.interop.h
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    private const string GraphicsCaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    /// <summary>GraphicsCaptureItem 的 WinRT 介面 IID (IGraphicsCaptureItem)。</summary>
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// 從視窗控制代碼建立擷取目標。
    /// </summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        object interop = GetCaptureItemInterop();
        try
        {
            Guid iid = GraphicsCaptureItemIid;
            IntPtr itemPtr = ((IGraphicsCaptureItemInterop)interop).CreateForWindow(hwnd, ref iid);
            if (itemPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("無法為指定視窗建立擷取目標。");
            }

            try
            {
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                // FromAbi 會自行 AddRef，這裡要釋放 CreateForWindow 給的那一份參考
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(interop);
        }
    }

    private static object GetCaptureItemInterop()
    {
        // 走 RoGetActivationFactory 而非 CsWinRT 的內部 helper，避免相依於
        // 特定 CsWinRT 版本才有的 API。
        int hr = WindowsCreateString(
            GraphicsCaptureItemClassName,
            GraphicsCaptureItemClassName.Length,
            out IntPtr classId);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(classId, in interopIid, out IntPtr factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                return Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    /// <summary>
    /// 把 D3D11 裝置包成 WGC 需要的 WinRT IDirect3DDevice。
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();

        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr devicePtr);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(devicePtr);
        }
        finally
        {
            Marshal.Release(devicePtr);
        }
    }

    /// <summary>
    /// 從擷取到的畫面取回底層的 D3D11 材質。
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        object? access = null;
        try
        {
            access = Marshal.GetObjectForIUnknown(surfacePtr);

            Guid iid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr = ((IDirect3DDxgiInterfaceAccess)access).GetInterface(ref iid);

            // Vortice 的 ComObject 接管這份參考，不需要額外 Release
            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            // 這個方法每擷取一幀就會跑一次，RCW 若留給 GC 慢慢回收，
            // 底層的 COM 參考也會跟著延後釋放。
            if (access is not null)
            {
                Marshal.ReleaseComObject(access);
            }

            Marshal.Release(surfacePtr);
        }
    }
}

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowMonitor.Capture;

/// <summary>
/// D3D11 裝置與 GPU→CPU 讀回。擷取到的畫面是留在顯示卡上的材質，
/// 要拿到像素必須先複製到 staging 材質再 Map 回主記憶體。
/// </summary>
internal sealed class D3D11Helper : IDisposable
{
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public D3D11Helper()
    {
        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        ];

        // BgraSupport 是 WGC 的硬性要求，漏掉會在建立 frame pool 時失敗
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (result.Failure || device is null || context is null)
        {
            // 沒有相容的硬體裝置時退回 WARP 軟體轉譯，至少讓程式仍可運作
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out device,
                out context);

            result.CheckError();
        }

        Device = device!;
        Context = context!;
    }

    /// <summary>
    /// 把來源材質的內容讀回 <paramref name="target"/> 的像素緩衝區。
    /// </summary>
    public void CopyToFrame(ID3D11Texture2D source, FrameData target)
    {
        Texture2DDescription sourceDescription = source.Description;
        int width = (int)sourceDescription.Width;
        int height = (int)sourceDescription.Height;

        ID3D11Texture2D staging = EnsureStaging(width, height);
        target.Resize(width, height);

        Context.CopyResource(staging, source);

        MappedSubresource mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            CopyRows(mapped, target, width, height);
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// 逐列複製。GPU 會把每列對齊到特定邊界，因此 RowPitch 通常大於 Width * 4，
    /// 若整塊複製會得到斜切錯位的畫面。
    /// </summary>
    private static unsafe void CopyRows(MappedSubresource mapped, FrameData target, int width, int height)
    {
        int rowBytes = width * 4;
        byte* source = (byte*)mapped.DataPointer;

        fixed (byte* destination = target.Pixels)
        {
            if (mapped.RowPitch == rowBytes)
            {
                Buffer.MemoryCopy(source, destination, target.Pixels.Length, (long)rowBytes * height);
                return;
            }

            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    source + (long)y * mapped.RowPitch,
                    destination + (long)y * rowBytes,
                    rowBytes,
                    rowBytes);
            }
        }
    }

    private ID3D11Texture2D EnsureStaging(int width, int height)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height)
        {
            return _staging;
        }

        _staging?.Dispose();

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _staging = Device.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        return _staging;
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
        Context.Dispose();
        Device.Dispose();
    }
}

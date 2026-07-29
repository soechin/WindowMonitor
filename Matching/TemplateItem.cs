using OpenCvSharp;

namespace WindowMonitor.Matching;

/// <summary>
/// 一張樣板影像，外加局部搜尋所需的「上次命中位置」。
/// </summary>
public sealed class TemplateItem(string name, string path, Mat image) : IDisposable
{
    public string Name { get; } = name;

    public string Path { get; } = path;

    /// <summary>BGR 三通道。比對時直接使用，不要在外部修改。</summary>
    public Mat Image { get; } = image;

    public int Width => Image.Width;

    public int Height => Image.Height;

    /// <summary>
    /// 上次命中的左上角座標。只由比對執行緒讀寫，因此不需要額外同步。
    /// </summary>
    public bool HasLastHit { get; private set; }

    public int LastX { get; private set; }

    public int LastY { get; private set; }

    public void RememberHit(int x, int y)
    {
        HasLastHit = true;
        LastX = x;
        LastY = y;
    }

    /// <summary>忘記上次位置，下次比對會退回全圖搜尋。</summary>
    public void ForgetPosition()
    {
        HasLastHit = false;
    }

    public string Display => $"{Name}　{Width}×{Height}";

    public void Dispose()
    {
        Image.Dispose();
    }
}

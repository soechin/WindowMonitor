namespace WindowMonitor.Capture;

/// <summary>
/// 一幀畫面的像素資料。格式固定為 BGRA32，且 <see cref="Pixels"/> 為緊密排列
/// （Stride == Width * 4），可直接餵給 WriteableBitmap 或之後的 template matching。
/// </summary>
public sealed class FrameData
{
    public byte[] Pixels { get; private set; } = [];

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>每列位元組數。緊密排列，等於 Width * 4。</summary>
    public int Stride => Width * 4;

    public DateTime Timestamp { get; internal set; }

    public long FrameId { get; internal set; }

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>
    /// 確保緩衝區足以容納指定尺寸。尺寸未變時重用既有陣列，避免每幀重新配置。
    /// </summary>
    internal void Resize(int width, int height)
    {
        int required = width * height * 4;
        if (Pixels.Length < required)
        {
            Pixels = new byte[required];
        }

        Width = width;
        Height = height;
    }
}

/// <summary>
/// 只保留最新一幀的雙緩衝。擷取端寫入 back buffer，完成後交換；
/// 讀取端因此不會讀到寫到一半的畫面。
/// </summary>
public sealed class FrameBuffer
{
    private readonly Lock _sync = new();
    private FrameData _front = new();
    private FrameData _back = new();
    private long _frameId;
    private bool _hasFrame;

    /// <summary>擷取端取得可寫入的緩衝區。</summary>
    internal FrameData AcquireWriteBuffer(int width, int height)
    {
        _back.Resize(width, height);
        return _back;
    }

    /// <summary>擷取端完成寫入後呼叫，交換 front／back 並回傳新的最新幀。</summary>
    internal FrameData Publish()
    {
        lock (_sync)
        {
            _back.Timestamp = DateTime.Now;
            _back.FrameId = ++_frameId;

            (_front, _back) = (_back, _front);
            _hasFrame = true;
            return _front;
        }
    }

    /// <summary>
    /// 取得最新一幀。回傳的是內部緩衝區的參考而非複本——在下一次擷取完成前都是有效的，
    /// 對 1 FPS 的取樣頻率而言足夠寬裕。若需要長時間持有，請改用 <see cref="TryCopyLatest"/>。
    /// </summary>
    public bool TryGetLatest(out FrameData frame)
    {
        lock (_sync)
        {
            frame = _front;
            return _hasFrame && !_front.IsEmpty;
        }
    }

    /// <summary>取得最新一幀的獨立複本，可安全地跨執行緒長時間持有。</summary>
    public bool TryCopyLatest(out FrameData frame)
    {
        lock (_sync)
        {
            if (!_hasFrame || _front.IsEmpty)
            {
                frame = new FrameData();
                return false;
            }

            var copy = new FrameData();
            copy.Resize(_front.Width, _front.Height);
            Array.Copy(_front.Pixels, copy.Pixels, _front.Width * _front.Height * 4);
            copy.Timestamp = _front.Timestamp;
            copy.FrameId = _front.FrameId;

            frame = copy;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _hasFrame = false;
        }
    }
}

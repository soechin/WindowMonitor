using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using OpenCvSharp;
using WindowMonitor.Capture;

namespace WindowMonitor.Matching;

/// <summary>
/// 對每一幀畫面執行 template matching。
///
/// 比對直接在擷取執行緒上同步完成（<see cref="IFrameSource.FrameCaptured"/> 就是在那條
/// 執行緒上觸發的）。這麼做的好處是 <see cref="FrameData"/> 天然沒有競爭——會覆寫它的
/// 「下一次擷取」正是同一條執行緒的下一輪，因此不必每幀複製一份數 MB 的緩衝。
/// 代價是比對若慢於擷取間隔會拖慢擷取，所以要把 <see cref="LastElapsedMilliseconds"/>
/// 顯示給使用者判斷。
/// </summary>
public sealed class TemplateMatcher : IDisposable
{
    /// <summary>
    /// 每隔這麼多幀強制全圖搜尋一次。
    ///
    /// 局部搜尋落空時不會忘掉位置（見 <see cref="TryMatch"/>），因此目標若跑到 ROI
    /// 之外重新出現就再也找不回來。這個週期性的全圖掃描就是為了補住那個破口，
    /// 代價是那種情境下偵測最多延後這麼多幀。
    ///
    /// 以幀數而非秒數計：不論擷取頻率怎麼調，全圖搜尋永遠只佔固定比例的幀。
    /// </summary>
    private const int FullSweepIntervalFrames = 10;

    private readonly Lock _sync = new();

    private IFrameSource? _source;
    private Mat? _gray;
    private Mat? _scores;
    private ResultSet _results = new(0, []);

    private long _resultId;
    private int _lastWidth;
    private int _lastHeight;
    private int _framesSinceSweep;

    public TemplateLibrary Library { get; } = new();

    public bool IsEnabled { get; set; }

    /// <summary>
    /// 命中過之後就一直只在上次位置附近搜尋。目標會大幅移動時可關閉。
    /// </summary>
    public bool UseLocalSearch { get; set; } = true;

    public double Threshold { get; set; } = 0.80;

    public double LastElapsedMilliseconds { get; private set; }

    /// <summary>上一幀走局部搜尋的樣板數，供測試與效能觀察。</summary>
    public int LastLocalSearchCount { get; private set; }

    /// <summary>上一幀走全圖搜尋的樣板數。</summary>
    public int LastFullSearchCount { get; private set; }

    public string? LastError { get; private set; }

    public event EventHandler? ResultsUpdated;

    public void Attach(IFrameSource source)
    {
        Detach();

        _source = source;
        source.FrameCaptured += OnFrameCaptured;
    }

    public void Detach()
    {
        if (_source is not null)
        {
            _source.FrameCaptured -= OnFrameCaptured;
            _source = null;
        }
    }

    /// <summary>
    /// 取得最新一批結果。<paramref name="resultId"/> 與上次相同就代表沒有變化，
    /// 顯示端可以直接跳過重繪。
    /// </summary>
    public bool TryGetResults(out IReadOnlyList<MatchResult> results, out long resultId)
    {
        ResultSet snapshot = Volatile.Read(ref _results);

        results = snapshot.Items;
        resultId = snapshot.Id;
        return snapshot.Items.Length > 0;
    }

    /// <summary>清掉所有樣板記住的位置，下次一律從全圖搜尋開始。</summary>
    public void ForgetPositions()
    {
        Library.ForgetPositions();
    }

    /// <summary>清空結果並通知顯示端，用於停用比對或停止擷取時。</summary>
    public void ClearResults()
    {
        Publish([]);
    }

    private void OnFrameCaptured(object? sender, FrameData frame)
    {
        MatchFrame(frame);
    }

    /// <summary>
    /// 對一幀執行比對。公開是為了讓測試不必真的跑一次擷取。
    /// </summary>
    public void MatchFrame(FrameData frame)
    {
        if (!IsEnabled || frame.IsEmpty || Library.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                // 畫面尺寸變了，舊的命中座標就沒有意義了
                if (frame.Width != _lastWidth || frame.Height != _lastHeight)
                {
                    ForgetPositions();
                    _lastWidth = frame.Width;
                    _lastHeight = frame.Height;
                }

                // 這一幀輪到全圖重掃了嗎
                bool sweep = ++_framesSinceSweep >= FullSweepIntervalFrames;
                if (sweep)
                {
                    _framesSinceSweep = 0;
                }

                long start = Stopwatch.GetTimestamp();

                ConvertToGray(frame);

                List<MatchResult> hits = [];
                int local = 0;
                int full = 0;

                Library.ForEach(item =>
                {
                    if (TryMatch(item, frame.FrameId, sweep, ref local, ref full, out MatchResult? hit))
                    {
                        hits.Add(hit);
                    }
                });

                LastLocalSearchCount = local;
                LastFullSearchCount = full;
                LastElapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                LastError = null;

                Publish([.. hits]);
            }
            catch (Exception ex)
            {
                // 比對出問題不該讓擷取迴圈跟著掛掉
                LastError = ex.Message;
            }
        }
    }

    private void ConvertToGray(FrameData frame)
    {
        if (_gray is null || _gray.Width != frame.Width || _gray.Height != frame.Height)
        {
            _gray?.Dispose();
            _gray = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        }

        WrapAndConvert(frame, _gray);
    }

    /// <summary>
    /// 把 FrameData 的 BGRA 緩衝零複製地包成 Mat 再轉成灰階。
    /// 用 fixed 指標而不是 byte[] 多載，避免 GC 在轉換途中搬動陣列。
    /// </summary>
    private static unsafe void WrapAndConvert(FrameData frame, Mat destination)
    {
        fixed (byte* pixels = frame.Pixels)
        {
            // 一定要用 frame.Width／Height：Pixels 的長度可能大於實際畫面
            // （FrameData.Resize 只在不夠大時才重新配置），從陣列長度回推會算錯。
            using Mat bgra = Mat.FromPixelData(
                frame.Height,
                frame.Width,
                MatType.CV_8UC4,
                (IntPtr)pixels,
                frame.Stride);

            // 只比形狀，所以直接轉單通道灰階：MatchTemplate 的成本正比於通道數，
            // 這一步就省下三分之二。alpha 對視窗內容沒有意義，一併丟掉。
            Cv2.CvtColor(bgra, destination, ColorConversionCodes.BGRA2GRAY);
        }
    }

    private bool TryMatch(
        TemplateItem item,
        long frameId,
        bool forceFullSweep,
        ref int localCount,
        ref int fullCount,
        [NotNullWhen(true)] out MatchResult? result)
    {
        result = null;

        if (_gray is null || item.Width > _gray.Width || item.Height > _gray.Height)
        {
            // 樣板比畫面大時 MatchTemplate 會直接拋例外，必須先擋掉
            item.ForgetPosition();
            return false;
        }

        if (UseLocalSearch && item.HasLastHit && !forceFullSweep)
        {
            localCount++;

            // 附近找不到就只代表這一幀沒出現，位置要留著。這正是省下大部分成本的地方：
            // 監控的常態是「東西還沒出現」，一落空就忘掉位置的話，等待期間每一幀都會
            // 退回全圖卷積。代價是目標若跑到 ROI 之外重新出現會漏掉，
            // 由 FullSweepIntervalFrames 的全圖重掃補住。
            return TrySearch(item, BuildRoi(item, _gray.Width, _gray.Height), frameId, out result);
        }

        fullCount++;
        return TrySearch(item, new Rect(0, 0, _gray.Width, _gray.Height), frameId, out result);
    }

    /// <summary>
    /// 以上次命中的中心為中心，取約四分之一畫面（半寬 × 半高）的搜尋範圍。
    /// 目標固定出現在某處、只會稍微偏移，這個餘裕綽綽有餘，而面積只有全圖的 1/4。
    /// </summary>
    private static Rect BuildRoi(TemplateItem item, int frameWidth, int frameHeight)
    {
        // 樣板本身可能比半個畫面還大，範圍不能小於它
        int width = Math.Clamp(frameWidth / 2, item.Width, frameWidth);
        int height = Math.Clamp(frameHeight / 2, item.Height, frameHeight);

        int centerX = item.LastX + (item.Width / 2);
        int centerY = item.LastY + (item.Height / 2);

        int left = Math.Clamp(centerX - (width / 2), 0, frameWidth - width);
        int top = Math.Clamp(centerY - (height / 2), 0, frameHeight - height);

        return new Rect(left, top, width, height);
    }

    private bool TrySearch(
        TemplateItem item,
        Rect region,
        long frameId,
        [NotNullWhen(true)] out MatchResult? result)
    {
        result = null;

        if (region.Width < item.Width || region.Height < item.Height)
        {
            return false;
        }

        Mat scores = EnsureScores(region.Width - item.Width + 1, region.Height - item.Height + 1);

        // new Mat(src, rect) 取的是 view 而非複本，不會多複製像素
        using Mat view = new(_gray!, region);

        Cv2.MatchTemplate(view, item.Image, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double score, out _, out Point location);

        if (score < Threshold)
        {
            return false;
        }

        // MinMaxLoc 給的是 ROI 內的座標，要加回 ROI 原點才是畫面座標。
        // 漏掉這步的症狀是「畫面靜止時框的位置正確，一移動就偏掉」。
        int x = region.X + location.X;
        int y = region.Y + location.Y;

        item.RememberHit(x, y);
        result = new MatchResult(item.Name, x, y, item.Width, item.Height, score, frameId);
        return true;
    }

    private Mat EnsureScores(int width, int height)
    {
        // 局部與全圖搜尋的結果尺寸不同，所以要跟著搜尋範圍走，不能只看畫面尺寸
        if (_scores is null || _scores.Width != width || _scores.Height != height)
        {
            _scores?.Dispose();
            _scores = new Mat(height, width, MatType.CV_32FC1);
        }

        return _scores;
    }

    private void Publish(MatchResult[] hits)
    {
        Volatile.Write(ref _results, new ResultSet(Interlocked.Increment(ref _resultId), hits));
        ResultsUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Detach();

        lock (_sync)
        {
            _gray?.Dispose();
            _gray = null;

            _scores?.Dispose();
            _scores = null;
        }

        Library.Dispose();
    }

    /// <summary>結果與其版本編號綁在一起，讓顯示端能以單次讀取取得一致的快照。</summary>
    private sealed record ResultSet(long Id, MatchResult[] Items);
}

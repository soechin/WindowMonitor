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
    /// <summary>ROI 至少要往外擴這麼多像素，避免小樣板的搜尋範圍過於侷促。</summary>
    private const int MinimumPadding = 32;

    private readonly Lock _sync = new();

    private IFrameSource? _source;
    private Mat? _bgr;
    private Mat? _scores;
    private ResultSet _results = new(0, []);

    private long _resultId;
    private int _lastWidth;
    private int _lastHeight;

    public TemplateLibrary Library { get; } = new();

    public bool IsEnabled { get; set; }

    /// <summary>命中後只在上次位置附近搜尋。目標會大幅移動時可關閉。</summary>
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

                long start = Stopwatch.GetTimestamp();

                ConvertToBgr(frame);

                List<MatchResult> hits = [];
                int local = 0;
                int full = 0;

                Library.ForEach(item =>
                {
                    if (TryMatch(item, frame.FrameId, ref local, ref full, out MatchResult? hit))
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

    private void ConvertToBgr(FrameData frame)
    {
        if (_bgr is null || _bgr.Width != frame.Width || _bgr.Height != frame.Height)
        {
            _bgr?.Dispose();
            _bgr = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        }

        WrapAndConvert(frame, _bgr);
    }

    /// <summary>
    /// 把 FrameData 的 BGRA 緩衝零複製地包成 Mat 再轉成 BGR。
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

            // alpha 對視窗內容沒有意義，留著只會讓分數受不確定的值影響
            Cv2.CvtColor(bgra, destination, ColorConversionCodes.BGRA2BGR);
        }
    }

    private bool TryMatch(
        TemplateItem item,
        long frameId,
        ref int localCount,
        ref int fullCount,
        [NotNullWhen(true)] out MatchResult? result)
    {
        result = null;

        if (_bgr is null || item.Width > _bgr.Width || item.Height > _bgr.Height)
        {
            // 樣板比畫面大時 MatchTemplate 會直接拋例外，必須先擋掉
            item.ForgetPosition();
            return false;
        }

        if (UseLocalSearch && item.HasLastHit)
        {
            localCount++;
            if (TrySearch(item, BuildRoi(item, _bgr.Width, _bgr.Height), frameId, out result))
            {
                return true;
            }

            // 附近找不到就忘掉位置，同一幀立刻退回全圖——這樣結果才會與
            // 每幀全圖搜尋完全一致，局部搜尋純粹是省時間，不會漏偵測。
            item.ForgetPosition();
        }

        fullCount++;
        return TrySearch(item, new Rect(0, 0, _bgr.Width, _bgr.Height), frameId, out result);
    }

    private static Rect BuildRoi(TemplateItem item, int frameWidth, int frameHeight)
    {
        int padding = Math.Max(MinimumPadding, Math.Max(item.Width, item.Height));

        int left = Math.Max(0, item.LastX - padding);
        int top = Math.Max(0, item.LastY - padding);
        int right = Math.Min(frameWidth, item.LastX + item.Width + padding);
        int bottom = Math.Min(frameHeight, item.LastY + item.Height + padding);

        return new Rect(left, top, right - left, bottom - top);
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
        using Mat view = new(_bgr!, region);

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
            _bgr?.Dispose();
            _bgr = null;

            _scores?.Dispose();
            _scores = null;
        }

        Library.Dispose();
    }

    /// <summary>結果與其版本編號綁在一起，讓顯示端能以單次讀取取得一致的快照。</summary>
    private sealed record ResultSet(long Id, MatchResult[] Items);
}

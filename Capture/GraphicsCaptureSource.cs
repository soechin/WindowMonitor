using System.Diagnostics;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WindowMonitor.Interop;

namespace WindowMonitor.Capture;

/// <summary>
/// 以 Windows.Graphics.Capture 擷取視窗畫面。
///
/// 這是唯一能可靠取得 DX12 遊戲畫面像素的途徑：GDI 的 PrintWindow／BitBlt 對
/// DXGI 呈現的內容多半只會拿到全黑，而 DWM Thumbnail 雖然畫得出來，但畫面是由
/// 系統合成器繪製的，程式本身拿不到任何像素。
/// </summary>
public sealed class GraphicsCaptureSource : IFrameSource
{
    private const DirectXPixelFormat CaptureFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>
    /// frame pool 的緩衝數。因為是低頻主動取樣而非逐幀處理，2 個就夠——
    /// 但前提是每輪取樣都會把池子排空，見 <see cref="AcquireFreshFrame"/>。
    /// </summary>
    private const int BufferCount = 2;

    /// <summary>連續這麼久都沒有新畫面，才視為目標沒在呈現。</summary>
    private const int StaleThresholdMilliseconds = 3000;

    /// <summary>
    /// 排空池子後等待新幀的上限。約三個 60 Hz 更新週期，
    /// 目標若真的在動早就等到了；等不到就是畫面靜止，不值得再耗下去。
    /// </summary>
    private const int FreshFrameTimeoutMilliseconds = 50;

    /// <summary>保護底下那些欄位。只在極短的區段內持有。</summary>
    private readonly Lock _sync = new();

    /// <summary>
    /// 串接 Start 與 Stop 的生命週期鎖，與 <see cref="_sync"/> 分開：
    /// 這一把要橫跨「等待擷取迴圈退出」的整段時間，不能讓另一邊趁隙插進來建新 session。
    /// </summary>
    private readonly Lock _lifecycle = new();

    /// <summary>
    /// WGC 把新畫面放進池子時觸發。只用來喚醒擷取迴圈，不代表我們會逐幀處理：
    /// 池子填滿後 WGC 就停止產生，一個取樣週期內大約只會觸發 BufferCount 次。
    /// </summary>
    private readonly ManualResetEventSlim _frameSignal = new(false);

    private D3D11Helper? _d3d;
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private CancellationTokenSource? _cancellation;
    private Task? _captureLoop;
    private PeriodicTimer? _timer;

    private IntPtr _targetWindow;
    private SizeInt32 _poolSize;

    /// <summary>
    /// session 的世代，每次 <see cref="Start"/> 建立新 session 就 +1。
    /// 目標關閉時排到別的執行緒去收拾的那個 Stop 會帶著當時的世代，
    /// 收拾前先確認沒有換代。
    /// </summary>
    private long _generation;

    /// <summary>由擷取執行緒寫入、UI 執行緒讀取。</summary>
    private volatile CaptureState _state = CaptureState.Stopped;
    private int _intervalMilliseconds = 1000;
    private int _emptyFrameStreak;

    /// <summary>
    /// 目標關閉後資源是在別的執行緒上收拾的，所以這裡一併看狀態，
    /// 呼叫端才不會在收拾完成前還讀到「仍在擷取」。
    /// </summary>
    public bool IsRunning =>
        _session is not null && _state is not (CaptureState.TargetClosed or CaptureState.Failed);

    public FrameBuffer Frames { get; } = new();

    public int IntervalMilliseconds
    {
        get => _intervalMilliseconds;
        set
        {
            int clamped = Math.Clamp(value, 50, 60_000);
            _intervalMilliseconds = clamped;

            // PeriodicTimer 支援執行中調整週期
            if (_timer is not null)
            {
                try
                {
                    _timer.Period = TimeSpan.FromMilliseconds(clamped);
                }
                catch (ObjectDisposedException)
                {
                    // 擷取剛好停止，忽略
                }
            }
        }
    }

    public event EventHandler<FrameData>? FrameCaptured;

    public event EventHandler<CaptureStateEventArgs>? StateChanged;

    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public void Start(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
        {
            throw new ArgumentException("目標視窗無效。", nameof(targetWindow));
        }

        // 整個「停掉舊的、建起新的」必須是一個不可分割的動作，否則目標關閉時
        // 排在背景的那個 Stop 可能插在中間，把剛建好的 session 拆掉。
        lock (_lifecycle)
        {
            StopLocked(null);

            lock (_sync)
            {
                try
                {
                    _generation++;
                    _targetWindow = targetWindow;
                    _d3d = new D3D11Helper();
                    _device = CaptureInterop.CreateDirect3DDevice(_d3d.Device);
                    _item = CaptureInterop.CreateItemForWindow(targetWindow);
                    _item.Closed += OnItemClosed;

                    _poolSize = _item.Size;
                    _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                        _device,
                        CaptureFormat,
                        BufferCount,
                        _poolSize);
                    _framePool.FrameArrived += OnFrameArrived;

                    _session = _framePool.CreateCaptureSession(_item);
                    ConfigureSession(_session);
                    _session.StartCapture();

                    Frames.Clear();
                    _frameSignal.Reset();
                    _emptyFrameStreak = 0;

                    _cancellation = new CancellationTokenSource();
                    _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMilliseconds));
                    _captureLoop = Task.Run(() => CaptureLoopAsync(_cancellation.Token));

                    SetState(CaptureState.Running, "擷取中");
                }
                catch (Exception ex)
                {
                    DisposeCaptureResources();
                    SetState(CaptureState.Failed, $"啟動擷取失敗：{ex.Message}");
                    throw;
                }
            }
        }
    }

    private static void ConfigureSession(GraphicsCaptureSession session)
    {
        // 游標會疊在畫面上干擾 template matching，一律關閉
        TrySet(() => session.IsCursorCaptureEnabled = false);

        // 關閉 WGC 預設的黃色擷取邊框（需 Windows 11 22621 以上）
        if (ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            TrySet(() =>
            {
                // 非封裝應用通常不需要，但呼叫過才能確保設定生效
                _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            });

            TrySet(() => session.IsBorderRequired = false);
        }

        static void TrySet(Action action)
        {
            // 這些都是可有可無的美化設定，任一項不支援都不該讓擷取失敗
            try
            {
                action();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        PeriodicTimer? timer = _timer;
        if (timer is null)
        {
            return;
        }

        try
        {
            // 先立即抓一次，不必等第一個間隔過去
            CaptureOnce(token);

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                CaptureOnce(token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (ObjectDisposedException)
        {
            // 停止過程中 timer 已釋放
        }
        catch (Exception ex)
        {
            SetState(CaptureState.Failed, $"擷取中斷：{ex.Message}");
        }
    }

    private void CaptureOnce(CancellationToken token)
    {
        Direct3D11CaptureFramePool? framePool = _framePool;
        D3D11Helper? d3d = _d3d;
        if (framePool is null || d3d is null)
        {
            return;
        }

        // 不能只靠 GraphicsCaptureItem.Closed：實測發現目標視窗關閉時它不一定會送出。
        // 反正每輪都要跑，順手檢查一下視窗還在不在，成本可以忽略。
        if (_targetWindow != IntPtr.Zero && !NativeMethods.IsWindow(_targetWindow))
        {
            HandleTargetClosed();
            return;
        }

        using Direct3D11CaptureFrame? frame = AcquireFreshFrame(framePool, token);
        if (frame is null)
        {
            // 沒有新幀不等於出問題：WGC 只在內容變化時產生幀，畫面靜止
            // （遊戲暫停、停在選單）時本來就抓不到東西，最後一幀仍然有效。
            // 只有持續一段時間都沒有新畫面，才值得提醒使用者。
            _emptyFrameStreak++;
            if (_emptyFrameStreak * _intervalMilliseconds >= StaleThresholdMilliseconds)
            {
                SetState(CaptureState.NotPresenting, "畫面靜止或視窗已最小化");
            }

            return;
        }

        _emptyFrameStreak = 0;

        // 目標視窗尺寸改變時 frame pool 的材質會不敷使用，必須重建。
        // 重建後本幀捨棄，下一幀才是正確尺寸。
        SizeInt32 contentSize = frame.ContentSize;
        if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
        {
            if (contentSize.Width > 0 && contentSize.Height > 0)
            {
                _poolSize = contentSize;
                framePool.Recreate(_device, CaptureFormat, BufferCount, _poolSize);
            }

            return;
        }

        using ID3D11Texture2D texture = CaptureInterop.GetTexture(frame.Surface);

        FrameData buffer = Frames.AcquireWriteBuffer(contentSize.Width, contentSize.Height);
        d3d.CopyToFrame(texture, buffer);
        FrameData published = Frames.Publish(MeasureAge(frame));

        SetState(CaptureState.Running, "擷取中");
        FrameCaptured?.Invoke(this, published);
    }

    /// <summary>
    /// 取得一張盡可能新的畫面。
    ///
    /// frame pool 是先進先出的佇列，<see cref="Direct3D11CaptureFramePool.TryGetNextFrame"/>
    /// 給的是最舊的那張；而池子一滿，WGC 就不再產生新畫面。低頻取樣時若每輪只取走一張，
    /// 佇列就永遠是滿的，拿到的像素會固定舊上約 BufferCount 個取樣間隔——1 FPS 時就是兩秒。
    ///
    /// 所以每輪先把積壓全部倒掉（buffer 還給 WGC，它才會繼續產生），再等一張真正的新幀。
    /// 這不等於逐幀處理：昂貴的讀回與比對仍然每輪只做一次，這裡多出來的成本就是
    /// 幾次 TryGetNextFrame 與一次等待。
    /// </summary>
    private Direct3D11CaptureFrame? AcquireFreshFrame(
        Direct3D11CaptureFramePool framePool,
        CancellationToken token)
    {
        // 先 Reset 再排空：排空期間抵達的新幀會把訊號留著，不會漏掉喚醒
        _frameSignal.Reset();

        // 積壓的都是舊畫面，只留最後一張當備援，
        // 免得更新率低於取樣率的目標整輪落空。
        Direct3D11CaptureFrame? frame = null;
        while (framePool.TryGetNextFrame() is { } stale)
        {
            frame?.Dispose();
            frame = stale;
        }

        // 逾時也照樣再撈一次：訊號可能在 Reset 之前就觸發過，那張其實已經在佇列裡了
        _frameSignal.Wait(FreshFrameTimeoutMilliseconds, token);

        if (framePool.TryGetNextFrame() is { } fresh)
        {
            frame?.Dispose();
            frame = fresh;
        }

        return frame;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // 只喚醒擷取迴圈。這個回呼跑在 WGC 的執行緒上，不能在這裡做任何實質工作。
        _frameSignal.Set();
    }

    /// <summary>
    /// 這張畫面從被擷取到現在過了多久。
    /// SystemRelativeTime 與 <see cref="Stopwatch"/> 同為 QPC 時基，可以直接相減。
    /// </summary>
    private static TimeSpan MeasureAge(Direct3D11CaptureFrame frame)
    {
        TimeSpan now = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        TimeSpan age = now - frame.SystemRelativeTime;

        // 時基理應一致，但真要對不上也不該回報負數
        return age > TimeSpan.Zero ? age : TimeSpan.Zero;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        HandleTargetClosed();
    }

    private void HandleTargetClosed()
    {
        if (_state == CaptureState.TargetClosed)
        {
            return;
        }

        SetState(CaptureState.TargetClosed, "目標視窗已關閉");

        long generation;
        lock (_sync)
        {
            generation = _generation;
        }

        // Stop() 會等待擷取迴圈結束，而這裡可能正是在該迴圈上執行，
        // 因此丟到別的執行緒去收拾，避免自我等待。帶上世代：這段期間 UI 已經
        // 解鎖了「開始擷取」，使用者若馬上啟動新的擷取，這次收拾必須放手。
        Task.Run(() =>
        {
            lock (_lifecycle)
            {
                StopLocked(generation);
            }
        });
    }

    private void SetState(CaptureState state, string message)
    {
        // 狀態沒變就不重複通知，避免每秒都在更新 UI
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new CaptureStateEventArgs(state, message));
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopLocked(null);
        }
    }

    /// <summary>
    /// 停止擷取。呼叫端必須已持有 <see cref="_lifecycle"/>。
    /// </summary>
    /// <param name="generation">
    /// 指定時，只有在 session 世代仍相符的情況下才真的停——用於目標關閉後排到
    /// 別的執行緒去收拾的那次呼叫，期間使用者若已重新啟動擷取就什麼都不該做。
    /// null 代表無條件停止（使用者按停止、或 <see cref="Start"/> 換目標）。
    /// </param>
    private void StopLocked(long? generation)
    {
        CancellationTokenSource? cancellation;
        Task? loop;

        lock (_sync)
        {
            if (generation is long expected && _generation != expected)
            {
                return;
            }

            cancellation = _cancellation;
            loop = _captureLoop;
            _cancellation = null;
            _captureLoop = null;
        }

        cancellation?.Cancel();

        // 等擷取迴圈退出後才釋放原生資源，否則會在讀回途中把材質抽掉。
        // 這裡刻意不設逾時：逾時就往下釋放等於明知故犯，而取消只在
        // WaitForNextTickAsync 被觀察到，最久就是一次 CaptureOnce
        // （含同步跑的 template matching）的時間，是有界的。
        try
        {
            loop?.Wait();
        }
        catch (AggregateException)
        {
            // 迴圈自身的例外已在其中處理過
        }

        cancellation?.Dispose();

        lock (_sync)
        {
            DisposeCaptureResources();
        }

        if (_state is not (CaptureState.TargetClosed or CaptureState.Failed))
        {
            SetState(CaptureState.Stopped, "已停止");
        }
    }

    private void DisposeCaptureResources()
    {
        _timer?.Dispose();
        _timer = null;

        _session?.Dispose();
        _session = null;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        _targetWindow = IntPtr.Zero;

        _device?.Dispose();
        _device = null;

        _d3d?.Dispose();
        _d3d = null;
    }

    public void Dispose()
    {
        // Stop 會等擷取迴圈退出，之後才沒人會再碰 _frameSignal
        Stop();
        _frameSignal.Dispose();
    }
}

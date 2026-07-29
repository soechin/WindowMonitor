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
    /// frame pool 的緩衝數。因為是低頻主動取樣而非逐幀處理，2 個就夠。
    /// </summary>
    private const int BufferCount = 2;

    /// <summary>連續這麼久都沒有新畫面，才視為目標沒在呈現。</summary>
    private const int StaleThresholdMilliseconds = 3000;

    private readonly Lock _sync = new();

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

        Stop();

        lock (_sync)
        {
            try
            {
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

                _session = _framePool.CreateCaptureSession(_item);
                ConfigureSession(_session);
                _session.StartCapture();

                Frames.Clear();
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
            CaptureOnce();

            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                CaptureOnce();
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

    private void CaptureOnce()
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

        using Direct3D11CaptureFrame? frame = framePool.TryGetNextFrame();
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
        FrameData published = Frames.Publish();

        SetState(CaptureState.Running, "擷取中");
        FrameCaptured?.Invoke(this, published);
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

        // Stop() 會等待擷取迴圈結束，而這裡可能正是在該迴圈上執行，
        // 因此丟到別的執行緒去收拾，避免自我等待。
        Task.Run(Stop);
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
        CancellationTokenSource? cancellation;
        Task? loop;

        lock (_sync)
        {
            cancellation = _cancellation;
            loop = _captureLoop;
            _cancellation = null;
            _captureLoop = null;
        }

        cancellation?.Cancel();

        // 等擷取迴圈退出後才釋放原生資源，否則會在讀回途中把材質抽掉
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
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

        _framePool?.Dispose();
        _framePool = null;

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
        Stop();
    }
}

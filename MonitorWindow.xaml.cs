using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowMonitor.Capture;
using WindowMonitor.Interop;

namespace WindowMonitor;

/// <summary>
/// 顯示擷取畫面的置頂小視窗。本身不提供任何控制介面，
/// 所有行為都由主視窗透過屬性設定。
/// </summary>
public partial class MonitorWindow : Window
{
    /// <summary>游標移入偵測與透明度過渡共用的輪詢間隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(60);

    /// <summary>每次輪詢的不透明度變化量，用來讓淡入淡出看起來平順。</summary>
    private const double OpacityStep = 0.14;

    /// <summary>視窗邊緣可用來縮放的寬度（DIP）。</summary>
    private const double ResizeBorderThickness = 8;

    private readonly IFrameSource _source;
    private readonly DispatcherTimer _timer;

    private WriteableBitmap? _bitmap;
    private IntPtr _handle;
    private bool _isHovered;

    /// <summary>由擷取執行緒設定、UI 執行緒的計時器讀取。</summary>
    private volatile bool _pendingFrame;

    private double _normalOpacity = 1.0;
    private double _hoverOpacity = 0.6;
    private bool _fadeOnHover = true;
    private bool _clickThrough;

    public MonitorWindow(IFrameSource source)
    {
        InitializeComponent();

        _source = source;
        _source.FrameCaptured += OnFrameCaptured;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PollInterval
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// 點擊穿透。開啟時整個視窗不接收滑鼠事件，也就無法拖曳或縮放，
    /// 需回主視窗關閉。
    /// </summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyClickThrough();
        }
    }

    /// <summary>一般狀態的不透明度（0~100）。</summary>
    public int OpacityPercent
    {
        get => (int)Math.Round(_normalOpacity * 100);
        set => _normalOpacity = PercentToOpacity(value);
    }

    /// <summary>游標移入時要淡到的不透明度（0~100）。</summary>
    public int HoverOpacityPercent
    {
        get => (int)Math.Round(_hoverOpacity * 100);
        set => _hoverOpacity = PercentToOpacity(value);
    }

    public bool FadeOnHover
    {
        get => _fadeOnHover;
        set
        {
            _fadeOnHover = value;
            if (!value)
            {
                _isHovered = false;
            }
        }
    }

    private static double PercentToOpacity(int percent)
    {
        return Math.Clamp(percent, 5, 100) / 100.0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _handle = new WindowInteropHelper(this).Handle;

        // AllowsTransparency="True" 之下 WPF 會接管命中測試，整個視窗都算 client 區，
        // 系統原本提供的邊緣縮放熱區會消失。這裡自己補回來。
        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);

        // 透明度走 WPF 的 Window.Opacity（需要 AllowsTransparency="True"）。
        // 原本試過 WS_EX_LAYERED + SetLayeredWindowAttributes，但 WPF 在硬體轉譯
        // 路徑下會把 WS_EX_LAYERED 清掉，透明度完全不會生效。
        // 本視窗只是每秒更新一張點陣圖，走軟體轉譯的成本可以忽略。
        Opacity = TargetOpacity;
        ApplyClickThrough();

        PositionAtBottomRight();
        _timer.Start();
    }

    /// <summary>
    /// 只處理 WM_NCHITTEST，讓視窗邊緣重新變成縮放熱區。
    /// 中央維持 HTCLIENT，這樣 DragMove 才能照常拖曳。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out RECT bounds))
        {
            return IntPtr.Zero;
        }

        // lParam 帶的是螢幕座標，必須以有號數解讀，
        // 否則主螢幕左側或上方的第二螢幕（負座標）會算錯。
        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));

        int border = (int)Math.Round(ResizeBorderThickness * VisualTreeHelper.GetDpi(this).DpiScaleX);

        bool left = x < bounds.Left + border;
        bool right = x >= bounds.Right - border;
        bool top = y < bounds.Top + border;
        bool bottom = y >= bounds.Bottom - border;

        int hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeMethods.HTTOPLEFT,
            (_, true, true, _) => NativeMethods.HTTOPRIGHT,
            (true, _, _, true) => NativeMethods.HTBOTTOMLEFT,
            (_, true, _, true) => NativeMethods.HTBOTTOMRIGHT,
            (true, _, _, _) => NativeMethods.HTLEFT,
            (_, true, _, _) => NativeMethods.HTRIGHT,
            (_, _, true, _) => NativeMethods.HTTOP,
            (_, _, _, true) => NativeMethods.HTBOTTOM,
            _ => 0
        };

        if (hit == 0)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return hit;
    }

    /// <summary>把視窗移到工作區右下角（WorkArea 已扣除工作列）。</summary>
    public void PositionAtBottomRight()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double margin = 16;

        Left = workArea.Right - Width - margin;
        Top = workArea.Bottom - Height - margin;
    }

    private void OnFrameCaptured(object? sender, FrameData frame)
    {
        // 事件來自擷取執行緒，這裡只記下「有新畫面」，
        // 實際讀取在 UI 執行緒重新向 FrameBuffer 取最新一幀，
        // 避免用到已被下一次擷取覆寫的緩衝區。
        _pendingFrame = true;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_pendingFrame)
        {
            _pendingFrame = false;
            RenderLatestFrame();
        }

        UpdateHoverState();
        StepOpacity();
    }

    private void RenderLatestFrame()
    {
        if (!_source.Frames.TryGetLatest(out FrameData frame) || frame.IsEmpty)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            FrameImage.Source = _bitmap;
        }

        _bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Stride,
            0);

        if (PlaceholderText.Visibility != Visibility.Collapsed)
        {
            PlaceholderText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 以輪詢判斷游標是否在視窗上。開啟點擊穿透時視窗收不到 MouseEnter／MouseLeave，
    /// 因此統一用輪詢，避免維護兩套邏輯。
    /// </summary>
    private void UpdateHoverState()
    {
        if (!_fadeOnHover)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out POINT cursor) ||
            !NativeMethods.GetWindowRect(_handle, out RECT bounds))
        {
            return;
        }

        bool hovered = cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                       cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;

        if (hovered != _isHovered)
        {
            _isHovered = hovered;
        }
    }

    private double TargetOpacity => _fadeOnHover && _isHovered ? _hoverOpacity : _normalOpacity;

    private void StepOpacity()
    {
        double target = TargetOpacity;
        double delta = target - Opacity;

        if (Math.Abs(delta) < 0.005)
        {
            if (Opacity != target)
            {
                Opacity = target;
            }

            return;
        }

        Opacity += Math.Clamp(delta, -OpacityStep, OpacityStep);
    }

    private void ApplyClickThrough()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // WS_EX_TRANSPARENT 讓整個視窗不參與命中測試，點擊直接落到下層視窗。
        // 搭配 WS_EX_NOACTIVATE 一併避免穿透期間意外搶走焦點。
        NativeMethods.SetExStyle(_handle, NativeMethods.WS_EX_TRANSPARENT, _clickThrough);
        NativeMethods.SetExStyle(_handle, NativeMethods.WS_EX_NOACTIVATE, _clickThrough);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 穿透開啟時根本收不到這個事件，因此不必額外判斷
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 使用者放開按鍵的時機剛好卡在拖曳開始前，忽略即可
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _source.FrameCaptured -= OnFrameCaptured;

        base.OnClosed(e);
    }
}

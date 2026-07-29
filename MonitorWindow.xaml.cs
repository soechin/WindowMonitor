using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowMonitor.Capture;
using WindowMonitor.Interop;
using WindowMonitor.Matching;

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

    /// <summary>命中標籤的高度（DIP），用來決定標籤放在框上方還是框內。</summary>
    private const double MarkerLabelHeight = 15;

    private static readonly Brush MarkerStroke = CreateFrozenBrush(Color.FromRgb(0x3F, 0xE0, 0x7C));
    private static readonly Brush MarkerLabelBackground =
        CreateFrozenBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10));

    private readonly IFrameSource _source;
    private readonly DispatcherTimer _timer;

    private WriteableBitmap? _bitmap;
    private IntPtr _handle;
    private bool _isHovered;

    /// <summary>上次畫進 Overlay 的結果版本。−1 代表「下次一定重畫」。</summary>
    private long _overlayResultId = -1;

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

        // 視窗大小改變後 letterbox 的縮放與位移都會變，命中框必須重算
        SizeChanged += (_, _) => _overlayResultId = -1;
    }

    /// <summary>
    /// 由主視窗注入。為 null 時完全不畫命中框。
    /// </summary>
    public TemplateMatcher? Matcher { get; set; }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

        UpdateOverlay();
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

            // 換了尺寸，命中框的換算基準也跟著變
            _overlayResultId = -1;
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
    /// 把比對命中的位置畫到 Overlay 上。結果版本沒變就不重畫。
    /// </summary>
    private void UpdateOverlay()
    {
        TemplateMatcher? matcher = Matcher;
        if (matcher is null || !matcher.IsEnabled)
        {
            ClearOverlay();
            return;
        }

        matcher.TryGetResults(out IReadOnlyList<MatchResult> results, out long resultId);
        if (resultId == _overlayResultId)
        {
            return;
        }

        // 一定要用 Overlay 自己的尺寸，不能用 FrameImage 的。
        // Image 在 Stretch="Uniform" 之下，ActualWidth／ActualHeight 是「縮放後的內容尺寸」
        // 而不是版面配置給它的格子尺寸，拿它算出來的 offset 永遠是 0——畫面有 letterbox 時
        // 命中框就會整個偏掉。Canvas 則是實實在在填滿整個格子。
        double areaWidth = Overlay.ActualWidth;
        double areaHeight = Overlay.ActualHeight;

        if (_bitmap is null || areaWidth <= 0 || areaHeight <= 0)
        {
            // 版面還沒算出來，保持 _overlayResultId 不變，下一輪再畫
            return;
        }

        _overlayResultId = resultId;
        Overlay.Children.Clear();

        if (results.Count == 0)
        {
            return;
        }

        // Uniform 縮放：等比放到放得下為止，剩下的空間在兩側平均留白
        double scale = Math.Min(areaWidth / _bitmap.PixelWidth, areaHeight / _bitmap.PixelHeight);
        double offsetX = (areaWidth - (_bitmap.PixelWidth * scale)) / 2;
        double offsetY = (areaHeight - (_bitmap.PixelHeight * scale)) / 2;

        foreach (MatchResult hit in results)
        {
            AddMarker(hit, scale, offsetX, offsetY);
        }
    }

    private void AddMarker(MatchResult hit, double scale, double offsetX, double offsetY)
    {
        double x = offsetX + (hit.X * scale);
        double y = offsetY + (hit.Y * scale);

        var box = new Rectangle
        {
            Width = hit.Width * scale,
            Height = hit.Height * scale,
            Stroke = MarkerStroke,
            StrokeThickness = 1.5
        };

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        Overlay.Children.Add(box);

        // 分數畫在框上。主視窗沒有結果清單，這是唯一能回饋門檻是否合適的地方。
        var label = new TextBlock
        {
            Text = $"{hit.TemplateName} {hit.Score:F2}",
            Foreground = MarkerStroke,
            Background = MarkerLabelBackground,
            FontSize = 11,
            Padding = new Thickness(3, 0, 3, 0)
        };

        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y >= MarkerLabelHeight ? y - MarkerLabelHeight : y);
        Overlay.Children.Add(label);
    }

    private void ClearOverlay()
    {
        if (Overlay.Children.Count > 0)
        {
            Overlay.Children.Clear();
        }

        _overlayResultId = -1;
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

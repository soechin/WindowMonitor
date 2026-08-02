using System.Runtime.InteropServices;
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

    /// <summary>
    /// ContentLayer 左右／上下各 1px 邊框，換算長寬比時要從視窗尺寸扣掉，
    /// 否則畫面區的比例永遠差一點，letterbox 黑邊不會完全消失。
    /// </summary>
    private const double ChromeThickness = 2;

    /// <summary>
    /// 視窗任一邊的最小長度（DIP）。XAML 的 MinWidth／MinHeight 只是絕對下限，
    /// 實際下限在這裡以「保持長寬比」的方式計算，避免兩者互相打架。
    /// </summary>
    private const double MinEdgeLength = 96;

    /// <summary>命中標籤的高度（DIP），用來讓標籤對齊點的垂直中心。</summary>
    private const double MarkerLabelHeight = 15;

    /// <summary>命中點標記的直徑（DIP）。</summary>
    private const double MarkerDotSize = 7;

    /// <summary>警告紅框閃爍一個完整週期所需的輪詢次數（約 0.7 秒）。</summary>
    private const int AlertPulseTicks = 12;

    private const double AlertMinOpacity = 0.2;
    private const double AlertMaxOpacity = 1.0;

    private static readonly Brush MarkerBrush = CreateFrozenBrush(Color.FromRgb(0x3F, 0xE0, 0x7C));
    private static readonly Brush MarkerLabelBackground =
        CreateFrozenBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10));

    private readonly IFrameSource _source;
    private readonly DispatcherTimer _timer;

    private WriteableBitmap? _bitmap;
    private IntPtr _handle;
    private bool _isHovered;

    /// <summary>目標畫面的寬高比（寬÷高）。0 代表還沒收到任何影格，此時不鎖比例。</summary>
    private double _aspectRatio;

    /// <summary>上次畫進 Overlay 的結果版本。−1 代表「下次一定重畫」。</summary>
    private long _overlayResultId = -1;

    /// <summary>目前是否有命中，決定警告紅框要不要亮。</summary>
    private bool _alertActive;

    /// <summary>閃爍的相位，每輪詢一次進一格。</summary>
    private int _alertPhase;

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

        SizeChanged += (_, _) =>
        {
            // 視窗大小改變後 letterbox 的縮放與位移都會變，命中框必須重算
            _overlayResultId = -1;
            ClampPositionToMonitor();
        };
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

        // 透明度套在 ContentLayer 而不是 Window.Opacity——後者作用於整個視窗的合成
        // 結果，會連警告紅框一起乘算掉（40% 視窗 × 閃爍波谷 0.2 幾乎看不見）。
        // Window.Opacity 固定維持 1.0，AlertBorder 掛在 ContentLayer 之外。
        // 另註：原本試過 WS_EX_LAYERED + SetLayeredWindowAttributes，但 WPF 在硬體
        // 轉譯路徑下會把 WS_EX_LAYERED 清掉，透明度完全不會生效；靠
        // AllowsTransparency="True" 讓 WPF 自己走分層合成才正確。
        // 本視窗只是每秒更新一張點陣圖，走軟體轉譯的成本可以忽略。
        ContentLayer.Opacity = TargetOpacity;
        ApplyClickThrough();

        PositionAtBottomRight();
        _timer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        return msg switch
        {
            NativeMethods.WM_NCHITTEST => HandleNcHitTest(hwnd, lParam, ref handled),
            NativeMethods.WM_SIZING => HandleSizing(wParam, lParam, ref handled),
            NativeMethods.WM_WINDOWPOSCHANGING => HandleWindowPosChanging(hwnd, lParam, ref handled),
            _ => IntPtr.Zero
        };
    }

    /// <summary>
    /// 讓視窗邊緣重新變成縮放熱區。
    /// 中央維持 HTCLIENT，這樣 DragMove 才能照常拖曳。
    /// </summary>
    private IntPtr HandleNcHitTest(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
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

    /// <summary>
    /// 使用者拖曳邊框縮放時介入：鎖定長寬比，並且不讓視窗長到螢幕外面。
    /// lParam 是實體像素的螢幕座標矩形。
    /// </summary>
    private IntPtr HandleSizing(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        RECT rect = Marshal.PtrToStructure<RECT>(lParam);
        int edge = (int)wParam;

        // 被拖的那條邊要跟著游標，所以固定「對面那條邊」當錨點回推新矩形
        bool anchorRight = edge is NativeMethods.WMSZ_LEFT
            or NativeMethods.WMSZ_TOPLEFT
            or NativeMethods.WMSZ_BOTTOMLEFT;
        bool anchorBottom = edge is NativeMethods.WMSZ_TOP
            or NativeMethods.WMSZ_TOPLEFT
            or NativeMethods.WMSZ_TOPRIGHT;

        // 只拖上下邊時由高推寬，其餘（左右邊與四個角）都由寬推高
        bool drivenByHeight = edge is NativeMethods.WMSZ_TOP or NativeMethods.WMSZ_BOTTOM;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double chromeX = ChromeThickness * dpi.DpiScaleX;
        double chromeY = ChromeThickness * dpi.DpiScaleY;
        double minWidth = MinEdgeLength * dpi.DpiScaleX;
        double minHeight = MinEdgeLength * dpi.DpiScaleY;

        // 從錨點邊算到螢幕邊界，就是這次縮放最多能長到多大
        double availWidth = double.PositiveInfinity;
        double availHeight = double.PositiveInfinity;

        if (NativeMethods.TryGetMonitorBounds(rect, out RECT monitor, out _))
        {
            availWidth = anchorRight ? rect.Right - monitor.Left : monitor.Right - rect.Left;
            availHeight = anchorBottom ? rect.Bottom - monitor.Top : monitor.Bottom - rect.Top;

            // 錨點邊本身就在螢幕外時不硬壓，交給 WM_WINDOWPOSCHANGING 收尾
            availWidth = Math.Max(availWidth, minWidth);
            availHeight = Math.Max(availHeight, minHeight);
        }

        double contentWidth = rect.Width - chromeX;
        double contentHeight = rect.Height - chromeY;

        if (_aspectRatio > 0)
        {
            if (drivenByHeight)
            {
                contentWidth = contentHeight * _aspectRatio;
            }
            else
            {
                contentHeight = contentWidth / _aspectRatio;
            }

            FitToLimits(
                ref contentWidth, ref contentHeight,
                minWidth - chromeX, minHeight - chromeY,
                availWidth - chromeX, availHeight - chromeY);
        }
        else
        {
            // 還沒有畫面，只做尺寸與邊界箝制
            contentWidth = Math.Clamp(contentWidth, minWidth - chromeX, availWidth - chromeX);
            contentHeight = Math.Clamp(contentHeight, minHeight - chromeY, availHeight - chromeY);
        }

        int width = (int)Math.Round(contentWidth + chromeX);
        int height = (int)Math.Round(contentHeight + chromeY);

        if (anchorRight)
        {
            rect.Left = rect.Right - width;
        }
        else
        {
            rect.Right = rect.Left + width;
        }

        if (anchorBottom)
        {
            rect.Top = rect.Bottom - height;
        }
        else
        {
            rect.Bottom = rect.Top + height;
        }

        Marshal.StructureToPtr(rect, lParam, false);

        handled = true;
        return (IntPtr)1;
    }

    /// <summary>
    /// 攔下所有移動與尺寸變更（拖曳、程式設定 Width／Height、DPI 切換），把視窗夾回螢幕內。
    /// 這裡刻意不設 handled，只就地改寫 WINDOWPOS——WPF 自己也要靠這個訊息同步
    /// Left／Top／Width／Height，攔掉的話屬性值會和實際位置對不上。
    /// </summary>
    private IntPtr HandleWindowPosChanging(IntPtr hwnd, IntPtr lParam, ref bool handled)
    {
        WINDOWPOS position = Marshal.PtrToStructure<WINDOWPOS>(lParam);

        bool keepPosition = (position.flags & NativeMethods.SWP_NOMOVE) != 0;
        bool keepSize = (position.flags & NativeMethods.SWP_NOSIZE) != 0;

        if ((keepPosition && keepSize) || NativeMethods.IsIconic(hwnd))
        {
            return IntPtr.Zero;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out RECT current))
        {
            return IntPtr.Zero;
        }

        // 訊息帶 SWP_NOMOVE／SWP_NOSIZE 時對應欄位是無效值，要拿目前的狀態補上
        int x = keepPosition ? current.Left : position.x;
        int y = keepPosition ? current.Top : position.y;
        int width = keepSize ? current.Width : position.cx;
        int height = keepSize ? current.Height : position.cy;

        if (width <= 0 || height <= 0)
        {
            return IntPtr.Zero;
        }

        // 用「還沒夾過」的位置去找最近的螢幕，拖過兩台螢幕的中線時才換得過去
        var proposed = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        if (!NativeMethods.TryGetMonitorBounds(proposed, out RECT monitor, out _))
        {
            return IntPtr.Zero;
        }

        // 尺寸一律重算，理由有兩個：一是不能大於螢幕，否則位置怎麼夾都會有一邊露在外面；
        // 二是要擋掉 Aero Snap——把視窗甩到螢幕邊緣時系統會逕自把它拉成半螢幕或四分之一
        // 螢幕，長寬比就毀了。作法是從比例正確的矩形出發，縮進「提議尺寸 ∩ 螢幕」之內。
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double chromeX = ChromeThickness * dpi.DpiScaleX;
        double chromeY = ChromeThickness * dpi.DpiScaleY;

        double contentWidth = width - chromeX;
        double contentHeight = height - chromeY;

        if (_aspectRatio > 0)
        {
            // 一律由寬推高，寬度尊重外面給的值。不能反過來拿「提議的尺寸」當上限去
            // 內縮——WPF 套用 Width／Height 的過程中會送出寬高還沒同時到位的中間訊息，
            // 把比例正確的矩形塞進那種矩形裡會讓視窗一路縮小。
            contentHeight = contentWidth / _aspectRatio;
        }

        FitToLimits(
            ref contentWidth, ref contentHeight,
            (MinEdgeLength * dpi.DpiScaleX) - chromeX,
            (MinEdgeLength * dpi.DpiScaleY) - chromeY,
            monitor.Width - chromeX, monitor.Height - chromeY);

        int fittedWidth = (int)Math.Round(contentWidth + chromeX);
        int fittedHeight = (int)Math.Round(contentHeight + chromeY);

        // 差 1px 以內視為四捨五入誤差。不加這道門檻，縮放過程每一格都會被判定為
        // 「尺寸有變」而反覆改寫，視窗會抖個不停。
        if (Math.Abs(fittedWidth - width) > 1 || Math.Abs(fittedHeight - height) > 1)
        {
            width = fittedWidth;
            height = fittedHeight;
        }

        // 不用 Math.Clamp：尺寸因四捨五入仍略大於螢幕時 min 會超過 max 而拋例外
        x = Math.Max(monitor.Left, Math.Min(x, monitor.Right - width));
        y = Math.Max(monitor.Top, Math.Min(y, monitor.Bottom - height));

        bool moved = x != (keepPosition ? current.Left : position.x) ||
                     y != (keepPosition ? current.Top : position.y);
        bool resized = width != (keepSize ? current.Width : position.cx) ||
                       height != (keepSize ? current.Height : position.cy);

        if (!moved && !resized)
        {
            return IntPtr.Zero;
        }

        position.x = x;
        position.y = y;
        position.cx = width;
        position.cy = height;

        // 改了值就得把對應的「不要動」旗標拿掉，否則系統根本不會採用。
        // 純尺寸變更（ApplyAspectRatio）造成的溢出就是靠這裡順便把位置移回來的。
        if (moved)
        {
            position.flags &= ~NativeMethods.SWP_NOMOVE;
        }

        if (resized)
        {
            position.flags &= ~NativeMethods.SWP_NOSIZE;
        }

        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 在保持長寬比的前提下等比縮放，讓尺寸同時滿足最小與最大限制。
    /// 縮小刻意放在放大之後：螢幕比最小尺寸還小時以螢幕為準。
    /// 單位由呼叫端決定（實體像素或 DIP），只要前後一致即可。
    /// </summary>
    private static void FitToLimits(
        ref double width, ref double height,
        double minWidth, double minHeight,
        double maxWidth, double maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double grow = Math.Max(minWidth / width, minHeight / height);
        if (grow > 1)
        {
            width *= grow;
            height *= grow;
        }

        double shrink = Math.Min(maxWidth / width, maxHeight / height);
        if (shrink < 1)
        {
            width *= shrink;
            height *= shrink;
        }
    }

    /// <summary>
    /// 讓視窗形狀符合目標畫面的長寬比。刻意維持面積不變，
    /// 目標從橫式變成直式時視窗才不會突然變得又細又長。
    /// </summary>
    private void ApplyAspectRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0)
        {
            return;
        }

        // 比例幾乎沒變就不要動視窗，免得每次目標尺寸微調都跳一下
        if (Math.Abs(ratio - _aspectRatio) < 0.001)
        {
            return;
        }

        _aspectRatio = ratio;

        double contentWidth = Width - ChromeThickness;
        double contentHeight = Height - ChromeThickness;

        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

        double area = contentWidth * contentHeight;
        contentWidth = Math.Sqrt(area * ratio);
        contentHeight = contentWidth / ratio;

        double maxWidth = double.PositiveInfinity;
        double maxHeight = double.PositiveInfinity;

        if (_handle != IntPtr.Zero &&
            NativeMethods.GetWindowRect(_handle, out RECT bounds) &&
            NativeMethods.TryGetMonitorBounds(bounds, out RECT monitor, out _))
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            maxWidth = (monitor.Width / dpi.DpiScaleX) - ChromeThickness;
            maxHeight = (monitor.Height / dpi.DpiScaleY) - ChromeThickness;
        }

        FitToLimits(
            ref contentWidth, ref contentHeight,
            MinEdgeLength - ChromeThickness, MinEdgeLength - ChromeThickness,
            maxWidth, maxHeight);

        // 放大後可能超出螢幕右下角，位置由 HandleWindowPosChanging 收尾
        Width = contentWidth + ChromeThickness;
        Height = contentHeight + ChromeThickness;
    }

    /// <summary>
    /// 取得目前所在螢幕的範圍，換算成 WPF 的 DIP 座標。
    /// 位置一律透過 Left／Top 設定而不是 SetWindowPos——WPF 自己也在管這兩個屬性，
    /// 繞過它直接搬 HWND 有時會被 WPF 的版面流程蓋回去。
    /// </summary>
    private bool TryGetMonitorBoundsInDips(out Rect monitorArea, out Rect workArea)
    {
        monitorArea = default;
        workArea = default;

        if (_handle == IntPtr.Zero ||
            !NativeMethods.GetWindowRect(_handle, out RECT bounds) ||
            !NativeMethods.TryGetMonitorBounds(bounds, out RECT monitor, out RECT work))
        {
            return false;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        monitorArea = ToDips(monitor, dpi);
        workArea = ToDips(work, dpi);
        return true;
    }

    private static Rect ToDips(RECT rect, DpiScale dpi)
    {
        return new Rect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            rect.Width / dpi.DpiScaleX,
            rect.Height / dpi.DpiScaleY);
    }

    /// <summary>把視窗移到工作區右下角（WorkArea 已扣除工作列）。</summary>
    public void PositionAtBottomRight()
    {
        const double margin = 16;

        // 取不到螢幕資訊時退回主螢幕工作區
        Rect area = TryGetMonitorBoundsInDips(out _, out Rect work) ? work : SystemParameters.WorkArea;

        Left = area.Right - Width - margin;
        Top = area.Bottom - Height - margin;
    }

    /// <summary>
    /// 把視窗夾回螢幕內。尺寸變大時右下角可能被推出畫面，
    /// 而純尺寸變更的 WM_WINDOWPOSCHANGING 不見得能順便修正位置，所以這裡再收一次尾。
    /// </summary>
    private void ClampPositionToMonitor()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) ||
            !TryGetMonitorBoundsInDips(out Rect monitor, out _))
        {
            return;
        }

        // 不用 Math.Clamp：視窗比螢幕還大時 min 會超過 max 而拋例外
        double x = Math.Max(monitor.Left, Math.Min(Left, monitor.Right - Width));
        double y = Math.Max(monitor.Top, Math.Min(Top, monitor.Bottom - Height));

        if (x != Left)
        {
            Left = x;
        }

        if (y != Top)
        {
            Top = y;
        }
    }

    private void OnFrameCaptured(object? sender, FrameData frame)
    {
        // 事件來自擷取執行緒，直接排到 UI 執行緒畫，不要等下一次輪詢——
        // 那會平白多出 0～60 ms，而且 Background 優先權在 UI 忙碌時還會被壓後。
        // 這裡不碰傳進來的 frame，實際像素在 UI 執行緒重新向 FrameBuffer 取最新一幀，
        // 避免用到已被下一次擷取覆寫的緩衝區。
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.InvokeAsync(RenderLatestFrame, DispatcherPriority.Render);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // 畫面本身由 OnFrameCaptured 事件驅動；這裡只負責跟影格無關的動畫與輪詢
        UpdateOverlay();
        StepAlert();
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

            // 這裡是唯一知道目標視窗尺寸的地方，順便讓視窗形狀跟上
            ApplyAspectRatio(frame.Width / (double)frame.Height);
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

        // 警告狀態必須在下面兩個早退之前就決定好，
        // 否則「結果沒變」或「版面還沒算出來」的那幾格紅框不會更新。
        _alertActive = results.Count > 0;

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
        // 「有沒有命中」由整個視窗的警告紅框表達，這裡只標出「命中在哪裡」。
        // hit.X／hit.Y 是命中區域的左上角，要加半個寬高才是中心。
        double centerX = offsetX + ((hit.X + (hit.Width / 2.0)) * scale);
        double centerY = offsetY + ((hit.Y + (hit.Height / 2.0)) * scale);

        var dot = new Ellipse
        {
            Width = MarkerDotSize,
            Height = MarkerDotSize,
            Fill = MarkerBrush
        };

        Canvas.SetLeft(dot, centerX - (MarkerDotSize / 2));
        Canvas.SetTop(dot, centerY - (MarkerDotSize / 2));
        Overlay.Children.Add(dot);

        // 分數畫在點旁邊。主視窗沒有結果清單，這是唯一能回饋門檻是否合適的地方。
        var label = new TextBlock
        {
            Text = $"{hit.TemplateName} {hit.Score:F2}",
            Foreground = MarkerBrush,
            Background = MarkerLabelBackground,
            FontSize = 11,
            Padding = new Thickness(3, 0, 3, 0)
        };

        Canvas.SetLeft(label, centerX + (MarkerDotSize / 2) + 2);
        Canvas.SetTop(label, centerY - (MarkerLabelHeight / 2));
        Overlay.Children.Add(label);
    }

    private void ClearOverlay()
    {
        if (Overlay.Children.Count > 0)
        {
            Overlay.Children.Clear();
        }

        _overlayResultId = -1;
        _alertActive = false;
    }

    /// <summary>
    /// 命中期間讓警告紅框週期性明暗變化。AlertBorder 掛在 ContentLayer 之外、
    /// Window.Opacity 又固定是 1.0，所以這裡寫進去的值就是最終看到的 alpha，
    /// 不會被使用者設定的視窗不透明度稀釋。
    /// </summary>
    private void StepAlert()
    {
        if (!_alertActive)
        {
            if (AlertBorder.Visibility != Visibility.Collapsed)
            {
                AlertBorder.Visibility = Visibility.Collapsed;
            }

            // 下次命中從最亮開始，第一眼就看得到
            _alertPhase = 0;
            return;
        }

        if (AlertBorder.Visibility != Visibility.Visible)
        {
            AlertBorder.Visibility = Visibility.Visible;
        }

        // cos 波：相位 0 最亮，走到半週期最暗，再平滑回到最亮
        double wave = (1 + Math.Cos(_alertPhase / (double)AlertPulseTicks * 2 * Math.PI)) / 2;
        AlertBorder.Opacity = AlertMinOpacity + ((AlertMaxOpacity - AlertMinOpacity) * wave);

        _alertPhase = (_alertPhase + 1) % AlertPulseTicks;
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
        double current = ContentLayer.Opacity;
        double delta = target - current;

        if (Math.Abs(delta) < 0.005)
        {
            if (current != target)
            {
                ContentLayer.Opacity = target;
            }

            return;
        }

        ContentLayer.Opacity = current + Math.Clamp(delta, -OpacityStep, OpacityStep);
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

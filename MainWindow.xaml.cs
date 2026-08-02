using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowMonitor.Capture;
using WindowMonitor.Interop;
using WindowMonitor.Matching;
using WindowMonitor.Models;

namespace WindowMonitor;

public partial class MainWindow : Window
{
    private static readonly int[] OpacityLevels = [100, 80, 60, 40];
    private static readonly int[] HoverOpacityLevels = [60, 40, 25, 10];

    private readonly GraphicsCaptureSource _source = new();
    private readonly TemplateMatcher _matcher = new();
    private MonitorWindow? _monitor;
    private long _frameCount;

    /// <summary>
    /// 目前擷取對象的顯示名稱。狀態列只有一格主訊息，擷取狀態（「擷取中」「目標視窗已關閉」）
    /// 本身不帶視窗資訊，所以在這裡記著，顯示時接上去。
    /// </summary>
    private string? _targetLabel;

    public MainWindow()
    {
        InitializeComponent();

        _source.StateChanged += OnCaptureStateChanged;
        _source.FrameCaptured += OnFrameCaptured;

        // 比對就掛在擷取事件上，跟著擷取執行緒同步跑
        _matcher.Attach(_source);

        InitializeOpacityOptions();
    }

    private void InitializeOpacityOptions()
    {
        foreach (int level in OpacityLevels)
        {
            OpacityCombo.Items.Add($"{level}%");
        }

        foreach (int level in HoverOpacityLevels)
        {
            HoverOpacityCombo.Items.Add($"{level}%");
        }

        OpacityCombo.SelectedIndex = 0;
        HoverOpacityCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// 狀態列主訊息。錯誤用紅字，下一則正常訊息會自動還原顏色。
    ///
    /// 這一格會截斷（視窗標題經常過長），所以 ToolTip 一律掛完整內容；
    /// details 則用在載入錯誤這種多行、放不進一行的細節。
    /// </summary>
    private void SetStatus(string message, bool isError = false, string? details = null)
    {
        StatusMessageText.Text = message;
        StatusMessageText.Foreground = isError
            ? (Brush)FindResource("ErrorBrush")
            : SystemColors.ControlTextBrush;
        StatusMessageText.ToolTip = details ?? message;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (!GraphicsCaptureSource.IsSupported())
        {
            SetStatus("這台電腦不支援 Windows.Graphics.Capture，無法擷取畫面。", isError: true);
            StartButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            return;
        }

        RefreshWindowList();
        ShowMonitorWindow();
        InitializeTemplates();
    }

    private void ShowMonitorWindow()
    {
        _monitor = new MonitorWindow(_source)
        {
            Owner = null,
            Matcher = _matcher,
            ClickThrough = ClickThroughCheck.IsChecked == true,
            FadeOnHover = FadeOnHoverCheck.IsChecked == true,
            OpacityPercent = OpacityLevels[Math.Max(OpacityCombo.SelectedIndex, 0)],
            HoverOpacityPercent = HoverOpacityLevels[Math.Max(HoverOpacityCombo.SelectedIndex, 0)]
        };

        _monitor.Show();
    }

    private void RefreshWindowList()
    {
        IntPtr self = new WindowInteropHelper(this).Handle;
        IntPtr monitorHandle = _monitor is null
            ? IntPtr.Zero
            : new WindowInteropHelper(_monitor).Handle;

        WindowInfo? previous = WindowList.SelectedItem as WindowInfo;

        List<WindowInfo> windows = WindowEnumerator.Enumerate(self)
            .Where(w => w.Handle != monitorHandle)
            .ToList();

        WindowList.ItemsSource = windows;

        // 盡量保留原本的選擇，避免每次重新整理都要重選
        if (previous is not null)
        {
            WindowList.SelectedItem = windows.FirstOrDefault(w => w.Handle == previous.Handle);
        }

        SetStatus($"找到 {windows.Count} 個可擷取的視窗。");
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void OnWindowListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (WindowList.SelectedItem is WindowInfo)
        {
            StartCapture();
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => StartCapture();

    private void StartCapture()
    {
        if (WindowList.SelectedItem is not WindowInfo target)
        {
            SetStatus("請先從清單選擇一個目標視窗。", isError: true);
            return;
        }

        if (!NativeMethods.IsWindow(target.Handle))
        {
            SetStatus("這個視窗已經關閉了，請重新整理清單。", isError: true);
            return;
        }

        try
        {
            _frameCount = 0;

            // 換了目標視窗，舊畫面的命中位置不能再沿用
            _matcher.ForgetPositions();
            _matcher.ClearResults();

            _targetLabel = target.Display;

            _source.IntervalMilliseconds = ParseInterval();
            _source.Start(target.Handle);

            SetCaptureUiState(true);
        }
        catch (Exception ex)
        {
            _targetLabel = null;

            // Start() 失敗時擷取並沒有跑起來，UI 不能停在「擷取中」的樣子，
            // 否則清單也跟著解不開
            SetCaptureUiState(false);
            SetStatus($"啟動擷取失敗：{ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 擷取中／非擷取中的 UI 狀態。清單一併鎖住——擷取期間選取必須等於擷取目標，
    /// 否則高亮會與狀態列的目標名稱對不起來，停止後再開始也會換到別的視窗。
    /// </summary>
    private void SetCaptureUiState(bool capturing)
    {
        StartButton.IsEnabled = !capturing;
        StopButton.IsEnabled = capturing;
        WindowList.IsEnabled = !capturing;
        RefreshButton.IsEnabled = !capturing;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        // 先清掉，Stop() 觸發的「已停止」才不會又接上視窗名稱
        _targetLabel = null;

        _source.Stop();
        _matcher.ForgetPositions();
        _matcher.ClearResults();

        SetCaptureUiState(false);
    }

    private int ParseInterval()
    {
        if (int.TryParse(IntervalBox.Text, out int value))
        {
            return Math.Clamp(value, 50, 60_000);
        }

        return 1000;
    }

    private void OnIntervalChanged(object sender, RoutedEventArgs e)
    {
        int interval = ParseInterval();
        IntervalBox.Text = interval.ToString();
        _source.IntervalMilliseconds = interval;
    }

    private void OnIntervalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnIntervalChanged(sender, e);
        }
    }

    private void OnCaptureStateChanged(object? sender, CaptureStateEventArgs e)
    {
        // 事件來自擷取執行緒
        Dispatcher.InvokeAsync(() =>
        {
            string? label = _targetLabel;

            if (e.State is CaptureState.TargetClosed or CaptureState.Failed)
            {
                SetCaptureUiState(false);
                _targetLabel = null;

                if (e.State == CaptureState.TargetClosed)
                {
                    RefreshWindowList();
                }
            }

            // 放在重新整理之後：兩者共用狀態列，這則訊息比「找到 N 個視窗」重要
            SetStatus(
                label is null ? e.Message : $"{e.Message} · {label}",
                e.State == CaptureState.Failed);
        });
    }

    private void OnFrameCaptured(object? sender, FrameData frame)
    {
        long count = Interlocked.Increment(ref _frameCount);
        int width = frame.Width;
        int height = frame.Height;
        DateTime timestamp = frame.Timestamp;

        Dispatcher.InvokeAsync(() =>
        {
            FrameInfoText.Text = $"{width} × {height} · {count} 幀 · {timestamp:HH:mm:ss}";
            UpdateMatchStatus();
        });
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null)
        {
            _monitor.ClickThrough = ClickThroughCheck.IsChecked == true;
        }
    }

    private void OnFadeOnHoverChanged(object sender, RoutedEventArgs e)
    {
        if (_monitor is not null)
        {
            _monitor.FadeOnHover = FadeOnHoverCheck.IsChecked == true;
        }
    }

    private void OnOpacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_monitor is not null && OpacityCombo.SelectedIndex >= 0)
        {
            _monitor.OpacityPercent = OpacityLevels[OpacityCombo.SelectedIndex];
        }
    }

    private void OnHoverOpacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_monitor is not null && HoverOpacityCombo.SelectedIndex >= 0)
        {
            _monitor.HoverOpacityPercent = HoverOpacityLevels[HoverOpacityCombo.SelectedIndex];
        }
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _monitor?.PositionAtBottomRight();
    }

    // ── 樣板比對 ────────────────────────────────────────────

    private void InitializeTemplates()
    {
        _matcher.Threshold = ThresholdSlider.Value;
        _matcher.UseLocalSearch = LocalSearchCheck.IsChecked == true;
        _matcher.IsEnabled = MatchEnabledCheck.IsChecked == true;

        ThresholdText.Text = _matcher.Threshold.ToString("F2");

        // 預設資料夾不存在不算錯誤，只是還沒有樣板可用
        if (_matcher.Library.FolderExists)
        {
            _matcher.Library.Reload();
        }

        UpdateTemplateUi();
    }

    private void UpdateTemplateUi()
    {
        TemplateLibrary library = _matcher.Library;

        // 路徑在版面上會被截斷，完整值只能靠 ToolTip 看
        TemplateFolderText.Text = library.FolderPath;
        TemplateFolderText.ToolTip = library.FolderPath;

        TemplateList.ItemsSource = library.Snapshot();

        IReadOnlyList<string> errors = library.LoadErrors;
        if (errors.Count > 0)
        {
            SetStatus(
                $"{errors.Count} 個樣板載入失敗",
                isError: true,
                details: string.Join(Environment.NewLine, errors));
        }

        UpdateMatchStatus();
    }

    private void ReloadTemplates()
    {
        _matcher.Library.Reload();

        // 樣板換了，之前記住的命中位置就不能再用
        _matcher.ForgetPositions();
        _matcher.ClearResults();

        UpdateTemplateUi();
        RematchLatestFrame();
    }

    /// <summary>
    /// 立刻用手上最新的一幀重跑一次比對。
    ///
    /// WGC 只在畫面內容變化時才產生新幀，遊戲暫停或停在選單時可能好幾秒都沒有下一幀。
    /// 少了這一步，使用者調門檻、換樣板、勾選啟用之後會覺得設定「沒有反應」。
    /// </summary>
    private void RematchLatestFrame()
    {
        if (!_matcher.IsEnabled)
        {
            return;
        }

        // 這裡是 UI 執行緒，擷取執行緒可能同時在覆寫 front buffer，所以取複本
        if (_source.Frames.TryCopyLatest(out FrameData frame))
        {
            _matcher.MatchFrame(frame);
        }

        UpdateMatchStatus();
    }

    private void OnReloadTemplatesClick(object sender, RoutedEventArgs e) => ReloadTemplates();

    private void OnChooseTemplateFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇樣板資料夾"
        };

        if (_matcher.Library.FolderExists)
        {
            dialog.InitialDirectory = _matcher.Library.FolderPath;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _matcher.Library.SetFolder(dialog.FolderName);
        ReloadTemplates();
    }

    private void OnOpenTemplateFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _matcher.Library.EnsureFolder();

            Process.Start(new ProcessStartInfo
            {
                FileName = _matcher.Library.FolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"無法開啟資料夾：{ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 比對耗時是判斷「要不要調大擷取間隔」的依據，所以每幀都更新。
    /// 比對在擷取執行緒上跑，耗時逼近間隔就代表擷取頻率已經被拖慢了。
    ///
    /// 這裡寫的是狀態列最右格，一行放得下才行，較長的說明一律移到 ToolTip。
    /// </summary>
    private void UpdateMatchStatus()
    {
        int count = _matcher.Library.Count;

        if (count == 0)
        {
            MatchInfoText.Text = "尚無樣板";
            MatchInfoText.ToolTip = "把 PNG 放進樣板資料夾後按「重新載入」。";
            return;
        }

        if (!_matcher.IsEnabled)
        {
            MatchInfoText.Text = $"{count} 樣板（未啟用）";
            MatchInfoText.ToolTip = null;
            return;
        }

        if (_matcher.LastError is not null)
        {
            MatchInfoText.Text = $"{count} 樣板 · 比對錯誤";
            MatchInfoText.ToolTip = _matcher.LastError;
            return;
        }

        MatchInfoText.Text = $"{count} 樣板 · {_matcher.LastElapsedMilliseconds:F1} ms" +
                             $"（局部 {_matcher.LastLocalSearchCount}／全圖 {_matcher.LastFullSearchCount}）";
        MatchInfoText.ToolTip = null;
    }

    private void OnMatchEnabledChanged(object sender, RoutedEventArgs e)
    {
        _matcher.IsEnabled = MatchEnabledCheck.IsChecked == true;

        if (_matcher.IsEnabled)
        {
            RematchLatestFrame();
            return;
        }

        _matcher.ForgetPositions();
        _matcher.ClearResults();
        UpdateMatchStatus();
    }

    private void OnLocalSearchChanged(object sender, RoutedEventArgs e)
    {
        _matcher.UseLocalSearch = LocalSearchCheck.IsChecked == true;
        RematchLatestFrame();
    }

    private void OnThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _matcher.Threshold = e.NewValue;

        // 這個事件在 InitializeComponent 期間就會觸發，那時控制項還沒建好
        if (ThresholdText is null)
        {
            return;
        }

        ThresholdText.Text = e.NewValue.ToString("F2");
        RematchLatestFrame();
    }

    /// <summary>
    /// 把最新一幀存成 PNG。除了驗證擷取到的像素是否正確之外，
    /// 之後也可以用來裁切 template matching 要用的樣板。
    /// </summary>
    private void OnSaveFrameClick(object sender, RoutedEventArgs e)
    {
        if (!_source.Frames.TryCopyLatest(out FrameData frame))
        {
            SetStatus("還沒有可儲存的畫面。", isError: true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG 影像|*.png",
            FileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BitmapSource bitmap = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                frame.Pixels,
                frame.Stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);

            SetStatus($"已儲存：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"儲存失敗：{ex.Message}", isError: true);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _source.StateChanged -= OnCaptureStateChanged;
        _source.FrameCaptured -= OnFrameCaptured;

        // 先關監控視窗再釋放擷取資源，避免它還在讀已釋放的緩衝
        _monitor?.Close();
        _monitor = null;

        // 擷取先停下來，才不會有執行緒還在碰比對用的 Mat
        _source.Dispose();
        _matcher.Dispose();

        base.OnClosed(e);
    }
}

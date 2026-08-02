using System.ComponentModel;
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
using WindowMonitor.Notifications;
using WindowMonitor.Settings;

namespace WindowMonitor;

public partial class MainWindow : Window
{
    private static readonly int[] OpacityLevels = [100, 80, 60, 40];
    private static readonly int[] HoverOpacityLevels = [60, 40, 25, 10];

    private const int MinIntervalMilliseconds = 50;
    private const int MaxIntervalMilliseconds = 60_000;
    private const int DefaultIntervalMilliseconds = 1000;

    private const int MinDwellSeconds = 0;
    private const int MaxDwellSeconds = 3600;
    private const int DefaultDwellSeconds = 5;

    /// <summary>
    /// 冷卻下限刻意是 1 秒而不是 0：0 的話「已連續命中滿 dwell」在之後的每一幀都成立，
    /// 會變成每個擷取間隔送一則。
    /// </summary>
    private const int MinCooldownSeconds = 1;
    private const int MaxCooldownSeconds = 86_400;
    private const int DefaultCooldownSeconds = 300;

    private readonly GraphicsCaptureSource _source = new();
    private readonly TemplateMatcher _matcher = new();
    private readonly MatchNotifier _notifier = new();
    private readonly AppSettings _settings = SettingsStore.Load();
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

        // 通知只吃真正的擷取幀，UI 觸發的重比對不會餵進去（見 TemplateMatcher.MatchCycleCompleted）
        _notifier.Attach(_matcher);
        _notifier.StatusChanged += OnNotifierStatusChanged;

        InitializeOpacityOptions();

        // 必須在 InitializeOpacityOptions 之後：兩個下拉選單的項目要先填好才選得了檔位
        ApplySettings();
    }

    /// <summary>
    /// 把設定檔的內容寫回控制項。這裡刻意只碰控制項，不直接推給 _source／_matcher／_monitor——
    /// OnSourceInitialized 之後的 ShowMonitorWindow() 與 InitializeTemplates() 本來就會
    /// 從控制項讀值去初始化那三者，重複一次只會多一份會走樣的邏輯。
    ///
    /// 設定值一律夾回控制項本身的合法範圍，設定檔被手改壞也不會讓 UI 進到不合理的狀態。
    /// </summary>
    private void ApplySettings()
    {
        int interval = Math.Clamp(
            _settings.IntervalMilliseconds,
            MinIntervalMilliseconds,
            MaxIntervalMilliseconds);

        IntervalBox.Text = interval.ToString();
        _source.IntervalMilliseconds = interval;

        // 樣板資料夾沒有對應的控制項，資料本身就在 Library 上
        if (!string.IsNullOrWhiteSpace(_settings.TemplateFolder))
        {
            _matcher.Library.SetFolder(_settings.TemplateFolder);
        }

        MatchEnabledCheck.IsChecked = _settings.MatchEnabled;
        LocalSearchCheck.IsChecked = _settings.UseLocalSearch;
        ThresholdSlider.Value = Math.Clamp(
            _settings.Threshold,
            ThresholdSlider.Minimum,
            ThresholdSlider.Maximum);

        ClickThroughCheck.IsChecked = _settings.ClickThrough;
        FadeOnHoverCheck.IsChecked = _settings.FadeOnHover;

        SelectOpacityLevel(OpacityCombo, OpacityLevels, _settings.OpacityPercent);
        SelectOpacityLevel(HoverOpacityCombo, HoverOpacityLevels, _settings.HoverOpacityPercent);

        NotifyEnabledCheck.IsChecked = _settings.NotifyEnabled;
        WebhookUrlBox.Text = _settings.DiscordWebhookUrl ?? string.Empty;
        DiscordUserIdBox.Text = _settings.DiscordUserId ?? string.Empty;

        // 訊息被清空過的話退回預設值，否則下次開啟會拿到一則空訊息
        MessageTemplateBox.Text = string.IsNullOrWhiteSpace(_settings.NotifyMessageTemplate)
            ? AppSettings.DefaultMessageTemplate
            : _settings.NotifyMessageTemplate;

        DwellSecondsBox.Text = Math
            .Clamp(_settings.NotifyDwellSeconds, MinDwellSeconds, MaxDwellSeconds)
            .ToString();

        CooldownSecondsBox.Text = Math
            .Clamp(_settings.NotifyCooldownSeconds, MinCooldownSeconds, MaxCooldownSeconds)
            .ToString();
    }

    /// <summary>設定檔存的是百分比值，找不到對應檔位（陣列改過或值被改壞）就維持原選擇。</summary>
    private static void SelectOpacityLevel(ComboBox combo, int[] levels, int percent)
    {
        int index = Array.IndexOf(levels, percent);

        if (index >= 0)
        {
            combo.SelectedIndex = index;
        }
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
        ApplyNotificationOptions();
    }

    private void ShowMonitorWindow()
    {
        _monitor = new MonitorWindow(_source)
        {
            Owner = null,
            Matcher = _matcher,
            RestorePlacement = _settings.MonitorPlacement,
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
            _notifier.ResetDwell();

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
        _notifier.ResetDwell();

        SetCaptureUiState(false);
    }

    private int ParseInterval()
    {
        if (int.TryParse(IntervalBox.Text, out int value))
        {
            return Math.Clamp(value, MinIntervalMilliseconds, MaxIntervalMilliseconds);
        }

        return DefaultIntervalMilliseconds;
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

                // 這條路徑不經過 OnStopClick，連續命中的計時要自己清
                _notifier.ResetDwell();

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

        // 顯示影格年齡而非取出時刻：取出時刻永遠看起來是新的，
        // 擷取管線積壓多久完全看不出來，而那才是延遲的來源。
        long ageMilliseconds = (long)frame.CaptureAge.TotalMilliseconds;

        Dispatcher.InvokeAsync(() =>
        {
            FrameInfoText.Text = $"{width} × {height} · {count} 幀 · 延遲 {ageMilliseconds} ms";
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

        // 同名樣板也可能換成完全不同的圖，之前那段連續命中不算數
        _notifier.ResetDwell();

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

        // 比對停過一段期間，那些幀完全沒有觀測值，不能接著上次的計時算
        _notifier.ResetDwell();

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

        // 判定方式變了，之前那段連續命中不再可比
        _notifier.ResetDwell();
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

        // 門檻變了，之前那段連續命中不再可比
        _notifier.ResetDwell();
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

    /// <summary>
    /// 存檔放在這裡而不是 OnClosed：後者第一件事就是關掉監控視窗，那之後讀不到它的位置。
    /// 而且此時視窗還在，存檔失敗才有地方報。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel)
        {
            return;
        }

        CollectSettings();

        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 安裝在唯讀目錄（例如 Program Files）時會寫不進去。安靜失敗會讓人以為
            // 記住設定的功能壞了，所以寧可跳一次警告——但不擋住關閉。
            MessageBox.Show(
                this,
                $"設定無法儲存：{ex.Message}{Environment.NewLine}{SettingsStore.FilePath}",
                "WindowMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CollectSettings()
    {
        _settings.IntervalMilliseconds = ParseInterval();

        // 預設資料夾不寫進設定檔：它是從執行檔位置算出來的，存成絕對路徑的話
        // 整個資料夾搬家之後就會指回舊位置。
        _settings.TemplateFolder = _matcher.Library.IsDefaultFolder
            ? null
            : _matcher.Library.FolderPath;

        _settings.MatchEnabled = MatchEnabledCheck.IsChecked == true;
        _settings.UseLocalSearch = LocalSearchCheck.IsChecked == true;
        _settings.Threshold = ThresholdSlider.Value;

        _settings.ClickThrough = ClickThroughCheck.IsChecked == true;
        _settings.FadeOnHover = FadeOnHoverCheck.IsChecked == true;
        _settings.OpacityPercent = OpacityLevels[Math.Max(OpacityCombo.SelectedIndex, 0)];
        _settings.HoverOpacityPercent = HoverOpacityLevels[Math.Max(HoverOpacityCombo.SelectedIndex, 0)];

        _settings.NotifyEnabled = NotifyEnabledCheck.IsChecked == true;

        // 空字串一律存成 null，與 TemplateFolder 的慣例一致（「沒設定」而不是「設定成空的」）
        string webhookUrl = WebhookUrlBox.Text.Trim();
        _settings.DiscordWebhookUrl = webhookUrl.Length == 0 ? null : webhookUrl;

        string userId = DiscordUserIdBox.Text.Trim();
        _settings.DiscordUserId = userId.Length == 0 ? null : userId;

        _settings.NotifyMessageTemplate = string.IsNullOrWhiteSpace(MessageTemplateBox.Text)
            ? AppSettings.DefaultMessageTemplate
            : MessageTemplateBox.Text;

        _settings.NotifyDwellSeconds = ParseDwellSeconds();
        _settings.NotifyCooldownSeconds = ParseCooldownSeconds();

        // 監控視窗沒開起來過（例如這台電腦不支援擷取）就保留載入時的記錄，不要清掉
        if (_monitor is not null)
        {
            _settings.MonitorPlacement = _monitor.GetPlacement();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _source.StateChanged -= OnCaptureStateChanged;
        _source.FrameCaptured -= OnFrameCaptured;
        _notifier.StatusChanged -= OnNotifierStatusChanged;

        // 先關監控視窗再釋放擷取資源，避免它還在讀已釋放的緩衝
        _monitor?.Close();
        _monitor = null;

        // 擷取先停下來，才不會有執行緒還在碰比對用的 Mat；
        // 停了之後也就不會再有 MatchCycleCompleted，通知才拆得乾淨
        _source.Dispose();
        _notifier.Dispose();
        _matcher.Dispose();

        base.OnClosed(e);
    }

    // ── 通知 ────────────────────────────────────────────────

    /// <summary>
    /// 把通知欄位的內容整包推給 <see cref="MatchNotifier"/>。
    /// 設定是不可變的快照，所以一律整包換掉，不做逐欄位更新。
    /// </summary>
    private void ApplyNotificationOptions()
    {
        _notifier.Options = new NotificationOptions(
            Enabled: NotifyEnabledCheck.IsChecked == true,
            WebhookUrl: WebhookUrlBox.Text.Trim(),
            UserId: DiscordUserIdBox.Text.Trim(),
            MessageTemplate: MessageTemplateBox.Text,
            Dwell: TimeSpan.FromSeconds(ParseDwellSeconds()),
            Cooldown: TimeSpan.FromSeconds(ParseCooldownSeconds()));
    }

    private int ParseDwellSeconds()
    {
        return ParseSeconds(DwellSecondsBox, MinDwellSeconds, MaxDwellSeconds, DefaultDwellSeconds);
    }

    private int ParseCooldownSeconds()
    {
        return ParseSeconds(
            CooldownSecondsBox,
            MinCooldownSeconds,
            MaxCooldownSeconds,
            DefaultCooldownSeconds);
    }

    /// <summary>
    /// 讀一個秒數欄位並夾回合法範圍，順便把夾過的值寫回去——使用者要看得到
    /// 自己打的 99999 其實被當成什麼用。比照 <see cref="ParseInterval"/> 的作法。
    /// </summary>
    private static int ParseSeconds(TextBox box, int minimum, int maximum, int fallback)
    {
        int value = int.TryParse(box.Text, out int parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

        string text = value.ToString();

        if (box.Text != text)
        {
            box.Text = text;
        }

        return value;
    }

    private void OnNotifyOptionChanged(object sender, RoutedEventArgs e) => ApplyNotificationOptions();

    private void OnNotifyNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyNotificationOptions();
        }
    }

    private void OnTestNotificationClick(object sender, RoutedEventArgs e)
    {
        // 先套用：使用者很可能剛貼完網址就直接按測試，那個欄位還沒失焦
        ApplyNotificationOptions();
        _notifier.SendTest();
    }

    /// <summary>通知的訊息來自送出執行緒，與 OnCaptureStateChanged 一樣要跳回 UI。</summary>
    private void OnNotifierStatusChanged(object? sender, NotificationStatusEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.InvokeAsync(() => SetStatus(e.Message, e.IsError, e.Details));
    }
}

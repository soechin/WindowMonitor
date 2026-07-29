using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WindowMonitor.Capture;
using WindowMonitor.Interop;
using WindowMonitor.Models;

namespace WindowMonitor;

public partial class MainWindow : Window
{
    private static readonly int[] OpacityLevels = [100, 80, 60, 40];
    private static readonly int[] HoverOpacityLevels = [60, 40, 25, 10];

    private readonly GraphicsCaptureSource _source = new();
    private MonitorWindow? _monitor;
    private long _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        _source.StateChanged += OnCaptureStateChanged;
        _source.FrameCaptured += OnFrameCaptured;

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (!GraphicsCaptureSource.IsSupported())
        {
            StatusText.Text = "這台電腦不支援 Windows.Graphics.Capture，無法擷取畫面。";
            StartButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            return;
        }

        RefreshWindowList();
        ShowMonitorWindow();
    }

    private void ShowMonitorWindow()
    {
        _monitor = new MonitorWindow(_source)
        {
            Owner = null,
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

        FooterText.Text = $"找到 {windows.Count} 個可擷取的視窗。";
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
            StatusText.Text = "請先從清單選擇一個目標視窗。";
            return;
        }

        if (!NativeMethods.IsWindow(target.Handle))
        {
            StatusText.Text = "這個視窗已經關閉了，請重新整理清單。";
            return;
        }

        try
        {
            _frameCount = 0;
            _source.IntervalMilliseconds = ParseInterval();
            _source.Start(target.Handle);

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            FooterText.Text = $"正在擷取：{target.Display}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"啟動擷取失敗：{ex.Message}";
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _source.Stop();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
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
            StatusText.Text = e.Message;

            if (e.State is CaptureState.TargetClosed or CaptureState.Failed)
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;

                if (e.State == CaptureState.TargetClosed)
                {
                    RefreshWindowList();
                }
            }
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
            FrameInfoText.Text = $"{width} × {height}　已擷取 {count} 幀　最後更新 {timestamp:HH:mm:ss}";
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

    /// <summary>
    /// 把最新一幀存成 PNG。除了驗證擷取到的像素是否正確之外，
    /// 之後也可以用來裁切 template matching 要用的樣板。
    /// </summary>
    private void OnSaveFrameClick(object sender, RoutedEventArgs e)
    {
        if (!_source.Frames.TryCopyLatest(out FrameData frame))
        {
            StatusText.Text = "還沒有可儲存的畫面。";
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

            StatusText.Text = $"已儲存：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"儲存失敗：{ex.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _source.StateChanged -= OnCaptureStateChanged;
        _source.FrameCaptured -= OnFrameCaptured;

        // 先關監控視窗再釋放擷取資源，避免它還在讀已釋放的緩衝
        _monitor?.Close();
        _monitor = null;

        _source.Dispose();

        base.OnClosed(e);
    }
}

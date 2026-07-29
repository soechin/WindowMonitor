namespace WindowMonitor.Capture;

public enum CaptureState
{
    Stopped,
    Running,
    /// <summary>目標存在但沒有產生新畫面（通常是視窗被最小化）。</summary>
    NotPresenting,
    TargetClosed,
    Failed
}

public sealed class CaptureStateEventArgs(CaptureState state, string message) : EventArgs
{
    public CaptureState State { get; } = state;

    public string Message { get; } = message;
}

/// <summary>
/// 畫面擷取來源的抽象。UI 層只依賴這個介面，日後要替換或新增後端不必動到 UI。
/// </summary>
public interface IFrameSource : IDisposable
{
    bool IsRunning { get; }

    /// <summary>最新一幀的緩衝，供顯示與之後的 template matching 使用。</summary>
    FrameBuffer Frames { get; }

    /// <summary>擷取間隔（毫秒）。可在執行中調整。</summary>
    int IntervalMilliseconds { get; set; }

    event EventHandler<FrameData>? FrameCaptured;

    event EventHandler<CaptureStateEventArgs>? StateChanged;

    void Start(IntPtr targetWindow);

    void Stop();
}

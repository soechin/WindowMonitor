namespace WindowMonitor.Matching;

/// <summary>
/// 一次命中的結果。座標是**畫面的像素座標**（與 <see cref="Capture.FrameData"/> 同一個座標系），
/// 不是監控視窗的 UI 座標——換算成 UI 座標是顯示端的責任。
/// </summary>
/// <param name="FrameId">來源幀的編號，讓消費端能判斷是不是同一批結果。</param>
public sealed record MatchResult(
    string TemplateName,
    int X,
    int Y,
    int Width,
    int Height,
    double Score,
    long FrameId);

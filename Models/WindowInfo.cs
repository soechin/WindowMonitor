namespace WindowMonitor.Models;

/// <summary>
/// 一個可作為擷取目標的候選視窗。
/// </summary>
public sealed class WindowInfo
{
    public required IntPtr Handle { get; init; }

    public required string Title { get; init; }

    public required string ProcessName { get; init; }

    public string Display => string.IsNullOrEmpty(ProcessName)
        ? Title
        : $"{ProcessName} — {Title}";

    public override string ToString() => Display;
}

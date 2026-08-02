using WindowMonitor.Settings;

namespace WindowMonitor.Notifications;

/// <summary>
/// 通知設定的不可變快照。由 UI 執行緒建立、擷取執行緒讀取，所以刻意做成 record：
/// 換設定就是整個換掉一個參考，不會讀到半新半舊的狀態。
/// 用 record 還有一個實際好處——值相等讓 <see cref="MatchNotifier.Options"/>
/// 能判斷「這次真的改了嗎」，沒改就不必重設連續命中的計時。
/// </summary>
public sealed record NotificationOptions(
    bool Enabled,
    string WebhookUrl,
    string UserId,
    string MessageTemplate,
    TimeSpan Dwell,
    TimeSpan Cooldown)
{
    public static NotificationOptions Disabled { get; } = new(
        Enabled: false,
        WebhookUrl: string.Empty,
        UserId: string.Empty,
        MessageTemplate: AppSettings.DefaultMessageTemplate,
        Dwell: TimeSpan.FromSeconds(5),
        Cooldown: TimeSpan.FromSeconds(300));
}

/// <summary>
/// 通知子系統回報給狀態列的訊息。欄位刻意對齊 MainWindow.SetStatus 的三個參數。
/// </summary>
public sealed class NotificationStatusEventArgs(string message, bool isError, string? details = null)
    : EventArgs
{
    public string Message { get; } = message;

    public bool IsError { get; } = isError;

    public string? Details { get; } = details;
}

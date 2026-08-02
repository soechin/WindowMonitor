using System.Globalization;
using System.Text;
using WindowMonitor.Matching;

namespace WindowMonitor.Notifications;

/// <summary>
/// 訊息樣板的代碼展開。純函式，不碰網路也不碰狀態。
/// </summary>
public static class MessageTemplate
{
    /// <summary>Discord 一則訊息的 content 上限。</summary>
    private const int MaxContentLength = 2000;

    /// <summary>
    /// 展開訊息樣板。可用代碼：
    /// {template} 樣板名稱／{score} 相似度／{x} {y} 命中區域左上角的畫面座標／
    /// {time} 時間（HH:mm:ss）／{mention} 展開成 &lt;@使用者ID&gt;，ID 未填時為空字串。
    ///
    /// 認不得的 {xxx} 原樣留著——使用者打錯字時看得到自己打了什麼，比靜默吃掉好。
    /// 也刻意不對展開值做 Markdown 逃脫：樣板檔名常含底線，Discord 會把 _foo_ 轉成斜體，
    /// 需要原樣顯示時由使用者自己在訊息裡加反引號（ToolTip 有寫）。
    /// </summary>
    public static string Format(string template, MatchResult hit, string userId, DateTime now)
    {
        // 相似度與座標一律用 InvariantCulture：這串是給 Discord 的，不是給本地格式的。
        // 某些地區設定會把小數點寫成逗號，讀起來會像兩個數字。
        StringBuilder builder = new(template);

        builder.Replace("{template}", hit.TemplateName);
        builder.Replace("{score}", hit.Score.ToString("F2", CultureInfo.InvariantCulture));
        builder.Replace("{x}", hit.X.ToString(CultureInfo.InvariantCulture));
        builder.Replace("{y}", hit.Y.ToString(CultureInfo.InvariantCulture));
        builder.Replace("{time}", now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        builder.Replace("{mention}", FormatMention(userId));

        return builder.ToString();
    }

    /// <summary>使用者 ID 沒填就展開成空字串，不留下 {mention} 這種看不懂的殘留。</summary>
    public static string FormatMention(string userId)
    {
        return string.IsNullOrWhiteSpace(userId) ? string.Empty : $"<@{userId.Trim()}>";
    }

    /// <summary>
    /// 砍到 Discord 的長度上限。超長會被整則退回（HTTP 400），寧可截斷也要送到。
    /// 切點刻意避開代理對，否則末端會多出一個無效的半個字元。
    /// </summary>
    public static string Truncate(string content)
    {
        if (content.Length <= MaxContentLength)
        {
            return content;
        }

        int length = MaxContentLength;

        if (char.IsHighSurrogate(content[length - 1]))
        {
            length--;
        }

        return content[..length];
    }
}

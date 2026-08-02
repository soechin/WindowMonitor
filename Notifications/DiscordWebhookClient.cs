using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowMonitor.Notifications;

/// <summary>
/// 一次送出的結果。<paramref name="RetryAfter"/> 有值代表撞到速率限制，
/// 等這麼久之後可以重試——那不算失敗，所以與 <paramref name="Error"/> 分開表示。
/// </summary>
public readonly record struct SendOutcome(bool Success, string? Error, TimeSpan? RetryAfter)
{
    public static SendOutcome Ok { get; } = new(true, null, null);

    public static SendOutcome Failed(string error) => new(false, error, null);

    public static SendOutcome Throttled(TimeSpan retryAfter) => new(false, null, retryAfter);
}

/// <summary>
/// Discord Webhook 的最小客戶端。只做一件事：把一段文字 POST 到 webhook 網址。
/// 沒有狀態，所以整個程式共用一個 static <see cref="HttpClient"/>。
/// </summary>
public static class DiscordWebhookClient
{
    /// <summary>
    /// 單次請求的上限。網路不通時不該讓送出佇列卡住太久，也不該拖長關閉流程。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>速率限制最多等這麼久，超過就當作這次送不出去。</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 全程式共用且不釋放。HttpClient 的正確用法是長壽命共用：每次 new 一個會把
    /// TCP 連線留在 TIME_WAIT，量大時耗盡連接埠。這裡只打 discord.com 單一主機，
    /// 沒有 DNS 輪替的疑慮，所以不必動用 IHttpClientFactory（本專案也沒有 DI）。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] DiscordDomains = ["discord.com", "discordapp.com"];

    private static HttpClient CreateClient()
    {
        HttpClient http = new() { Timeout = RequestTimeout };

        // Discord 要求所有 API 請求都帶 User-Agent，缺了會被擋在前面的 Cloudflare。
        // HttpClient 預設一個都不帶，所以這行是必要的、不是禮貌性質。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowMonitor/1.0 (+local desktop utility)");

        return http;
    }

    /// <summary>
    /// 網址長得像不像 Discord webhook。擋掉最常見的設定錯誤（貼成頻道連結或邀請連結），
    /// 同時也是一道安全閥：設定檔被改壞時，程式不會把訊息（含使用者 ID）POST 到任意主機。
    /// </summary>
    public static bool IsValidWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !IsDiscordHost(uri.Host))
        {
            return false;
        }

        // 兩種寫法都要接受：/api/webhooks/... 與帶版本的 /api/v10/webhooks/...
        return uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.Contains("/webhooks/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>discord.com、discordapp.com，以及 ptb.／canary. 這類子網域。</summary>
    private static bool IsDiscordHost(string host)
    {
        foreach (string domain in DiscordDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 送出一則訊息。不丟例外——呼叫端是背景送出迴圈，例外只會變成沒人看得到的
    /// unobserved task，所以一律轉成 <see cref="SendOutcome"/> 帶回去。
    /// </summary>
    public static async Task<SendOutcome> SendAsync(
        string webhookUrl,
        string content,
        CancellationToken token)
    {
        // 用預設的 encoder：中文會跳脫成 \uXXXX，那是合法 JSON，Discord 解得開。
        // 這不是給人讀的設定檔，不需要 SettingsStore 那邊的 UnsafeRelaxedJsonEscaping。
        string json = JsonSerializer.Serialize(
            new Payload(MessageTemplate.Truncate(content), new AllowedMentions(["users"])));

        try
        {
            using StringContent body = new(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PostAsync(webhookUrl, body, token);

            if (response.IsSuccessStatusCode)
            {
                return SendOutcome.Ok;
            }

            return await DescribeFailureAsync(response, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 關閉程式，不是錯誤
            return SendOutcome.Ok;
        }
        catch (TaskCanceledException)
        {
            // token 沒被取消卻取消了，那就是 HttpClient.Timeout
            return SendOutcome.Failed("送出通知逾時，請檢查網路連線。");
        }
        catch (HttpRequestException ex)
        {
            return SendOutcome.Failed($"無法連上 Discord：{ex.Message}");
        }
    }

    private static async Task<SendOutcome> DescribeFailureAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        // 錯誤訊息裡永遠不放 webhook 網址——那是憑證，狀態列與 ToolTip 都看得到。
        string payload = await ReadSnippetAsync(response, token);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return SendOutcome.Throttled(ParseRetryAfter(response, payload));
        }

        int code = (int)response.StatusCode;

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                SendOutcome.Failed($"Webhook 網址無效或已被刪除（HTTP {code}）。"),
            _ => SendOutcome.Failed($"Discord 回應 HTTP {code}：{payload}")
        };
    }

    private static async Task<string> ReadSnippetAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(token);
            return text.Length > 200 ? text[..200] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // 連錯誤內容都讀不到時，狀態碼本身已經足夠報給使用者了
            return string.Empty;
        }
    }

    /// <summary>
    /// 429 要等多久。retry_after 自 API v8 起是「秒」的浮點數；讀不到就退回
    /// Retry-After 標頭（整數秒），再讀不到就給一個保守的預設值。
    /// </summary>
    private static TimeSpan ParseRetryAfter(HttpResponseMessage response, string payload)
    {
        double seconds = 2;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("retry_after", out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetDouble(out double value))
            {
                seconds = value;
            }
            else if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            {
                seconds = delta.TotalSeconds;
            }
        }
        catch (JsonException)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            {
                seconds = delta.TotalSeconds;
            }
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxRetryAfter.TotalSeconds));
    }

    private sealed record Payload(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("allowed_mentions")] AllowedMentions AllowedMentions);

    /// <summary>
    /// parse 與 users／roles 互斥，兩個一起給 Discord 會回 400。
    /// 這裡選 parse:["users"]：訊息樣板是使用者自由編輯的，裡面可能不只一個 &lt;@id&gt;，
    /// 用 parse 就一律照 content 寫的 ping；同時沒列進 parse 的
    /// @everyone／@here／身分組一律不會觸發通知，等於順手上了保險。
    /// </summary>
    private sealed record AllowedMentions(
        [property: JsonPropertyName("parse")] string[] Parse);
}

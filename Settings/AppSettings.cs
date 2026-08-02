using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowMonitor.Settings;

/// <summary>監控視窗的位置與大小（DIP）。</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// 設定檔是純文字，使用者改壞（或存檔當下視窗還沒定位而寫出 NaN）都有可能，
    /// 套用之前一律先問過這裡。位置超出螢幕不算壞值，由視窗自己夾回來。
    /// </summary>
    [JsonIgnore]
    public bool IsUsable =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;
}

/// <summary>
/// 使用者設定。
///
/// 每個屬性的預設值都必須與 XAML 上的初始值一致——設定檔不存在、或是舊版設定檔
/// 缺了某個欄位時，走的就是這裡的預設值。
/// </summary>
public sealed class AppSettings
{
    public int IntervalMilliseconds { get; set; } = 1000;

    /// <summary>null 代表沿用 <see cref="Matching.TemplateLibrary"/> 的預設資料夾。</summary>
    public string? TemplateFolder { get; set; }

    public bool MatchEnabled { get; set; }
    public bool UseLocalSearch { get; set; } = true;
    public double Threshold { get; set; } = 0.80;

    public bool ClickThrough { get; set; }
    public bool FadeOnHover { get; set; } = true;

    /// <summary>
    /// 存百分比值而不是下拉選單的索引：日後增減檔位時，舊設定檔才不會對應到別的值。
    /// </summary>
    public int OpacityPercent { get; set; } = 100;

    public int HoverOpacityPercent { get; set; } = 60;

    /// <summary>
    /// 通知訊息的預設樣板。做成常數是因為 XAML 那邊要用 x:Static 綁同一份——
    /// 這是所有設定裡唯一的長字串，兩邊各抄一份而抄錯的症狀是
    /// 「使用者改了訊息，下次開啟卻被悄悄改回預設」，非常難察覺。
    /// </summary>
    public const string DefaultMessageTemplate = "{template} 出現了！{mention}";

    public bool NotifyEnabled { get; set; }

    /// <summary>
    /// Discord Webhook 網址。null 代表尚未設定。
    ///
    /// 這是以純文字保存的憑證：拿到 settings.json 的人都能以這個 webhook 的身分
    /// 往該頻道發訊息（但讀不到頻道內容，也做不了別的事）。不加密的理由是
    /// .NET 上的 DPAPI（ProtectedData）要多裝一個 NuGet 套件，而且程式若能無條件
    /// 解開金鑰，同一台機器上以相同身分執行的任何程式也解得開——換來的是
    /// 「看起來安全」。外洩的處置成本很低：到 Discord 刪掉該 webhook 重建即可。
    /// UI 上會明講這件事。
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }

    /// <summary>要被 @ 的 Discord 使用者 ID。null 代表 {mention} 展開成空字串。</summary>
    public string? DiscordUserId { get; set; }

    public string NotifyMessageTemplate { get; set; } = DefaultMessageTemplate;

    /// <summary>樣板必須連續命中這麼久才通知。</summary>
    public int NotifyDwellSeconds { get; set; } = 5;

    /// <summary>
    /// 同一個樣板兩次通知之間的最短間隔。一段連續命中本來就只通知一次，
    /// 這個值擋的是目標反覆消失又出現造成的洗版。
    /// </summary>
    public int NotifyCooldownSeconds { get; set; } = 300;

    /// <summary>null 代表沒有記錄，監控視窗開在右下角。</summary>
    public WindowPlacement? MonitorPlacement { get; set; }
}

/// <summary>
/// 設定檔的讀寫。放在執行檔旁，與樣板資料夾同一套慣例，整個資料夾複製走就能保留設定。
/// </summary>
public static class SettingsStore
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // 設定檔要能用記事本讀改，而樣板資料夾路徑經常含中文；
        // 預設的 encoder 會把非 ASCII 一律跳脫成 \uXXXX，路徑就變成看不懂的一串。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 載入設定。檔案不存在、內容壞掉、讀取失敗一律退回預設值——
    /// 設定只是便利功能，不該擋住程式啟動。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>寫入設定。例外往外丟，由呼叫端決定怎麼告訴使用者。</summary>
    public static void Save(AppSettings settings)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}

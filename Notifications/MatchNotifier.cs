using System.Diagnostics;
using System.Threading.Channels;
using WindowMonitor.Matching;

namespace WindowMonitor.Notifications;

/// <summary>
/// 樣板命中時通知 Discord。
///
/// 規則是「上升緣 + 連續停留 + 一次一則 + 冷卻」：某個樣板從沒命中變成命中之後，
/// 必須連續命中滿 <see cref="NotificationOptions.Dwell"/> 才送出，中間只要有一次
/// 取樣落空就重新計時。這是為了「人有時就坐在螢幕前」——短暫出現不需要打擾。
///
/// 一段連續命中只送一則，目標留在畫面上再久都不會再叫。
/// <see cref="NotificationOptions.Cooldown"/> 管的是另一件事：目標消失又出現時，
/// 那在規則上是全新的一段，會重新通知——冷卻就是用來擋掉這種反覆進出的洗版。
///
/// 判定跑在擷取執行緒上（<see cref="TemplateMatcher.MatchCycleCompleted"/>），
/// 所以那條路徑上只有一次字典掃描，真正的送出丟給背景的 pump。
/// </summary>
public sealed class MatchNotifier : IDisposable
{
    /// <summary>
    /// 兩次送出之間至少隔這麼久。Discord 對單一 webhook 的限制大約是 2 秒 5 次，
    /// 這個間隔等於 2 秒 4 次，留了餘裕。因為送出是單一消費者、一次一則，
    /// 光這一行就足以讓正常使用永遠碰不到 429。
    /// </summary>
    private static readonly TimeSpan MinSendSpacing = TimeSpan.FromMilliseconds(500);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, TemplateState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;

    /// <summary>
    /// 送出佇列。容量刻意很小：dwell 加冷卻讓正常情況下的通知間隔以分鐘計，
    /// 會塞爆的只有「使用者狂按測試」或「同一幀多個樣板同時命中」。
    /// 滿了就丟掉最新的（DropWrite）而不是等——TryWrite 永遠不阻塞，
    /// 這條路徑跑在擷取執行緒上，一毫秒都不能等。
    /// </summary>
    private readonly Channel<PendingSend> _queue = Channel.CreateBounded<PendingSend>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

    private TemplateMatcher? _matcher;
    private NotificationOptions _options = NotificationOptions.Disabled;

    public MatchNotifier()
    {
        _pump = Task.Run(() => PumpAsync(_cancellation.Token));
    }

    /// <summary>送出結果與設定錯誤都從這裡回報，由 UI 端跳回主執行緒寫進狀態列。</summary>
    public event EventHandler<NotificationStatusEventArgs>? StatusChanged;

    /// <summary>
    /// 通知設定。值真的變了才重設連續命中的計時——UI 上每個欄位失焦都會重新套用一次，
    /// 沒有這道比較的話，光是點一下訊息欄再點開就會把計時歸零。
    /// </summary>
    public NotificationOptions Options
    {
        get
        {
            lock (_sync)
            {
                return _options;
            }
        }

        set
        {
            lock (_sync)
            {
                if (_options == value)
                {
                    return;
                }

                _options = value;

                // dwell／冷卻改了之後，進行中的計時已經失去意義
                ForgetRunsLocked();
            }
        }
    }

    public void Attach(TemplateMatcher matcher)
    {
        Detach();

        _matcher = matcher;
        matcher.MatchCycleCompleted += OnMatchCycleCompleted;
    }

    public void Detach()
    {
        if (_matcher is not null)
        {
            _matcher.MatchCycleCompleted -= OnMatchCycleCompleted;
            _matcher = null;
        }
    }

    /// <summary>
    /// 丟掉所有「連續命中」的計時，但保留冷卻紀錄。
    ///
    /// 保留冷卻是刻意的：停止再開始、換目標視窗、重新載入樣板都是使用者在撥弄設定，
    /// 不該變成繞過冷卻、連送好幾則的後門。
    /// </summary>
    public void ResetDwell()
    {
        lock (_sync)
        {
            ForgetRunsLocked();
        }
    }

    /// <summary>
    /// 立刻送一則測試訊息，忽略啟用開關與 dwell／冷卻。
    /// 刻意走與正式通知完全相同的展開與佇列，測試會過就代表正式路徑也會過。
    /// </summary>
    public void SendTest()
    {
        NotificationOptions options = Options;

        if (!DiscordWebhookClient.IsValidWebhookUrl(options.WebhookUrl))
        {
            // 網址格式不對就連碰都不碰網路，直接告訴使用者——這是整個設定流程中
            // 最容易出錯、也最值得立即回饋的一步
            Report(new NotificationStatusEventArgs(
                "Webhook 網址格式不正確，請貼上 Discord 產生的完整 Webhook 網址。",
                isError: true));
            return;
        }

        MatchResult sample = new("測試樣板", 100, 200, 64, 64, 0.99, 0);
        string content = MessageTemplate.Format(
            options.MessageTemplate,
            sample,
            options.UserId,
            DateTime.Now);

        _queue.Writer.TryWrite(new PendingSend(options.WebhookUrl, content, sample.TemplateName, IsTest: true));
    }

    private void OnMatchCycleCompleted(object? sender, MatchCycleEventArgs e)
    {
        // 這裡是擷取執行緒。全程只有一次字典掃描，沒有 I/O 也沒有 await。
        lock (_sync)
        {
            NotificationOptions options = _options;

            if (!options.Enabled || !DiscordWebhookClient.IsValidWebhookUrl(options.WebhookUrl))
            {
                // 沒啟用就連狀態都不記：之後開啟時本來就該從頭開始算連續命中
                ForgetRunsLocked();
                return;
            }

            long now = Stopwatch.GetTimestamp();

            BreakRunsMissingFrom(e.Hits);

            foreach (MatchResult hit in e.Hits)
            {
                if (TryAdvance(hit, options, now))
                {
                    string content = MessageTemplate.Format(
                        options.MessageTemplate,
                        hit,
                        options.UserId,
                        DateTime.Now);

                    _queue.Writer.TryWrite(
                        new PendingSend(options.WebhookUrl, content, hit.TemplateName, IsTest: false));
                }
            }
        }
    }

    /// <summary>
    /// 這一輪沒出現的樣板，連續命中就斷了，重新計時，下次再出現時就是全新的一段。
    /// NotifiedAt 刻意保留：冷卻要跨越「消失又出現」，否則閃一下就能繞過冷卻。
    /// </summary>
    private void BreakRunsMissingFrom(IReadOnlyList<MatchResult> hits)
    {
        foreach (KeyValuePair<string, TemplateState> entry in _states)
        {
            if (entry.Value.RunStartedAt == 0)
            {
                continue;
            }

            bool present = false;

            foreach (MatchResult hit in hits)
            {
                if (string.Equals(hit.TemplateName, entry.Key, StringComparison.Ordinal))
                {
                    present = true;
                    break;
                }
            }

            if (!present)
            {
                entry.Value.RunStartedAt = 0;
                entry.Value.RunNotified = false;
            }
        }
    }

    /// <summary>推進一個命中的狀態，回傳「這次要不要通知」。</summary>
    private bool TryAdvance(MatchResult hit, NotificationOptions options, long now)
    {
        if (!_states.TryGetValue(hit.TemplateName, out TemplateState? state))
        {
            state = new TemplateState();
            _states[hit.TemplateName] = state;
        }

        if (state.RunStartedAt == 0)
        {
            // 上升緣：開始計時，但這一幀本身不通知。
            // 即使 dwell 設 0 也要等到下一次取樣還在——「出現且持續」就是這個意思。
            state.RunStartedAt = now;
            state.RunNotified = false;
            return false;
        }

        if (state.RunNotified)
        {
            // 這一段已經叫過了。目標留在畫面上多久都不再重複——
            // 使用者要的是「一次出現一則」，而不是沒完沒了的提醒。
            return false;
        }

        if (Stopwatch.GetElapsedTime(state.RunStartedAt, now) < options.Dwell)
        {
            return false;
        }

        if (state.NotifiedAt != 0 &&
            Stopwatch.GetElapsedTime(state.NotifiedAt, now) < options.Cooldown)
        {
            // 上一則才剛送出不久，代表目標在短時間內消失又出現。
            // 這裡不設 RunNotified：等冷卻過了，這一段仍然值得通知一次。
            return false;
        }

        state.NotifiedAt = now;
        state.RunNotified = true;
        return true;
    }

    private void ForgetRunsLocked()
    {
        foreach (TemplateState state in _states.Values)
        {
            state.RunStartedAt = 0;
            state.RunNotified = false;
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        try
        {
            await foreach (PendingSend item in _queue.Reader.ReadAllAsync(token))
            {
                SendOutcome outcome = await DiscordWebhookClient.SendAsync(
                    item.WebhookUrl,
                    item.Content,
                    token);

                // 429 只重試一次。dwell 加冷卻之下幾乎不會發生，真的發生就代表
                // 使用者在狂按測試——再多重試也只是把塞車往後推。
                if (outcome.RetryAfter is TimeSpan wait)
                {
                    await Task.Delay(wait, token);
                    outcome = await DiscordWebhookClient.SendAsync(item.WebhookUrl, item.Content, token);
                }

                ReportOutcome(outcome, item);

                await Task.Delay(MinSendSpacing, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 關閉程式，正常結束
        }
    }

    private void ReportOutcome(SendOutcome outcome, PendingSend item)
    {
        if (outcome.Success)
        {
            Report(new NotificationStatusEventArgs(
                item.IsTest ? "測試訊息已送出。" : $"已通知 Discord：{item.Label}",
                isError: false));
            return;
        }

        // RetryAfter 還有值代表重試之後仍被限流
        string message = outcome.Error ?? "Discord 速率限制，這則通知沒有送出。";

        Report(new NotificationStatusEventArgs(message, isError: true));
    }

    private void Report(NotificationStatusEventArgs args)
    {
        StatusChanged?.Invoke(this, args);
    }

    public void Dispose()
    {
        Detach();

        _queue.Writer.TryComplete();
        _cancellation.Cancel();

        // 刻意不等 _pump：它可能正卡在一次 HTTP 上，而 MainWindow.OnClosed 之後
        // 緊接著就是 GraphicsCaptureSource 那個沒有逾時的 loop.Wait()。
        // 在這裡多等最壞會讓關視窗多花一個 HttpClient.Timeout（10 秒）。
        // 代價是關閉當下正在飛的那一則可能送不出去，這個取捨是划算的。
        //
        // 也因為不等，_cancellation 不能 Dispose：pump 還握著它的 token，
        // 之後那些 Task.Delay(token) 會變成 ObjectDisposedException——那不是
        // OperationCanceledException，接不住就成了沒人看得到的 unobserved exception。
        // 這個物件與程式同生共死，交給 GC 就好。
    }

    /// <summary>
    /// 一個樣板的通知狀態。兩個時間都是 Stopwatch 時戳，0 代表「沒有」。
    ///
    /// 用 Stopwatch 而非 DateTime：dwell 與冷卻量的都是「經過多久」，而 DateTime.Now
    /// 會被使用者調時間、NTP 校時、日光節約時間往前往後拉，拉一下就可能讓冷卻
    /// 永遠不到期、或是當場全部到期。Stopwatch 是單調遞增的。
    /// </summary>
    private sealed class TemplateState
    {
        /// <summary>這一段連續命中的起點。0 = 目前沒在命中。</summary>
        public long RunStartedAt;

        /// <summary>這一段連續命中是否已經通知過。連續命中中斷時跟著清掉。</summary>
        public bool RunNotified;

        /// <summary>最後一次為這個樣板送出通知的時刻。0 = 從來沒送過。</summary>
        public long NotifiedAt;
    }

    /// <summary>
    /// 排進佇列的一則訊息。送出需要的東西在排隊當下就全部烤進來了，
    /// pump 因此完全不必回頭讀 _states 或 _options——送出路徑上沒有跨執行緒讀取。
    /// </summary>
    private readonly record struct PendingSend(
        string WebhookUrl,
        string Content,
        string Label,
        bool IsTest);
}

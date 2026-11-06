using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Notifications;

public enum ToastTone
{
    Failure,
    Success,
    Neutral,
}

public sealed record ToastLine(string Text, Uri Url, ToastTone Tone);

/// <param name="OpenUrl">單筆時是該 run；只有新版本時是 release；彙總時為 null（點空白處開主視窗）。</param>
public sealed record ToastContent(
    string Title,
    IReadOnlyList<ToastLine> Lines,
    string? MoreText,
    ToastTone Tone,
    TimeSpan Duration,
    Uri? OpenUrl);

/// <summary>要通知的新版本。</summary>
/// <param name="Version">SemVersion 的 ToString，不含 <c>v</c> 與 build metadata，也是記成「已通知版本」的值。</param>
/// <param name="Url">release 的 html_url，已通過 <see cref="Display.WebLinks.IsAllowed"/>。</param>
public sealed record UpdateNotice(string Version, Uri Url);

/// <summary>
/// 合併與鎖定暫存（spec §9.4、§9.5）：NotificationMergeWindow 內的通知併成一則；
/// 提示還開著時新的內容併入；鎖定期間暫存，解鎖後一則「鎖定期間的變化」。
/// 合併視窗只用單調時鐘計時（spec §3.1），系統時間被調整也不影響。
/// 文字在送出（<see cref="TryFlush(TimeSpan, TimeSpan, AppLanguage)"/>）時才依語言組出來：合併視窗中途切換語言，送出的也是新的語言。
/// 新版本提示（<see cref="UpdateNotice"/>）不是 run，另外存放，但走同一個合併視窗、鎖定暫存與提示視窗；只有它時不算鎖定期間的變化。
/// </summary>
public sealed class NotificationQueue(TimeProvider time)
{
    private const string Separator = " · ";

    /// <summary>
    /// run 的 html_url 不是合法網址時，行的 Url 改用這個。App 開啟前會檢查 https＋github.com（spec §10.7），這個不會被開啟。
    /// </summary>
    private static readonly Uri NotOpenable = new("about:blank");

    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();

    /// <summary>還沒顯示的通知（合併視窗中，或鎖定期間暫存）。</summary>
    private readonly List<PlannedNotification> _pending = [];

    /// <summary>目前開著的提示裡的通知；App 呼叫 Dismissed 後清空。</summary>
    private List<PlannedNotification> _shown = [];

    /// <summary>還沒顯示的新版本提示。</summary>
    private UpdateNotice? _pendingNotice;

    /// <summary>目前開著的提示裡的新版本；和 <see cref="_shown"/> 一起併入新內容、一起清空。</summary>
    private UpdateNotice? _shownNotice;

    /// <summary>剛送出、呼叫端還沒拿走的新版本（<see cref="TakeFlushedNotice"/>）。</summary>
    private UpdateNotice? _flushedNotice;

    /// <summary>_pending 或 _pendingNotice 第一筆加入時的單調時間戳，合併視窗從這裡起算。</summary>
    private long _pendingSince;

    private bool _locked;

    /// <summary>解鎖時還有暫存，下一次送出的是「鎖定期間的變化」。</summary>
    private bool _flushAsLocked;

    public bool Locked
    {
        get
        {
            lock (_gate)
            {
                return _locked;
            }
        }
    }

    public void SetLocked(bool locked)
    {
        lock (_gate)
        {
            if (_locked == locked)
            {
                return;
            }

            _locked = locked;
            if (!locked)
            {
                // 沒有暫存就不顯示（spec §9.5）；只有新版本提示時不是鎖定期間的變化，不看它。
                _flushAsLocked = _pending.Count > 0;
            }
        }
    }

    public void Add(IReadOnlyList<PlannedNotification> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        lock (_gate)
        {
            foreach (var item in items)
            {
                StartMergeWindowIfEmpty();
                _pending.Add(item);
            }
        }
    }

    /// <summary>排入新版本提示；已經有排隊中的就換成這一個，一次只通知最新的。</summary>
    public void AddNotice(UpdateNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        lock (_gate)
        {
            StartMergeWindowIfEmpty();
            _pendingNotice = notice;
        }
    }

    /// <summary>使用者關閉檢查新版本：排隊中的不再顯示，開著的提示之後併入新內容時也不再帶新版本行。</summary>
    public void DropNotice()
    {
        lock (_gate)
        {
            _pendingNotice = null;
            _shownNotice = null;
            _flushedNotice = null;
        }
    }

    /// <returns>
    /// 上一次 <see cref="TryFlush(TimeSpan, TimeSpan, AppLanguage)"/> 真的送出的新版本，只回傳一次；沒有時是 null。
    /// 新版本要到這時才算通知過：鎖定中程式結束時，暫存的通知會消失。
    /// </returns>
    public UpdateNotice? TakeFlushedNotice()
    {
        lock (_gate)
        {
            var notice = _flushedNotice;
            _flushedNotice = null;
            return notice;
        }
    }

    /// <returns>合併視窗已過、而且沒有鎖定時，回傳要顯示（或更新）的內容；否則 null。文字用共用的目前語言（<see cref="UiText.Current"/>）。</returns>
    public ToastContent? TryFlush(TimeSpan failureDuration, TimeSpan successDuration) =>
        TryFlush(failureDuration, successDuration, UiText.Current);

    /// <returns>合併視窗已過、而且沒有鎖定時，回傳要顯示（或更新）的內容；否則 null。</returns>
    public ToastContent? TryFlush(TimeSpan failureDuration, TimeSpan successDuration, AppLanguage language)
    {
        lock (_gate)
        {
            if (_locked || (_pending.Count == 0 && _pendingNotice is null) || _time.GetElapsedTime(_pendingSince) < Tuning.NotificationMergeWindow)
            {
                return null;
            }

            var asLocked = _flushAsLocked;
            _flushAsLocked = false;
            if (asLocked)
            {
                _shownNotice = null;
            }

            // 鎖定期間的變化另起一則；否則併入還開著的提示。
            var items = asLocked ? new List<PlannedNotification>() : new List<PlannedNotification>(_shown);
            var keys = new HashSet<(long RunId, int Attempt, string? Conclusion)>(items.Select(Key));
            var added = false;
            foreach (var item in _pending)
            {
                if (keys.Add(Key(item)))
                {
                    items.Add(item);
                    added = true;
                }
            }

            _pending.Clear();
            if (!added && _pendingNotice is null)
            {
                // 只是重複加入已顯示的內容，不必更新提示。
                return null;
            }

            _shown = items;
            if (_pendingNotice is { } notice)
            {
                _shownNotice = notice;
                _flushedNotice = notice;
                _pendingNotice = null;
            }

            return Compose(items, _shownNotice, asLocked, failureDuration, successDuration, UiText.Of(language), language);
        }
    }

    /// <summary>App 關掉提示時呼叫；之後的通知另起一則，不再併入。</summary>
    public void Dismissed()
    {
        lock (_gate)
        {
            _shown = [];
            _shownNotice = null;
        }
    }

    /// <summary>呼叫端已持有 <see cref="_gate"/>。</summary>
    private void StartMergeWindowIfEmpty()
    {
        if (_pending.Count == 0 && _pendingNotice is null)
        {
            _pendingSince = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// 只有 run 時就是 <see cref="Build"/>。有新版本時：只有它是單筆版面，點空白處開 release；
    /// 也有 run 時照 run 的版面，最後加一行新版本（放最後：行數超過上限時，被省略的是 run 不是它）。
    /// 新版本只通知一次，一律用失敗的停留秒數，免得一下子就錯過。
    /// </summary>
    private static ToastContent Compose(
        IReadOnlyList<PlannedNotification> items,
        UpdateNotice? notice,
        bool lockedSummary,
        TimeSpan failureDuration,
        TimeSpan successDuration,
        TextTable text,
        AppLanguage language)
    {
        if (notice is null)
        {
            return Build(items, lockedSummary, failureDuration, successDuration, text, language);
        }

        if (items.Count == 0)
        {
            ToastLine[] only = [new(text.ToastUpdateOpen, notice.Url, ToastTone.Neutral)];
            return new ToastContent(UiText.Format(text.ToastUpdateAvailable, notice.Version), only, null, ToastTone.Neutral, failureDuration, notice.Url);
        }

        var runs = Build(items, lockedSummary, failureDuration, successDuration, text, language);
        ToastLine updateLine = new(UiText.Format(text.ToastUpdateLine, notice.Version), notice.Url, ToastTone.Neutral);
        return runs with { Lines = [.. runs.Lines, updateLine], Duration = failureDuration };
    }

    private static ToastContent Build(
        IReadOnlyList<PlannedNotification> items,
        bool lockedSummary,
        TimeSpan failureDuration,
        TimeSpan successDuration,
        TextTable text,
        AppLanguage language)
    {
        var tone = OverallTone(items);
        // 灰色結論的停留同成功（spec §9.4）。
        var duration = tone == ToastTone.Failure ? failureDuration : successDuration;

        if (items.Count == 1 && !lockedSummary)
        {
            var only = items[0];
            var url = RunUrl(only.Run);
            ToastLine[] body = [new(BodyText(only.Run), url ?? NotOpenable, ToneOf(only.State))];
            // 網址不合法時退回彙總的行為：點空白處開主視窗。
            return new ToastContent(Heading(only, text, language), body, null, tone, duration, url);
        }

        var counts = Counts(items, text);
        // 不是鎖定期間的彙總一定至少 2 筆（1 筆時是上面的單筆提示），英文固定用複數。
        var title = lockedSummary ? UiText.Format(text.ToastLocked, counts) : UiText.Format(text.ToastSummary, items.Count, counts);
        var lines = items
            .Take(Tuning.SummaryToastMaxLines)
            .Select(i => new ToastLine(Heading(i, text, language), RunUrl(i.Run) ?? NotOpenable, ToneOf(i.State)))
            .ToList();
        var hidden = items.Count - lines.Count;
        var more = hidden > 0 ? UiText.Format(text.ToastMore, hidden) : null;
        return new ToastContent(title, lines, more, tone, duration, OpenUrl: null);
    }

    /// <summary>單筆提示的標題，也是彙總每一行的文字：<c>失敗：repo · workflow #123</c>（<c>Failed: repo · workflow #123</c>）。</summary>
    private static string Heading(PlannedNotification item, TextTable text, AppLanguage language)
    {
        var workflow = WorkflowText(item.Run);
        var number = $"#{item.Run.RunNumber}";
        var workflowPart = workflow.Length == 0 ? number : $"{workflow} {number}";
        var label = RunStateMapper.InLanguage(item.State, language).Label;
        return UiText.Format(text.ToastHeading, SingleLine(label), SingleLine(item.RepoFullName), workflowPart);
    }

    /// <summary>單筆提示的內文 <c>display_title · 分支 · by actor</c>；缺值的部分連同分隔符號省略。</summary>
    private static string BodyText(RunInfo run)
    {
        var actor = SingleLine(run.ActorLogin);
        string[] parts = [SingleLine(run.DisplayTitle), SingleLine(run.HeadBranch), actor.Length == 0 ? string.Empty : $"by {actor}"];
        return string.Join(Separator, parts.Where(p => p.Length > 0));
    }

    /// <summary>name 為 null 時用 path 的檔名（spec §10.5）；E3 的名稱由 Engine 先填進 RunInfo。</summary>
    private static string WorkflowText(RunInfo run)
    {
        var name = SingleLine(run.WorkflowName);
        return name.Length > 0 ? name : SingleLine(Path.GetFileName(run.Path));
    }

    private static string Counts(IReadOnlyList<PlannedNotification> items, TextTable text)
    {
        var failures = items.Count(i => i.State.Category == RunCategory.Failure);
        var successes = items.Count(i => i.State.Category == RunCategory.Success);
        var others = items.Count - failures - successes;

        // 數量為 0 的部分省略。
        var parts = new List<string>();
        if (failures > 0)
        {
            parts.Add(UiText.Format(text.ToastCountFailed, failures));
        }

        if (successes > 0)
        {
            parts.Add(UiText.Format(text.ToastCountSucceeded, successes));
        }

        if (others > 0)
        {
            parts.Add(UiText.Format(text.ToastCountOthers, others));
        }

        return string.Join(text.CommonListSeparator, parts);
    }

    /// <summary>含失敗就是失敗；否則有成功就是成功；只有灰色才是 Neutral。</summary>
    private static ToastTone OverallTone(IReadOnlyList<PlannedNotification> items)
    {
        if (items.Any(i => i.State.Category == RunCategory.Failure))
        {
            return ToastTone.Failure;
        }

        return items.Any(i => i.State.Category == RunCategory.Success) ? ToastTone.Success : ToastTone.Neutral;
    }

    private static ToastTone ToneOf(RunState state) => state.Category switch
    {
        RunCategory.Failure => ToastTone.Failure,
        RunCategory.Success => ToastTone.Success,
        _ => ToastTone.Neutral,
    };

    private static Uri? RunUrl(RunInfo run) =>
        Uri.TryCreate(run.HtmlUrl, UriKind.Absolute, out var url) ? url : null;

    private static (long RunId, int Attempt, string? Conclusion) Key(PlannedNotification item) =>
        (item.Run.Id, item.Run.RunAttempt, item.Run.Conclusion);

    /// <summary>換行換成空白、去掉頭尾空白，讓提示每一行保持單行；null 回空字串。</summary>
    private static string SingleLine(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

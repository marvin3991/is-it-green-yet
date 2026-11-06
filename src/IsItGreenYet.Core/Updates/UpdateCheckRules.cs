using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;

namespace IsItGreenYet.Core.Updates;

internal enum UpdateDecisionKind
{
    NoUpdate,
    AlreadyNotified,
    BadTag,
    BadUrl,
    Notify,
}

/// <param name="Notice">只有 <see cref="UpdateDecisionKind.Notify"/> 時有值。</param>
internal sealed record UpdateDecision(UpdateDecisionKind Kind, UpdateNotice? Notice);

/// <summary>檢查新版本的純函式：到期判斷、結果分類、要不要帶 ETag、要不要通知。不連網，時間由呼叫端傳入。</summary>
internal static class UpdateCheckRules
{
    /// <param name="LastFailedUtc">修正後的上次失敗時間，呼叫端存回去。</param>
    public readonly record struct UpdateDue(bool Due, DateTimeOffset? LastFailedUtc);

    /// <summary>
    /// 到期判斷，只看 UTC 牆上時間（電腦會睡眠，不用計時器累計）。
    /// 失敗後 <see cref="Tuning.UpdateCheckRetryFloor"/> 內一律不到期，優先於其他條件；
    /// 否則從沒檢查過、上次檢查在未來（時鐘被調回）、或距離上次完成滿 <see cref="Tuning.UpdateCheckInterval"/> 就到期。
    /// </summary>
    /// <remarks>失敗時間在未來時夾到現在：時鐘被調回之後，要從現在起再等滿下限，不會無限期等下去。</remarks>
    public static UpdateDue Evaluate(DateTimeOffset nowUtc, DateTimeOffset? lastCompletedUtc, DateTimeOffset? lastFailedUtc)
    {
        if (lastFailedUtc is { } failed)
        {
            if (failed > nowUtc)
            {
                return new UpdateDue(false, nowUtc);
            }

            if (nowUtc - failed < Tuning.UpdateCheckRetryFloor)
            {
                return new UpdateDue(false, failed);
            }
        }

        var due = lastCompletedUtc is not { } last || last > nowUtc || nowUtc - last >= Tuning.UpdateCheckInterval;
        return new UpdateDue(due, lastFailedUtc);
    }

    /// <summary>只有 200、304、404 算完成，會更新上次檢查時間；其他結果都算失敗。</summary>
    public static bool IsCompleted(ApiOutcome outcome) =>
        outcome is ApiOutcome.Ok or ApiOutcome.NotModified or ApiOutcome.NotFound;

    /// <summary>
    /// 沒有上次檢查時間就不帶 ETag：settings.json 重置而 cache.db 還在時，304 沒有內容，新版本會被蓋住。
    /// </summary>
    public static bool ShouldSendEtag(DateTimeOffset? lastCompletedUtc) => lastCompletedUtc is not null;

    /// <summary>
    /// 200 的內容要不要通知。依序判斷：tag 解析不了或缺少、不比目前版本新、不比已通知版本新、網址不允許或缺少，都不通知。
    /// </summary>
    /// <remarks>
    /// 不比已通知版本新：最新的 release 被撤下時，/releases/latest 會回到較舊的版本，而同一個版本只通知一次。
    /// 設定檔只記一個版本，分不出那個較舊的版本通知過沒有，所以沒通知過的也不通知（使用者已經知道有更新的版本）。
    /// </remarks>
    /// <param name="lastNotifiedVersion">已通知版本（<see cref="SemVersion.ToString"/> 的結果）；解析不了時當成沒通知過。</param>
    public static UpdateDecision Decide(SemVersion current, ReleaseInfo release, string? lastNotifiedVersion)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(release);

        if (!SemVersion.TryParseTag(release.TagName, out var latest))
        {
            return new UpdateDecision(UpdateDecisionKind.BadTag, null);
        }

        if (latest.CompareTo(current) <= 0)
        {
            return new UpdateDecision(UpdateDecisionKind.NoUpdate, null);
        }

        if (SemVersion.TryParse(lastNotifiedVersion, out var notified) && notified.CompareTo(latest) >= 0)
        {
            return new UpdateDecision(UpdateDecisionKind.AlreadyNotified, null);
        }

        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var url) || !WebLinks.IsAllowed(url))
        {
            return new UpdateDecision(UpdateDecisionKind.BadUrl, null);
        }

        return new UpdateDecision(UpdateDecisionKind.Notify, new UpdateNotice(latest.ToString(), url));
    }
}

using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Localization;

/// <summary>確認對話框上寫明是哪一筆 run（第二階段 spec §9.2）。</summary>
/// <param name="Workflow">畫面上的 workflow 名稱（同清單第二行）。</param>
/// <param name="Attempt">第幾次嘗試；確認後當成 expectedAttempt 送出（第二階段 spec §5.1）。</param>
/// <param name="Branch">畫面上的分支（fork 是 <c>owner:branch</c>）；head_branch 是 null 時是 null。</param>
public sealed record ConfirmTarget(string RepoFullName, string Workflow, int RunNumber, int Attempt, string? Branch);

/// <summary>確認對話框的文字（第二階段 spec §9.2），在開啟當下用目前語言組好；開著時切換語言不更新。</summary>
/// <param name="Target">repo · workflow #run_number，換行後是第幾次嘗試與分支。</param>
/// <param name="ConfirmLabel">右邊執行按鈕的文字。</param>
/// <param name="CancelLabel">左邊「不要」的文字。</param>
/// <param name="Danger">執行按鈕用 Danger 樣式（取消類）；否則用 Accent（重跑類）。</param>
public sealed record ConfirmText(string Title, string Target, string Body, string ConfirmLabel, string CancelLabel, bool Danger);

/// <summary>寫入結果要顯示的文字（第二階段 spec §9.4）。</summary>
/// <param name="AsDialog">用錯誤對話框顯示（被拒、結果不確定）；否則用訊息條（已受理、未送出）。</param>
public sealed record ResultText(bool AsDialog, string Text);

/// <summary>
/// 寫入操作的畫面文字（第二階段 spec §9.1～§9.4、§9.6）：選單項目、停用原因、確認對話框、送出中、結果與「取消中」。純函式，語言一律明確傳入。
/// </summary>
/// <remarks>
/// <para>
/// 列舉一律逐一對應：每個已定義的值都有自己的一項，不認得的值（新增列舉值卻沒有加文字）丟 <see cref="ArgumentOutOfRangeException"/>，
/// 不會落到通用的說法。
/// </para>
/// <para>
/// WriteTextTests 用 <c>Enum.GetValues</c> 逐值呼叫每一個對應：<see cref="ActionBlock"/>（<see cref="Block"/>）、
/// <see cref="NotSentReason"/> 與 <see cref="WriteRejectReason"/>（<see cref="Result"/> 的文字）、<see cref="WriteOutcome"/>（<see cref="Result"/>
/// 用對話框或訊息條）、<see cref="WriteAction"/>（<see cref="MenuLabel"/>、<see cref="Confirm"/>、已受理的文字與危險樣式）。
/// 對話框或訊息條、重跑類或取消類各有一張期望表，表的鍵必須等於列舉的全部值；新增列舉值時，漏加這裡的對應或漏改期望表，測試都會失敗。
/// </para>
/// </remarks>
public static class WriteText
{
    /// <summary>
    /// 權限不足的 403 沒有帶 X-Accepted-GitHub-Permissions 時寫出的權限：四個寫入端點都要 fine-grained 的 Actions: write
    /// （第二階段 spec §3.1 [W1]、[W6]），寫法同那個標頭（例如 <c>actions=write</c>）。
    /// </summary>
    private const string ActionsWritePermission = "actions=write";

    /// <summary>對話框裡的分行與分段（同其他錯誤訊息的寫法，例如 StartupFailed）。</summary>
    private const string LineBreak = "\n";

    private const string ParagraphBreak = "\n\n";

    /// <summary>沒有狀態碼（連線層錯誤）時記 0（同 GitHubClient）。</summary>
    private const int NoStatusCode = 0;

    /// <summary>run 選單的項目文字（第二階段 spec §9.1）。</summary>
    public static string MenuLabel(WriteAction action, AppLanguage language)
    {
        var text = UiText.Of(language);
        return action switch
        {
            WriteAction.RerunFailedJobs => text.MenuRerunFailedJobs,
            WriteAction.RerunAll => text.MenuRerunAll,
            WriteAction.Cancel => text.MenuCancelRun,
            WriteAction.ForceCancel => text.MenuForceCancel,
            _ => throw Unknown(nameof(action), action),
        };
    }

    /// <summary>
    /// 停用的原因（第二階段 spec §7 的原因代碼），放在停用項目的 tooltip 與報讀說明。<see cref="ActionBlock.None"/>（可以用）是空字串。
    /// 缺少權限時加上「到設定頁按『檢查連線』可以重新檢查」（第二階段 spec §4.3）。
    /// </summary>
    public static string Block(ActionBlock block, AppLanguage language)
    {
        var text = UiText.Of(language);
        return block switch
        {
            ActionBlock.None => string.Empty,
            ActionBlock.InFlight => text.ActionBlockInFlight,
            ActionBlock.MissingPermission => text.ActionBlockMissingPermission + text.ActionBlockCheckConnectionHint,
            ActionBlock.NotLoggedIn => text.ActionBlockNotLoggedIn,
            ActionBlock.VersionError => text.ActionBlockVersionError,
            ActionBlock.Offline => text.ActionBlockOffline,
            ActionBlock.RateLimitPaused => text.ActionBlockRateLimitPaused,
            ActionBlock.NotCompleted => text.ActionBlockNotCompleted,
            ActionBlock.Completed => text.ActionBlockCompleted,
            ActionBlock.NoRerunnableJobs => text.ActionBlockNoRerunnableJobs,
            ActionBlock.NoFailedJobs => text.ActionBlockNoFailedJobs,
            ActionBlock.AwaitingApproval => text.ActionBlockAwaitingApproval,
            ActionBlock.RerunPending => text.ActionBlockRerunPending,
            ActionBlock.WorkflowDisabled => text.ActionBlockWorkflowDisabled,
            ActionBlock.TooOld => UiText.Format(text.ActionBlockTooOld, Tuning.RerunWindowDays),
            ActionBlock.RerunLimit => text.ActionBlockRerunLimit,
            ActionBlock.CancelPending => text.ActionBlockCancelPending,
            ActionBlock.ForceNotYet => UiText.Format(text.ActionBlockForceNotYet, Tuning.CancelGraceBeforeForce.TotalMinutes),
            _ => throw Unknown(nameof(block), block),
        };
    }

    /// <summary>
    /// 確認對話框（第二階段 spec §9.2）：標題、是哪一筆、說明的重點、左「不要」與右邊的執行按鈕。取消類的執行按鈕用 Danger 樣式，重跑類用 Accent。
    /// </summary>
    public static ConfirmText Confirm(WriteAction action, ConfirmTarget target, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(target);
        var text = UiText.Of(language);
        var (title, body, button) = action switch
        {
            WriteAction.RerunFailedJobs => (text.ConfirmRerunFailedJobsTitle,
                UiText.Format(text.ConfirmRerunFailedJobsBody, Tuning.MaxReruns), text.ConfirmRerunFailedJobsButton),
            WriteAction.RerunAll => (text.ConfirmRerunAllTitle, UiText.Format(text.ConfirmRerunAllBody, Tuning.MaxReruns), text.ConfirmRerunAllButton),
            WriteAction.Cancel => (text.ConfirmCancelRunTitle, text.ConfirmCancelRunBody, text.ConfirmCancelRunButton),
            WriteAction.ForceCancel => (text.ConfirmForceCancelTitle, text.ConfirmForceCancelBody, text.ConfirmForceCancelButton),
            _ => throw Unknown(nameof(action), action),
        };

        return new ConfirmText(title, TargetText(target, text), body, button, text.ConfirmNo, Danger: IsCancel(action));
    }

    /// <summary>
    /// 寫入結果的顯示（第二階段 spec §9.4）：已受理與未送出用訊息條，被拒與結果不確定用錯誤對話框。
    /// 程式正在結束（未送出：<see cref="NotSentReason.ShuttingDown"/>）回 null，不顯示（同複製 log）。
    /// </summary>
    public static ResultText? Result(WriteResult result, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = UiText.Of(language);
        return result.Outcome switch
        {
            WriteOutcome.Accepted => new ResultText(AsDialog: false, AcceptedText(result.Action, text)),
            WriteOutcome.Rejected => new ResultText(AsDialog: true, RejectedText(result, text)),
            WriteOutcome.NotSent => NotSentText(result, language) is { } notSent ? new ResultText(AsDialog: false, notSent) : null,
            WriteOutcome.Uncertain => new ResultText(AsDialog: true, UncertainText(result, text)),
            _ => throw Unknown(nameof(result), result.Outcome),
        };
    }

    /// <summary>確認之後、結果回來之前的訊息條（第二階段 spec §9.4）。</summary>
    public static string Sending(AppLanguage language) => UiText.Of(language).WriteSending;

    /// <summary>「取消中」（第二階段 spec §9.3）：狀態的文字、tooltip、報讀名稱與時間欄共用。</summary>
    public static string Cancelling(AppLanguage language) => UiText.Of(language).RowCancelling;

    /// <summary>repo · workflow #run_number，換行後是「第 N 次嘗試 · 分支 x」；沒有分支時只寫第幾次嘗試。</summary>
    private static string TargetText(ConfirmTarget target, TextTable text)
    {
        var run = UiText.Format(text.ConfirmTarget, target.RepoFullName, target.Workflow, target.RunNumber);
        var attempt = string.IsNullOrWhiteSpace(target.Branch)
            ? UiText.Format(text.RowAttempt, target.Attempt)
            : UiText.Format(text.ConfirmAttemptBranch, target.Attempt, target.Branch);
        return run + LineBreak + attempt;
    }

    /// <summary>已受理：重跑與取消各一句（強制取消同取消）。</summary>
    private static string AcceptedText(WriteAction action, TextTable text) =>
        IsCancel(action) ? text.WriteAcceptedCancel : text.WriteAcceptedRerun;

    /// <summary>被拒：§3.3 的分類各一句，伺服器有 message 時另起一段附上原文（不改寫）。</summary>
    private static string RejectedText(WriteResult result, TextTable text)
    {
        // Engine 一定會帶原因（WriteContracts）；沒有時同 Engine 的分類，當成「被拒：其他」。
        var reason = (result.Rejected ?? WriteRejectReason.Other) switch
        {
            WriteRejectReason.RateLimited => text.WriteRejectedRateLimited,
            WriteRejectReason.Unauthorized => text.WriteRejectedUnauthorized,
            WriteRejectReason.InsufficientPermission => UiText.Format(text.WriteRejectedInsufficientPermission,
                string.IsNullOrWhiteSpace(result.AcceptedPermissions) ? ActionsWritePermission : result.AcceptedPermissions),
            WriteRejectReason.Forbidden => text.WriteRejectedForbidden,
            WriteRejectReason.NotFound => text.WriteRejectedNotFound,
            WriteRejectReason.Conflict => text.WriteRejectedConflict,
            WriteRejectReason.Unprocessable => text.WriteRejectedUnprocessable,
            WriteRejectReason.Moved => text.WriteRejectedMoved,
            WriteRejectReason.Other => UiText.Format(text.WriteRejectedGeneric, StatusText(result.StatusCode, text)),
            var unknown => throw Unknown(nameof(result), unknown),
        };

        return WithServerMessage(reason, result, text);
    }

    /// <summary>
    /// 結果不確定：沒拿到回應（狀態碼 0）是 §9.4 的原文；收到 5xx（§3.3 第 11 列）時寫出狀態碼，不說「沒有收到回應」，
    /// 伺服器有 message 時同被拒另起一段附上原文。
    /// </summary>
    private static string UncertainText(WriteResult result, TextTable text) =>
        result.StatusCode == NoStatusCode
            ? text.WriteUncertain
            : WithServerMessage(UiText.Format(text.WriteUncertainServerError, StatusText(result.StatusCode, text)), result, text);

    private static string WithServerMessage(string explanation, WriteResult result, TextTable text) =>
        string.IsNullOrWhiteSpace(result.ServerMessage)
            ? explanation
            : explanation + ParagraphBreak + UiText.Format(text.WriteServerMessage, result.ServerMessage);

    /// <summary>未送出：第二階段 spec §5.2 的原因各一句；程式正在結束回 null。</summary>
    private static string? NotSentText(WriteResult result, AppLanguage language)
    {
        var text = UiText.Of(language);

        // Engine 一定會帶原因（WriteContracts）；沒有時當成送出前的未預期錯誤。
        return (result.NotSent ?? NotSentReason.Unexpected) switch
        {
            NotSentReason.WritesDisabled => text.WriteNotSentWritesDisabled,
            NotSentReason.NotLoggedIn => text.WriteNotSentNotLoggedIn,
            NotSentReason.VersionError => text.WriteNotSentVersionError,
            NotSentReason.RateLimitPaused => text.WriteNotSentRateLimitPaused,
            NotSentReason.Offline => text.WriteNotSentOffline,
            NotSentReason.MissingPermission => text.WriteNotSentMissingPermission,

            // 不能操作時 Engine 帶 §7 的原因代碼；沒帶（不該發生）時同未預期錯誤，不寫出空的原因。
            NotSentReason.Unavailable => result.Block is { } block and not ActionBlock.None
                ? UiText.Format(text.WriteNotSentUnavailable, Block(block, language))
                : text.WriteNotSentUnexpected,
            NotSentReason.AttemptChanged => text.WriteNotSentAttemptChanged,
            NotSentReason.InFlight => text.WriteNotSentInFlight,
            NotSentReason.NotFound => text.WriteNotSentNotFound,
            NotSentReason.QueueTimeout => text.WriteNotSentQueueTimeout,
            NotSentReason.ShuttingDown => null,
            NotSentReason.Unexpected => text.WriteNotSentUnexpected,
            var unknown => throw Unknown(nameof(result), unknown),
        };
    }

    /// <summary>
    /// 取消類（E13、E14）或重跑類（E11、E12）：確認對話框的危險樣式與已受理的文字都看它。
    /// 逐一對應，不認得的值丟例外，不會默默當成重跑類。
    /// </summary>
    private static bool IsCancel(WriteAction action) => action switch
    {
        WriteAction.RerunFailedJobs or WriteAction.RerunAll => false,
        WriteAction.Cancel or WriteAction.ForceCancel => true,
        _ => throw Unknown(nameof(action), action),
    };

    private static string StatusText(int statusCode, TextTable text) =>
        statusCode == NoStatusCode ? text.CommonConnectionFailed : UiText.Format(text.CommonStatusCode, statusCode);

    private static ArgumentOutOfRangeException Unknown<T>(string paramName, T value)
        where T : struct, Enum =>
        new(paramName, value, "沒有對應文字的 " + typeof(T).Name + "。");
}

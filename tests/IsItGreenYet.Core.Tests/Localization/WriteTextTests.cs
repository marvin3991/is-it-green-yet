using System.Globalization;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Tests.Localization;

/// <summary>
/// 寫入操作的畫面文字（第二階段 spec §9.1～§9.4、§9.6）：選單項目、停用原因、確認對話框、送出結果。
/// 每個列舉值都要有兩種語言的文字：新增列舉值卻漏了文字時，這裡的 Theory 會失敗，不會落到通用的說法。
/// </summary>
public class WriteTextTests
{
    private const AppLanguage Chinese = AppLanguage.TraditionalChinese;
    private const AppLanguage English = AppLanguage.English;

    /// <summary>權限不足的 403 帶回的 X-Accepted-GitHub-Permissions（假資料）。</summary>
    private const string AcceptedPermissions = "actions=write";

    /// <summary>伺服器回應的 message 原文（假資料）。</summary>
    private const string ServerMessage = "Cannot cancel a workflow run that is completed.";

    private static readonly ConfirmTarget Target = new("octo-org/sample-app", "CI", 42, 2, "main");

    /// <summary>
    /// 每種結果用對話框（true）還是訊息條（false）顯示（第二階段 spec §9.4）。鍵必須等於 <see cref="WriteOutcome"/> 的全部值
    /// （<c>期望表涵蓋每個列舉值</c>）：新增結果時要在這裡決定怎麼顯示。
    /// </summary>
    private static readonly Dictionary<WriteOutcome, bool> AsDialogByOutcome = new()
    {
        [WriteOutcome.Accepted] = false,
        [WriteOutcome.Rejected] = true,
        [WriteOutcome.NotSent] = false,
        [WriteOutcome.Uncertain] = true,
    };

    /// <summary>
    /// 每個動作是取消類（true：危險樣式、「已送出取消」）還是重跑類（false：Accent、「已送出重跑」）（第二階段 spec §9.1、§9.4）。
    /// 鍵必須等於 <see cref="WriteAction"/> 的全部值。
    /// </summary>
    private static readonly Dictionary<WriteAction, bool> CancelClassByAction = new()
    {
        [WriteAction.RerunFailedJobs] = false,
        [WriteAction.RerunAll] = false,
        [WriteAction.Cancel] = true,
        [WriteAction.ForceCancel] = true,
    };

    public static TheoryData<AppLanguage> Languages => [Chinese, English];

    public static TheoryData<WriteOutcome> Outcomes => new(Enum.GetValues<WriteOutcome>());

    public static TheoryData<NotSentReason> AllNotSentReasons => new(Enum.GetValues<NotSentReason>());

    public static TheoryData<ActionBlock> Blocks => new(Enum.GetValues<ActionBlock>().Where(block => block != ActionBlock.None).ToArray());

    public static TheoryData<NotSentReason> ShownNotSentReasons =>
        new(Enum.GetValues<NotSentReason>().Where(reason => reason != NotSentReason.ShuttingDown).ToArray());

    public static TheoryData<WriteRejectReason> RejectReasons => new(Enum.GetValues<WriteRejectReason>());

    public static TheoryData<WriteAction> Actions => new(Enum.GetValues<WriteAction>());

    [Theory]
    [MemberData(nameof(Blocks))]
    public void 每個ActionBlock都有兩種語言的文字(ActionBlock block)
    {
        AssertBothLanguages(language => WriteText.Block(block, language));
    }

    [Fact]
    public void 可以用時沒有原因()
    {
        Assert.Equal(string.Empty, WriteText.Block(ActionBlock.None, Chinese));
        Assert.Equal(string.Empty, WriteText.Block(ActionBlock.None, English));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 缺少權限的說明含檢查連線(AppLanguage language)
    {
        // 第二階段 spec §4.3：停用的項目提示「到設定頁按『檢查連線』可以重新檢查」；引用的按鈕名稱要和設定頁一致。
        var quoted = Quoted(UiText.Of(language).SettingsCheckConnection, language);

        Assert.Contains(quoted, WriteText.Block(ActionBlock.MissingPermission, language), StringComparison.Ordinal);
        Assert.Contains(quoted, ResultOf(NotSent(NotSentReason.MissingPermission), language).Text, StringComparison.Ordinal);

        // 其他原因不加這句。
        Assert.DoesNotContain(quoted, WriteText.Block(ActionBlock.Offline, language), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ShownNotSentReasons))]
    public void 每個NotSentReason都有文字(NotSentReason reason)
    {
        AssertBothLanguages(language => ResultOf(NotSent(reason), language).Text);
    }

    [Fact]
    public void 程式正在結束時不顯示()
    {
        // 第二階段 spec §9.4：程式正在結束時當成「已取消」，不顯示（同複製 log）。
        Assert.Null(WriteText.Result(NotSent(NotSentReason.ShuttingDown), Chinese));
        Assert.Null(WriteText.Result(NotSent(NotSentReason.ShuttingDown), English));
    }

    [Theory]
    [MemberData(nameof(RejectReasons))]
    public void 每個WriteRejectReason都有文字(WriteRejectReason reason)
    {
        AssertBothLanguages(language => ResultOf(Rejected(reason), language).Text);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 權限不足時帶出需要的權限(AppLanguage language)
    {
        var withHeader = Rejected(WriteRejectReason.InsufficientPermission) with { AcceptedPermissions = "actions=write; contents=read" };
        Assert.Contains("actions=write; contents=read", ResultOf(withHeader, language).Text, StringComparison.Ordinal);

        // 沒有 X-Accepted-GitHub-Permissions 時，寫出四個端點都要的 Actions write（第二階段 spec §3.1）。
        var withoutHeader = Rejected(WriteRejectReason.InsufficientPermission) with { AcceptedPermissions = null };
        Assert.Contains(AcceptedPermissions, ResultOf(withoutHeader, language).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 期望表涵蓋每個列舉值()
    {
        // 新增列舉值時，用這兩張表的 Theory 會逐值呼叫 WriteText；這裡確保期望表也跟著決定了新值的分類，不會因為表裡沒有而略過。
        Assert.Equal(Enum.GetValues<WriteOutcome>().ToHashSet(), AsDialogByOutcome.Keys.ToHashSet());
        Assert.Equal(Enum.GetValues<WriteAction>().ToHashSet(), CancelClassByAction.Keys.ToHashSet());
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void 被拒與結果不確定用對話框而受理與未送出用訊息條(WriteOutcome outcome)
    {
        Assert.True(AsDialogByOutcome.TryGetValue(outcome, out var asDialog), $"期望表沒有 {outcome}");

        // 代表性的結果：每個欄位都帶值（被拒的原因、未送出的原因），Result 只讀這個結果用得到的欄位。
        var result = new WriteResult(WriteAction.Cancel, outcome, 409, WriteRejectReason.Conflict, NotSentReason.Offline,
            ServerMessage: ServerMessage);
        foreach (var language in new[] { Chinese, English })
        {
            var shown = ResultOf(result, language);
            Assert.Equal(asDialog, shown.AsDialog);
            Assert.False(string.IsNullOrWhiteSpace(shown.Text));
        }

        AssertBothLanguages(language => ResultOf(result, language).Text);
    }

    [Theory]
    [MemberData(nameof(AllNotSentReasons))]
    public void 未送出只有程式正在結束時不顯示(NotSentReason reason)
    {
        foreach (var language in new[] { Chinese, English })
        {
            var shown = WriteText.Result(NotSent(reason), language);
            Assert.Equal(reason == NotSentReason.ShuttingDown, shown is null);
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 被拒時附上伺服器message原文(AppLanguage language)
    {
        var text = UiText.Of(language);
        var withMessage = ResultOf(Rejected(WriteRejectReason.Conflict) with { ServerMessage = ServerMessage }, language).Text;
        Assert.Contains(ServerMessage, withMessage, StringComparison.Ordinal);
        Assert.StartsWith(text.WriteRejectedConflict, withMessage, StringComparison.Ordinal);

        // 沒有 message 時只有原因的說明，不留下空的「GitHub 的訊息：」。
        var withoutMessage = ResultOf(Rejected(WriteRejectReason.Conflict) with { ServerMessage = null }, language).Text;
        Assert.Equal(text.WriteRejectedConflict, withoutMessage);
        Assert.Equal(withoutMessage, ResultOf(Rejected(WriteRejectReason.Conflict) with { ServerMessage = " " }, language).Text);
    }

    [Fact]
    public void 其他被拒附上狀態碼()
    {
        var result = Rejected(WriteRejectReason.Other) with { StatusCode = 418 };

        Assert.Contains("418", ResultOf(result, Chinese).Text, StringComparison.Ordinal);
        Assert.Contains("HTTP 418", ResultOf(result, English).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 結果不確定說明可能已經生效()
    {
        // 第二階段 spec §9.4 的原文。
        Assert.Equal(
            "沒有收到 GitHub 的回應，可能已經生效。App 會自動確認這筆 run 的狀態。",
            ResultOf(new WriteResult(WriteAction.RerunAll, WriteOutcome.Uncertain), Chinese).Text);
    }

    [Fact]
    public void 收到5xx的結果不確定寫出狀態碼與伺服器message而且不說沒有回應()
    {
        // 第二階段 spec §9.4：5xx 有回應，但可能已經生效（§3.3 第 11 列）。
        var result = new WriteResult(WriteAction.RerunAll, WriteOutcome.Uncertain, 502, ServerMessage: ServerMessage);

        Assert.Equal(
            "GitHub 回報伺服器錯誤（狀態碼 502），可能已經生效。App 會自動確認這筆 run 的狀態。\n\nGitHub 的訊息：" + ServerMessage,
            ResultOf(result, Chinese).Text);
        var english = ResultOf(result, English).Text;
        Assert.Contains("HTTP 502", english, StringComparison.Ordinal);
        Assert.Contains(ServerMessage, english, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.Of(English).WriteUncertain, english, StringComparison.Ordinal);

        // 沒有 message 時只有說明。
        Assert.Equal(
            "GitHub 回報伺服器錯誤（狀態碼 503），可能已經生效。App 會自動確認這筆 run 的狀態。",
            ResultOf(result with { StatusCode = 503, ServerMessage = null }, Chinese).Text);
        Assert.True(ResultOf(result, Chinese).AsDialog);
        AssertBothLanguages(language => ResultOf(result, language).Text);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void 受理的訊息分重跑與取消(WriteAction action)
    {
        Assert.True(CancelClassByAction.TryGetValue(action, out var cancelClass), $"期望表沒有 {action}");
        foreach (var language in new[] { Chinese, English })
        {
            var text = UiText.Of(language);
            Assert.NotEqual(text.WriteAcceptedRerun, text.WriteAcceptedCancel);
            Assert.Equal(cancelClass ? text.WriteAcceptedCancel : text.WriteAcceptedRerun, Accepted(action, language));
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 不能操作時附上原因(AppLanguage language)
    {
        var result = NotSent(NotSentReason.Unavailable) with { Block = ActionBlock.CancelPending };

        Assert.Contains(WriteText.Block(ActionBlock.CancelPending, language), ResultOf(result, language).Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void 確認文字含repo_workflow與run編號(AppLanguage language)
    {
        var confirm = WriteText.Confirm(WriteAction.RerunFailedJobs, Target, language);

        Assert.Contains("octo-org/sample-app", confirm.Target, StringComparison.Ordinal);
        Assert.Contains("CI #42", confirm.Target, StringComparison.Ordinal);

        // 第幾次嘗試與分支（第二階段 spec §9.2）。
        Assert.Contains(UiText.Format(UiText.Of(language).ConfirmAttemptBranch, 2, "main"), confirm.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void 確認文字的寫法()
    {
        Assert.Equal("octo-org/sample-app · CI #42\n第 2 次嘗試 · 分支 main", WriteText.Confirm(WriteAction.Cancel, Target, Chinese).Target);
        Assert.Equal("octo-org/sample-app · CI #42\nAttempt 2 · Branch main", WriteText.Confirm(WriteAction.Cancel, Target, English).Target);

        // head_branch 是 null 時只寫第幾次嘗試。
        Assert.Equal("octo-org/sample-app · CI #42\n第 2 次嘗試", WriteText.Confirm(WriteAction.Cancel, Target with { Branch = null }, Chinese).Target);
        Assert.Equal("octo-org/sample-app · CI #42\nAttempt 2", WriteText.Confirm(WriteAction.Cancel, Target with { Branch = " " }, English).Target);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void 取消類用危險樣式而重跑類不是(WriteAction action)
    {
        Assert.True(CancelClassByAction.TryGetValue(action, out var cancelClass), $"期望表沒有 {action}");
        Assert.Equal(cancelClass, WriteText.Confirm(action, Target, Chinese).Danger);
        Assert.Equal(cancelClass, WriteText.Confirm(action, Target, English).Danger);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void 每個WriteAction都有選單文字與確認文字(WriteAction action)
    {
        AssertBothLanguages(language => WriteText.MenuLabel(action, language));
        AssertBothLanguages(language => WriteText.Confirm(action, Target, language).Title);
        AssertBothLanguages(language => WriteText.Confirm(action, Target, language).Body);
        AssertBothLanguages(language => WriteText.Confirm(action, Target, language).ConfirmLabel);

        // 左邊的按鈕一律是「不要」（第二階段 spec §9.2）。
        Assert.Equal("不要", WriteText.Confirm(action, Target, Chinese).CancelLabel);
        Assert.Equal(UiText.Of(English).ConfirmNo, WriteText.Confirm(action, Target, English).CancelLabel);
    }

    [Fact]
    public void 重跑的說明與停用原因用Tuning的數值()
    {
        var maxReruns = Tuning.MaxReruns.ToString(CultureInfo.InvariantCulture);
        Assert.Contains(maxReruns, WriteText.Confirm(WriteAction.RerunFailedJobs, Target, Chinese).Body, StringComparison.Ordinal);
        Assert.Contains(maxReruns, WriteText.Confirm(WriteAction.RerunAll, Target, English).Body, StringComparison.Ordinal);

        Assert.Contains(Tuning.RerunWindowDays.ToString(CultureInfo.InvariantCulture), WriteText.Block(ActionBlock.TooOld, Chinese), StringComparison.Ordinal);
        Assert.Contains(
            Tuning.CancelGraceBeforeForce.TotalMinutes.ToString(CultureInfo.InvariantCulture),
            WriteText.Block(ActionBlock.ForceNotYet, English),
            StringComparison.Ordinal);
    }

    [Fact]
    public void 每種原因的文字都不同()
    {
        foreach (var language in new[] { Chinese, English })
        {
            AssertDistinct(Enum.GetValues<ActionBlock>().Where(block => block != ActionBlock.None).Select(block => WriteText.Block(block, language)));
            AssertDistinct(Enum.GetValues<NotSentReason>().Where(reason => reason != NotSentReason.ShuttingDown)
                .Select(reason => ResultOf(NotSent(reason), language).Text));
            AssertDistinct(Enum.GetValues<WriteRejectReason>().Select(reason => ResultOf(Rejected(reason), language).Text));
            AssertDistinct(Enum.GetValues<WriteAction>().Select(action => WriteText.MenuLabel(action, language)));
        }
    }

    [Fact]
    public void 送出中與取消中()
    {
        Assert.Equal("正在送出…", WriteText.Sending(Chinese));
        Assert.Equal(UiText.Of(English).WriteSending, WriteText.Sending(English));

        // 第二階段 spec §9.3 的原文：「取消中」與英文的 Cancelling。
        Assert.Equal("取消中", WriteText.Cancelling(Chinese));
        Assert.Equal("Cancelling", WriteText.Cancelling(English));
    }

    [Fact]
    public void 不認得的列舉值丟例外()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Block((ActionBlock)99, Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.MenuLabel((WriteAction)99, Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Confirm((WriteAction)99, Target, Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Result(new WriteResult(WriteAction.Cancel, (WriteOutcome)99), Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Result(new WriteResult((WriteAction)99, WriteOutcome.Accepted), Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Result(NotSent((NotSentReason)99), Chinese));
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteText.Result(Rejected((WriteRejectReason)99), Chinese));
    }

    /// <summary>兩種語言都有文字、不一樣，英文沒有漏翻的中文。</summary>
    private static void AssertBothLanguages(Func<AppLanguage, string> text)
    {
        var chinese = text(Chinese);
        var english = text(English);

        Assert.False(string.IsNullOrWhiteSpace(chinese));
        Assert.False(string.IsNullOrWhiteSpace(english));
        Assert.NotEqual(chinese, english);
        Assert.False(english.Any(TextTableTests.IsCjk), english);
    }

    private static void AssertDistinct(IEnumerable<string> texts)
    {
        var list = texts.ToList();
        Assert.Equal(list.Count, list.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>中文用「」、英文用 "" 引用畫面上的名稱（同 TextTableTests 的選單名稱檢查）。</summary>
    private static string Quoted(string name, AppLanguage language) =>
        language == English ? "\"" + name + "\"" : "「" + name + "」";

    private static WriteResult NotSent(NotSentReason reason) =>
        new(WriteAction.Cancel, WriteOutcome.NotSent, NotSent: reason, Block: reason == NotSentReason.Unavailable ? ActionBlock.Completed : null);

    private static WriteResult Rejected(WriteRejectReason reason) =>
        new(WriteAction.RerunAll, WriteOutcome.Rejected, 403, reason, AcceptedPermissions: AcceptedPermissions);

    private static string Accepted(WriteAction action, AppLanguage language) =>
        ResultOf(new WriteResult(action, WriteOutcome.Accepted, 201), language).Text;

    private static ResultText ResultOf(WriteResult result, AppLanguage language)
    {
        var shown = WriteText.Result(result, language);
        Assert.NotNull(shown);
        return shown;
    }
}

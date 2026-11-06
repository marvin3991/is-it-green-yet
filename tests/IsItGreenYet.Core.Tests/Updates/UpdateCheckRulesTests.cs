using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Updates;

namespace IsItGreenYet.Core.Tests.Updates;

public class UpdateCheckRulesTests
{
    private const string ReleaseUrl = "https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0";

    // 刻意寫字面值，不用 Tuning：這兩個數字是需求。
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    // ── 到期 ───────────────────────────────────────────────────────────

    [Fact]
    public void 從沒檢查過就到期()
    {
        Assert.Equal(new UpdateCheckRules.UpdateDue(true, null), UpdateCheckRules.Evaluate(Now, null, null));
    }

    [Fact]
    public void 未滿3天不到期()
    {
        Assert.False(UpdateCheckRules.Evaluate(Now, Now - ThreeDays + OneSecond, null).Due);
    }

    [Fact]
    public void 剛好滿3天到期()
    {
        Assert.True(UpdateCheckRules.Evaluate(Now, Now - ThreeDays, null).Due);
    }

    [Fact]
    public void 上次檢查在未來視為到期()
    {
        Assert.True(UpdateCheckRules.Evaluate(Now, Now + OneSecond, null).Due);
    }

    [Fact]
    public void 失敗未滿1小時不到期()
    {
        var failed = Now - OneHour + OneSecond;

        Assert.Equal(new UpdateCheckRules.UpdateDue(false, failed), UpdateCheckRules.Evaluate(Now, null, failed));
    }

    [Fact]
    public void 失敗剛好滿1小時到期()
    {
        var failed = Now - OneHour;

        Assert.Equal(new UpdateCheckRules.UpdateDue(true, failed), UpdateCheckRules.Evaluate(Now, null, failed));
    }

    [Fact]
    public void 失敗滿1小時但上次檢查未滿3天仍不到期()
    {
        Assert.False(UpdateCheckRules.Evaluate(Now, Now - ThreeDays + OneSecond, Now - OneHour).Due);
    }

    [Fact]
    public void 失敗冷卻優先於未來的上次檢查()
    {
        Assert.False(UpdateCheckRules.Evaluate(Now, Now + OneHour, Now - OneSecond).Due);
    }

    [Fact]
    public void 失敗時間在未來時不到期並夾到現在()
    {
        var due = UpdateCheckRules.Evaluate(Now, null, Now + OneSecond);

        Assert.False(due.Due);
        Assert.Equal(Now, due.LastFailedUtc);
    }

    // ── 完成分類與 ETag ────────────────────────────────────────────────

    [Fact]
    public void 只有200與304與404算完成()
    {
        ApiOutcome[] completed = [ApiOutcome.Ok, ApiOutcome.NotModified, ApiOutcome.NotFound];

        foreach (var outcome in Enum.GetValues<ApiOutcome>())
        {
            Assert.Equal(completed.Contains(outcome), UpdateCheckRules.IsCompleted(outcome));
        }
    }

    [Fact]
    public void 沒有上次檢查時間不送ETag()
    {
        Assert.False(UpdateCheckRules.ShouldSendEtag(null));
        Assert.True(UpdateCheckRules.ShouldSendEtag(Now));
    }

    // ── 是否通知 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.2.0", "v0.3.0", true)]
    [InlineData("1.9.0", "v1.10.0", true)]
    [InlineData("0.2.0", "v0.2.0", false)]
    [InlineData("0.2.0", "v0.1.0", false)]
    [InlineData("1.10.0", "v1.9.0", false)]
    [InlineData("0.3.0-beta.1", "v0.3.0", true)]
    [InlineData("0.3.0-beta.1", "v0.3.0-beta.2", true)]
    [InlineData("0.3.0-beta.2", "v0.3.0-beta.1", false)]
    [InlineData("0.3.0", "v0.3.0-rc.1", false)]
    [InlineData("0.3.0-beta.1", "v0.3.0-beta.1+abc", false)]
    public void 決定通知的各種情況(string current, string tag, bool notify)
    {
        var decision = UpdateCheckRules.Decide(Version(current), new ReleaseInfo(tag, ReleaseUrl), null);

        Assert.Equal(notify ? UpdateDecisionKind.Notify : UpdateDecisionKind.NoUpdate, decision.Kind);
        Assert.Equal(notify, decision.Notice is not null);
    }

    [Fact]
    public void 新版的通知帶html_url與不含v和build的版本()
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.3.0+abc", ReleaseUrl), null);

        Assert.Equal(new UpdateNotice("0.3.0", new Uri(ReleaseUrl)), decision.Notice);
    }

    [Fact]
    public void 已通知過的版本不再通知()
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.3.0", ReleaseUrl), "0.3.0");

        Assert.Equal(new UpdateDecision(UpdateDecisionKind.AlreadyNotified, null), decision);
    }

    [Fact]
    public void 已通知但有更新的版本照樣通知()
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.4.0", ReleaseUrl), "0.3.0");

        Assert.Equal(UpdateDecisionKind.Notify, decision.Kind);
        Assert.Equal("0.4.0", decision.Notice!.Version);
    }

    [Fact]
    public void 已通知的版本比最新版新時_回退到的較舊版本不再通知()
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.3.0", ReleaseUrl), "0.4.0");

        Assert.Equal(new UpdateDecision(UpdateDecisionKind.AlreadyNotified, null), decision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v0.3.0")]
    public void 已通知版本解析不了當成沒通知過(string stored)
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.3.0", ReleaseUrl), stored);

        Assert.Equal(UpdateDecisionKind.Notify, decision.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("V0.3.0")]
    public void tag解析不了或缺少是BadTag(string? tag)
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo(tag, ReleaseUrl), null);

        Assert.Equal(new UpdateDecision(UpdateDecisionKind.BadTag, null), decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0")]
    [InlineData("https://github.com.evil.example/marvin3991/is-it-green-yet/releases/tag/v0.3.0")]
    [InlineData("https://evil@github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0")]
    [InlineData("https://github.com:8443/marvin3991/is-it-green-yet/releases/tag/v0.3.0")]
    public void html_url不允許不通知(string? htmlUrl)
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo("v0.3.0", htmlUrl), null);

        Assert.Equal(new UpdateDecision(UpdateDecisionKind.BadUrl, null), decision);
    }

    [Theory]
    [InlineData("latest", null, nameof(UpdateDecisionKind.BadTag))]
    [InlineData("v0.2.0", null, nameof(UpdateDecisionKind.NoUpdate))]
    [InlineData("v0.3.0", "0.3.0", nameof(UpdateDecisionKind.AlreadyNotified))]
    public void 排在網址之前的判斷不看html_url(string tag, string? notified, string expected)
    {
        var decision = UpdateCheckRules.Decide(Version("0.2.0"), new ReleaseInfo(tag, "http://evil.example/"), notified);

        Assert.Equal(expected, decision.Kind.ToString());
    }

    private static SemVersion Version(string text)
    {
        Assert.True(SemVersion.TryParse(text, out var version), text);
        return version;
    }
}

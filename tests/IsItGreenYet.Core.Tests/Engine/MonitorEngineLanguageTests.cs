using System.Globalization;
using System.Net;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>
/// Engine 的畫面文字用設定裡的語言（不看共用的目前語言、也不看執行緒的文化）；
/// 設定頁換語言（SettingsChanged）之後，快照、橫幅、問題標示、提示與操作結果都換成新的語言。
/// </summary>
public class MonitorEngineLanguageTests
{
    private static readonly DateTimeOffset Start = EngineHarness.Start;

    [Fact]
    public async Task 英文設定的快照橫幅與系統匣原因是英文()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.Language = AppLanguage.English;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1)));
        });
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Gone, "API version is no longer supported."));

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(BannerKind.VersionError, h.Last.Banner?.Kind);
        Assert.Equal("This GitHub API version is no longer supported. Please update the app.", h.Last.Banner?.Text);
        Assert.Equal("App outdated", h.Last.TrayIssueLine);
        Assert.Equal("Failed", Assert.Single(h.Last.Rows).State.Label);
    }

    [Fact]
    public async Task 英文設定的主額度橫幅與限流原因是英文()
    {
        await using var h = EngineHarness.Create(h => h.Settings.Language = AppLanguage.English);
        await h.DrainAsync();
        PrimaryLimit(h);

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(BannerKind.PrimaryLimit, h.Last.Banner?.Kind);
        Assert.Equal("API rate limit used up. Expected to reset at 10:10.", h.Last.Banner?.Text);
        Assert.Equal("Rate limited, retry 10:10", h.Last.TrayIssueLine);
    }

    [Fact]
    public async Task 英文設定的權限問題標示與提示是英文()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.Language = AppLanguage.English;
            h.Main.Runs.Add(running);
        });
        await h.DrainAsync();
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal("Failed: octo-org/sample-app · CI #42", Assert.Single(h.Toasts).Title);

        h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "pull_requests=read")));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.Equal("Missing the Pull requests permission, or not accessible", h.RepoRow(FakeData.RepoId).ProblemNote);
    }

    [Fact]
    public async Task 共用的目前語言不影響Engine()
    {
        // 測試組件的共用語言是繁體中文；Engine 只看自己的設定（英文）。
        Assert.Equal(AppLanguage.TraditionalChinese, UiText.Current);
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.Language = AppLanguage.English;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "queued", null, Start.AddMinutes(-1), Start));
        });

        await h.DrainAsync();

        Assert.Equal("Queued", Assert.Single(h.Last.Rows).State.Label);
    }

    [Fact]
    public async Task 換語言後一次性提示換成新的文字並保留識別碼()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        h.GitHub.AddHeader(EngineHarness.ProbeAPath, "Deprecation", "@1798761600");
        await h.AdvanceAsync(Tuning.ProbeInterval);
        var chinese = h.Last.Banner;
        Assert.Equal("GitHub API 版本即將停止支援，請留意程式更新。", chinese?.Text);
        Assert.NotEqual(0, chinese?.NoticeId);

        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { Language = AppLanguage.English }));
        await h.StepAsync();

        var english = h.Last.Banner;
        Assert.Equal(BannerKind.Deprecated, english?.Kind);
        Assert.Equal("This GitHub API version will soon stop being supported. Watch for an app update.", english?.Text);
        // App 用識別碼記住使用者關掉的是哪一則：換語言不能讓關掉的提示又冒出來。
        Assert.Equal(chinese?.NoticeId, english?.NoticeId);

        // 換回中文：同一則提示，文字回到中文。
        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { Language = AppLanguage.TraditionalChinese }));
        await h.StepAsync();
        Assert.Equal(chinese?.Text, h.Last.Banner?.Text);
        Assert.Equal(chinese?.NoticeId, h.Last.Banner?.NoticeId);
    }

    [Fact]
    public async Task 換語言後全域暫停的橫幅換成新的文字並保留恢復時間()
    {
        await using var h = EngineHarness.Create();
        await h.DrainAsync();
        PrimaryLimit(h);
        await h.AdvanceAsync(Tuning.ProbeInterval);
        var chinese = h.Last.Banner;
        Assert.Equal("主額度用完，預計 10:10 恢復。", chinese?.Text);

        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { Language = AppLanguage.English }));
        await h.StepAsync();

        Assert.Equal(BannerKind.PrimaryLimit, h.Last.Banner?.Kind);
        Assert.Equal("API rate limit used up. Expected to reset at 10:10.", h.Last.Banner?.Text);
        Assert.Equal(chinese?.Until, h.Last.Banner?.Until);
        Assert.Equal("Rate limited, retry 10:10", h.Last.TrayIssueLine);
    }

    [Fact]
    public async Task 換語言後問題標示與清單的狀態換成新的語言()
    {
        await using var h = EngineHarness.Create(h =>
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "timed_out", Start.AddDays(-1), Start.AddDays(-1))));
        await h.DrainAsync();
        h.GitHub.Override(EngineHarness.PullsPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by integration",
            ("X-Accepted-GitHub-Permissions", "pull_requests=read")));
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.Equal("缺少 Pull requests 權限或無法存取", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal("失敗（逾時）", Assert.Single(h.Last.Rows).State.Label);

        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { Language = AppLanguage.English }));
        await h.StepAsync();

        Assert.Equal("Missing the Pull requests permission, or not accessible", h.RepoRow(FakeData.RepoId).ProblemNote);
        Assert.Equal("Missing the Pull requests permission, or not accessible", h.Cache.GetRepoStates()[FakeData.RepoId].ProblemNote);
        Assert.Equal("Failed (timed out)", Assert.Single(h.Last.Rows).State.Label);
    }

    [Fact]
    public async Task 換語言前排進合併視窗的提示用新的語言送出()
    {
        var running = FakeData.Run(9001, 42, "in_progress", null, Start.AddMinutes(-1), Start);
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(running));
        await h.DrainAsync();
        FakeData.Complete(running, "failure", h.Clock.GetUtcNow());
        await h.AdvanceAsync(Tuning.ActiveRepoInterval);
        Assert.Empty(h.Toasts);

        // 完成觀察時是中文；合併視窗還沒過就換成英文。
        h.Engine.Post(new EngineCommand.SettingsChanged(new AppSettings { Language = AppLanguage.English }));
        await h.AdvanceAsync(Tuning.NotificationMergeWindow);

        Assert.Equal("Failed: octo-org/sample-app · CI #42", Assert.Single(h.Toasts).Title);
    }

    [Fact]
    public async Task 英文設定的複製失敗log結果是英文()
    {
        await using var h = EngineHarness.Create(h =>
        {
            h.Settings.Language = AppLanguage.English;
            h.Main.Runs.Add(FakeData.Run(9001, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1)));
        });
        await h.DrainAsync();

        // 沒有任何 job（例如 startup_failure）：spec §10.9 第 4 點的訊息。
        var copy = await h.Engine.CopyFailedLogAsync(FakeData.RepoId, 9001, CancellationToken.None);

        Assert.False(copy.Success);
        Assert.Equal("This run has no failed job logs to copy.", copy.Message);
    }

    [Fact]
    public async Task 英文設定的檢查連線結果是英文()
    {
        await using var h = EngineHarness.Create(h => h.Settings.Language = AppLanguage.English);
        await h.DrainAsync();

        var result = await h.Engine.CheckConnectionAsync(CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("1 repo visible (0 private)", result.Message);
    }

    /// <summary>下一個探測回主額度用完（reset 在 600 秒後），只回這一次。</summary>
    private static void PrimaryLimit(EngineHarness h) =>
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded for user ID 1001.",
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-used", "5000"),
            ("x-ratelimit-reset", h.GitHub.ResetEpoch(TimeSpan.FromSeconds(600)).ToString(CultureInfo.InvariantCulture))),
            times: 1);
}

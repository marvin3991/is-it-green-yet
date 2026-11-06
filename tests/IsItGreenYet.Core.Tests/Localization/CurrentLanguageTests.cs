using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tests.Auth;
using IsItGreenYet.Core.Tests.TestSupport;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Localization;

/// <summary>
/// 沒有語言參數的多載（App 用的）跟著共用的目前語言（<see cref="UiText.Current"/>）：App 切換語言後，下一次產生的文字就是新的語言。
/// 這裡會改共用的值，所以放在不平行的集合，每個測試結束時改回繁體中文。
/// </summary>
[Collection(CurrentLanguageCollection.Name)]
public sealed class CurrentLanguageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    public CurrentLanguageTests() => UiText.Current = AppLanguage.English;

    public void Dispose() => UiText.Current = AppLanguage.TraditionalChinese;

    [Fact]
    public void Core各單元沒有語言參數的多載跟著目前語言()
    {
        var formatter = new DisplayFormatter(new ManualTimeProvider(Now));
        var summary = new StatusSummary(OverallState.Failure, 1, 2, 0, ConnectionIssue.None);

        Assert.Equal("Failed (timed out)", RunStateMapper.Map("completed", "timed_out").Label);
        Assert.Equal("Just now", formatter.StartTime(Now));
        Assert.Equal("Queued 5s", DisplayFormatter.Queued(TimeSpan.FromSeconds(5)));
        Assert.Equal("Scheduled", DisplayFormatter.Trigger(new RunInfo(
            1, 1, 1, "CI", ".github/workflows/ci.yml", 1, 1, "t", "schedule", "completed", "success", "main", "sha", 1, "o/r", null, 1, "alice", "alice", [],
            Now, Now, Now, "https://github.com/o/r/actions/runs/1"), null).Text);
        Assert.Equal("IsItGreenYet\n1 in progress · 2 failed · Not updated yet", StatusAggregator.TrayTooltip(summary, null, null));
        Assert.Equal("Archived", RepoDiscovery.SkipReasonText(SkipReason.Archived));
    }

    [Fact]
    public async Task token的原因與提示跟著目前語言()
    {
        var store = new FakeCredentialStore();
        Assert.Equal("No PAT saved", (await new PatTokenProvider(store).GetTokenAsync(CancellationToken.None)).Problem);

        var time = new ManualTimeProvider(Now);
        var queue = new NotificationQueue(time);
        var run = new RunInfo(
            9, 1, 1, "CI", ".github/workflows/ci.yml", 3, 1, "t", "push", "completed", "success", "main", "sha", 1, "o/r", null, 1, "alice", "alice", [],
            Now, Now, Now, "https://github.com/o/r/actions/runs/9");
        queue.Add([new PlannedNotification(run, RunStateMapper.Map(run), "o/r", NotificationKind.MyRunSucceeded)]);
        time.Advance(Tuning.NotificationMergeWindow);

        Assert.Equal("Succeeded: o/r · CI #3", queue.TryFlush(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))?.Title);
    }

    [Fact]
    public void 建立資料夾失敗的訊息跟著目前語言()
    {
        using var workspace = new TempWorkspace();

        // 同名的檔案擋住資料夾：Directory.CreateDirectory 丟 IOException。
        var blocked = workspace.PathTo("blocked");
        File.WriteAllText(blocked, string.Empty);
        var paths = new AppPaths(blocked);

        var english = Assert.Throws<InvalidOperationException>(paths.EnsureCreated);
        var chinese = Assert.Throws<InvalidOperationException>(() => paths.EnsureCreated(AppLanguage.TraditionalChinese));

        Assert.StartsWith($"Couldn't create the folder \"{blocked}\", so the app can't save data. Reason: ", english.Message, StringComparison.Ordinal);
        Assert.StartsWith($"無法建立資料夾「{blocked}」，程式無法儲存資料。原因：", chinese.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(english.InnerException);
    }

    [Fact]
    public void 不認得的語言當成預設值()
    {
        UiText.Current = (AppLanguage)99;

        Assert.Equal(AppLanguage.TraditionalChinese, UiText.Current);
    }
}

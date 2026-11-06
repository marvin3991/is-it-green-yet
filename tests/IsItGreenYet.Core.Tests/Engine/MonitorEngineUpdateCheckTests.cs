using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>引擎裡的檢查新版本：什麼時候送、結果怎麼處理、對其他狀態完全沒有影響。</summary>
public class MonitorEngineUpdateCheckTests
{
    private const string ReleasePath = EngineHarness.ReleasePath;
    private const string ReleaseApiUrl = FakeGitHub.ApiBase + ReleasePath;
    private const string ReleasePage = "https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0";
    private const string ReleaseETag = "W/\"release-etag\"";

    /// <summary>error.log 每一筆的開頭是 <c>[時間] 標題</c>。</summary>
    private const string LogEntryTitle = "] 檢查新版本";

    private const string CurrentVersion = "0.2.0";
    private const string NewTag = "v0.3.0";
    private const string NewVersion = "0.3.0";
    private const string NoticeTitle = "有新版本：0.3.0";

    private const string NetworkFailure = "網路錯誤";
    private const string Status429 = "429";
    private const string PrimaryLimit = "403限流";
    private const string Status401 = "401";
    private const string Status403 = "403";
    private const string Status500 = "500";
    private const string BrokenJson = "JSON壞掉";
    private const string Status400 = "400";

    private const string InjectedMessage = "模擬的例外";

    /// <summary>E1、E10 之後就輪到檢查；只是防止測試寫錯時無限迴圈。</summary>
    private const int MaxStepsUntilCheck = 10;

    /// <summary>清單失敗後的重試時間由引擎決定；只是防止測試寫錯時無限迴圈。</summary>
    private const int MaxMinutesUntilListRetried = 30;

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    // 刻意寫字面值，不用 Tuning：這兩個數字是需求。
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Window = Tuning.NotificationMergeWindow;
    private static readonly TimeSpan SleepGap = Tuning.SleepGapThreshold + OneSecond;

    /// <summary>比每小時的判斷長：暫停期間一定會有一次判斷。</summary>
    private static readonly TimeSpan LongPause = OneHour + TimeSpan.FromMinutes(30);

    // ── 選項關閉 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 選項關閉時啟動喚醒解鎖與過3天都不送請求()
    {
        await using var h = Create(h => h.Settings.CheckForUpdates = false);

        await h.DrainAsync();
        await SuspendAndResumeAsync(h);
        await LockAndUnlockAsync(h);
        await TickAcrossSleepGapAsync(h);
        await h.AdvanceInStepsAsync(ThreeDays + OneHour, TimeSpan.FromHours(6));

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Null(h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 檢查進行中關閉選項_結果丟棄()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ =>
        {
            h.Settings.CheckForUpdates = false;
            return Release();
        }));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastUpdateCheckUtc);
        Assert.Null(h.Settings.LastNotifiedVersion);
        Assert.Null(h.Cache.Get(ReleaseApiUrl));
    }

    [Fact]
    public async Task 檢查進行中關閉選項_失敗也不算嘗試()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ =>
        {
            h.Settings.CheckForUpdates = false;
            return Failure(h, Status500);
        }));

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(0, LogEntries(h));

        h.GitHub.RemoveOverrides(ReleasePath);
        h.Settings.CheckForUpdates = true;
        await SuspendAndResumeAsync(h);

        // 沒有留下失敗時間，不必等 1 小時。
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 檢查進行中關閉選項_例外也不算嘗試()
    {
        await using var h = Create(h => h.GitHub.Override(
            ReleasePath,
            _ =>
            {
                h.Settings.CheckForUpdates = false;
                throw new InvalidOperationException(InjectedMessage);
            },
            times: 1));

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        h.Settings.CheckForUpdates = true;
        await SuspendAndResumeAsync(h);

        // 沒有留下失敗時間，不必等 1 小時。
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 通知排隊中關閉選項_不顯示也不記已通知()
    {
        await using var h = Create(h => ServeRelease(h));

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        h.Settings.CheckForUpdates = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.AdvanceAsync(Window);

        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastNotifiedVersion);
        Assert.Null(h.Cache.Get(ReleaseApiUrl));

        // 那次檢查在關閉之前就完成了。
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 輪詢請求進行中關閉選項_排隊中的提示不送出也不記已通知()
    {
        await using var h = Create(h => ServeRelease(h));

        // E1、E10、檢查都送完，提示在合併視窗裡。下一步是輪詢，它已經過了指令處理。
        await h.StepAsync();
        await h.StepAsync();
        await h.StepAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        h.GitHub.OnRequest = request =>
        {
            if (request.RequestUri!.PathAndQuery == EngineHarness.ListAPath)
            {
                h.GitHub.OnRequest = null;
                h.Clock.Manual.Advance(Window);
                h.Settings.CheckForUpdates = false;
                h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
            }
        };

        await h.StepAsync();

        Assert.False(h.Settings.CheckForUpdates);
        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastNotifiedVersion);
        Assert.Null(h.Cache.Get(ReleaseApiUrl));

        await h.DrainAsync();
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public async Task 已判斷到期還沒送時關閉選項_不送()
    {
        await using var h = Create();

        // 第一步驗證，第二步判斷到期但先送 E10。設定頁先改物件才送指令，所以這裡只改物件。
        await h.StepAsync();
        await h.StepAsync();
        h.Settings.CheckForUpdates = false;
        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 已判斷到期還沒送時關閉又打開_要等下一次每小時判斷()
    {
        await using var h = Create();

        // 第一步驗證，第二步判斷到期但先送 E10。
        await h.StepAsync();
        await h.StepAsync();
        h.Settings.CheckForUpdates = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.StepAsync();

        h.Settings.CheckForUpdates = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 關閉時已到期的判斷也不送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        // 到期了，但同一步裡判斷之前就關掉：指令先處理，判斷看到的已經是關閉。
        JumpWallClock(h, ThreeDays);
        h.Settings.CheckForUpdates = false;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await SuspendAndResumeAsync(h);

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 重新開啟選項後由每小時判斷接手()
    {
        await using var h = Create(h => h.Settings.CheckForUpdates = false);
        await h.DrainAsync();

        h.Settings.CheckForUpdates = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    // ── 時機 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 沒有token時不送_登入後才送()
    {
        await using var h = Create(
            h => h.Tokens.Returns((string?)null).Returns(Fixture.FakeToken),
            defaultToken: false);

        await h.DrainAsync();
        Assert.Empty(h.GitHub.Handler.Requests);

        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task E1驗證成功後才送()
    {
        await using var h = Create();

        await h.DrainAsync();

        Assert.Equal(["/user", "/rate_limit", ReleasePath], h.GitHub.ApiRequests.Take(3));
    }

    [Fact]
    public async Task 驗證中不送也不算嘗試()
    {
        await using var h = Create(h => h.GitHub.Override("/user", _ => h.GitHub.Error(HttpStatusCode.InternalServerError, "boom")));

        await h.AdvanceInStepsAsync(OneHour * 2, TimeSpan.FromMinutes(10));
        Assert.True(h.GitHub.Count("/user") > 1);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Equal(0, LogEntries(h));

        h.GitHub.RemoveOverrides("/user");
        await h.AdvanceInStepsAsync(OneHour, TimeSpan.FromMinutes(5));

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 未到期時啟動不送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start.AddDays(-2));

        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Equal(Start.AddDays(-2), h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 滿3天的啟動會送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start - ThreeDays);

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 每小時判斷不連網_滿3天才再送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        await h.AdvanceInStepsAsync(ThreeDays - OneHour, OneHour);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 喚醒時判斷一次()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        // 只推進牆鐘：每小時的判斷還沒到，只有喚醒會判斷。
        h.Engine.Post(new EngineCommand.Suspend());
        await h.DrainAsync();
        JumpWallClock(h, ThreeDays);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 睡眠中不送_每小時判斷到期也一樣()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.Suspend());
        await h.StepAsync();

        var before = h.GitHub.Handler.Requests.Count;
        await h.AdvanceInStepsAsync(ThreeDays + OneHour * 2, OneHour);
        Assert.Equal(before, h.GitHub.Handler.Requests.Count);
        Assert.Equal(0, LogEntries(h));

        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();

        // 睡眠中的跳過不算嘗試：喚醒就送，不必等 1 小時。
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 判斷之後才進入睡眠_不會在睡眠中送出()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start - ThreeDays);

        // 第一步驗證、第二步 E10、都還沒輪到檢查時就進入睡眠。
        await h.StepAsync();
        await h.StepAsync();
        h.Engine.Post(new EngineCommand.Suspend());
        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 無人值守喚醒靠Tick間隔判斷()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        JumpWallClock(h, ThreeDays);
        Assert.Equal(0, await h.DrainAsync());
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await TickAcrossSleepGapAsync(h);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 解鎖時判斷一次()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetLocked(true));
        await h.DrainAsync();
        JumpWallClock(h, ThreeDays);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        h.Engine.Post(new EngineCommand.SetLocked(false));
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 牆鐘沒到期時單調時鐘前進也不送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();

        h.Clock.Manual.AdvanceMonotonic(TimeSpan.FromDays(4));
        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 時鐘調回時上次檢查在未來就送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start.AddDays(1));

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);

        // 執行中也一樣：剛完成一次之後時鐘被調回。
        h.Clock.Manual.SetUtcNow(Start.AddDays(-1));
        await SuspendAndResumeAsync(h);

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 節流視窗滿時到期的檢查等放行才送()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start);
        await h.DrainAsync();
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);

        JumpWallClock(h, ThreeDays);
        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);

        // 節流視窗暫滿不算跳過：不等下一次每小時判斷，放行就送。
        await h.AdvanceAsync(Tuning.RequestWindow);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 跟隨301轉址的檢查也計入每分鐘上限()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => FakeHttpHandler.Status(
            HttpStatusCode.MovedPermanently,
            new Dictionary<string, string> { ["Location"] = FakeGitHub.ApiBase + "/repos/marvin3991/renamed/releases/latest" })));
        await h.DrainAsync();

        // GitHubClient 照 Location 再送一次，實際是兩個請求。
        await MonitorEngineReviewTests.FillRequestWindowAsync(h);

        Assert.Contains(h.GitHub.Responses, response => response.Status == (int)HttpStatusCode.MovedPermanently);
        Assert.Equal(Tuning.MaxRequestsPerMinute, h.GitHub.ApiRequests.Count);
    }

    [Fact]
    public async Task 請求進行中程式結束_不算嘗試也不寫log()
    {
        using var cts = new CancellationTokenSource();
        await using var h = Create(h =>
        {
            h.GitHub.AbortWhenCancelled = true;
            h.GitHub.Override(ReleasePath, _ =>
            {
                cts.Cancel();
                return Release();
            });
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            for (var step = 0; step < MaxStepsUntilCheck; step++)
            {
                await h.Engine.RunOnceAsync(cts.Token);
            }
        });

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(0, LogEntries(h));
        Assert.Null(h.Settings.LastUpdateCheckUtc);
    }

    // ── 失敗 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NetworkFailure)]
    [InlineData(Status429)]
    [InlineData(PrimaryLimit)]
    [InlineData(Status401)]
    [InlineData(Status403)]
    [InlineData(Status500)]
    [InlineData(BrokenJson)]
    [InlineData(Status400)]
    public async Task 失敗後1小時內不重試_滿1小時重試(string failure)
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => Failure(h, failure)));

        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.AdvanceInStepsAsync(OneHour - OneSecond, TimeSpan.FromMinutes(10));
        await SuspendAndResumeAsync(h);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneSecond);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Theory]
    [InlineData(NetworkFailure)]
    [InlineData(Status429)]
    [InlineData(PrimaryLimit)]
    [InlineData(Status401)]
    [InlineData(Status403)]
    [InlineData(Status500)]
    [InlineData(BrokenJson)]
    [InlineData(Status400)]
    public async Task 失敗不更新上次檢查時間(string failure)
    {
        var last = Start - ThreeDays;
        await using var h = Create(h =>
        {
            h.Settings.LastUpdateCheckUtc = last;
            h.GitHub.Override(ReleasePath, _ => Failure(h, failure));
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(last, h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 解鎖與喚醒不會提早重試()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500)));
        await h.DrainAsync();

        await h.AdvanceAsync(TimeSpan.FromMinutes(10));
        await LockAndUnlockAsync(h);
        await SuspendAndResumeAsync(h);
        await TickAcrossSleepGapAsync(h);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        // 解鎖與喚醒不推遲每小時的判斷：從失敗起算滿 1 小時就重試。
        await h.AdvanceAsync(OneHour - TimeSpan.FromMinutes(10) - SleepGap);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 解鎖觸發的失敗_下一次判斷正好在滿1小時()
    {
        var thirtyMinutes = TimeSpan.FromMinutes(30);
        await using var h = Create(h =>
        {
            h.Settings.LastUpdateCheckUtc = Start - ThreeDays + thirtyMinutes;
            h.GitHub.Override(ReleasePath, _ => Failure(h, Status500));
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(thirtyMinutes);
        await LockAndUnlockAsync(h);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        // 每小時的節奏從失敗起重算。沒有重算的話，失敗後 30 分鐘會有一次判斷（還不到下限），下一次要再等 1 小時。
        await h.AdvanceInStepsAsync(OneHour, thirtyMinutes);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 時鐘調回也不會提早重試()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500)));
        await h.DrainAsync();

        // 失敗時間變成在未來：夾到現在，從現在起再等 1 小時。
        h.Clock.Manual.SetUtcNow(Start.AddHours(-5));
        await SuspendAndResumeAsync(h);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour - OneSecond);
        await SuspendAndResumeAsync(h);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneSecond);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 離線時跳過_恢復後解鎖也要等滿1小時()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start.AddDays(-2));
        await h.DrainAsync();

        h.GitHub.NetworkDown = true;
        await h.AdvanceInStepsAsync(Tuning.ProbeInterval * (Tuning.OfflineThreshold + 1), Tuning.ProbeInterval);
        Assert.True(h.Last.Offline);

        // 到期了，但引擎知道自己離線：不送，算一次嘗試。
        JumpWallClock(h, TimeSpan.FromDays(1));
        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        h.GitHub.NetworkDown = false;
        h.Engine.Post(new EngineCommand.NetworkAvailable());
        await h.DrainAsync();
        Assert.False(h.Last.Offline);

        await LockAndUnlockAsync(h);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 全域暫停時跳過_1小時後才再判斷()
    {
        var pause = TimeSpan.FromSeconds(600);
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start.AddDays(-2));
        await h.DrainAsync();

        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimitResponse(h, pause), times: 1);
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));

        // 到期了，但全域暫停中：不送，算一次嘗試。
        JumpWallClock(h, TimeSpan.FromDays(1));
        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        var waited = pause + Tuning.RateLimitResetMargin + Tuning.ProbeInterval;
        await h.AdvanceInStepsAsync(waited, Tuning.ProbeInterval);
        Assert.False(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));

        await LockAndUnlockAsync(h);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(OneHour - waited);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 未登入跳過不算嘗試_重新登入立刻判斷()
    {
        await using var h = Create(
            h =>
            {
                // PAT 模式沒有定期重新檢查，token 只在按「重新檢查」時才取。
                h.Settings.AuthMode = AuthMode.Pat;
                h.Tokens.Returns((string?)null).Returns(Fixture.FakeToken);
            },
            defaultToken: false);

        await h.AdvanceInStepsAsync(OneHour * 2, TimeSpan.FromMinutes(30));
        Assert.Empty(h.GitHub.Handler.Requests);
        Assert.Equal(0, LogEntries(h));

        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 暫停中換token_驗證中的跳過不算嘗試()
    {
        await using var h = Create(h =>
        {
            h.Settings.AuthMode = AuthMode.Pat;
            h.Settings.LastUpdateCheckUtc = Start.AddDays(-2);
        });
        await h.DrainAsync();
        await EnterGlobalPauseAsync(h);

        // 狀態仍是 Ok，但 E1 要等暫停結束才送。
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);

        await PassHourlyEvaluationDuringPauseAsync(h);

        // 驗證成功就送，不必從那次跳過起再等 1 小時。
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 暫停中被登出_未登入的跳過不算嘗試()
    {
        await using var h = Create(h =>
        {
            h.Settings.AuthMode = AuthMode.Pat;
            h.Settings.LastUpdateCheckUtc = Start.AddDays(-2);
        });
        await h.DrainAsync();
        await EnterGlobalPauseAsync(h);

        h.Tokens.Returns((string?)null);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();
        Assert.NotEqual(AuthStatus.Ok, h.Last.Auth);

        await PassHourlyEvaluationDuringPauseAsync(h);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        h.Tokens.Returns(Fixture.FakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 暫停中進入睡眠_睡眠中的跳過不算嘗試()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start.AddDays(-2));
        await h.DrainAsync();
        await EnterGlobalPauseAsync(h);

        h.Engine.Post(new EngineCommand.Suspend());
        await h.StepAsync();
        await PassHourlyEvaluationDuringPauseAsync(h);
        Assert.Equal(0, h.GitHub.Count(ReleasePath));

        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();

        Assert.False(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 處理結果丟例外後解鎖也要等滿1小時()
    {
        await using var h = Create(h =>
        {
            ServeRelease(h);
            ThrowOnceFrom(h, "ApplyUpdateCheckResult");
        });
        await h.DrainAsync();

        await LockAndUnlockAsync(h);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        // 確認例外真的丟了：沒丟的話檢查算完成，上面的次數也是 1。
        Assert.Contains(InjectedMessage, h.ErrorLogText, StringComparison.Ordinal);
        Assert.Null(h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 完成時清掉失敗時間_之後時鐘調回照樣到期()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500), times: 1));
        await h.DrainAsync();

        // 第二次沒有 Override，FakeGitHub 回 404，算完成。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(Start + OneHour, h.Settings.LastUpdateCheckUtc);

        // 調回到失敗之前：上次檢查在未來就到期；舊的失敗時間還留著的話，它也在未來，會先走冷卻。
        h.Clock.Manual.SetUtcNow(Start - TimeSpan.FromMinutes(1));
        await SuspendAndResumeAsync(h);

        Assert.Equal(3, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 失敗時間只在記憶體_重開後不必等1小時()
    {
        await using var h = Create(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500)));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.RestartAsync();
        await h.DrainAsync();

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
    }

    // ── 只通知一次 ────────────────────────────────────────────────────

    [Fact]
    public async Task 新版只通知一次_點擊網址是html_url()
    {
        await using var h = Create(h => ServeRelease(h));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        var toast = Assert.Single(h.Toasts);
        Assert.Equal(NoticeTitle, toast.Title);
        Assert.Equal(new Uri(ReleasePage), toast.OpenUrl);
        Assert.Equal(new Uri(ReleasePage), Assert.Single(toast.Lines).Url);
        Assert.Equal(NewVersion, h.Settings.LastNotifiedVersion);

        await h.AdvanceInStepsAsync(ThreeDays, TimeSpan.FromHours(6));
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        await h.AdvanceAsync(Window);

        Assert.Single(h.Toasts);
    }

    [Fact]
    public async Task 同一版本重啟後不再通知()
    {
        await using var h = Create(h => ServeRelease(h));
        await h.DrainAsync();
        await h.AdvanceAsync(Window);
        Assert.Single(h.Toasts);

        await h.RestartAsync();
        await h.DrainAsync();
        await h.AdvanceAsync(ThreeDays);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        await h.AdvanceAsync(Window);

        Assert.Single(h.Toasts);
    }

    [Fact]
    public async Task 已通知過但有更新的版本照樣通知()
    {
        await using var h = Create(h =>
        {
            h.Settings.LastNotifiedVersion = NewVersion;
            ServeRelease(h, tag: "v0.4.0", url: "https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.4.0");
        });

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal("有新版本：0.4.0", Assert.Single(h.Toasts).Title);
        Assert.Equal("0.4.0", h.Settings.LastNotifiedVersion);
    }

    [Fact]
    public async Task 最新release回退時已通知過的版本不再通知()
    {
        const string NewerTag = "v0.4.0";
        const string NewerPage = "https://github.com/marvin3991/is-it-green-yet/releases/tag/v0.4.0";
        await using var h = Create(h => ServeRelease(h));
        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        ServeRelease(h, tag: NewerTag, url: NewerPage);
        await h.AdvanceAsync(ThreeDays);
        await h.AdvanceAsync(Window);
        Assert.Equal([NoticeTitle, "有新版本：0.4.0"], h.Toasts.Select(t => t.Title));

        // v0.4.0 被刪掉，/releases/latest 回到通知過的 v0.3.0。
        ServeRelease(h);
        await h.AdvanceAsync(ThreeDays);
        await h.AdvanceAsync(Window);

        Assert.Equal(3, h.GitHub.Count(ReleasePath));
        Assert.Equal(2, h.Toasts.Count);
        Assert.Equal("0.4.0", h.Settings.LastNotifiedVersion);
    }

    [Fact]
    public async Task 提示交出後App來不及存已通知版本_重開到期後照樣通知()
    {
        // App 結束的瞬間：提示與存檔都排進 UI 執行緒就被丟棄，引擎看不出來。這時 ETag 如果存了，下次回 304，這個版本就再也不會通知。
        await using var h = Create(ServeReleaseHonoringETag);
        await h.DrainAsync();
        await h.AdvanceAsync(Window);
        Assert.Single(h.Toasts);

        h.Settings.LastNotifiedVersion = null;
        await h.RestartAsync();
        await h.DrainAsync();
        await h.AdvanceAsync(ThreeDays);
        await h.AdvanceAsync(Window);

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(2, h.Toasts.Count);
        Assert.Equal(NewVersion, h.Settings.LastNotifiedVersion);
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("v0.1.9")]
    [InlineData("v0.2.0-rc.1")]
    public async Task 相同或較舊版本不通知(string tag)
    {
        await using var h = Create(h => ServeRelease(h, tag));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Empty(h.Toasts);
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Null(h.Settings.LastNotifiedVersion);
    }

    [Fact]
    public async Task _200當下就更新上次檢查時間()
    {
        await using var h = Create(h => ServeRelease(h));

        await h.DrainAsync();

        Assert.Empty(h.Toasts);
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 通知送出之後才記已通知_ETag等下一次200才存()
    {
        await using var h = Create(h => ServeRelease(h));

        await h.DrainAsync();
        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastNotifiedVersion);
        Assert.Null(h.Cache.Get(ReleaseApiUrl));

        await h.AdvanceAsync(Window);

        Assert.Single(h.Toasts);
        Assert.Equal(NewVersion, h.Settings.LastNotifiedVersion);
        Assert.Null(h.Cache.Get(ReleaseApiUrl));

        // 下一次回 200 時已經通知過，這時才存；再下一次就帶著它送。
        await h.AdvanceAsync(ThreeDays);
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);
        Assert.Single(h.Toasts);

        await h.AdvanceAsync(ThreeDays);
        Assert.Equal(ReleaseETag, ReleaseRequests(h)[^1].Header("If-None-Match"));
    }

    [Fact]
    public async Task 鎖定時收到200_解鎖才通知()
    {
        await using var h = Create(h => ServeRelease(h));
        h.Engine.Post(new EngineCommand.SetLocked(true));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Empty(h.Toasts);
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Null(h.Settings.LastNotifiedVersion);

        h.Engine.Post(new EngineCommand.SetLocked(false));
        await h.DrainAsync();

        Assert.Equal(NoticeTitle, Assert.Single(h.Toasts).Title);
        Assert.Equal(NewVersion, h.Settings.LastNotifiedVersion);
    }

    [Fact]
    public async Task 鎖定中程式結束_到期後再通知()
    {
        await using var h = Create(h => ServeRelease(h));
        h.Engine.Post(new EngineCommand.SetLocked(true));
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.RestartAsync();
        await h.DrainAsync();
        await h.AdvanceAsync(Window);
        Assert.Empty(h.Toasts);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        await h.AdvanceAsync(ThreeDays);
        await h.AdvanceAsync(Window);

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Null(ReleaseRequests(h)[^1].Header("If-None-Match"));
        Assert.Equal(NoticeTitle, Assert.Single(h.Toasts).Title);
    }

    [Fact]
    public async Task tag解析不了算完成_寫errorlog()
    {
        await using var h = Create(h => ServeRelease(h, tag: "latest"));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Empty(h.Toasts);
        Assert.Equal(1, LogEntries(h));
        Assert.Contains("latest", h.ErrorLogText, StringComparison.Ordinal);
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);
    }

    [Fact]
    public async Task 缺tag_name算完成_寫errorlog()
    {
        await using var h = Create(h => ServeBody(h, new JsonObject { ["html_url"] = ReleasePage }.ToJsonString()));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Empty(h.Toasts);
        Assert.Equal(1, LogEntries(h));
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);

        // 算完成：不會每小時重試。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Theory]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData("http://github.com/marvin3991/is-it-green-yet/releases/tag/v0.3.0")]
    [InlineData("not a url")]
    [InlineData(null)]
    public async Task html_url不允許_寫errorlog不通知(string? url)
    {
        await using var h = Create(h => ServeRelease(h, url: url));

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastNotifiedVersion);
        Assert.Equal(1, LogEntries(h));
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);
    }

    [Fact]
    public async Task 目前版本解析不了不送請求_只寫一次log()
    {
        await using var h = Create(h => h.CurrentAppVersion = "0.2.0.0");

        await h.DrainAsync();
        await h.AdvanceInStepsAsync(ThreeDays, TimeSpan.FromHours(6));
        await SuspendAndResumeAsync(h);

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Equal(1, LogEntries(h));
        Assert.Null(h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 目前版本去掉build之後才比較()
    {
        await using var h = Create(h =>
        {
            h.CurrentAppVersion = "0.3.0+abc1234";
            ServeRelease(h);
        });

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Empty(h.Toasts);
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
    }

    // ── ETag ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 沒有上次檢查時間時不帶ETag()
    {
        await using var h = Create(h => h.Cache.PutCache(new HttpCacheEntry(ReleaseApiUrl, ReleaseETag, null, null)));

        await h.DrainAsync();

        Assert.Null(Assert.Single(ReleaseRequests(h)).Header("If-None-Match"));
    }

    [Fact]
    public async Task 有上次檢查時間時帶ETag()
    {
        await using var h = Create(h =>
        {
            h.Settings.LastUpdateCheckUtc = Start - ThreeDays;
            h.Cache.PutCache(new HttpCacheEntry(ReleaseApiUrl, ReleaseETag, null, null));
        });

        await h.DrainAsync();

        Assert.Equal(ReleaseETag, Assert.Single(ReleaseRequests(h)).Header("If-None-Match"));
    }

    [Fact]
    public async Task _304更新上次檢查時間且不通知()
    {
        var last = Start - ThreeDays;
        await using var h = Create(h =>
        {
            h.Settings.LastUpdateCheckUtc = last;
            h.Cache.PutCache(new HttpCacheEntry(ReleaseApiUrl, ReleaseETag, null, null));
            h.GitHub.Override(ReleasePath, _ => FakeHttpHandler.Status(HttpStatusCode.NotModified));
        });

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Empty(h.Toasts);
        Assert.Equal(0, LogEntries(h));
    }

    [Fact]
    public async Task 沒有新版的200立刻存ETag()
    {
        await using var h = Create(h => ServeRelease(h, tag: "v0.2.0"));

        await h.DrainAsync();

        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);

        // 下一次帶這個 ETag 送出。
        await h.AdvanceAsync(ThreeDays);
        Assert.Equal(ReleaseETag, ReleaseRequests(h)[^1].Header("If-None-Match"));
    }

    [Fact]
    public async Task 已通知過的200立刻存ETag()
    {
        await using var h = Create(h =>
        {
            h.Settings.LastNotifiedVersion = NewVersion;
            ServeRelease(h);
        });

        await h.DrainAsync();

        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);
        await h.AdvanceAsync(Window);
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public async Task 失敗與404不動ETag()
    {
        await using var h = Create(h =>
        {
            h.Settings.LastUpdateCheckUtc = Start - ThreeDays;
            h.Cache.PutCache(new HttpCacheEntry(ReleaseApiUrl, ReleaseETag, null, null));
            h.GitHub.Override(ReleasePath, _ => Failure(h, Status500), times: 1);
        });

        await h.DrainAsync();
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);

        // 第二次沒有 Override，FakeGitHub 回 404。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(ReleaseETag, h.Cache.Get(ReleaseApiUrl)?.ETag);
    }

    // ── 靜默 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task _404算完成_不影響角標橫幅與提示()
    {
        await using var h = Create();

        await h.DrainAsync();
        await h.AdvanceAsync(Window);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Contains((ReleasePath, 404), h.GitHub.Responses);
        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);
        Assert.False(h.Last.Offline);
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Empty(h.Toasts);

        // 算完成：不會每小時重試。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Theory]
    [InlineData(PrimaryLimit)]
    [InlineData(Status429)]
    public async Task 限流回應不觸發全域暫停(string failure)
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, failure)));

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);
        Assert.Equal(0, LogEntries(h));

        // 節流器沒有被暫停：下一個請求照常送出。
        var before = h.GitHub.ApiRequests.Count;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.GitHub.ApiRequests.Count > before);
    }

    [Fact]
    public async Task 連續網路錯誤不進入離線()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, NetworkFailure)));

        await h.DrainAsync();
        for (var attempt = 1; attempt < Tuning.OfflineThreshold; attempt++)
        {
            await h.AdvanceAsync(OneHour);
        }

        Assert.Equal(Tuning.OfflineThreshold, h.GitHub.Count(ReleasePath));
        Assert.False(h.Last.Offline);
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);
        Assert.Equal(0, LogEntries(h));
    }

    [Fact]
    public async Task 連續5xx不加角標()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500)));

        await h.DrainAsync();
        for (var attempt = 1; attempt < Tuning.ServerErrorBadgeThreshold; attempt++)
        {
            await h.AdvanceAsync(OneHour);
        }

        Assert.Equal(Tuning.ServerErrorBadgeThreshold, h.GitHub.Count(ReleasePath));
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);
    }

    [Fact]
    public async Task 檢查的成功回應不會把輪詢的連續5xx歸零()
    {
        // 請求順序：E10 500、檢查 200、清單 500、清單重試 500。
        await using var h = CreateQuiet(h =>
        {
            h.GitHub.Override("/rate_limit", _ => Failure(h, Status500), times: 1);
            ServeRelease(h, "v0.2.0");
            h.GitHub.Override(EngineHarness.ListAPath, _ => Failure(h, Status500), times: 2);
        });
        await h.DrainAsync();
        Assert.Equal(1, h.GitHub.Count(ReleasePath));

        for (var minute = 0; minute < MaxMinutesUntilListRetried && h.GitHub.Count(EngineHarness.ListAPath) < 2; minute++)
        {
            h.Engine.Post(new EngineCommand.RefreshAll());
            await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        }

        Assert.Contains(h.Snapshots, s => s.Summary.Issues.HasFlag(ConnectionIssue.ServerErrors));
    }

    [Fact]
    public async Task 檢查的回應不會把輪詢的連續網路錯誤歸零()
    {
        // 請求順序：E10 失敗、檢查 200、清單失敗、探測失敗。
        await using var h = Create(h =>
        {
            h.GitHub.Override("/rate_limit", _ => Failure(h, NetworkFailure), times: 1);
            ServeRelease(h, "v0.2.0");
            h.GitHub.Override(EngineHarness.ListAPath, _ => Failure(h, NetworkFailure), times: 1);
            h.GitHub.Override(EngineHarness.ProbeAPath, _ => Failure(h, NetworkFailure), times: 1);
        });
        await h.DrainAsync();

        await h.AdvanceAsync(Tuning.ProbeInterval);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Contains(h.Snapshots, s => s.Offline);
    }

    [Fact]
    public async Task 檢查的回應帶Deprecation_不跳橫幅也不寫errorlog()
    {
        await using var h = CreateQuiet(h =>
        {
            ServeRelease(h, "v0.2.0");
            h.GitHub.AddHeader(ReleasePath, "Deprecation", "true");
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Null(h.Last.Banner);
        Assert.Equal(string.Empty, h.ErrorLogText);
    }

    [Fact]
    public async Task 檢查的回應帶X_GitHub_SSO_不寫errorlog()
    {
        await using var h = CreateQuiet(h =>
        {
            ServeRelease(h, "v0.2.0");
            h.GitHub.AddHeader(ReleasePath, "X-GitHub-SSO", "required; url=https://github.com/orgs/x/sso?authorization_request=y");
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(string.Empty, h.ErrorLogText);
    }

    [Fact]
    public async Task _401不進入未登入()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status401)));

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(AuthStatus.Ok, h.Last.Auth);
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);

        // 也沒有重新取得 token、重送：FakeTokenProvider 只排了一個結果。
        Assert.Equal(1, h.Tokens.Calls);
    }

    [Fact]
    public async Task _400不進入版本錯誤()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status400)));

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Null(h.Last.Banner);

        // 版本錯誤會停止所有輪詢：下一個請求照常送出。
        var before = h.GitHub.ApiRequests.Count;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();
        Assert.True(h.GitHub.ApiRequests.Count > before);
    }

    [Fact]
    public async Task 不更新額度與最後更新時間()
    {
        await using var h = CreateQuiet(h =>
        {
            h.Settings.LastUpdateCheckUtc = Start - ThreeDays + OneHour;
            h.GitHub.Override(ReleasePath, _ => FakeHttpHandler.Json(
                new JsonObject { ["tag_name"] = "v0.2.0", ["html_url"] = ReleasePage }.ToJsonString(),
                HttpStatusCode.OK,
                new Dictionary<string, string>
                {
                    ["x-ratelimit-limit"] = "5000",
                    ["x-ratelimit-remaining"] = "1",
                    ["x-ratelimit-used"] = "4999",
                    ["x-ratelimit-reset"] = h.GitHub.ResetEpoch(OneHour).ToString(CultureInfo.InvariantCulture),
                    ["x-ratelimit-resource"] = "core",
                }));
        });
        await h.DrainAsync();
        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        var lastUpdated = h.Last.LastUpdatedUtc;
        var remaining = h.Last.RateLimit?.Remaining;

        await h.AdvanceAsync(OneHour);

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Equal(lastUpdated, h.Last.LastUpdatedUtc);
        Assert.Equal(remaining, h.Last.RateLimit?.Remaining);
    }

    [Fact]
    public async Task 未登入與離線不送也不跳提示()
    {
        await using var signedOut = Create(h => h.Tokens.Returns((string?)null), defaultToken: false);
        await signedOut.DrainAsync();
        Assert.Empty(signedOut.GitHub.Handler.Requests);
        Assert.Empty(signedOut.Toasts);

        await using var offline = Create(h => h.GitHub.NetworkDown = true);
        await offline.AdvanceInStepsAsync(TimeSpan.FromMinutes(5), Tuning.ProbeInterval);
        Assert.Equal(0, offline.GitHub.Count(ReleasePath));
        Assert.Empty(offline.Toasts);
    }

    [Fact]
    public async Task 請求都寫進apilog_404與網路錯誤不寫errorlog()
    {
        await using var h = Create();

        await h.DrainAsync();
        Assert.Equal(1, ApiLogLines(h));
        Assert.Equal(0, LogEntries(h));

        h.GitHub.Override(ReleasePath, _ => Failure(h, NetworkFailure));
        await h.AdvanceAsync(ThreeDays);

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(2, ApiLogLines(h));
        Assert.Equal(0, LogEntries(h));
    }

    [Theory]
    [InlineData(Status401)]
    [InlineData(Status403)]
    [InlineData(Status500)]
    [InlineData(BrokenJson)]
    public async Task 其他失敗寫errorlog_同一種連續失敗只寫一次(string failure)
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, failure)));

        await h.DrainAsync();
        Assert.Equal(1, LogEntries(h));

        await h.AdvanceAsync(OneHour);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(1, LogEntries(h));
    }

    [Fact]
    public async Task 不同種的失敗各寫一次errorlog()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500), times: 1));

        await h.DrainAsync();
        Assert.Equal(1, LogEntries(h));

        h.GitHub.Override(ReleasePath, _ => Failure(h, Status401), times: 1);
        await h.AdvanceAsync(OneHour);

        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(2, LogEntries(h));
    }

    [Fact]
    public async Task 完成之後同一種失敗再寫一次errorlog()
    {
        await using var h = CreateQuiet(h => h.GitHub.Override(ReleasePath, _ => Failure(h, Status500), times: 1));

        await h.DrainAsync();
        Assert.Equal(1, LogEntries(h));

        // 第二次沒有 Override，FakeGitHub 回 404，算完成。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(Start + OneHour, h.Settings.LastUpdateCheckUtc);

        h.GitHub.Override(ReleasePath, _ => Failure(h, Status500), times: 1);
        await h.AdvanceAsync(ThreeDays);

        Assert.Equal(3, h.GitHub.Count(ReleasePath));
        Assert.Equal(2, LogEntries(h));
    }

    [Fact]
    public async Task 送到正確路徑並帶token()
    {
        await using var h = Create();

        await h.DrainAsync();

        var request = Assert.Single(ReleaseRequests(h));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/repos/marvin3991/is-it-green-yet/releases/latest", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + Fixture.FakeToken, request.Header("Authorization"));
    }

    [Fact]
    public async Task 處理結果丟例外只寫errorlog不跳橫幅()
    {
        await using var h = Create(h =>
        {
            ServeRelease(h);
            ThrowOnceFrom(h, "ApplyUpdateCheckResult");
        });

        await h.DrainAsync();

        Assert.Equal(1, h.GitHub.Count(ReleasePath));
        Assert.Null(h.Last.Banner);
        Assert.Equal(ConnectionIssue.None, h.Last.Summary.Issues);
        Assert.Empty(h.Toasts);
        Assert.Null(h.Settings.LastUpdateCheckUtc);
        Assert.Equal(1, LogEntries(h));
        Assert.Contains(InjectedMessage, h.ErrorLogText, StringComparison.Ordinal);

        // 其餘輪詢照常，1 小時後重試。
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ListAPath));
        await h.AdvanceAsync(OneHour);
        Assert.Equal(2, h.GitHub.Count(ReleasePath));
        Assert.Equal(Start + OneHour, h.Settings.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task 檢查時讀到資料庫損毀_走改名重建()
    {
        // 自動更新暫停：沒有輪詢會再碰到損毀，只有檢查這條路看得到。
        await using var h = CreateQuiet(h => h.GitHub.Override(
            ReleasePath,
            _ =>
            {
                MonitorEngineReviewTests.CorruptTable(h.Workspace.Paths.CacheDbPath, "http_cache");
                return Release(new JsonObject { ["tag_name"] = "v0.2.0", ["html_url"] = ReleasePage }.ToJsonString());
            },
            times: 1));

        await h.DrainAsync();

        Assert.Equal(1, h.Engine.DatabaseRebuildCount);
        Assert.Equal(BannerKind.DatabaseRecovered, h.Last.Banner?.Kind);
    }

    [Fact]
    public async Task 檢查先撞到資料庫錯誤_輪詢第一次失敗照樣跳橫幅()
    {
        await using var h = Create(h => h.Settings.LastUpdateCheckUtc = Start - ThreeDays);

        // E1、E10 之後 http_cache 不見；下一步的檢查要帶 ETag，先撞到。
        await h.StepAsync();
        await h.StepAsync();
        MonitorEngineReviewTests.Execute(h.Workspace.Paths.CacheDbPath, "DROP TABLE http_cache;");
        await h.StepAsync();
        Assert.Null(h.Last.Banner);
        Assert.Equal(1, LogEntries(h));

        // 型別和訊息都相同的例外。檢查這條路只寫 log，不能佔掉橫幅的去重名額。
        await h.StepAsync();

        Assert.Equal(BannerKind.UnexpectedError, h.Last.Banner?.Kind);
    }

    [Fact]
    public async Task 判斷時丟例外只寫errorlog不跳橫幅()
    {
        await using var h = Create(h => ThrowOnceFrom(h, "EvaluateUpdateCheck"));

        await h.DrainAsync();

        Assert.Equal(0, h.GitHub.Count(ReleasePath));
        Assert.Null(h.Last.Banner);
        Assert.Equal(1, LogEntries(h));

        // 同一步接下來的輪詢沒有受影響。
        Assert.Equal(1, h.GitHub.Count(EngineHarness.ListAPath));

        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    [Fact]
    public async Task 立刻存ETag丟例外仍算完成_不跳橫幅()
    {
        await using var h = Create(h =>
        {
            ServeRelease(h, tag: "v0.2.0");
            ThrowOnceFrom(h, "SaveUpdateEtag");
        });

        await h.DrainAsync();

        Assert.Equal(Start, h.Settings.LastUpdateCheckUtc);
        Assert.Null(h.Last.Banner);
        Assert.Equal(1, LogEntries(h));

        // 算完成：不會 1 小時後重試。
        await h.AdvanceAsync(OneHour);
        Assert.Equal(1, h.GitHub.Count(ReleasePath));
    }

    // ── 輔助 ──────────────────────────────────────────────────────────

    /// <summary>打開檢查新版本的環境；目前版本固定成 <see cref="CurrentVersion"/>。</summary>
    private static EngineHarness Create(Action<EngineHarness>? arrange = null, bool defaultToken = true) =>
        EngineHarness.Create(
            h =>
            {
                h.Settings.CheckForUpdates = true;
                h.CurrentAppVersion = CurrentVersion;
                arrange?.Invoke(h);
            },
            defaultToken);

    /// <summary>
    /// 自動更新暫停：沒有輪詢請求夾在檢查之間。
    /// 輪詢的成功回應會把連續失敗的計數歸零，所以「檢查不影響其他狀態」要在這個環境才驗得出來。
    /// </summary>
    private static EngineHarness CreateQuiet(Action<EngineHarness>? arrange = null) =>
        Create(h =>
        {
            h.Settings.RefreshMode = RefreshMode.Paused;
            arrange?.Invoke(h);
        });

    private static void ServeRelease(EngineHarness h, string? tag = NewTag, string? url = ReleasePage) =>
        ServeBody(h, new JsonObject { ["tag_name"] = tag, ["html_url"] = url }.ToJsonString());

    private static void ServeBody(EngineHarness h, string body)
    {
        h.GitHub.RemoveOverrides(ReleasePath);
        h.GitHub.Override(ReleasePath, _ => Release(body));
    }

    /// <summary>同 GitHub：請求帶了相符的 ETag 就回 304，沒有內容可以比較。</summary>
    private static void ServeReleaseHonoringETag(EngineHarness h) =>
        h.GitHub.Override(ReleasePath, request =>
            request.Headers.TryGetValues("If-None-Match", out var etags) && etags.Contains(ReleaseETag)
                ? FakeHttpHandler.Status(HttpStatusCode.NotModified)
                : Release());

    private static HttpResponseMessage Release(string? body = null) =>
        FakeHttpHandler.Json(
            body ?? new JsonObject { ["tag_name"] = NewTag, ["html_url"] = ReleasePage }.ToJsonString(),
            HttpStatusCode.OK,
            new Dictionary<string, string> { ["ETag"] = ReleaseETag });

    private static HttpResponseMessage Failure(EngineHarness h, string failure) => failure switch
    {
        NetworkFailure => throw new HttpRequestException("模擬的連線失敗"),
        Status429 => h.GitHub.Error(HttpStatusCode.TooManyRequests, "Too many requests", ("retry-after", "60")),
        PrimaryLimit => PrimaryLimitResponse(h, TimeSpan.FromSeconds(600)),
        Status401 => h.GitHub.Error(HttpStatusCode.Unauthorized, "Bad credentials"),
        Status403 => h.GitHub.Error(HttpStatusCode.Forbidden, "Resource not accessible by personal access token"),
        Status500 => h.GitHub.Error(HttpStatusCode.InternalServerError, "boom"),
        BrokenJson => FakeHttpHandler.Json("{not json"),
        Status400 => h.GitHub.Error(HttpStatusCode.BadRequest, "Bad request"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    private static HttpResponseMessage PrimaryLimitResponse(EngineHarness h, TimeSpan resetIn) =>
        h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded for user ID 1001.",
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-used", "5000"),
            ("x-ratelimit-reset", h.GitHub.ResetEpoch(resetIn).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Engine 執行到 <paramref name="method"/> 裡第一次讀牆鐘時丟一次例外。</summary>
    /// <remarks>
    /// 靠呼叫堆疊判斷（<see cref="EngineClock.CalledFrom"/>），所以目標方法要標 NoInlining：Release 的動態 PGO 會把跑得夠多次的小方法內嵌，
    /// 內嵌之後堆疊裡沒有它，例外就不會丟。CI 是 Release，本機預設是 Debug，只在 CI 偶爾失敗。
    /// 要固定重現：Release 加環境變數 <c>DOTNET_TC_CallCountingDelayMs=0</c>、<c>DOTNET_TC_BackgroundWorkerTimeoutMs=0</c>。
    /// </remarks>
    private static void ThrowOnceFrom(EngineHarness h, string method)
    {
        var target = typeof(MonitorEngine).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(target);
        Assert.True(target.GetMethodImplementationFlags().HasFlag(MethodImplAttributes.NoInlining), $"{method} 要標 NoInlining。");

        var armed = 1;
        h.Clock.BeforeGetUtcNow = () =>
        {
            if (Volatile.Read(ref armed) == 1 && EngineClock.CalledFrom(method) && Interlocked.Exchange(ref armed, 0) == 1)
            {
                throw new InvalidOperationException(InjectedMessage);
            }
        };
    }

    private static void JumpWallClock(EngineHarness h, TimeSpan delta) => h.Clock.Manual.SetUtcNow(h.Clock.GetUtcNow() + delta);

    private static async Task EnterGlobalPauseAsync(EngineHarness h)
    {
        h.GitHub.Override(EngineHarness.ProbeAPath, _ => PrimaryLimitResponse(h, LongPause), times: 1);
        await h.AdvanceAsync(Tuning.ProbeInterval);
        Assert.True(h.Last.Summary.Issues.HasFlag(ConnectionIssue.RateLimited));
    }

    /// <summary>檢查已經到期（2 天前加這裡的 1 天）；暫停中先經過一次每小時判斷，再等到暫停結束。</summary>
    private static async Task PassHourlyEvaluationDuringPauseAsync(EngineHarness h)
    {
        JumpWallClock(h, TimeSpan.FromDays(1));
        await h.AdvanceAsync(OneHour);
        await h.AdvanceAsync(LongPause - OneHour + Tuning.RateLimitResetMargin + Tuning.ProbeInterval);
    }

    private static async Task SuspendAndResumeAsync(EngineHarness h)
    {
        h.Engine.Post(new EngineCommand.Suspend());
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.Resume());
        await h.DrainAsync();
    }

    private static async Task LockAndUnlockAsync(EngineHarness h)
    {
        h.Engine.Post(new EngineCommand.SetLocked(true));
        await h.DrainAsync();
        h.Engine.Post(new EngineCommand.SetLocked(false));
        await h.DrainAsync();
    }

    /// <summary>兩次 Tick 之間單調時鐘跳過 SleepGapThreshold：無人值守喚醒沒有 Resume 事件，靠這個偵測（spec §6.5）。</summary>
    private static async Task TickAcrossSleepGapAsync(EngineHarness h)
    {
        h.Engine.Tick();
        h.Clock.Advance(SleepGap);
        h.Engine.Tick();
        await h.DrainAsync();
    }

    private static List<RecordedRequest> ReleaseRequests(EngineHarness h) =>
        h.GitHub.Handler.Requests.Where(r => r.Uri.PathAndQuery == ReleasePath).ToList();

    private static int LogEntries(EngineHarness h) => Regex.Count(h.ErrorLogText, Regex.Escape(LogEntryTitle));

    private static int ApiLogLines(EngineHarness h) => Regex.Count(h.ApiLogText, Regex.Escape(ReleasePath));
}

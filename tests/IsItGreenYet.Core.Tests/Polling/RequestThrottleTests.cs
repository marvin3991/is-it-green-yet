using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Polling;

public class RequestThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void _60秒內第61個要等()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        // 每秒送一個，t = 0～59 秒共 60 個，每個送出前都放行。
        for (var i = 0; i < Tuning.MaxRequestsPerMinute; i++)
        {
            if (i > 0)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Null(throttle.TimeUntilAllowed());
            throttle.RecordSent();
        }

        // t = 59 秒：第 61 個要等最早那個（t = 0）滿 60 秒，也就是再 1 秒。
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 同一刻送滿60個時第61個要等滿60秒()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        SendMany(throttle, Tuning.MaxRequestsPerMinute);

        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 滑動視窗過期後放行()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);
        SendMany(throttle, Tuning.MaxRequestsPerMinute);

        time.Advance(TimeSpan.FromMilliseconds(59_500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), throttle.TimeUntilAllowed());

        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Null(throttle.TimeUntilAllowed());

        // 過期的紀錄不再計入：再送 59 個仍放行，第 60 個之後才要等。
        SendMany(throttle, Tuning.MaxRequestsPerMinute - 1);
        Assert.Null(throttle.TimeUntilAllowed());
        throttle.RecordSent();
        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 只過期一部分時等到足夠的紀錄過期()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        // t = 0 送 30 個，t = 30 送 30 個。
        SendMany(throttle, 30);
        time.Advance(TimeSpan.FromSeconds(30));
        SendMany(throttle, 30);
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.TimeUntilAllowed());

        // t = 60：前 30 個過期，可以再送 30 個。
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(throttle.TimeUntilAllowed());
        SendMany(throttle, 30);
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 超過上限仍有記錄時要等到降回上限以下()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        // 使用者操作那條也會 RecordSent，可能讓視窗內超過 60 個。
        // t = 0、10、20 各送 1 個，t = 30 送 59 個，共 62 個。
        throttle.RecordSent();
        time.Advance(TimeSpan.FromSeconds(10));
        throttle.RecordSent();
        time.Advance(TimeSpan.FromSeconds(10));
        throttle.RecordSent();
        time.Advance(TimeSpan.FromSeconds(10));
        SendMany(throttle, Tuning.MaxRequestsPerMinute - 1);

        // 要降回 59 個才能再送，也就是等第 3 舊的（t = 20）過期：t = 80，現在 t = 30。
        Assert.Equal(TimeSpan.FromSeconds(50), throttle.TimeUntilAllowed());
        time.Advance(TimeSpan.FromSeconds(49));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.TimeUntilAllowed());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 暫停期間不放行()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        throttle.PauseFor(TimeSpan.FromSeconds(120), "次級限流");

        Assert.True(throttle.IsPaused);
        Assert.Equal("次級限流", throttle.PauseReason);
        Assert.Equal(TimeSpan.FromSeconds(120), throttle.PauseRemaining);
        Assert.Equal(TimeSpan.FromSeconds(120), throttle.TimeUntilAllowed());

        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(TimeSpan.FromSeconds(1), throttle.TimeUntilAllowed());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(throttle.IsPaused);
        Assert.Null(throttle.PauseRemaining);
        Assert.Null(throttle.PauseReason);
        Assert.Null(throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 解除暫停後立即放行()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);
        throttle.PauseFor(TimeSpan.FromSeconds(960), "次級限流");

        throttle.ClearPause();

        Assert.False(throttle.IsPaused);
        Assert.Null(throttle.PauseReason);
        Assert.Null(throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 暫停與每分鐘上限同時成立時取較久的等待()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);
        SendMany(throttle, Tuning.MaxRequestsPerMinute);

        throttle.PauseFor(TimeSpan.FromSeconds(10), "主額度用完");
        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());

        throttle.PauseFor(TimeSpan.FromSeconds(90), "主額度用完");
        Assert.Equal(TimeSpan.FromSeconds(90), throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 暫停重疊時保留較晚的結束時間()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        throttle.PauseFor(TimeSpan.FromSeconds(960), "次級限流");
        time.Advance(TimeSpan.FromSeconds(10));

        // 較短的暫停不會把進行中的暫停縮短。
        throttle.PauseFor(Tuning.MinRateLimitWait, "多個 repo 回 403");
        Assert.Equal(TimeSpan.FromSeconds(950), throttle.PauseRemaining);
        Assert.Equal("次級限流", throttle.PauseReason);

        // 較長的暫停會延長，原因也換成新的。
        throttle.PauseFor(TimeSpan.FromSeconds(2000), "主額度用完");
        Assert.Equal(TimeSpan.FromSeconds(2000), throttle.PauseRemaining);
        Assert.Equal("主額度用完", throttle.PauseReason);
    }

    [Fact]
    public void 暫停時間不大於0時用最短等待()
    {
        // spec §6.4 第 1 列：算出來 ≤ 0 時用 MinRateLimitWait。
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        throttle.PauseFor(TimeSpan.Zero, "主額度用完");
        Assert.Equal(Tuning.MinRateLimitWait, throttle.PauseRemaining);

        throttle.ClearPause();
        throttle.PauseFor(TimeSpan.FromSeconds(-30), "主額度用完");
        Assert.Equal(Tuning.MinRateLimitWait, throttle.PauseRemaining);
    }

    [Fact]
    public void 極長的暫停不溢位()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        throttle.PauseFor(TimeSpan.MaxValue, "主額度用完");
        time.Advance(TimeSpan.FromDays(1));

        Assert.True(throttle.IsPaused);
        Assert.Equal(TimeSpan.MaxValue - TimeSpan.FromDays(1), throttle.PauseRemaining);
    }

    [Fact]
    public void 牆鐘倒退不影響()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);
        SendMany(throttle, Tuning.MaxRequestsPerMinute);
        throttle.PauseFor(TimeSpan.FromSeconds(30), "次級限流");

        time.SetUtcNow(Start.AddHours(-1));
        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
        Assert.Equal(TimeSpan.FromSeconds(30), throttle.PauseRemaining);

        // 牆鐘往前跳也不會提早放行。
        time.SetUtcNow(Start.AddDays(1));
        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
        Assert.True(throttle.IsPaused);

        // 只有單調時鐘前進才算數。
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.False(throttle.IsPaused);
        time.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.Null(throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 老化每60秒升一級最高到第2級()
    {
        var step = Tuning.PriorityAgingStep;

        Assert.Equal(RequestPriority.Maintenance, RequestThrottle.Aged(RequestPriority.Maintenance, step - TimeSpan.FromSeconds(1)));
        Assert.Equal(RequestPriority.PullRequests, RequestThrottle.Aged(RequestPriority.Maintenance, step));
        Assert.Equal(RequestPriority.RunsPoll, RequestThrottle.Aged(RequestPriority.Maintenance, step * 2));
        Assert.Equal(RequestPriority.PushedRepo, RequestThrottle.Aged(RequestPriority.Maintenance, step * 5 - TimeSpan.FromSeconds(1)));
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.Maintenance, step * 5));
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.Maintenance, TimeSpan.FromDays(1)));

        Assert.Equal(RequestPriority.PushedRepo, RequestThrottle.Aged(RequestPriority.Probe, step));
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.Probe, step * 2));
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.Probe, step * 10));

        // 第 2 級本身不再升；使用者操作不受老化影響，也不會被老化追上。
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.RunningRepo, TimeSpan.FromDays(1)));
        Assert.Equal(RequestPriority.User, RequestThrottle.Aged(RequestPriority.User, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void 老化的等待時間不合理時不出錯()
    {
        Assert.Equal(RequestPriority.Probe, RequestThrottle.Aged(RequestPriority.Probe, TimeSpan.FromSeconds(-30)));
        Assert.Equal(RequestPriority.Probe, RequestThrottle.Aged(RequestPriority.Probe, TimeSpan.MinValue));
        Assert.Equal(RequestPriority.RunningRepo, RequestThrottle.Aged(RequestPriority.Probe, TimeSpan.MaxValue));
    }

    [Fact]
    public void 寫入之間至少隔1秒()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        // 還沒有送過寫入：不用等。
        Assert.Null(throttle.TimeUntilMutativeAllowed());

        throttle.RecordMutativeSent();
        Assert.Equal(Tuning.MutativeRequestSpacing, throttle.TimeUntilMutativeAllowed());

        // 差 1 毫秒還不行，滿 MutativeRequestSpacing 才放行（第二階段 spec §3.2）。
        time.Advance(Tuning.MutativeRequestSpacing - TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1), throttle.TimeUntilMutativeAllowed());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(throttle.TimeUntilMutativeAllowed());

        // 間隔從最後一個寫入起算，用單調時鐘：牆鐘倒退不會提早放行。
        throttle.RecordMutativeSent();
        time.SetUtcNow(Start.AddHours(-1));
        Assert.Equal(Tuning.MutativeRequestSpacing, throttle.TimeUntilMutativeAllowed());
    }

    [Fact]
    public void 寫入間隔不影響一般請求()
    {
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        // 剛送出一個寫入：一般請求照每分鐘上限放行，不等寫入的間隔。
        throttle.RecordMutativeSent();
        Assert.Null(throttle.TimeUntilAllowed());

        // 一般請求送滿視窗也不影響寫入的間隔（兩者各自計算，寫入另外也要記一筆 RecordSent）。
        time.Advance(Tuning.MutativeRequestSpacing);
        SendMany(throttle, Tuning.MaxRequestsPerMinute);
        Assert.Null(throttle.TimeUntilMutativeAllowed());
        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 兩條工作同時記錄時不遺漏()
    {
        // 輪詢與使用者操作共用同一個節流器（spec §3.1）。
        var time = new ManualTimeProvider(Start);
        var throttle = new RequestThrottle(time);

        Parallel.For(0, Tuning.MaxRequestsPerMinute, _ => throttle.RecordSent());

        Assert.Equal(Tuning.RequestWindow, throttle.TimeUntilAllowed());
    }

    [Fact]
    public void 沒有時間來源時丟例外()
    {
        Assert.Throws<ArgumentNullException>(() => new RequestThrottle(null!));
    }

    private static void SendMany(RequestThrottle throttle, int count)
    {
        for (var i = 0; i < count; i++)
        {
            throttle.RecordSent();
        }
    }
}

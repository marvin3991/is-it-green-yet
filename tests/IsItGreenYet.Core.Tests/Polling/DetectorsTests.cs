using IsItGreenYet.Core.Polling;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Polling;

public class DetectorsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    // ── SecondaryBackoff ─────────────────────────────────────────────

    [Fact]
    public void 次級退避60到960並在第5次標示持續()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        int[] expectedSeconds = [60, 120, 240, 480, 960, 960, 960];

        Assert.Equal(0, backoff.Level);
        Assert.False(backoff.Persistent);

        for (var i = 0; i < expectedSeconds.Length; i++)
        {
            var wait = backoff.OnLimited();
            var n = i + 1;

            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds[i]), wait);
            Assert.Equal(Math.Min(n, Tuning.SecondaryBackoffPersistentLevel), backoff.Level);
            Assert.Equal(n >= Tuning.SecondaryBackoffPersistentLevel, backoff.Persistent);

            // 暫停結束後送出的第一個請求又被限流。
            time.Advance(wait);
        }
    }

    [Fact]
    public void 限流與成功交錯時等待仍遞增()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);

        var first = backoff.OnLimited();
        time.Advance(first);
        backoff.OnSuccess();
        time.Advance(TimeSpan.FromSeconds(5));
        backoff.OnSuccess();
        var second = backoff.OnLimited();

        time.Advance(second);
        backoff.OnSuccess();
        time.Advance(TimeSpan.FromSeconds(30));
        var third = backoff.OnLimited();

        Assert.Equal(TimeSpan.FromSeconds(60), first);
        Assert.Equal(TimeSpan.FromSeconds(120), second);
        Assert.Equal(TimeSpan.FromSeconds(240), third);
        Assert.Equal(3, backoff.Level);
    }

    [Fact]
    public void 成功不會直接歸零()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        LimitRepeatedly(time, backoff, 3);

        backoff.OnSuccess();

        Assert.Equal(3, backoff.Level);
    }

    [Fact]
    public void 安靜一段時間後降一級()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        var lastWait = LimitRepeatedly(time, backoff, 3);
        Assert.Equal(TimeSpan.FromSeconds(240), lastWait);

        // 暫停的 240 秒不算安靜期；暫停結束後再安靜 240 秒才降一級。
        time.Advance(lastWait);
        backoff.OnSuccess();
        Assert.Equal(3, backoff.Level);

        time.Advance(lastWait - TimeSpan.FromSeconds(1));
        backoff.OnSuccess();
        Assert.Equal(3, backoff.Level);

        time.Advance(TimeSpan.FromSeconds(1));
        backoff.OnSuccess();
        Assert.Equal(2, backoff.Level);

        // 之後每再安靜一段（仍以上一次實際等待的 240 秒為準）再降一級。
        time.Advance(lastWait);
        backoff.OnSuccess();
        Assert.Equal(1, backoff.Level);

        time.Advance(lastWait);
        backoff.OnSuccess();
        Assert.Equal(0, backoff.Level);

        time.Advance(lastWait * 10);
        backoff.OnSuccess();
        Assert.Equal(0, backoff.Level);
        Assert.Equal(TimeSpan.FromSeconds(60), backoff.OnLimited());
    }

    [Fact]
    public void 降級後再被限流從降級後的等級往上()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        var lastWait = LimitRepeatedly(time, backoff, 3);

        time.Advance(lastWait * 2);
        backoff.OnSuccess();
        Assert.Equal(2, backoff.Level);

        Assert.Equal(TimeSpan.FromSeconds(240), backoff.OnLimited());
        Assert.Equal(3, backoff.Level);
    }

    [Fact]
    public void 持續被限流在降級後解除()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        var lastWait = LimitRepeatedly(time, backoff, Tuning.SecondaryBackoffPersistentLevel);
        Assert.True(backoff.Persistent);

        time.Advance(lastWait * 2);
        backoff.OnSuccess();

        Assert.Equal(Tuning.SecondaryBackoffPersistentLevel - 1, backoff.Level);
        Assert.False(backoff.Persistent);
    }

    [Fact]
    public void 沒有任何回應的安靜期間也算()
    {
        // 降級條件只看「沒有限流回應」的時間（spec §6.4），離線或暫停自動更新期間也算安靜。
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        var lastWait = LimitRepeatedly(time, backoff, 2);

        time.Advance(lastWait * 2);

        Assert.Equal(1, backoff.Level);
        Assert.Equal(TimeSpan.FromSeconds(120), backoff.OnLimited());
    }

    [Fact]
    public void 退避降級不受牆鐘影響()
    {
        var time = new ManualTimeProvider(Start);
        var backoff = new SecondaryBackoff(time);
        LimitRepeatedly(time, backoff, 2);

        time.SetUtcNow(Start.AddDays(1));
        backoff.OnSuccess();
        Assert.Equal(2, backoff.Level);

        time.SetUtcNow(Start.AddDays(-1));
        backoff.OnSuccess();
        Assert.Equal(2, backoff.Level);
    }

    // ── ForbiddenBurstDetector ───────────────────────────────────────

    [Fact]
    public void _60秒內3個repo觸發()
    {
        var time = new ManualTimeProvider(Start);
        var detector = new ForbiddenBurstDetector(time);

        Assert.False(detector.Record(1));
        Assert.False(detector.Record(1)); // 同一個 repo 只算一個
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(detector.Record(2));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(detector.Record(3)); // 剛好 60 秒仍在視窗內

        // 觸發後清空，要重新累積 3 個。
        Assert.False(detector.Record(1));
        Assert.False(detector.Record(2));
        Assert.True(detector.Record(3));
    }

    [Fact]
    public void 超過60秒的紀錄不算()
    {
        var time = new ManualTimeProvider(Start);
        var detector = new ForbiddenBurstDetector(time);

        detector.Record(1);
        time.Advance(TimeSpan.FromSeconds(10));
        detector.Record(2);
        time.Advance(TimeSpan.FromSeconds(40));
        detector.Record(1); // repo 1 在 t = 50 又出現，以最近一次為準
        time.Advance(TimeSpan.FromSeconds(21));

        // t = 71：repo 2（t = 10）已超過 60 秒，視窗內只有 repo 1 與 3。
        Assert.False(detector.Record(3));
        Assert.True(detector.Record(4));
    }

    [Fact]
    public void 多個repo的403偵測不受牆鐘影響()
    {
        var time = new ManualTimeProvider(Start);
        var detector = new ForbiddenBurstDetector(time);

        detector.Record(1);
        detector.Record(2);
        time.SetUtcNow(Start.AddHours(1));

        Assert.True(detector.Record(3));
    }

    // ── OfflineDetector ──────────────────────────────────────────────

    [Fact]
    public void 連續3次網路錯誤轉離線()
    {
        var detector = new OfflineDetector();

        Assert.False(detector.OnNetworkError());
        Assert.False(detector.OnNetworkError());
        Assert.False(detector.IsOffline);

        Assert.True(detector.OnNetworkError());
        Assert.True(detector.IsOffline);

        // 已經離線時再出錯，不會再回報「剛轉為離線」。
        Assert.False(detector.OnNetworkError());
        Assert.True(detector.IsOffline);
    }

    [Fact]
    public void 成功後恢復()
    {
        var detector = new OfflineDetector();
        for (var i = 0; i < Tuning.OfflineThreshold; i++)
        {
            detector.OnNetworkError();
        }

        detector.OnSuccess();

        Assert.False(detector.IsOffline);
        // 恢復後要重新累積連續錯誤。
        Assert.False(detector.OnNetworkError());
        Assert.False(detector.OnNetworkError());
        Assert.True(detector.OnNetworkError());
    }

    [Fact]
    public void 中間有成功就重新計算網路錯誤()
    {
        var detector = new OfflineDetector();

        detector.OnNetworkError();
        detector.OnNetworkError();
        detector.OnSuccess();
        detector.OnNetworkError();

        Assert.False(detector.OnNetworkError());
        Assert.False(detector.IsOffline);
    }

    // ── ServerErrorDetector ──────────────────────────────────────────

    [Fact]
    public void 連續3個5xx()
    {
        var detector = new ServerErrorDetector();

        detector.OnServerError();
        detector.OnServerError();
        Assert.False(detector.IsActive);

        detector.OnServerError();
        Assert.True(detector.IsActive);

        detector.OnServerError();
        Assert.True(detector.IsActive);

        detector.OnOtherResponse();
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void 中間有非5xx回應就重新計算()
    {
        var detector = new ServerErrorDetector();

        detector.OnServerError();
        detector.OnServerError();
        detector.OnOtherResponse(); // 例如 304
        detector.OnServerError();
        detector.OnServerError();

        Assert.False(detector.IsActive);
    }

    [Fact]
    public void 長時間異常時計數不溢位()
    {
        var detector = new ServerErrorDetector();

        for (var i = 0; i < 100_000; i++)
        {
            detector.OnServerError();
        }

        Assert.True(detector.IsActive);
        detector.OnOtherResponse();
        Assert.False(detector.IsActive);
    }

    [Fact]
    public void 沒有時間來源時丟例外()
    {
        Assert.Throws<ArgumentNullException>(() => new SecondaryBackoff(null!));
        Assert.Throws<ArgumentNullException>(() => new ForbiddenBurstDetector(null!));
    }

    /// <summary>連續被限流 count 次，每次都在暫停結束後立刻再被限流；回傳最後一次的等待時間（暫停尚未開始經過）。</summary>
    private static TimeSpan LimitRepeatedly(ManualTimeProvider time, SecondaryBackoff backoff, int count)
    {
        var wait = TimeSpan.Zero;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                time.Advance(wait);
            }

            wait = backoff.OnLimited();
        }

        return wait;
    }
}

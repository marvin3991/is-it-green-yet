namespace IsItGreenYet.Core.Tests.TestSupport;

public class ManualTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 牆鐘往回調時單調時鐘不倒退()
    {
        var time = new ManualTimeProvider(Start);
        var before = time.GetTimestamp();

        time.SetUtcNow(Start.AddHours(-1));

        Assert.Equal(Start.AddHours(-1), time.GetUtcNow());
        Assert.True(time.GetTimestamp() >= before);
    }

    [Fact]
    public void Advance會同時推進兩個時鐘()
    {
        var time = new ManualTimeProvider(Start);
        var before = time.GetTimestamp();

        time.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(Start.AddSeconds(90), time.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(90), time.GetElapsedTime(before));
    }

    [Fact]
    public void AdvanceMonotonic只推進單調時鐘()
    {
        var time = new ManualTimeProvider(Start);
        var before = time.GetTimestamp();

        time.AdvanceMonotonic(TimeSpan.FromMinutes(10));

        Assert.Equal(Start, time.GetUtcNow());
        Assert.Equal(TimeSpan.FromMinutes(10), time.GetElapsedTime(before));
    }

    [Fact]
    public void 本地時區可以指定()
    {
        var time = new ManualTimeProvider(Start, ManualTimeProvider.Taipei);

        Assert.Equal(TimeSpan.FromHours(8), time.GetLocalNow().Offset);
        Assert.Equal(10, time.GetLocalNow().Hour);
    }
}

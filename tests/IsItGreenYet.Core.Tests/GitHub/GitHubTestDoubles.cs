using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.GitHub;

/// <summary>測試內手寫的快取：只放測試指定的項目，並記錄 GitHubClient 查過哪些網址（不依賴 SQLite）。</summary>
internal sealed class FakeCacheStore : IHttpCacheStore
{
    private readonly Dictionary<string, HttpCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _lookups = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lookups
    {
        get
        {
            lock (_gate)
            {
                return _lookups.ToList();
            }
        }
    }

    public void Put(HttpCacheEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.Url] = entry;
        }
    }

    public HttpCacheEntry? Get(string url)
    {
        lock (_gate)
        {
            _lookups.Add(url);
            return _entries.GetValueOrDefault(url);
        }
    }
}

/// <summary>
/// 時間照 ManualTimeProvider，但記錄每個建立的計時器（CancellationTokenSource 的逾時就是一個），
/// 由測試決定何時觸發，不必真的等 30 秒。
/// </summary>
internal sealed class TimerControlledTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly ManualTimeProvider _inner = new(utcNow, ManualTimeProvider.Taipei);
    private readonly List<ControlledTimer> _timers = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<TimeSpan> DueTimes
    {
        get
        {
            lock (_gate)
            {
                return _timers.Select(timer => timer.DueTime).ToList();
            }
        }
    }

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ControlledTimer(callback, state, dueTime);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>觸發所有還沒被釋放的計時器。</summary>
    public void FireAll()
    {
        List<ControlledTimer> timers;
        lock (_gate)
        {
            timers = _timers.ToList();
        }

        foreach (var timer in timers)
        {
            timer.Fire();
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private volatile bool _disposed;

        public TimeSpan DueTime { get; } = dueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>收到請求後一直等到被取消，用來模擬伺服器沒有回應。</summary>
internal sealed class HangingHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("取消之後不會走到這裡");
    }
}

/// <summary>header 立刻回來，但 body 讀不完（一直等到被取消）。</summary>
internal sealed class HangingBodyStream : Stream
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("只支援非同步讀取");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>包住另一個 handler，記錄自己有沒有被釋放。</summary>
internal sealed class DisposeTrackingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

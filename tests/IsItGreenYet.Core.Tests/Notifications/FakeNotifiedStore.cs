using IsItGreenYet.Core.Notifications;

namespace IsItGreenYet.Core.Tests.Notifications;

public sealed record NotifiedEntry(
    long RunId,
    int RunAttempt,
    string Conclusion,
    NotificationKind? Kind,
    bool Suppressed,
    DateTimeOffset At);

/// <summary>
/// 手寫的假 notified 表：只放在記憶體，但同一個實例可以交給新的 NotificationPlanner，模擬重啟後讀回 DB。
/// 和 DB 的主鍵一樣，同一組 (run_id, run_attempt, conclusion) 重複寫入直接丟例外，抓出重複 Add。
/// </summary>
public sealed class FakeNotifiedStore : INotifiedStore
{
    private readonly List<NotifiedEntry> _entries = [];

    public IReadOnlyList<NotifiedEntry> Entries => _entries;

    public bool Contains(long runId, int runAttempt, string conclusion) =>
        _entries.Exists(e => e.RunId == runId && e.RunAttempt == runAttempt && e.Conclusion == conclusion);

    public void Add(long runId, int runAttempt, string conclusion, NotificationKind? kind, bool suppressed, DateTimeOffset at)
    {
        if (Contains(runId, runAttempt, conclusion))
        {
            throw new InvalidOperationException($"重複寫入 notified：({runId}, {runAttempt}, {conclusion})");
        }

        _entries.Add(new NotifiedEntry(runId, runAttempt, conclusion, kind, suppressed, at));
    }
}

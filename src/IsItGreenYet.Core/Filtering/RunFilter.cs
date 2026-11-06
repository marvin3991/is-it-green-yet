using System.Diagnostics.CodeAnalysis;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Filtering;

/// <summary>主清單的一列。<see cref="IsTrackedFailure"/>：屬於某追蹤鍵 lastDecisive 組、而且是失敗。</summary>
public sealed record RunRow(RunInfo Run, RepoInfo Repo, RunState State, string WorkflowName, bool IsTrackedFailure);

/// <summary>篩選條件（spec §10.4）：同一欄位 OR、不同欄位 AND；空集合代表不篩這一欄。</summary>
public sealed record RunFilterCriteria(
    IReadOnlySet<string> Repos,
    IReadOnlySet<string> Workflows,
    IReadOnlySet<string> Events,
    IReadOnlySet<DisplayState> Statuses,
    IReadOnlySet<string> Branches,
    IReadOnlySet<string> Actors,
    string? SearchText,
    bool AttentionOnly)
{
    public static RunFilterCriteria Empty { get; } = new(
        new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
        new HashSet<DisplayState>(), new HashSet<string>(), new HashSet<string>(),
        null, false);
}

public static class RunFilter
{
    /// <summary>搜尋比對 repo full_name、display_title、workflow 名稱、head_branch，不分大小寫。</summary>
    public static IReadOnlyList<RunRow> Apply(IReadOnlyList<RunRow> rows, RunFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(criteria);

        // 只有空白的搜尋文字視為沒有搜尋；頭尾空白不參與比對。
        var search = string.IsNullOrWhiteSpace(criteria.SearchText) ? null : criteria.SearchText.Trim();
        if (search is null && !criteria.AttentionOnly && !HasSetFilter(criteria))
        {
            return rows;
        }

        // 只挑出符合的列、保留原順序，不改動傳入的清單（摘要列的計數用原清單，spec §7.4）。
        return [.. rows.Where(row => Matches(row, criteria, search))];
    }

    private static bool HasSetFilter(RunFilterCriteria criteria) =>
        IsActive(criteria.Repos) || IsActive(criteria.Workflows) || IsActive(criteria.Events)
        || IsActive(criteria.Statuses) || IsActive(criteria.Branches) || IsActive(criteria.Actors);

    /// <summary>同一欄位內是 OR（集合內任一值），不同欄位之間是 AND（spec §10.4）。</summary>
    private static bool Matches(RunRow row, RunFilterCriteria criteria, string? search) =>
        InSet(criteria.Repos, row.Repo.FullName)
        // Workflow 以名稱比對，不同 repo 的同名 workflow 會一起選到。
        && InSet(criteria.Workflows, row.WorkflowName)
        && InSet(criteria.Events, row.Run.Event)
        && InSet(criteria.Statuses, row.State.Display)
        && InSet(criteria.Branches, row.Run.HeadBranch)
        && InSet(criteria.Actors, row.Run.ActorLogin)
        && (!criteria.AttentionOnly || NeedsAttention(row))
        && (search is null || MatchesSearch(row, search));

    /// <summary>追蹤中的失敗，加上所有未完成與等待核准的 run（Q13）。</summary>
    private static bool NeedsAttention(RunRow row) =>
        row.IsTrackedFailure || row.State.Category is RunCategory.Pending or RunCategory.Waiting;

    private static bool MatchesSearch(RunRow row, string search) =>
        Contains(row.Repo.FullName, search)
        || Contains(row.Run.DisplayTitle, search)
        || Contains(row.WorkflowName, search)
        || Contains(row.Run.HeadBranch, search);

    private static bool Contains(string? text, string search) =>
        text is not null && text.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>空集合（或 null）代表不篩這一欄；值缺席（例如 head_branch 為 null）時不符合有條件的欄位。</summary>
    private static bool InSet(IReadOnlySet<string>? set, string? value) =>
        !IsActive(set) || (value is not null && set.Contains(value));

    private static bool InSet(IReadOnlySet<DisplayState>? set, DisplayState value) =>
        !IsActive(set) || set.Contains(value);

    private static bool IsActive<T>([NotNullWhen(true)] IReadOnlySet<T>? set) => set is { Count: > 0 };
}

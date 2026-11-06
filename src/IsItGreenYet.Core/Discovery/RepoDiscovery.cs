using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Discovery;

/// <summary>spec §5.2 的略過原因，順序即判斷順序。</summary>
public enum SkipReason
{
    None,
    Muted,
    Archived,
    Disabled,
    Fork,
    Collaborator,
    Stale,
    NoWorkflows,
    AllWorkflowsDisabled,
}

public sealed record DiscoveryOptions(bool IncludeForks, bool IncludeCollaborators);

public sealed record WorkflowCounts(int Total, int Active);

/// <param name="CanForceInclude">
/// 強制納入對這個 repo 有作用：命中第 4～6 條而略過，或靠強制納入才監看中（管理頁要能取消）。
/// 其他情況（靜音、封存、停用、沒有 workflow、workflow 都已停用、本來就監看中）為 false。
/// </param>
public sealed record RepoClassification(long RepoId, bool Monitored, SkipReason Reason, bool CanForceInclude);

/// <param name="MissingRounds">更新後每個 repo 連續不見的輪數（不見的才列）。</param>
public sealed record ListingRoundResult(
    IReadOnlyDictionary<long, int> MissingRounds,
    IReadOnlyList<long> RemoveIds,
    IReadOnlyList<(long Id, string OldName, string NewName)> Renames);

/// <summary>repo 分類、略過規則、消失與改名判定（spec §5）。純函式。</summary>
public static class RepoDiscovery
{
    /// <summary>清單 A 的 owner.id 等於我 → Own，否則 Organization；只在清單 B → Collaborator；兩邊都有以 A 為準。</summary>
    /// <remarks>
    /// fork 不是另一種類別，保留 IsFork 交給 Classify 的第 4 條。分頁途中排序變動時同一個 repo 可能出現兩次，只留第一筆。
    /// </remarks>
    public static IReadOnlyList<RepoInfo> Merge(long myUserId, IReadOnlyList<RepoInfo> listA, IReadOnlyList<RepoInfo>? listB)
    {
        ArgumentNullException.ThrowIfNull(listA);

        var seen = new HashSet<long>();
        var merged = new List<RepoInfo>(listA.Count + (listB?.Count ?? 0));
        foreach (var repo in listA)
        {
            if (seen.Add(repo.Id))
            {
                var category = repo.OwnerId == myUserId ? RepoCategory.Own : RepoCategory.Organization;
                merged.Add(repo with { Category = category });
            }
        }

        if (listB is not null)
        {
            foreach (var repo in listB)
            {
                // 已經在清單 A 的不採用清單 B 的資料。
                if (seen.Add(repo.Id))
                {
                    merged.Add(repo with { Category = RepoCategory.Collaborator });
                }
            }
        }

        return merged;
    }

    /// <param name="workflows">null 代表還沒查過 workflow 清單，第 7、8 條暫不判定。</param>
    /// <param name="latestLocalRunAt">本地紀錄中這個 repo 最新一筆 run 的 created_at；沒有紀錄為 null。</param>
    public static RepoClassification Classify(
        RepoInfo repo,
        RepoOverride repoOverride,
        DiscoveryOptions options,
        DateTimeOffset? latestLocalRunAt,
        WorkflowCounts? workflows,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(repoOverride);
        ArgumentNullException.ThrowIfNull(options);

        // 第 1～3 條：強制納入救不回來（靜音只要取消靜音即可）。
        if (repoOverride.Muted)
        {
            return Skipped(repo, SkipReason.Muted, canForceInclude: false);
        }

        if (repo.IsArchived)
        {
            return Skipped(repo, SkipReason.Archived, canForceInclude: false);
        }

        if (repo.IsDisabled)
        {
            return Skipped(repo, SkipReason.Disabled, canForceInclude: false);
        }

        // 第 4～6 條：強制納入時整組跳過，繼續判斷第 7、8 條。
        var forcible = ForcibleReason(repo, options, latestLocalRunAt, now);
        if (forcible != SkipReason.None && !repoOverride.ForceIncluded)
        {
            return Skipped(repo, forcible, canForceInclude: true);
        }

        // 第 7、8 條：還沒查過 workflow 清單時不判定；強制納入救不回來（Q30）。
        if (workflows is not null)
        {
            if (workflows.Total <= 0)
            {
                return Skipped(repo, SkipReason.NoWorkflows, canForceInclude: false);
            }

            if (workflows.Active <= 0)
            {
                return Skipped(repo, SkipReason.AllWorkflowsDisabled, canForceInclude: false);
            }
        }

        return new RepoClassification(repo.Id, Monitored: true, SkipReason.None, CanForceInclude: forcible != SkipReason.None);
    }

    /// <summary>設定開啟，或有任何被強制納入的協作 repo，就要抓清單 B 與探測 B。</summary>
    public static bool NeedsCollaboratorList(DiscoveryOptions options, IEnumerable<(RepoInfo Repo, RepoOverride Override)> known)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(known);

        return options.IncludeCollaborators
            || known.Any(k => k.Repo.Category == RepoCategory.Collaborator && k.Override.ForceIncluded);
    }

    /// <param name="roundComplete">這一輪所有分頁都成功；false 時不計消失。</param>
    /// <remarks>
    /// 只看 knownRepos：清單裡新出現的 repo 由 Merge 與 Classify 處理，previousMissing 裡已經不認得的 id 直接丟掉。
    /// 在清單裡看得到的 repo 一定還在，不論這一輪完不完整都歸零；同一個 id 的 full_name 不同就回報改名（大小寫不同也算）。
    /// 看不到的 repo 只有完整的一輪才累計；連續 RemovalConfirmRounds 輪都不見才列入 RemoveIds（spec §5.4、Q29）。
    /// </remarks>
    public static ListingRoundResult EvaluateListing(
        IReadOnlyList<RepoInfo> knownRepos,
        IReadOnlyList<RepoInfo> listed,
        bool roundComplete,
        IReadOnlyDictionary<long, int> previousMissing)
    {
        ArgumentNullException.ThrowIfNull(knownRepos);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(previousMissing);

        var listedById = new Dictionary<long, RepoInfo>();
        foreach (var repo in listed)
        {
            listedById.TryAdd(repo.Id, repo);
        }

        var missing = new Dictionary<long, int>();
        var removeIds = new List<long>();
        var renames = new List<(long Id, string OldName, string NewName)>();
        var evaluated = new HashSet<long>();

        foreach (var repo in knownRepos)
        {
            if (!evaluated.Add(repo.Id))
            {
                continue;
            }

            if (listedById.TryGetValue(repo.Id, out var current))
            {
                if (!string.Equals(repo.FullName, current.FullName, StringComparison.Ordinal))
                {
                    renames.Add((repo.Id, repo.FullName, current.FullName));
                }

                continue;
            }

            var previous = previousMissing.TryGetValue(repo.Id, out var rounds) ? Math.Max(rounds, 0) : 0;
            if (!roundComplete)
            {
                // 分頁中途失敗、或無法確定抓完：這一輪不算數，維持原本的輪數。
                if (previous > 0)
                {
                    missing[repo.Id] = previous;
                }

                continue;
            }

            // 到門檻就一定移除，輪數停在門檻，不會溢位。
            var updated = Math.Min(previous, Tuning.RemovalConfirmRounds - 1) + 1;
            missing[repo.Id] = updated;
            if (updated >= Tuning.RemovalConfirmRounds)
            {
                removeIds.Add(repo.Id);
            }
        }

        return new ListingRoundResult(missing, removeIds, renames);
    }

    /// <summary>repo 管理頁「原因」欄的文字（spec §5.2，不含「略過：」前綴）；監看中回空字串。用共用的目前語言（<see cref="UiText.Current"/>）。</summary>
    public static string SkipReasonText(SkipReason reason) => SkipReasonText(reason, UiText.Current);

    public static string SkipReasonText(SkipReason reason, AppLanguage language)
    {
        var text = UiText.Of(language);
        return reason switch
        {
            SkipReason.Muted => text.SkipMuted,
            SkipReason.Archived => text.SkipArchived,
            SkipReason.Disabled => text.SkipDisabled,
            SkipReason.Fork => text.SkipFork,
            SkipReason.Collaborator => text.SkipCollaborator,
            SkipReason.Stale => UiText.Format(text.SkipStale, Tuning.StaleRepoDays),
            // Actions 被停用時可能也落在這一條，分不出來（spec §5.2 第 7 條說明）。
            SkipReason.NoWorkflows => text.SkipNoWorkflows,
            SkipReason.AllWorkflowsDisabled => text.SkipAllWorkflowsDisabled,
            _ => string.Empty,
        };
    }

    private static RepoClassification Skipped(RepoInfo repo, SkipReason reason, bool canForceInclude) =>
        new(repo.Id, Monitored: false, reason, canForceInclude);

    /// <summary>第 4～6 條中第一個命中的（不看強制納入）；都沒命中回 None。</summary>
    private static SkipReason ForcibleReason(
        RepoInfo repo,
        DiscoveryOptions options,
        DateTimeOffset? latestLocalRunAt,
        DateTimeOffset now)
    {
        if (repo.IsFork && !options.IncludeForks)
        {
            return SkipReason.Fork;
        }

        if (repo.Category == RepoCategory.Collaborator && !options.IncludeCollaborators)
        {
            return SkipReason.Collaborator;
        }

        // 第 6 條：pushed_at 超過 90 天（或為 null），而且本地沒有 90 天內的 run 紀錄。
        var limit = TimeSpan.FromDays(Tuning.StaleRepoDays);
        if (!IsWithin(repo.PushedAt, now, limit) && !IsWithin(latestLocalRunAt, now, limit))
        {
            return SkipReason.Stale;
        }

        return SkipReason.None;
    }

    /// <summary>null 視為沒有；剛好在期限上算期限內；未來的時間（時鐘誤差）也算期限內。</summary>
    private static bool IsWithin(DateTimeOffset? at, DateTimeOffset now, TimeSpan limit) =>
        at is { } value && now - value <= limit;
}

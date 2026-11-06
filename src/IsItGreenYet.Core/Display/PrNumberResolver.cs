using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Display;

/// <summary>決定 run 要顯示哪個 PR 編號（spec §10.6，Q18）。</summary>
public static class PrNumberResolver
{
    private const string PullRequestEvent = "pull_request";

    /// <returns>依序：同 head_sha 且同來源 repo 的開啟中 PR → run.pull_requests → 本地保存的編號；都沒有回空清單。</returns>
    /// <remarks>
    /// 第 1 步只在 event 是 <c>pull_request</c> 時做，<paramref name="openPullRequests"/> 為 null（PR 追蹤停用或還沒抓到）時略過。
    /// 不合理的編號（≤ 0）與重複的編號會被去掉；從來沒觀察到就回空清單，不推測（§10.6 第 4 點）。
    /// </remarks>
    public static IReadOnlyList<int> Resolve(
        RunInfo run,
        IReadOnlyCollection<PullRequestInfo>? openPullRequests,
        IReadOnlyList<int> storedNumbers)
    {
        if (string.Equals(run.Event, PullRequestEvent, StringComparison.Ordinal) && openPullRequests is not null)
        {
            var matched = MatchOpenPullRequests(run, openPullRequests);
            if (matched.Count > 0)
            {
                return matched;
            }
        }

        // pull_requests 只含 head 相符且仍開啟的 PR，不一定是觸發這次 run 的那個；順序照 API，第一個是主要編號。
        var fromRun = ValidDistinct(run.PullRequestNumbers);
        if (fromRun.Count > 0)
        {
            return fromRun;
        }

        // PR 關閉後 API 不再回傳，改用第一次觀察到時存下的 runs.pr_numbers。
        return ValidDistinct(storedNumbers);
    }

    /// <summary>同一個 repo、head.sha 相同、而且 head.repo.id 等於 run 的來源 repo id 的開啟中 PR，依編號由小到大。</summary>
    private static List<int> MatchOpenPullRequests(RunInfo run, IReadOnlyCollection<PullRequestInfo> openPullRequests)
    {
        if (string.IsNullOrWhiteSpace(run.HeadSha))
        {
            return [];
        }

        return openPullRequests
            .Where(pr => pr is not null
                && pr.RepoId == run.RepoId
                && pr.HeadRepoId == run.HeadRepoId
                && string.Equals(pr.HeadSha, run.HeadSha, StringComparison.Ordinal)
                && pr.Number > 0)
            .Select(pr => pr.Number)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>去掉 ≤ 0 與重複的編號，保留第一次出現的順序。</summary>
    private static List<int> ValidDistinct(IReadOnlyList<int>? numbers)
    {
        var result = new List<int>();
        if (numbers is null)
        {
            return result;
        }

        var seen = new HashSet<int>();
        foreach (var number in numbers)
        {
            if (number > 0 && seen.Add(number))
            {
                result.Add(number);
            }
        }

        return result;
    }
}

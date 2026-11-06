using System.Globalization;
using IsItGreenYet.Core.GitHub.Dto;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.GitHub;

/// <summary>E2 的一頁 repo 清單。</summary>
/// <param name="CanPush">
/// 這一頁每個 repo 的 <c>permissions.push</c>（第二階段 spec §4.1）：回應沒有 <c>permissions</c> 物件的 repo 是 null（不確定）。
/// </param>
public sealed record RepoPage(IReadOnlyList<RepoInfo> Repos, IReadOnlyDictionary<long, bool?> CanPush);

/// <summary>spec §4.2 的 E1～E10，把 DTO 轉成 Models；第二階段 spec §3.1 的寫入端點 E11～E14；以及檢查新版本的 E15。</summary>
/// <remarks>
/// 每種資料每次用相同的 URL（參數固定、不用會重排的排序），ETag 才有效（spec §4.1）。
/// 304 時 Value 為 null、CacheEntry 是快取中原有的項目；呼叫端沿用本地資料。
/// 清單裡單筆缺必要欄位時只略過那一筆（spec §15）；repo 清單例外，整頁不採用（見 <see cref="GetRepoPageAsync"/>）。
/// </remarks>
public sealed class GitHubApi(GitHubClient client) : IActionsWriter
{
    private readonly GitHubClient _client = client;

    /// <summary>
    /// spec §4.2 E2 的四種 URL：清單 A／B（完整，不帶 sort）、探測 A／B（sort=pushed，只抓第 1 頁）。
    /// </summary>
    public static string RepoListUrl(bool collaborator, bool probe)
    {
        // 不送 type：和 affiliation 同時出現會回 422（spec §4.2 E2）。
        var affiliation = collaborator ? "collaborator" : "owner,organization_member";
        var sort = probe ? "&sort=pushed" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"/user/repos?affiliation={affiliation}{sort}&per_page={Tuning.ListPageSize}");
    }

    public async Task<ApiResult<AccountInfo>> GetUserAsync(CancellationToken ct)
    {
        var result = await _client.GetAsync<SimpleUserDto>("/user", ct: ct).ConfigureAwait(false);
        return MapSingle(result, DtoMapper.ToAccount);
    }

    /// <summary>一頁 repo 清單；Category 先填 Own，由 RepoDiscovery.Merge 決定。</summary>
    /// <remarks>
    /// 任何一筆缺必要欄位，整頁回 ParseError、不帶快取項：略過一筆會讓它被當成「從清單消失」，
    /// 連續兩輪就會被移出監看（spec §5.4 要求分頁中途失敗不算數）。
    /// </remarks>
    public async Task<ApiResult<RepoPage>> GetRepoPageAsync(string url, CancellationToken ct)
    {
        var result = await _client.GetAsync<List<RepoDto?>>(url, page => RepoIds(MapRepos(page)), ct: ct).ConfigureAwait(false);
        if (result.Outcome != ApiOutcome.Ok)
        {
            return Pass<List<RepoDto?>, RepoPage>(result, result.Outcome, null, result.CacheEntry);
        }

        var page = MapRepos(result.Value!);
        return page is null
            ? Pass<List<RepoDto?>, RepoPage>(result, ApiOutcome.ParseError, null, null, "repo 清單有項目缺少必要欄位")
            : Pass<List<RepoDto?>, RepoPage>(result, ApiOutcome.Ok, page, result.CacheEntry);
    }

    public async Task<ApiResult<IReadOnlyList<WorkflowInfo>>> GetWorkflowsAsync(RepoInfo repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var url = string.Create(CultureInfo.InvariantCulture, $"{RepoPath(repo)}/actions/workflows?per_page={Tuning.ListPageSize}");
        var result = await _client.GetAsync<WorkflowsPageDto>(url, page => MapWorkflows(repo.Id, page).Select(w => w.Id).ToArray(), ct: ct)
            .ConfigureAwait(false);
        return MapList(result, page => MapWorkflows(repo.Id, page));
    }

    /// <summary>主要輪詢：per_page=RunsPageSize，不帶任何篩選。</summary>
    public Task<ApiResult<IReadOnlyList<RunInfo>>> GetRunsAsync(RepoInfo repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return GetRunsPageAsync(repo, string.Create(CultureInfo.InvariantCulture,
            $"{RepoPath(repo)}/actions/runs?per_page={Tuning.RunsPageSize}"), ct);
    }

    /// <summary>PR 分支補抓：head_sha 篩選。</summary>
    public Task<ApiResult<IReadOnlyList<RunInfo>>> GetRunsForShaAsync(RepoInfo repo, string headSha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        return GetRunsPageAsync(repo, string.Create(CultureInfo.InvariantCulture,
            $"{RepoPath(repo)}/actions/runs?head_sha={Uri.EscapeDataString(headSha)}&per_page={Tuning.RunsPageSize}"), ct);
    }

    /// <summary>E5：某 workflow 在預設分支的 run，per_page=SeedPageSize。</summary>
    /// <remarks>分支名稱可能含 /、#、%、空白、中文，branch 參數一律用 Uri.EscapeDataString 編碼（Review Focus 1）。</remarks>
    public Task<ApiResult<IReadOnlyList<RunInfo>>> GetDefaultBranchRunsAsync(RepoInfo repo, long workflowId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo.DefaultBranch);
        return GetRunsPageAsync(repo, string.Create(CultureInfo.InvariantCulture,
            $"{RepoPath(repo)}/actions/workflows/{workflowId}/runs?branch={Uri.EscapeDataString(repo.DefaultBranch)}&per_page={Tuning.SeedPageSize}"),
            ct);
    }

    public async Task<ApiResult<RunInfo>> GetRunAsync(RepoInfo repo, long runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var url = string.Create(CultureInfo.InvariantCulture, $"{RepoPath(repo)}/actions/runs/{runId}");
        var result = await _client.GetAsync<RunDto>(url, ct: ct).ConfigureAwait(false);
        return MapSingle(result, dto => DtoMapper.ToRun(repo.Id, dto));
    }

    public async Task<ApiResult<IReadOnlyList<PullRequestInfo>>> GetOpenPullRequestsAsync(RepoInfo repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var url = string.Create(CultureInfo.InvariantCulture, $"{RepoPath(repo)}/pulls?state=open&per_page={Tuning.ListPageSize}");
        var result = await _client.GetAsync<List<PullRequestDto?>>(url,
            page => MapPullRequests(repo.Id, page).Select(pr => (long)pr.Number).ToArray(), ct: ct).ConfigureAwait(false);
        return MapList(result, page => MapPullRequests(repo.Id, page));
    }

    /// <summary>E8：filter=latest，跟著 Link 抓完所有頁（不帶 If-None-Match）。</summary>
    /// <remarks>
    /// 任何一頁失敗就回傳那一頁的錯誤（不回部分結果）。Link 指回已抓過的頁時停止，避免無限迴圈；
    /// 兩頁之間 job 清單變動造成的重複項以 id 去重。回傳的結果不帶快取項。
    /// </remarks>
    public async Task<ApiResult<IReadOnlyList<JobInfo>>> GetJobsAsync(RepoInfo repo, long runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var url = string.Create(CultureInfo.InvariantCulture, $"{RepoPath(repo)}/actions/runs/{runId}/jobs?filter=latest&per_page={Tuning.ListPageSize}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new List<JobInfo>();
        var seenIds = new HashSet<long>();
        var deprecated = false;
        Uri? movedTo = null;

        while (true)
        {
            var page = await _client.GetAsync<JobsPageDto>(url, conditional: false, ct: ct).ConfigureAwait(false);
            deprecated |= page.Deprecated;
            movedTo ??= page.MovedTo;
            if (page.Outcome != ApiOutcome.Ok)
            {
                return Pass<JobsPageDto, IReadOnlyList<JobInfo>>(page, page.Outcome, null, null, page.Message, deprecated, movedTo);
            }

            visited.Add(page.CacheEntry?.Url ?? url);
            foreach (var job in (page.Value!.Jobs ?? []).Select(DtoMapper.ToJob).OfType<JobInfo>())
            {
                if (seenIds.Add(job.Id))
                {
                    jobs.Add(job);
                }
            }

            var next = page.CacheEntry?.NextUrl;
            if (next is null || !visited.Add(next))
            {
                return Pass<JobsPageDto, IReadOnlyList<JobInfo>>(page, ApiOutcome.Ok, jobs, null, page.Message, deprecated, movedTo);
            }

            url = next;
        }
    }

    /// <summary>E10：只讀 resources.core。要的是當下的數字，不帶 If-None-Match（304 拿不到內容）。</summary>
    public async Task<ApiResult<RateLimitSnapshot>> GetRateLimitAsync(CancellationToken ct)
    {
        var result = await _client.GetAsync<RateLimitDto>("/rate_limit", conditional: false, ct: ct).ConfigureAwait(false);
        return MapSingle(result, dto => DtoMapper.ToRateLimit(dto, result.RateLimit?.ServerDate));
    }

    /// <summary>E15：專案自己的最新 release，只用於檢查新版本。</summary>
    /// <param name="conditional">false 時不查快取、不帶 If-None-Match（見 UpdateCheckRules.ShouldSendEtag）。</param>
    public async Task<ApiResult<ReleaseInfo>> GetLatestReleaseAsync(bool conditional, CancellationToken ct)
    {
        var url = GitHubClient.RepoPath(Tuning.ProjectRepoFullName) + "/releases/latest";
        var result = await _client.GetAsync<ReleaseDto>(url, conditional: conditional, ct: ct).ConfigureAwait(false);
        return MapSingle(result, DtoMapper.ToRelease);
    }

    /// <summary>E9：失敗 job 的 log 尾端。</summary>
    public Task<(ApiResult<object> Result, IReadOnlyList<string> Lines)> GetJobLogTailAsync(
        RepoInfo repo, long jobId, int tailLines, CancellationToken ct) =>
        _client.DownloadJobLogTailAsync(repo.FullName, jobId, tailLines, ct);

    /// <summary>E11：重跑失敗的 job 與其下游 job。</summary>
    public Task<ApiResult<object>> RerunFailedJobsAsync(RepoInfo repo, long runId, CancellationToken ct) =>
        PostRunActionAsync(repo, runId, "rerun-failed-jobs", ct);

    /// <summary>E12：整個 run 重跑。</summary>
    public Task<ApiResult<object>> RerunAllAsync(RepoInfo repo, long runId, CancellationToken ct) =>
        PostRunActionAsync(repo, runId, "rerun", ct);

    /// <summary>E13：取消。</summary>
    public Task<ApiResult<object>> CancelRunAsync(RepoInfo repo, long runId, CancellationToken ct) =>
        PostRunActionAsync(repo, runId, "cancel", ct);

    /// <summary>E14：強制取消。</summary>
    public Task<ApiResult<object>> ForceCancelRunAsync(RepoInfo repo, long runId, CancellationToken ct) =>
        PostRunActionAsync(repo, runId, "force-cancel", ct);

    private static string RepoPath(RepoInfo repo) => GitHubClient.RepoPath(repo.FullName);

    /// <summary><c>POST /repos/{owner}/{repo}/actions/runs/{run_id}/{action}</c>；結果原樣交回，分類與重送由 Engine 處理。</summary>
    private Task<ApiResult<object>> PostRunActionAsync(RepoInfo repo, long runId, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);
        return _client.PostAsync(string.Create(CultureInfo.InvariantCulture, $"{RepoPath(repo)}/actions/runs/{runId}/{action}"), ct);
    }

    private async Task<ApiResult<IReadOnlyList<RunInfo>>> GetRunsPageAsync(RepoInfo repo, string url, CancellationToken ct)
    {
        var result = await _client.GetAsync<RunsPageDto>(url, page => MapRuns(repo.Id, page).Select(run => run.Id).ToArray(), ct: ct)
            .ConfigureAwait(false);
        return MapList(result, page => MapRuns(repo.Id, page));
    }

    /// <summary>任何一筆無效就回 null（整頁不採用）。</summary>
    private static RepoPage? MapRepos(List<RepoDto?> page)
    {
        var repos = new List<RepoInfo>(page.Count);
        var canPush = new Dictionary<long, bool?>(page.Count);
        foreach (var dto in page)
        {
            if (DtoMapper.ToRepo(dto) is not { } repo)
            {
                return null;
            }

            repos.Add(repo);
            canPush[repo.Id] = DtoMapper.ToCanPush(dto);
        }

        return new RepoPage(repos, canPush);
    }

    private static long[] RepoIds(RepoPage? page) => page?.Repos.Select(repo => repo.Id).ToArray() ?? [];

    private static List<WorkflowInfo> MapWorkflows(long repoId, WorkflowsPageDto page) =>
        (page.Workflows ?? []).Select(dto => DtoMapper.ToWorkflow(repoId, dto)).OfType<WorkflowInfo>().ToList();

    private static List<RunInfo> MapRuns(long repoId, RunsPageDto page) =>
        (page.WorkflowRuns ?? []).Select(dto => DtoMapper.ToRun(repoId, dto)).OfType<RunInfo>().ToList();

    private static List<PullRequestInfo> MapPullRequests(long repoId, List<PullRequestDto?> page) =>
        page.Select(dto => DtoMapper.ToPullRequest(repoId, dto)).OfType<PullRequestInfo>().ToList();

    /// <summary>清單：Ok 時逐筆轉換（無效的略過），其他結果原樣傳回。</summary>
    private static ApiResult<IReadOnlyList<TItem>> MapList<TPage, TItem>(ApiResult<TPage> result, Func<TPage, List<TItem>> map) =>
        result.Outcome == ApiOutcome.Ok
            ? Pass<TPage, IReadOnlyList<TItem>>(result, ApiOutcome.Ok, map(result.Value!), result.CacheEntry)
            : Pass<TPage, IReadOnlyList<TItem>>(result, result.Outcome, default, result.CacheEntry);

    /// <summary>單一物件：Ok 但缺必要欄位時回 ParseError、不帶快取項（不更新 ETag）。</summary>
    private static ApiResult<TModel> MapSingle<TDto, TModel>(ApiResult<TDto> result, Func<TDto, TModel?> map)
        where TModel : class
    {
        if (result.Outcome != ApiOutcome.Ok)
        {
            return Pass<TDto, TModel>(result, result.Outcome, null, result.CacheEntry);
        }

        return map(result.Value!) is { } model
            ? Pass<TDto, TModel>(result, ApiOutcome.Ok, model, result.CacheEntry)
            : Pass<TDto, TModel>(result, ApiOutcome.ParseError, null, null, "回應缺少必要欄位");
    }

    /// <summary>換掉結果種類與內容，其他欄位（狀態碼、額度、header、限流資訊）原樣保留。</summary>
    private static ApiResult<TOut> Pass<TIn, TOut>(
        ApiResult<TIn> source,
        ApiOutcome outcome,
        TOut? value,
        HttpCacheEntry? cacheEntry,
        string? message = null,
        bool? deprecated = null,
        Uri? movedTo = null) => new()
    {
        Outcome = outcome,
        Value = value,
        StatusCode = source.StatusCode,
        RateLimit = source.RateLimit,
        CacheEntry = cacheEntry,
        MovedTo = movedTo ?? source.MovedTo,
        LimitKind = source.LimitKind,
        RetryWait = source.RetryWait,
        HasRetryAfter = source.HasRetryAfter,
        InsufficientPermission = source.InsufficientPermission,
        AcceptedPermissions = source.AcceptedPermissions,
        OAuthScopes = source.OAuthScopes,
        AcceptedOAuthScopes = source.AcceptedOAuthScopes,
        Message = message ?? source.Message,
        PollInterval = source.PollInterval,
        Deprecated = deprecated ?? source.Deprecated,
        Sso = source.Sso,
        WriteRejected = source.WriteRejected,
        Dispatched = source.Dispatched,
    };
}

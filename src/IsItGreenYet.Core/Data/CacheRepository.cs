using System.Globalization;
using System.Text.Json;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace IsItGreenYet.Core.Data;

/// <summary>repo 的監看狀態（repos 表中 API 欄位以外的部分）。</summary>
public sealed record RepoState(
    long RepoId,
    int? WorkflowsTotal,
    int? WorkflowsActive,
    DateTimeOffset? WorkflowsCheckedAt,
    string? SkipReason,
    string? ProblemNote,
    DateTimeOffset? LastSuccessAt,
    int MissingRounds,
    DateTimeOffset? BaselineT0);

/// <summary>重跑送出後等著確認新 attempt 的 run（第二階段 spec §6.2、§8.1），重新啟動後接著追。</summary>
/// <param name="RepoId">run 所屬的 repo：重新啟動後要排 E6（計畫的差異表第 2 列）。</param>
/// <param name="PreviousAttempt">送出時的 run_attempt；看到更大的值才算重跑開始。</param>
/// <param name="Notify">重跑完成時要通知（第二階段 spec §8.1）。</param>
public sealed record PendingRerun(long RunId, long RepoId, int PreviousAttempt, DateTimeOffset RequestedAt, bool Notify);

/// <summary>status_snapshot 表唯一的一列（第二階段 spec §10.2）；App 寫、命令列讀。</summary>
/// <param name="StoppedAt">App 正常結束的時間；執行中是 null。</param>
/// <param name="StatusJson">Engine 組好的狀態 JSON，原樣存取，不在這一層解析。</param>
public sealed record StatusSnapshotRow(DateTimeOffset WrittenAt, DateTimeOffset HeartbeatAt, DateTimeOffset? StoppedAt,
    string AppVersion, string StatusJson, long RefreshStartedSeq, long RefreshDoneSeq, bool RefreshPartial);

/// <summary>和 repo 清單同一個交易寫入的 push 權限（第二階段 spec §4.1）。</summary>
/// <param name="UserId">取得這些值時的帳號；權限的鍵是 (repo_id, user_id)。</param>
/// <param name="CanPush">值是 null 的 repo 是不確定（回應沒有 permissions 物件），不寫入。</param>
public sealed record RepoPermissionsUpdate(long UserId, IReadOnlyDictionary<long, bool?> CanPush);

/// <summary>
/// cache.db 的所有存取（spec §14.3）。時間一律存 UTC ISO 8601。
/// 會寫入快取項的方法都在同一個交易裡寫資料與 ETag，確保「有 ETag 就有對應的資料」。
/// </summary>
public sealed class CacheRepository : IHttpCacheStore, INotifiedStore
{
    /// <summary>
    /// 時間的儲存格式：UTC 的 ISO 8601 往返格式（<c>2026-10-24T02:00:00.0000000+00:00</c>）。
    /// 位數固定、時區固定，所以 SQL 裡的字串比較就等於時間比較。
    /// </summary>
    internal const string TimeFormat = "O";

    /// <summary>account 表只有一列（spec §14.3：主鍵 id = 1）。</summary>
    private const int AccountRowId = 1;

    /// <summary>status_snapshot 表只有一列（第二階段 spec §10.2：主鍵 id = 1）。</summary>
    private const int StatusSnapshotRowId = 1;

    private const string RepoColumns = """
        repo_id, full_name, name, owner_id, owner_login, owner_type, category,
        is_fork, is_archived, is_disabled, is_private, default_branch, html_url, pushed_at
        """;

    private const string RunColumns = """
        run_id, repo_id, workflow_id, workflow_name, path, run_number, run_attempt, display_title, event,
        status, conclusion, head_branch, head_sha, head_repo_id, head_repo_full_name, head_repo_html_url,
        actor_id, actor_login, triggering_actor_login, pr_numbers, run_started_at, created_at, updated_at, html_url
        """;

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    /// <summary>寫入時間（updated_at、fetched_at 等）取系統時間；測試請改用可注入時間的建構式。</summary>
    public CacheRepository(string databasePath)
        : this(databasePath, TimeProvider.System)
    {
    }

    /// <param name="time">只用來填 updated_at、fetched_at、completed_observed_at 等「寫入當下」的時間。</param>
    public CacheRepository(string databasePath, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(time);
        _connectionString = DatabaseInitializer.BuildConnectionString(databasePath);
        _time = time;
    }

    // ── 帳號 ──────────────────────────────────────────────────────────
    public AccountInfo? GetAccount()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT user_id, login FROM account WHERE id = $id;",
            ("$id", AccountRowId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new AccountInfo(Int64(reader, "user_id"), Text(reader, "login")) : null;
    }

    public void SaveAccount(AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO account (id, user_id, login, updated_at) VALUES ($id, $userId, $login, $now)
            ON CONFLICT(id) DO UPDATE SET
                user_id = excluded.user_id, login = excluded.login, updated_at = excluded.updated_at;
            """,
            ("$id", AccountRowId), ("$userId", account.UserId), ("$login", account.Login), ("$now", NowText()));
    }

    // ── repo ──────────────────────────────────────────────────────────
    public IReadOnlyList<RepoInfo> GetRepos()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            $"SELECT {RepoColumns} FROM repos ORDER BY full_name COLLATE NOCASE, repo_id;");
        return ReadAll(command, ReadRepo);
    }

    /// <summary>
    /// 只新增或更新 API 欄位，不動監看狀態（<see cref="RepoState"/>），也不刪除清單外的 repo：
    /// 探測只看第 1 頁，repo 消失要連續兩輪才算（spec §5.4），刪除由 <see cref="DeleteRepo"/> 負責。
    /// </summary>
    /// <param name="permissions">
    /// 同一個交易寫入 repo_permissions（第二階段 spec §4.1）：值是 null 的 repo 不確定，不寫、也不蓋掉之前的值；有值的 UPSERT。
    /// null 時不動權限（例如還不知道帳號）。
    /// </param>
    public void UpsertRepos(IReadOnlyList<RepoInfo> repos, IReadOnlyList<HttpCacheEntry> cacheEntries, RepoPermissionsUpdate? permissions = null)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(cacheEntries);
        if (permissions is not null)
        {
            ArgumentNullException.ThrowIfNull(permissions.CanPush, nameof(permissions));
        }

        var now = NowText();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // 權限先寫：後面任何一筆 repo 失敗，權限連同 repo 與快取項一起回復。
        if (permissions is not null)
        {
            WritePermissions(connection, transaction, permissions, now);
        }

        foreach (var repo in repos)
        {
            Execute(connection, transaction, """
                INSERT INTO repos (repo_id, full_name, name, owner_id, owner_login, owner_type, category,
                                   is_fork, is_archived, is_disabled, is_private, default_branch, html_url, pushed_at)
                VALUES ($repoId, $fullName, $name, $ownerId, $ownerLogin, $ownerType, $category,
                        $isFork, $isArchived, $isDisabled, $isPrivate, $defaultBranch, $htmlUrl, $pushedAt)
                ON CONFLICT(repo_id) DO UPDATE SET
                    full_name = excluded.full_name,
                    name = excluded.name,
                    owner_id = excluded.owner_id,
                    owner_login = excluded.owner_login,
                    owner_type = excluded.owner_type,
                    category = excluded.category,
                    is_fork = excluded.is_fork,
                    is_archived = excluded.is_archived,
                    is_disabled = excluded.is_disabled,
                    is_private = excluded.is_private,
                    default_branch = excluded.default_branch,
                    html_url = excluded.html_url,
                    pushed_at = excluded.pushed_at;
                """,
                ("$repoId", repo.Id),
                ("$fullName", repo.FullName),
                ("$name", repo.Name),
                ("$ownerId", repo.OwnerId),
                ("$ownerLogin", repo.OwnerLogin),
                ("$ownerType", repo.OwnerType),
                ("$category", repo.Category.ToString()),
                ("$isFork", Flag(repo.IsFork)),
                ("$isArchived", Flag(repo.IsArchived)),
                ("$isDisabled", Flag(repo.IsDisabled)),
                ("$isPrivate", Flag(repo.IsPrivate)),
                ("$defaultBranch", repo.DefaultBranch),
                ("$htmlUrl", repo.HtmlUrl),
                ("$pushedAt", FormatTimeOrNull(repo.PushedAt)));
        }

        foreach (var entry in cacheEntries)
        {
            WriteCache(connection, transaction, entry, now);
        }

        transaction.Commit();
    }

    /// <summary>
    /// 連同 runs、attempts、workflows、PR、耗時樣本、PR 補抓紀錄、該 repo 網址的快取，
    /// 以及所有帳號在這個 repo 的寫入權限、這個 repo 的待確認重跑（計畫的差異表第 3 列：留著只會變成孤兒資料）一起刪除。
    /// </summary>
    /// <remarks>
    /// 靜音與強制納入、完成時通知我的標記、notified 不在上述清單裡，保留不動；
    /// notified 由 <see cref="Cleanup"/> 在 run 不存在後清掉。
    /// </remarks>
    public void DeleteRepo(long repoId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        string? fullName;
        using (var lookup = Command(connection, transaction,
            "SELECT full_name FROM repos WHERE repo_id = $repoId;",
            ("$repoId", repoId)))
        {
            fullName = lookup.ExecuteScalar() as string;
        }

        Execute(connection, transaction, """
            DELETE FROM run_attempts WHERE run_id IN (SELECT run_id FROM runs WHERE repo_id = $repoId);
            DELETE FROM runs WHERE repo_id = $repoId;
            DELETE FROM workflows WHERE repo_id = $repoId;
            DELETE FROM pull_requests WHERE repo_id = $repoId;
            DELETE FROM duration_samples WHERE repo_id = $repoId;
            DELETE FROM pr_seed WHERE repo_id = $repoId;
            DELETE FROM repo_permissions WHERE repo_id = $repoId;
            DELETE FROM pending_reruns WHERE repo_id = $repoId;
            DELETE FROM repos WHERE repo_id = $repoId;
            """,
            ("$repoId", repoId));

        // 以 repo id 為路徑的網址不必查表；不在 repos 表裡就不知道名稱，
        // /repos/{full_name}/ 形式的網址只能留給之後的 DeleteCacheForPrefix。
        foreach (var prefix in RepoUrlPrefixes(repoId, fullName))
        {
            DeleteCacheForPrefix(connection, transaction, prefix);
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<long, RepoState> GetRepoStates()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT repo_id, workflows_total, workflows_active, workflows_checked_at, skip_reason, problem_note,
                   last_success_at, missing_rounds, baseline_t0
            FROM repos;
            """);
        return ReadAll(command, ReadRepoState).ToDictionary(state => state.RepoId);
    }

    /// <summary>狀態存在 repos 表裡；repo 不存在（已被移除）時不做事。</summary>
    public void SaveRepoState(RepoState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        using var connection = OpenConnection();
        Execute(connection, null, """
            UPDATE repos SET
                workflows_total = $workflowsTotal,
                workflows_active = $workflowsActive,
                workflows_checked_at = $workflowsCheckedAt,
                skip_reason = $skipReason,
                problem_note = $problemNote,
                last_success_at = $lastSuccessAt,
                missing_rounds = $missingRounds,
                baseline_t0 = $baselineT0
            WHERE repo_id = $repoId;
            """,
            ("$repoId", state.RepoId),
            ("$workflowsTotal", state.WorkflowsTotal),
            ("$workflowsActive", state.WorkflowsActive),
            ("$workflowsCheckedAt", FormatTimeOrNull(state.WorkflowsCheckedAt)),
            ("$skipReason", state.SkipReason),
            ("$problemNote", state.ProblemNote),
            ("$lastSuccessAt", FormatTimeOrNull(state.LastSuccessAt)),
            ("$missingRounds", state.MissingRounds),
            ("$baselineT0", FormatTimeOrNull(state.BaselineT0)));
    }

    public IReadOnlyDictionary<long, RepoOverride> GetOverrides()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT repo_id, muted, force_included FROM repo_overrides;");
        using var reader = command.ExecuteReader();
        var overrides = new Dictionary<long, RepoOverride>();
        while (reader.Read())
        {
            overrides[Int64(reader, "repo_id")] = new RepoOverride(Bool(reader, "muted"), Bool(reader, "force_included"));
        }

        return overrides;
    }

    /// <summary>以 repo id 為鍵（改名不影響，spec §5.2）；repo 本身不必已在 repos 表裡。</summary>
    public void SetOverride(long repoId, RepoOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO repo_overrides (repo_id, muted, force_included, updated_at)
            VALUES ($repoId, $muted, $forceIncluded, $now)
            ON CONFLICT(repo_id) DO UPDATE SET
                muted = excluded.muted, force_included = excluded.force_included, updated_at = excluded.updated_at;
            """,
            ("$repoId", repoId), ("$muted", Flag(value.Muted)), ("$forceIncluded", Flag(value.ForceIncluded)), ("$now", NowText()));
    }

    // ── workflow ──────────────────────────────────────────────────────
    public IReadOnlyList<WorkflowInfo> GetWorkflows(long repoId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT repo_id, workflow_id, name, path, state, html_url
            FROM workflows WHERE repo_id = $repoId
            ORDER BY name COLLATE NOCASE, workflow_id;
            """,
            ("$repoId", repoId));
        return ReadAll(command, reader => new WorkflowInfo(
            Int64(reader, "repo_id"),
            Int64(reader, "workflow_id"),
            Text(reader, "name"),
            Text(reader, "path"),
            Text(reader, "state"),
            Text(reader, "html_url")));
    }

    /// <summary>
    /// 整批替換這個 repo 的 workflow 清單：不在新清單裡的刪除，其餘更新。
    /// 已存在的 workflow 保留 seed_checked_at／seed_result（補抓狀態不是 API 欄位）。
    /// </summary>
    public void ReplaceWorkflows(long repoId, IReadOnlyList<WorkflowInfo> workflows, HttpCacheEntry? cache)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        EnsureSameRepo(repoId, workflows, workflow => workflow.RepoId, nameof(workflows));

        var now = NowText();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            DELETE FROM workflows
            WHERE repo_id = $repoId AND workflow_id NOT IN (SELECT value FROM json_each($keepIds));
            """,
            ("$repoId", repoId), ("$keepIds", JsonSerializer.Serialize(workflows.Select(w => w.Id).ToArray())));

        foreach (var workflow in workflows)
        {
            Execute(connection, transaction, """
                INSERT INTO workflows (repo_id, workflow_id, name, path, state, html_url)
                VALUES ($repoId, $workflowId, $name, $path, $state, $htmlUrl)
                ON CONFLICT(repo_id, workflow_id) DO UPDATE SET
                    name = excluded.name, path = excluded.path, state = excluded.state, html_url = excluded.html_url;
                """,
                ("$repoId", repoId),
                ("$workflowId", workflow.Id),
                ("$name", workflow.Name),
                ("$path", workflow.Path),
                ("$state", workflow.State),
                ("$htmlUrl", workflow.HtmlUrl));
        }

        if (cache is not null)
        {
            WriteCache(connection, transaction, cache, now);
        }

        transaction.Commit();
    }

    // ── run ───────────────────────────────────────────────────────────
    /// <summary>由新到舊（created_at）。被 <see cref="MarkRunGone"/> 標成已不存在的 run 不回傳（已移出追蹤）。</summary>
    public IReadOnlyList<RunInfo> GetRuns(long repoId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            $"SELECT {RunColumns} FROM runs WHERE repo_id = $repoId AND gone = 0 ORDER BY created_at DESC, run_id DESC;",
            ("$repoId", repoId));
        return ReadAll(command, ReadRun);
    }

    /// <summary>每個 run 看過的所有 attempt，依 attempt 由小到大；範圍與 <see cref="GetRuns"/> 相同。</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>> GetAttempts(long repoId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT a.run_id, a.run_attempt, a.status, a.conclusion, a.run_started_at, a.updated_at
            FROM run_attempts a JOIN runs r ON r.run_id = a.run_id
            WHERE r.repo_id = $repoId AND r.gone = 0
            ORDER BY a.run_id, a.run_attempt;
            """,
            ("$repoId", repoId));

        var grouped = new Dictionary<long, List<RunAttemptRecord>>();
        foreach (var attempt in ReadAll(command, ReadAttempt))
        {
            if (!grouped.TryGetValue(attempt.RunId, out var list))
            {
                list = [];
                grouped[attempt.RunId] = list;
            }

            list.Add(attempt);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<RunAttemptRecord>)pair.Value);
    }

    /// <summary>
    /// 同一個交易：upsert runs 與 attempts、<paramref name="isLatestPage"/> 時重設該 repo 的 in_latest_page、寫入快取項。
    /// pr_numbers 只增不減（PR 關閉後 API 不再回傳）。
    /// </summary>
    /// <remarks>
    /// 不是第一頁（E5、E6、PR 分支補抓）時不動既有的 in_latest_page。
    /// 出現在回應裡的 run 一定還存在，所以會清掉「已不存在」的標記。
    /// attempt 第一次被存成完成（<see cref="RunState.IsCompleted"/>）時記下 completed_observed_at，之後不改。
    /// </remarks>
    public void SaveRunsPage(long repoId, IReadOnlyList<RunInfo> runs, bool isLatestPage, HttpCacheEntry? cache)
    {
        ArgumentNullException.ThrowIfNull(runs);
        EnsureSameRepo(repoId, runs, run => run.RepoId, nameof(runs));

        var now = NowText();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // 快取項先寫：後面任何一筆失敗，整個交易連同新的 ETag 一起回復。
        if (cache is not null)
        {
            WriteCache(connection, transaction, cache, now);
        }

        if (isLatestPage)
        {
            Execute(connection, transaction,
                "UPDATE runs SET in_latest_page = 0 WHERE repo_id = $repoId AND in_latest_page <> 0;",
                ("$repoId", repoId));
        }

        foreach (var run in runs)
        {
            var pullRequestNumbers = MergePullRequestNumbers(ReadStoredPullRequestNumbers(connection, transaction, run.Id), run.PullRequestNumbers);
            UpsertRun(connection, transaction, run, pullRequestNumbers, isLatestPage, now);
            UpsertAttempt(connection, transaction, run, now);
        }

        transaction.Commit();
    }

    /// <summary>單筆 run 回 404：標成已不存在，保留本地紀錄。</summary>
    public void MarkRunGone(long runId)
    {
        using var connection = OpenConnection();
        Execute(connection, null, "UPDATE runs SET gone = 1 WHERE run_id = $runId;", ("$runId", runId));
    }

    // ── 耗時樣本 ───────────────────────────────────────────────────────
    /// <returns>由新到舊。</returns>
    public IReadOnlyList<TimeSpan> GetDurationSamples(long repoId, long workflowId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT duration_seconds FROM duration_samples
            WHERE repo_id = $repoId AND workflow_id = $workflowId
            ORDER BY completed_at DESC, run_id DESC, run_attempt DESC;
            """,
            ("$repoId", repoId), ("$workflowId", workflowId));
        return ReadAll(command, reader => TimeSpan.FromSeconds(reader.GetDouble(reader.GetOrdinal("duration_seconds"))));
    }

    /// <summary>每個 repo×workflow 只保留最近 DurationSamplesKept 筆。</summary>
    /// <remarks>同一個 (run_id, run_attempt) 重複加入時以最後一次為準；比保留範圍還舊的樣本加入後立刻被剔除。</remarks>
    public void AddDurationSample(long runId, int attempt, long repoId, long workflowId, TimeSpan duration, DateTimeOffset completedAt)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            INSERT INTO duration_samples (run_id, run_attempt, repo_id, workflow_id, duration_seconds, completed_at)
            VALUES ($runId, $attempt, $repoId, $workflowId, $seconds, $completedAt)
            ON CONFLICT(run_id, run_attempt) DO UPDATE SET
                repo_id = excluded.repo_id,
                workflow_id = excluded.workflow_id,
                duration_seconds = excluded.duration_seconds,
                completed_at = excluded.completed_at;

            DELETE FROM duration_samples
            WHERE repo_id = $repoId AND workflow_id = $workflowId
              AND rowid NOT IN (
                  SELECT rowid FROM duration_samples
                  WHERE repo_id = $repoId AND workflow_id = $workflowId
                  ORDER BY completed_at DESC, run_id DESC, run_attempt DESC
                  LIMIT $keep);
            """,
            ("$runId", runId),
            ("$attempt", attempt),
            ("$repoId", repoId),
            ("$workflowId", workflowId),
            ("$seconds", duration.TotalSeconds),
            ("$completedAt", FormatTime(completedAt)),
            ("$keep", Tuning.DurationSamplesKept));

        transaction.Commit();
    }

    // ── PR ────────────────────────────────────────────────────────────
    /// <returns>依 PR 編號由小到大。</returns>
    public IReadOnlyList<PullRequestInfo> GetPullRequests(long repoId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT repo_id, number, head_repo_id, head_repo_full_name, head_ref, head_sha, html_url
            FROM pull_requests WHERE repo_id = $repoId
            ORDER BY number;
            """,
            ("$repoId", repoId));
        return ReadAll(command, reader => new PullRequestInfo(
            Int64(reader, "repo_id"),
            Int32(reader, "number"),
            Int64OrNull(reader, "head_repo_id"),
            TextOrNull(reader, "head_repo_full_name"),
            Text(reader, "head_ref"),
            Text(reader, "head_sha"),
            Text(reader, "html_url")));
    }

    /// <summary>每次 E7 回 200 時整批替換（spec §14.3）。</summary>
    public void ReplacePullRequests(long repoId, IReadOnlyList<PullRequestInfo> pullRequests, HttpCacheEntry? cache)
    {
        ArgumentNullException.ThrowIfNull(pullRequests);
        EnsureSameRepo(repoId, pullRequests, pullRequest => pullRequest.RepoId, nameof(pullRequests));

        var now = NowText();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM pull_requests WHERE repo_id = $repoId;", ("$repoId", repoId));

        foreach (var pullRequest in pullRequests)
        {
            Execute(connection, transaction, """
                INSERT INTO pull_requests (repo_id, number, head_repo_id, head_repo_full_name, head_ref, head_sha, html_url, fetched_at)
                VALUES ($repoId, $number, $headRepoId, $headRepoFullName, $headRef, $headSha, $htmlUrl, $now)
                ON CONFLICT(repo_id, number) DO UPDATE SET
                    head_repo_id = excluded.head_repo_id,
                    head_repo_full_name = excluded.head_repo_full_name,
                    head_ref = excluded.head_ref,
                    head_sha = excluded.head_sha,
                    html_url = excluded.html_url,
                    fetched_at = excluded.fetched_at;
                """,
                ("$repoId", repoId),
                ("$number", pullRequest.Number),
                ("$headRepoId", pullRequest.HeadRepoId),
                ("$headRepoFullName", pullRequest.HeadRepoFullName),
                ("$headRef", pullRequest.HeadRef),
                ("$headSha", pullRequest.HeadSha),
                ("$htmlUrl", pullRequest.HtmlUrl),
                ("$now", now));
        }

        if (cache is not null)
        {
            WriteCache(connection, transaction, cache, now);
        }

        transaction.Commit();
    }

    public bool PrSeedChecked(long repoId, string headSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT 1 FROM pr_seed WHERE repo_id = $repoId AND head_sha = $headSha LIMIT 1;",
            ("$repoId", repoId), ("$headSha", headSha));
        return command.ExecuteScalar() is not null;
    }

    public void MarkPrSeedChecked(long repoId, string headSha, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO pr_seed (repo_id, head_sha, checked_at) VALUES ($repoId, $headSha, $checkedAt)
            ON CONFLICT(repo_id, head_sha) DO UPDATE SET checked_at = excluded.checked_at;
            """,
            ("$repoId", repoId), ("$headSha", headSha), ("$checkedAt", FormatTime(at)));
    }

    // ── 完成時通知我 ──────────────────────────────────────────────────
    public IReadOnlySet<long> GetWatched()
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null, "SELECT run_id FROM watched_runs;");
        return ReadAll(command, reader => Int64(reader, "run_id")).ToHashSet();
    }

    /// <summary>重複標記時保留第一次的 created_at；取消不存在的標記不做事。</summary>
    public void SetWatched(long runId, bool watched)
    {
        using var connection = OpenConnection();
        if (watched)
        {
            Execute(connection, null,
                "INSERT INTO watched_runs (run_id, created_at) VALUES ($runId, $now) ON CONFLICT(run_id) DO NOTHING;",
                ("$runId", runId), ("$now", NowText()));
        }
        else
        {
            Execute(connection, null, "DELETE FROM watched_runs WHERE run_id = $runId;", ("$runId", runId));
        }
    }

    // ── HTTP 快取 ─────────────────────────────────────────────────────
    /// <summary>
    /// 存下的 item_ids 無法解析時當成沒有快取：沒有可信的項目清單就不該送 ETag，
    /// 否則 304 時拿不到對應的資料（快取丟了只會重抓，spec §14.3）。
    /// </summary>
    public HttpCacheEntry? Get(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT url, etag, next_url, item_ids FROM http_cache WHERE url = $url;",
            ("$url", url));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var itemIdsJson = TextOrNull(reader, "item_ids");
        IReadOnlyList<long>? itemIds = null;
        if (itemIdsJson is not null && !TryDeserializeArray(itemIdsJson, out itemIds))
        {
            return null;
        }

        return new HttpCacheEntry(Text(reader, "url"), TextOrNull(reader, "etag"), TextOrNull(reader, "next_url"), itemIds);
    }

    public void PutCache(HttpCacheEntry entry)
    {
        using var connection = OpenConnection();
        WriteCache(connection, null, entry, NowText());
    }

    /// <summary>
    /// 刪除網址以 <paramref name="urlPrefix"/> 開頭的快取項（例如 repo 改名後的舊網址）。
    /// 逐字比對、區分大小寫；底線與百分比不當萬用字元。空白前綴會刪光所有快取，視為呼叫錯誤。
    /// </summary>
    public void DeleteCacheForPrefix(string urlPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);

        using var connection = OpenConnection();
        DeleteCacheForPrefix(connection, null, urlPrefix);
    }

    // ── 通知去重 ───────────────────────────────────────────────────────
    public bool Contains(long runId, int runAttempt, string conclusion)
    {
        ArgumentNullException.ThrowIfNull(conclusion);

        using var connection = OpenConnection();
        using var command = Command(connection, null, """
            SELECT 1 FROM notified
            WHERE run_id = $runId AND run_attempt = $runAttempt AND conclusion = $conclusion
            LIMIT 1;
            """,
            ("$runId", runId), ("$runAttempt", runAttempt), ("$conclusion", conclusion));
        return command.ExecuteScalar() is not null;
    }

    /// <summary>同一個鍵已經有紀錄時保留第一筆（去重鍵保證最多通知一次，spec §9.2）。</summary>
    public void Add(long runId, int runAttempt, string conclusion, NotificationKind? kind, bool suppressed, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(conclusion);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO notified (run_id, run_attempt, conclusion, kind, notified_at, suppressed)
            VALUES ($runId, $runAttempt, $conclusion, $kind, $notifiedAt, $suppressed)
            ON CONFLICT(run_id, run_attempt, conclusion) DO NOTHING;
            """,
            ("$runId", runId),
            ("$runAttempt", runAttempt),
            ("$conclusion", conclusion),
            ("$kind", kind?.ToString()),
            ("$notifiedAt", FormatTime(at)),
            ("$suppressed", Flag(suppressed)));
    }

    // ── 寫入與狀態表（第二階段 spec §4.1、§6.2、§10.2）──────────────────
    /// <summary>
    /// 只回 user_id 是這個帳號的資料列：其他帳號讀到的值一律視為不確定（第二階段 spec §4.1）。
    /// 沒有資料列的 repo 不在字典裡，也是不確定。寫入見 <see cref="UpsertRepos"/>。
    /// </summary>
    public IReadOnlyDictionary<long, bool> GetRepoPermissions(long userId)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT repo_id, can_push FROM repo_permissions WHERE user_id = $userId;",
            ("$userId", userId));
        using var reader = command.ExecuteReader();
        var permissions = new Dictionary<long, bool>();
        while (reader.Read())
        {
            permissions[Int64(reader, "repo_id")] = Bool(reader, "can_push");
        }

        return permissions;
    }

    /// <summary>
    /// 讀不懂的資料列（例如時間欄不是這個程式寫的格式）逐列略過，其他列照常讀回；SQLite 本身的錯誤（損毀、鎖定等）照常丟出。
    /// </summary>
    /// <param name="skipped">略過一列時呼叫：那一列的 run_id 與原因（run_id 是 INTEGER PRIMARY KEY，一定讀得到）。</param>
    /// <returns>依 run_id 由小到大。</returns>
    public IReadOnlyList<PendingRerun> GetPendingReruns(Action<long, Exception>? skipped = null)
    {
        using var connection = OpenConnection();
        using var command = Command(connection, null,
            "SELECT run_id, repo_id, prev_attempt, requested_at, notify FROM pending_reruns ORDER BY run_id;");
        using var reader = command.ExecuteReader();
        var rows = new List<PendingRerun>();
        while (reader.Read())
        {
            var runId = Int64(reader, "run_id");
            try
            {
                rows.Add(new PendingRerun(
                    runId,
                    Int64(reader, "repo_id"),
                    Int32(reader, "prev_attempt"),
                    Time(reader, "requested_at"),
                    Bool(reader, "notify")));
            }
            catch (Exception ex) when (ex is not SqliteException)
            {
                skipped?.Invoke(runId, ex);
            }
        }

        return rows;
    }

    /// <summary>同一個 run 再寫一次時整列覆蓋（例如同一個 run 又送了一次重跑）。</summary>
    public void UpsertPendingRerun(PendingRerun row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO pending_reruns (run_id, repo_id, prev_attempt, requested_at, notify)
            VALUES ($runId, $repoId, $prevAttempt, $requestedAt, $notify)
            ON CONFLICT(run_id) DO UPDATE SET
                repo_id = excluded.repo_id,
                prev_attempt = excluded.prev_attempt,
                requested_at = excluded.requested_at,
                notify = excluded.notify;
            """,
            ("$runId", row.RunId),
            ("$repoId", row.RepoId),
            ("$prevAttempt", row.PreviousAttempt),
            ("$requestedAt", FormatTime(row.RequestedAt)),
            ("$notify", Flag(row.Notify)));
    }

    /// <summary>不存在的 run 不做事。</summary>
    public void DeletePendingRerun(long runId)
    {
        using var connection = OpenConnection();
        Execute(connection, null, "DELETE FROM pending_reruns WHERE run_id = $runId;", ("$runId", runId));
    }

    public StatusSnapshotRow? GetStatusSnapshot()
    {
        using var connection = OpenConnection();
        return ReadStatusSnapshot(connection, null);
    }

    /// <summary>
    /// UPSERT id = 1，每次整列寫：status_json 與 heartbeat_at 一起寫，不單獨更新心跳（第二階段 spec §10.2）。
    /// stopped_at 是 null 時寫成 null（啟動後第一次寫入時清掉上一次結束的時間）。
    /// </summary>
    public void SaveStatusSnapshot(StatusSnapshotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var connection = OpenConnection();
        Execute(connection, null, """
            INSERT INTO status_snapshot (id, written_at, heartbeat_at, stopped_at, app_version, status_json,
                                         refresh_started_seq, refresh_done_seq, refresh_partial)
            VALUES ($id, $writtenAt, $heartbeatAt, $stoppedAt, $appVersion, $statusJson,
                    $refreshStartedSeq, $refreshDoneSeq, $refreshPartial)
            ON CONFLICT(id) DO UPDATE SET
                written_at = excluded.written_at,
                heartbeat_at = excluded.heartbeat_at,
                stopped_at = excluded.stopped_at,
                app_version = excluded.app_version,
                status_json = excluded.status_json,
                refresh_started_seq = excluded.refresh_started_seq,
                refresh_done_seq = excluded.refresh_done_seq,
                refresh_partial = excluded.refresh_partial;
            """,
            ("$id", StatusSnapshotRowId),
            ("$writtenAt", FormatTime(row.WrittenAt)),
            ("$heartbeatAt", FormatTime(row.HeartbeatAt)),
            ("$stoppedAt", FormatTimeOrNull(row.StoppedAt)),
            ("$appVersion", row.AppVersion),
            ("$statusJson", row.StatusJson),
            ("$refreshStartedSeq", row.RefreshStartedSeq),
            ("$refreshDoneSeq", row.RefreshDoneSeq),
            ("$refreshPartial", Flag(row.RefreshPartial)));
    }

    /// <summary>
    /// 讀 status_snapshot 那一列；<see cref="StatusSnapshotReader"/>（命令列的唯讀連線）也用它，欄位對應只有一份。
    /// </summary>
    /// <param name="transaction">連線上有交易時必須傳入（Microsoft.Data.Sqlite 的規定）。</param>
    internal static StatusSnapshotRow? ReadStatusSnapshot(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(connection, transaction, """
            SELECT written_at, heartbeat_at, stopped_at, app_version, status_json,
                   refresh_started_seq, refresh_done_seq, refresh_partial
            FROM status_snapshot WHERE id = $id;
            """,
            ("$id", StatusSnapshotRowId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StatusSnapshotRow(
                Time(reader, "written_at"),
                Time(reader, "heartbeat_at"),
                TimeOrNull(reader, "stopped_at"),
                Text(reader, "app_version"),
                Text(reader, "status_json"),
                Int64(reader, "refresh_started_seq"),
                Int64(reader, "refresh_done_seq"),
                Bool(reader, "refresh_partial"))
            : null;
    }

    // ── 清理 ──────────────────────────────────────────────────────────
    /// <summary>
    /// 刪除 created_at 超過 RunRetentionDays 的 run 與 attempt，但保留 <paramref name="protectedRunIds"/>
    /// （追蹤鍵的 latest 與 lastDecisive 組、被標記的 run）與 in_latest_page 的 run；
    /// notified 只刪 run_id 已不在 runs 表的紀錄。
    /// </summary>
    /// <remarks>剛好滿 RunRetentionDays 不算超過。watched_runs 裡的 run 就算呼叫端漏傳也一樣保留。</remarks>
    public void Cleanup(DateTimeOffset now, IReadOnlySet<long> protectedRunIds)
    {
        ArgumentNullException.ThrowIfNull(protectedRunIds);

        const string expiredRuns = """
            SELECT run_id FROM runs
            WHERE created_at < $cutoff
              AND in_latest_page = 0
              AND run_id NOT IN (SELECT value FROM json_each($protectedIds))
              AND run_id NOT IN (SELECT run_id FROM watched_runs)
            """;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, $"""
            DELETE FROM run_attempts WHERE run_id IN ({expiredRuns});
            DELETE FROM runs WHERE run_id IN ({expiredRuns});
            DELETE FROM notified WHERE run_id NOT IN (SELECT run_id FROM runs);
            """,
            ("$cutoff", FormatTime(now.AddDays(-Tuning.RunRetentionDays))),
            ("$protectedIds", JsonSerializer.Serialize(protectedRunIds.ToArray())));

        transaction.Commit();
    }

    // ── 內部：run 寫入 ─────────────────────────────────────────────────
    private static void UpsertRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunInfo run,
        IReadOnlyList<int> pullRequestNumbers,
        bool isLatestPage,
        string now)
    {
        // in_latest_page 取較大值：第一頁先整批歸零再設 1；其他來源（isLatestPage = false）保留原值。
        Execute(connection, transaction, $"""
            INSERT INTO runs ({RunColumns}, in_latest_page, gone, fetched_at)
            VALUES ($runId, $repoId, $workflowId, $workflowName, $path, $runNumber, $runAttempt, $displayTitle, $event,
                    $status, $conclusion, $headBranch, $headSha, $headRepoId, $headRepoFullName, $headRepoHtmlUrl,
                    $actorId, $actorLogin, $triggeringActorLogin, $prNumbers, $runStartedAt, $createdAt, $updatedAt, $htmlUrl,
                    $inLatestPage, 0, $now)
            ON CONFLICT(run_id) DO UPDATE SET
                repo_id = excluded.repo_id,
                workflow_id = excluded.workflow_id,
                workflow_name = excluded.workflow_name,
                path = excluded.path,
                run_number = excluded.run_number,
                run_attempt = excluded.run_attempt,
                display_title = excluded.display_title,
                event = excluded.event,
                status = excluded.status,
                conclusion = excluded.conclusion,
                head_branch = excluded.head_branch,
                head_sha = excluded.head_sha,
                head_repo_id = excluded.head_repo_id,
                head_repo_full_name = excluded.head_repo_full_name,
                head_repo_html_url = excluded.head_repo_html_url,
                actor_id = excluded.actor_id,
                actor_login = excluded.actor_login,
                triggering_actor_login = excluded.triggering_actor_login,
                pr_numbers = excluded.pr_numbers,
                run_started_at = excluded.run_started_at,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                html_url = excluded.html_url,
                in_latest_page = MAX(runs.in_latest_page, excluded.in_latest_page),
                gone = 0,
                fetched_at = excluded.fetched_at;
            """,
            ("$runId", run.Id),
            ("$repoId", run.RepoId),
            ("$workflowId", run.WorkflowId),
            ("$workflowName", run.WorkflowName),
            ("$path", run.Path),
            ("$runNumber", run.RunNumber),
            ("$runAttempt", run.RunAttempt),
            ("$displayTitle", run.DisplayTitle),
            ("$event", run.Event),
            ("$status", run.Status),
            ("$conclusion", run.Conclusion),
            ("$headBranch", run.HeadBranch),
            ("$headSha", run.HeadSha),
            ("$headRepoId", run.HeadRepoId),
            ("$headRepoFullName", run.HeadRepoFullName),
            ("$headRepoHtmlUrl", run.HeadRepoHtmlUrl),
            ("$actorId", run.ActorId),
            ("$actorLogin", run.ActorLogin),
            ("$triggeringActorLogin", run.TriggeringActorLogin),
            ("$prNumbers", JsonSerializer.Serialize(pullRequestNumbers)),
            ("$runStartedAt", FormatTimeOrNull(run.RunStartedAt)),
            ("$createdAt", FormatTime(run.CreatedAt)),
            ("$updatedAt", FormatTime(run.UpdatedAt)),
            ("$htmlUrl", run.HtmlUrl),
            ("$inLatestPage", Flag(isLatestPage)),
            ("$now", now));
    }

    private static void UpsertAttempt(SqliteConnection connection, SqliteTransaction transaction, RunInfo run, string now)
    {
        var observedAt = RunStateMapper.Map(run.Status, run.Conclusion).IsCompleted ? now : null;

        Execute(connection, transaction, """
            INSERT INTO run_attempts (run_id, run_attempt, status, conclusion, run_started_at, updated_at, completed_observed_at)
            VALUES ($runId, $runAttempt, $status, $conclusion, $runStartedAt, $updatedAt, $observedAt)
            ON CONFLICT(run_id, run_attempt) DO UPDATE SET
                status = excluded.status,
                conclusion = excluded.conclusion,
                run_started_at = excluded.run_started_at,
                updated_at = excluded.updated_at,
                completed_observed_at = COALESCE(run_attempts.completed_observed_at, excluded.completed_observed_at);
            """,
            ("$runId", run.Id),
            ("$runAttempt", run.RunAttempt),
            ("$status", run.Status),
            ("$conclusion", run.Conclusion),
            ("$runStartedAt", FormatTimeOrNull(run.RunStartedAt)),
            ("$updatedAt", FormatTime(run.UpdatedAt)),
            ("$observedAt", observedAt));
    }

    private static IReadOnlyList<int> ReadStoredPullRequestNumbers(SqliteConnection connection, SqliteTransaction transaction, long runId)
    {
        using var command = Command(connection, transaction,
            "SELECT pr_numbers FROM runs WHERE run_id = $runId;",
            ("$runId", runId));
        return command.ExecuteScalar() is string json ? ParsePullRequestNumbers(json) : [];
    }

    /// <summary>聯集後由小到大排序，去掉重複。</summary>
    private static IReadOnlyList<int> MergePullRequestNumbers(IReadOnlyList<int> stored, IReadOnlyList<int> incoming) =>
        stored.Concat(incoming).Distinct().Order().ToArray();

    /// <summary>格式錯誤時當成沒有編號；下一次 API 回應會再補回來。</summary>
    private static IReadOnlyList<int> ParsePullRequestNumbers(string json) =>
        TryDeserializeArray<int>(json, out var numbers) && numbers is not null ? numbers : [];

    // ── 內部：寫入權限 ─────────────────────────────────────────────────
    /// <summary>null 是不確定（回應沒有 permissions 物件）：不寫成 false，也不蓋掉之前讀到的值（第二階段 spec §4.1）。</summary>
    private static void WritePermissions(SqliteConnection connection, SqliteTransaction transaction, RepoPermissionsUpdate permissions, string now)
    {
        foreach (var (repoId, canPush) in permissions.CanPush)
        {
            if (canPush is not { } value)
            {
                continue;
            }

            Execute(connection, transaction, """
                INSERT INTO repo_permissions (repo_id, user_id, can_push, updated_at)
                VALUES ($repoId, $userId, $canPush, $now)
                ON CONFLICT(repo_id, user_id) DO UPDATE SET
                    can_push = excluded.can_push, updated_at = excluded.updated_at;
                """,
                ("$repoId", repoId), ("$userId", permissions.UserId), ("$canPush", Flag(value)), ("$now", now));
        }
    }

    // ── 內部：快取 ─────────────────────────────────────────────────────
    private static void WriteCache(SqliteConnection connection, SqliteTransaction? transaction, HttpCacheEntry entry, string now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Url);

        Execute(connection, transaction, """
            INSERT INTO http_cache (url, etag, next_url, item_ids, updated_at)
            VALUES ($url, $etag, $nextUrl, $itemIds, $now)
            ON CONFLICT(url) DO UPDATE SET
                etag = excluded.etag, next_url = excluded.next_url, item_ids = excluded.item_ids, updated_at = excluded.updated_at;
            """,
            ("$url", entry.Url),
            ("$etag", entry.ETag),
            ("$nextUrl", entry.NextUrl),
            ("$itemIds", entry.ItemIds is null ? null : JsonSerializer.Serialize(entry.ItemIds)),
            ("$now", now));
    }

    /// <summary>用 substr 逐字比對，避開 LIKE 把 repo 名稱裡的 <c>_</c>、<c>%</c> 當萬用字元。</summary>
    private static void DeleteCacheForPrefix(SqliteConnection connection, SqliteTransaction? transaction, string urlPrefix) =>
        Execute(connection, transaction,
            "DELETE FROM http_cache WHERE substr(url, 1, length($prefix)) = $prefix;",
            ("$prefix", urlPrefix));

    /// <summary>
    /// 一個 repo 的 API 網址可能以完整網址或相對路徑當快取鍵，兩種都刪。
    /// 除了自己組的 <c>/repos/{full_name}/</c>，第 2 頁以後的網址是 Link header 給的，
    /// 可能是 <c>/repositories/{repo_id}/</c> 形式（spec §4.1 分頁、轉址），一樣刪。
    /// 結尾的斜線避免刪到名稱或 id 開頭相同的其他 repo（例如 sample-app 與 sample-app-v2、1001 與 10010）。
    /// </summary>
    /// <param name="fullName">null 代表 repo 不在 repos 表裡，只回傳以 id 為路徑的前綴。</param>
    private static List<string> RepoUrlPrefixes(long repoId, string? fullName)
    {
        var id = repoId.ToString(CultureInfo.InvariantCulture);
        List<string> prefixes = [$"{Tuning.ApiBaseUrl}/repositories/{id}/", $"/repositories/{id}/"];
        if (fullName is not null)
        {
            prefixes.Add($"{Tuning.ApiBaseUrl}/repos/{fullName}/");
            prefixes.Add($"/repos/{fullName}/");
        }

        return prefixes;
    }

    // ── 內部：讀取對應 ─────────────────────────────────────────────────
    private static RepoInfo ReadRepo(SqliteDataReader reader) => new(
        Int64(reader, "repo_id"),
        Text(reader, "full_name"),
        Text(reader, "name"),
        Int64(reader, "owner_id"),
        Text(reader, "owner_login"),
        Text(reader, "owner_type"),
        Bool(reader, "is_private"),
        Bool(reader, "is_fork"),
        Bool(reader, "is_archived"),
        Bool(reader, "is_disabled"),
        Text(reader, "default_branch"),
        Text(reader, "html_url"),
        TimeOrNull(reader, "pushed_at"),
        ParseCategory(Text(reader, "category")));

    private static RepoState ReadRepoState(SqliteDataReader reader) => new(
        Int64(reader, "repo_id"),
        Int32OrNull(reader, "workflows_total"),
        Int32OrNull(reader, "workflows_active"),
        TimeOrNull(reader, "workflows_checked_at"),
        TextOrNull(reader, "skip_reason"),
        TextOrNull(reader, "problem_note"),
        TimeOrNull(reader, "last_success_at"),
        Int32(reader, "missing_rounds"),
        TimeOrNull(reader, "baseline_t0"));

    private static RunInfo ReadRun(SqliteDataReader reader) => new(
        Int64(reader, "run_id"),
        Int64(reader, "repo_id"),
        Int64(reader, "workflow_id"),
        TextOrNull(reader, "workflow_name"),
        Text(reader, "path"),
        Int32(reader, "run_number"),
        Int32(reader, "run_attempt"),
        Text(reader, "display_title"),
        Text(reader, "event"),
        TextOrNull(reader, "status"),
        TextOrNull(reader, "conclusion"),
        TextOrNull(reader, "head_branch"),
        Text(reader, "head_sha"),
        Int64(reader, "head_repo_id"),
        TextOrNull(reader, "head_repo_full_name"),
        TextOrNull(reader, "head_repo_html_url"),
        Int64OrNull(reader, "actor_id"),
        TextOrNull(reader, "actor_login"),
        TextOrNull(reader, "triggering_actor_login"),
        ParsePullRequestNumbers(Text(reader, "pr_numbers")),
        TimeOrNull(reader, "run_started_at"),
        Time(reader, "created_at"),
        Time(reader, "updated_at"),
        Text(reader, "html_url"));

    private static RunAttemptRecord ReadAttempt(SqliteDataReader reader) => new(
        Int64(reader, "run_id"),
        Int32(reader, "run_attempt"),
        TextOrNull(reader, "status"),
        TextOrNull(reader, "conclusion"),
        TimeOrNull(reader, "run_started_at"),
        Time(reader, "updated_at"));

    /// <summary>category 是本程式自己寫入的列舉名稱；讀到別的值代表檔案被外部改過。</summary>
    private static RepoCategory ParseCategory(string value) =>
        Enum.TryParse<RepoCategory>(value, ignoreCase: false, out var category) && Enum.IsDefined(category)
            ? category
            : throw new InvalidDataException($"cache.db 的 repos.category 是無法辨識的值「{value}」。");

    // ── 內部：SQLite 共用 ──────────────────────────────────────────────
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private string NowText() => FormatTime(_time.GetUtcNow());

    /// <summary>null 參數一律轉成 DBNull，讓 NOT NULL 欄位由資料庫把關。</summary>
    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static List<T> ReadAll<T>(SqliteCommand command, Func<SqliteDataReader, T> map)
    {
        var items = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(map(reader));
        }

        return items;
    }

    /// <summary>寫入前檢查：清單裡每一筆都必須屬於指定的 repo，否則整批不寫。</summary>
    private static void EnsureSameRepo<T>(long repoId, IReadOnlyList<T> items, Func<T, long> repoOf, string paramName)
    {
        foreach (var item in items)
        {
            var actual = repoOf(item);
            if (actual != repoId)
            {
                throw new ArgumentException($"清單裡有 repo_id = {actual} 的項目，和指定的 repo_id = {repoId} 不符。", paramName);
            }
        }
    }

    private static bool TryDeserializeArray<T>(string json, out IReadOnlyList<T>? values)
    {
        try
        {
            values = JsonSerializer.Deserialize<T[]>(json);
            return true;
        }
        catch (JsonException)
        {
            values = null;
            return false;
        }
    }

    private static int Flag(bool value) => value ? 1 : 0;

    internal static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string? FormatTimeOrNull(DateTimeOffset? value) => value is { } time ? FormatTime(time) : null;

    internal static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private static string Text(SqliteDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    private static string? TextOrNull(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long Int64(SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    private static long? Int64OrNull(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static int Int32(SqliteDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    private static int? Int32OrNull(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static bool Bool(SqliteDataReader reader, string column) => Int64(reader, column) != 0;

    private static DateTimeOffset Time(SqliteDataReader reader, string column) => ParseTime(Text(reader, column));

    private static DateTimeOffset? TimeOrNull(SqliteDataReader reader, string column) =>
        TextOrNull(reader, column) is { } text ? ParseTime(text) : null;
}

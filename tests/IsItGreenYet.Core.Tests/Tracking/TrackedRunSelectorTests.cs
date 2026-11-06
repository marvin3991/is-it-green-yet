using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Tracking;

public class TrackedRunSelectorTests
{
    private const long RepoId = 100;
    private const long ForkRepoId = 200;
    private const long OtherRepoId = 300;
    private const long CiWorkflowId = 1;
    private const long LintWorkflowId = 2;
    private const long DocsWorkflowId = 3;
    private const long DisabledWorkflowId = 4;
    private const string DefaultBranch = "main";
    private const string FeatureBranch = "feature/login";

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly RepoInfo Repo = new(
        Id: RepoId, FullName: "octo-org/sample-app", Name: "sample-app", OwnerId: 10, OwnerLogin: "octo-org",
        OwnerType: "Organization", IsPrivate: false, IsFork: false, IsArchived: false, IsDisabled: false,
        DefaultBranch: DefaultBranch, HtmlUrl: "https://github.com/octo-org/sample-app", PushedAt: Now,
        Category: RepoCategory.Organization);

    private static readonly TrackingKey CiDefaultKey = new(RepoId, CiWorkflowId, RepoId, DefaultBranch);

    // ── spec §7.2 每個鍵的判定 ──────────────────────────────────────────

    [Fact]
    public void 較新的成功會取代較舊的失敗()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure"),
            Run(id: 1011, number: 11, sha: "bbb", conclusion: "success"),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(CiDefaultKey, key.Key);
        Assert.Equal(KeyKind.DefaultBranch, key.Kind);
        Assert.Equal(1011, key.Latest.Id);
        Assert.False(key.Pending);
        Assert.Equal(RunCategory.Success, key.LastDecisive);
        Assert.Equal([1011L], key.LastDecisiveGroupRunIds);
        Assert.False(key.Failing);
    }

    [Fact]
    public void 較新的取消不會取代較舊的失敗()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure"),
            Run(id: 1011, number: 11, sha: "bbb", conclusion: "cancelled"),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(1011, key.Latest.Id);
        Assert.False(key.Pending);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.Equal([1010L], key.LastDecisiveGroupRunIds);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 失敗後重跑再被取消仍算失敗()
    {
        var input = Input(
            [
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
                Run(id: 1011, number: 11, sha: "bbb", conclusion: "cancelled", attempt: 2),
            ],
            attempts: new Dictionary<long, IReadOnlyList<RunAttemptRecord>>
            {
                [1011] = [Attempt(1011, 1, "completed", "failure")],
            });

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(1011, key.Latest.Id);
        Assert.False(key.Pending);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.Equal([1011L], key.LastDecisiveGroupRunIds);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 重跑進行中時沿用上一次attempt的失敗()
    {
        var input = Input(
            [
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
                Run(id: 1011, number: 11, sha: "bbb", status: "in_progress", conclusion: null, attempt: 2),
            ],
            attempts: new Dictionary<long, IReadOnlyList<RunAttemptRecord>>
            {
                [1011] = [Attempt(1011, 1, "completed", "failure")],
            });

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(1011, key.Latest.Id);
        Assert.True(key.Pending);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 同一commit的push失敗與pull_request成功仍算失敗()
    {
        var input = Input(
            [
                Run(id: 1040, number: 40, sha: "ccc", conclusion: "failure", branch: FeatureBranch, evt: "push"),
                Run(id: 1041, number: 41, sha: "ccc", conclusion: "success", branch: FeatureBranch, evt: "pull_request"),
            ],
            pullRequests: [Pr(number: 5, headRef: FeatureBranch, headSha: "ccc")]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(new TrackingKey(RepoId, CiWorkflowId, RepoId, FeatureBranch), key.Key);
        Assert.Equal(KeyKind.PullRequestBranch, key.Kind);
        Assert.Equal(1041, key.Latest.Id);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.Equal([1041L, 1040L], key.LastDecisiveGroupRunIds);
        Assert.True(key.Failing);
    }

    [Fact]
    public void PR關閉後不再追蹤該分支()
    {
        var runs = new[]
        {
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
            Run(id: 1020, number: 20, sha: "fff", conclusion: "failure", branch: FeatureBranch, evt: "pull_request"),
        };

        // 開啟中清單是空的，或只剩別的分支：feature/login 都不再有鍵。
        PullRequestInfo[][] scenarios = [[], [Pr(number: 9, headRef: "feature/other", headSha: "zzz")]];
        foreach (var openPullRequests in scenarios)
        {
            var keys = TrackedRunSelector.Evaluate(Input(runs, pullRequests: openPullRequests), Now);

            var key = Assert.Single(keys);
            Assert.Equal(KeyKind.DefaultBranch, key.Kind);
            Assert.DoesNotContain(keys, k => k.Kind == KeyKind.PullRequestBranch);
        }
    }

    [Fact]
    public void fork的同名main分支不算預設分支()
    {
        // 用 push 事件，單獨驗證「來源 repo 必須是自己」這個條件。
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", headRepoId: ForkRepoId)]);

        Assert.Empty(TrackedRunSelector.Evaluate(input, Now));
    }

    [Fact]
    public void pull_request_target不算預設分支()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", evt: "pull_request_target")]);

        Assert.Empty(TrackedRunSelector.Evaluate(input, Now));
    }

    [Fact]
    public void 停用的workflow不追蹤()
    {
        var input = Input(
            [Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", workflowId: DisabledWorkflowId)],
            workflows: [Workflow(CiWorkflowId), Workflow(DisabledWorkflowId, "disabled_manually")]);

        Assert.Empty(TrackedRunSelector.Evaluate(input, Now));
        Assert.Empty(TrackedRunSelector.UnknownWorkflowIds(input));
    }

    [Fact]
    public void 未知workflow先當成active()
    {
        const long unknownWorkflowId = 999;
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", workflowId: unknownWorkflowId)]);

        Assert.Equal([unknownWorkflowId], TrackedRunSelector.UnknownWorkflowIds(input));
        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));
        Assert.Equal(new TrackingKey(RepoId, unknownWorkflowId, RepoId, DefaultBranch), key.Key);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 超過90天的失敗不算failing()
    {
        var updatedAt = Now - TimeSpan.FromDays(91);
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: updatedAt)]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(updatedAt, key.LastDecisiveAt);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.False(key.Failing);
    }

    [Fact]
    public void 重跑舊run不搶走latest()
    {
        var input = Input(
            [
                // #10 剛被重跑成功，updated_at 比 #11 新，但 run_number 較小。
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "success", attempt: 2, updatedAt: Now.AddMinutes(-1)),
                Run(id: 1011, number: 11, sha: "bbb", conclusion: "failure", updatedAt: Now.AddMinutes(-30)),
            ],
            attempts: new Dictionary<long, IReadOnlyList<RunAttemptRecord>>
            {
                [1010] = [Attempt(1010, 1, "completed", "failure")],
            });

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(1011, key.Latest.Id);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.Equal([1011L], key.LastDecisiveGroupRunIds);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 多個workflow各自成鍵()
    {
        var input = Input(
            [
                Run(id: 2005, number: 5, sha: "aaa", conclusion: "success", workflowId: LintWorkflowId),
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", workflowId: CiWorkflowId),
            ],
            workflows: [Workflow(CiWorkflowId), Workflow(LintWorkflowId)]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Collection(
            keys,
            ci =>
            {
                Assert.Equal(CiWorkflowId, ci.Key.WorkflowId);
                Assert.True(ci.Failing);
            },
            lint =>
            {
                Assert.Equal(LintWorkflowId, lint.Key.WorkflowId);
                Assert.Equal(RunCategory.Success, lint.LastDecisive);
                Assert.False(lint.Failing);
            });
    }

    [Fact]
    public void 完全沒有資料回傳空清單()
    {
        var input = new RepoTrackingInput(Repo, [], [], new Dictionary<long, IReadOnlyList<RunAttemptRecord>>(), null);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Empty(keys);
        Assert.Empty(TrackedRunSelector.UnknownWorkflowIds(input));
        Assert.Empty(TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Fact]
    public void 預設分支沒有決定性結論時列為補抓候選()
    {
        var input = Input(
            [
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "cancelled", workflowId: CiWorkflowId),
                Run(id: 2005, number: 5, sha: "aaa", conclusion: "success", workflowId: LintWorkflowId),
                Run(id: 4001, number: 1, sha: "aaa", conclusion: "cancelled", workflowId: DisabledWorkflowId),
            ],
            workflows: [
                Workflow(CiWorkflowId),
                Workflow(LintWorkflowId),
                Workflow(DocsWorkflowId),
                Workflow(DisabledWorkflowId, "disabled_inactivity"),
            ]);

        var keys = TrackedRunSelector.Evaluate(input, Now);
        var candidates = TrackedRunSelector.DefaultBranchSeedCandidates(input, keys);

        // CI 只有取消；Docs 本地沒有任何 run；Lint 已有成功；停用的 workflow 不補抓。
        Assert.Equal([CiWorkflowId, DocsWorkflowId], candidates);
    }

    [Fact]
    public void PR最新sha沒有run時列為補抓候選()
    {
        var input = Input(
            [
                Run(id: 1020, number: 20, sha: "old", conclusion: "failure", branch: "feature/a", evt: "pull_request"),
                Run(id: 1030, number: 30, sha: "same", conclusion: "success", branch: "feature/c", evt: "pull_request"),
            ],
            pullRequests: [
                Pr(number: 5, headRef: "feature/a", headSha: "new"),
                Pr(number: 6, headRef: "feature/b", headSha: "bbb"),
                Pr(number: 7, headRef: "feature/c", headSha: "same"),
            ]);

        var candidates = TrackedRunSelector.PullRequestSeedCandidates(input);

        // #5 本地最新 run 的 sha 不是 PR 的 head；#6 本地沒有這個分支的 run；#7 已經有最新 sha 的 run。
        Assert.Equal([5, 6], candidates.Select(pr => pr.Number));
    }

    // ── 補充：邊界與例外 ────────────────────────────────────────────────

    [Fact]
    public void 等待核准的latest算進行中()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
            Run(id: 1011, number: 11, sha: "bbb", status: "waiting", conclusion: null),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.True(key.Pending);
        Assert.Equal(RunCategory.Success, key.LastDecisive);
        Assert.False(key.Failing);
    }

    [Fact]
    public void 狀態不認得的latest在完成前算進行中()
    {
        var input = Input([Run(id: 1011, number: 11, sha: "bbb", status: "brand_new_status", conclusion: null)]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.True(key.Pending);
        Assert.Null(key.LastDecisive);
        Assert.Empty(key.LastDecisiveGroupRunIds);
        Assert.Null(key.LastDecisiveAt);
        Assert.False(key.Failing);
    }

    [Fact]
    public void 正在跑的新commit不會蓋掉較舊的失敗()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure"),
            Run(id: 1011, number: 11, sha: "bbb", status: "in_progress", conclusion: null),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.True(key.Pending);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 組的先後依組內最大的run_number()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure"),
            Run(id: 1012, number: 12, sha: "bbb", conclusion: "success"),
            // 同一個 commit 之後又手動跑了一次（例如 workflow_dispatch），這組的組序變成 13。
            Run(id: 1013, number: 13, sha: "aaa", conclusion: "success", evt: "workflow_dispatch"),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(1013, key.Latest.Id);
        Assert.Equal(RunCategory.Failure, key.LastDecisive);
        Assert.Equal([1013L, 1010L], key.LastDecisiveGroupRunIds);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 失敗組的時間取組內最晚的updated_at()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: Now.AddDays(-100)),
            Run(id: 1011, number: 11, sha: "aaa", conclusion: "cancelled", updatedAt: Now.AddDays(-10), evt: "schedule"),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(Now.AddDays(-10), key.LastDecisiveAt);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 剛好90天的失敗仍算failing()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: Now - TimeSpan.FromDays(90))]);

        Assert.True(Assert.Single(TrackedRunSelector.Evaluate(input, Now)).Failing);
    }

    [Fact]
    public void 時間戳比本機未來的失敗仍算failing()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: Now.AddHours(1))]);

        Assert.True(Assert.Single(TrackedRunSelector.Evaluate(input, Now)).Failing);
    }

    [Fact]
    public void now的時區不影響90天判斷()
    {
        var updatedAt = Now - TimeSpan.FromDays(91);
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: updatedAt)]);
        var taipeiNow = Now.ToOffset(TimeSpan.FromHours(8));

        Assert.False(Assert.Single(TrackedRunSelector.Evaluate(input, taipeiNow)).Failing);
        Assert.True(Assert.Single(TrackedRunSelector.Evaluate(input, taipeiNow.AddDays(-2))).Failing);
    }

    [Fact]
    public void head_branch為null的run不屬於任何鍵()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", branch: null)]);

        Assert.Empty(TrackedRunSelector.Evaluate(input, Now));
    }

    [Fact]
    public void PR追蹤停用時只產生預設分支鍵()
    {
        var input = Input(
            [
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
                Run(id: 1020, number: 20, sha: "fff", conclusion: "failure", branch: FeatureBranch, evt: "pull_request"),
            ],
            pullRequests: null);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(KeyKind.DefaultBranch, key.Kind);
        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Fact]
    public void fork的PR分支以來源repo與分支精確比對()
    {
        var input = Input(
            [
                // fork 的 PR：分支剛好也叫 main，事件是 pull_request。
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", evt: "pull_request", headRepoId: ForkRepoId),
                // 自己 repo 上同名的 feature 分支，但 PR 來自 fork 的 feature：不算。
                Run(id: 1011, number: 11, sha: "bbb", conclusion: "failure", branch: "feature", evt: "push"),
            ],
            pullRequests: [
                Pr(number: 5, headRef: DefaultBranch, headSha: "aaa", headRepoId: ForkRepoId),
                Pr(number: 6, headRef: "feature", headSha: "ccc", headRepoId: ForkRepoId),
            ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(new TrackingKey(RepoId, CiWorkflowId, ForkRepoId, DefaultBranch), key.Key);
        Assert.Equal(KeyKind.PullRequestBranch, key.Kind);
        Assert.True(key.Failing);
    }

    [Fact]
    public void PR的來源repo未知時不追蹤也不補抓()
    {
        var input = Input(
            [Run(id: 1020, number: 20, sha: "fff", conclusion: "failure", branch: FeatureBranch, evt: "pull_request")],
            pullRequests: [Pr(number: 5, headRef: FeatureBranch, headSha: "ggg", headRepoId: null)]);

        Assert.Empty(TrackedRunSelector.Evaluate(input, Now));
        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Fact]
    public void 從預設分支開出的PR把pull_request的run併入預設分支鍵()
    {
        var runs = new[]
        {
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "success", evt: "push"),
            Run(id: 1011, number: 11, sha: "aaa", conclusion: "failure", evt: "pull_request"),
        };

        // 沒有開啟中的 PR：pull_request 事件不算預設分支的 run。
        var withoutPr = Assert.Single(TrackedRunSelector.Evaluate(Input(runs, pullRequests: []), Now));
        Assert.Equal(1010, withoutPr.Latest.Id);
        Assert.False(withoutPr.Failing);

        // 有一個從 main 開出的 PR：同一個鍵只出現一次，所有事件都算。
        var withPr = TrackedRunSelector.Evaluate(Input(runs, pullRequests: [Pr(number: 8, headRef: DefaultBranch, headSha: "aaa")]), Now);
        var key = Assert.Single(withPr);
        Assert.Equal(CiDefaultKey, key.Key);
        Assert.Equal(KeyKind.DefaultBranch, key.Kind);
        Assert.Equal(1011, key.Latest.Id);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 同一個分支有兩個開啟中PR時只有一個鍵()
    {
        var input = Input(
            [Run(id: 1020, number: 20, sha: "fff", conclusion: "failure", branch: FeatureBranch, evt: "pull_request")],
            pullRequests: [
                Pr(number: 5, headRef: FeatureBranch, headSha: "fff"),
                Pr(number: 6, headRef: FeatureBranch, headSha: "fff"),
            ]);

        Assert.Single(TrackedRunSelector.Evaluate(input, Now));
        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Fact]
    public void 重複的run只取attempt最新的一筆()
    {
        var input = Input([
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", attempt: 1, updatedAt: Now.AddMinutes(-20)),
            Run(id: 1010, number: 10, sha: "aaa", conclusion: "success", attempt: 2, updatedAt: Now.AddMinutes(-5)),
        ]);

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Equal(2, key.Latest.RunAttempt);
        Assert.Equal([1010L], key.LastDecisiveGroupRunIds);
        Assert.Equal(RunCategory.Success, key.LastDecisive);
    }

    [Fact]
    public void attempt紀錄落後時以較新的RunInfo為準()
    {
        // DB 的 attempt 1 還停在 in_progress，RunInfo 已經是完成的 attempt 1。
        var input = Input(
            [Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: Now.AddMinutes(-1))],
            attempts: new Dictionary<long, IReadOnlyList<RunAttemptRecord>>
            {
                [1010] = [Attempt(1010, 1, "in_progress", null, Now.AddMinutes(-10))],
            });

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.False(key.Pending);
        Assert.True(key.Failing);
    }

    [Fact]
    public void 屬於別的run的attempt紀錄不列入()
    {
        var input = Input(
            [Run(id: 1011, number: 11, sha: "bbb", conclusion: "cancelled", attempt: 2)],
            attempts: new Dictionary<long, IReadOnlyList<RunAttemptRecord>>
            {
                [1011] = [Attempt(9999, 1, "completed", "failure")],
            });

        var key = Assert.Single(TrackedRunSelector.Evaluate(input, Now));

        Assert.Null(key.LastDecisive);
        Assert.False(key.Failing);
    }

    [Fact]
    public void 其他repo的資料不列入()
    {
        var input = Input(
            [
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", repoId: OtherRepoId, workflowId: 77, headRepoId: OtherRepoId),
            ],
            workflows: [Workflow(CiWorkflowId), Workflow(88, repoId: OtherRepoId)],
            pullRequests: [Pr(number: 5, headRef: FeatureBranch, headSha: "ggg", repoId: OtherRepoId)]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Empty(keys);
        Assert.Empty(TrackedRunSelector.UnknownWorkflowIds(input));
        Assert.Equal([CiWorkflowId], TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Fact]
    public void 結果依種類與workflow排序()
    {
        var input = Input(
            [
                Run(id: 2020, number: 20, sha: "fff", conclusion: "success", branch: FeatureBranch, evt: "pull_request", workflowId: LintWorkflowId),
                Run(id: 2005, number: 5, sha: "aaa", conclusion: "success", workflowId: LintWorkflowId),
                Run(id: 1020, number: 20, sha: "fff", conclusion: "success", branch: FeatureBranch, evt: "pull_request"),
                Run(id: 1010, number: 10, sha: "aaa", conclusion: "success"),
            ],
            workflows: [Workflow(LintWorkflowId), Workflow(CiWorkflowId)],
            pullRequests: [Pr(number: 5, headRef: FeatureBranch, headSha: "fff")]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Equal(
            [
                (KeyKind.DefaultBranch, CiWorkflowId),
                (KeyKind.DefaultBranch, LintWorkflowId),
                (KeyKind.PullRequestBranch, CiWorkflowId),
                (KeyKind.PullRequestBranch, LintWorkflowId),
            ],
            keys.Select(k => (k.Kind, k.Key.WorkflowId)));
    }

    [Fact]
    public void 未知workflow依id排序且不重複()
    {
        var input = Input([
            Run(id: 1, number: 1, sha: "aaa", conclusion: "success", workflowId: 900),
            Run(id: 2, number: 2, sha: "aaa", conclusion: "success", workflowId: 500),
            Run(id: 3, number: 3, sha: "bbb", conclusion: "success", workflowId: 900),
            Run(id: 4, number: 4, sha: "bbb", conclusion: "success", workflowId: CiWorkflowId),
        ]);

        Assert.Equal([500L, 900L], TrackedRunSelector.UnknownWorkflowIds(input));
    }

    [Fact]
    public void 未知workflow也列為補抓候選()
    {
        // 只在功能分支上看過這個 workflow，預設分支沒有鍵。
        var input = Input([Run(id: 1, number: 1, sha: "aaa", conclusion: "success", workflowId: 900, branch: FeatureBranch)]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Equal([CiWorkflowId, 900L], TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
    }

    [Fact]
    public void 預設分支只有PR事件的run時列為補抓候選()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "success", evt: "pull_request")]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Empty(keys);
        Assert.Equal([CiWorkflowId], TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
    }

    [Fact]
    public void 超過90天的失敗仍有決定性結論不列為補抓候選()
    {
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", updatedAt: Now - TimeSpan.FromDays(120))]);

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Empty(TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 預設分支空白時不產生預設分支鍵也不補抓(string defaultBranch)
    {
        var repo = Repo with { DefaultBranch = defaultBranch };
        var input = Input([Run(id: 1010, number: 10, sha: "aaa", conclusion: "failure", branch: defaultBranch)]) with { Repo = repo };

        var keys = TrackedRunSelector.Evaluate(input, Now);

        Assert.Empty(keys);
        Assert.Empty(TrackedRunSelector.DefaultBranchSeedCandidates(input, keys));
    }

    [Fact]
    public void PR補抓候選依head_sha去重並略過空白sha()
    {
        var input = Input(
            [],
            pullRequests: [
                Pr(number: 7, headRef: "feature/b", headSha: "same"),
                Pr(number: 5, headRef: "feature/a", headSha: "same", headRepoId: ForkRepoId),
                Pr(number: 6, headRef: "feature/c", headSha: " "),
            ]);

        var candidates = TrackedRunSelector.PullRequestSeedCandidates(input);

        Assert.Equal([5], candidates.Select(pr => pr.Number));
    }

    [Fact]
    public void PR補抓比對跨workflow最新建立的run()
    {
        var input = Input(
            [
                // 不同 workflow 的 run_number 不能互相比較，改用 created_at。
                Run(id: 1050, number: 50, sha: "old", conclusion: "success", branch: FeatureBranch, createdAt: Now.AddHours(-3)),
                Run(id: 2003, number: 3, sha: "new", conclusion: "success", branch: FeatureBranch, workflowId: LintWorkflowId, createdAt: Now.AddHours(-1)),
            ],
            workflows: [Workflow(CiWorkflowId), Workflow(LintWorkflowId)],
            pullRequests: [Pr(number: 5, headRef: FeatureBranch, headSha: "new")]);

        Assert.Empty(TrackedRunSelector.PullRequestSeedCandidates(input));
    }

    [Theory]
    [InlineData("pull_request", true)]
    [InlineData("pull_request_target", true)]
    [InlineData("pull_request_review", true)]
    [InlineData("pull_request_review_comment", true)]
    [InlineData("push", false)]
    [InlineData("schedule", false)]
    [InlineData("workflow_dispatch", false)]
    [InlineData("workflow_run", false)]
    [InlineData("dynamic", false)]
    [InlineData("", false)]
    [InlineData("PULL_REQUEST", false)]
    public void 只有pull_request開頭的事件算PR事件(string eventName, bool expected)
    {
        Assert.Equal(expected, TrackedRunSelector.IsPullRequestEvent(eventName));
    }

    [Fact]
    public void IsPullRequestEvent收到null時不丟例外()
    {
        Assert.False(TrackedRunSelector.IsPullRequestEvent(null!));
    }

    [Fact]
    public void 參數為null時丟出ArgumentNullException()
    {
        var input = Input([]);

        Assert.Throws<ArgumentNullException>(() => TrackedRunSelector.Evaluate(null!, Now));
        Assert.Throws<ArgumentNullException>(() => TrackedRunSelector.UnknownWorkflowIds(null!));
        Assert.Throws<ArgumentNullException>(() => TrackedRunSelector.DefaultBranchSeedCandidates(null!, []));
        Assert.Throws<ArgumentNullException>(() => TrackedRunSelector.DefaultBranchSeedCandidates(input, null!));
        Assert.Throws<ArgumentNullException>(() => TrackedRunSelector.PullRequestSeedCandidates(null!));
    }

    // ── 測試資料（手寫假資料）────────────────────────────────────────────

    private static RepoTrackingInput Input(
        IReadOnlyCollection<RunInfo> runs,
        IReadOnlyCollection<WorkflowInfo>? workflows = null,
        IReadOnlyCollection<PullRequestInfo>? pullRequests = null,
        IReadOnlyDictionary<long, IReadOnlyList<RunAttemptRecord>>? attempts = null) =>
        new(
            Repo,
            workflows ?? [Workflow(CiWorkflowId)],
            runs,
            attempts ?? new Dictionary<long, IReadOnlyList<RunAttemptRecord>>(),
            pullRequests);

    private static WorkflowInfo Workflow(long id, string state = "active", long repoId = RepoId) =>
        new(repoId, id, $"Workflow {id}", $".github/workflows/w{id}.yml", state,
            $"https://github.com/octo-org/sample-app/actions/workflows/w{id}.yml");

    private static RunInfo Run(
        long id,
        int number,
        string sha,
        string? conclusion,
        string status = "completed",
        long workflowId = CiWorkflowId,
        string? branch = DefaultBranch,
        string evt = "push",
        long headRepoId = RepoId,
        long repoId = RepoId,
        int attempt = 1,
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? createdAt = null)
    {
        var updated = updatedAt ?? Now.AddHours(-1);
        var created = createdAt ?? updated.AddMinutes(-5);
        return new RunInfo(
            Id: id, RepoId: repoId, WorkflowId: workflowId, WorkflowName: $"Workflow {workflowId}",
            Path: $".github/workflows/w{workflowId}.yml", RunNumber: number, RunAttempt: attempt,
            DisplayTitle: $"Run {number}", Event: evt, Status: status, Conclusion: conclusion,
            HeadBranch: branch, HeadSha: sha, HeadRepoId: headRepoId, HeadRepoFullName: "octo-org/sample-app",
            HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app", ActorId: 9, ActorLogin: "alice",
            TriggeringActorLogin: "alice", PullRequestNumbers: [], RunStartedAt: created, CreatedAt: created,
            UpdatedAt: updated, HtmlUrl: $"https://github.com/octo-org/sample-app/actions/runs/{id}");
    }

    private static RunAttemptRecord Attempt(long runId, int attempt, string? status, string? conclusion, DateTimeOffset? updatedAt = null)
    {
        var updated = updatedAt ?? Now.AddHours(-2);
        return new RunAttemptRecord(runId, attempt, status, conclusion, updated.AddMinutes(-5), updated);
    }

    private static PullRequestInfo Pr(int number, string headRef, string headSha, long? headRepoId = RepoId, long repoId = RepoId) =>
        new(repoId, number, headRepoId, headRepoId is null ? null : "octo-org/sample-app", headRef, headSha,
            $"https://github.com/octo-org/sample-app/pull/{number}");
}

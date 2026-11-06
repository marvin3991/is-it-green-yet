using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tests.Engine;

public class ActionRulesTests
{
    private const long RunId = 9001;

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 2, 0, 0, TimeSpan.Zero);

    /// <summary>共通條件都不成立、沒有送出中、沒有待確認的重跑、不在取消中。</summary>
    private static readonly ActionContext Clear = new(ActionBlock.None, InFlight: false, RerunPending: false, Cancelling: null,
        CancelGraceElapsed: false);

    private static readonly CancellingInfo CancelAccepted = new(Now.AddMinutes(-1), ForceSent: false);

    // ── 依 run 狀態（第二階段 spec §7.1 表格，每一格一列）──────────────

    [Theory]
    [InlineData("queued", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("in_progress", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("requested", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("pending", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("brand_new_status", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData(null, null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("waiting", null, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None)]
    [InlineData("completed", "failure", ActionBlock.None, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "timed_out", ActionBlock.None, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "startup_failure", ActionBlock.NoRerunnableJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("startup_failure", null, ActionBlock.NoRerunnableJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("startup_failure", "startup_failure", ActionBlock.NoRerunnableJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "cancelled", ActionBlock.None, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "success", ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "skipped", ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "neutral", ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "stale", ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "brand_new", ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", null, ActionBlock.NoFailedJobs, ActionBlock.None, ActionBlock.Completed)]
    [InlineData("completed", "action_required", ActionBlock.AwaitingApproval, ActionBlock.AwaitingApproval, ActionBlock.Completed)]
    public void 依run狀態的可操作性照spec的表格(
        string? status, string? conclusion, ActionBlock rerunFailedJobs, ActionBlock rerunAll, ActionBlock cancel)
    {
        var actions = ActionRules.Evaluate(Run(status, conclusion), Workflow(), Now, Clear);

        // 不在取消中：強制取消一律不出現（§6.4 的條件不成立）。
        Assert.Equal(
            new RunActions(FakeData.RepoId, 1, rerunFailedJobs, rerunAll, cancel, ShowForceCancel: false, ActionBlock.ForceNotYet),
            actions);
    }

    [Fact]
    public void 結果帶出run所屬的repo與最新的run_attempt()
    {
        var actions = ActionRules.Evaluate(Run("completed", "failure", attempt: 3), Workflow(), Now, Clear);

        Assert.Equal(FakeData.RepoId, actions.RepoId);
        Assert.Equal(3, actions.RunAttempt);
    }

    // ── 共通條件（§7.2）────────────────────────────────────────────────

    [Fact]
    public void 送出中時四個動作都停用()
    {
        var inFlight = Clear with { InFlight = true };

        AssertAllBlocked(ActionRules.Evaluate(Run("completed", "failure"), Workflow(), Now, inFlight), ActionBlock.InFlight);
        AssertAllBlocked(ActionRules.Evaluate(Run("in_progress", null), Workflow(), Now, inFlight), ActionBlock.InFlight);

        // 送出中排在其他共通條件之前。
        AssertAllBlocked(
            ActionRules.Evaluate(Run("completed", "failure"), Workflow(), Now, inFlight with { GlobalBlock = ActionBlock.Offline }),
            ActionBlock.InFlight);
    }

    [Fact]
    public void 缺權限時四個動作都停用()
    {
        var missing = Clear with { GlobalBlock = ActionBlock.MissingPermission };

        AssertAllBlocked(ActionRules.Evaluate(Run("completed", "failure"), Workflow(), Now, missing), ActionBlock.MissingPermission);
        AssertAllBlocked(ActionRules.Evaluate(Run("in_progress", null), Workflow(), Now, missing), ActionBlock.MissingPermission);
    }

    // ── 只限重跑（§7.3）──────────────────────────────────────────────

    [Fact]
    public void status是startup_failure算已完成不是未完成()
    {
        var run = Run("startup_failure", null);

        var actions = ActionRules.Evaluate(run, Workflow(), Now, Clear);
        Assert.Equal(ActionBlock.NoRerunnableJobs, actions.RerunFailedJobs);
        Assert.Equal(ActionBlock.None, actions.RerunAll);
        Assert.Equal(ActionBlock.Completed, actions.Cancel);

        // 已完成的 run 不出現強制取消，即使本地還記著取消中而且超過 5 分鐘。
        var cancelling = ActionRules.Evaluate(run, Workflow(), Now, Clear with { Cancelling = CancelAccepted, CancelGraceElapsed = true });
        Assert.False(cancelling.ShowForceCancel);
    }

    [Fact]
    public void workflow停用不能重跑()
    {
        var disabled = Workflow(state: "disabled_manually");

        var failed = ActionRules.Evaluate(Run("completed", "failure"), disabled, Now, Clear);
        Assert.Equal(ActionBlock.WorkflowDisabled, failed.RerunFailedJobs);
        Assert.Equal(ActionBlock.WorkflowDisabled, failed.RerunAll);
        Assert.Equal(ActionBlock.Completed, failed.Cancel);

        // 未完成的照樣是「還沒完成」（第一個成立的條件）；本地沒有 workflow 清單時不擋，由伺服器決定。
        Assert.Equal(ActionBlock.NotCompleted, ActionRules.Evaluate(Run("in_progress", null), disabled, Now, Clear).RerunAll);
        Assert.Equal(ActionBlock.None, ActionRules.Evaluate(Run("completed", "failure"), workflow: null, Now, Clear).RerunAll);
    }

    [Fact]
    public void 剛好30天還能重跑而30天又1秒不行()
    {
        var window = TimeSpan.FromDays(Tuning.RerunWindowDays);

        var edge = ActionRules.Evaluate(Run("completed", "failure", created: Now - window), Workflow(), Now, Clear);
        Assert.Equal(ActionBlock.None, edge.RerunFailedJobs);
        Assert.Equal(ActionBlock.None, edge.RerunAll);

        var old = ActionRules.Evaluate(Run("completed", "failure", created: Now - window - TimeSpan.FromSeconds(1)), Workflow(), Now, Clear);
        Assert.Equal(ActionBlock.TooOld, old.RerunFailedJobs);
        Assert.Equal(ActionBlock.TooOld, old.RerunAll);
        Assert.Equal(ActionBlock.Completed, old.Cancel);
    }

    [Fact]
    public void run_attempt是50還能重跑而51不行()
    {
        var fifty = ActionRules.Evaluate(Run("completed", "failure", attempt: Tuning.MaxReruns), Workflow(), Now, Clear);
        Assert.Equal(ActionBlock.None, fifty.RerunFailedJobs);
        Assert.Equal(ActionBlock.None, fifty.RerunAll);

        var fiftyOne = ActionRules.Evaluate(Run("completed", "failure", attempt: Tuning.MaxReruns + 1), Workflow(), Now, Clear);
        Assert.Equal(ActionBlock.RerunLimit, fiftyOne.RerunFailedJobs);
        Assert.Equal(ActionBlock.RerunLimit, fiftyOne.RerunAll);
    }

    [Fact]
    public void 已有待確認的重跑時兩種重跑都停用而取消照run狀態()
    {
        var actions = ActionRules.Evaluate(Run("completed", "failure"), Workflow(), Now, Clear with { RerunPending = true });

        Assert.Equal(ActionBlock.RerunPending, actions.RerunFailedJobs);
        Assert.Equal(ActionBlock.RerunPending, actions.RerunAll);
        Assert.Equal(ActionBlock.Completed, actions.Cancel);
    }

    // ── 只限取消（§7.4、§6.4）──────────────────────────────────────────

    [Fact]
    public void 取消中時取消停用而強制取消要等5分鐘()
    {
        var run = Run("in_progress", null);

        var early = ActionRules.Evaluate(run, Workflow(), Now, Clear with { Cancelling = CancelAccepted });
        Assert.Equal(ActionBlock.CancelPending, early.Cancel);
        Assert.False(early.ShowForceCancel);
        Assert.Equal(ActionBlock.ForceNotYet, early.ForceCancel);

        var late = ActionRules.Evaluate(run, Workflow(), Now, Clear with { Cancelling = CancelAccepted, CancelGraceElapsed = true });
        Assert.Equal(ActionBlock.CancelPending, late.Cancel);
        Assert.True(late.ShowForceCancel);
        Assert.Equal(ActionBlock.None, late.ForceCancel);
    }

    [Fact]
    public void 強制取消送出後不再出現()
    {
        var forceSent = Clear with { Cancelling = CancelAccepted with { ForceSent = true }, CancelGraceElapsed = true };

        var actions = ActionRules.Evaluate(Run("in_progress", null), Workflow(), Now, forceSent);

        Assert.False(actions.ShowForceCancel);
        Assert.Equal(ActionBlock.CancelPending, actions.ForceCancel);
        Assert.Equal(ActionBlock.CancelPending, actions.Cancel);
    }

    [Fact]
    public void 取消超過5分鐘時離線強制取消仍出現但停用()
    {
        var offline = new ActionContext(ActionBlock.Offline, InFlight: false, RerunPending: false, CancelAccepted, CancelGraceElapsed: true);

        var actions = ActionRules.Evaluate(Run("in_progress", null), Workflow(), Now, offline);

        Assert.True(actions.ShowForceCancel);
        Assert.Equal(ActionBlock.Offline, actions.ForceCancel);
        Assert.Equal(ActionBlock.Offline, actions.Cancel);
        Assert.Equal(ActionBlock.Offline, actions.RerunAll);
    }

    [Fact]
    public void waiting可以取消()
    {
        var actions = ActionRules.Evaluate(Run("waiting", null), Workflow(), Now, Clear);

        Assert.Equal(ActionBlock.None, actions.Cancel);
        Assert.Equal(ActionBlock.NotCompleted, actions.RerunFailedJobs);
        Assert.Equal(ActionBlock.NotCompleted, actions.RerunAll);
    }

    [Fact]
    public void action_required只能開啟run()
    {
        var actions = ActionRules.Evaluate(Run("completed", "action_required"), Workflow(), Now, Clear);

        Assert.Equal(ActionBlock.AwaitingApproval, actions.RerunFailedJobs);
        Assert.Equal(ActionBlock.AwaitingApproval, actions.RerunAll);
        Assert.Equal(ActionBlock.Completed, actions.Cancel);
        Assert.False(actions.ShowForceCancel);
        Assert.Equal(ActionBlock.ForceNotYet, actions.ForceCancel);
    }

    private static void AssertAllBlocked(RunActions actions, ActionBlock block)
    {
        Assert.Equal(block, actions.RerunFailedJobs);
        Assert.Equal(block, actions.RerunAll);
        Assert.Equal(block, actions.Cancel);
        Assert.Equal(block, actions.ForceCancel);
        Assert.False(actions.ShowForceCancel);
    }

    /// <param name="created">created_at（重跑 30 天的起算點）；預設是一天前。</param>
    private static RunInfo Run(string? status, string? conclusion, DateTimeOffset? created = null, int attempt = 1)
    {
        var createdAt = created ?? Now.AddDays(-1);
        return new RunInfo(
            Id: RunId, RepoId: FakeData.RepoId, WorkflowId: FakeData.WorkflowId, WorkflowName: FakeData.WorkflowName,
            Path: FakeData.WorkflowPath, RunNumber: 42, RunAttempt: attempt, DisplayTitle: "Fix login button", Event: "push",
            Status: status, Conclusion: conclusion, HeadBranch: "main", HeadSha: FakeData.MainSha, HeadRepoId: FakeData.RepoId,
            HeadRepoFullName: FakeData.RepoName, HeadRepoHtmlUrl: "https://github.com/" + FakeData.RepoName, ActorId: FakeData.MyId,
            ActorLogin: FakeData.MyLogin, TriggeringActorLogin: FakeData.MyLogin, PullRequestNumbers: [], RunStartedAt: createdAt,
            CreatedAt: createdAt, UpdatedAt: createdAt, HtmlUrl: $"https://github.com/{FakeData.RepoName}/actions/runs/{RunId}");
    }

    private static WorkflowInfo Workflow(string state = "active") =>
        new(FakeData.RepoId, FakeData.WorkflowId, FakeData.WorkflowName, FakeData.WorkflowPath, state,
            $"https://github.com/{FakeData.RepoName}/blob/main/{FakeData.WorkflowPath}");
}

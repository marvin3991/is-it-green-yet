using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;

namespace IsItGreenYet.Core.Tests.Tracking;

public class RunStateMapperTests
{
    [Theory]
    [InlineData("queued", null, DisplayState.Queued, RunCategory.Pending, "排隊")]
    [InlineData("requested", null, DisplayState.Queued, RunCategory.Pending, "排隊")]
    [InlineData("pending", null, DisplayState.Queued, RunCategory.Pending, "排隊")]
    [InlineData("in_progress", null, DisplayState.InProgress, RunCategory.Pending, "進行中")]
    [InlineData("waiting", null, DisplayState.Waiting, RunCategory.Waiting, "等待核准")]
    [InlineData("completed", "action_required", DisplayState.Waiting, RunCategory.Waiting, "等待核准")]
    [InlineData("completed", "success", DisplayState.Success, RunCategory.Success, "成功")]
    [InlineData("completed", "failure", DisplayState.Failure, RunCategory.Failure, "失敗")]
    [InlineData("completed", "timed_out", DisplayState.Failure, RunCategory.Failure, "失敗（逾時）")]
    [InlineData("completed", "startup_failure", DisplayState.Failure, RunCategory.Failure, "失敗（啟動失敗）")]
    [InlineData("startup_failure", null, DisplayState.Failure, RunCategory.Failure, "失敗（啟動失敗）")]
    [InlineData("startup_failure", "startup_failure", DisplayState.Failure, RunCategory.Failure, "失敗（啟動失敗）")]
    [InlineData("completed", "cancelled", DisplayState.Cancelled, RunCategory.Gray, "取消")]
    [InlineData("completed", "skipped", DisplayState.Skipped, RunCategory.Gray, "略過")]
    [InlineData("completed", "neutral", DisplayState.Skipped, RunCategory.Gray, "略過（neutral）")]
    [InlineData("completed", "stale", DisplayState.Skipped, RunCategory.Gray, "略過（stale）")]
    [InlineData("completed", null, DisplayState.Unknown, RunCategory.Gray, "未知")]
    [InlineData("completed", "brand_new", DisplayState.Unknown, RunCategory.Gray, "未知（brand_new）")]
    [InlineData("brand_new_status", null, DisplayState.Unknown, RunCategory.Pending, "未知（brand_new_status）")]
    [InlineData(null, null, DisplayState.Unknown, RunCategory.Pending, "未知")]
    public void 每一種status與conclusion都對應到spec的顯示狀態(
        string? status, string? conclusion, DisplayState display, RunCategory category, string label)
    {
        var state = RunStateMapper.Map(status, conclusion);

        Assert.Equal(display, state.Display);
        Assert.Equal(category, state.Category);
        Assert.Equal(label, state.Label);
    }

    [Theory]
    [InlineData("COMPLETED", "success")]
    [InlineData("Completed", "success")]
    public void 大小寫不同的值不認得且完成前當未完成(string status, string conclusion)
    {
        var state = RunStateMapper.Map(status, conclusion);

        Assert.Equal(DisplayState.Unknown, state.Display);
        Assert.Equal(RunCategory.Pending, state.Category);
    }

    [Theory]
    [InlineData("", null, RunCategory.Pending)]
    [InlineData("  ", null, RunCategory.Pending)]
    [InlineData("completed", "", RunCategory.Gray)]
    public void 空字串當成null(string? status, string? conclusion, RunCategory category)
    {
        var state = RunStateMapper.Map(status, conclusion);

        Assert.Equal(DisplayState.Unknown, state.Display);
        Assert.Equal(category, state.Category);
        Assert.Equal("未知", state.Label);
        Assert.Null(state.RawValue);
    }

    [Fact]
    public void 完成的run以conclusion為原始值未完成的以status為原始值()
    {
        Assert.Equal("timed_out", RunStateMapper.Map("completed", "timed_out").RawValue);
        Assert.Equal("in_progress", RunStateMapper.Map("in_progress", null).RawValue);
        Assert.Equal("brand_new", RunStateMapper.Map("completed", "brand_new").RawValue);
        Assert.Equal("brand_new_status", RunStateMapper.Map("brand_new_status", null).RawValue);
    }

    [Fact]
    public void 未完成狀態不看conclusion()
    {
        var state = RunStateMapper.Map("in_progress", "failure");

        Assert.Equal(DisplayState.InProgress, state.Display);
        Assert.Equal(RunCategory.Pending, state.Category);
    }

    [Fact]
    public void 只有成功失敗與灰色算完成()
    {
        Assert.True(RunStateMapper.Map("completed", "success").IsCompleted);
        Assert.True(RunStateMapper.Map("completed", "failure").IsCompleted);
        Assert.True(RunStateMapper.Map("completed", "cancelled").IsCompleted);
        Assert.True(RunStateMapper.Map("completed", "brand_new").IsCompleted);
        Assert.False(RunStateMapper.Map("completed", "action_required").IsCompleted);
        Assert.False(RunStateMapper.Map("waiting", null).IsCompleted);
        Assert.False(RunStateMapper.Map("in_progress", null).IsCompleted);
        Assert.False(RunStateMapper.Map(null, null).IsCompleted);
    }

    [Fact]
    public void RunInfo與attempt紀錄用同一套對應()
    {
        var now = new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);
        var run = new RunInfo(
            Id: 1, RepoId: 2, WorkflowId: 3, WorkflowName: "CI", Path: ".github/workflows/ci.yml",
            RunNumber: 7, RunAttempt: 2, DisplayTitle: "Fix", Event: "push", Status: "completed", Conclusion: "timed_out",
            HeadBranch: "main", HeadSha: "abc", HeadRepoId: 2, HeadRepoFullName: "octo-org/sample-app",
            HeadRepoHtmlUrl: "https://github.com/octo-org/sample-app", ActorId: 9, ActorLogin: "alice",
            TriggeringActorLogin: "alice", PullRequestNumbers: [], RunStartedAt: now, CreatedAt: now, UpdatedAt: now,
            HtmlUrl: "https://github.com/octo-org/sample-app/actions/runs/1");
        var attempt = new RunAttemptRecord(1, 1, "completed", "cancelled", now, now);

        Assert.Equal("失敗（逾時）", RunStateMapper.Map(run).Label);
        Assert.Equal(DisplayState.Cancelled, RunStateMapper.Map(attempt).Display);
    }
}

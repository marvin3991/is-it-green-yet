using System.Text.Json.Nodes;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Engine;

/// <summary>每一列的可操作性與寫入權限（第二階段 spec §4、§7）：快照的 Actions 與 WritesEnabled。</summary>
public class MonitorEngineActionTests
{
    private const long FailedRunId = 9001;

    /// <summary>classic token 的 scope，不含 repo。</summary>
    private const string ScopesWithoutRepo = "read:org, workflow";

    private const long BobId = 1002;
    private const string BobLogin = "bob";

    private const long OtherRepoId = 2002;
    private const string OtherRepoName = "octo-org/sample-api";

    private static readonly DateTimeOffset Start = EngineHarness.Start;

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>一天前失敗、可以重跑的 run，在預設 repo 的 E4 第 1 頁。</summary>
    private static JsonObject FailedRun() =>
        FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5));

    /// <summary>寫入功能打開、預設 repo 有一筆失敗的 run；<paramref name="arrange"/> 在建立 Engine 之前呼叫。</summary>
    private static EngineHarness Create(Action<EngineHarness>? arrange = null) =>
        EngineHarness.Create(h =>
        {
            h.Settings.AllowWriteActions = true;
            h.Main.Runs.Add(FailedRun());
            arrange?.Invoke(h);
        });

    /// <summary>沒有任何條件擋住時，一天前失敗的 run 的可操作性。</summary>
    private static RunActions Allowed(int attempt = 1) =>
        new(FakeData.RepoId, attempt, ActionBlock.None, ActionBlock.None, ActionBlock.Completed, ShowForceCancel: false, ActionBlock.ForceNotYet);

    [Fact]
    public async Task 寫入功能關閉時快照的WritesEnabled是false()
    {
        await using var h = EngineHarness.Create(h => h.Main.Runs.Add(FailedRun()));
        await h.DrainAsync();

        Assert.All(h.Snapshots, snapshot =>
        {
            Assert.False(snapshot.WritesEnabled);
            Assert.NotNull(snapshot.Actions);
        });

        // 啟動時讀 DB 發布的第一份快照還沒有任何一列：可操作性是空集合，不是 null。
        Assert.Empty(h.Snapshots[0].Rows);
        Assert.Empty(h.Snapshots[0].Actions);

        h.Settings.AllowWriteActions = true;
        h.Engine.Post(new EngineCommand.SettingsChanged(h.Settings));
        await h.StepAsync();

        Assert.True(h.Last.WritesEnabled);
    }

    [Fact]
    public async Task 快照帶出每一列的可操作性()
    {
        const long runningId = 9002;
        const long nightlyRunId = 9003;
        const long nightlyWorkflowId = 161336;
        await using var h = Create(h =>
        {
            h.Main.Runs.Clear();
            h.Main.Workflows.Add(FakeData.Workflow(nightlyWorkflowId, "Nightly", ".github/workflows/nightly.yml", state: "disabled_manually"));
            h.Main.Runs.Add(FakeData.Run(runningId, 43, "in_progress", null, Start.AddMinutes(-5)));
            h.Main.Runs.Add(FakeData.Run(FailedRunId, 42, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5),
                attempt: 2));
            h.Main.Runs.Add(FakeData.Run(nightlyRunId, 7, "completed", "success", Start.AddDays(-2), Start.AddDays(-2).AddMinutes(5),
                workflowId: nightlyWorkflowId, name: "Nightly"));
        });
        await h.DrainAsync();

        var snapshot = h.Last;
        Assert.True(snapshot.WritesEnabled);
        Assert.Equal(snapshot.Rows.Select(row => row.Run.Id).Order(), snapshot.Actions.Keys.Order());
        Assert.Equal(Allowed(attempt: 2), snapshot.Actions[FailedRunId]);
        Assert.Equal(
            new RunActions(FakeData.RepoId, 1, ActionBlock.NotCompleted, ActionBlock.NotCompleted, ActionBlock.None, false, ActionBlock.ForceNotYet),
            snapshot.Actions[runningId]);

        // workflow 的狀態取自本地的 workflow 清單（E3）。
        Assert.Equal(
            new RunActions(FakeData.RepoId, 1, ActionBlock.NoFailedJobs, ActionBlock.WorkflowDisabled, ActionBlock.Completed, false,
                ActionBlock.ForceNotYet),
            snapshot.Actions[nightlyRunId]);
    }

    // ── scope（第二階段 spec §4.1）────────────────────────────────────

    [Fact]
    public async Task scope不含repo時停用寫入()
    {
        await using var h = Create(h => h.GitHub.SetOAuthScopes(Fixture.FakeToken, ScopesWithoutRepo));
        await h.DrainAsync();

        AssertAllBlocked(h.Last.Actions[FailedRunId], ActionBlock.MissingPermission);
    }

    [Theory]
    [InlineData("read:org, repo, workflow", ActionBlock.None)]
    [InlineData(" REPO ,workflow", ActionBlock.None)]
    [InlineData("", ActionBlock.None)]
    [InlineData(",", ActionBlock.None)]
    [InlineData("public_repo", ActionBlock.MissingPermission)]
    [InlineData("repo:status, read:org", ActionBlock.MissingPermission)]
    public async Task scope以逗號分隔去掉空白不分大小寫找repo(string scopes, ActionBlock expected)
    {
        await using var h = Create(h => h.GitHub.SetOAuthScopes(Fixture.FakeToken, scopes));
        await h.DrainAsync();

        // 只有 public_repo 也當成不夠（spec §4.1 推測）；值是空的（fine-grained token）是不確定，不停用。
        Assert.Equal(expected, h.Last.Actions[FailedRunId].RerunAll);
    }

    [Fact]
    public async Task 換成沒有scope_header的token後不再停用寫入()
    {
        await using var h = Create(h => h.GitHub.SetOAuthScopes(Fixture.FakeToken, ScopesWithoutRepo));
        await h.DrainAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);

        // 換到另一把 token，它的 E1 遇到連線失敗（沒有回應）：新 token 的回應出現之前是不確定，舊 token 記下的 scope 不算。
        h.GitHub.NetworkDown = true;
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        Assert.Equal(1, await h.StepAsync());
        Assert.False(h.Last.Offline);
        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);

        // 新 token 的回應沒有 X-OAuth-Scopes（例如 fine-grained token）：仍是不確定。
        h.GitHub.NetworkDown = false;
        await h.AdvanceAsync(Tuning.OfflineProbeMax);
        Assert.Contains(h.GitHub.Handler.Requests, request => request.Header("Authorization") == "Bearer " + EngineHarness.OtherFakeToken);
        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);
    }

    // ── push（第二階段 spec §4.1）─────────────────────────────────────

    [Fact]
    public async Task push為false的repo停用寫入()
    {
        await using var h = Create(h =>
        {
            h.Main.Repo["permissions"] = Permissions(push: false);
            var other = h.GitHub.AddRepo(FakeData.Repo(OtherRepoId, OtherRepoName, Start.AddDays(-1), push: true));
            other.Runs.Add(FakeData.Run(9101, 5, "completed", "failure", Start.AddDays(-1), Start.AddDays(-1).AddMinutes(5),
                repo: OtherRepoName, headRepoId: OtherRepoId));
        });
        await h.DrainAsync();

        AssertAllBlocked(h.Last.Actions[FailedRunId], ActionBlock.MissingPermission);

        // 只停用那個 repo：push 是 true 的 repo 照常可以寫。
        Assert.Equal(ActionBlock.None, h.Last.Actions[9101].RerunAll);
    }

    [Fact]
    public async Task 重新啟動後從資料庫讀回push權限()
    {
        await using var h = Create(h => h.Main.Repo["permissions"] = Permissions(push: false));
        await h.DrainAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);

        // 重新啟動後清單回應不再帶 permissions 物件（不確定）：不蓋掉 DB 讀回來的 false。
        h.Main.Repo.Remove("permissions");
        await h.RestartAsync();
        var snapshots = h.Snapshots.Count;
        var requests = h.GitHub.ApiRequests.Count;
        await h.DrainAsync();

        // 啟動時只讀 DB 發布的第一份快照就已經停用；之後抓到的清單沒有 permissions，仍是 DB 的值。
        Assert.Equal(ActionBlock.MissingPermission, h.Snapshots[snapshots].Actions[FailedRunId].RerunAll);
        Assert.Contains(EngineHarness.ListAPath, h.GitHub.ApiRequests.Skip(requests));
        AssertAllBlocked(h.Last.Actions[FailedRunId], ActionBlock.MissingPermission);
    }

    [Fact]
    public async Task 換帳號後舊帳號的push為false不再停用()
    {
        await using var h = Create(h => h.Main.Repo["permissions"] = Permissions(push: false));
        await h.DrainAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);

        // 換成 bob 的 token；bob 看到的清單沒有 permissions 物件，DB 裡也沒有 bob 的資料列：不確定。
        h.Main.Repo.Remove("permissions");
        h.GitHub.User = FakeData.User(BobId, BobLogin);
        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());

        // 驗證 bob 的這一步還沒抓清單：換帳號的當下就不再用 alice 的值。
        Assert.Equal(1, await h.StepAsync());
        Assert.Equal("/user", h.GitHub.ApiRequests[^1]);
        Assert.Equal(BobId, h.Cache.GetAccount()?.UserId);
        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);

        await h.DrainAsync();
        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);

        // 舊帳號的資料列留在 DB（鍵是 (repo_id, user_id)），只是不再適用。
        Assert.False(h.Cache.GetRepoPermissions(FakeData.MyId)[FakeData.RepoId]);
    }

    [Fact]
    public async Task 移除的repo重新出現時不沿用舊的push()
    {
        await using var h = Create(h => h.Main.Repo["permissions"] = Permissions(push: false));
        await h.DrainAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);

        // 連續兩輪不見：repo 連同 DB 的權限資料列一起刪掉（第二階段實作計畫「和 spec 的差異與補充」第 3 列），記憶體也要一致。
        h.Main.Listed = false;
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        await h.AdvanceAsync(Tuning.RepoListRefresh);
        Assert.DoesNotContain(h.Cache.GetRepos(), repo => repo.Id == FakeData.RepoId);
        Assert.False(h.Cache.GetRepoPermissions(FakeData.MyId).ContainsKey(FakeData.RepoId));

        // 重新出現時清單沒有 permissions 物件：不確定，不沿用刪掉之前的 false。
        h.Main.Repo.Remove("permissions");
        h.Main.Listed = true;
        await h.AdvanceAsync(Tuning.RepoListRefresh);

        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);
    }

    // ── 其他共通條件（第二階段 spec §5.4、§7.2）──────────────────────

    [Fact]
    public async Task 離線時所有寫入停用()
    {
        await using var h = Create();
        await h.DrainAsync();

        h.GitHub.NetworkDown = true;
        h.Engine.Post(new EngineCommand.RefreshAll());
        await h.DrainAsync();

        Assert.True(h.Last.Offline);
        AssertAllBlocked(h.Last.Actions[FailedRunId], ActionBlock.Offline);
    }

    [Fact]
    public async Task 自動更新暫停時寫入不停用()
    {
        await using var h = Create();
        await h.DrainAsync();

        h.Engine.Post(new EngineCommand.SetRefreshMode(RefreshMode.Paused));
        await h.DrainAsync();

        Assert.Equal(RefreshMode.Paused, h.Last.RefreshMode);
        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);
    }

    // ── 被拒之後的停用（第二階段 spec §4.3）────────────────────────────

    [Fact]
    public async Task 檢查連線清除被拒的停用()
    {
        await using var h = Create();
        await h.DrainAsync();

        h.Engine.MarkWriteDenied(FakeData.RepoId, Fixture.FakeToken);
        await h.StepAsync();
        AssertAllBlocked(h.Last.Actions[FailedRunId], ActionBlock.MissingPermission);

        var check = await h.Engine.CheckConnectionAsync(None);
        Assert.True(check.Ok);
        await h.StepAsync();

        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);
    }

    [Fact]
    public async Task 換token後被拒的停用不再算()
    {
        await using var h = Create();
        await h.DrainAsync();

        h.Engine.MarkWriteDenied(FakeData.RepoId, Fixture.FakeToken);
        await h.StepAsync();
        Assert.Equal(ActionBlock.MissingPermission, h.Last.Actions[FailedRunId].RerunAll);

        h.Tokens.Returns(EngineHarness.OtherFakeToken);
        h.Engine.Post(new EngineCommand.RecheckAuth());
        await h.DrainAsync();

        Assert.Equal(Allowed(), h.Last.Actions[FailedRunId]);
    }

    [Fact]
    public void TokenIdentity不含token原文()
    {
        var identity = TokenIdentity.Of(Fixture.FakeToken);

        Assert.NotNull(identity);
        Assert.DoesNotContain(Fixture.FakeToken, identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Fixture.FakeToken["ghp_".Length..], identity, StringComparison.OrdinalIgnoreCase);

        // 同一把 token 每次都是同一個識別、不同的 token 不同；SHA-256 的十六進位是 64 個字元。
        Assert.Equal(identity, TokenIdentity.Of(Fixture.FakeToken));
        Assert.NotEqual(identity, TokenIdentity.Of(EngineHarness.OtherFakeToken));
        Assert.Equal(64, identity.Length);
        Assert.True(identity.All(Uri.IsHexDigit));

        Assert.Null(TokenIdentity.Of(null));
        Assert.Null(TokenIdentity.Of(string.Empty));
    }

    private static JsonObject Permissions(bool push) => new() { ["admin"] = false, ["push"] = push, ["pull"] = true };

    private static void AssertAllBlocked(RunActions actions, ActionBlock block)
    {
        Assert.Equal(block, actions.RerunFailedJobs);
        Assert.Equal(block, actions.RerunAll);
        Assert.Equal(block, actions.Cancel);
        Assert.Equal(block, actions.ForceCancel);
        Assert.False(actions.ShowForceCancel);
    }
}

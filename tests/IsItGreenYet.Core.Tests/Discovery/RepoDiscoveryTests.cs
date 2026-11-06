using IsItGreenYet.Core.Discovery;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Tests.Discovery;

public class RepoDiscoveryTests
{
    private const long MyId = 1001;
    private const long OrgId = 2002;
    private const long PartnerId = 3003;

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);
    private static readonly DiscoveryOptions Defaults = new(IncludeForks: false, IncludeCollaborators: false);
    private static readonly WorkflowCounts HasWorkflows = new(Total: 2, Active: 2);
    private static readonly WorkflowCounts NoWorkflows = new(Total: 0, Active: 0);

    // ── Merge（§5.1）────────────────────────────────────────────────

    [Fact]
    public void 自己的與組織的與協作的分類()
    {
        var own = Repo(1, "sample-user/sample-app", ownerId: MyId);
        var org = Repo(2, "octo-org/sample-app", ownerId: OrgId);
        var collaborator = Repo(3, "octo-partner/sample-app", ownerId: PartnerId);
        var collaboratorFork = Repo(4, "octo-partner/sample-app-fork", ownerId: PartnerId, fork: true);

        var merged = RepoDiscovery.Merge(MyId, [own, org], [collaborator, collaboratorFork]);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, merged.Select(r => r.Id));
        Assert.Equal(RepoCategory.Own, merged[0].Category);
        Assert.Equal(RepoCategory.Organization, merged[1].Category);
        Assert.Equal(RepoCategory.Collaborator, merged[2].Category);
        // fork 不是另一種類別：保留原本的類別，由 IsFork 交給第 4 條判斷。
        Assert.Equal(RepoCategory.Collaborator, merged[3].Category);
        Assert.True(merged[3].IsFork);
    }

    [Fact]
    public void 同時在A與B以A為準()
    {
        var inA = Repo(10, "sample-user/sample-app", ownerId: MyId);
        var orgInA = Repo(11, "octo-org/sample-app", ownerId: OrgId);
        // 清單 B 裡同一個 id 的資料（例如抓取時間不同而名稱不同）不採用。
        var sameInB = Repo(10, "sample-user/sample-app-old", ownerId: MyId, category: RepoCategory.Collaborator);
        var orgInB = Repo(11, "octo-org/sample-app", ownerId: OrgId, category: RepoCategory.Collaborator);

        var merged = RepoDiscovery.Merge(MyId, [inA, orgInA], [sameInB, orgInB]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(RepoCategory.Own, merged[0].Category);
        Assert.Equal("sample-user/sample-app", merged[0].FullName);
        Assert.Equal(RepoCategory.Organization, merged[1].Category);
    }

    [Fact]
    public void 清單B為null時只用清單A()
    {
        var merged = RepoDiscovery.Merge(MyId, [Repo(1, ownerId: MyId), Repo(2, ownerId: OrgId)], null);

        Assert.Equal(new[] { RepoCategory.Own, RepoCategory.Organization }, merged.Select(r => r.Category));
    }

    [Fact]
    public void 分頁間重複出現的id只留第一筆()
    {
        // 分頁途中 repo 排序變動時，同一個 repo 可能出現在兩頁。
        var first = Repo(5, "octo-org/sample-app", ownerId: OrgId);
        var duplicate = Repo(5, "octo-org/sample-app", ownerId: OrgId);

        var merged = RepoDiscovery.Merge(MyId, [first, duplicate], [Repo(6, ownerId: PartnerId), Repo(6, ownerId: PartnerId)]);

        Assert.Equal(new long[] { 5, 6 }, merged.Select(r => r.Id));
    }

    [Fact]
    public void 兩個清單都是空的回空清單()
    {
        Assert.Empty(RepoDiscovery.Merge(MyId, [], []));
        Assert.Empty(RepoDiscovery.Merge(MyId, [], null));
    }

    // ── Classify（§5.2，依序判斷，命中第一個就停）──────────────────────

    [Fact]
    public void 第1條手動靜音優先於所有規則()
    {
        var repo = Repo(1, archived: true, disabled: true, fork: true, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, new RepoOverride(Muted: true, ForceIncluded: true), Defaults, null, NoWorkflows, Now);

        AssertSkipped(result, SkipReason.Muted, canForceInclude: false);
    }

    [Fact]
    public void 第2條封存優先於停用與之後的規則()
    {
        var repo = Repo(1, archived: true, disabled: true, fork: true, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, NoWorkflows, Now);

        AssertSkipped(result, SkipReason.Archived, canForceInclude: false);
    }

    [Fact]
    public void 第3條停用優先於fork與之後的規則()
    {
        var repo = Repo(1, disabled: true, fork: true, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, NoWorkflows, Now);

        AssertSkipped(result, SkipReason.Disabled, canForceInclude: false);
    }

    [Fact]
    public void 第4條fork在設定未開啟時略過且可強制納入()
    {
        var repo = Repo(1, fork: true, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, NoWorkflows, Now);
        AssertSkipped(result, SkipReason.Fork, canForceInclude: true);

        // 設定開啟後不再命中第 4 條，往下判斷到第 5 條。
        var included = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults with { IncludeForks = true }, null, NoWorkflows, Now);
        AssertSkipped(included, SkipReason.Collaborator, canForceInclude: true);
    }

    [Fact]
    public void 第5條協作repo在設定未開啟時略過且可強制納入()
    {
        var repo = Repo(1, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, NoWorkflows, Now);
        AssertSkipped(result, SkipReason.Collaborator, canForceInclude: true);

        var included = RepoDiscovery.Classify(
            repo, RepoOverride.None, Defaults with { IncludeCollaborators = true }, null, NoWorkflows, Now);
        AssertSkipped(included, SkipReason.Stale, canForceInclude: true);
    }

    [Fact]
    public void 第6條超過90天沒有活動略過且可強制納入()
    {
        var repo = Repo(1, pushedDaysAgo: 91);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, Now.AddDays(-120), NoWorkflows, Now);

        AssertSkipped(result, SkipReason.Stale, canForceInclude: true);
    }

    [Fact]
    public void 第7條沒有workflow略過且不可強制納入()
    {
        var repo = Repo(1);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, NoWorkflows, Now);

        // 總數為 0 時先命中第 7 條，不會落到第 8 條。
        AssertSkipped(result, SkipReason.NoWorkflows, canForceInclude: false);
    }

    [Fact]
    public void 第8條workflow全部停用略過且不可強制納入()
    {
        var repo = Repo(1);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, new WorkflowCounts(Total: 3, Active: 0), Now);

        AssertSkipped(result, SkipReason.AllWorkflowsDisabled, canForceInclude: false);
    }

    [Fact]
    public void 都沒命中就監看()
    {
        var repo = Repo(1, pushedDaysAgo: 3);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, new WorkflowCounts(Total: 3, Active: 1), Now);

        Assert.Equal(new RepoClassification(1, Monitored: true, SkipReason.None, CanForceInclude: false), result);
    }

    [Fact]
    public void stale但90天內有run就監看()
    {
        var repo = Repo(1, pushedDaysAgo: 200);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, Now.AddDays(-30), HasWorkflows, Now);

        Assert.True(result.Monitored);
        Assert.Equal(SkipReason.None, result.Reason);
    }

    [Fact]
    public void pushed_at為null且沒有本地run算stale()
    {
        var repo = Repo(1, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, Defaults, null, HasWorkflows, Now);

        AssertSkipped(result, SkipReason.Stale, canForceInclude: true);
    }

    [Fact]
    public void 剛好90天不算超過()
    {
        var exactly = Repo(1) with { PushedAt = Now.AddDays(-Tuning.StaleRepoDays) };
        var justOver = Repo(1) with { PushedAt = Now.AddDays(-Tuning.StaleRepoDays).AddSeconds(-1) };

        Assert.True(RepoDiscovery.Classify(exactly, RepoOverride.None, Defaults, null, HasWorkflows, Now).Monitored);
        Assert.Equal(
            SkipReason.Stale,
            RepoDiscovery.Classify(justOver, RepoOverride.None, Defaults, null, HasWorkflows, Now).Reason);

        // 本地 run 剛好 90 天前也算「90 天內有紀錄」。
        Assert.True(
            RepoDiscovery.Classify(justOver, RepoOverride.None, Defaults, Now.AddDays(-Tuning.StaleRepoDays), HasWorkflows, Now).Monitored);
    }

    [Fact]
    public void 未來的時間戳不算stale()
    {
        // GitHub 時間比本機快（時鐘誤差）時，pushed_at 或 run 時間可能在未來。
        var futurePush = Repo(1) with { PushedAt = Now.AddMinutes(5) };
        var neverPushed = Repo(2, pushedDaysAgo: null);

        Assert.True(RepoDiscovery.Classify(futurePush, RepoOverride.None, Defaults, null, HasWorkflows, Now).Monitored);
        Assert.True(RepoDiscovery.Classify(neverPushed, RepoOverride.None, Defaults, Now.AddMinutes(5), HasWorkflows, Now).Monitored);
    }

    [Fact]
    public void 強制納入救回stale()
    {
        var repo = Repo(1, pushedDaysAgo: 365);

        var result = RepoDiscovery.Classify(repo, new RepoOverride(Muted: false, ForceIncluded: true), Defaults, null, HasWorkflows, Now);

        // 監看中；CanForceInclude 仍為 true，管理頁才能取消強制納入。
        Assert.Equal(new RepoClassification(1, Monitored: true, SkipReason.None, CanForceInclude: true), result);
    }

    [Fact]
    public void 強制納入同時救回fork與協作與stale()
    {
        var repo = Repo(1, fork: true, category: RepoCategory.Collaborator, pushedDaysAgo: null);

        var result = RepoDiscovery.Classify(repo, new RepoOverride(Muted: false, ForceIncluded: true), Defaults, null, HasWorkflows, Now);

        Assert.Equal(new RepoClassification(1, Monitored: true, SkipReason.None, CanForceInclude: true), result);
    }

    [Fact]
    public void 封存不可強制納入()
    {
        var forced = new RepoOverride(Muted: false, ForceIncluded: true);

        var archived = RepoDiscovery.Classify(Repo(1, archived: true), forced, Defaults, null, HasWorkflows, Now);
        var disabled = RepoDiscovery.Classify(Repo(2, disabled: true), forced, Defaults, null, HasWorkflows, Now);

        AssertSkipped(archived, SkipReason.Archived, canForceInclude: false);
        AssertSkipped(disabled, SkipReason.Disabled, canForceInclude: false);
    }

    [Fact]
    public void 強制納入救不回沒有workflow()
    {
        var forced = new RepoOverride(Muted: false, ForceIncluded: true);
        var forkWithoutWorkflows = Repo(1, fork: true);

        var noWorkflows = RepoDiscovery.Classify(forkWithoutWorkflows, forced, Defaults, null, NoWorkflows, Now);
        var allDisabled = RepoDiscovery.Classify(forkWithoutWorkflows, forced, Defaults, null, new WorkflowCounts(2, 0), Now);

        AssertSkipped(noWorkflows, SkipReason.NoWorkflows, canForceInclude: false);
        AssertSkipped(allDisabled, SkipReason.AllWorkflowsDisabled, canForceInclude: false);
    }

    [Fact]
    public void 還沒查過workflow時第7與第8條不判定()
    {
        var result = RepoDiscovery.Classify(Repo(1), RepoOverride.None, Defaults, null, workflows: null, Now);

        Assert.True(result.Monitored);
        Assert.Equal(SkipReason.None, result.Reason);
    }

    [Fact]
    public void 設定開啟後fork與協作直接監看()
    {
        var options = new DiscoveryOptions(IncludeForks: true, IncludeCollaborators: true);
        var repo = Repo(1, fork: true, category: RepoCategory.Collaborator);

        var result = RepoDiscovery.Classify(repo, RepoOverride.None, options, null, HasWorkflows, Now);

        Assert.Equal(new RepoClassification(1, Monitored: true, SkipReason.None, CanForceInclude: false), result);
    }

    [Fact]
    public void 分類的參數為null時丟例外()
    {
        Assert.Throws<ArgumentNullException>(() => RepoDiscovery.Classify(null!, RepoOverride.None, Defaults, null, null, Now));
        Assert.Throws<ArgumentNullException>(() => RepoDiscovery.Classify(Repo(1), null!, Defaults, null, null, Now));
        Assert.Throws<ArgumentNullException>(() => RepoDiscovery.Classify(Repo(1), RepoOverride.None, null!, null, null, Now));
        Assert.Throws<ArgumentNullException>(() => RepoDiscovery.Merge(MyId, null!, null));
    }

    // ── NeedsCollaboratorList（§5.1）────────────────────────────────

    [Fact]
    public void 有強制納入的協作repo時要抓清單B()
    {
        var forced = new RepoOverride(Muted: false, ForceIncluded: true);
        var ownForced = (Repo(1, ownerId: MyId), forced);
        var collaboratorNotForced = (Repo(2, ownerId: PartnerId, category: RepoCategory.Collaborator), RepoOverride.None);
        var collaboratorForced = (Repo(3, ownerId: PartnerId, category: RepoCategory.Collaborator), forced);

        Assert.False(RepoDiscovery.NeedsCollaboratorList(Defaults, [ownForced, collaboratorNotForced]));
        Assert.True(RepoDiscovery.NeedsCollaboratorList(Defaults, [ownForced, collaboratorNotForced, collaboratorForced]));
    }

    [Fact]
    public void 設定開啟時要抓清單B()
    {
        Assert.True(RepoDiscovery.NeedsCollaboratorList(Defaults with { IncludeCollaborators = true }, []));
        // 「包含 fork」與清單 B 無關。
        Assert.False(RepoDiscovery.NeedsCollaboratorList(Defaults with { IncludeForks = true }, []));
    }

    // ── EvaluateListing（§5.4）──────────────────────────────────────

    [Fact]
    public void 分頁未完成不算消失()
    {
        // 150 個 repo 分兩頁（每頁 100）。第 2 頁失敗，或第 2 頁回 304 卻沒有 Link 而無法確定抓完，
        // 這一輪都不完整：不得累計消失、不得移除，即使某些 repo 上一輪已經不見過一次。
        var known = Enumerable.Range(1, 150).Select(i => Repo(i, $"octo-org/sample-app-{i}")).ToList();
        var firstPage = known.Take(Tuning.ListPageSize).ToList();
        var previousMissing = new Dictionary<long, int> { [120] = 1, [5] = 1 };

        var result = RepoDiscovery.EvaluateListing(known, firstPage, roundComplete: false, previousMissing);

        Assert.Empty(result.RemoveIds);
        Assert.Empty(result.Renames);
        // repo 120 不在已抓到的頁裡：維持 1，不累計。repo 5 在第 1 頁出現，確定還在，歸零。
        AssertMissing(result.MissingRounds, (120, 1));

        // 下一輪完整（第 2 頁照存下的 next 抓到，304 的頁用快取內容）時，全部都在，沒有人被移除。
        var complete = RepoDiscovery.EvaluateListing(known, known, roundComplete: true, result.MissingRounds);
        Assert.Empty(complete.RemoveIds);
        Assert.Empty(complete.MissingRounds);
    }

    [Fact]
    public void 連續兩輪不見才移除()
    {
        var known = new[] { Repo(1, "octo-org/sample-app"), Repo(2, "octo-org/sample-app-2"), Repo(3, "octo-org/sample-app-3") };
        var listed = new[] { known[0], known[1] };

        var round1 = RepoDiscovery.EvaluateListing(known, listed, roundComplete: true, new Dictionary<long, int>());
        Assert.Empty(round1.RemoveIds);
        AssertMissing(round1.MissingRounds, (3, 1));

        var round2 = RepoDiscovery.EvaluateListing(known, listed, roundComplete: true, round1.MissingRounds);
        Assert.Equal(new long[] { 3 }, round2.RemoveIds);
        AssertMissing(round2.MissingRounds, (3, Tuning.RemovalConfirmRounds));
    }

    [Fact]
    public void 只有一輪不見之後又出現就重新計算()
    {
        var known = new[] { Repo(1, "octo-org/sample-app"), Repo(2, "octo-org/sample-app-2") };

        var round1 = RepoDiscovery.EvaluateListing(known, [known[0]], roundComplete: true, new Dictionary<long, int>());
        var round2 = RepoDiscovery.EvaluateListing(known, known, roundComplete: true, round1.MissingRounds);
        var round3 = RepoDiscovery.EvaluateListing(known, [known[0]], roundComplete: true, round2.MissingRounds);

        Assert.Empty(round2.MissingRounds);
        Assert.Empty(round3.RemoveIds);
        AssertMissing(round3.MissingRounds, (2, 1));
    }

    [Fact]
    public void 不完整的一輪不打斷也不累計連續不見()
    {
        var known = new[] { Repo(1, "octo-org/sample-app"), Repo(2, "octo-org/sample-app-2") };

        var round1 = RepoDiscovery.EvaluateListing(known, [known[0]], roundComplete: true, new Dictionary<long, int>());
        var partial = RepoDiscovery.EvaluateListing(known, [], roundComplete: false, round1.MissingRounds);
        var round2 = RepoDiscovery.EvaluateListing(known, [known[0]], roundComplete: true, partial.MissingRounds);

        AssertMissing(partial.MissingRounds, (2, 1));
        Assert.Empty(partial.RemoveIds);
        Assert.Equal(new long[] { 2 }, round2.RemoveIds);
    }

    [Fact]
    public void 同id改名回報()
    {
        var known = new[] { Repo(7, "octo-org/sample-app"), Repo(8, "octo-org/sample-app-2") };
        var listed = new[] { Repo(7, "octo-org/sample-app-renamed"), Repo(8, "octo-org/Sample-App-2") };

        var result = RepoDiscovery.EvaluateListing(known, listed, roundComplete: true, new Dictionary<long, int>());

        // 只改大小寫也算改名（網址與顯示都要跟著換）。
        Assert.Equal(
            new[] { (7L, "octo-org/sample-app", "octo-org/sample-app-renamed"), (8L, "octo-org/sample-app-2", "octo-org/Sample-App-2") },
            result.Renames);
        Assert.Empty(result.MissingRounds);
        Assert.Empty(result.RemoveIds);
    }

    [Fact]
    public void 不完整的一輪也回報看到的改名()
    {
        var known = new[] { Repo(7, "octo-org/sample-app"), Repo(8, "octo-org/sample-app-2") };

        var result = RepoDiscovery.EvaluateListing(
            known, [Repo(7, "octo-org/sample-app-renamed")], roundComplete: false, new Dictionary<long, int>());

        Assert.Equal(new[] { (7L, "octo-org/sample-app", "octo-org/sample-app-renamed") }, result.Renames);
        Assert.Empty(result.MissingRounds);
    }

    [Fact]
    public void 已經不認得的repo的舊紀錄會清掉()
    {
        var known = new[] { Repo(1, "octo-org/sample-app") };
        var previousMissing = new Dictionary<long, int> { [999] = 1 };

        var complete = RepoDiscovery.EvaluateListing(known, known, roundComplete: true, previousMissing);
        var partial = RepoDiscovery.EvaluateListing(known, [], roundComplete: false, previousMissing);

        Assert.Empty(complete.MissingRounds);
        Assert.Empty(partial.MissingRounds);
        Assert.Empty(complete.RemoveIds);
    }

    [Fact]
    public void 新出現的repo不在這裡處理()
    {
        // 清單裡有、本地還沒有的 repo 由 Merge 與 Classify 處理，這裡只看已知的 repo。
        var known = new[] { Repo(1, "octo-org/sample-app") };
        var listed = new[] { Repo(1, "octo-org/sample-app"), Repo(2, "octo-org/sample-app-2") };

        var result = RepoDiscovery.EvaluateListing(known, listed, roundComplete: true, new Dictionary<long, int>());

        Assert.Empty(result.MissingRounds);
        Assert.Empty(result.RemoveIds);
        Assert.Empty(result.Renames);
    }

    [Fact]
    public void 空的已知清單回空結果()
    {
        var result = RepoDiscovery.EvaluateListing([], [], roundComplete: true, new Dictionary<long, int>());

        Assert.Empty(result.MissingRounds);
        Assert.Empty(result.RemoveIds);
        Assert.Empty(result.Renames);
    }

    [Fact]
    public void 已知清單有重複id時只算一次()
    {
        var known = new[] { Repo(1, "octo-org/sample-app"), Repo(1, "octo-org/sample-app") };

        var result = RepoDiscovery.EvaluateListing(known, [], roundComplete: true, new Dictionary<long, int>());

        AssertMissing(result.MissingRounds, (1, 1));
    }

    // ── SkipReasonText ──────────────────────────────────────────────

    [Theory]
    [InlineData(SkipReason.Muted, "已靜音")]
    [InlineData(SkipReason.Archived, "已封存")]
    [InlineData(SkipReason.Disabled, "已停用")]
    [InlineData(SkipReason.Fork, "fork")]
    [InlineData(SkipReason.Collaborator, "協作 repo")]
    [InlineData(SkipReason.Stale, "超過 90 天沒有活動")]
    [InlineData(SkipReason.NoWorkflows, "沒有 workflow（或 Actions 已停用，無法分辨）")]
    [InlineData(SkipReason.AllWorkflowsDisabled, "workflow 都已停用")]
    [InlineData(SkipReason.None, "")]
    [InlineData((SkipReason)99, "")]
    public void 略過原因文字照spec(SkipReason reason, string expected)
    {
        Assert.Equal(expected, RepoDiscovery.SkipReasonText(reason));
    }

    private static void AssertSkipped(RepoClassification result, SkipReason reason, bool canForceInclude)
    {
        Assert.False(result.Monitored);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(canForceInclude, result.CanForceInclude);
    }

    /// <summary>比對連續不見的輪數，不看字典的列舉順序。</summary>
    private static void AssertMissing(IReadOnlyDictionary<long, int> actual, params (long Id, int Rounds)[] expected)
    {
        Assert.Equal(
            expected.OrderBy(e => e.Id).ToArray(),
            actual.Select(p => (Id: p.Key, Rounds: p.Value)).OrderBy(e => e.Id).ToArray());
    }

    /// <param name="pushedDaysAgo">null 代表 pushed_at 為 null（從沒 push 過）。</param>
    private static RepoInfo Repo(
        long id,
        string fullName = "octo-org/sample-app",
        long ownerId = OrgId,
        bool fork = false,
        bool archived = false,
        bool disabled = false,
        int? pushedDaysAgo = 1,
        RepoCategory category = RepoCategory.Own)
    {
        var owner = fullName.Split('/')[0];
        var name = fullName.Split('/')[1];
        return new RepoInfo(
            Id: id,
            FullName: fullName,
            Name: name,
            OwnerId: ownerId,
            OwnerLogin: owner,
            OwnerType: ownerId == OrgId ? "Organization" : "User",
            IsPrivate: false,
            IsFork: fork,
            IsArchived: archived,
            IsDisabled: disabled,
            DefaultBranch: "main",
            HtmlUrl: $"https://github.com/{fullName}",
            PushedAt: pushedDaysAgo is { } days ? Now.AddDays(-days) : null,
            Category: category);
    }
}

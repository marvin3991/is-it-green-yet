using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Models;
using static IsItGreenYet.Core.Tests.Display.DisplaySamples;

namespace IsItGreenYet.Core.Tests.Display;

public class PrNumberResolverTests
{
    private static readonly RunInfo PullRequestRun = DisplaySamples.Run with
    {
        Event = "pull_request",
        HeadBranch = "feature/login",
    };

    [Fact]
    public void 同sha的開啟中PR優先()
    {
        var run = PullRequestRun with { PullRequestNumbers = [5] };
        PullRequestInfo[] open = [PullRequest(7, OtherSha, RepoId), PullRequest(9, Sha, RepoId)];

        Assert.Equal([9], PrNumberResolver.Resolve(run, open, storedNumbers: [3]));
    }

    [Fact]
    public void 比對不到退回pull_requests()
    {
        var run = PullRequestRun with { PullRequestNumbers = [5, 6] };
        PullRequestInfo[] open = [PullRequest(7, OtherSha, RepoId)];

        // 有多個時全部回傳（tooltip 列出全部），順序照 API。
        Assert.Equal([5, 6], PrNumberResolver.Resolve(run, open, storedNumbers: [3]));
    }

    [Fact]
    public void 都沒有用本地保存的編號()
    {
        // PR 關閉後 API 不再回傳，靠第一次觀察到時存下的編號。
        var run = PullRequestRun with { PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(7, OtherSha, RepoId)];

        Assert.Equal([4], PrNumberResolver.Resolve(run, open, storedNumbers: [4]));
    }

    [Fact]
    public void fork空陣列且比對不到回空()
    {
        var run = PullRequestRun with
        {
            HeadRepoId = ForkRepoId,
            HeadRepoFullName = "fork-user/sample-app",
            HeadRepoHtmlUrl = ForkHtmlUrl,
            PullRequestNumbers = [],
        };
        PullRequestInfo[] open = [PullRequest(7, OtherSha, ForkRepoId)];

        Assert.Empty(PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void fork的run可用來源repo相同的開啟中PR比對到()
    {
        var run = PullRequestRun with { HeadRepoId = ForkRepoId, PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(8, Sha, ForkRepoId)];

        Assert.Equal([8], PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 同sha但來源repo不同的PR不採用()
    {
        var run = PullRequestRun with { PullRequestNumbers = [5] };
        PullRequestInfo[] open = [PullRequest(8, Sha, ForkRepoId)];

        Assert.Equal([5], PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 開啟中PR的來源repo缺值時不採用()
    {
        var run = PullRequestRun with { PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(8, Sha, headRepoId: null)];

        Assert.Empty(PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 其他repo的開啟中PR不採用()
    {
        var run = PullRequestRun with { PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(8, Sha, RepoId, repoId: 999)];

        Assert.Empty(PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 非pull_request事件不比對開啟中PR()
    {
        // §10.6 第 1 步只在 event 是 pull_request 時做；其餘步驟照常。
        PullRequestInfo[] open = [PullRequest(9, Sha, RepoId)];

        Assert.Equal([5], PrNumberResolver.Resolve(DisplaySamples.Run with { PullRequestNumbers = [5] }, open, storedNumbers: []));
        Assert.Empty(PrNumberResolver.Resolve(DisplaySamples.Run with { Event = "pull_request_target" }, open, storedNumbers: []));
    }

    [Fact]
    public void PR追蹤停用時跳過比對()
    {
        var run = PullRequestRun with { PullRequestNumbers = [5] };

        Assert.Equal([5], PrNumberResolver.Resolve(run, openPullRequests: null, storedNumbers: [3]));
        Assert.Equal([3], PrNumberResolver.Resolve(run with { PullRequestNumbers = [] }, openPullRequests: null, storedNumbers: [3]));
    }

    [Fact]
    public void 同sha有多個開啟中PR時全部依編號排序()
    {
        var run = PullRequestRun with { PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(12, Sha, RepoId), PullRequest(9, Sha, RepoId), PullRequest(12, Sha, RepoId)];

        Assert.Equal([9, 12], PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 重複與不合理的編號只留有效的一個()
    {
        Assert.Equal([5, 6], PrNumberResolver.Resolve(PullRequestRun with { PullRequestNumbers = [5, 0, 5, -1, 6] }, null, []));
        Assert.Equal([4], PrNumberResolver.Resolve(PullRequestRun, null, storedNumbers: [4, 4, 0]));
        Assert.Empty(PrNumberResolver.Resolve(PullRequestRun with { PullRequestNumbers = [0] }, null, storedNumbers: [-2]));
    }

    [Fact]
    public void sha空白時不比對開啟中PR()
    {
        var run = PullRequestRun with { HeadSha = "", PullRequestNumbers = [] };
        PullRequestInfo[] open = [PullRequest(8, "", RepoId)];

        Assert.Empty(PrNumberResolver.Resolve(run, open, storedNumbers: []));
    }

    [Fact]
    public void 輸入清單為null時不丟例外()
    {
        var run = PullRequestRun with { PullRequestNumbers = null! };

        Assert.Empty(PrNumberResolver.Resolve(run, [], storedNumbers: null!));
        Assert.Empty(PrNumberResolver.Resolve(run, [null!], storedNumbers: []));
    }
}

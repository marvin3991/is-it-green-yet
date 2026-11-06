using IsItGreenYet.Core.Display;
using static IsItGreenYet.Core.Tests.Display.DisplaySamples;

namespace IsItGreenYet.Core.Tests.Display;

public class WebLinksTests
{
    [Fact]
    public void repo與PR與commit連結以repo的html_url為基底()
    {
        Assert.Equal(RepoHtmlUrl + "/actions", WebLinks.RepoActions(Repo).AbsoluteUri);
        Assert.Equal(RepoHtmlUrl + "/pull/17", WebLinks.PullRequest(Repo, 17).AbsoluteUri);
        Assert.Equal(RepoHtmlUrl + "/commit/" + Sha, WebLinks.Commit(Repo, Sha).AbsoluteUri);
    }

    [Fact]
    public void html_url結尾有斜線時不重複()
    {
        var repo = Repo with { HtmlUrl = RepoHtmlUrl + "/" };

        Assert.Equal(RepoHtmlUrl + "/actions", WebLinks.RepoActions(repo).AbsoluteUri);
        Assert.Equal(RepoHtmlUrl + "/tree/main", WebLinks.Branch(Run with { HeadRepoHtmlUrl = RepoHtmlUrl + "/" }, repo)!.AbsoluteUri);
    }

    [Fact]
    public void 分支含斜線與井號要逐段編碼()
    {
        var uri = WebLinks.Branch(Run with { HeadBranch = "feature/a#1" }, Repo);

        Assert.NotNull(uri);
        Assert.Equal(RepoHtmlUrl + "/tree/feature/a%231", uri.AbsoluteUri);
        // # 被編碼後不會被當成片段，整個分支名都留在路徑裡。
        Assert.Equal("", uri.Fragment);
        Assert.True(WebLinks.IsAllowed(uri));
    }

    [Theory]
    [InlineData("100%", "100%25")]
    [InlineData("fix login", "fix%20login")]
    [InlineData("功能/登入", "%E5%8A%9F%E8%83%BD/%E7%99%BB%E5%85%A5")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("a&b=c", "a%26b%3Dc")]
    [InlineData("release/v1.2", "release/v1.2")]
    [InlineData("user/feature/%20 #中", "user/feature/%2520%20%23%E4%B8%AD")]
    public void 分支名稱的特殊字元逐段編碼(string branch, string encoded)
    {
        var uri = WebLinks.Branch(Run with { HeadBranch = branch }, Repo);

        Assert.NotNull(uri);
        Assert.Equal(RepoHtmlUrl + "/tree/" + encoded, uri.AbsoluteUri);
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
        // 解碼後要還原成原本的分支名，斜線仍是路徑分段。
        Assert.EndsWith("/tree/" + branch, Uri.UnescapeDataString(uri.AbsolutePath));
    }

    [Fact]
    public void fork的分支用來源repo的html_url()
    {
        var run = Run with
        {
            HeadRepoId = ForkRepoId,
            HeadRepoFullName = "fork-user/sample-app",
            HeadRepoHtmlUrl = ForkHtmlUrl,
            HeadBranch = "main",
        };

        Assert.Equal(ForkHtmlUrl + "/tree/main", WebLinks.Branch(run, Repo)!.AbsoluteUri);
    }

    [Fact]
    public void head_branch為null回null()
    {
        Assert.Null(WebLinks.Branch(Run with { HeadBranch = null }, Repo));
        Assert.Null(WebLinks.Branch(Run with { HeadBranch = "" }, Repo));
    }

    [Fact]
    public void 來源repo網址缺值時同一個repo退回repo網址()
    {
        var run = Run with { HeadRepoHtmlUrl = null };

        Assert.Equal(RepoHtmlUrl + "/tree/main", WebLinks.Branch(run, Repo)!.AbsoluteUri);
    }

    [Fact]
    public void fork的來源repo網址缺值時不給連結()
    {
        // 退回本 repo 會開到同名的另一個分支，所以寧可不給（§10.5 避免和本 repo 的同名分支混淆）。
        var run = Run with { HeadRepoId = ForkRepoId, HeadRepoHtmlUrl = null };

        Assert.Null(WebLinks.Branch(run, Repo));
        Assert.Null(WebLinks.Branch(run with { HeadRepoHtmlUrl = "  " }, Repo));
    }

    [Theory]
    [InlineData("https://github.com/octo-org/sample-app", true)]
    [InlineData("https://github.com/octo-org/sample-app/tree/feature/a%231", true)]
    [InlineData("HTTPS://GITHUB.COM/octo-org/sample-app", true)]
    [InlineData("https://github.com:443/octo-org/sample-app", true)]
    [InlineData("http://github.com/octo-org/sample-app", false)]
    [InlineData("https://api.github.com/repos/octo-org/sample-app", false)]
    [InlineData("https://gist.github.com/octo-org", false)]
    [InlineData("https://github.com.evil.example/octo-org", false)]
    [InlineData("https://evil.example/github.com", false)]
    [InlineData("https://github.com@evil.example/octo-org", false)]
    [InlineData("https://user@github.com/octo-org", false)]
    [InlineData("https://github.com:8443/octo-org", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://github.com/octo-org", false)]
    public void 只允許https的github_com(string url, bool allowed)
    {
        Assert.Equal(allowed, WebLinks.IsAllowed(new Uri(url, UriKind.Absolute)));
    }

    [Fact]
    public void 專案網址符合需求而且允許開啟()
    {
        Assert.Equal("https://github.com/marvin3991/is-it-green-yet", WebLinks.ProjectRepo.AbsoluteUri);
        Assert.True(WebLinks.IsAllowed(WebLinks.ProjectRepo));
    }

    [Fact]
    public void 相對網址與null不允許()
    {
        Assert.False(WebLinks.IsAllowed(new Uri("/octo-org/sample-app", UriKind.Relative)));
        Assert.False(WebLinks.IsAllowed(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://github.com/octo-org/sample-app")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/octo-org/sample-app")]
    public void html_url不合法時不丟例外且連結不被允許(string htmlUrl)
    {
        var repo = Repo with { HtmlUrl = htmlUrl };
        var run = Run with { HeadRepoHtmlUrl = htmlUrl };

        Assert.False(WebLinks.IsAllowed(WebLinks.RepoActions(repo)));
        Assert.False(WebLinks.IsAllowed(WebLinks.PullRequest(repo, 17)));
        Assert.False(WebLinks.IsAllowed(WebLinks.Commit(repo, Sha)));
        var branch = WebLinks.Branch(run, repo);
        Assert.True(branch is null || !WebLinks.IsAllowed(branch));
    }

    [Fact]
    public void commit的sha也會編碼()
    {
        // sha 正常只有十六進位字元；萬一不是，也不能讓它跳出路徑。
        Assert.Equal(RepoHtmlUrl + "/commit/a%2F..%3Fb", WebLinks.Commit(Repo, "a/..?b").AbsoluteUri);
    }
}

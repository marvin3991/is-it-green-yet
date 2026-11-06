using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Tests.GitHub;

public class LinkHeaderTests
{
    private const string Page2 = "https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100&page=2";

    [Fact]
    public void 取rel_next()
    {
        var header =
            $"<{Page2}>; rel=\"next\", " +
            "<https://api.github.com/user/repos?affiliation=owner,organization_member&per_page=100&page=5>; rel=\"last\"";

        // 網址本身含逗號（affiliation=owner,organization_member），不能直接用逗號切開。
        Assert.Equal(Page2, LinkHeader.Next(header));
    }

    [Fact]
    public void next不在第一個也找得到()
    {
        var header =
            "<https://api.github.com/user/repos?per_page=100&page=1>; rel=\"prev\", " +
            "<https://api.github.com/user/repos?per_page=100&page=1>; rel=\"first\", " +
            "<https://api.github.com/user/repos?per_page=100&page=3>; rel=\"next\"";

        Assert.Equal("https://api.github.com/user/repos?per_page=100&page=3", LinkHeader.Next(header));
    }

    [Fact]
    public void 沒有next回null()
    {
        var header =
            "<https://api.github.com/user/repos?per_page=100&page=1>; rel=\"prev\", " +
            "<https://api.github.com/user/repos?per_page=100&page=1>; rel=\"first\"";

        Assert.Null(LinkHeader.Next(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 沒有header回null(string? header)
    {
        Assert.Null(LinkHeader.Next(header));
    }

    [Theory]
    [InlineData("<https://api.github.com/x?page=2>; rel=next")]
    [InlineData("<https://api.github.com/x?page=2>; REL=\"NEXT\"")]
    [InlineData("<https://api.github.com/x?page=2>;rel=\"next\"")]
    [InlineData("  <https://api.github.com/x?page=2> ;  rel = \"next\"  ")]
    [InlineData("<https://api.github.com/x?page=2>; title=\"a, b; c\"; rel=\"next\"")]
    [InlineData("<https://api.github.com/x?page=2>; rel=\"last next\"")]
    public void rel的各種寫法(string header)
    {
        Assert.Equal("https://api.github.com/x?page=2", LinkHeader.Next(header));
    }

    [Theory]
    [InlineData("<https://api.github.com/x?page=2>; rel=\"nextpage\"")]
    [InlineData("<https://api.github.com/x?page=2>; rel=\"prev\"; next")]
    public void 不是next的rel不算(string header)
    {
        Assert.Null(LinkHeader.Next(header));
    }

    [Theory]
    [InlineData("https://api.github.com/x?page=2; rel=\"next\"")]
    [InlineData("<https://api.github.com/x?page=2; rel=\"next\"")]
    [InlineData("<>; rel=\"next\"")]
    [InlineData("garbage")]
    public void 格式錯誤回null(string header)
    {
        Assert.Null(LinkHeader.Next(header));
    }

    [Fact]
    public void 格式錯誤的項目之後仍能找到next()
    {
        var header = "garbage; rel=\"next\", <>; rel=\"next\", <https://api.github.com/x?page=3>; rel=\"next\"";

        Assert.Equal("https://api.github.com/x?page=3", LinkHeader.Next(header));
    }

    [Fact]
    public void 有多個next時取第一個()
    {
        var header = "<https://api.github.com/x?page=2>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"next\"";

        Assert.Equal("https://api.github.com/x?page=2", LinkHeader.Next(header));
    }
}

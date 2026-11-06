using IsItGreenYet.Core.Updates;

namespace IsItGreenYet.Core.Tests.Updates;

public class SemVersionTests
{
    /// <summary>20 位數，超過 long 的最大值（19 位數）。</summary>
    private const string TwentyDigits = "12345678901234567890";

    /// <summary>19 位數，等於 long 的最大值。</summary>
    private const string NineteenDigits = "9223372036854775807";

    [Theory]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.0.0-0a", "1.0.0-0a")]
    [InlineData("1.0.0--", "1.0.0--")]
    [InlineData("1.0.0-0", "1.0.0-0")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1+a.b", "1.0.0-rc.1")]
    [InlineData("1.0.0+20261104", "1.0.0")]
    [InlineData("1.0.0+01", "1.0.0")]
    [InlineData(TwentyDigits + ".0.0", TwentyDigits + ".0.0")]
    public void 合法版本可解析(string text, string expected)
    {
        Assert.True(SemVersion.TryParse(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("v1.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("vv1.2.3")]
    [InlineData("0.2.0.0")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+a..b")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\n")]
    [InlineData("１.2.3")]
    [InlineData("1.2.3-１")]
    [InlineData("1１.2.3")]
    [InlineData("1.1２.3")]
    [InlineData("1.2.1３")]
    [InlineData("1.2.3-1１")]
    [InlineData("1.2.3-１a")]
    [InlineData("1.2.3-a１")]
    [InlineData("1.2.3+１")]
    [InlineData("1.2.3-a.01")]
    public void 不合法版本解析失敗(string? text)
    {
        Assert.False(SemVersion.TryParse(text, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void tag只去掉一個小寫v()
    {
        Assert.True(SemVersion.TryParseTag("v1.2.3", out var tagged));
        Assert.Equal("1.2.3", tagged.ToString());
        Assert.True(SemVersion.TryParseTag("v1.2.3-beta.1+abc", out var prerelease));
        Assert.Equal("1.2.3-beta.1", prerelease.ToString());
        Assert.True(SemVersion.TryParseTag("1.2.3", out var plain));
        Assert.Equal("1.2.3", plain.ToString());

        Assert.False(SemVersion.TryParseTag("vv1.2.3", out _));
        Assert.False(SemVersion.TryParseTag("V1.2.3", out _));
        Assert.False(SemVersion.TryParseTag("v", out _));
        Assert.False(SemVersion.TryParseTag("latest", out _));
        Assert.False(SemVersion.TryParseTag(string.Empty, out _));
        Assert.False(SemVersion.TryParseTag(null, out _));
    }

    [Fact]
    public void 目前版本切掉加號後解析()
    {
        Assert.True(SemVersion.TryParseCurrent("0.2.0+abc", out var current));
        Assert.Equal(Parse("0.2.0"), current);
        Assert.True(SemVersion.TryParseCurrent("0.3.0-beta.1+abc", out var prerelease));
        Assert.Equal("0.3.0-beta.1", prerelease.ToString());
        Assert.True(SemVersion.TryParseCurrent("0.2.0", out var plain));
        Assert.Equal("0.2.0", plain.ToString());

        Assert.False(SemVersion.TryParseCurrent("0.2.0.0", out _));
        Assert.False(SemVersion.TryParseCurrent(string.Empty, out _));
        Assert.False(SemVersion.TryParseCurrent("+abc", out _));
        Assert.False(SemVersion.TryParseCurrent(null, out _));
    }

    [Theory]
    [InlineData("0.2.0+abc+def")]
    [InlineData("0.2.0+a_b")]
    [InlineData("0.2.0+")]
    public void 目前版本加號之後不是合法的build也切掉(string text)
    {
        Assert.True(SemVersion.TryParseCurrent(text, out var current));
        Assert.Equal("0.2.0", current.ToString());
    }

    [Fact]
    public void SemVer第11條的順序範例全部成立()
    {
        string[] chain =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
            "2.0.0", "2.1.0", "2.1.1",
        ];

        for (var lower = 0; lower < chain.Length; lower++)
        {
            for (var higher = lower + 1; higher < chain.Length; higher++)
            {
                AssertLess(chain[lower], chain[higher]);
            }
        }
    }

    [Fact]
    public void 一點十大於一點九()
    {
        AssertLess("1.9.0", "1.10.0");
    }

    [Fact]
    public void 數字識別子按數值比()
    {
        AssertLess("1.0.0-rc.9", "1.0.0-rc.10");
    }

    [Fact]
    public void 數字識別子小於英數識別子()
    {
        AssertLess("1.0.0-1", "1.0.0-a");
        AssertLess("1.0.0-999", "1.0.0--");
    }

    [Fact]
    public void 英數識別子照ASCII比()
    {
        AssertLess("1.0.0-Z", "1.0.0-a");
        AssertLess("1.0.0-rc10", "1.0.0-rc9");
    }

    [Fact]
    public void 欄位少的prerelease較小()
    {
        AssertLess("1.0.0-a", "1.0.0-a.1");
    }

    [Fact]
    public void 正式版大於同號prerelease()
    {
        AssertLess("1.0.0-rc.1", "1.0.0");
        AssertLess("1.0.0-zzz", "1.0.0");
    }

    [Fact]
    public void 超過long範圍也能比較()
    {
        AssertLess(NineteenDigits + ".0.0", TwentyDigits + ".0.0");
        AssertLess("0." + NineteenDigits + ".0", "0." + TwentyDigits + ".0");
        AssertLess("0.0." + NineteenDigits, "0.0." + TwentyDigits);
        AssertLess("1.0.0-" + NineteenDigits, "1.0.0-" + TwentyDigits);
        AssertLess(TwentyDigits + ".0.0", "12345678901234567891.0.0");
    }

    [Fact]
    public void build不影響比較與相等()
    {
        var left = Parse("1.0.0+a");
        var right = Parse("1.0.0+b");

        Assert.Equal(0, left.CompareTo(right));
        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal("1.0.0", left.ToString());
    }

    [Fact]
    public void prerelease不同就不相等()
    {
        Assert.False(Parse("1.0.0-a").Equals(Parse("1.0.0-b")));
        Assert.False(Parse("1.0.0-a").Equals(Parse("1.0.0")));
    }

    [Fact]
    public void 和null比較時較大且不相等()
    {
        var version = Parse("1.0.0");

        Assert.True(version.CompareTo(null) > 0);
        Assert.False(version.Equals((SemVersion?)null));
        Assert.False(version.Equals((object?)null));
        Assert.False(version.Equals("1.0.0"));
    }

    private static SemVersion Parse(string text)
    {
        Assert.True(SemVersion.TryParse(text, out var version), text);
        return version;
    }

    private static void AssertLess(string lower, string higher)
    {
        Assert.True(Parse(lower).CompareTo(Parse(higher)) < 0, $"{lower} 應該小於 {higher}");
        Assert.True(Parse(higher).CompareTo(Parse(lower)) > 0, $"{higher} 應該大於 {lower}");
    }
}

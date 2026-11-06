using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace IsItGreenYet.Core.Updates;

/// <summary>
/// SemVer 2.0.0 的版本號（https://semver.org/），用來比較 release 的 tag 和目前版本。
/// 版本號的數字存成字串、比較時先比位數再比內容，再長的數字也不會溢位。build metadata 不參與比較與相等，也不留在 <see cref="ToString"/>。
/// </summary>
internal sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private const char TagPrefix = 'v';
    private const char BuildSeparator = '+';
    private const char IdentifierSeparator = '.';

    // semver.org 的官方正規表示式，兩處調整：[0-9] 取代 \d（擋全形數字）、\A…\z 取代 ^…$（$ 會放過結尾換行）。
    private static readonly Regex Pattern = new(
        @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)"
        + @"(?:-(?<prerelease>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + @"(?:\+(?<buildmetadata>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z",
        RegexOptions.CultureInvariant);

    private readonly string _major;
    private readonly string _minor;
    private readonly string _patch;

    /// <summary>prerelease 的識別子；正式版是空陣列。</summary>
    private readonly string[] _prerelease;

    /// <summary>major.minor.patch[-pre]，也是相等的依據。</summary>
    private readonly string _text;

    private SemVersion(string major, string minor, string patch, string? prerelease)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prerelease = prerelease?.Split(IdentifierSeparator) ?? [];
        _text = prerelease is null ? $"{major}.{minor}.{patch}" : $"{major}.{minor}.{patch}-{prerelease}";
    }

    /// <summary>照 SemVer 嚴格解析：不去空白、不接受 <c>v</c> 前綴。</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (text is null || Pattern.Match(text) is not { Success: true } match)
        {
            return false;
        }

        var prerelease = match.Groups["prerelease"];
        version = new SemVersion(
            match.Groups["major"].Value,
            match.Groups["minor"].Value,
            match.Groups["patch"].Value,
            prerelease.Success ? prerelease.Value : null);
        return true;
    }

    /// <summary>release 的 tag：只去掉一個小寫 <c>v</c> 前綴。</summary>
    public static bool TryParseTag(string? tag, [NotNullWhen(true)] out SemVersion? version) =>
        TryParse(tag is not null && tag.StartsWith(TagPrefix) ? tag[1..] : tag, out version);

    /// <summary>目前版本（InformationalVersion）：切掉第一個 <c>+</c> 之後的部分（SDK 接上的 commit）再解析。</summary>
    public static bool TryParseCurrent(string? version, [NotNullWhen(true)] out SemVersion? result) =>
        TryParse(version?.Split(BuildSeparator, count: 2)[0], out result);

    /// <summary>SemVer 第 11 條的順序。</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = CompareNumbers(_major, other._major);
        if (result == 0)
        {
            result = CompareNumbers(_minor, other._minor);
        }

        if (result == 0)
        {
            result = CompareNumbers(_patch, other._patch);
        }

        return result != 0 ? result : ComparePrerelease(_prerelease, other._prerelease);
    }

    public bool Equals(SemVersion? other) => other is not null && _text == other._text;

    public override bool Equals(object? obj) => Equals(obj as SemVersion);

    public override int GetHashCode() => _text.GetHashCode(StringComparison.Ordinal);

    /// <summary>major.minor.patch[-pre]，不含 <c>v</c> 與 build metadata。</summary>
    public override string ToString() => _text;

    /// <summary>數字沒有前導零（<see cref="Pattern"/> 保證），位數多的就大，位數相同時逐字比。</summary>
    private static int CompareNumbers(string left, string right)
    {
        var byLength = left.Length.CompareTo(right.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }

    private static int ComparePrerelease(string[] left, string[] right)
    {
        // 正式版大於 prerelease，所以這裡是空陣列的那一邊大。
        if (left.Length == 0 || right.Length == 0)
        {
            return right.Length.CompareTo(left.Length);
        }

        var shared = Math.Min(left.Length, right.Length);
        for (var index = 0; index < shared; index++)
        {
            var result = CompareIdentifiers(left[index], right[index]);
            if (result != 0)
            {
                return result;
            }
        }

        // 前面都相同時，欄位少的較小。
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric != rightNumeric)
        {
            // 數字小於英數。
            return leftNumeric ? -1 : 1;
        }

        return leftNumeric ? CompareNumbers(left, right) : string.CompareOrdinal(left, right);
    }
}

namespace IsItGreenYet.Core.GitHub;

/// <summary>只取 Link header 的 rel="next"，不自己組 page（spec §4.1）。</summary>
public static class LinkHeader
{
    private const string NextRelation = "next";

    /// <summary>
    /// 依 RFC 8288 的格式 <c>&lt;網址&gt;; rel="next", &lt;網址&gt;; rel="last"</c> 找出 next 的網址。
    /// 網址本身可能含逗號（例如 <c>affiliation=owner,organization_member</c>），所以不能直接用逗號切開，
    /// 而是逐一讀出角括號裡的網址與它後面的參數。格式錯誤的項目略過；找不到回 <see langword="null"/>。
    /// </summary>
    public static string? Next(string? linkHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(linkHeaderValue))
        {
            return null;
        }

        var text = linkHeaderValue.AsSpan();
        var position = 0;
        while (true)
        {
            var open = text[position..].IndexOf('<');
            if (open < 0)
            {
                return null;
            }

            var urlStart = position + open + 1;
            var close = text[urlStart..].IndexOf('>');
            if (close < 0)
            {
                return null;
            }

            var url = text.Slice(urlStart, close).Trim();
            position = urlStart + close + 1;
            var isNext = ReadParameters(text, ref position);

            if (isNext && !url.IsEmpty)
            {
                return url.ToString();
            }
        }
    }

    /// <summary>
    /// 讀出一個 link-value 的參數，直到頂層的逗號或字串結尾；位置停在逗號之後。
    /// 引號內的逗號、分號不算分隔。回傳 rel 參數是否含 next（rel 可以是空白分隔的多個值）。
    /// </summary>
    private static bool ReadParameters(ReadOnlySpan<char> text, ref int position)
    {
        var isNext = false;
        while (position < text.Length)
        {
            // 參數之間以分號分隔；遇到逗號就是下一個 link-value。
            var separator = text[position..].IndexOfAny(';', ',');
            if (separator < 0)
            {
                position = text.Length;
                return isNext;
            }

            position += separator;
            if (text[position] == ',')
            {
                position++;
                return isNext;
            }

            position++;
            var (name, value) = ReadParameter(text, ref position);
            if (name.Equals("rel", StringComparison.OrdinalIgnoreCase) && ContainsNext(value))
            {
                isNext = true;
            }
        }

        return isNext;
    }

    /// <summary>讀出 <c>name=value</c> 或 <c>name="value"</c>；位置停在下一個分號或逗號上（不吃掉它）。</summary>
    private static (string Name, string Value) ReadParameter(ReadOnlySpan<char> text, ref int position)
    {
        var nameEnd = text[position..].IndexOfAny('=', ';', ',');
        if (nameEnd < 0 || text[position + nameEnd] != '=')
        {
            // 沒有值的參數（例如單獨的 next），不是 rel。
            var end = nameEnd < 0 ? text.Length : position + nameEnd;
            var bare = text[position..end].Trim().ToString();
            position = end;
            return (bare, string.Empty);
        }

        var name = text.Slice(position, nameEnd).Trim().ToString();
        position += nameEnd + 1;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position < text.Length && text[position] == '"')
        {
            var valueStart = position + 1;
            var quoteEnd = text[valueStart..].IndexOf('"');
            if (quoteEnd < 0)
            {
                // 引號沒有關上：剩下的都當成值。
                position = text.Length;
                return (name, text[valueStart..].ToString());
            }

            position = valueStart + quoteEnd + 1;
            return (name, text.Slice(valueStart, quoteEnd).ToString());
        }

        var valueEnd = text[position..].IndexOfAny(';', ',');
        var stop = valueEnd < 0 ? text.Length : position + valueEnd;
        var token = text[position..stop].Trim().ToString();
        position = stop;
        return (name, token);
    }

    private static bool ContainsNext(string relations)
    {
        foreach (var relation in relations.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (relation.Equals(NextRelation, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

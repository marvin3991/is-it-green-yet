using System.Text;

namespace IsItGreenYet.Core.GitHub;

/// <summary>從串流讀出最後 N 行，不把整份 log 載入記憶體（job log 可能有數十 MB）。</summary>
public static class LogTail
{
    /// <summary>
    /// 設計選擇（spec 沒有規定）：一行最多保留的字元數。沒有換行的超長內容只留這一行的結尾，
    /// 否則整份沒有換行的 log 會變成一行、全部留在記憶體裡。
    /// </summary>
    internal const int MaxLineLength = 16 * 1024;

    /// <summary>設計選擇：每次從串流讀取的位元組數。</summary>
    private const int BufferBytes = 64 * 1024;

    private const char ByteOrderMark = (char)0xFEFF;

    // 不輸出 BOM、遇到不合法的位元組換成替代字元而不丟例外。
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// 依序讀完串流，只留最後 <paramref name="maxLines"/> 行。行尾同 StreamReader.ReadLine：
    /// <c>\r\n</c>、<c>\n</c>、單獨的 <c>\r</c> 都算；最後一行沒有換行也算一行；開頭的 BOM 去掉。
    /// 串流不需要能 Seek，HTTP 下載的串流可以直接傳進來。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadLastLinesAsync(Stream stream, int maxLines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);

        var decoder = Utf8.GetDecoder();
        var bytes = new byte[BufferBytes];
        var chars = new char[Utf8.GetMaxCharCount(BufferBytes)];
        var state = new TailState(maxLines);

        int read;
        while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            state.Feed(chars.AsSpan(0, count));
        }

        // 串流結尾還沒湊成字元的位元組（截斷的 UTF-8）換成替代字元。
        var rest = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        state.Feed(chars.AsSpan(0, rest));
        return state.Finish();
    }

    /// <summary>逐段餵入解碼後的字元，維護「目前這一行」與最後 N 行。</summary>
    private sealed class TailState(int maxLines)
    {
        private readonly Queue<string> _lines = new();
        private readonly StringBuilder _current = new();
        private bool _atStart = true;
        private bool _afterCarriageReturn;

        public void Feed(ReadOnlySpan<char> chars)
        {
            if (_atStart && !chars.IsEmpty)
            {
                _atStart = false;
                if (chars[0] == ByteOrderMark)
                {
                    chars = chars[1..];
                }
            }

            while (!chars.IsEmpty)
            {
                // \r\n 可能被切在兩段之間：上一段以 \r 結尾時，這一段開頭的 \n 不算新的一行。
                if (_afterCarriageReturn)
                {
                    _afterCarriageReturn = false;
                    if (chars[0] == '\n')
                    {
                        chars = chars[1..];
                        continue;
                    }
                }

                var lineBreak = chars.IndexOfAny('\r', '\n');
                if (lineBreak < 0)
                {
                    Append(chars);
                    return;
                }

                Append(chars[..lineBreak]);
                EndLine();
                _afterCarriageReturn = chars[lineBreak] == '\r';
                chars = chars[(lineBreak + 1)..];
            }
        }

        public IReadOnlyList<string> Finish()
        {
            // 最後一行沒有換行也算一行；以換行結尾時不多出一個空行。
            if (_current.Length > 0)
            {
                EndLine();
            }

            return _lines.ToArray();
        }

        private void Append(ReadOnlySpan<char> chars)
        {
            _current.Append(chars);

            // 超過上限兩倍才裁掉前面，攤提下來每個字元只搬一次。
            if (_current.Length > 2 * MaxLineLength)
            {
                _current.Remove(0, _current.Length - MaxLineLength);
            }
        }

        private void EndLine()
        {
            var start = Math.Max(0, _current.Length - MaxLineLength);
            _lines.Enqueue(_current.ToString(start, _current.Length - start));
            _current.Clear();
            if (_lines.Count > maxLines)
            {
                _lines.Dequeue();
            }
        }
    }
}

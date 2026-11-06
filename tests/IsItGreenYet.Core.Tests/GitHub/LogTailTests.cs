using System.Globalization;
using System.Text;
using IsItGreenYet.Core.GitHub;

namespace IsItGreenYet.Core.Tests.GitHub;

public class LogTailTests
{
    /// <summary>單一測試允許的最長時間；遠大於實際所需，只用來攔下卡住或極慢的實作。</summary>
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(60);

    private const char Bom = (char)0xFEFF;

    private const char ReplacementCharacter = (char)0xFFFD;

    [Fact]
    public async Task 只留最後N行()
    {
        var lines = await ReadAsync("1\n2\n3\n4\n5\n", 3);

        Assert.Equal(["3", "4", "5"], lines);
    }

    [Fact]
    public async Task 行數不足N行時全部回傳()
    {
        var lines = await ReadAsync("first\nsecond\n", 200);

        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task 沒有結尾換行()
    {
        var lines = await ReadAsync("a\nb\nc", 2);

        Assert.Equal(["b", "c"], lines);
    }

    [Fact]
    public async Task CRLF混LF()
    {
        var lines = await ReadAsync("a\r\nb\nc\r\nd\n\r\ne", 10);

        Assert.Equal(["a", "b", "c", "d", "", "e"], lines);
        Assert.All(lines, line => Assert.DoesNotContain('\r', line));
    }

    [Fact]
    public async Task 單獨的CR也算換行()
    {
        // 同 StreamReader.ReadLine：\r、\n、\r\n 都是行尾。
        var lines = await ReadAsync("progress 10%\rprogress 90%\rdone\n", 10);

        Assert.Equal(["progress 10%", "progress 90%", "done"], lines);
    }

    [Fact]
    public async Task 空行保留()
    {
        var lines = await ReadAsync("\n\nx\n\n", 10);

        Assert.Equal(["", "", "x", ""], lines);
    }

    [Fact]
    public async Task 空串流回空清單()
    {
        Assert.Empty(await ReadAsync("", 5));
    }

    [Fact]
    public async Task 開頭的BOM去掉但內容中的不動()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes($"first\nsec{Bom}ond\n")).ToArray();

        var lines = await LogTail.ReadLastLinesAsync(new MemoryStream(bytes), 10, CancellationToken.None);

        Assert.Equal(["first", $"sec{Bom}ond"], lines);
    }

    [Fact]
    public async Task 每次只讀一個位元組時CRLF與中文也不會被切壞()
    {
        // CRLF 與 UTF-8 多位元組字元剛好跨過讀取邊界。
        const string text = "建置失敗\r\n第二行：錯誤 ✗\r\n最後一行沒有換行";
        var stream = new TrickleStream(Encoding.UTF8.GetBytes(text));

        var lines = await LogTail.ReadLastLinesAsync(stream, 10, CancellationToken.None);

        Assert.Equal(["建置失敗", "第二行：錯誤 ✗", "最後一行沒有換行"], lines);
    }

    [Fact]
    public async Task 不合法的UTF8換成替代字元而不丟例外()
    {
        byte[] bytes = [(byte)'o', (byte)'k', 0xFF, (byte)'\n', (byte)'n', (byte)'e', (byte)'x', (byte)'t'];

        var lines = await LogTail.ReadLastLinesAsync(new MemoryStream(bytes), 10, CancellationToken.None);

        Assert.Equal([$"ok{ReplacementCharacter}", "next"], lines);
    }

    [Fact]
    public async Task 超長的一行只留結尾()
    {
        var head = new string('h', LogTail.MaxLineLength);
        var tail = new string('t', LogTail.MaxLineLength - 4) + "END!";
        var lines = await ReadAsync(head + tail + "\nnext", 10);

        Assert.Equal(2, lines.Count);
        Assert.Equal(tail, lines[0]);
        Assert.Equal("next", lines[1]);
    }

    [Fact]
    public async Task 剛好等於上限長度的一行完整保留()
    {
        var line = new string('x', LogTail.MaxLineLength);

        var lines = await ReadAsync(line + "\r\n", 10);

        Assert.Equal([line], lines);
    }

    [Fact]
    public async Task _50MB串流不整檔載入()
    {
        const long totalBytes = 50L * 1024 * 1024;
        const int maxLines = 200;
        var stream = new GeneratedLogStream(totalBytes);
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        var lines = await LogTail.ReadLastLinesAsync(stream, maxLines, CancellationToken.None).WaitAsync(GenerousTimeout);

        Assert.True(stream.Position >= totalBytes);
        Assert.Equal(GeneratedLogStream.ExpectedTail(stream.LinesWritten, maxLines), lines);
        // 讀到九成時量一次存活的物件：整份載入至少要 50 MiB 的 bytes 或 100 MiB 的 UTF-16 字元。
        Assert.NotNull(stream.LiveBytesNearEnd);
        Assert.True(stream.LiveBytesNearEnd - baseline < 32L * 1024 * 1024,
            $"讀取途中多出 {stream.LiveBytesNearEnd - baseline} bytes 存活物件");
    }

    [Fact]
    public async Task 取消時丟出OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LogTail.ReadLastLinesAsync(new GeneratedLogStream(1024 * 1024), 10, cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 行數必須大於0(int maxLines)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LogTail.ReadLastLinesAsync(new MemoryStream(), maxLines, CancellationToken.None));
    }

    [Fact]
    public async Task 串流不可為null()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => LogTail.ReadLastLinesAsync(null!, 10, CancellationToken.None));
    }

    private static Task<IReadOnlyList<string>> ReadAsync(string text, int maxLines) =>
        LogTail.ReadLastLinesAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), maxLines, CancellationToken.None);

    /// <summary>每次 Read 最多只給一個位元組，用來測讀取邊界。</summary>
    private sealed class TrickleStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= content.Length || buffer.IsEmpty)
            {
                return 0;
            }

            buffer[0] = content[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 邊讀邊產生內容的唯讀串流：不能 Seek、不給長度，自己也不保存內容。
    /// 第 i 行是 <c>line 00000001 ... </c>，奇數行用 CRLF、偶數行用 LF，最後一行沒有換行。
    /// </summary>
    private sealed class GeneratedLogStream(long totalBytes) : Stream
    {
        private const string Padding = " The quick brown fox jumps over the lazy dog.";

        private byte[] _current = [];
        private int _offset;
        private long _position;
        private long _produced;
        private bool _measured;

        public long LinesWritten { get; private set; }

        public long? LiveBytesNearEnd { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public static string LineText(long number) =>
            "line " + number.ToString("D8", CultureInfo.InvariantCulture) + Padding;

        public static IReadOnlyList<string> ExpectedTail(long lineCount, int maxLines)
        {
            var first = Math.Max(1, lineCount - maxLines + 1);
            var expected = new List<string>();
            for (var number = first; number <= lineCount; number++)
            {
                expected.Add(LineText(number));
            }

            return expected;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                if (_offset >= _current.Length && !NextLine())
                {
                    break;
                }

                var take = Math.Min(buffer.Length - written, _current.Length - _offset);
                _current.AsSpan(_offset, take).CopyTo(buffer[written..]);
                _offset += take;
                written += take;
            }

            _position += written;
            if (!_measured && _position >= totalBytes * 9 / 10)
            {
                _measured = true;
                LiveBytesNearEnd = GC.GetTotalMemory(forceFullCollection: true);
            }

            return written;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool NextLine()
        {
            if (_produced >= totalBytes)
            {
                return false;
            }

            LinesWritten++;
            var text = LineText(LinesWritten);
            // 這一行就會到達總長度時不加換行，最後一行沒有結尾換行。
            var last = _produced + text.Length >= totalBytes;
            var ending = last ? "" : LinesWritten % 2 == 1 ? "\r\n" : "\n";
            _current = Encoding.ASCII.GetBytes(text + ending);
            _produced += _current.Length;
            _offset = 0;
            return true;
        }
    }
}

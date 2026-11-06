using System.Globalization;
using System.Text;

namespace IsItGreenYet.Core.Logging;

/// <summary>api.log 的一行（spec §14.4）。不含任何 header 或 body。</summary>
public sealed record ApiLogEntry(
    string Method,
    Uri Url,
    int StatusCode,
    string? Resource,
    int? Remaining,
    int? Limit,
    TimeSpan Elapsed,
    Uri? RedirectTo,
    int? RetryAfterSeconds);

/// <summary>logs\api.log：每個請求一行，Tab 分隔，超過 ApiLogMaxBytes 輪替。寫入失敗一律吞掉。</summary>
public sealed class ApiLog(string logsDirectory, SecretRedactor redactor, TimeProvider time)
{
    private const string FileName = "api.log";

    /// <summary>輪替後的舊檔：api.1.log 最新，數字越大越舊（spec §14.4）。</summary>
    private const string RotatedFileNameFormat = "api.{0}.log";

    /// <summary>輪替中途的暫存名稱；api.log 先改名成這個，成功後才推移舊檔。</summary>
    private const string RotatingFileName = "api.rotating.log";

    /// <summary>spec §14.4 範例：<c>2026-10-24T10:07:33.123+08:00</c>。</summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    private const char Separator = '\t';

    private const string NotePrefix = "# ";

    /// <summary>額度欄位整個沒有值（例如簽名下載主機的回應）或只缺其中一個數字時的補位，讓每行的欄位數固定。</summary>
    private const string Missing = "-";

    private readonly string? _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory) ? null : logsDirectory;
    private readonly SecretRedactor _redactor = redactor;
    private readonly TimeProvider _time = time;
    private readonly Lock _gate = new();

    public void Write(ApiLogEntry entry)
    {
        if (_logsDirectory is null)
        {
            return;
        }

        try
        {
            var line = new StringBuilder()
                .Append(_time.GetLocalNow().ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append(Separator).Append(entry.Method)
                .Append(Separator).Append(SecretRedactor.DescribeUrl(entry.Url))
                .Append(Separator).Append(entry.StatusCode.ToString(CultureInfo.InvariantCulture))
                .Append(Separator).Append(QuotaText(entry))
                .Append(Separator).Append(ElapsedText(entry.Elapsed));

            if (entry.RedirectTo is not null)
            {
                line.Append(Separator).Append("→ ").Append(SecretRedactor.DescribeRedirect(entry.Url, entry.RedirectTo));
            }

            if (entry.RetryAfterSeconds is { } retryAfter)
            {
                line.Append(Separator).Append("retry-after=").Append(retryAfter.ToString(CultureInfo.InvariantCulture));
            }

            AppendLine(line.ToString());
        }
        catch
        {
            // 寫不進 log 就算了，不能因此中斷輪詢。
        }
    }

    /// <summary>寫一行以 <c># </c> 開頭的註記，例如全域暫停與恢復。</summary>
    public void Note(string text)
    {
        if (_logsDirectory is null)
        {
            return;
        }

        try
        {
            AppendLine(NotePrefix + text);
        }
        catch
        {
            // 同 Write。
        }
    }

    /// <summary><c>core 4870/5000</c>；缺的部分以 <see cref="Missing"/> 補位。</summary>
    private static string QuotaText(ApiLogEntry entry)
    {
        var resource = string.IsNullOrWhiteSpace(entry.Resource) ? null : entry.Resource;
        var quota = entry.Remaining is null && entry.Limit is null
            ? null
            : $"{NumberOrMissing(entry.Remaining)}/{NumberOrMissing(entry.Limit)}";

        if (resource is null)
        {
            return quota ?? Missing;
        }

        return quota is null ? resource : $"{resource} {quota}";
    }

    private static string NumberOrMissing(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? Missing;

    /// <summary>整數毫秒（無條件捨去）；負值當成 0。</summary>
    private static string ElapsedText(TimeSpan elapsed)
    {
        var milliseconds = elapsed < TimeSpan.Zero ? 0 : (long)elapsed.TotalMilliseconds;
        return milliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    private void AppendLine(string line)
    {
        // 一個請求一行：外來文字裡的換行改成空白，遮罩在最後套用到整行。
        var text = _redactor.Redact(SingleLine(line)) + Environment.NewLine;
        var directory = _logsDirectory!;
        var path = Path.Combine(directory, FileName);

        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            RotateIfNeeded(directory, path);
            File.AppendAllText(path, text, Encoding.UTF8);
        }
    }

    /// <summary>
    /// 超過 ApiLogMaxBytes 時 api.log → api.1.log，舊的依序往後推，最舊的一份被覆蓋掉；
    /// 連同目前的檔案最多 1 + ApiLogKeepFiles 份。
    /// 最可能失敗的一步（api.log 被沒給 FILE_SHARE_DELETE 的程式開著）放在最前面：
    /// 失敗就放棄這次輪替、不動任何舊檔，照樣附加到 api.log，下一次寫入再試。
    /// </summary>
    private static void RotateIfNeeded(string directory, string path)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length <= Tuning.ApiLogMaxBytes)
        {
            return;
        }

        // 暫存檔若是上次程式在輪替中途被結束留下的，直接覆蓋（只是診斷用的舊紀錄）。
        var staging = Path.Combine(directory, RotatingFileName);
        if (!TryMove(path, staging))
        {
            return;
        }

        try
        {
            for (var index = Tuning.ApiLogKeepFiles - 1; index >= 1; index--)
            {
                var older = RotatedPath(directory, index);
                if (File.Exists(older))
                {
                    File.Move(older, RotatedPath(directory, index + 1), overwrite: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 推移到一半失敗：把暫存檔放回 api.log，不拿它覆蓋還沒推走的 api.1.log。
            TryMove(staging, path);
            return;
        }

        File.Move(staging, RotatedPath(directory, 1), overwrite: true);
    }

    private static bool TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RotatedPath(string directory, int index) =>
        Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, RotatedFileNameFormat, index));

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}

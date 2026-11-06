using System.Globalization;
using System.Text;

namespace IsItGreenYet.Core.Logging;

/// <summary>
/// error.log：未預期的例外。超過 ErrorLogMaxBytes 整份改名為 .old（同 SAM）。
/// 記錄失敗絕不能再丟例外出來，否則會變成無窮迴圈。
/// </summary>
public sealed class ErrorLog(string directory, SecretRedactor redactor, TimeProvider time)
{
    private const string FileName = "error.log";

    private const string OldSuffix = ".old";

    /// <summary>同 SAM 的時間格式（本地時間）。</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly string? _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    private readonly SecretRedactor _redactor = redactor;
    private readonly TimeProvider _time = time;
    private readonly Lock _gate = new();

    /// <summary>每種例外（型別＋訊息）最近一次回 true 的單調時間戳。</summary>
    private readonly Dictionary<string, long> _lastReported = new(StringComparer.Ordinal);

    /// <returns>這種例外（型別＋訊息）在 ErrorBannerDedupWindow 內第一次出現時回 true，供呼叫端決定要不要顯示橫幅。</returns>
    public bool Write(string context, Exception exception)
    {
        try
        {
            lock (_gate)
            {
                // 同一種例外照樣每次都寫進 log，只有橫幅要去重。
                var firstInWindow = IsFirstInWindow(exception);
                TryAppend(context, exception.ToString());
                return firstInWindow;
            }
        }
        catch
        {
            // 連型別或訊息都取不到的例外，不回報橫幅，避免每一輪都跳。
            return false;
        }
    }

    /// <summary>
    /// 同 <see cref="Write"/> 寫整筆例外（含堆疊），但不碰橫幅的去重紀錄：不跳橫幅的路徑用這個，才不會佔掉之後真的要跳橫幅的那一次。
    /// </summary>
    public void WriteWithoutBanner(string context, Exception exception)
    {
        try
        {
            lock (_gate)
            {
                TryAppend(context, exception.ToString());
            }
        }
        catch
        {
            // 連內容都取不到的例外就不寫了，記錄失敗不能再丟例外。
        }
    }

    public void WriteMessage(string context, string message)
    {
        lock (_gate)
        {
            TryAppend(context, message);
        }
    }

    /// <summary>
    /// 視窗從回 true 的那一刻起算，期間重複出現不延長；滿 ErrorBannerDedupWindow 後再出現就重新算第一次。
    /// 用單調時鐘，系統時間被調整也不受影響。
    /// </summary>
    private bool IsFirstInWindow(Exception exception)
    {
        var key = $"{exception.GetType().FullName}\n{exception.Message}";
        var now = _time.GetTimestamp();
        RemoveExpired(now);

        if (_lastReported.ContainsKey(key))
        {
            return false;
        }

        _lastReported[key] = now;
        return true;
    }

    /// <summary>過期的紀錄直接清掉，長時間執行也不會一直累積。</summary>
    private void RemoveExpired(long now)
    {
        var expired = _lastReported
            .Where(pair => _time.GetElapsedTime(pair.Value, now) >= Tuning.ErrorBannerDedupWindow)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expired)
        {
            _lastReported.Remove(key);
        }
    }

    /// <summary>呼叫端已持有 <see cref="_gate"/>。</summary>
    private void TryAppend(string context, string? body)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(_directory, FileName);
            Directory.CreateDirectory(_directory);

            // 檔案過大就整份換掉，避免長期執行把磁碟塞滿。
            if (File.Exists(path) && new FileInfo(path).Length > Tuning.ErrorLogMaxBytes)
            {
                File.Move(path, path + OldSuffix, overwrite: true);
            }

            var entry = new StringBuilder()
                .Append('[').Append(_time.GetLocalNow().ToString(TimestampFormat, CultureInfo.InvariantCulture)).Append("] ")
                .AppendLine(context)
                .AppendLine(body)
                .AppendLine()
                .ToString();

            // 例外訊息與堆疊可能帶 token 或簽名網址，整筆遮罩後才落地（spec §13.4）。
            File.AppendAllText(path, _redactor.Redact(entry), Encoding.UTF8);
        }
        catch
        {
            // 寫不進 log 就算了，不能因此中斷程式。
        }
    }
}

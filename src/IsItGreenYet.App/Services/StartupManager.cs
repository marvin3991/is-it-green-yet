using IsItGreenYet.App.Localization;
using Microsoft.Win32;

namespace IsItGreenYet.App.Services;

/// <summary>開機自啟在登錄檔裡的狀態。</summary>
internal enum StartupState
{
    /// <summary>沒有值，或值指向的 exe 已經不存在（開機也啟動不了）。</summary>
    Disabled,

    /// <summary>新格式：<c>"&lt;exe 路徑&gt;" --tray</c>。</summary>
    Enabled,

    /// <summary>沒有帶 <c>--tray</c> 的舊格式（同 SAM 的寫法）；視為已啟用，下次儲存設定時改寫成新格式（spec §3.3）。</summary>
    EnabledLegacyFormat,
}

/// <summary>
/// 開機自動啟動（同 SAM 的結構）。寫 HKCU 不需要系統管理員權限，
/// 也不會影響電腦上的其他使用者。值帶 <see cref="TrayArgument"/>，開機時只出現在系統匣、不開視窗（Q15）。
/// </summary>
internal static class StartupManager
{
    /// <summary>App 啟動時看到這個參數就不顯示主視窗（spec §3.3、Q15）。</summary>
    public const string TrayArgument = "--tray";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IsItGreenYet";

    private const char Quote = '"';

    /// <summary>UNC 路徑（包括 <c>\\?\</c> 開頭的）的開頭。</summary>
    private const string NetworkPathPrefix = @"\\";

    /// <summary>
    /// 以登錄檔的實際狀態為準，使用者可能在外面手動改過。舊格式也算已啟用；值指向的 exe 已經不存在時算沒啟用，
    /// 所以 exe 搬走之後設定頁不會顯示已勾選。這裡只讀不寫：勾選並儲存才會改寫成目前執行的 exe。
    /// </summary>
    public static bool IsEnabled() => GetState() != StartupState.Disabled;

    /// <summary>已啟用、但還是沒有參數的舊格式；設定頁儲存時看到這個要呼叫 <see cref="SetEnabled"/>(true) 改寫。</summary>
    public static bool NeedsRewrite() => GetState() == StartupState.EnabledLegacyFormat;

    public static StartupState GetState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return Evaluate(key?.GetValue(ValueName) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 讀不到登錄檔時保守地回報「沒啟用」，不要讓設定畫面開不起來（同 SAM）。
            return StartupState.Disabled;
        }
    }

    /// <exception cref="InvalidOperationException">寫入登錄檔失敗時丟出，訊息（目前的語言）可直接顯示給使用者。</exception>
    public static void SetEnabled(bool enabled)
    {
        var executablePath = Environment.ProcessPath;

        if (enabled && string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(Loc.Texts.StartupAutoStartNoPath);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException(Loc.Texts.StartupAutoStartKeyFailed);

            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(executablePath!), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(Loc.Format(Loc.Texts.StartupAutoStartFailed, ex.Message), ex);
        }
    }

    /// <summary>路徑可能含空白，加引號才不會被拆成多個參數（同 SAM）。</summary>
    internal static string BuildCommand(string executablePath) =>
        $"{Quote}{executablePath}{Quote} {TrayArgument}";

    /// <summary>登錄檔的值對應的狀態：指向的 exe 已經不存在就是沒啟用，其他照 <see cref="Parse"/>。</summary>
    internal static StartupState Evaluate(string? stored) =>
        PointsToMissingExecutable(stored) ? StartupState.Disabled : Parse(stored);

    /// <summary>
    /// 有引號、切得出完整路徑，而且那個檔案不存在。沒有引號的舊值切不準（路徑可能含空白），
    /// 相對路徑的結果又會跟著目前目錄變，這些都判斷不了，一律回 false，維持 <see cref="Parse"/> 的結果。
    /// 網路路徑也不判斷：這裡在 UI 執行緒上跑，連不上時 File.Exists 會卡住畫面（實測 21～42 秒）。
    /// </summary>
    internal static bool PointsToMissingExecutable(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var command = stored.Trim();
        if (command[0] != Quote)
        {
            return false;
        }

        var closing = command.IndexOf(Quote, 1);
        if (closing < 0)
        {
            return false;
        }

        var path = command[1..closing];
        return Path.IsPathFullyQualified(path)
            && !path.StartsWith(NetworkPathPrefix, StringComparison.Ordinal)
            && !File.Exists(path);
    }

    /// <summary>
    /// 有值就算已啟用；執行檔之後的參數裡有 <see cref="TrayArgument"/> 才是新格式。
    /// 沒有引號的舊值無法可靠地切出路徑（路徑可能含空白），整串當成參數來找，找不到就當舊格式改寫。
    /// </summary>
    internal static StartupState Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return StartupState.Disabled;
        }

        var command = stored.Trim();
        var arguments = command;
        if (command[0] == Quote)
        {
            var closing = command.IndexOf(Quote, 1);
            arguments = closing < 0 ? string.Empty : command[(closing + 1)..];
        }

        var hasTrayArgument = arguments
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);

        return hasTrayArgument ? StartupState.Enabled : StartupState.EnabledLegacyFormat;
    }
}

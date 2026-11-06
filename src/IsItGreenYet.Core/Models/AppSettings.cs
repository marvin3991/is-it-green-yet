using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Models;

/// <summary>認證方式；settings.json 只記這個，不存 token（spec §13.3）。JSON 值為 <c>ghCli</c>／<c>pat</c>。</summary>
public enum AuthMode
{
    GhCli,
    Pat,
}

/// <summary>主視窗的位置與大小（DIP）。</summary>
public sealed class WindowPlacement
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = Tuning.WindowDefaultWidth;

    public double Height { get; set; } = Tuning.WindowDefaultHeight;

    public bool Maximized { get; set; }
}

/// <summary>清單篩選偏好（不含搜尋文字，spec §10.4）。Statuses 存 DisplayState 的名稱。</summary>
public sealed class FilterPreferences
{
    public List<string> Repos { get; set; } = [];

    public List<string> Workflows { get; set; } = [];

    public List<string> Events { get; set; } = [];

    public List<string> Statuses { get; set; } = [];

    public List<string> Branches { get; set; } = [];

    public List<string> Actors { get; set; } = [];

    public bool AttentionOnly { get; set; }
}

/// <summary>
/// 使用者設定（spec §14.2）。所有欄位都有可用的預設值，設定檔缺漏或損毀時直接回落到預設。
/// JSON 用 camelCase；RefreshMode 的 JSON 值為 <c>auto</c>／<c>30s</c>／<c>1m</c>／<c>5m</c>／<c>paused</c>。
/// </summary>
public sealed class AppSettings
{
    public AuthMode AuthMode { get; set; } = AuthMode.GhCli;

    /// <summary>執行 gh 時移除 GH_TOKEN／GITHUB_TOKEN（Q32）。</summary>
    public bool IgnoreGhTokenEnv { get; set; }

    public bool IncludeForks { get; set; }

    public bool IncludeCollaboratorRepos { get; set; }

    public RefreshMode RefreshMode { get; set; } = RefreshMode.Auto;

    /// <summary>暫停前的模式；恢復時回到這裡。不可為 Paused。</summary>
    public RefreshMode PreviousRefreshMode { get; set; } = RefreshMode.Auto;

    public bool NotifyTrackedFailure { get; set; } = true;

    public bool NotifyMyRunSuccess { get; set; } = true;

    public bool NotifyWatchedRunDone { get; set; } = true;

    public int FailureToastSeconds { get; set; } = Tuning.FailureToastSeconds;

    public int SuccessToastSeconds { get; set; } = Tuning.SuccessToastSeconds;

    public int LogTailLines { get; set; } = Tuning.LogTailLines;

    public bool AlwaysOnTop { get; set; }

    public bool RunAtStartup { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 允許重跑與取消（第二階段 spec D1、§9.5）；預設關閉，沒有這個欄位（第一階段的設定檔）時也是關閉。
    /// 關閉時不送任何寫入請求，run 選單和第一階段相同。
    /// </summary>
    public bool AllowWriteActions { get; set; }

    /// <summary>定期檢查新版本；預設開啟，沒有這個欄位（之前的設定檔）時也是開啟。關閉時完全不送檢查請求。</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>上次完成的檢查（200、304、404）的 UTC 時間；null 表示從沒檢查過。由 Engine 透過 UpdateCheckRecord 交回，不在設定頁編輯。</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>已通知過的版本（SemVersion 的 ToString，不含 <c>v</c> 與 build metadata）；同一個版本只通知一次。由 Engine 透過 UpdateCheckRecord 交回。</summary>
    public string? LastNotifiedVersion { get; set; }

    /// <summary>
    /// 介面語言；JSON 值為 <c>zh-Hant</c>／<c>en</c>，沒有這個欄位（既有使用者）或不認得的值時是繁體中文。
    /// 第一次執行（沒有 settings.json）的預設依 Windows 顯示語言，由 SettingsService 決定。
    /// </summary>
    public AppLanguage Language { get; set; } = AppLanguages.Default;

    public WindowPlacement Window { get; set; } = new();

    public FilterPreferences Filters { get; set; } = new();

    /// <summary>把不合理的值修正回可用範圍，避免壞設定讓程式行為異常。</summary>
    public AppSettings Normalized()
    {
        if (!Enum.IsDefined(AuthMode))
        {
            AuthMode = AuthMode.GhCli;
        }

        if (!Enum.IsDefined(RefreshMode))
        {
            RefreshMode = RefreshMode.Auto;
        }

        Language = AppLanguages.Normalize(Language);

        // 恢復自動更新時回到這個模式；存成 Paused 就永遠恢復不了（spec §10.3、§14.2）。
        if (!Enum.IsDefined(PreviousRefreshMode) || PreviousRefreshMode == RefreshMode.Paused)
        {
            PreviousRefreshMode = RefreshMode.Auto;
        }

        FailureToastSeconds = InRangeOrDefault(
            FailureToastSeconds, Tuning.FailureToastMinSeconds, Tuning.FailureToastMaxSeconds, Tuning.FailureToastSeconds);
        SuccessToastSeconds = InRangeOrDefault(
            SuccessToastSeconds, Tuning.SuccessToastMinSeconds, Tuning.SuccessToastMaxSeconds, Tuning.SuccessToastSeconds);
        LogTailLines = InRangeOrDefault(
            LogTailLines, Tuning.LogTailLinesMin, Tuning.LogTailLinesMax, Tuning.LogTailLines);

        // JSON 寫成 null 時反序列化會留下 null，這裡補回預設物件。
        Window = NormalizeWindow(Window ?? new WindowPlacement());
        Filters = NormalizeFilters(Filters ?? new FilterPreferences());

        return this;
    }

    private static int InRangeOrDefault(int value, int min, int max, int fallback) =>
        value >= min && value <= max ? value : fallback;

    /// <summary>寬高只有下限（spec §14.2），各自判斷；位置不檢查螢幕範圍，那要由 App 依實際螢幕判斷（spec §10.1）。</summary>
    private static WindowPlacement NormalizeWindow(WindowPlacement window)
    {
        if (window.Left is { } left && !double.IsFinite(left))
        {
            window.Left = null;
        }

        if (window.Top is { } top && !double.IsFinite(top))
        {
            window.Top = null;
        }

        if (!double.IsFinite(window.Width) || window.Width < Tuning.WindowMinWidth)
        {
            window.Width = Tuning.WindowDefaultWidth;
        }

        if (!double.IsFinite(window.Height) || window.Height < Tuning.WindowMinHeight)
        {
            window.Height = Tuning.WindowDefaultHeight;
        }

        return window;
    }

    private static FilterPreferences NormalizeFilters(FilterPreferences filters)
    {
        filters.Repos = CleanValues(filters.Repos);
        filters.Workflows = CleanValues(filters.Workflows);
        filters.Events = CleanValues(filters.Events);
        filters.Statuses = CleanValues(filters.Statuses);
        filters.Branches = CleanValues(filters.Branches);
        filters.Actors = CleanValues(filters.Actors);
        return filters;
    }

    /// <summary>
    /// 去掉 null、空白與重複（Ordinal，分支與 workflow 名稱分大小寫），保留原本順序。
    /// 不修剪頭尾空白：值要和清單上的名稱逐字比對（spec §10.4）。
    /// 不認得的值（例如已不存在的 repo）照樣保留，讓你在篩選列取消勾選。
    /// </summary>
    private static List<string> CleanValues(List<string>? values) =>
        values is null
            ? []
            : values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
}

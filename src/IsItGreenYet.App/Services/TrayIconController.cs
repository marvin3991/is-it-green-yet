using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using IsItGreenYet.App.Localization;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Tracking;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 系統匣圖示、tooltip 與右鍵選單（spec §12）。建立失敗時（系統匣不可用）同 SAM 降級成一般視窗，不跳訊息、只寫 error.log：
/// <see cref="IsAvailable"/> 為 false，其他方法都不做事。
/// </summary>
/// <remarks>
/// 只能在 UI 執行緒建立與操作；唯一的例外是 <see cref="PostSnapshot"/>，它可以從 Engine 的背景執行緒呼叫。
/// 選單與左鍵的動作一律排回 WPF 的 Dispatcher 執行：例外會走 App 的 DispatcherUnhandledException（寫 error.log、跳對話框），
/// 不會變成 WinForms 自己的錯誤對話框；選單也已經先關閉，再開對話框才不會疊在選單上。
/// 選單與 tooltip 的文字用目前的語言；換語言時 App 呼叫 <see cref="RefreshLanguage"/>。
/// </remarks>
internal sealed class TrayIconController : IDisposable
{
    /// <summary>
    /// <c>NotifyIcon.Text</c> 的長度上限（UTF-16 字元數），超過時 setter 丟 <see cref="ArgumentOutOfRangeException"/>。
    /// 出處：dotnet/winforms release/10.0 的 NotifyIcon.cs <c>MaxTextSize = 127</c>（NOTIFYICONDATAW.szTip 128 個字元含結尾 0）；
    /// 2026-10-26 以反射讀 Microsoft.WindowsDesktop.App 10.0.13 的 System.Windows.Forms.dll 確認為 127，128 個字元會丟例外。
    /// </summary>
    internal const int MaxTooltipLength = 127;

    /// <summary>截短原因行時接在後面的省略號。</summary>
    private const string Ellipsis = "…";

    /// <summary>截短處去掉的結尾字元：原因行之間的分隔「 · 」（spec §7.4）。</summary>
    private static readonly char[] TrailingSeparators = [' ', '·'];

    /// <summary>
    /// 加入系統匣時的 tooltip：只有名稱（spec §12、§7.4 的第一行），收到第一份快照才換成狀態文字。
    /// Windows 11 會把加入當下的 tooltip 存成這個圖示的名稱（HKCU\Control Panel\NotifyIconSettings 的 InitialTooltip，
    /// 「設定 > 個人化 > 工作列 > 其他系統匣圖示」顯示的就是它；2026-10-26 實測），狀態文字不能留在那裡。
    /// </summary>
    private const string InitialTooltip = "IsItGreenYet";

    /// <summary>右鍵選單的字型清單取自主畫面的同一個資源（Themes/Design.xaml），中文用微軟正黑體、其他用 Consolas。</summary>
    private const string FontFamilyKey = "FontFamily.Default";

    private readonly Forms.NotifyIcon? _notifyIcon;
    private readonly MixedFontMenuRenderer? _menuRenderer;

    // 右鍵選單的項目（spec §12）：文字依目前的語言，換語言時重設（RefreshLanguage）。
    private readonly Forms.ToolStripMenuItem? _openItem;
    private readonly Forms.ToolStripMenuItem? _refreshItem;
    private readonly Forms.ToolStripMenuItem? _pauseItem;
    private readonly Forms.ToolStripMenuItem? _repoManagerItem;
    private readonly Forms.ToolStripMenuItem? _settingsItem;
    private readonly Forms.ToolStripMenuItem? _openFolderItem;
    private readonly Forms.ToolStripMenuItem? _exitItem;
    private readonly Func<bool> _isPaused;
    private readonly Action<string, Exception> _reportError;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly TrayIconPalette? _palette;

    private Drawing.Icon? _currentIcon;
    private (OverallState State, bool Badge)? _shownIcon;

    /// <summary>最近一次套用的快照：換語言時用它重組 tooltip。</summary>
    private MonitorSnapshot? _appliedSnapshot;

    /// <summary>背景執行緒送來、還沒套用的最新快照。</summary>
    private MonitorSnapshot? _incoming;

    /// <summary>已經排了一個「套用快照」到 UI 執行緒（0／1）；連續好幾份快照只套用最新的一份。</summary>
    private int _applyScheduled;

    private bool _disposed;

    /// <param name="isPaused">目前是不是暫停自動更新（settings.json 的 refreshMode）；開選單時決定顯示「暫停」或「恢復」。</param>
    /// <param name="findResource">取色用（<see cref="TrayIconPalette.FromResources"/>）。</param>
    /// <param name="reportError">套用快照失敗時呼叫（寫 error.log、必要時顯示橫幅）；快照會一直來，不能每次都跳對話框。</param>
    /// <param name="errorLog">系統匣建立失敗（降級）時寫一筆紀錄。</param>
    public TrayIconController(
        Func<bool> isPaused,
        Func<object, object?> findResource,
        Action<string, Exception> reportError,
        ErrorLog errorLog,
        Dispatcher dispatcher,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(findResource);
        ArgumentNullException.ThrowIfNull(errorLog);
        _isPaused = isPaused ?? throw new ArgumentNullException(nameof(isPaused));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));

        try
        {
            _palette = TrayIconPalette.FromResources(findResource);
            var menuTheme = TrayMenuTheme.FromResources(findResource);
            _notifyIcon = new Forms.NotifyIcon();

            var menu = new Forms.ContextMenuStrip();

            // 字型同主畫面（FontFamily.Default）；字級維持選單原本的系統選單字級。清單裡的字型都用不到時維持系統字型，外觀照樣是深色。
            TrayMenuRenderer menuRenderer = new(menuTheme);
            if (findResource(FontFamilyKey) is System.Windows.Media.FontFamily family
                && MixedFontMenuRenderer.TryCreate(family, menu.Font.SizeInPoints, menuTheme) is { } mixedFont)
            {
                _menuRenderer = mixedFont;
                menuRenderer = mixedFont;
                menu.Font = mixedFont.PrimaryFont;
            }

            var text = Loc.Texts;
            _openItem = new Forms.ToolStripMenuItem(text.TrayOpen, null, (_, _) => Raise(OpenRequested));
            _refreshItem = new Forms.ToolStripMenuItem(text.TrayRefresh, null, (_, _) => Raise(RefreshRequested));
            _pauseItem = new Forms.ToolStripMenuItem(text.TrayPause, null, (_, _) => Raise(PauseToggleRequested));
            _repoManagerItem = new Forms.ToolStripMenuItem(text.CommonRepoManager, null, (_, _) => Raise(RepoManagerRequested));
            _settingsItem = new Forms.ToolStripMenuItem(text.CommonSettings, null, (_, _) => Raise(SettingsRequested));
            _openFolderItem = new Forms.ToolStripMenuItem(text.CommonOpenFolder, null, (_, _) => Raise(OpenFolderRequested));
            _exitItem = new Forms.ToolStripMenuItem(text.TrayExit, null, (_, _) => Raise(ExitRequested));
            menu.Items.Add(_openItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_refreshItem);
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_repoManagerItem);
            menu.Items.Add(_settingsItem);
            menu.Items.Add(_openFolderItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(_exitItem);
            menu.Opening += OnMenuOpening;

            // 深色、Windows 11 風格的外觀（項目高度依字型算，所以在設好字型、加好項目之後）。
            menuRenderer.Apply(menu);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.MouseClick += OnMouseClick;

            // 先有圖示與 tooltip 再顯示：NotifyIcon 沒有圖示時不會加進系統匣。還沒有快照時是無資料、沒有角標（spec §7.3 第 4 列）。
            ApplyIcon(OverallState.NoData, badge: false);
            ApplyTooltip(InitialTooltip);
            _notifyIcon.Visible = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
        {
            // 系統匣不可用（例如沒有 Explorer）：降級為一般視窗，關閉即結束（spec §12、§15，同 SAM）。
            errorLog.Write("建立系統匣圖示失敗，改成一般視窗", ex);
            ReleaseNotifyIcon();
            _menuRenderer?.Dispose();
            _menuRenderer = null;
            _currentIcon?.Dispose();
            _currentIcon = null;
            _notifyIcon = null;
            _openItem = _refreshItem = _pauseItem = _repoManagerItem = _settingsItem = _openFolderItem = _exitItem = null;
            IsAvailable = false;
        }
    }

    /// <summary>系統匣圖示建立成功。WinForms 不檢查 Shell_NotifyIcon 的回傳值，「沒丟例外但沒出現」偵測不到（spec §12）。</summary>
    public bool IsAvailable { get; } = true;

    public event Action? OpenRequested;

    public event Action? RefreshRequested;

    /// <summary>「暫停自動更新／恢復自動更新」：由主視窗依 previousRefreshMode 切換（spec §10.3）。</summary>
    public event Action? PauseToggleRequested;

    public event Action? RepoManagerRequested;

    public event Action? SettingsRequested;

    public event Action? OpenFolderRequested;

    public event Action? ExitRequested;

    /// <summary>可以從任何執行緒呼叫（Engine 的 SnapshotPublished）：只記下最新的快照，排一次到 UI 執行緒套用。</summary>
    public void PostSnapshot(MonitorSnapshot snapshot)
    {
        if (!IsAvailable || Volatile.Read(ref _disposed))
        {
            return;
        }

        Interlocked.Exchange(ref _incoming, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            // Dispatcher 已經關閉（程式正在結束）時不會丟例外，操作直接被放棄。
            _dispatcher.InvokeAsync(ApplyIncomingSnapshot, DispatcherPriority.Background);
        }
    }

    /// <summary>依快照更新圖示與 tooltip（UI 執行緒）。圖示只在整體狀態或角標改變時重畫。</summary>
    internal void ApplySnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_notifyIcon is null || _disposed)
        {
            return;
        }

        _appliedSnapshot = snapshot;
        var summary = snapshot.Summary;
        ApplyIcon(summary.State, badge: summary.Issues != ConnectionIssue.None);

        DateTimeOffset? local = snapshot.LastUpdatedUtc is { } updated
            ? TimeZoneInfo.ConvertTime(updated, _time.LocalTimeZone)
            : null;
        ApplyTooltip(FitTooltip(summary, local, snapshot.TrayIssueLine));
    }

    /// <summary>
    /// 換語言（UI 執行緒）：選單文字換成新的語言；tooltip 用最近一次的快照重組（原因行由 Engine 產生，下一份快照就是新的語言）。
    /// </summary>
    internal void RefreshLanguage()
    {
        if (_notifyIcon is null || _disposed)
        {
            return;
        }

        var text = Loc.Texts;
        _openItem!.Text = text.TrayOpen;
        _refreshItem!.Text = text.TrayRefresh;
        _pauseItem!.Text = _isPaused() ? text.TrayResume : text.TrayPause;
        _repoManagerItem!.Text = text.CommonRepoManager;
        _settingsItem!.Text = text.CommonSettings;
        _openFolderItem!.Text = text.CommonOpenFolder;
        _exitItem!.Text = text.TrayExit;

        if (_appliedSnapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
        }
    }

    /// <summary>
    /// 系統匣 tooltip（spec §7.4）：<see cref="StatusAggregator.TrayTooltip"/> 的內容超過 <see cref="MaxTooltipLength"/> 時，
    /// 先截短原因那一行（尾端加「…」）；連前兩行都放不下（不會發生，仍防著）才截整段。
    /// </summary>
    internal static string FitTooltip(StatusSummary summary, DateTimeOffset? lastUpdatedLocal, string? issueLine)
    {
        var full = StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, issueLine);
        if (full.Length <= MaxTooltipLength)
        {
            return full;
        }

        if (!string.IsNullOrWhiteSpace(issueLine))
        {
            // 原因行只剩省略號時的總長度，扣掉後就是原因行還能保留的字數。
            var keep = MaxTooltipLength - StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, Ellipsis).Length;
            var kept = Truncate(issueLine.Trim(), keep).TrimEnd(TrailingSeparators);
            if (kept.Length > 0)
            {
                return StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, kept + Ellipsis);
            }
        }

        var withoutIssue = StatusAggregator.TrayTooltip(summary, lastUpdatedLocal, issueLine: null);
        return withoutIssue.Length <= MaxTooltipLength
            ? withoutIssue
            : Truncate(withoutIssue, MaxTooltipLength - Ellipsis.Length) + Ellipsis;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        ReleaseNotifyIcon();

        // 選單（ReleaseNotifyIcon 裡釋放）不再用到這些字型之後才釋放。
        _menuRenderer?.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    /// <summary>保留前 <paramref name="length"/> 個 UTF-16 字元；不把代理字組（例如 emoji）切成兩半。</summary>
    private static string Truncate(string text, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= length)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end];
    }

    private void ApplyIncomingSnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        if (Interlocked.Exchange(ref _incoming, null) is not { } snapshot)
        {
            return;
        }

        try
        {
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _reportError("更新系統匣圖示", ex);
        }
    }

    /// <summary>狀態或角標改變時才重畫；新圖示設好之後才釋放舊的（同 SAM），舊的 HICON 不會留著。</summary>
    private void ApplyIcon(OverallState state, bool badge)
    {
        if (_notifyIcon is null || _palette is null || _shownIcon == (state, badge))
        {
            return;
        }

        var previous = _currentIcon;
        _currentIcon = TrayIconFactory.Create(state, badge, _palette);
        _notifyIcon.Icon = _currentIcon;
        _shownIcon = (state, badge);
        previous?.Dispose();
    }

    private void ApplyTooltip(string text)
    {
        // 內容沒變就不設：每次設定都會呼叫 Shell_NotifyIcon(NIM_MODIFY)。
        if (_notifyIcon is not null && !string.Equals(_notifyIcon.Text, text, StringComparison.Ordinal))
        {
            _notifyIcon.Text = text;
        }
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_pauseItem is not null)
        {
            _pauseItem.Text = _isPaused() ? Loc.Texts.TrayResume : Loc.Texts.TrayPause;
        }
    }

    /// <summary>左鍵開主視窗（spec §12）；右鍵由 NotifyIcon 自己開 ContextMenuStrip。</summary>
    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            Raise(OpenRequested);
        }
    }

    private void Raise(Action? handler)
    {
        if (handler is not null && !_disposed)
        {
            _dispatcher.BeginInvoke(handler);
        }
    }

    private void ReleaseNotifyIcon()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            _notifyIcon.Visible = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            // 圖示本來就沒加上去（建立到一半失敗）；照樣釋放。
        }

        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}

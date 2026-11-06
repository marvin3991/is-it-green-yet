using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using IsItGreenYet.App.Localization;
using IsItGreenYet.App.Services;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Auth;
using IsItGreenYet.Core.Display;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.GitHub;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.App.ViewModels;

/// <summary>
/// 設定頁用到的外部資源與動作（spec §11.2）。App 由 <see cref="WindowNavigator"/> 接上真正的實作；
/// 驗證畫面時可以換成假的（記憶體裡的認證管理員、假的檢查連線結果、不碰登錄檔的開機自啟）。
/// </summary>
/// <param name="Settings">目前的設定；按「儲存」時直接改這個物件（同 SAM），存檔失敗就改回原值。</param>
/// <param name="SaveSettings">寫 settings.json；失敗丟 IOException／UnauthorizedAccessException。</param>
/// <param name="VerifyToken">用候選的 PAT 單獨送一次 E1（GET /user），不改變 Engine 正在用的 token。</param>
/// <param name="ReadAccount">最近一次 E1 驗證成功的帳號（cache.db 的 account）；讀不到回 null。</param>
internal sealed record SettingsDependencies(
    AppSettings Settings,
    Action SaveSettings,
    EngineFeed Engine,
    Func<CancellationToken, Task<ConnectionCheckResult>> CheckConnection,
    Func<string, CancellationToken, Task<ApiResult<AccountInfo>>> VerifyToken,
    ICredentialStore Credentials,
    Func<bool> IsStartupEnabled,
    Func<bool> StartupNeedsRewrite,
    Action<bool> SetStartupEnabled,
    Func<AccountInfo?> ReadAccount,
    ErrorLog ErrorLog);

/// <summary>
/// 設定頁（spec §11.2）。按「儲存」才生效，「取消」或 Esc 保持原樣（同 SAM）：所有變更（包含 PAT 的儲存與清除）都在儲存時才套用。
/// 只在 UI 執行緒使用。PAT 的明文不放在這裡的欄位：只在 <see cref="SaveAsync"/> 執行期間以參數傳入，用完就丟（spec §13.3、§13.4）。
/// 畫面文字用目前的語言（spec §11.2 的項目名稱照原文；狀態與錯誤訊息 spec 沒有給文字，用最短的說明，SAM 的句型）。
/// 「語言」也是按「儲存」才生效：存好之後由 WindowNavigator 套用到整個 App（主視窗的 ApplySavedSettings）。
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    // ── token 的格式（同 Core 的 TokenText 規則）───────────────────────
    // RFC 9110 §5.5：標頭值的可見字元 VCHAR（%x21–7E）。夾著空白、換行或非 ASCII 字元的值放進標頭，
    // HttpClient 會丟出帶原文的例外，所以送出前先擋下來。
    private const char FirstVisible = '!';
    private const char LastVisible = '~';

    /// <summary>
    /// Windows 認證管理員 blob 的上限 CRED_MAX_CREDENTIAL_BLOB_SIZE（wincred.h，2,560 bytes，同 WindowsCredentialStore）。
    /// blob 用 UTF-16LE 存，存不進去的 PAT 不必先送去 GitHub 驗證。
    /// </summary>
    private const int CredentialBlobMaxBytes = 5 * 512;

    /// <summary>連線層錯誤沒有狀態碼（GitHubClient 記 0）。</summary>
    private const int NoResponse = 0;

    private readonly SettingsDependencies _deps;

    /// <summary>這個對話框裡被 GitHub 回 401 的 PAT（SHA-256，只在記憶體）：同一把不再送，避免連續送出無效憑證（spec §13.2）。</summary>
    private readonly HashSet<string> _rejectedPats = new(StringComparer.Ordinal);

    private AuthMode _authMode;
    private bool _ignoreGhTokenEnv;
    private bool _includeForks;
    private bool _includeCollaboratorRepos;
    private bool _notifyTrackedFailure;
    private bool _notifyMyRunSuccess;
    private bool _notifyWatchedRunDone;
    private string _failureToastSecondsText;
    private string _successToastSecondsText;
    private string _logTailLinesText;
    private bool _allowWriteActions;
    private bool _runAtStartup;
    private bool _minimizeToTray;
    private bool _alwaysOnTop;
    private bool _checkForUpdates;
    private AppLanguage _language;

    private AuthStatus _auth = AuthStatus.Unknown;
    private TokenSource _source = TokenSource.None;
    private string? _login;
    private string _accountText = Loc.Texts.SettingsVerifying;
    private string _tokenSourceText = Loc.Texts.SettingsSourceNone;

    private bool _isChecking;
    private string _connectionText = string.Empty;
    private string _scopesText = string.Empty;

    private bool _clearPatRequested;
    private bool _isBusy;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;

    public SettingsViewModel(SettingsDependencies dependencies)
    {
        _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

        var settings = _deps.Settings;
        _authMode = settings.AuthMode;
        _ignoreGhTokenEnv = settings.IgnoreGhTokenEnv;
        _includeForks = settings.IncludeForks;
        _includeCollaboratorRepos = settings.IncludeCollaboratorRepos;
        _notifyTrackedFailure = settings.NotifyTrackedFailure;
        _notifyMyRunSuccess = settings.NotifyMyRunSuccess;
        _notifyWatchedRunDone = settings.NotifyWatchedRunDone;
        _failureToastSecondsText = settings.FailureToastSeconds.ToString(CultureInfo.InvariantCulture);
        _successToastSecondsText = settings.SuccessToastSeconds.ToString(CultureInfo.InvariantCulture);
        _logTailLinesText = settings.LogTailLines.ToString(CultureInfo.InvariantCulture);
        _allowWriteActions = settings.AllowWriteActions;
        _minimizeToTray = settings.MinimizeToTray;
        _alwaysOnTop = settings.AlwaysOnTop;
        _checkForUpdates = settings.CheckForUpdates;
        _language = AppLanguages.Normalize(settings.Language);

        // 以登錄檔的實際狀態為準，使用者可能在外面手動改過（同 SAM）。
        _runAtStartup = _deps.IsStartupEnabled();

        ReloadAccount();
        if (_deps.Engine.LatestSnapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
        }
        else
        {
            UpdateAccount();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── 帳號 ──────────────────────────────────────────────────────────

    public bool IsGhCliMode
    {
        get => _authMode == AuthMode.GhCli;
        set
        {
            if (value)
            {
                SetAuthMode(AuthMode.GhCli);
            }
        }
    }

    public bool IsPatMode
    {
        get => _authMode == AuthMode.Pat;
        set
        {
            if (value)
            {
                SetAuthMode(AuthMode.Pat);
            }
        }
    }

    /// <summary>目前登入的 login（spec §11.2）；未登入或還在驗證時寫狀態。</summary>
    public string AccountText { get => _accountText; private set => SetField(ref _accountText, value); }

    /// <summary>token 來源：gh 儲存的憑證或環境變數（spec §11.2、§13.1），或 PAT。</summary>
    public string TokenSourceText { get => _tokenSourceText; private set => SetField(ref _tokenSourceText, value); }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetField(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CanCheckConnection));
            }
        }
    }

    public bool CanCheckConnection => !_isChecking;

    /// <summary>「檢查連線」的結果：看得到幾個 repo、其中私有幾個（spec §11.2），或失敗原因。</summary>
    public string ConnectionText
    {
        get => _connectionText;
        private set
        {
            if (SetField(ref _connectionText, value))
            {
                OnPropertyChanged(nameof(HasConnectionText));
            }
        }
    }

    public bool HasConnectionText => _connectionText.Length > 0;

    /// <summary>X-OAuth-Scopes 判讀結果（spec §4.4、§11.2）；E1 沒有成功時留白。</summary>
    public string ScopesText
    {
        get => _scopesText;
        private set
        {
            if (SetField(ref _scopesText, value))
            {
                OnPropertyChanged(nameof(HasScopesText));
            }
        }
    }

    public bool HasScopesText => _scopesText.Length > 0;

    /// <summary>按了「清除」：儲存時從 Windows 認證管理員刪除 PAT。</summary>
    public bool ClearPatRequested => _clearPatRequested;

    /// <summary>PAT 模式下刪掉 PAT 就沒有 token 可用，所以要先改回 gh CLI 才刪。</summary>
    public string PatHint => !_clearPatRequested ? Loc.Texts.SettingsPatHintDefault
        : _authMode == AuthMode.Pat ? Loc.Texts.SettingsPatHintClearNeedsGhCli : Loc.Texts.SettingsPatHintClear;

    public bool IgnoreGhTokenEnv { get => _ignoreGhTokenEnv; set => SetField(ref _ignoreGhTokenEnv, value); }

    // ── 監看範圍 ───────────────────────────────────────────────────────

    public bool IncludeForks { get => _includeForks; set => SetField(ref _includeForks, value); }

    public bool IncludeCollaboratorRepos { get => _includeCollaboratorRepos; set => SetField(ref _includeCollaboratorRepos, value); }

    // ── 通知 ───────────────────────────────────────────────────────────

    public bool NotifyTrackedFailure { get => _notifyTrackedFailure; set => SetField(ref _notifyTrackedFailure, value); }

    public bool NotifyMyRunSuccess { get => _notifyMyRunSuccess; set => SetField(ref _notifyMyRunSuccess, value); }

    public bool NotifyWatchedRunDone { get => _notifyWatchedRunDone; set => SetField(ref _notifyWatchedRunDone, value); }

    public string FailureToastSecondsText { get => _failureToastSecondsText; set => SetField(ref _failureToastSecondsText, value ?? string.Empty); }

    public string SuccessToastSecondsText { get => _successToastSecondsText; set => SetField(ref _successToastSecondsText, value ?? string.Empty); }

    public string FailureToastLabel => RangeLabel(Loc.Texts.SettingsFailureToastName, Tuning.FailureToastMinSeconds, Tuning.FailureToastMaxSeconds);

    public string SuccessToastLabel => RangeLabel(Loc.Texts.SettingsSuccessToastName, Tuning.SuccessToastMinSeconds, Tuning.SuccessToastMaxSeconds);

    // ── 失敗 log ───────────────────────────────────────────────────────

    public string LogTailLinesText { get => _logTailLinesText; set => SetField(ref _logTailLinesText, value ?? string.Empty); }

    /// <summary>spec §11.2 原文「每個 job 保留的行數（20–5000）」，範圍取自 Tuning。</summary>
    public string LogTailLinesLabel => RangeLabel(Loc.Texts.SettingsLogTailLinesName, Tuning.LogTailLinesMin, Tuning.LogTailLinesMax);

    // ── 寫入操作（第二階段 spec §9.5）────────────────────────────────────

    /// <summary>允許在 App 內重跑與取消 run；儲存後經 SettingsChanged 交給 Engine，run 選單看快照的 WritesEnabled。</summary>
    public bool AllowWriteActions { get => _allowWriteActions; set => SetField(ref _allowWriteActions, value); }

    // ── 一般 ───────────────────────────────────────────────────────────

    public bool RunAtStartup { get => _runAtStartup; set => SetField(ref _runAtStartup, value); }

    public bool MinimizeToTray { get => _minimizeToTray; set => SetField(ref _minimizeToTray, value); }

    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetField(ref _alwaysOnTop, value); }

    /// <summary>定期檢查新版本；關閉後完全不送檢查請求。Engine 直接讀設定物件，按「儲存」之後就生效。</summary>
    public bool CheckForUpdates { get => _checkForUpdates; set => SetField(ref _checkForUpdates, value); }

    /// <summary>間隔天數取自 Tuning。</summary>
    public string CheckForUpdatesLabel => Loc.Format(Loc.Texts.SettingsCheckForUpdates, Tuning.UpdateCheckIntervalDays);

    // ── 語言 ───────────────────────────────────────────────────────────

    public bool IsEnglish
    {
        get => _language == AppLanguage.English;
        set
        {
            if (value)
            {
                SetLanguage(AppLanguage.English);
            }
        }
    }

    public bool IsTraditionalChinese
    {
        get => _language == AppLanguage.TraditionalChinese;
        set
        {
            if (value)
            {
                SetLanguage(AppLanguage.TraditionalChinese);
            }
        }
    }

    // ── 最下方：專案連結與版本 ─────────────────────────────────────────

    /// <summary>連結文字用完整網址：不寫任何會暗示 GitHub 背書的字眼。</summary>
    public string ProjectRepoText => WebLinks.ProjectRepo.AbsoluteUri;

    public string VersionLabel => Loc.Format(Loc.Texts.SettingsVersionLabel, AppHost.DisplayVersion);

    // ── 儲存 ───────────────────────────────────────────────────────────

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool CanSave => !_isBusy;

    /// <summary>驗證 PAT 期間（要等網路）設定內容停用：儲存用的是按下「儲存」那一刻的值，這段期間改的欄位不會生效，所以不讓它改。</summary>
    public bool CanEdit => !_isBusy;

    /// <summary>儲存進行中的說明（例如正在驗證 PAT）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetField(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => _statusText.Length > 0;

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetField(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText.Length > 0;

    /// <summary>Engine 發布的新快照（已在 UI 執行緒上）：更新登入狀態與 token 來源。</summary>
    public void ApplySnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var becameOk = snapshot.Auth == AuthStatus.Ok && _auth != AuthStatus.Ok;
        _auth = snapshot.Auth;
        _source = snapshot.TokenSource;
        if (becameOk)
        {
            // 剛驗證成功：Engine 已經把帳號寫進 cache.db（可能換了帳號）。
            ReloadAccount();
        }

        UpdateAccount();
    }

    public void ShowError(string message) => ErrorText = message ?? string.Empty;

    /// <summary>瀏覽器開不起來（同主視窗的 OpenLink）：寫 error.log，設定頁底部顯示錯誤。</summary>
    public void ReportLinkFailed(Exception ex)
    {
        _deps.ErrorLog.Write("開啟連結失敗", ex);
        ErrorText = Loc.Texts.MessageBrowserFailed;
    }

    /// <summary>
    /// 換語言之後（設定頁開著時才會用到，例如另一個入口換了語言）：程式算出來的文字重新讀。
    /// 檢查連線與錯誤的訊息是當時的結果，維持原本的語言。
    /// </summary>
    public void RefreshLanguage()
    {
        UpdateAccount();
        OnPropertyChanged(nameof(PatHint));
        OnPropertyChanged(nameof(FailureToastLabel));
        OnPropertyChanged(nameof(SuccessToastLabel));
        OnPropertyChanged(nameof(LogTailLinesLabel));
        OnPropertyChanged(nameof(CheckForUpdatesLabel));
        OnPropertyChanged(nameof(VersionLabel));
    }

    /// <summary>「清除」：儲存時才刪除（「取消」保持原樣，spec §11.2）。</summary>
    public void RequestClearPat()
    {
        if (_clearPatRequested)
        {
            return;
        }

        _clearPatRequested = true;
        OnPropertyChanged(nameof(ClearPatRequested));
        OnPropertyChanged(nameof(PatHint));
    }

    /// <summary>「清除」之後又貼上新的 PAT：改成儲存新的 PAT（取代已存的），不再刪除。</summary>
    public void CancelClearPat()
    {
        if (!_clearPatRequested)
        {
            return;
        }

        _clearPatRequested = false;
        OnPropertyChanged(nameof(ClearPatRequested));
        OnPropertyChanged(nameof(PatHint));
    }

    /// <summary>
    /// 「檢查連線」（spec §11.2）：Engine 用目前的 token 送 E1 與 repo 清單，回報看得到幾個 repo、其中私有幾個與 scopes 判讀。
    /// 檢查的是目前生效的認證方式；還沒儲存的變更不影響。
    /// </summary>
    public async Task CheckConnectionAsync(CancellationToken ct)
    {
        if (_isChecking)
        {
            return;
        }

        IsChecking = true;
        ConnectionText = Loc.Texts.SettingsChecking;
        ScopesText = string.Empty;
        try
        {
            var result = await _deps.CheckConnection(ct);
            ConnectionText = result.Message;

            // 有 login 代表 E1 成功；repo 清單失敗時照樣顯示 scopes 判讀。
            // Core 在 X-OAuth-Scopes 含 repo、或沒有這個 header（fine-grained PAT）時都不給提示（spec §4.4）。
            if (result.Login is { } login)
            {
                ScopesText = Loc.Format(Loc.Texts.SettingsScopes, result.ScopesNote ?? Loc.Texts.SettingsNoScopeIssue);
                _login = login;
                UpdateAccount();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 視窗已經關閉，或程式正在結束（Engine 已經停止）。
            ConnectionText = string.Empty;
        }
        catch (Exception ex)
        {
            _deps.ErrorLog.Write("檢查連線", ex);
            ConnectionText = Loc.Texts.CommonUnexpectedError;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// 「儲存」（spec §11.2）。依序：檢查數值 → PAT（有貼上新的就先用 E1 驗證，失敗就不儲存）→ 開機自啟 →
    /// settings.json → Windows 認證管理員 → 通知 Engine。任何一步失敗就停下並顯示錯誤，之後的步驟都不做，
    /// 已經套用的步驟改回原狀（見 <see cref="Commit"/>）。
    /// </summary>
    /// <param name="patInput">PasswordBox 的內容；呼叫端讀完就清空 PasswordBox，這裡用完不保留。gh CLI 模式下忽略。</param>
    /// <returns>全部儲存成功時回 true，視窗可以關閉。</returns>
    public async Task<bool> SaveAsync(string? patInput, CancellationToken ct)
    {
        if (_isBusy)
        {
            return false;
        }

        ErrorText = string.Empty;
        var text = Loc.Texts;
        if (!TryParseInRange(_failureToastSecondsText, text.SettingsFailureToastName, Tuning.FailureToastMinSeconds, Tuning.FailureToastMaxSeconds, out var failureSeconds)
            || !TryParseInRange(_successToastSecondsText, text.SettingsSuccessToastName, Tuning.SuccessToastMinSeconds, Tuning.SuccessToastMaxSeconds, out var successSeconds)
            || !TryParseInRange(_logTailLinesText, text.SettingsLogTailLinesName, Tuning.LogTailLinesMin, Tuning.LogTailLinesMax, out var logTailLines))
        {
            return false;
        }

        // 第一個 await 之前一次讀好所有欄位：驗證 PAT 要等網路，之後的步驟一律用按下「儲存」那一刻的值。
        var updated = new SettingsValues(
            _authMode, _ignoreGhTokenEnv, _includeForks, _includeCollaboratorRepos,
            _notifyTrackedFailure, _notifyMyRunSuccess, _notifyWatchedRunDone,
            failureSeconds, successSeconds, logTailLines, _allowWriteActions,
            _alwaysOnTop, _runAtStartup, _minimizeToTray, _language, _checkForUpdates);
        var clearPat = _clearPatRequested;

        var candidate = updated.AuthMode == AuthMode.Pat ? patInput?.Trim() : null;
        if (string.IsNullOrEmpty(candidate))
        {
            candidate = null;
        }

        if (candidate is not null && !CheckPatFormat(candidate))
        {
            return false;
        }

        // 選 PAT 卻沒有可用的 PAT（沒貼上新的，而且沒有存好的或要清除）：存下去只會變成未登入，先擋下來。
        if (updated.AuthMode == AuthMode.Pat && candidate is null)
        {
            if (clearPat)
            {
                ErrorText = text.SettingsPatClearNeedsGhCli;
                return false;
            }

            var stored = HasStoredPat();
            if (stored is not true)
            {
                if (stored is false)
                {
                    ErrorText = text.SettingsPatRequired;
                }

                return false;
            }
        }

        IsBusy = true;
        try
        {
            if (candidate is not null && !await VerifyPatAsync(candidate, ct))
            {
                return false;
            }

            return !ct.IsCancellationRequested && Commit(updated, candidate, clearPat);
        }
        finally
        {
            StatusText = string.Empty;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 驗證通過之後套用：開機自啟 → settings.json → Windows 認證管理員 → 通知 Engine。
    /// 任何一步失敗就把前面已經套用的改回去，畫面顯示錯誤，「取消」關閉時一切維持原樣（spec §11.2）。
    /// 認證管理員排在設定檔之後：刪除 PAT 無法復原，所以設定檔存好了才刪；寫入或刪除失敗時設定檔改回原值，
    /// 不改變目前的認證方式（spec §15）。
    /// </summary>
    private bool Commit(SettingsValues updated, string? candidate, bool clearPat)
    {
        if (!ApplyStartup(updated.RunAtStartup, out var startupBefore))
        {
            return false;
        }

        var settings = _deps.Settings;
        var previous = SettingsValues.From(settings);
        updated.ApplyTo(settings);
        try
        {
            _deps.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 沒存進去就改回原值：Engine 取 token 時讀的也是這個物件（AppHost 的 SettingsTokenProvider）。
            previous.ApplyTo(settings);
            RevertStartup(startupBefore);
            _deps.ErrorLog.Write("儲存設定失敗", ex);
            ErrorText = Loc.Format(Loc.Texts.SettingsSaveFailed, ex.Message);
            return false;
        }

        if (!ApplyCredential(candidate, clearPat))
        {
            previous.ApplyTo(settings);
            RestoreSettingsFile();
            RevertStartup(startupBefore);
            return false;
        }

        // 認證方式或「忽略環境變數」改變時，SettingsChanged 會讓 Engine 重新取得 token 並用 E1 驗證；
        // 只換了 PAT（認證方式本來就是 PAT）時另外送「重新檢查」，Engine 才會改用新的 PAT。
        // 換了語言時，SettingsChanged 讓 Engine 用新的語言重建橫幅與問題標示；App 這邊由 WindowNavigator 在視窗關閉後套用。
        var patChanged = candidate is not null || clearPat;
        var authChanged = previous.AuthMode != updated.AuthMode || previous.IgnoreGhTokenEnv != updated.IgnoreGhTokenEnv;
        _deps.Engine.Post(new EngineCommand.SettingsChanged(settings));
        if (patChanged && !authChanged && updated.AuthMode == AuthMode.Pat)
        {
            _deps.Engine.Post(new EngineCommand.RecheckAuth());
        }

        return true;
    }

    private void SetAuthMode(AuthMode mode)
    {
        if (_authMode == mode)
        {
            return;
        }

        _authMode = mode;
        OnPropertyChanged(nameof(IsGhCliMode));
        OnPropertyChanged(nameof(IsPatMode));
        OnPropertyChanged(nameof(PatHint));
    }

    private void SetLanguage(AppLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsTraditionalChinese));
    }

    private bool TryParseInRange(string text, string name, int min, int max, out int value)
    {
        // 全形數字（中文輸入法）先轉成半形。
        var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            return true;
        }

        ErrorText = Loc.Format(Loc.Texts.SettingsRangeError, name, min, max);
        return false;
    }

    /// <summary>只接受可以放進 Authorization 標頭、也存得進認證管理員的值；錯誤訊息不帶任何內容。</summary>
    private bool CheckPatFormat(string candidate)
    {
        foreach (var c in candidate)
        {
            if (c < FirstVisible || c > LastVisible)
            {
                ErrorText = Loc.Texts.SettingsPatFormat;
                return false;
            }
        }

        if (Encoding.Unicode.GetByteCount(candidate) > CredentialBlobMaxBytes)
        {
            ErrorText = Loc.Texts.SettingsPatTooLong;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 有沒有已經存好的 PAT：Engine 正在用 PAT 就一定有；否則讀一次認證管理員（讀到的值不保留）。
    /// 讀取失敗時顯示錯誤並回 null。
    /// </summary>
    private bool? HasStoredPat()
    {
        if (_source == TokenSource.Pat)
        {
            return true;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(_deps.Credentials.Read(Tuning.CredentialTarget));
        }
        catch (Exception ex)
        {
            _deps.ErrorLog.Write("讀取 Windows 認證管理員", ex);
            ErrorText = Loc.Format(Loc.Texts.AuthCredentialReadFailed, Describe(ex));
            return null;
        }
    }

    /// <summary>儲存前先用 E1 驗證候選的 PAT（spec §11.2、§13.3）；失敗就不儲存。被回 401 的 PAT 在這個對話框裡不再送第二次。</summary>
    private async Task<bool> VerifyPatAsync(string candidate, CancellationToken ct)
    {
        var fingerprint = Fingerprint(candidate);
        if (_rejectedPats.Contains(fingerprint))
        {
            ErrorText = Loc.Texts.SettingsPatRejectedAgain;
            return false;
        }

        if (VerifyBlockedReason() is { } blocked)
        {
            ErrorText = blocked;
            return false;
        }

        StatusText = Loc.Texts.SettingsVerifyingPat;
        ApiResult<AccountInfo> result;
        try
        {
            result = await _deps.VerifyToken(candidate, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 視窗已經關閉，或程式正在結束。
            return false;
        }
        catch (Exception ex)
        {
            _deps.ErrorLog.Write("驗證 PAT", ex);
            ErrorText = Loc.Texts.CommonUnexpectedError;
            return false;
        }

        if (ct.IsCancellationRequested)
        {
            return false;
        }

        if (result.Outcome == ApiOutcome.Ok && result.Value is not null)
        {
            return true;
        }

        if (result.Outcome == ApiOutcome.Unauthorized)
        {
            _rejectedPats.Add(fingerprint);
        }

        ErrorText = result.Outcome switch
        {
            ApiOutcome.Unauthorized => Loc.Texts.SettingsPatUnauthorized,
            ApiOutcome.RateLimited => Loc.Texts.SettingsPatRateLimited,
            _ => Loc.Format(Loc.Texts.SettingsPatVerifyFailed, StatusCodeText(result.StatusCode)),
        };
        return false;
    }

    /// <summary>
    /// 驗證 PAT 的 E1 不經過 Engine 的節流器，所以要自己遵守 Engine 的停止條件：全域暫停（限流）期間所有請求都不送（spec §6.4），
    /// API 版本錯誤時也停止送出（spec §15）。依據是最新快照的角標：Engine 在暫停結束時會醒來發布新的快照。
    /// 未登入不擋：PAT 模式未登入時，就是等你在這裡貼上新的 PAT（spec §13.2）。
    /// </summary>
    private string? VerifyBlockedReason()
    {
        var issues = _deps.Engine.LatestSnapshot?.Summary.Issues ?? ConnectionIssue.None;
        if (issues.HasFlag(ConnectionIssue.VersionError))
        {
            return Loc.Texts.SettingsPatVersionError;
        }

        return issues.HasFlag(ConnectionIssue.RateLimited) ? Loc.Texts.SettingsPatRateLimited : null;
    }

    /// <summary>
    /// 開機自啟（同 SAM，spec §11.2）：和登錄檔的實際狀態不同，或已啟用但還是沒有 --tray 的舊格式時才寫入。
    /// 寫入失敗時顯示錯誤，勾選回到登錄檔的實際狀態（spec §15）。
    /// </summary>
    /// <param name="changedFrom">有改變啟用狀態時是改變前的狀態，給後面的步驟失敗時改回去；沒有改變（含只改寫舊格式）時為 null。</param>
    private bool ApplyStartup(bool runAtStartup, out bool? changedFrom)
    {
        changedFrom = null;
        try
        {
            var enabled = _deps.IsStartupEnabled();
            if (runAtStartup != enabled || (runAtStartup && _deps.StartupNeedsRewrite()))
            {
                _deps.SetStartupEnabled(runAtStartup);
                if (runAtStartup != enabled)
                {
                    changedFrom = enabled;
                }
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            _deps.ErrorLog.Write("設定開機自動啟動失敗", ex);
            RunAtStartup = _deps.IsStartupEnabled();
            ErrorText = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 後面的步驟失敗：開機自啟改回儲存前的狀態。勾選維持你的選擇，再按一次「儲存」就重試。
    /// 改不回去時只寫 error.log：登錄檔停在新的狀態，正好和勾選一致。
    /// </summary>
    private void RevertStartup(bool? changedFrom)
    {
        if (changedFrom is not { } before)
        {
            return;
        }

        try
        {
            _deps.SetStartupEnabled(before);
        }
        catch (InvalidOperationException ex)
        {
            _deps.ErrorLog.Write("還原開機自動啟動失敗", ex);
        }
    }

    /// <summary>
    /// settings.json 已經存了新值、後面的步驟失敗：記憶體裡的設定已改回原值，再存一次。
    /// 這次也失敗時只寫 error.log（畫面已經顯示後面那一步的錯誤）。
    /// </summary>
    private void RestoreSettingsFile()
    {
        try
        {
            _deps.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _deps.ErrorLog.Write("還原設定檔失敗", ex);
        }
    }

    /// <summary>新的 PAT 寫進認證管理員；按了「清除」就刪除。失敗時顯示錯誤（由呼叫端改回設定，不改變目前的認證方式，spec §15）。</summary>
    private bool ApplyCredential(string? candidate, bool clearPat)
    {
        try
        {
            if (candidate is not null)
            {
                _deps.Credentials.Write(Tuning.CredentialTarget, candidate);
            }
            else if (clearPat)
            {
                _deps.Credentials.Delete(Tuning.CredentialTarget);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _deps.ErrorLog.Write(candidate is not null ? "寫入 Windows 認證管理員" : "刪除 Windows 認證管理員的 PAT", ex);
            ErrorText = Loc.Format(candidate is not null ? Loc.Texts.SettingsCredentialWriteFailed : Loc.Texts.SettingsCredentialDeleteFailed, Describe(ex));
            return false;
        }
    }

    private void ReloadAccount()
    {
        try
        {
            if (_deps.ReadAccount() is { } account)
            {
                _login = account.Login;
            }
        }
        catch (Exception ex)
        {
            // 讀不到帳號只影響顯示，不擋設定頁。
            _deps.ErrorLog.Write("讀取帳號", ex);
        }
    }

    private void UpdateAccount()
    {
        var text = Loc.Texts;
        AccountText = _auth switch
        {
            AuthStatus.Ok => _login ?? text.SettingsSignedIn,
            AuthStatus.Unauthorized => text.SettingsNotSignedIn,
            _ => text.SettingsVerifying,
        };

        TokenSourceText = _source switch
        {
            TokenSource.GhStored => text.SettingsSourceGhStored,
            TokenSource.GhEnvironment => text.SettingsSourceGhEnvironment,
            TokenSource.Pat => text.SettingsSourcePat,
            _ => text.SettingsSourceNone,
        };
    }

    private static string RangeLabel(string name, int min, int max) => Loc.Format(Loc.Texts.SettingsRangeLabel, name, min, max);

    private static string StatusCodeText(int statusCode) =>
        statusCode == NoResponse ? Loc.Texts.CommonConnectionFailed : Loc.Format(Loc.Texts.CommonStatusCode, statusCode);

    /// <summary>
    /// 系統錯誤用 Windows 的錯誤說明加錯誤碼（同 Core 的 PatTokenProvider）；長度超過上限有自己的說明；
    /// 其他例外的訊息內容無法保證不夾帶 secret，只寫型別名稱。
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        Win32Exception win32 => Loc.Format(Loc.Texts.CommonErrorCode, win32.Message, win32.NativeErrorCode),
        ArgumentException => Loc.Texts.SettingsPatTooLong,
        _ => ex.GetType().Name,
    };

    /// <summary>PAT 的 SHA-256（十六進位）；只用來在記憶體裡比對「是不是剛才被拒的那一把」。</summary>
    private static string Fingerprint(string candidate)
    {
        var bytes = Encoding.UTF8.GetBytes(candidate);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>設定頁改得到的欄位；儲存失敗時用來改回原值。</summary>
    private sealed record SettingsValues(
        AuthMode AuthMode,
        bool IgnoreGhTokenEnv,
        bool IncludeForks,
        bool IncludeCollaboratorRepos,
        bool NotifyTrackedFailure,
        bool NotifyMyRunSuccess,
        bool NotifyWatchedRunDone,
        int FailureToastSeconds,
        int SuccessToastSeconds,
        int LogTailLines,
        bool AllowWriteActions,
        bool AlwaysOnTop,
        bool RunAtStartup,
        bool MinimizeToTray,
        AppLanguage Language,
        bool CheckForUpdates)
    {
        public static SettingsValues From(AppSettings settings) => new(
            settings.AuthMode,
            settings.IgnoreGhTokenEnv,
            settings.IncludeForks,
            settings.IncludeCollaboratorRepos,
            settings.NotifyTrackedFailure,
            settings.NotifyMyRunSuccess,
            settings.NotifyWatchedRunDone,
            settings.FailureToastSeconds,
            settings.SuccessToastSeconds,
            settings.LogTailLines,
            settings.AllowWriteActions,
            settings.AlwaysOnTop,
            settings.RunAtStartup,
            settings.MinimizeToTray,
            settings.Language,
            settings.CheckForUpdates);

        public void ApplyTo(AppSettings settings)
        {
            settings.AuthMode = AuthMode;
            settings.IgnoreGhTokenEnv = IgnoreGhTokenEnv;
            settings.IncludeForks = IncludeForks;
            settings.IncludeCollaboratorRepos = IncludeCollaboratorRepos;
            settings.NotifyTrackedFailure = NotifyTrackedFailure;
            settings.NotifyMyRunSuccess = NotifyMyRunSuccess;
            settings.NotifyWatchedRunDone = NotifyWatchedRunDone;
            settings.FailureToastSeconds = FailureToastSeconds;
            settings.SuccessToastSeconds = SuccessToastSeconds;
            settings.LogTailLines = LogTailLines;
            settings.AllowWriteActions = AllowWriteActions;
            settings.AlwaysOnTop = AlwaysOnTop;
            settings.RunAtStartup = RunAtStartup;
            settings.MinimizeToTray = MinimizeToTray;
            settings.Language = Language;
            settings.CheckForUpdates = CheckForUpdates;
        }
    }
}

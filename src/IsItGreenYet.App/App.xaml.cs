using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IsItGreenYet.App.Localization;
using IsItGreenYet.App.Services;
using IsItGreenYet.App.Themes;
using IsItGreenYet.App.Views;
using IsItGreenYet.Core;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Logging;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Notifications;
using IsItGreenYet.Core.Services;

namespace IsItGreenYet.App;

/// <summary>
/// 殼層（spec §3.3）：單一實例 → 資料夾 → error.log → cache.db → settings.json → AppHost → 主視窗 → Engine。
/// 介面語言：一開始先從 settings.json 只讀出語言（不改動檔案），建立資料夾、開啟快取資料庫失敗的訊息也用設定的語言；
/// 讀完整份設定後套用（<see cref="Loc.Apply"/>），設定頁儲存時再切換。
/// </summary>
public partial class App : Application
{
    private const string AppTitle = "IsItGreenYet";

    /// <summary>建立命令列刷新事件失敗時 error.log 的標題（第二階段 spec §10.5）。</summary>
    private const string RefreshSignalFailedContext = "建立命令列刷新事件失敗，這次執行不支援 --refresh";

    /// <summary>設定檔讀不到、改名也失敗時 error.log 的標題（<see cref="SettingsService.LoadFailedFileKept"/>）。</summary>
    private const string SettingsLoadFailedContext = "讀不到設定檔，這次用預設值、不會寫回，重新啟動即可";

    /// <summary>權限連改名都拒絕時 error.log 的標題：重新啟動也一樣，所以不拒絕寫入、沒有橫幅（<see cref="SettingsService.LoadFailedFileKept"/>）。</summary>
    private const string SettingsLoadDeniedContext = "沒有權限讀取設定檔，也改不了名，這次用預設值，之後儲存時會覆蓋它";

    /// <summary>設計選擇：結束時最多等 Engine 收尾這麼久，收不完就直接結束，不讓程式關不掉。</summary>
    private static readonly TimeSpan HostDisposeTimeout = TimeSpan.FromSeconds(5);

    private static AppHost? s_services;

    private readonly SecretRedactor _redactor = new();

    // 結束時取消；Engine 收尾逾時還可能拿著這個 token，所以不 Dispose（行程本來就要結束）。
    private readonly CancellationTokenSource _shutdown = new();

    private SingleInstanceGuard? _instanceGuard;
    private AppPaths? _paths;
    private ErrorLog? _errorLog;
    private AppHost? _host;
    private MainWindow? _mainWindow;
    private SessionMonitor? _sessionMonitor;
    private DispatcherTimer? _tickTimer;
    private TrayIconController? _tray;
    private ToastWindow? _toast;

    /// <summary>錯誤對話框正在顯示（見 <see cref="ShowError"/>）。</summary>
    private bool _errorDialogOpen;

    /// <summary>各視窗共用的物件（同 SAM 的 AppServices）。啟動流程完成前存取會丟例外。</summary>
    internal static AppHost Services =>
        s_services ?? throw new InvalidOperationException("應用程式還沒完成啟動。");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // gh 用裸檔名啟動（GhCliTokenProvider），Windows 會找目前目錄：從別的資料夾啟動時不能跑到那裡的 gh.exe。程式裡的路徑都是絕對路徑，不受影響。
        Environment.CurrentDirectory = Environment.SystemDirectory;

        // Windows 顯示語言：在 Loc.Apply 改掉 UI 執行緒的 CurrentUICulture 之前記下來，第一次執行（沒有 settings.json）用它決定預設語言。
        var displayCulture = CultureInfo.CurrentUICulture;

        // WPF 內建的右鍵選單（TextBox 的剪下／複製／貼上等）改用深色選單樣式；在顯示任何視窗或錯誤對話框之前登記。
        BuiltinMenus.Register(this);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var time = TimeProvider.System;

        // 通知的時間界線是程式啟動時間（spec §9.3），在做任何耗時的初始化之前記下來。
        var startedAtUtc = time.GetUtcNow();

        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.IsFirstInstance)
        {
            // 已經有一份在跑，請它把視窗叫出來然後自己退場（spec §15「重複啟動」）。
            _instanceGuard.SignalExistingInstance();
            _instanceGuard.Dispose();
            _instanceGuard = null;
            Shutdown();
            return;
        }

        var paths = AppPaths.Default;
        var settingsStore = new SettingsService(paths.SettingsPath, displayCulture);
        Loc.Instance.Apply(settingsStore.PeekLanguage());
        try
        {
            paths.EnsureCreated();
        }
        catch (InvalidOperationException ex)
        {
            // %LOCALAPPDATA% 不可寫：顯示錯誤並指出路徑（spec §15，同 SAM）。訊息已經含路徑。
            ShowError(ex.Message);
            Shutdown();
            return;
        }

        _paths = paths;
        _errorLog = new ErrorLog(paths.RootDirectory, _redactor, time);

        // 單一實例比 error.log 早建立：刷新事件建立失敗的原因到這裡才寫得進去；啟動照常進行。
        if (_instanceGuard.RefreshSignalError is { } refreshSignalError)
        {
            _errorLog.Write(RefreshSignalFailedContext, refreshSignalError);
        }

        try
        {
            InitializeShell(paths, settingsStore, time, startedAtUtc, HasTrayArgument(e.Args));
        }
        catch (Exception ex)
        {
            // 啟動到一半失敗：ShutdownMode 是 OnExplicitShutdown，不收掉的話會變成沒有視窗的殭屍行程。
            _errorLog.Write("啟動失敗", ex);
            ShowError(Loc.Format(Loc.Texts.StartupFailed, paths.ErrorLogPath, _redactor.Redact(ex.Message)));
            Shutdown();
        }
    }

    private void InitializeShell(AppPaths paths, SettingsService settingsStore, TimeProvider time, DateTimeOffset startedAtUtc, bool startInTray)
    {
        DatabaseInitResult database;
        try
        {
            database = DatabaseInitializer.Ensure(paths.CacheDbPath, time);
        }
        catch (Exception ex)
        {
            _errorLog!.Write("快取資料庫初始化失敗", ex);
            ShowError(Loc.Format(Loc.Texts.StartupDatabaseFailed, paths.CacheDbPath, _redactor.Redact(ex.Message)));
            Shutdown();
            return;
        }

        var firstRun = !File.Exists(paths.SettingsPath);
        var settings = settingsStore.Load();

        // 建立任何視窗之前套用設定的語言（和先前只讀出的語言相同時不做事）。
        Loc.Instance.Apply(settings.Language);
        if (firstRun)
        {
            // 第一次執行就把預設值寫出來，使用者才找得到 settings.json。
            // 只在檔案原本不存在時寫：損毀但改名失敗時檔案還在，不能拿預設值蓋掉它。
            TrySaveInitialSettings(settingsStore, settings);
        }

        var startupWarning = BuildStartupWarning(database, settingsStore);

        _host = new AppHost(paths, settingsStore, settings, _errorLog!, _redactor, time, startedAtUtc, startupWarning, Dispatcher);
        s_services = _host;

        _sessionMonitor = new SessionMonitor(_host.Engine.Post, _host.ReportBackgroundError, _errorLog!);

        _mainWindow = new MainWindow(_host);
        _mainWindow.SourceInitialized += OnMainWindowSourceInitialized;
        MainWindow = _mainWindow;

        // 系統匣不可用時維持 IsTrayAvailable = false：✕ 直接結束，不收到看不到的系統匣（spec §10.1、§12）。
        _tray = CreateTray(_host, _mainWindow, time);
        _mainWindow.IsTrayAvailable = _tray.IsAvailable;
        _host.Engine.SnapshotPublished += _tray.PostSnapshot;
        Loc.Instance.LanguageChanged += _tray.RefreshLanguage;

        _toast = new ToastWindow(_host.ErrorLog, OpenMainWindowFromToast, time: time);
        _toast.Dismissed += OnToastDismissed;
        _host.Engine.ToastReady += OnToastReady;

        _instanceGuard!.ActivationRequested += OnActivationRequested;
        _instanceGuard.RefreshRequested += OnRefreshRequested;
        _instanceGuard.StartListening();

        // 一律先建立視窗控制代碼：帶 --tray 時不 Show，SourceInitialized 也照樣發生，系統事件才訂閱得到（spec §6.5）。
        // 系統匣不可用時 --tray 也照樣顯示視窗，否則程式會在背景跑、卻沒有任何地方叫得出來。
        new WindowInteropHelper(_mainWindow).EnsureHandle();
        if (!startInTray || !_tray.IsAvailable)
        {
            _mainWindow.Show();
        }

        StartEngine();
    }

    /// <summary>系統匣圖示與右鍵選單（spec §12）：選單的動作都交給主視窗已經提供的方法。</summary>
    private TrayIconController CreateTray(AppHost host, MainWindow window, TimeProvider time)
    {
        var tray = new TrayIconController(
            isPaused: () => host.Settings.RefreshMode == RefreshMode.Paused,
            findResource: TryFindResource,
            reportError: host.ReportBackgroundError,
            errorLog: host.ErrorLog,
            dispatcher: Dispatcher,
            time: time);

        tray.OpenRequested += window.BringToFront;
        tray.RefreshRequested += window.RefreshNow;
        tray.PauseToggleRequested += window.ToggleAutoRefreshPaused;
        tray.RepoManagerRequested += () => window.Navigator.OpenRepoManager(VisibleOwner(window));
        tray.SettingsRequested += () => window.Navigator.OpenSettings(VisibleOwner(window));
        tray.OpenFolderRequested += () =>
        {
            if (!window.Navigator.OpenDataFolder())
            {
                // 同主畫面的訊息條；spec 沒有給文字。
                ShowError(Loc.Texts.CommonFolderFailed);
            }
        };
        tray.ExitRequested += window.ExitApplication;
        return tray;
    }

    /// <summary>主視窗收在系統匣時不當 owner：從沒顯示過的視窗不能當 owner（WPF 會丟例外）。</summary>
    private static Window? VisibleOwner(MainWindow window) => window.IsVisible ? window : null;

    /// <summary>Engine 在背景執行緒觸發（spec §9.4）：交回 UI 執行緒顯示或更新提示。</summary>
    private void OnToastReady(ToastContent content) =>
        Dispatcher.InvokeAsync(() => ShowToast(content));

    /// <summary>提示失敗不跳對話框：通知可能接連進來，改成寫 error.log 並用橫幅提示一次（spec §15）。</summary>
    private void ShowToast(ToastContent content)
    {
        try
        {
            _toast?.Present(content);
        }
        catch (Exception ex)
        {
            _host?.ReportBackgroundError("顯示通知提示", ex);
        }
    }

    /// <summary>提示關掉（時間到、✕、點了開啟）：之後的通知另起一則，不再併入（spec §9.4）。</summary>
    /// <remarks>
    /// 已知限制（要改 Core 才能修，不在 App 的範圍）：Engine 到下一步開頭才處理這個指令，這一步結尾合併提示時
    /// 用的還是關掉前的內容。所以在一步執行到一半時關掉（或新內容已經排進 UI 執行緒、在關掉之後才顯示），
    /// 剛關掉的那幾筆會跟著新通知以彙總再出現一次；Engine 隨後才清掉「顯示中」的紀錄，
    /// 這則彙總還開著時再來的通知會整個換掉它，而不是併進去。
    /// 修法：ToastContent 帶流水號、ToastDismissed 帶回，NotificationQueue 只在號碼相符時才清空。
    /// </remarks>
    private void OnToastDismissed() => _host?.Engine.Post(new EngineCommand.ToastDismissed());

    /// <summary>彙總提示點空白處開主視窗（spec §9.4）。</summary>
    private void OpenMainWindowFromToast() => _mainWindow?.BringToFront();

    /// <summary>
    /// DB 與設定檔都損毀時，只保留比較嚴重的 DB 那則（spec §10.2 依嚴重度只顯示最重要的一則；同 SAM 的先後）。
    /// 回傳依語言產生橫幅的方法：換語言時主畫面重新產生，<see cref="BannerInfo.NoticeId"/> 不變（關掉的不會再出現）。
    /// 設定檔讀不到、改名也失敗時（<see cref="SettingsService.LoadFailedFileKept"/>）用自己的文字：檔案沒有被改名，不能說「已改名保留」。
    /// </summary>
    private Func<AppLanguage, BannerInfo>? BuildStartupWarning(DatabaseInitResult database, SettingsService settingsStore)
    {
        // 橫幅只留一則，error.log 不管最後顯示哪一則都要寫。
        if (settingsStore.LastLoadError is { } settingsLoadError)
        {
            _errorLog!.Write(settingsStore.LoadFailedFileKept ? SettingsLoadFailedContext : SettingsLoadDeniedContext, settingsLoadError);
        }

        if (database.RecoveredFromCorruption)
        {
            var backupName = Path.GetFileName(database.BackupPath);
            var databaseNoticeId = BannerInfo.NextNoticeId();
            return language => new BannerInfo(BannerKind.DatabaseRecovered, UiText.DatabaseRebuilt(backupName, language), Until: null)
            {
                NoticeId = databaseNoticeId,
            };
        }

        if (settingsStore.LastRecoveryBackupPath is not null)
        {
            var settingsNoticeId = BannerInfo.NextNoticeId();
            return language => new BannerInfo(BannerKind.SettingsRecovered, UiText.Of(language).BannerSettingsRecovered, Until: null)
            {
                NoticeId = settingsNoticeId,
            };
        }

        if (settingsStore.LoadFailedFileKept)
        {
            var settingsNoticeId = BannerInfo.NextNoticeId();
            return language => new BannerInfo(BannerKind.SettingsRecovered, UiText.Of(language).BannerSettingsLoadFailed, Until: null)
            {
                NoticeId = settingsNoticeId,
            };
        }

        return null;
    }

    private void TrySaveInitialSettings(SettingsService settingsStore, AppSettings settings)
    {
        try
        {
            settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 寫不出來不影響這次執行，之後儲存設定時會再試。
            _errorLog!.Write("建立設定檔失敗", ex);
        }
    }

    /// <summary>
    /// 呼叫 StartAsync；同步就失敗時不開 tick 計時器（Engine 沒在跑，每秒 Tick 只會一直失敗）。
    /// 非同步失敗時才停掉計時器。兩種都寫 error.log 並用橫幅提示（spec §15 背景例外）。
    /// </summary>
    private void StartEngine()
    {
        var host = _host!;
        Task start;
        try
        {
            start = host.Engine.StartAsync(_shutdown.Token);
        }
        catch (Exception ex)
        {
            host.ReportBackgroundError("監看引擎啟動失敗", ex);
            return;
        }

        // spec §3.1：Core 不開計時器，App 每秒呼叫 Tick()（睡眠偵測與喚醒迴圈都靠它）。
        _tickTimer = new DispatcherTimer { Interval = Tuning.EngineTick };
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();

        _ = ObserveEngineStartAsync(host, start);
    }

    private async Task ObserveEngineStartAsync(AppHost host, Task start)
    {
        try
        {
            // 在 UI 執行緒上接續，才能直接停掉計時器。
            await start;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 程式正在結束。
        }
        catch (Exception ex)
        {
            _tickTimer?.Stop();
            host.ReportBackgroundError("監看引擎啟動失敗", ex);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _host?.Engine.Tick();
        }
        catch (Exception ex)
        {
            // Tick 只記時間戳並喚醒迴圈，會失敗代表 Engine 已經不能用；停掉計時器，免得每秒寫一筆 error.log。
            _tickTimer?.Stop();
            _host?.ReportBackgroundError("監看引擎 Tick 失敗", ex);
        }
    }

    /// <summary>spec §6.5：在 UI 執行緒、主視窗 SourceInitialized 之後才訂閱 SystemEvents。</summary>
    private void OnMainWindowSourceInitialized(object? sender, EventArgs e) => _sessionMonitor?.Start();

    /// <summary>在單一實例的監聽執行緒上觸發，交回 UI 執行緒處理。</summary>
    private void OnActivationRequested() =>
        Dispatcher.BeginInvoke(() => _mainWindow?.BringToFront());

    /// <summary>
    /// 命令列 <c>--refresh</c> 的訊號（第二階段 spec §10.5），在單一實例的監聽執行緒上觸發。
    /// Engine 的 Post 可以從任何執行緒呼叫，不必交回 UI 執行緒；Engine 已經釋放時 Post 不做事，結束途中收到的訊號就此忽略。
    /// </summary>
    private void OnRefreshRequested() =>
        Volatile.Read(ref _host)?.Engine.Post(new EngineCommand.CliRefresh());

    private static bool HasTrayArgument(string[] args) =>
        args.Any(arg => string.Equals(arg, StartupManager.TrayArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>UI 執行緒的未預期例外：寫 error.log、跳對話框、程式繼續執行（spec §15，同 SAM）。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _errorLog?.Write("未處理的例外", e.Exception);

        var logPath = (_paths ?? AppPaths.Default).ErrorLogPath;
        ShowError(Loc.Format(Loc.Texts.ErrorUnexpectedWithLog, logPath, _redactor.Redact(e.Exception.Message)));

        // 已經回報給使用者，讓程式繼續跑，不要因為一次錯誤就整個關掉。
        e.Handled = true;
    }

    /// <summary>沒有人等待的背景工作失敗（在終結器執行緒上觸發）：寫 error.log、橫幅提示，不跳對話框（spec §15）。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();

        const string context = "背景工作發生未處理的例外";
        if (Volatile.Read(ref _host) is { } host)
        {
            host.ReportBackgroundError(context, e.Exception);
        }
        else
        {
            _errorLog?.Write(context, e.Exception);
        }
    }

    /// <summary>
    /// 顯示錯誤（spec §15）：深色的錯誤對話框（<see cref="ErrorDialog"/>）。App 有看得到的視窗時以使用中的那一個（沒有就用主視窗）為 owner、置中在它上面；
    /// 沒有時（例如啟動失敗、收在系統匣）在游標所在的螢幕置中（WPF 的 CenterScreen）並出現在工作列，否則找不到它。
    /// </summary>
    /// <remarks>
    /// 這是錯誤路徑，對話框本身不能讓訊息消失：建立或顯示時丟任何例外（例如 XAML 資源載入失敗），
    /// 或對話框還開著時又來一個錯誤（包括對話框自己出錯又觸發 DispatcherUnhandledException），都改用系統 MessageBox。
    /// 主畫面的寫入結果（被拒、結果不確定，第二階段 spec §9.4）也用這個顯示。
    /// </remarks>
    internal void ShowError(string message)
    {
        if (!_errorDialogOpen)
        {
            _errorDialogOpen = true;
            ErrorDialog? dialog = null;
            try
            {
                // 先決定 owner 再建立對話框：還沒有主視窗時，WPF 會把第一個建立的視窗當成 MainWindow。
                var owner = ErrorDialogOwner();
                dialog = new ErrorDialog(AppTitle, message);
                if (owner is not null)
                {
                    dialog.Owner = owner;
                }
                else
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    dialog.ShowInTaskbar = true;
                }

                dialog.ShowDialog();
                return;
            }
            catch (Exception ex)
            {
                _errorLog?.Write("顯示錯誤對話框失敗，改用系統訊息框", ex);
                CloseQuietly(dialog);
            }
            finally
            {
                _errorDialogOpen = false;
            }
        }

        MessageBox.Show(message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>ShowDialog 已經把對話框顯示出來才失敗時收掉它，免得和接著跳出的 MessageBox 同時出現；收不掉就留著，訊息框照樣顯示。</summary>
    private static void CloseQuietly(Window? window)
    {
        try
        {
            window?.Close();
        }
        catch (InvalidOperationException)
        {
            // 例如 Dispatcher 仍在暫停處理：留給使用者自己關。
        }
    }

    /// <summary>使用中的視窗；沒有就是看得到的主視窗；都沒有回 null。從沒顯示過或已經隱藏的視窗不能當 owner（WPF 會丟例外）。</summary>
    private Window? ErrorDialogOwner() =>
        Windows.OfType<Window>().FirstOrDefault(window => window is { IsActive: true, IsVisible: true })
        ?? (MainWindow is { IsVisible: true } main ? main : null);

    protected override void OnExit(ExitEventArgs e)
    {
        _tickTimer?.Stop();

        // 先停掉 Engine 的事件，再收掉提示與系統匣：重複使用中的提示視窗要真的關掉，系統匣圖示才不會殘留（同 SAM）。
        if (_host is { } host)
        {
            host.Engine.ToastReady -= OnToastReady;
            if (_tray is not null)
            {
                host.Engine.SnapshotPublished -= _tray.PostSnapshot;
                Loc.Instance.LanguageChanged -= _tray.RefreshLanguage;
            }
        }

        if (_toast is not null)
        {
            _toast.Dismissed -= OnToastDismissed;
            _toast.Shutdown();
        }

        _tray?.Dispose();

        // SystemEvents 是靜態事件，結束前一定要取消訂閱（spec §6.5）。
        _sessionMonitor?.Dispose();

        _shutdown.Cancel();
        DisposeHost();

        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }

    private void DisposeHost()
    {
        if (_host is not { } host)
        {
            return;
        }

        try
        {
            // 在執行緒集區上等：Engine 收尾時若要回到 UI 執行緒，就不會和這裡互相等待。
            var disposal = Task.Run(async () => await host.DisposeAsync().ConfigureAwait(false));
            if (!disposal.Wait(HostDisposeTimeout))
            {
                _errorLog?.WriteMessage("結束程式", $"監看引擎在 {HostDisposeTimeout.TotalSeconds} 秒內沒有收尾完成，直接結束。");
            }
        }
        catch (Exception ex)
        {
            _errorLog?.Write("結束監看引擎失敗", ex);
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using IsItGreenYet.App.ViewModels;
using IsItGreenYet.App.Views;
using IsItGreenYet.Core.Data;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.App.Services;

/// <summary>
/// 開啟其他視窗與資料夾的入口：主畫面的標題列、橫幅、空狀態，以及之後的系統匣選單都走這裡（spec §10、§12）。
/// </summary>
/// <remarks>
/// repo 管理頁與設定頁都是對話框（同 SAM 的 ShowDialog）。已經開著時再叫一次（例如從系統匣），只把開著的那一個叫到前景。
/// </remarks>
internal sealed class WindowNavigator
{
    private readonly AppHost _host;
    private readonly EngineFeed _engine;

    private RepoManagerWindow? _repoManager;
    private SettingsWindow? _settings;

    public WindowNavigator(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        // 主視窗建構時就建立（Engine 啟動之前），所以每一份快照都收得到：對話框一打開就有最新的 repo 清單與登入狀態。
        _engine = new EngineFeed(host.Engine.Post, host.ReportBackgroundError);
        host.Engine.SnapshotPublished += _engine.Publish;
    }

    /// <summary>repo 管理頁（spec §11.1）。</summary>
    public void OpenRepoManager(Window? owner)
    {
        if (BringToFront(_repoManager))
        {
            return;
        }

        var window = new RepoManagerWindow(_engine);
        _repoManager = window;

        // 開啟時抓一次清單 B，才列得出所有看得到的 repo（spec §5.1、§11.1）；抓回來後由快照更新清單。
        _engine.Post(new EngineCommand.RepoManagerOpened());
        try
        {
            ShowDialog(window, owner);
        }
        finally
        {
            _repoManager = null;
        }
    }

    /// <summary>設定頁（spec §11.2）。儲存後即時套用「視窗置頂」與關閉鈕的說明。</summary>
    public void OpenSettings(Window? owner)
    {
        if (BringToFront(_settings))
        {
            return;
        }

        var dependencies = new SettingsDependencies(
            Settings: _host.Settings,
            SaveSettings: _host.SaveSettings,
            Engine: _engine,
            CheckConnection: _host.Engine.CheckConnectionAsync,
            VerifyToken: _host.VerifyTokenAsync,
            Credentials: new WindowsCredentialStore(),
            IsStartupEnabled: StartupManager.IsEnabled,
            StartupNeedsRewrite: StartupManager.NeedsRewrite,
            SetStartupEnabled: StartupManager.SetEnabled,
            ReadAccount: ReadAccount,
            ErrorLog: _host.ErrorLog);

        var window = new SettingsWindow(new SettingsViewModel(dependencies), _engine, OpenDataFolder, _host.Paths.RootDirectory);
        _settings = window;
        try
        {
            ShowDialog(window, owner);
        }
        finally
        {
            _settings = null;
        }

        if (window.Saved && Application.Current?.MainWindow is MainWindow main)
        {
            main.ApplySavedSettings();
        }
    }

    /// <summary>用檔案總管開啟 <c>%LOCALAPPDATA%\IsItGreenYet</c>（spec §10.2、§11.2、§12）。</summary>
    /// <returns>開不起來時回 false（已寫 error.log），由呼叫端提示。</returns>
    public bool OpenDataFolder()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _host.Paths.RootDirectory,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _host.ErrorLog.Write("開啟資料夾失敗", ex);
            return false;
        }
    }

    /// <summary>
    /// 主視窗看得到時以它為 owner、置中在它上面；收在系統匣時沒有 owner，改成螢幕置中並出現在工作列（否則找不到這個視窗）。
    /// </summary>
    private static void ShowDialog(Window window, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
        }

        window.ShowDialog();
    }

    private static bool BringToFront(Window? window)
    {
        if (window is null)
        {
            return false;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        return true;
    }

    /// <summary>
    /// 設定頁顯示「目前登入的 login」（spec §11.2）：快照沒有帳號，讀 Engine 最近一次 E1 驗證成功時寫進 cache.db 的 account。
    /// 每次呼叫各開一條連線（CacheRepository 的做法），和 Engine 同時讀寫沒有問題（WAL）。
    /// </summary>
    private AccountInfo? ReadAccount() => new CacheRepository(_host.Paths.CacheDbPath, TimeProvider.System).GetAccount();
}

/// <summary>
/// repo 管理頁與設定頁看到的 Engine：最新的快照、快照更新的事件，以及送指令。
/// App 由 <see cref="WindowNavigator"/> 接到 MonitorEngine；驗證畫面時可以直接用假資料呼叫 <see cref="Publish"/>。
/// </summary>
internal sealed class EngineFeed(Action<EngineCommand> post, Action<string, Exception> reportError)
{
    private readonly Action<EngineCommand> _post = post ?? throw new ArgumentNullException(nameof(post));
    private readonly Action<string, Exception> _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
    private MonitorSnapshot? _latest;

    /// <summary>在發布快照的執行緒上觸發（Engine 的背景執行緒），接收端要自己轉回 UI 執行緒。</summary>
    public event Action<MonitorSnapshot>? SnapshotPublished;

    /// <summary>最新的一份快照；還沒有任何快照時為 null。</summary>
    public MonitorSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    public void Publish(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
        SnapshotPublished?.Invoke(snapshot);
    }

    public void Post(EngineCommand command) => _post(command);

    /// <summary>套用快照等背景性質的失敗：寫 error.log，同一種例外 10 分鐘內只用橫幅提示一次（spec §15）。</summary>
    public void ReportError(string context, Exception exception) => _reportError(context, exception);
}

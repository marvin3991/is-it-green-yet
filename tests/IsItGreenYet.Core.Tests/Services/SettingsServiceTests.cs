using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using IsItGreenYet.Core.Engine;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;
using IsItGreenYet.Core.Services;
using IsItGreenYet.Core.Tests.TestSupport;

namespace IsItGreenYet.Core.Tests.Services;

public class SettingsServiceTests
{
    // ── 沿用 SAM 的案例（改成本專案欄位）────────────────────────────────

    [Fact]
    public void 設定檔不存在時回傳預設值且不建立檔案()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        var settings = service.Load();

        AssertSpecDefaults(settings);
        Assert.False(File.Exists(workspace.Paths.SettingsPath));
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 儲存後可以原樣讀回()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);
        var settings = new AppSettings
        {
            AuthMode = AuthMode.Pat,
            IgnoreGhTokenEnv = true,
            IncludeForks = true,
            IncludeCollaboratorRepos = true,
            RefreshMode = RefreshMode.Paused,
            PreviousRefreshMode = RefreshMode.Minutes5,
            NotifyTrackedFailure = false,
            NotifyMyRunSuccess = false,
            NotifyWatchedRunDone = false,
            FailureToastSeconds = 30,
            SuccessToastSeconds = 8,
            LogTailLines = 1000,
            AlwaysOnTop = true,
            RunAtStartup = true,
            MinimizeToTray = false,
            AllowWriteActions = true,
            CheckForUpdates = false,
            LastUpdateCheckUtc = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero),
            LastNotifiedVersion = "0.3.0",
            // 主螢幕左邊的副螢幕座標是負的，要原樣保留。
            Window = new WindowPlacement { Left = -1280.5, Top = 40, Width = 1440, Height = 900, Maximized = true },
            Filters = new FilterPreferences
            {
                Repos = ["octo-org/sample-app"],
                Workflows = ["CI", "Lint"],
                Events = ["push", "pull_request"],
                Statuses = [nameof(DisplayState.Failure), nameof(DisplayState.InProgress)],
                Branches = ["main", "feature/a#1"],
                Actors = ["alice"],
                AttentionOnly = true,
            },
        };

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(AuthMode.Pat, loaded.AuthMode);
        Assert.True(loaded.IgnoreGhTokenEnv);
        Assert.True(loaded.IncludeForks);
        Assert.True(loaded.IncludeCollaboratorRepos);
        Assert.Equal(RefreshMode.Paused, loaded.RefreshMode);
        Assert.Equal(RefreshMode.Minutes5, loaded.PreviousRefreshMode);
        Assert.False(loaded.NotifyTrackedFailure);
        Assert.False(loaded.NotifyMyRunSuccess);
        Assert.False(loaded.NotifyWatchedRunDone);
        Assert.Equal(30, loaded.FailureToastSeconds);
        Assert.Equal(8, loaded.SuccessToastSeconds);
        Assert.Equal(1000, loaded.LogTailLines);
        Assert.True(loaded.AlwaysOnTop);
        Assert.True(loaded.RunAtStartup);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.AllowWriteActions);
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero), loaded.LastUpdateCheckUtc);
        Assert.Equal("0.3.0", loaded.LastNotifiedVersion);
        Assert.Equal(-1280.5, loaded.Window.Left);
        Assert.Equal(40, loaded.Window.Top);
        Assert.Equal(1440, loaded.Window.Width);
        Assert.Equal(900, loaded.Window.Height);
        Assert.True(loaded.Window.Maximized);
        Assert.Equal(["octo-org/sample-app"], loaded.Filters.Repos);
        Assert.Equal(["CI", "Lint"], loaded.Filters.Workflows);
        Assert.Equal(["push", "pull_request"], loaded.Filters.Events);
        Assert.Equal(["Failure", "InProgress"], loaded.Filters.Statuses);
        Assert.Equal(["main", "feature/a#1"], loaded.Filters.Branches);
        Assert.Equal(["alice"], loaded.Filters.Actors);
        Assert.True(loaded.Filters.AttentionOnly);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 儲存後不會留下暫存檔()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings());

        Assert.True(File.Exists(workspace.Paths.SettingsPath));
        Assert.False(File.Exists(workspace.Paths.SettingsPath + ".tmp"));
    }

    [Fact]
    public void 設定檔內容損毀時改名保留並回落到預設值()
    {
        using var workspace = new TempWorkspace();
        const string broken = "{ 這不是合法 JSON";
        File.WriteAllText(workspace.Paths.SettingsPath, broken, Encoding.UTF8);
        var service = new SettingsService(workspace.Paths.SettingsPath);

        var settings = service.Load();

        AssertSpecDefaults(settings);
        Assert.NotNull(service.LastRecoveryBackupPath);
        Assert.True(File.Exists(service.LastRecoveryBackupPath));
        Assert.Equal(broken, File.ReadAllText(service.LastRecoveryBackupPath, Encoding.UTF8));
        Assert.False(File.Exists(workspace.Paths.SettingsPath));
    }

    // ── 讀不到設定檔（其他程式開著、權限不足）──────────────────────────

    [Theory]
    [InlineData(FileAccess.Read, FileShare.None)]
    [InlineData(FileAccess.ReadWrite, FileShare.None)]
    [InlineData(FileAccess.ReadWrite, FileShare.Read)]
    [InlineData(FileAccess.ReadWrite, FileShare.ReadWrite)]
    public void 其他程式開著而且不允許刪除時回預設值_原檔不動(FileAccess access, FileShare share)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);
        var before = File.ReadAllBytes(workspace.Paths.SettingsPath);

        AppSettings settings;
        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, access, share))
        {
            settings = service.Load();
        }

        // 讀不到使用者的選擇：這次執行不檢查新版本，否則可能違反「關閉後完全不送」。
        AssertSpecDefaults(settings, checkForUpdates: false);
        Assert.Null(service.LastRecoveryBackupPath);
        Assert.True(service.LoadFailedFileKept);
        Assert.IsAssignableFrom<IOException>(service.LastLoadError);
        Assert.Equal(before, File.ReadAllBytes(workspace.Paths.SettingsPath));
        Assert.Equal([workspace.Paths.SettingsPath], SettingsFiles(workspace));
    }

    [Fact]
    public void 讀不到又改名失敗之後即使檔案放開了也拒絕儲存_原檔不動()
    {
        using var workspace = new TempWorkspace();
        var service = LoadWhileLocked(workspace, out var settings);
        var before = File.ReadAllBytes(workspace.Paths.SettingsPath);

        Assert.Throws<IOException>(() => service.Save(settings));

        Assert.Equal(before, File.ReadAllBytes(workspace.Paths.SettingsPath));
        Assert.Equal([workspace.Paths.SettingsPath], SettingsFiles(workspace));
    }

    [Fact]
    public void 讀不到又改名失敗之後檔案還開著時也拒絕儲存_不留暫存檔()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);

        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var settings = service.Load();

            Assert.Throws<IOException>(() => service.Save(settings));
        }

        Assert.Equal([workspace.Paths.SettingsPath], SettingsFiles(workspace));
    }

    [Theory]
    [InlineData(AppLanguage.TraditionalChinese)]
    [InlineData(AppLanguage.English)]
    public void 拒絕儲存的訊息依設定的語言說明重新啟動即可(AppLanguage language)
    {
        using var workspace = new TempWorkspace();
        var service = LoadWhileLocked(workspace, out _);

        var exception = Assert.Throws<IOException>(() => service.Save(new AppSettings { Language = language }));

        Assert.Equal(UiText.Of(language).SettingsSaveRefusedFileKept, exception.Message);
    }

    [Fact]
    public void 放開之後新的執行讀回原本的設定_可以照常儲存()
    {
        using var workspace = new TempWorkspace();
        LoadWhileLocked(workspace, out _);

        var restarted = new SettingsService(workspace.Paths.SettingsPath);
        var settings = restarted.Load();

        Assert.Equal(AuthMode.Pat, settings.AuthMode);
        Assert.Equal(AppLanguage.English, settings.Language);
        Assert.Equal(777, settings.LogTailLines);
        Assert.False(restarted.LoadFailedFileKept);
        Assert.Null(restarted.LastLoadError);

        settings.LogTailLines = 300;
        restarted.Save(settings);

        Assert.Equal(300, new SettingsService(workspace.Paths.SettingsPath).Load().LogTailLines);
    }

    [Fact]
    public void 同一個物件放開之後再讀成功就不再拒絕儲存()
    {
        using var workspace = new TempWorkspace();
        var service = LoadWhileLocked(workspace, out _);

        var settings = service.Load();

        Assert.Equal(777, settings.LogTailLines);
        Assert.False(service.LoadFailedFileKept);
        Assert.Null(service.LastLoadError);

        settings.LogTailLines = 300;
        service.Save(settings);

        Assert.Equal(300, new SettingsService(workspace.Paths.SettingsPath).Load().LogTailLines);
    }

    [Fact]
    public void 內容損毀時改名保留_不算留著原檔_可以照常儲存()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, "{ 這不是合法 JSON");

        var settings = service.Load();

        Assert.NotNull(service.LastRecoveryBackupPath);
        Assert.False(service.LoadFailedFileKept);
        Assert.Null(service.LastLoadError);

        service.Save(settings);

        Assert.True(File.Exists(workspace.Paths.SettingsPath));
    }

    /// <summary>FileShare.None 時讀不到（IOException）；FileShare.Read 時讀得到、內容損毀（JsonException），但一樣改不了名。</summary>
    [Theory]
    [InlineData(FileShare.None, typeof(IOException))]
    [InlineData(FileShare.Read, typeof(JsonException))]
    public void 內容損毀又被開著時這次拒絕儲存_下次啟動才改名保留(FileShare share, Type expectedError)
    {
        using var workspace = new TempWorkspace();
        const string broken = "{ 這不是合法 JSON";
        var service = WriteSettings(workspace, broken);

        AppSettings settings;
        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, FileAccess.Read, share))
        {
            settings = service.Load();
        }

        Assert.True(service.LoadFailedFileKept);
        Assert.IsAssignableFrom(expectedError, service.LastLoadError);
        Assert.False(settings.CheckForUpdates);
        Assert.Throws<IOException>(() => service.Save(settings));
        Assert.Equal(broken, File.ReadAllText(workspace.Paths.SettingsPath, Encoding.UTF8));

        var restarted = new SettingsService(workspace.Paths.SettingsPath);
        AssertSpecDefaults(restarted.Load());

        Assert.False(restarted.LoadFailedFileKept);
        Assert.NotNull(restarted.LastRecoveryBackupPath);
        Assert.Equal(broken, File.ReadAllText(restarted.LastRecoveryBackupPath, Encoding.UTF8));
    }

    /// <summary>既有行為：持有者允許刪除時改名成功，健康的檔案也被當成損毀，但有備份與橫幅，不是靜默遺失（docs/behavior.zh-TW.md 例外處理）。</summary>
    [Theory]
    [InlineData(FileAccess.Read, FileShare.Delete)]
    [InlineData(FileAccess.ReadWrite, FileShare.Read | FileShare.Delete)]
    [InlineData(FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)]
    public void 其他程式開著但允許刪除時改名保留_可以儲存(FileAccess access, FileShare share)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);

        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, access, share))
        {
            var settings = service.Load();

            AssertSpecDefaults(settings);
            Assert.False(service.LoadFailedFileKept);
            Assert.NotNull(service.LastRecoveryBackupPath);
            Assert.False(File.Exists(workspace.Paths.SettingsPath));

            service.Save(settings);
        }

        Assert.True(File.Exists(workspace.Paths.SettingsPath));
        Assert.Equal(HealthySettingsJson, File.ReadAllText(service.LastRecoveryBackupPath, Encoding.UTF8));
    }

    [Theory]
    [InlineData(FileAccess.Read, FileShare.Read)]
    [InlineData(FileAccess.Read, FileShare.ReadWrite)]
    [InlineData(FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)]
    public void 其他程式開著但允許讀取時照常讀到設定(FileAccess access, FileShare share)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);

        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, access, share))
        {
            Assert.Equal(777, service.Load().LogTailLines);
        }

        Assert.False(service.LoadFailedFileKept);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    /// <summary>持續性的讀取失敗不能永遠拒絕儲存：改名之後這次就能存，下次啟動讀得到（重新啟動救不了這種情況）。</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void 權限拒絕讀取時照舊改名保留_可以儲存()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);
        var rule = DenyOnSettingsFile(workspace, FileSystemRights.ReadData);

        try
        {
            var settings = service.Load();

            AssertSpecDefaults(settings);
            Assert.False(service.LoadFailedFileKept);
            Assert.NotNull(service.LastRecoveryBackupPath);

            settings.LogTailLines = 300;
            service.Save(settings);

            Assert.Equal(300, new SettingsService(workspace.Paths.SettingsPath).Load().LogTailLines);
        }
        finally
        {
            RemoveDeny(workspace, rule);
        }
    }

    /// <summary>
    /// 權限連改名都拒絕：重新啟動也一樣，拒絕儲存的話設定會永遠存不進去，所以放行；原檔會被覆蓋，只留 error.log 的原因。
    /// 改名另外用不共用刪除的 handle 擋住：行程開著備份與還原權限時（例如從 Git Bash 以系統管理員權限跑），ACL 擋不住改名，覆蓋則兩種情況都做得到。
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void 權限拒絕完全控制時不拒絕儲存_不會永遠存不進去()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, HealthySettingsJson);
        var holder = new FileStream(workspace.Paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var rule = DenyOnSettingsFile(workspace, FileSystemRights.FullControl);

        try
        {
            AppSettings settings;
            using (holder)
            {
                settings = service.Load();
            }

            AssertSpecDefaults(settings);
            Assert.Null(service.LastRecoveryBackupPath);
            Assert.IsType<UnauthorizedAccessException>(service.LastLoadError);
            Assert.False(service.LoadFailedFileKept);

            settings.LogTailLines = 300;
            service.Save(settings);

            Assert.Equal(300, new SettingsService(workspace.Paths.SettingsPath).Load().LogTailLines);
        }
        finally
        {
            RemoveDeny(workspace, rule);
        }
    }

    [Fact]
    public void 正常讀取與設定檔不存在時都不算留著原檔()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Load();

        Assert.False(service.LoadFailedFileKept);

        service.Save(new AppSettings());
        service.Load();

        Assert.False(service.LoadFailedFileKept);
        Assert.Null(service.LastLoadError);
    }

    [Fact]
    public void 欄位缺漏時缺的部分使用預設值()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """{ "includeForks": true, "window": { "width": 1200 } }""");

        var settings = service.Load();

        Assert.True(settings.IncludeForks);
        Assert.Equal(1200, settings.Window.Width);
        Assert.Equal(640, settings.Window.Height);        // 預設值
        Assert.Null(settings.Window.Left);
        Assert.Equal(AuthMode.GhCli, settings.AuthMode);
        Assert.Equal(RefreshMode.Auto, settings.RefreshMode);
        Assert.Equal(12, settings.FailureToastSeconds);
        Assert.True(settings.MinimizeToTray);
        Assert.NotNull(settings.Filters);
        Assert.Empty(settings.Filters.Repos);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 舊設定檔沒有allowWriteActions時是false()
    {
        using var workspace = new TempWorkspace();
        // 第一階段寫的設定檔：沒有這個欄位。
        var service = WriteSettings(workspace, """{ "authMode": "pat", "includeForks": true, "refreshMode": "1m" }""");

        var settings = service.Load();

        Assert.False(settings.AllowWriteActions);
        Assert.Equal(AuthMode.Pat, settings.AuthMode);
        Assert.True(settings.IncludeForks);
        Assert.Equal(RefreshMode.Minute1, settings.RefreshMode);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 舊設定檔沒有檢查新版本欄位時用預設值()
    {
        using var workspace = new TempWorkspace();
        // 這個功能之前寫的設定檔：三個欄位都沒有。
        var service = WriteSettings(workspace, """{ "authMode": "pat", "allowWriteActions": true }""");

        var settings = service.Load();

        Assert.True(settings.CheckForUpdates);
        Assert.Null(settings.LastUpdateCheckUtc);
        Assert.Null(settings.LastNotifiedVersion);
        Assert.Equal(AuthMode.Pat, settings.AuthMode);
        Assert.True(settings.AllowWriteActions);
        Assert.Null(service.LastRecoveryBackupPath);
        Assert.False(service.LoadFailedFileKept);
    }

    [Fact]
    public void UpdateCheckRecord寫入後能往返()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);
        var checkedAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero);
        var settings = new AppSettings { LogTailLines = 300 };

        new UpdateCheckRecord(checkedAt, "0.3.0").ApplyTo(settings);
        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(checkedAt, loaded.LastUpdateCheckUtc);
        Assert.Equal("0.3.0", loaded.LastNotifiedVersion);
        // 只動這兩個欄位。
        Assert.True(loaded.CheckForUpdates);
        Assert.Equal(300, loaded.LogTailLines);

        new UpdateCheckRecord(null, null).ApplyTo(loaded);
        service.Save(loaded);
        var cleared = service.Load();

        Assert.Null(cleared.LastUpdateCheckUtc);
        Assert.Null(cleared.LastNotifiedVersion);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(5001)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void 不合理的log行數會被修正回預設(int stored)
    {
        using var workspace = new TempWorkspace();

        var settings = WriteSettings(workspace, Invariant($$"""{ "logTailLines": {{stored}} }""")).Load();

        Assert.Equal(200, settings.LogTailLines);
    }

    [Fact]
    public void 篩選清單會去掉空白與重複()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """
            {
              "filters": {
                "repos": ["octo-org/sample-app", "", "   ", "octo-org/sample-app", "octo-org/other-app"],
                "branches": ["main", null, "Main", "main", "feature/x"]
              }
            }
            """);

        var filters = service.Load().Filters;

        Assert.Equal(["octo-org/sample-app", "octo-org/other-app"], filters.Repos);
        // 分支名稱分大小寫，Main 與 main 是兩個分支。
        Assert.Equal(["main", "Main", "feature/x"], filters.Branches);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData("\"oauth\"")]
    [InlineData("\"Pat\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{ \"mode\": \"pat\" }")]
    public void 認證方式不認得時回落到預設(string storedJson)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, $$"""{ "authMode": {{storedJson}}, "includeForks": true }""");

        var settings = service.Load();

        Assert.Equal(AuthMode.GhCli, settings.AuthMode);
        // 只有這個欄位回落，整份設定不算損毀。
        Assert.True(settings.IncludeForks);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 篩選清單為null時回落到空清單()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """{ "filters": { "repos": null, "actors": null, "attentionOnly": true } }""");

        var filters = service.Load().Filters;

        Assert.NotNull(filters.Repos);
        Assert.Empty(filters.Repos);
        Assert.NotNull(filters.Actors);
        Assert.Empty(filters.Actors);
        Assert.True(filters.AttentionOnly);
    }

    // ── 計畫指定的新增案例 ──────────────────────────────────────────────

    [Theory]
    [InlineData(RefreshMode.Auto, "auto")]
    [InlineData(RefreshMode.Seconds30, "30s")]
    [InlineData(RefreshMode.Minute1, "1m")]
    [InlineData(RefreshMode.Minutes5, "5m")]
    [InlineData(RefreshMode.Paused, "paused")]
    public void refreshMode字串往返(RefreshMode mode, string json)
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings { RefreshMode = mode });

        Assert.Equal(json, ReadSavedJson(workspace).GetProperty("refreshMode").GetString());
        Assert.Equal(mode, service.Load().RefreshMode);
        // 手寫的字串也讀得懂。
        Assert.Equal(mode, WriteSettings(workspace, $$"""{ "refreshMode": "{{json}}" }""").Load().RefreshMode);
    }

    [Fact]
    public void 暫停時previousRefreshMode不可是paused()
    {
        var settings = new AppSettings
        {
            RefreshMode = RefreshMode.Paused,
            PreviousRefreshMode = RefreshMode.Paused,
        }.Normalized();

        Assert.Equal(RefreshMode.Paused, settings.RefreshMode);
        Assert.Equal(RefreshMode.Auto, settings.PreviousRefreshMode);
    }

    [Theory]
    [InlineData(2, 5, 12, 5)]                          // 失敗提示低於 3
    [InlineData(121, 5, 12, 5)]                        // 失敗提示高於 120
    [InlineData(12, 1, 12, 5)]                         // 成功提示低於 2
    [InlineData(30, 61, 30, 5)]                        // 成功提示高於 60，失敗提示在範圍內保留
    [InlineData(0, 0, 12, 5)]
    [InlineData(-1, -1, 12, 5)]
    [InlineData(int.MaxValue, int.MinValue, 12, 5)]
    public void 提示秒數超出範圍回預設(int failureSeconds, int successSeconds, int expectedFailure, int expectedSuccess)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(
            workspace,
            Invariant($$"""{ "failureToastSeconds": {{failureSeconds}}, "successToastSeconds": {{successSeconds}} }"""));

        var settings = service.Load();

        Assert.Equal(expectedFailure, settings.FailureToastSeconds);
        Assert.Equal(expectedSuccess, settings.SuccessToastSeconds);
    }

    [Theory]
    [InlineData(599, 640, 1000, 640)]
    [InlineData(1000, 399, 1000, 640)]
    [InlineData(599.9, 399.9, 1000, 640)]
    [InlineData(1200, 300, 1200, 640)]                // 寬高各自判斷，合格的那一邊保留
    [InlineData(0, 0, 1000, 640)]
    [InlineData(-800, -600, 1000, 640)]
    public void 視窗尺寸小於下限回預設(double width, double height, double expectedWidth, double expectedHeight)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, Invariant($$"""{ "window": { "width": {{width}}, "height": {{height}} } }"""));

        var window = service.Load().Window;

        Assert.Equal(expectedWidth, window.Width);
        Assert.Equal(expectedHeight, window.Height);
    }

    // ── 邊界與例外 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(3, 2)]
    [InlineData(120, 60)]
    public void 提示秒數剛好在範圍邊界時保留(int failureSeconds, int successSeconds)
    {
        var settings = new AppSettings
        {
            FailureToastSeconds = failureSeconds,
            SuccessToastSeconds = successSeconds,
        }.Normalized();

        Assert.Equal(failureSeconds, settings.FailureToastSeconds);
        Assert.Equal(successSeconds, settings.SuccessToastSeconds);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(5000)]
    public void log行數剛好在範圍邊界時保留(int lines)
    {
        var settings = new AppSettings { LogTailLines = lines }.Normalized();

        Assert.Equal(lines, settings.LogTailLines);
    }

    [Fact]
    public void 視窗尺寸剛好等於下限時保留()
    {
        var window = new AppSettings
        {
            Window = new WindowPlacement { Width = 600, Height = 400 },
        }.Normalized().Window;

        Assert.Equal(600, window.Width);
        Assert.Equal(400, window.Height);
    }

    [Fact]
    public void 視窗數值不是有限數時回預設()
    {
        var window = new AppSettings
        {
            Window = new WindowPlacement
            {
                Left = double.NaN,
                Top = double.PositiveInfinity,
                Width = double.NaN,
                Height = double.PositiveInfinity,
            },
        }.Normalized().Window;

        Assert.Null(window.Left);
        Assert.Null(window.Top);
        Assert.Equal(1000, window.Width);
        Assert.Equal(640, window.Height);
    }

    [Fact]
    public void window與filters為null時回落到預設()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """{ "window": null, "filters": null }""");

        var settings = service.Load();

        AssertSpecDefaults(settings);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData(RefreshMode.Auto, "auto")]
    [InlineData(RefreshMode.Seconds30, "30s")]
    [InlineData(RefreshMode.Minute1, "1m")]
    [InlineData(RefreshMode.Minutes5, "5m")]
    public void previousRefreshMode字串往返(RefreshMode mode, string json)
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings { RefreshMode = RefreshMode.Paused, PreviousRefreshMode = mode });

        Assert.Equal(json, ReadSavedJson(workspace).GetProperty("previousRefreshMode").GetString());
        Assert.Equal(mode, service.Load().PreviousRefreshMode);
    }

    [Fact]
    public void 檔案裡的previousRefreshMode是paused時讀成自動()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """{ "refreshMode": "paused", "previousRefreshMode": "paused" }""");

        var settings = service.Load();

        Assert.Equal(RefreshMode.Paused, settings.RefreshMode);
        Assert.Equal(RefreshMode.Auto, settings.PreviousRefreshMode);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData("\"10s\"")]
    [InlineData("\"Auto\"")]
    [InlineData("\"PAUSED\"")]
    [InlineData("\" auto \"")]
    [InlineData("\"\"")]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{ \"value\": \"paused\" }")]
    [InlineData("[\"paused\"]")]
    public void refreshMode不認得的值回落到自動(string storedJson)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(
            workspace,
            $$"""{ "refreshMode": {{storedJson}}, "previousRefreshMode": {{storedJson}}, "logTailLines": 300 }""");

        var settings = service.Load();

        Assert.Equal(RefreshMode.Auto, settings.RefreshMode);
        Assert.Equal(RefreshMode.Auto, settings.PreviousRefreshMode);
        // 物件或陣列要整個跳過，後面的欄位照常讀到。
        Assert.Equal(300, settings.LogTailLines);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData(AuthMode.GhCli, "ghCli")]
    [InlineData(AuthMode.Pat, "pat")]
    public void authMode字串往返(AuthMode mode, string json)
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings { AuthMode = mode });

        Assert.Equal(json, ReadSavedJson(workspace).GetProperty("authMode").GetString());
        Assert.Equal(mode, service.Load().AuthMode);
    }

    [Fact]
    public void 列舉值超出定義範圍時回預設()
    {
        var settings = new AppSettings
        {
            AuthMode = (AuthMode)99,
            RefreshMode = (RefreshMode)99,
            PreviousRefreshMode = (RefreshMode)(-1),
        }.Normalized();

        Assert.Equal(AuthMode.GhCli, settings.AuthMode);
        Assert.Equal(RefreshMode.Auto, settings.RefreshMode);
        Assert.Equal(RefreshMode.Auto, settings.PreviousRefreshMode);
    }

    [Fact]
    public void 正規化時程式設定的null清單與null物件不會丟例外()
    {
        var settings = new AppSettings
        {
            Window = null!,
            Filters = new FilterPreferences { Repos = null!, Workflows = ["CI", null!, "CI"] },
        }.Normalized();

        Assert.Equal(1000, settings.Window.Width);
        Assert.Empty(settings.Filters.Repos);
        Assert.Equal(["CI"], settings.Filters.Workflows);
    }

    [Fact]
    public void 儲存的JSON使用camelCase與spec的欄位名稱()
    {
        using var workspace = new TempWorkspace();
        new SettingsService(workspace.Paths.SettingsPath).Save(new AppSettings());

        var root = ReadSavedJson(workspace);

        Assert.Equal(
            [
                "allowWriteActions", "alwaysOnTop", "authMode", "checkForUpdates", "failureToastSeconds", "filters", "ignoreGhTokenEnv",
                "includeCollaboratorRepos", "includeForks", "language", "lastNotifiedVersion", "lastUpdateCheckUtc", "logTailLines", "minimizeToTray",
                "notifyMyRunSuccess", "notifyTrackedFailure", "notifyWatchedRunDone",
                "previousRefreshMode", "refreshMode", "runAtStartup", "successToastSeconds", "window",
            ],
            PropertyNames(root));
        Assert.Equal(["height", "left", "maximized", "top", "width"], PropertyNames(root.GetProperty("window")));
        Assert.Equal(
            ["actors", "attentionOnly", "branches", "events", "repos", "statuses", "workflows"],
            PropertyNames(root.GetProperty("filters")));

        Assert.Equal("ghCli", root.GetProperty("authMode").GetString());
        Assert.Equal("auto", root.GetProperty("refreshMode").GetString());
        Assert.Equal("auto", root.GetProperty("previousRefreshMode").GetString());
        Assert.Equal(12, root.GetProperty("failureToastSeconds").GetInt32());
        // 沒有記錄過位置時寫成 null，而不是省略欄位。
        Assert.Equal(JsonValueKind.Null, root.GetProperty("window").GetProperty("left").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("filters").GetProperty("repos").ValueKind);
    }

    [Fact]
    public void 儲存的檔案是UTF8無BOM且中文不跳脫()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings { Filters = new FilterPreferences { Branches = ["功能/登入"] } });

        var bytes = File.ReadAllBytes(workspace.Paths.SettingsPath);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Contains("功能/登入", Encoding.UTF8.GetString(bytes));
        Assert.Equal(["功能/登入"], service.Load().Filters.Branches);
    }

    [Fact]
    public void 有BOM的設定檔也能讀()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(
            workspace.Paths.SettingsPath,
            """{ "includeForks": true }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var service = new SettingsService(workspace.Paths.SettingsPath);

        var settings = service.Load();

        Assert.True(settings.IncludeForks);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 手動編輯留下的註解結尾逗號與不認得的欄位都可以讀()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, """
            {
              // 手動加的註解
              "futureOption": { "nested": [1, 2, 3] },
              "IncludeForks": true,
              "logTailLines": 500,
            }
            """);

        var settings = service.Load();

        Assert.True(settings.IncludeForks);
        Assert.Equal(500, settings.LogTailLines);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"includeForks\": \"yes\" }")]
    [InlineData("{ \"includeForks\": null }")]
    [InlineData("{ \"logTailLines\": 99999999999 }")]
    [InlineData("{ \"logTailLines\": 3.5 }")]
    [InlineData("{ \"window\": 5 }")]
    [InlineData("{ \"filters\": { \"repos\": \"octo-org/sample-app\" } }")]
    [InlineData("{ \"lastUpdateCheckUtc\": \"not-a-date\" }")]
    public void 格式或型別錯誤時改名保留並回落到預設值(string content)
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, content);

        var settings = service.Load();

        AssertSpecDefaults(settings);
        Assert.NotNull(service.LastRecoveryBackupPath);
        Assert.True(File.Exists(service.LastRecoveryBackupPath));
    }

    [Fact]
    public void JSON內容為null時回落到預設值且不視為損毀()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, "null");

        var settings = service.Load();

        AssertSpecDefaults(settings);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 損毀檔的備份檔名用本地時間戳()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.Paths.SettingsPath, "{", Encoding.UTF8);
        // 2026-10-24 02:00:00 UTC = 台北 10:00:00。
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero), ManualTimeProvider.Taipei);
        var service = new SettingsService(workspace.Paths.SettingsPath, time);

        service.Load();

        Assert.Equal(workspace.Paths.SettingsPath + ".broken-20261024100000.bak", service.LastRecoveryBackupPath);
        Assert.True(File.Exists(service.LastRecoveryBackupPath));
    }

    [Fact]
    public void 讀取正常後清掉上一次的備份路徑()
    {
        using var workspace = new TempWorkspace();
        var service = WriteSettings(workspace, "{");
        service.Load();
        Assert.NotNull(service.LastRecoveryBackupPath);

        service.Save(new AppSettings { IncludeForks = true });
        var settings = service.Load();

        Assert.True(settings.IncludeForks);
        Assert.Null(service.LastRecoveryBackupPath);
    }

    [Fact]
    public void 儲存時會先正規化()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings
        {
            LogTailLines = 1,
            PreviousRefreshMode = RefreshMode.Paused,
            Window = new WindowPlacement { Width = 10, Height = 10 },
        });

        var root = ReadSavedJson(workspace);
        Assert.Equal(200, root.GetProperty("logTailLines").GetInt32());
        Assert.Equal("auto", root.GetProperty("previousRefreshMode").GetString());
        Assert.Equal(1000, root.GetProperty("window").GetProperty("width").GetDouble());
        Assert.Equal(640, root.GetProperty("window").GetProperty("height").GetDouble());
    }

    [Fact]
    public void 儲存時會覆寫既有設定檔()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        service.Save(new AppSettings { LogTailLines = 300 });
        service.Save(new AppSettings { LogTailLines = 400 });

        Assert.Equal(400, service.Load().LogTailLines);
        Assert.False(File.Exists(workspace.Paths.SettingsPath + ".tmp"));
    }

    [Fact]
    public void 儲存時資料夾不存在會自動建立()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "nested", "deeper", "settings.json");
        var service = new SettingsService(path);

        service.Save(new AppSettings { IncludeForks = true });

        Assert.True(File.Exists(path));
        Assert.True(service.Load().IncludeForks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 建構子拒絕空白路徑(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SettingsService(path!));
    }

    [Fact]
    public void 儲存null設定時丟出例外()
    {
        using var workspace = new TempWorkspace();
        var service = new SettingsService(workspace.Paths.SettingsPath);

        Assert.Throws<ArgumentNullException>(() => service.Save(null!));
        Assert.False(File.Exists(workspace.Paths.SettingsPath));
    }

    // ── 輔助 ───────────────────────────────────────────────────────────

    /// <summary>和預設值都不同，讀回預設值就看得出來。</summary>
    private const string HealthySettingsJson = """{ "authMode": "pat", "language": "en", "logTailLines": 777 }""";

    /// <summary>設定檔被其他程式開著（不共用）時讀一次，離開時已經放開。</summary>
    private static SettingsService LoadWhileLocked(TempWorkspace workspace, out AppSettings settings)
    {
        var service = WriteSettings(workspace, HealthySettingsJson);
        using (new FileStream(workspace.Paths.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = service.Load();
        }

        Assert.True(service.LoadFailedFileKept);
        return service;
    }

    private static string[] SettingsFiles(TempWorkspace workspace) =>
        Directory.GetFiles(workspace.Root, Path.GetFileName(workspace.Paths.SettingsPath) + "*");

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule DenyOnSettingsFile(TempWorkspace workspace, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        var file = new FileInfo(workspace.Paths.SettingsPath);
        var security = file.GetAccessControl();
        security.AddAccessRule(rule);
        file.SetAccessControl(security);
        return rule;
    }

    /// <summary>ACL 跟著檔案走：備份檔也要拿掉，暫存資料夾才清得掉。</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveDeny(TempWorkspace workspace, FileSystemAccessRule rule)
    {
        foreach (var path in SettingsFiles(workspace))
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.RemoveAccessRule(rule);
            file.SetAccessControl(security);
        }
    }

    /// <summary>數字照 JSON 格式寫（小數點、負號），不受目前文化設定影響。</summary>
    private static string Invariant(FormattableString json) => FormattableString.Invariant(json);

    private static SettingsService WriteSettings(TempWorkspace workspace, string json)
    {
        File.WriteAllText(workspace.Paths.SettingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new SettingsService(workspace.Paths.SettingsPath);
    }

    private static JsonElement ReadSavedJson(TempWorkspace workspace)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsPath, Encoding.UTF8));
        return document.RootElement.Clone();
    }

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

    /// <summary>spec §14.2 的預設值。</summary>
    /// <param name="checkForUpdates">讀不到設定檔而且原檔留著（<see cref="SettingsService.LoadFailedFileKept"/>）時是 false，其他情況是預設的 true。</param>
    private static void AssertSpecDefaults(AppSettings settings, bool checkForUpdates = true)
    {
        Assert.Equal(AuthMode.GhCli, settings.AuthMode);
        Assert.False(settings.IgnoreGhTokenEnv);
        Assert.False(settings.IncludeForks);
        Assert.False(settings.IncludeCollaboratorRepos);
        Assert.Equal(RefreshMode.Auto, settings.RefreshMode);
        Assert.Equal(RefreshMode.Auto, settings.PreviousRefreshMode);
        Assert.True(settings.NotifyTrackedFailure);
        Assert.True(settings.NotifyMyRunSuccess);
        Assert.True(settings.NotifyWatchedRunDone);
        Assert.Equal(12, settings.FailureToastSeconds);
        Assert.Equal(5, settings.SuccessToastSeconds);
        Assert.Equal(200, settings.LogTailLines);
        Assert.False(settings.AlwaysOnTop);
        Assert.False(settings.RunAtStartup);
        Assert.True(settings.MinimizeToTray);
        Assert.False(settings.AllowWriteActions);
        Assert.Equal(checkForUpdates, settings.CheckForUpdates);
        Assert.Null(settings.LastUpdateCheckUtc);
        Assert.Null(settings.LastNotifiedVersion);
        Assert.NotNull(settings.Window);
        Assert.Null(settings.Window.Left);
        Assert.Null(settings.Window.Top);
        Assert.Equal(1000, settings.Window.Width);
        Assert.Equal(640, settings.Window.Height);
        Assert.False(settings.Window.Maximized);
        Assert.NotNull(settings.Filters);
        Assert.Empty(settings.Filters.Repos);
        Assert.Empty(settings.Filters.Workflows);
        Assert.Empty(settings.Filters.Events);
        Assert.Empty(settings.Filters.Statuses);
        Assert.Empty(settings.Filters.Branches);
        Assert.Empty(settings.Filters.Actors);
        Assert.False(settings.Filters.AttentionOnly);
    }
}

using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core;

/// <summary>集中管理資料落地位置。測試時傳入暫存目錄即可完全隔離。</summary>
public sealed class AppPaths
{
    public const string FolderName = "IsItGreenYet";

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public string CacheDbPath => Path.Combine(RootDirectory, "cache.db");

    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string ErrorLogPath => Path.Combine(RootDirectory, "error.log");

    /// <summary>正式執行時的位置：%LOCALAPPDATA%\IsItGreenYet。</summary>
    public static AppPaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName));

    /// <summary>建立資料夾。無權限或路徑不可用時丟出訊息明確的例外，不靜默失敗；訊息用共用的目前語言（<see cref="UiText.Current"/>）。</summary>
    public void EnsureCreated() => EnsureCreated(UiText.Current);

    /// <param name="language">例外訊息（App 直接顯示給使用者）的語言。</param>
    public void EnsureCreated(AppLanguage language)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                UiText.Format(UiText.Of(language).StartupFolderFailed, RootDirectory, ex.Message), ex);
        }
    }
}

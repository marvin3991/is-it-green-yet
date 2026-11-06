using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Services;

/// <summary>
/// 讀寫 settings.json（同 SAM）。設定檔壞掉絕不能讓程式開不起來，
/// 所以讀取失敗一律回落到預設值：改得了名就改名保留原檔；改名也失敗而且檔案還在（例如其他程式開著）時，
/// 原檔不動、這次拒絕寫入（<see cref="LoadFailedFileKept"/>），免得拿預設值蓋掉使用者的設定。
/// </summary>
public sealed class SettingsService
{
    /// <summary>損毀備份檔名裡的時間戳（同 SAM），用本地時間方便對照。</summary>
    private const string BackupTimestampFormat = "yyyyMMddHHmmss";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            SettingsJsonConverters.RefreshModeConverter,
            SettingsJsonConverters.AuthModeConverter,
            SettingsJsonConverters.AppLanguageConverter,
        },
    };

    private readonly string _filePath;
    private readonly TimeProvider _time;
    private readonly CultureInfo _displayCulture;

    /// <summary>第一次執行的預設語言依目前執行緒的 CurrentUICulture（行程啟動時就是 Windows 顯示語言）。</summary>
    public SettingsService(string filePath)
        : this(filePath, TimeProvider.System, CultureInfo.CurrentUICulture)
    {
    }

    /// <param name="displayCulture">
    /// Windows 顯示語言：沒有 settings.json（第一次執行）時用它決定預設語言（<see cref="AppLanguages.FromDisplayCulture"/>）。
    /// App 在改掉 UI 執行緒的 CurrentUICulture 之前先記下來傳進來。
    /// </param>
    public SettingsService(string filePath, CultureInfo displayCulture)
        : this(filePath, TimeProvider.System, displayCulture)
    {
    }

    /// <summary>指定時間來源，讓測試能預期損毀備份的檔名。</summary>
    internal SettingsService(string filePath, TimeProvider time)
        : this(filePath, time, CultureInfo.CurrentUICulture)
    {
    }

    internal SettingsService(string filePath, TimeProvider time, CultureInfo displayCulture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(displayCulture);
        _filePath = filePath;
        _time = time;
        _displayCulture = displayCulture;
    }

    /// <summary>上一次 Load 因設定檔損毀而改名保留的路徑；正常讀取時為 null。</summary>
    public string? LastRecoveryBackupPath { get; private set; }

    /// <summary>上一次 Load 讀不到設定檔、改名也失敗而且檔案還在時的原因；其他情況為 null。</summary>
    public Exception? LastLoadError { get; private set; }

    /// <summary>
    /// 上一次 Load 回的是預設值，原檔還留著沒動（原因見 <see cref="LastLoadError"/>）：這時 <see cref="Save"/> 一律拒絕，
    /// 否則檔案一放開，任何一次儲存都會拿預設值蓋掉使用者的設定。佔用的程式放開之後，重新啟動就恢復。
    /// 只在這種情況拒絕，持續性的失敗不能拒絕，否則設定會永遠存不進去：改名成功時照常儲存；
    /// 權限連改名都拒絕（<see cref="UnauthorizedAccessException"/>，例如拒絕完全控制）時重新啟動也一樣，也照常儲存，原檔會被覆蓋。
    /// </summary>
    public bool LoadFailedFileKept => LastLoadError is not (null or UnauthorizedAccessException);

    public AppSettings Load()
    {
        LastRecoveryBackupPath = null;
        LastLoadError = null;

        if (!File.Exists(_filePath))
        {
            // 第一次執行：語言依 Windows 顯示語言。設定檔已經存在（包括損毀、改名保留）時是既有使用者，語言的預設是繁體中文。
            return new AppSettings { Language = AppLanguages.FromDisplayCulture(_displayCulture) };
        }

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            return (settings ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastRecoveryBackupPath = TryBackupBrokenFile();
            if (LastRecoveryBackupPath is null && File.Exists(_filePath))
            {
                LastLoadError = ex;
            }

            // 原檔還留著時讀不到使用者的選擇，用預設值送請求可能違反「關閉後完全不送」，所以這次執行不檢查新版本。
            // 其他失敗維持預設開啟：權限連改名都拒絕時，之後的 Save 會覆蓋檔案，設成關閉就永久關掉了；內容損毀已改名時有橫幅也有備份。
            return new AppSettings { CheckForUpdates = !LoadFailedFileKept };
        }
    }

    /// <summary>
    /// 只讀出介面語言，不改動任何檔案（損毀時也不改名）：App 在建立資料夾、開啟快取資料庫之前就要顯示錯誤時用它決定語言。
    /// 結果和之後 <see cref="Load"/> 讀到的語言相同：沒有設定檔時依 Windows 顯示語言；讀不到、格式錯誤、沒有欄位或不認得的值時是繁體中文。
    /// 和 Load 一樣整份反序列化：語言欄位正確、其他欄位型別錯誤時，Load 視為損毀、回到預設，這裡也回繁體中文
    /// （只讀語言欄位時會回英文，最早期的錯誤訊息就和之後的畫面不同語言）。
    /// </summary>
    public AppLanguage PeekLanguage()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppLanguages.FromDisplayCulture(_displayCulture);
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return AppLanguages.Normalize(JsonSerializer.Deserialize<AppSettings>(json, Options)?.Language ?? AppLanguages.Default);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return AppLanguages.Default;
        }
    }

    /// <summary>先寫暫存檔再置換，避免寫到一半當機留下半截設定檔。</summary>
    /// <exception cref="IOException">
    /// <see cref="LoadFailedFileKept"/> 成立時丟出，不碰任何檔案；訊息是 <paramref name="settings"/> 的語言，可以直接顯示給使用者。
    /// </exception>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (LoadFailedFileKept)
        {
            throw new IOException(UiText.Of(settings.Language).SettingsSaveRefusedFileKept);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(settings.Normalized(), Options);

        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private string? TryBackupBrokenFile()
    {
        try
        {
            // 固定用 InvariantCulture，避免非西曆的文化設定把年份寫成別的曆法。
            var stamp = _time.GetLocalNow().ToString(BackupTimestampFormat, CultureInfo.InvariantCulture);
            var backupPath = $"{_filePath}.broken-{stamp}.bak";
            File.Move(_filePath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 連改名都失敗就算了，回落到預設值比擋住啟動重要。
            return null;
        }
    }
}

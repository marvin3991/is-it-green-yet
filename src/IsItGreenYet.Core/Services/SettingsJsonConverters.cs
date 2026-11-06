using System.Text.Json;
using System.Text.Json.Serialization;
using IsItGreenYet.Core.Localization;
using IsItGreenYet.Core.Models;

namespace IsItGreenYet.Core.Services;

/// <summary>
/// settings.json 裡列舉欄位的字串值（spec §14.2）。
/// 讀到不認得的值（拼錯、大小寫不同、數字、null、物件或陣列）一律回落到預設值、不丟例外：
/// 一個欄位寫錯，不該讓整份設定被當成損毀而全部回到預設。
/// </summary>
internal static class SettingsJsonConverters
{
    public static readonly JsonConverter<RefreshMode> RefreshModeConverter = new SpecStringEnumConverter<RefreshMode>(
        RefreshMode.Auto,
        (RefreshMode.Auto, "auto"),
        (RefreshMode.Seconds30, "30s"),
        (RefreshMode.Minute1, "1m"),
        (RefreshMode.Minutes5, "5m"),
        (RefreshMode.Paused, "paused"));

    public static readonly JsonConverter<AuthMode> AuthModeConverter = new SpecStringEnumConverter<AuthMode>(
        AuthMode.GhCli,
        (AuthMode.GhCli, "ghCli"),
        (AuthMode.Pat, "pat"));

    /// <summary>介面語言：不認得的值（例如 <c>zh-TW</c>、<c>EN</c>）回落到繁體中文，同沒有這個欄位的既有使用者。</summary>
    public static readonly JsonConverter<AppLanguage> AppLanguageConverter = new SpecStringEnumConverter<AppLanguage>(
        AppLanguages.Default,
        (AppLanguage.TraditionalChinese, AppLanguages.TraditionalChineseCode),
        (AppLanguage.English, AppLanguages.EnglishCode));
}

/// <summary>列舉與 spec 字串值一對一轉換；字串比對用 Ordinal，只認 spec 寫的拼法。</summary>
internal sealed class SpecStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly TEnum _fallback;
    private readonly (TEnum Value, string Json)[] _map;

    public SpecStringEnumConverter(TEnum fallback, params (TEnum Value, string Json)[] map)
    {
        _fallback = fallback;
        _map = map;
    }

    /// <summary>JSON 的 null 也交給 Read，才能回落到預設值而不是丟例外。</summary>
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // 物件或陣列要整個跳過，後面的欄位才讀得到；數字、布林、null 跳過時不動。
            reader.Skip();
            return _fallback;
        }

        var text = reader.GetString();
        foreach (var (value, json) in _map)
        {
            if (string.Equals(text, json, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return _fallback;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // 未定義的列舉值寫成預設值的字串，避免寫出下次讀不懂的內容。
        writer.WriteStringValue(JsonOf(value) ?? JsonOf(_fallback));
    }

    private string? JsonOf(TEnum value)
    {
        foreach (var (candidate, json) in _map)
        {
            if (EqualityComparer<TEnum>.Default.Equals(candidate, value))
            {
                return json;
            }
        }

        return null;
    }
}

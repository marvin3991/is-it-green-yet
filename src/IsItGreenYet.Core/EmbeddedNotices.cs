using System.Reflection;
using System.Text;

namespace IsItGreenYet.Core;

/// <summary>
/// 內嵌在這個組件裡的 LICENSE 與第三方聲明（repo 根目錄的檔案，見 IsItGreenYet.Core.csproj 的 EmbeddedResource）：
/// 只下載或轉傳 exe 的人也看得到。App 的授權對話框與命令列的 <c>notices</c> 共用。
/// </summary>
/// <remarks>
/// 換行一律換成目前平台的換行：資源的換行跟著建置時的工作目錄（Windows 的 git 可能是 CRLF，也可能是 LF）。
/// 讀不到時丟 <see cref="IOException"/>，不回傳殘缺的內容；怎麼告訴使用者由呼叫端決定。
/// </remarks>
public static class EmbeddedNotices
{
    /// <summary>LICENSE 的資源名稱（csproj 的 LogicalName）。</summary>
    internal const string LicenseResource = "IsItGreenYet.Core.Legal.LICENSE";

    /// <summary>THIRD-PARTY-NOTICES.md 的資源名稱（csproj 的 LogicalName）。</summary>
    internal const string ThirdPartyNoticesResource = "IsItGreenYet.Core.Legal.THIRD-PARTY-NOTICES.md";

    /// <summary><see cref="ReadAll"/> 用來隔開兩份文字的分隔線（純 ASCII）。</summary>
    internal const string Divider = "================================================================";

    /// <summary>不是合法 UTF-8 時丟例外，不用替代字元蓋過去。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <exception cref="IOException">讀不到資源，或內容不能用。</exception>
    public static string ReadLicense() => ReadFrom(typeof(EmbeddedNotices).Assembly, LicenseResource);

    /// <exception cref="IOException">讀不到資源，或內容不能用。</exception>
    public static string ReadThirdPartyNotices() => ReadFrom(typeof(EmbeddedNotices).Assembly, ThirdPartyNoticesResource);

    /// <summary>LICENSE、分隔線、第三方聲明，依序接起來的全文。</summary>
    /// <exception cref="IOException">讀不到資源，或內容不能用。</exception>
    public static string ReadAll()
    {
        var newLine = Environment.NewLine;
        return ReadLicense() + newLine + Divider + newLine + newLine + ReadThirdPartyNotices();
    }

    /// <exception cref="IOException">組件裡沒有這個資源，或內容不能用。</exception>
    internal static string ReadFrom(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new IOException($"找不到內嵌資源 {resourceName}。");
        return Decode(stream, resourceName);
    }

    /// <summary>照 UTF-8 讀出來（開頭的 BOM 不算內容），換行換成目前平台的換行。</summary>
    /// <exception cref="IOException">不是合法的 UTF-8，或內容是空的。</exception>
    internal static string Decode(Stream stream, string resourceName)
    {
        string text;
        try
        {
            using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException($"內嵌資源 {resourceName} 不是合法的 UTF-8。", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new IOException($"內嵌資源 {resourceName} 是空的。");
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}

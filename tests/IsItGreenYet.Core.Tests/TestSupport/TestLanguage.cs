using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using IsItGreenYet.Core.Localization;

namespace IsItGreenYet.Core.Tests.TestSupport;

/// <summary>
/// 測試組件一載入就把共用的目前語言設成繁體中文：既有的測試斷言的是中文（spec 原文），照原本的寫法（沒有語言參數的多載）跑。
/// 英文的測試一律明確傳入語言，不改這個值；要驗證「沒有語言參數的多載跟著目前語言」的測試放在不平行的集合裡，結束時改回來
/// （<see cref="CurrentLanguageCollection"/>）。
/// </summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "測試組件：所有測試開始前就要固定共用的目前語言，不依賴這台電腦的顯示語言。")]
    internal static void UseTraditionalChinese() => UiText.Current = AppLanguage.TraditionalChinese;
}

/// <summary>會改共用目前語言（<see cref="UiText.Current"/>）的測試：不和其他測試同時跑（xUnit 在所有平行的測試跑完之後才跑這個集合）。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentLanguageCollection
{
    public const string Name = "共用的目前語言";
}

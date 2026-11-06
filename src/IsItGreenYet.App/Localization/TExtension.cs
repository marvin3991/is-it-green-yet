using System.Windows.Data;
using System.Windows.Markup;

namespace IsItGreenYet.App.Localization;

/// <summary>
/// XAML 的畫面文字：<c>{l:T 名稱}</c> 繫結到目前語言的字串表（<see cref="Loc.Table"/> 的同名項目，名稱見 Core 的 TextTable）。
/// 換語言時（<see cref="Loc.Apply"/>）即時更新。用在樣式的 Setter 時回傳 Binding 本身，由 WPF 套到目標上。
/// </summary>
/// <remarks>名稱寫錯時畫面是空的，Core 測試專案的 AppXamlTextTests 會掃描所有 XAML 擋下來。</remarks>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key)
    {
        Key = key;
    }

    /// <summary>字串表的項目名稱，例如 <c>CommonClose</c>。</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Table) + "." + Key)
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

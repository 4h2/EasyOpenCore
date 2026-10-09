using System.Windows.Data;
using System.Windows.Markup;
using EasyOpenCore.Core.Localization;

namespace EasyOpenCore.App.Localization;

/// <summary>
/// XAML: <c>Text="{l:Tr nav.overview}"</c>. Binds to <see cref="Loc"/>'s indexer so the text
/// updates live when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

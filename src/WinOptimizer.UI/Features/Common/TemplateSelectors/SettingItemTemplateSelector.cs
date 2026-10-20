using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Common.TemplateSelectors;

/// <summary>
/// Selects between the regular SettingItemTemplate and the SettingExpanderItemTemplate
/// based on whether the setting has children.
/// </summary>
public partial class SettingItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RegularTemplate { get; set; }
    public DataTemplate? ExpanderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is SettingItemViewModel vm && vm.IsParentSetting)
            return ExpanderTemplate;
        return RegularTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

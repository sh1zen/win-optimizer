using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Displays structured reports inline while keeping ordinary scan findings compact.</summary>
public sealed partial class ResultDetailTemplateSelector : DataTemplateSelector
{
    public DataTemplate CompactTemplate { get; set; } = null!;
    public DataTemplate ReportTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is ISelectableResultItem { Report: not null } ? ReportTemplate : CompactTemplate;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

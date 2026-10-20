using System.Collections;
using CommunityToolkit.WinUI.Collections;
using WinOptimizer.UI.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

/// <summary>
/// Applies identical ordering and tie breakers to tables and grouped app views.
/// </summary>
internal static class AppSortHelper
{
    public static void ApplySortDescriptions(AdvancedCollectionView view, AppSortMode mode)
    {
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(SortDirection.Ascending, new AppComparer(mode)));
        }
    }

    public static IEnumerable<AppItemViewModel> Order(IEnumerable<AppItemViewModel> source, AppSortMode mode) =>
        source.OrderBy(item => item, new AppComparer(mode));

    /// <summary>Sorts nullable measurements descending, keeping unknown values last.</summary>
    private sealed class AppComparer(AppSortMode mode) : IComparer, IComparer<AppItemViewModel>
    {
        int IComparer.Compare(object? x, object? y) => Compare((AppItemViewModel?)x, (AppItemViewModel?)y);

        public int Compare(AppItemViewModel? x, AppItemViewModel? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            var comparison = mode switch
            {
                AppSortMode.GroupAsc => StringComparer.CurrentCultureIgnoreCase.Compare(x.SortGroupName, y.SortGroupName),
                AppSortMode.InstallationDate => Nullable.Compare(y.InstallationDate, x.InstallationDate),
                AppSortMode.Size => Nullable.Compare(y.SizeBytes, x.SizeBytes),
                AppSortMode.LastUsageTime => Nullable.Compare(y.LastUsageTime, x.LastUsageTime),
                _ => 0,
            };
            if (comparison != 0) return comparison;
            comparison = StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name);
            return comparison != 0 ? comparison : StringComparer.OrdinalIgnoreCase.Compare(x.Id, y.Id);
        }
    }
}

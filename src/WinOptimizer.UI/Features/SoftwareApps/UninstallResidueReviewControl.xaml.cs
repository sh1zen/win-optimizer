using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps;

/// <summary>Hierarchical registry/path review with a lifetime guard during selected-resource mutations.</summary>
public sealed partial class UninstallResidueReviewControl : UserControl, ICustomDialogContentState
{
    public UninstallResidueViewModel ViewModel { get; }
    public bool CanClose => ViewModel.CanClose;
    public UninstallResidueReviewControl(UninstallResidueViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}

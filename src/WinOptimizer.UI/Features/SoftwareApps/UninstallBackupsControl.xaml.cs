using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps;

/// <summary>Persistent selected-backup recovery with an explicit confirmation and mutation close guard.</summary>
public sealed partial class UninstallBackupsControl : UserControl, ICustomDialogContentState
{
    public UninstallBackupsViewModel ViewModel { get; }
    public bool CanClose => ViewModel.CanClose;
    public UninstallBackupsControl(UninstallBackupsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}

using Microsoft.UI.Xaml.Media;

namespace WinOptimizer.UI.Features.Customize.Interfaces;

/// <summary>Reads a local program's shell artwork without requiring app inventory or a network lookup.</summary>
public interface IProgramIconSource
{
    Task<ImageSource?> LoadAsync(string path);
}

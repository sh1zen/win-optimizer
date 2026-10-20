namespace WinOptimizer.Application.Configuration;

/// <summary>Exposes configuration file dialogs without a window or UI framework dependency.</summary>
public interface IConfigurationFilePicker
{
    bool IsAvailable { get; }
    string? OpenFile(string title, string filterName, string filterPattern, string? initialDirectory = null);
    string? SaveFile(string title, string filterName, string filterPattern, string defaultFileName, string defaultExtension);
}

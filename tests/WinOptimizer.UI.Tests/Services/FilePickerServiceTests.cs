using FluentAssertions;
using Moq;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>
/// Verifies that file, folder and save pickers return without opening a dialog
/// when no main window is available.
/// </summary>
public class FilePickerServiceTests
{
    private readonly Mock<IMainWindowProvider> _mockMainWindowProvider = new();

    private FilePickerService CreateSut()
    {
        return new FilePickerService(_mockMainWindowProvider.Object);
    }

    [Fact]
    public void PickFile_WhenMainWindowIsNull_ReturnsNull()
    {
        _mockMainWindowProvider.Setup(p => p.MainWindow).Returns((Microsoft.UI.Xaml.Window?)null);

        var sut = CreateSut();

        var result = sut.PickFile(new[] { "XML Files", "*.xml" });

        result.Should().BeNull();
    }

    [Fact]
    public void PickFolder_WhenMainWindowIsNull_ReturnsNull()
    {
        _mockMainWindowProvider.Setup(p => p.MainWindow).Returns((Microsoft.UI.Xaml.Window?)null);

        var sut = CreateSut();

        var result = sut.PickFolder();

        result.Should().BeNull();
    }

    [Fact]
    public void PickSaveFile_WhenMainWindowIsNull_ReturnsNull()
    {
        _mockMainWindowProvider.Setup(p => p.MainWindow).Returns((Microsoft.UI.Xaml.Window?)null);

        var sut = CreateSut();

        var result = sut.PickSaveFile(new[] { "XML Files", "*.xml" });

        result.Should().BeNull();
    }
}

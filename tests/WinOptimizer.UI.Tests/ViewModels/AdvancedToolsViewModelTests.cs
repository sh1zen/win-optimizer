using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class AdvancedToolsViewModelTests
{
    private readonly Mock<ILocalizationService> _mockLocalization = new();

    public AdvancedToolsViewModelTests()
    {
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
    }

    private AdvancedToolsViewModel CreateViewModel()
    {
        return new AdvancedToolsViewModel(_mockLocalization.Object);
    }

    [Fact]
    public void PageDestinations_UseExistingLocalizedTitlesAndDescriptions()
    {
        using var vm = CreateViewModel();
        vm.RestoreDisplayName.Should().Be("Nav_Restore");
        vm.RestoreDescription.Should().Be("Restore_Description");
        vm.SystemInformationDisplayName.Should().Be("Nav_SystemInformation");
        vm.SystemInformationDescription.Should().Be("SystemInfo_Description");
    }

    [Fact]
    public void PageDestinations_UseFallbackTitlesWhenLocalizationReturnsNull()
    {
        _mockLocalization.Setup(l => l.GetString(It.IsAny<string>())).Returns((string?)null!);
        using var vm = CreateViewModel();
        vm.RestoreDisplayName.Should().Be("Restore");
        vm.SystemInformationDisplayName.Should().Be("System information");
    }
}

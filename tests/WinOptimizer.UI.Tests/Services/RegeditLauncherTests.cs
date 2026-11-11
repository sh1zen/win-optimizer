using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Utilities;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class RegeditLauncherTests
{
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<ILogService> _mockLogService = new();

    private RegeditLauncher CreateSut()
    {
        return new RegeditLauncher(
            _mockInteractiveUserService.Object,
            _mockProcessExecutor.Object,
            _mockLogService.Object);
    }

    // -------------------------------------------------------
    // KeyExists - basic parsing
    // -------------------------------------------------------

    [Fact]
    public void KeyExists_WithPathWithoutBackslash_ReturnsFalse()
    {
        var sut = CreateSut();

        var result = sut.KeyExists("HKLM");

        result.Should().BeFalse();
    }

    [Fact]
    public void KeyExists_WithEmptyString_ReturnsFalse()
    {
        var sut = CreateSut();

        var result = sut.KeyExists("");

        result.Should().BeFalse();
    }

    [Fact]
    public void KeyExists_WithUnknownHive_ReturnsFalse()
    {
        var sut = CreateSut();

        var result = sut.KeyExists(@"HKEY_INVALID\SOFTWARE\Test");

        result.Should().BeFalse();
    }

    [Fact]
    public void KeyExists_WithNonExistentPath_ReturnsFalse()
    {
        var sut = CreateSut();

        var result = sut.KeyExists(@"HKLM\SOFTWARE\NonExistentKeyThatShouldNeverExist_12345");

        result.Should().BeFalse();
    }
}

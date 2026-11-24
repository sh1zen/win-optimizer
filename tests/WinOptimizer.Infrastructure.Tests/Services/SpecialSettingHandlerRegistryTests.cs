// File: tests/WinOptimizer.Infrastructure.Tests/Services/SpecialSettingHandlerRegistryTests.cs
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class SpecialSettingHandlerRegistryTests
{
    [Fact]
    public void TryGet_ResolvesHandlersByIdAndReturnsNullForAnUnknownId()
    {
        var power = new Mock<ISpecialSettingHandler>().Object;
        var update = new Mock<ISpecialSettingHandler>().Object;
        var sut = new SpecialSettingHandlerRegistry(new Dictionary<string, ISpecialSettingHandler>
        {
            ["power-plan-selection"] = power,
            ["updates-policy-mode"] = update,
        });

        sut.TryGet("power-plan-selection").Should().BeSameAs(power);
        sut.TryGet("updates-policy-mode").Should().BeSameAs(update);
        sut.TryGet("nope").Should().BeNull();
    }
}

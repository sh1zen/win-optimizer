using FluentAssertions;
using WinOptimizer.UI.Features.Common.Utilities;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class UnitConversionHelperTests
{
    [Fact]
    public void ConvertFromSystemUnits_Minutes_IntegerDivisionTruncates()
    {
        // 59 / 60 = 0 (integer division)
        var result = UnitConversionHelper.ConvertFromSystemUnits(59, "minutes");

        result.Should().Be(0);
    }

    [Fact]
    public void ConvertFromSystemUnits_Hours_IntegerDivisionTruncates()
    {
        // 3599 / 3600 = 0 (integer division)
        var result = UnitConversionHelper.ConvertFromSystemUnits(3599, "hours");

        result.Should().Be(0);
    }
}

using FluentAssertions;
using WinOptimizer.UI.Features.Common.Utilities;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class UnitConversionHelperTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 1)]
    [InlineData(120, 2)]
    [InlineData(300, 5)]
    [InlineData(3600, 60)]
    public void ConvertFromSystemUnits_Minutes_VariousValues(int systemValue, int expected)
    {
        var result = UnitConversionHelper.ConvertFromSystemUnits(systemValue, "minutes");

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3600, 1)]
    [InlineData(7200, 2)]
    [InlineData(36000, 10)]
    public void ConvertFromSystemUnits_Hours_VariousValues(int systemValue, int expected)
    {
        var result = UnitConversionHelper.ConvertFromSystemUnits(systemValue, "hours");

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(100, 100)]
    [InlineData(1000, 1000)]
    public void ConvertFromSystemUnits_Milliseconds_VariousValues(int systemValue, int expected)
    {
        var result = UnitConversionHelper.ConvertFromSystemUnits(systemValue, "milliseconds");

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("seconds")]
    [InlineData("bytes")]
    [InlineData("percent")]
    [InlineData("unknown")]
    public void ConvertFromSystemUnits_UnsupportedUnits_PassThrough(string units)
    {
        var result = UnitConversionHelper.ConvertFromSystemUnits(99, units);

        result.Should().Be(99);
    }

    [Theory]
    [InlineData("MINUTES", 120, 2)]
    [InlineData("Minutes", 120, 2)]
    [InlineData("mInUtEs", 120, 2)]
    [InlineData("HOURS", 3600, 1)]
    [InlineData("Hours", 3600, 1)]
    [InlineData("MILLISECONDS", 5, 5)]
    [InlineData("Milliseconds", 5, 5)]
    public void ConvertFromSystemUnits_CaseInsensitive(string units, int systemValue, int expected)
    {
        var result = UnitConversionHelper.ConvertFromSystemUnits(systemValue, units);

        result.Should().Be(expected);
    }

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

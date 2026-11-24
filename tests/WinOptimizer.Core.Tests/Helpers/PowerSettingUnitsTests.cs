using FluentAssertions;
using WinOptimizer.Core.Features.Common.Helpers;
using Xunit;

namespace WinOptimizer.Core.Tests.Helpers;

public class PowerSettingUnitsTests
{
    [Theory]
    [InlineData(59, "minutes")]
    [InlineData(3599, "hours")]
    public void ToDisplayValue_TruncatesFractionalUnits(int systemValue, string displayUnits)
    {
        PowerSettingUnits.ToDisplayValue(systemValue, displayUnits).Should().Be(0);
    }
}

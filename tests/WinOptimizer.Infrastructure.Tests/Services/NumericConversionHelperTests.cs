using FluentAssertions;
using WinOptimizer.Infrastructure.Features.Common.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class NumericConversionHelperTests
{

    [Fact]
    public void ConvertNumericValue_StringNonNumeric_ThrowsArgument()
    {
        var action = () => NumericConversionHelper.ConvertNumericValue("abc");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ConvertNumericValue_UnsupportedType_ThrowsArgument()
    {
        var action = () => NumericConversionHelper.ConvertNumericValue(new object());
        action.Should().Throw<ArgumentException>();
    }
}

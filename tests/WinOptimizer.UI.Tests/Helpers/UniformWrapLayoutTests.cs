using FluentAssertions;
using WinOptimizer.UI.Features.Common.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Helpers;

public sealed class UniformWrapLayoutTests
{
    [Theory]
    [InlineData(240, 1)]
    [InlineData(611.9, 1)]
    [InlineData(612, 2)]
    [InlineData(923.9, 2)]
    [InlineData(924, 3)]
    [InlineData(1052, 3)]
    [InlineData(1052.3, 3)]
    [InlineData(1240, 3)]
    [InlineData(1800, 3)]
    public void Stretching_FillsEveryRowAcrossResizeBoundaries(double width, int expectedColumns)
    {
        var layout = UniformWrapLayout.Calculate(width, 300, 12, 6, stretchItems: true, maxColumns: 3);
        layout.Columns.Should().Be(expectedColumns);
        (layout.Columns * layout.CellWidth + (layout.Columns - 1) * 12).Should().BeApproximately(width, 0.000001);
        layout.CellWidth.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(300, 1)]
    [InlineData(692, 2)]
    [InlineData(1044, 3)]
    [InlineData(1400, 4)]
    public void FixedCards_KeepTheirExistingWidthAndColumnCount(double width, int expectedColumns)
    {
        // The column limit applies only to the opt-in stretching mode.
        var layout = UniformWrapLayout.Calculate(width, 340, 12, 6, stretchItems: false, maxColumns: 1);
        layout.Columns.Should().Be(expectedColumns);
        layout.CellWidth.Should().Be(340);
    }

    [Fact]
    public void AutomaticFixedWidths_PreserveTheExistingSingleRowLayout()
    {
        var layout = UniformWrapLayout.Calculate(900, 0, 12, 6, stretchItems: false, maxColumns: 3);
        layout.Columns.Should().Be(6);
        layout.CellWidth.Should().Be(150);
    }
}

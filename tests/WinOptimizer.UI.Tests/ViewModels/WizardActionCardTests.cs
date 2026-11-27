using FluentAssertions;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WizardActionCardTests
{
    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    public void IconPath_UsesTheCustomIconUntilThePathIsCleared(string? clearedPath)
    {
        var card = new WizardActionCard { IconPath = "M0,0 L10,10" };
        card.UsePathIcon.Should().BeTrue();

        card.IconPath = clearedPath!;

        card.UsePathIcon.Should().BeFalse();
    }

    [Fact]
    public void FailedToProcessing_Retry_Transition()
    {
        var card = new WizardActionCard();

        card.HasFailed = true;
        card.HasFailed.Should().BeTrue();

        card.IsProcessing = true; // retry
        card.IsProcessing.Should().BeTrue();
        card.HasFailed.Should().BeFalse();
        card.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void IsComplete_SetTrue_RaisesPropertyChangedForCleared_IsProcessing()
    {
        var card = new WizardActionCard();
        card.IsProcessing = true;

        var changedProperties = new List<string>();
        card.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        card.IsComplete = true;

        // IsProcessing should have been set to false, generating a PropertyChanged
        changedProperties.Should().Contain(nameof(card.IsProcessing));
    }

    [Fact]
    public void HasFailed_SetTrue_RaisesPropertyChangedForCleared_IsProcessing()
    {
        var card = new WizardActionCard();
        card.IsProcessing = true;

        var changedProperties = new List<string>();
        card.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        card.HasFailed = true;

        changedProperties.Should().Contain(nameof(card.IsProcessing));
    }

    [Fact]
    public void IsProcessing_SetTrue_RaisesPropertyChangedForCleared_IsComplete()
    {
        var card = new WizardActionCard();
        card.IsComplete = true;

        var changedProperties = new List<string>();
        card.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        card.IsProcessing = true;

        changedProperties.Should().Contain(nameof(card.IsComplete));
    }
}

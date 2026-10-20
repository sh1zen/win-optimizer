using FluentAssertions;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WizardActionCardTests
{
    [Fact]
    public void IconPath_WhenSetToNonEmpty_SetsUsePathIconTrue()
    {
        var card = new WizardActionCard();

        card.IconPath = "M0,0 L10,10";

        card.UsePathIcon.Should().BeTrue();
    }

    [Fact]
    public void IconPath_WhenSetToEmpty_SetsUsePathIconFalse()
    {
        var card = new WizardActionCard();
        card.IconPath = "M0,0 L10,10"; // first set to non-empty

        card.IconPath = "";

        card.UsePathIcon.Should().BeFalse();
    }

    [Fact]
    public void IconPath_WhenSetToNull_SetsUsePathIconFalse()
    {
        var card = new WizardActionCard();
        card.IconPath = "some path";

        card.IconPath = null!;

        card.UsePathIcon.Should().BeFalse();
    }

    [Fact]
    public void IsProcessing_SetTrue_ClearsIsCompleteAndHasFailed()
    {
        var card = new WizardActionCard();
        card.IsComplete = true;

        card.IsProcessing = true;

        card.IsComplete.Should().BeFalse();
        card.HasFailed.Should().BeFalse();
        card.IsProcessing.Should().BeTrue();
    }

    [Fact]
    public void IsComplete_SetFalse_DoesNotResetOtherFlags()
    {
        var card = new WizardActionCard();
        card.IsProcessing = true;

        card.IsComplete = false; // Setting to false should not trigger the clearing logic

        card.IsProcessing.Should().BeTrue();
    }

    [Fact]
    public void HasFailed_SetFalse_DoesNotResetOtherFlags()
    {
        var card = new WizardActionCard();
        card.IsProcessing = true;

        card.HasFailed = false;

        card.IsProcessing.Should().BeTrue();
    }

    [Fact]
    public void IsProcessing_SetFalse_DoesNotResetOtherFlags()
    {
        var card = new WizardActionCard();
        card.IsComplete = true;

        card.IsProcessing = false;

        card.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void ProcessingToComplete_Transition()
    {
        var card = new WizardActionCard();

        card.IsProcessing = true;
        card.IsProcessing.Should().BeTrue();
        card.IsComplete.Should().BeFalse();
        card.HasFailed.Should().BeFalse();

        card.IsComplete = true;
        card.IsComplete.Should().BeTrue();
        card.IsProcessing.Should().BeFalse();
        card.HasFailed.Should().BeFalse();
    }

    [Fact]
    public void ProcessingToFailed_Transition()
    {
        var card = new WizardActionCard();

        card.IsProcessing = true;
        card.IsProcessing.Should().BeTrue();

        card.HasFailed = true;
        card.HasFailed.Should().BeTrue();
        card.IsProcessing.Should().BeFalse();
        card.IsComplete.Should().BeFalse();
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

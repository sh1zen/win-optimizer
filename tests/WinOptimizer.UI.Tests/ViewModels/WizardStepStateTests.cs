using FluentAssertions;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WizardStepStateTests
{
    private WizardStepState CreateState()
    {
        return new WizardStepState
        {
            StepNumber = 1,
            Title = "Step One",
            Icon = "icon1",
        };
    }

    [Fact]
    public void DefaultState_IsLockedIncompleteAndCollapsed()
    {
        var state = new WizardStepState();

        state.IsExpanded.Should().BeFalse();
        state.IsAvailable.Should().BeFalse();
        state.IsComplete.Should().BeFalse();
        state.StatusText.Should().BeEmpty();
    }

    [Fact]
    public void StatusText_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var state = CreateState();
        state.StatusText = "Ready";

        var raised = false;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(state.StatusText))
                raised = true;
        };

        state.StatusText = "Ready"; // same value

        raised.Should().BeFalse();
    }

    [Fact]
    public void IsExpanded_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var state = CreateState();
        // Default is false; set to false again
        var raised = false;
        state.PropertyChanged += (_, _) => raised = true;

        state.IsExpanded = false;

        raised.Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var state = CreateState();
        var raised = false;
        state.PropertyChanged += (_, _) => raised = true;

        state.IsAvailable = false; // default is false

        raised.Should().BeFalse();
    }

    [Fact]
    public void IsComplete_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        var state = CreateState();
        var raised = false;
        state.PropertyChanged += (_, _) => raised = true;

        state.IsComplete = false; // default is false

        raised.Should().BeFalse();
    }

    [Fact]
    public void ShowChevron_WhenAvailableAndNotComplete_ReturnsTrue()
    {
        var state = CreateState();
        state.IsAvailable = true;
        state.IsComplete = false;

        state.ShowChevron.Should().BeTrue();
    }

    [Fact]
    public void ShowChevron_WhenLocked_ReturnsFalse()
    {
        var state = CreateState();
        state.IsAvailable = false;
        state.IsComplete = false;

        state.ShowChevron.Should().BeFalse();
    }

    [Fact]
    public void ShowChevron_WhenLockedAndComplete_ReturnsFalse()
    {
        var state = CreateState();
        state.IsAvailable = false;
        state.IsComplete = true;

        state.ShowChevron.Should().BeFalse();
    }

    [Fact]
    public void TransitionFromLockedToAvailable_UpdatesIsLockedAndShowChevron()
    {
        var state = CreateState();
        state.IsAvailable = false;

        state.IsLocked.Should().BeTrue();
        state.ShowChevron.Should().BeFalse();

        state.IsAvailable = true;

        state.IsLocked.Should().BeFalse();
        state.ShowChevron.Should().BeTrue();
    }

    [Fact]
    public void TransitionFromAvailableToComplete_HidesChevron()
    {
        var state = CreateState();
        state.IsAvailable = true;

        state.ShowChevron.Should().BeTrue();

        state.IsComplete = true;

        state.ShowChevron.Should().BeFalse();
    }

    [Fact]
    public void ExpandCollapseCycle_TogglesChevronRotation()
    {
        var state = CreateState();

        state.ChevronRotation.Should().Be(0);

        state.IsExpanded = true;
        state.ChevronRotation.Should().Be(180);

        state.IsExpanded = false;
        state.ChevronRotation.Should().Be(0);
    }
}

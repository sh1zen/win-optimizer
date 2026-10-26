using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Checks that automatic-removal consent is localized and awaited on the UI dispatcher.</summary>
public sealed class UninstallResidueReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticUninstallConsentUsesDispatcherAndReturnsUsersChoice(bool consent)
    {
        var dialogs = new Mock<IDialogService>();
        var dispatcher = new Mock<IDispatcherService>();
        var text = new Mock<ILocalizationService>();
        var onDispatcher = false;
        dispatcher.Setup(source => source.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns(async (Func<Task> action) =>
            {
                onDispatcher = true;
                try { await action(); }
                finally { onDispatcher = false; }
            });
        text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        text.Setup(source => source.GetString("Uninstall_AutomaticPrompt", It.IsAny<object[]>())).Returns("No registered uninstaller for App. Use automatic uninstall?");
        dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .Callback((ConfirmationRequest request) =>
            {
                Assert.True(onDispatcher);
                Assert.Equal("Uninstall_AutomaticTitle", request.Title);
                Assert.Contains("App", request.Message);
                Assert.Equal("Uninstall_AutomaticProceed", request.ConfirmButtonText);
                Assert.Equal("Button_Cancel", request.CancelButtonText);
            }).ReturnsAsync(new ConfirmationResponse { Confirmed = consent });
        var review = new UninstallResidueReview(Mock.Of<IUninstallResidueService>(), dialogs.Object, dispatcher.Object,
            Mock.Of<ITaskProgressService>(), text.Object, Mock.Of<IApplicationModeService>());

        Assert.Equal(consent, await review.ConfirmAutomaticUninstallAsync(new ItemDefinition { Id = "app", Name = "App", Description = "Test" }, CancellationToken.None));
        dialogs.Verify(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
    }
}

using System.Net;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Uses fixture HTTP responses and deployment boundaries without registering packages.</summary>
public sealed class StoreDownloadServiceTests
{
    [Fact]
    public async Task AlternateAccountUsesItsReadableWorkspace()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var files = new Mock<IFileSystemService>();
        var user = new Mock<IInteractiveUserService>();
        var deployment = new Mock<IAppxDeployment>(MockBehavior.Strict);
        user.SetupGet(account => account.IsOtsElevation).Returns(true);
        deployment.SetupGet(adapter => adapter.InteractiveAccountSid).Returns(sid);
        using var http = new HttpClient(new StoreHandler(""));
        var service = new StoreDownloadService(Mock.Of<ITaskProgressService>(), Mock.Of<ILocalizationService>(),
            files.Object, http, deployment.Object, user.Object);

        Assert.False(await service.DownloadAndInstallPackageAsync("Fixture.Product"));

        files.Verify(fs => fs.CreateDirectory(It.Is<string>(path => path.StartsWith(Path.Combine(AppDataPaths.User(sid), "Temp") + Path.DirectorySeparatorChar))), Times.Once);
        files.Verify(fs => fs.GetTempPath(), Times.Never);
        deployment.VerifyGet(adapter => adapter.InteractiveAccountSid, Times.Once);
        deployment.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadedPackagesUseTheAccountDeploymentOwnerAndPropagateFailure(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinOptimizer-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new Mock<IFileSystemService>();
            files.Setup(fs => fs.GetTempPath()).Returns(root);
            files.Setup(fs => fs.CombinePath(It.IsAny<string[]>())).Returns((string[] parts) => Path.Combine(parts));
            files.Setup(fs => fs.CreateDirectory(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
            var deployment = new Mock<IAppxDeployment>(MockBehavior.Strict);
            var setup = deployment.Setup(adapter => adapter.InstallPackageAsync(It.Is<string>(path => path.StartsWith(root) && path.EndsWith(".msixbundle")),
                It.Is<IReadOnlyList<string>>(dependencies => dependencies.Count == 0), CancellationToken.None));
            if (fail) setup.ThrowsAsync(new IOException("fixture deployment failed"));
            else setup.Returns(Task.CompletedTask);
            using var http = new HttpClient(new StoreHandler("<a href=\"https://fixture.invalid/app\">Fixture.App_1.0.0.0_neutral__publisher.msixbundle</a>"));
            var service = new StoreDownloadService(Mock.Of<ITaskProgressService>(), Mock.Of<ILocalizationService>(),
                files.Object, http, deployment.Object, Mock.Of<IInteractiveUserService>());

            Assert.Equal(!fail, await service.DownloadAndInstallPackageAsync("Fixture.Product"));

            deployment.Verify(adapter => adapter.InstallPackageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), CancellationToken.None), Times.AtLeastOnce);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class StoreHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Post ? new StringContent(html) : new ByteArrayContent([1, 2, 3])
            });
    }
}

using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class WindowsRegistryDefragBootTests
{
    [Fact]
    public void LatestScheduledAttemptSupersedesOlderSuccessEvenBeforeWinPeStarts()
    {
        var root = Directory.CreateTempSubdirectory("registry-defrag-status-");
        try
        {
            var prior = CreateJob(root, DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(Path.Combine(prior, "started"), "");
            File.WriteAllText(Path.Combine(prior, "result.json"), """{"State":"Completed","Compacted":2,"Errors":[]}""");
            var next = CreateJob(root, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.Combine(next, "scheduled"), "");
            var pending = WindowsRegistryDefragBoot.ReadStatus(root.FullName, _ => { });
            Assert.Contains("has not reported starting", pending);
            Assert.Contains(next, pending);

            File.WriteAllText(Path.Combine(next, "started"), "");
            Assert.Contains("ended without a result", WindowsRegistryDefragBoot.ReadStatus(root.FullName, _ => { }));

            File.WriteAllText(Path.Combine(next, "result.json"), """{"State":"Completed","Compacted":1,"Errors":[]}""");
            Assert.Contains("Hives compacted: 1", WindowsRegistryDefragBoot.ReadStatus(root.FullName, _ => { }));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("""{"State":"Completed","Compacted":2,"Errors":[],"CleanupPending":true}""", "did not confirm cleanup")]
    [InlineData("""{"State":"Completed","Compacted":2,"Errors":[],"CleanupErrors":["BCD device removal failed."]}""", "BCD device removal failed.")]
    public void CompactionReceiptReportsUnconfirmedOrFailedBootCleanup(string receipt, string expected)
    {
        var root = Directory.CreateTempSubdirectory("registry-defrag-status-");
        try
        {
            var job = CreateJob(root, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(job, "started"), "");
            File.WriteAllText(Path.Combine(job, "result.json"), receipt);

            var status = WindowsRegistryDefragBoot.ReadStatus(root.FullName, _ => { });

            Assert.Contains("Hives compacted: 2", status);
            Assert.Contains(expected, status);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("""{"State":"Failed","Compacted":0}""")]
    [InlineData("""{"State":"Restored","Compacted":0,"Errors":null}""")]
    [InlineData("""{"State":"Completed","Errors":[]}""")]
    [InlineData("""{"State":"Completed","Compacted":-1,"Errors":[]}""")]
    public void IncompleteReceiptIsReportedWithoutAbortingTheScan(string receipt)
    {
        var root = Directory.CreateTempSubdirectory("registry-defrag-status-");
        try
        {
            var job = CreateJob(root, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(job, "started"), "");
            File.WriteAllText(Path.Combine(job, "result.json"), receipt);

            var status = WindowsRegistryDefragBoot.ReadStatus(root.FullName, _ => { });

            Assert.Contains("Unable to read the offline registry maintenance result", status);
        }
        finally { root.Delete(recursive: true); }
    }

    private static string CreateJob(DirectoryInfo root, DateTime created)
    {
        var path = root.CreateSubdirectory("WinOptimizer.RegistryDefrag-" + Guid.NewGuid().ToString("N")).FullName;
        Directory.SetCreationTimeUtc(path, created);
        return path;
    }
}

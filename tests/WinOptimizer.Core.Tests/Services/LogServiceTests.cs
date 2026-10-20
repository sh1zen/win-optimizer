using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Services;

public class LogServiceTests : IDisposable
{
    [Fact]
    public async Task TopicsAreStoredAndNestedAsyncFlowsRemainIndependent()
    {
        using var service = CreateService(); service.StartLog();
        using (service.BeginTopic(LogTopic.Startup))
        {
            await Task.WhenAll(WriteAsync(LogTopic.Software, "apps"), WriteAsync(LogTopic.Updates, "updates"));
            service.LogInformation("outer");
        }
        service.LogInformation("unscoped");
        service.LogWarning("Cleaner confirmation: unavailable");
        var page = await service.ReadRecentAsync(Path.GetFileName(service.GetLogPath()));
        Assert.Equal(LogTopic.General, page.Entries.Single(entry => entry.Message == "unscoped").Topic);
        Assert.Equal(LogTopic.Cleaner, page.Entries.Single(entry => entry.Message == "Cleaner confirmation: unavailable").Topic);
        Assert.Equal(LogTopic.Startup, page.Entries.Single(entry => entry.Message == "outer").Topic);
        Assert.Equal(LogTopic.Software, page.Entries.Single(entry => entry.Message == "apps").Topic);
        Assert.Equal(LogTopic.Updates, page.Entries.Single(entry => entry.Message == "updates").Topic);

        async Task WriteAsync(LogTopic topic, string message)
        {
            using var scope = service.BeginTopic(topic);
            await Task.Yield();
            service.LogInformation(message);
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinOptimizerLogTests_" + Guid.NewGuid().ToString("N"), "win-optimizer", "Logs");
    private LogService CreateService() => new(_directory);
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_directory)!)!, true);
    }

    [Fact]
    public void GetLogPath_ReturnsPathInWinOptimizerDirectory()
    {
        using var service = CreateService();

        var path = service.GetLogPath();

        path.Should().NotBeNullOrEmpty();
        path.Should().Contain("win-optimizer");
        path.Should().Contain("Logs");
        path.Should().EndWith(".jsonl");
    }

    [Fact]
    public void Log_WithDifferentLevels_DoesNotThrow()
    {
        using var service = CreateService();

        // Without calling StartLog(), these should still not throw
        // (they just won't write to the file)
        var action = () =>
        {
            service.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Info, "info message");
            service.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Warning, "warning message");
            service.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Error, "error message");
            service.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Debug, "debug message");
            service.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Success, "success message");
        };

        action.Should().NotThrow();
    }

    [Fact]
    public void Log_WithException_DoesNotThrow()
    {
        using var service = CreateService();

        var ex = new InvalidOperationException("test error");
        var action = () => service.Log(
            WinOptimizer.Core.Features.Common.Enums.LogLevel.Error, "error", ex);

        action.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var service = CreateService();

        var action = () =>
        {
            service.Dispose();
            service.Dispose();
        };

        action.Should().NotThrow();
    }

    [Fact]
    public void SetSystemInfoProvider_DoesNotThrow()
    {
        using var service = CreateService();
        var mockProvider = new Mock<ISystemInfoProvider>();

        var action = () => service.SetSystemInfoProvider(mockProvider.Object);

        action.Should().NotThrow();
    }

    [Fact]
    public void StartLog_WithSystemInfoProvider_WritesAllDiagnosticFields()
    {
        var service = CreateService();
        var mockProvider = new Mock<ISystemInfoProvider>();
        mockProvider.Setup(p => p.Collect()).Returns(new SystemInfo
        {
            AppVersion = "26.02.20",
            OperatingSystem = "Windows 11 Pro 25H2 (Build 26100.4061)",
            Architecture = "x64",
            DeviceType = "Laptop",
            Cpu = "AMD Ryzen 7 7840HS (16 cores)",
            Ram = "32 GB",
            Gpu = "NVIDIA GeForce RTX 4060 (Dedicated)",
            DotNetRuntime = ".NET 10.0.0",
            Elevation = "Admin (OTS)",
            FirmwareType = "UEFI",
            SecureBoot = "Enabled",
            Tpm = "2.0",
            DomainJoined = "No"
        });
        service.SetSystemInfoProvider(mockProvider.Object);
        service.StartLog();
        var logPath = service.GetLogPath();
        service.Dispose(); // Release file lock before reading

        var logContent = File.ReadAllText(logPath);
        logContent.Should().Contain("win-optimizer 26.02.20 Log Started");
        logContent.Should().Contain("OS:            Windows 11 Pro 25H2 (Build 26100.4061)");
        logContent.Should().Contain("Architecture:  x64");
        logContent.Should().Contain("Device Type:   Laptop");
        logContent.Should().Contain("CPU:           AMD Ryzen 7 7840HS (16 cores)");
        logContent.Should().Contain("RAM:           32 GB");
        logContent.Should().Contain("GPU:           NVIDIA GeForce RTX 4060 (Dedicated)");
        logContent.Should().Contain(".NET Runtime:  .NET 10.0.0");
        logContent.Should().Contain("Elevation:     Admin (OTS)");
        logContent.Should().Contain("Firmware:      UEFI");
        logContent.Should().Contain("Secure Boot:   Enabled");
        logContent.Should().Contain("TPM:           2.0");
        logContent.Should().Contain("Domain Joined: No");
        logContent.Should().Contain("=====================================");
    }

    [Fact]
    public async Task StartLogAsync_OpensLoggingWhileDiagnosticsRun_AndSharesInitialization()
    {
        using var service = CreateService();
        using var release = new ManualResetEventSlim();
        var collecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ISystemInfoProvider>();
        provider.Setup(p => p.Collect()).Returns(() =>
        {
            collecting.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Diagnostic test was not released.");
            return new SystemInfo { AppVersion = "test-session" };
        });
        service.SetSystemInfoProvider(provider.Object);

        var pending = service.StartLogAsync();
        try
        {
            await collecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending.IsCompleted.Should().BeFalse();
            service.StartLogAsync().Should().BeSameAs(pending);
            await Task.Run(() => service.LogInformation("window creating")).WaitAsync(TimeSpan.FromSeconds(5));
            var page = await service.ReadAsync(Path.GetFileName(service.GetLogPath())).WaitAsync(TimeSpan.FromSeconds(5));
            page.Entries.Should().Contain(entry => entry.Message == "window creating");
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var completed = await service.ReadAsync(Path.GetFileName(service.GetLogPath()));
        completed.Entries.Should().Contain(entry => entry.Message.Contains("test-session Log Started"));
        provider.Verify(p => p.Collect(), Times.Once);
    }

    [Fact]
    public async Task StartLogAsync_ReportsDiagnosticFailure_AndKeepsWriterUsable()
    {
        using var service = CreateService();
        var failure = new InvalidOperationException("diagnostic query failed");
        var provider = new Mock<ISystemInfoProvider>();
        provider.Setup(p => p.Collect()).Throws(failure);
        service.SetSystemInfoProvider(provider.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartLogAsync());
        error.InnerException.Should().BeSameAs(failure);
        error.Message.Should().Contain(service.GetLogPath());
        service.LogInformation("startup continues");
        var page = await service.ReadAsync(Path.GetFileName(service.GetLogPath()));
        page.Entries.Should().Contain(entry => entry.Message == "startup continues");
    }

    [Fact]
    public async Task StartLogAsync_DisposalDuringCollection_DoesNotWriteLateHeaderOrReopenFile()
    {
        using var service = CreateService();
        using var release = new ManualResetEventSlim();
        var collecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Mock<ISystemInfoProvider>();
        provider.Setup(p => p.Collect()).Returns(() =>
        {
            collecting.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Diagnostic test was not released.");
            return new SystemInfo { AppVersion = "late-header" };
        });
        service.SetSystemInfoProvider(provider.Object);
        var pending = service.StartLogAsync();
        try
        {
            await collecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
        }
        finally
        {
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        File.ReadAllText(service.GetLogPath()).Should().NotContain("late-header");
    }

    [Fact]
    public void StartLog_WithoutSystemInfoProvider_WritesFallbackMessage()
    {
        var service = CreateService();
        service.StartLog();
        var logPath = service.GetLogPath();
        service.Dispose(); // Release file lock before reading

        var logContent = File.ReadAllText(logPath);
        logContent.Should().Contain("win-optimizer Log Started");
        logContent.Should().Contain("System info unavailable (provider not configured)");
    }

    [Fact]
    public void StartLog_DoesNotContainUserOrMachineInfo()
    {
        var service = CreateService();
        var mockProvider = new Mock<ISystemInfoProvider>();
        mockProvider.Setup(p => p.Collect()).Returns(new SystemInfo());
        service.SetSystemInfoProvider(mockProvider.Object);
        service.StartLog();
        var logPath = service.GetLogPath();
        service.Dispose(); // Release file lock before reading

        var logContent = File.ReadAllText(logPath);
        logContent.Should().NotContain("User:");
        logContent.Should().NotContain("Elevated User:");
        logContent.Should().NotContain("Machine:");
        logContent.Should().NotContain("Timestamp:");
    }

    // ── CleanupOldLogs (BP-7) ──

    [Fact]
    public async Task StructuredRecordsKeepLevelsMessagesAndExceptionFields()
    {
        using var service = CreateService();
        service.StartLog();
        service.LogError("quoted \"message\"\nsecond line — 漢字", new IOException("disk failure", new InvalidOperationException("inner failure")));
        var page = await service.ReadAsync(Path.GetFileName(service.GetLogPath()));
        var entry = Assert.Single(page.Entries, entry => entry.Level == WinOptimizer.Core.Features.Common.Enums.LogLevel.Error);
        Assert.Equal("quoted \"message\"\nsecond line — 漢字", entry.Message);
        Assert.NotNull(entry.Timestamp);
        Assert.Equal(typeof(IOException).FullName, entry.Exception!.Type);
        Assert.Equal("disk failure", entry.Exception.Message);
        Assert.Contains("inner failure", entry.Exception.InnerException);
        service.Dispose();
        foreach (var line in File.ReadAllLines(service.GetLogPath()))
            Assert.NotNull(JsonSerializer.Deserialize<AppLogEntry>(line, WinOptimizer.Core.Features.Common.Services.LogPageReader.JsonOptions));
    }

    [Fact]
    public async Task ClearRemovesOnlyApplicationLogs_AndActiveLoggingContinues()
    {
        using var service = CreateService();
        service.StartLog();
        File.WriteAllText(Path.Combine(_directory, "WinOptimizer_Log_older.log"), "unrecognized file");
        File.WriteAllText(Path.Combine(_directory, "WinOptimizer_Log_older.jsonl"), "older log");
        var receipt = Path.Combine(_directory, "ChangeHistory.jsonl");
        File.WriteAllText(receipt, "keep history");
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "keep notes");
        await service.ClearAsync();
        Assert.Equal(0, new FileInfo(service.GetLogPath()).Length);
        Assert.Equal("keep history", File.ReadAllText(receipt));
        Assert.Equal("keep notes", File.ReadAllText(Path.Combine(_directory, "notes.txt")));
        Assert.Equal("unrecognized file", File.ReadAllText(Path.Combine(_directory, "WinOptimizer_Log_older.log")));
        Assert.Single(await service.GetFilesAsync());
        service.LogInformation("written after clear");
        var entry = Assert.Single((await service.ReadAsync(Path.GetFileName(service.GetLogPath()))).Entries);
        Assert.Equal("written after clear", entry.Message);
    }

    [Fact]
    public async Task SavedSessionsUseStructuredRecords_AndReadingDoesNotCreateStorage()
    {
        using var service = CreateService();
        Assert.Empty(await service.GetFilesAsync());
        Assert.False(Directory.Exists(_directory));
        service.StartLog();
        service.LogError("Saved diagnostic");
        var file = Assert.Single(await service.GetFilesAsync());
        Assert.EndsWith(".jsonl", file.FileName);
        var page = await service.ReadAsync(file.FileName);
        Assert.Contains(page.Entries, entry => entry.Message == "Saved diagnostic");
    }

    [Fact]
    public async Task SessionReadersRequireTheStructuredFileAndTopic()
    {
        using var service = CreateService();
        Directory.CreateDirectory(_directory);
        const string file = "WinOptimizer_Log_saved.jsonl";
        File.WriteAllText(Path.Combine(_directory, file), "{\"Timestamp\":\"2026-10-20T12:00:00Z\",\"Level\":\"Info\",\"Message\":\"Missing topic\"}\n");
        var page = await service.ReadAsync(file);
        Assert.Empty(page.Entries);
        Assert.Equal(1, page.UnreadableEntries);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync("WinOptimizer_Log_saved.log"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadRecentAsync("WinOptimizer_Log_saved.log"));
    }

    [Theory]
    [InlineData("../WinOptimizer_Log_private.jsonl")]
    [InlineData("C:\\private\\WinOptimizer_Log_private.jsonl")]
    [InlineData("notes.txt")]
    public async Task ReadsRejectPathsOutsideTheSavedLogNamespace(string fileName)
    {
        using var service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(fileName));
    }

    [Fact]
    public async Task ClearAndConcurrentWritesLeaveCompleteStructuredRecords()
    {
        using var service = CreateService();
        service.StartLog();
        await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 100; i++) service.LogInformation($"event {i}"); }), service.ClearAsync());
        service.LogInformation("final event");
        var page = await service.ReadAsync(Path.GetFileName(service.GetLogPath()), pageSize: 500);
        Assert.Equal(0, page.UnreadableEntries);
        Assert.Contains(page.Entries, entry => entry.Message == "final event");
    }

    [Fact]
    public void CleanupOldLogs_DeletesFilesOlderThanMaxAge()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinOptimizerLogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create an "old" log file with a creation time well in the past
            var oldFile = Path.Combine(tempDir, "WinOptimizer_Log_20200101_000000.jsonl");
            File.WriteAllText(oldFile, "old");
            File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-60));

            // Create a "recent" log file
            var recentFile = Path.Combine(tempDir, "WinOptimizer_Log_20260226_120000.jsonl");
            File.WriteAllText(recentFile, "recent");

            LogService.CleanupOldLogs(tempDir, maxAgeDays: 30, maxFiles: 100);

            File.Exists(oldFile).Should().BeFalse("file older than maxAgeDays should be deleted");
            File.Exists(recentFile).Should().BeTrue("recent file should be kept");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CleanupOldLogs_CapsToMaxFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinOptimizerLogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Create 5 files, all recent
            for (int i = 0; i < 5; i++)
            {
                var file = Path.Combine(tempDir, $"WinOptimizer_Log_20260226_{i:D6}.jsonl");
                File.WriteAllText(file, $"log {i}");
                File.SetCreationTimeUtc(file, DateTime.UtcNow.AddMinutes(-5 + i));
            }

            LogService.CleanupOldLogs(tempDir, maxAgeDays: 30, maxFiles: 3);

            var remaining = Directory.GetFiles(tempDir, "WinOptimizer_Log_*.jsonl");
            remaining.Should().HaveCount(3, "should cap to maxFiles");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CleanupOldLogs_NonExistentDirectory_DoesNotThrow()
    {
        var action = () => LogService.CleanupOldLogs(
            Path.Combine(Path.GetTempPath(), "NonExistentDir_" + Guid.NewGuid().ToString("N")));

        action.Should().NotThrow();
    }

    [Fact]
    public void CleanupOldLogs_EmptyDirectory_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"WinOptimizerLogTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var action = () => LogService.CleanupOldLogs(tempDir);
            action.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

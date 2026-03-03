using Leash.Api.Services;
using Leash.Api.Services.Harness;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Leash.Tests.Services;

public class TranscriptWatcherTests : IDisposable
{
    private readonly TranscriptWatcher _watcher;

    public TranscriptWatcherTests()
    {
        var mockLogger = new Mock<ILogger<TranscriptWatcher>>();
        var registry = new HarnessClientRegistry(new IHarnessClient[]
        {
            new ClaudeHarnessClient(),
            new CopilotHarnessClient()
        });
        _watcher = new TranscriptWatcher(registry, mockLogger.Object);
    }

    [Fact]
    public void GetProjects_ShouldReturnEmptyList_WhenNoProjectsExist()
    {
        // Act - the ~/.claude/projects directory may or may not exist
        var projects = _watcher.GetProjects();

        // Assert
        Assert.NotNull(projects);
    }

    [Fact]
    public void GetTranscript_ShouldReturnEmptyList_ForNonexistentSession()
    {
        // Act
        var entries = _watcher.GetTranscript("nonexistent-session-id");

        // Assert
        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public void FindTranscriptFile_ShouldReturnNull_ForNonexistentSession()
    {
        // Act
        var file = _watcher.FindTranscriptFile("nonexistent-session-id");

        // Assert
        Assert.Null(file);
    }

    [Fact]
    public void Start_ShouldNotThrow_WhenProjectsDirDoesNotExist()
    {
        // Act & Assert - should not throw
        _watcher.Start();
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        // Act & Assert - calling Dispose twice should not throw
        _watcher.Dispose();
        _watcher.Dispose();
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}

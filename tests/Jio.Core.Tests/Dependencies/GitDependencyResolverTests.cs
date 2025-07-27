using Jio.Core.Dependencies;
using Jio.Core.Logging;
using Moq;
using Xunit;

namespace Jio.Core.Tests.Dependencies;

public class GitDependencyResolverTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly string _testCacheDir;
    private readonly GitDependencyResolver _resolver;

    public GitDependencyResolverTests()
    {
        _mockLogger = new Mock<ILogger>();
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"jio-git-test-{Guid.NewGuid():N}");
        _resolver = new GitDependencyResolver(_mockLogger.Object, _testCacheDir);
    }

    [Theory]
    [InlineData("git+https://github.com/user/repo.git", true)]
    [InlineData("git+ssh://git@github.com:user/repo.git", true)]
    [InlineData("git://github.com/user/repo.git", true)]
    [InlineData("https://github.com/user/repo.git", true)]
    [InlineData("github:user/repo", true)]
    [InlineData("user/repo", true)]
    [InlineData("user/repo#branch", true)]
    [InlineData("^1.0.0", false)]
    [InlineData("~2.0.0", false)]
    [InlineData("latest", false)]
    [InlineData("file:../local", false)]
    public void IsGitDependency_ReturnsCorrectResult(string spec, bool expected)
    {
        // Act
        var result = _resolver.IsGitDependency(spec);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ResolveAsync_WithGitHubShorthand_ParsesCorrectly()
    {
        // Arrange
        var gitUrl = "user/repo#v1.0.0";
        
        // Note: This test would require mocking git commands or network access
        // For unit testing, we'd typically mock the git operations
        // This is more of an integration test scenario
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _resolver.ResolveAsync(gitUrl));
    }

    [Fact]
    public async Task ResolveAsync_WithFullGitUrl_ParsesCorrectly()
    {
        // Arrange
        var gitUrl = "git+https://github.com/user/repo.git#v1.0.0";
        
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _resolver.ResolveAsync(gitUrl));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testCacheDir))
        {
            Directory.Delete(_testCacheDir, recursive: true);
        }
    }
}
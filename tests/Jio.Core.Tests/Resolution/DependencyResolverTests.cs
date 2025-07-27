using FluentAssertions;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Resolution;
using Moq;
using Xunit;

namespace Jio.Core.Tests.Resolution;

[Collection("Resolution Tests")]
public class DependencyResolverTests
{
    private readonly Mock<IPackageRegistry> _registryMock;
    private readonly DependencyResolver _resolver;

    public DependencyResolverTests()
    {
        _registryMock = new Mock<IPackageRegistry>();
        _resolver = new DependencyResolver(_registryMock.Object);
    }

    [Fact]
    public async Task ResolveAsync_Should_Resolve_Direct_Dependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-app",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0",
                ["lodash"] = "^4.17.21"
            }
        };

        SetupMockRegistry("express", "4.18.2", new Dictionary<string, string>());
        SetupMockRegistry("lodash", "4.17.21", new Dictionary<string, string>());

        // Act
        var graph = await _resolver.ResolveAsync(manifest);

        // Assert
        graph.Packages.Should().HaveCount(2);
        graph.RootDependencies.Should().ContainKey("express");
        graph.RootDependencies.Should().ContainKey("lodash");
        
        var expressPackage = graph.Packages["express@4.18.2"];
        expressPackage.Name.Should().Be("express");
        expressPackage.Version.Should().Be("4.18.2");
    }

    [Fact]
    public async Task ResolveAsync_Should_Resolve_Transitive_Dependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-app",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            }
        };

        var expressDeps = new Dictionary<string, string>
        {
            ["accepts"] = "~1.3.8",
            ["array-flatten"] = "1.1.1"
        };

        SetupMockRegistry("express", "4.18.2", expressDeps);
        SetupMockRegistry("accepts", "1.3.8", new Dictionary<string, string>());
        SetupMockRegistry("array-flatten", "1.1.1", new Dictionary<string, string>());

        // Act
        var graph = await _resolver.ResolveAsync(manifest);

        // Assert
        graph.Packages.Should().HaveCount(3);
        graph.Packages.Should().ContainKey("express@4.18.2");
        graph.Packages.Should().ContainKey("accepts@1.3.8");
        graph.Packages.Should().ContainKey("array-flatten@1.1.1");
    }

    [Fact]
    public async Task ResolveAsync_Should_Handle_Dev_Dependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-app",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0"
            },
            DevDependencies = new Dictionary<string, string>
            {
                ["typescript"] = "^5.0.0"
            }
        };

        SetupMockRegistry("express", "4.18.2", new Dictionary<string, string>());
        SetupMockRegistry("typescript", "5.0.0", new Dictionary<string, string>());

        // Act
        var graph = await _resolver.ResolveAsync(manifest);

        // Assert
        graph.Packages.Should().HaveCount(2);
        
        var expressPackage = graph.Packages["express@4.18.2"];
        expressPackage.Dev.Should().BeFalse();
        
        var tsPackage = graph.Packages["typescript@5.0.0"];
        tsPackage.Dev.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_Should_Not_Duplicate_Shared_Dependencies()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-app",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["package-a"] = "1.0.0",
                ["package-b"] = "1.0.0"
            }
        };

        var sharedDep = new Dictionary<string, string>
        {
            ["shared-lib"] = "^2.0.0"
        };

        SetupMockRegistry("package-a", "1.0.0", sharedDep);
        SetupMockRegistry("package-b", "1.0.0", sharedDep);
        SetupMockRegistry("shared-lib", "2.0.0", new Dictionary<string, string>());

        // Act
        var graph = await _resolver.ResolveAsync(manifest);

        // Assert
        graph.Packages.Should().HaveCount(3);
        graph.Packages.Should().ContainKey("package-a@1.0.0");
        graph.Packages.Should().ContainKey("package-b@1.0.0");
        graph.Packages.Should().ContainKey("shared-lib@2.0.0");
        
        // Should only resolve shared-lib once
        _registryMock.Verify(r => r.GetPackageManifestAsync("shared-lib", "2.0.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_Should_Handle_Version_Range_Resolution()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-app",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["package-with-range"] = "^1.0.0"
            }
        };

        _registryMock.Setup(r => r.GetPackageVersionsAsync("package-with-range", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "1.0.0", "1.0.5", "1.1.0", "2.0.0" });

        SetupMockRegistry("package-with-range", "1.1.0", new Dictionary<string, string>());

        // Act
        var graph = await _resolver.ResolveAsync(manifest);

        // Assert
        graph.Packages.Should().HaveCount(1);
        graph.Packages.Should().ContainKey("package-with-range@1.1.0");
    }

    private void SetupMockRegistry(string name, string version, Dictionary<string, string> dependencies)
    {
        var manifest = new PackageManifest
        {
            Name = name,
            Version = version,
            Dependencies = dependencies
        };

        _registryMock.Setup(r => r.GetPackageManifestAsync(name, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        _registryMock.Setup(r => r.GetPackageIntegrityAsync(name, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"sha512-{Convert.ToBase64String(Guid.NewGuid().ToByteArray())}");

        _registryMock.Setup(r => r.GetPackageVersionsAsync(name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { version });
    }
}
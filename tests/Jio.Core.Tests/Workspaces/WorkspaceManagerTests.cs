using System.Text.Json;
using FluentAssertions;
using Jio.Core.Models;
using Jio.Core.Workspaces;

namespace Jio.Core.Tests.Workspaces;

public sealed class WorkspaceManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly WorkspaceManager _workspaceManager;

    public WorkspaceManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-workspace-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        _workspaceManager = new WorkspaceManager(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task GetWorkspacesAsync_WhenNoPackageJsonExists_ReturnsEmptyList()
    {
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkspacesAsync_WhenPackageJsonHasNoWorkspaces_ReturnsEmptyList()
    {
        var manifest = new { name = "root", version = "1.0.0" };
        await CreatePackageJson(_tempDirectory, manifest);
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithStringWorkspacePattern_ReturnsMatchingWorkspaces()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var package1Dir = Path.Combine(_tempDirectory, "packages", "package1");
        Directory.CreateDirectory(package1Dir);
        await CreatePackageJson(package1Dir, new { name = "package1", version = "1.0.0" });
        
        var package2Dir = Path.Combine(_tempDirectory, "packages", "package2");
        Directory.CreateDirectory(package2Dir);
        await CreatePackageJson(package2Dir, new { name = "package2", version = "1.0.0" });
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(2);
        workspaces.Should().Contain(w => w.Name == "package1");
        workspaces.Should().Contain(w => w.Name == "package2");
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithArrayWorkspacePattern_ReturnsMatchingWorkspaces()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = new[] { "packages/*", "apps/*" } };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var packageDir = Path.Combine(_tempDirectory, "packages", "lib");
        Directory.CreateDirectory(packageDir);
        await CreatePackageJson(packageDir, new { name = "lib", version = "1.0.0" });
        
        var appDir = Path.Combine(_tempDirectory, "apps", "web");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { name = "web", version = "1.0.0" });
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(2);
        workspaces.Should().Contain(w => w.Name == "lib");
        workspaces.Should().Contain(w => w.Name == "web");
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithYarnWorkspaceFormat_ReturnsMatchingWorkspaces()
    {
        var rootManifest = new { 
            name = "root", 
            version = "1.0.0", 
            workspaces = new { packages = new[] { "packages/*" } } 
        };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var packageDir = Path.Combine(_tempDirectory, "packages", "package1");
        Directory.CreateDirectory(packageDir);
        await CreatePackageJson(packageDir, new { name = "package1", version = "1.0.0" });
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("package1");
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithDoubleAsteriskPattern_ReturnsNestedWorkspaces()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/**" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var nestedDir = Path.Combine(_tempDirectory, "packages", "nested", "deep");
        Directory.CreateDirectory(nestedDir);
        await CreatePackageJson(nestedDir, new { name = "deep-package", version = "1.0.0" });
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("deep-package");
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithExactPath_ReturnsSpecificWorkspace()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = new[] { "specific-package" } };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var packageDir = Path.Combine(_tempDirectory, "specific-package");
        Directory.CreateDirectory(packageDir);
        await CreatePackageJson(packageDir, new { name = "specific", version = "1.0.0" });
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("specific");
        workspaces[0].RelativePath.Should().Be("specific-package");
    }

    [Fact]
    public async Task GetWorkspacesAsync_SkipsDirectoriesWithoutPackageJson()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var validDir = Path.Combine(_tempDirectory, "packages", "valid");
        Directory.CreateDirectory(validDir);
        await CreatePackageJson(validDir, new { name = "valid", version = "1.0.0" });
        
        var invalidDir = Path.Combine(_tempDirectory, "packages", "invalid");
        Directory.CreateDirectory(invalidDir);
        // No package.json in invalidDir
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("valid");
    }

    [Fact]
    public async Task GetWorkspacesAsync_SkipsPackagesWithoutName()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var validDir = Path.Combine(_tempDirectory, "packages", "valid");
        Directory.CreateDirectory(validDir);
        await CreatePackageJson(validDir, new { name = "valid", version = "1.0.0" });
        
        var invalidDir = Path.Combine(_tempDirectory, "packages", "invalid");
        Directory.CreateDirectory(invalidDir);
        await CreatePackageJson(invalidDir, new { version = "1.0.0" }); // No name
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("valid");
    }

    [Fact]
    public async Task GetWorkspaceDependencyGraphAsync_WithNoDependencies_ReturnsEmptyGraph()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var package1Dir = Path.Combine(_tempDirectory, "packages", "package1");
        Directory.CreateDirectory(package1Dir);
        await CreatePackageJson(package1Dir, new { name = "package1", version = "1.0.0" });
        
        var graph = await _workspaceManager.GetWorkspaceDependencyGraphAsync();
        
        graph.Should().ContainKey("package1");
        graph["package1"].Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkspaceDependencyGraphAsync_WithWorkspaceDependencies_ReturnsCorrectGraph()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var libDir = Path.Combine(_tempDirectory, "packages", "lib");
        Directory.CreateDirectory(libDir);
        await CreatePackageJson(libDir, new { name = "lib", version = "1.0.0" });
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            dependencies = new { lib = "1.0.0" }
        });
        
        var graph = await _workspaceManager.GetWorkspaceDependencyGraphAsync();
        
        graph.Should().ContainKey("lib");
        graph.Should().ContainKey("app");
        graph["lib"].Should().BeEmpty();
        graph["app"].Should().Contain("lib");
    }

    [Fact]
    public async Task GetWorkspaceDependencyGraphAsync_WithDevDependencies_IncludesDevDeps()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var testUtilsDir = Path.Combine(_tempDirectory, "packages", "test-utils");
        Directory.CreateDirectory(testUtilsDir);
        await CreatePackageJson(testUtilsDir, new { name = "test-utils", version = "1.0.0" });
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            devDependencies = new Dictionary<string, object> { ["test-utils"] = "1.0.0" }
        });
        
        var graph = await _workspaceManager.GetWorkspaceDependencyGraphAsync();
        
        graph["app"].Should().Contain("test-utils");
    }

    [Fact]
    public async Task GetWorkspaceDependencyGraphAsync_IgnoresExternalDependencies()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            dependencies = new { react = "^18.0.0", lodash = "^4.17.21" }
        });
        
        var graph = await _workspaceManager.GetWorkspaceDependencyGraphAsync();
        
        graph["app"].Should().BeEmpty();
    }

    [Fact]
    public async Task GetWorkspaceDependencyGraphAsync_DeduplicatesDependencies()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var libDir = Path.Combine(_tempDirectory, "packages", "lib");
        Directory.CreateDirectory(libDir);
        await CreatePackageJson(libDir, new { name = "lib", version = "1.0.0" });
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            dependencies = new { lib = "1.0.0" },
            devDependencies = new { lib = "1.0.0" }
        });
        
        var graph = await _workspaceManager.GetWorkspaceDependencyGraphAsync();
        
        graph["app"].Should().HaveCount(1);
        graph["app"].Should().Contain("lib");
    }

    [Fact]
    public async Task GetTopologicalOrderAsync_WithNoDependencies_ReturnsOriginalOrder()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var package1Dir = Path.Combine(_tempDirectory, "packages", "package1");
        Directory.CreateDirectory(package1Dir);
        await CreatePackageJson(package1Dir, new { name = "package1", version = "1.0.0" });
        
        var package2Dir = Path.Combine(_tempDirectory, "packages", "package2");
        Directory.CreateDirectory(package2Dir);
        await CreatePackageJson(package2Dir, new { name = "package2", version = "1.0.0" });
        
        var orderedWorkspaces = await _workspaceManager.GetTopologicalOrderAsync();
        
        orderedWorkspaces.Should().HaveCount(2);
        orderedWorkspaces.Select(w => w.Name).Should().Contain("package1");
        orderedWorkspaces.Select(w => w.Name).Should().Contain("package2");
    }

    [Fact]
    public async Task GetTopologicalOrderAsync_WithDependencies_ReturnsDependenciesFirst()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var libDir = Path.Combine(_tempDirectory, "packages", "lib");
        Directory.CreateDirectory(libDir);
        await CreatePackageJson(libDir, new { name = "lib", version = "1.0.0" });
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            dependencies = new { lib = "1.0.0" }
        });
        
        var orderedWorkspaces = await _workspaceManager.GetTopologicalOrderAsync();
        
        orderedWorkspaces.Should().HaveCount(2);
        var libIndex = orderedWorkspaces.FindIndex(w => w.Name == "lib");
        var appIndex = orderedWorkspaces.FindIndex(w => w.Name == "app");
        libIndex.Should().BeLessThan(appIndex, "lib should come before app in topological order");
    }

    [Fact]
    public async Task GetTopologicalOrderAsync_WithChainedDependencies_ReturnsCorrectOrder()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var utilsDir = Path.Combine(_tempDirectory, "packages", "utils");
        Directory.CreateDirectory(utilsDir);
        await CreatePackageJson(utilsDir, new { name = "utils", version = "1.0.0" });
        
        var libDir = Path.Combine(_tempDirectory, "packages", "lib");
        Directory.CreateDirectory(libDir);
        await CreatePackageJson(libDir, new { 
            name = "lib", 
            version = "1.0.0",
            dependencies = new { utils = "1.0.0" }
        });
        
        var appDir = Path.Combine(_tempDirectory, "packages", "app");
        Directory.CreateDirectory(appDir);
        await CreatePackageJson(appDir, new { 
            name = "app", 
            version = "1.0.0",
            dependencies = new { lib = "1.0.0" }
        });
        
        var orderedWorkspaces = await _workspaceManager.GetTopologicalOrderAsync();
        
        orderedWorkspaces.Should().HaveCount(3);
        var utilsIndex = orderedWorkspaces.FindIndex(w => w.Name == "utils");
        var libIndex = orderedWorkspaces.FindIndex(w => w.Name == "lib");
        var appIndex = orderedWorkspaces.FindIndex(w => w.Name == "app");
        
        utilsIndex.Should().BeLessThan(libIndex);
        libIndex.Should().BeLessThan(appIndex);
    }

    [Fact]
    public async Task Operations_WithCancellationToken_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var workspacesTask = _workspaceManager.GetWorkspacesAsync(cts.Token);
        await workspacesTask.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetWorkspacesAsync_WithInvalidJson_SkipsCorruptedWorkspaces()
    {
        var rootManifest = new { name = "root", version = "1.0.0", workspaces = "packages/*" };
        await CreatePackageJson(_tempDirectory, rootManifest);
        
        var validDir = Path.Combine(_tempDirectory, "packages", "valid");
        Directory.CreateDirectory(validDir);
        await CreatePackageJson(validDir, new { name = "valid", version = "1.0.0" });
        
        var corruptedDir = Path.Combine(_tempDirectory, "packages", "corrupted");
        Directory.CreateDirectory(corruptedDir);
        await File.WriteAllTextAsync(Path.Combine(corruptedDir, "package.json"), "{ invalid json }");
        
        var workspaces = await _workspaceManager.GetWorkspacesAsync();
        
        workspaces.Should().HaveCount(1);
        workspaces[0].Name.Should().Be("valid");
    }

    private async Task CreatePackageJson(string directory, object manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "package.json"), json);
    }
}
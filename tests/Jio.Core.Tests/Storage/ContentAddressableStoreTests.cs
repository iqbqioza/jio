using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Jio.Core.Configuration;
using Jio.Core.Storage;
using Moq;

namespace Jio.Core.Tests.Storage;

public class ContentAddressableStoreTests : IDisposable
{
    private readonly string _tempStoreDirectory;
    private readonly JioConfiguration _configuration;
    private readonly ContentAddressableStore _store;
    private readonly Mock<HttpClient> _httpClientMock;

    public ContentAddressableStoreTests()
    {
        _tempStoreDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _configuration = new JioConfiguration
        {
            StoreDirectory = _tempStoreDirectory,
            UseHardLinks = false // Disable hard links for tests
        };
        _httpClientMock = new Mock<HttpClient>();
        _store = new ContentAddressableStore(_configuration, _httpClientMock.Object);
    }

    [Fact]
    public async Task GetPackagePathAsync_Should_Return_Consistent_Path()
    {
        // Arrange
        var packageName = "test-package";
        var version = "1.0.0";

        // Act
        var path1 = await _store.GetPackagePathAsync(packageName, version);
        var path2 = await _store.GetPackagePathAsync(packageName, version);

        // Assert
        path1.Should().Be(path2);
        path1.Should().StartWith(_tempStoreDirectory);
    }

    [Fact]
    public async Task ExistsAsync_Should_Return_False_For_NonExistent_Package()
    {
        // Arrange
        var packageName = "non-existent";
        var version = "1.0.0";

        // Act
        var exists = await _store.ExistsAsync(packageName, version);

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task AddPackageAsync_Should_Extract_Package_To_Store()
    {
        // Arrange
        var packageName = "test-package";
        var version = "1.0.0";
        var packageContent = CreateTestPackage();

        // Act
        using var stream = new MemoryStream(packageContent);
        await _store.AddPackageAsync(packageName, version, stream);

        // Assert
        var exists = await _store.ExistsAsync(packageName, version);
        exists.Should().BeTrue();

        var packagePath = await _store.GetPackagePathAsync(packageName, version);
        Directory.Exists(packagePath).Should().BeTrue();
        File.Exists(Path.Combine(packagePath, "package", "index.js")).Should().BeTrue();
    }

    [Fact]
    public async Task AddPackageAsync_Should_Not_Overwrite_Existing_Package()
    {
        // Arrange
        var packageName = "test-package";
        var version = "1.0.0";
        var packageContent = CreateTestPackage();

        // Act
        using var stream1 = new MemoryStream(packageContent);
        await _store.AddPackageAsync(packageName, version, stream1);

        var packagePath = await _store.GetPackagePathAsync(packageName, version);
        var creationTime = Directory.GetCreationTimeUtc(packagePath);

        await Task.Delay(100); // Ensure time difference

        using var stream2 = new MemoryStream(packageContent);
        await _store.AddPackageAsync(packageName, version, stream2);

        // Assert
        var newCreationTime = Directory.GetCreationTimeUtc(packagePath);
        newCreationTime.Should().Be(creationTime);
    }

    [Fact]
    public async Task LinkPackageAsync_Should_Copy_Package_To_Target()
    {
        // Arrange
        var packageName = "test-package";
        var version = "1.0.0";
        var targetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "node_modules", packageName);
        var packageContent = CreateTestPackage();

        using var stream = new MemoryStream(packageContent);
        await _store.AddPackageAsync(packageName, version, stream);

        // Act
        await _store.LinkPackageAsync(packageName, version, targetPath);

        // Assert
        Directory.Exists(targetPath).Should().BeTrue();
        File.Exists(Path.Combine(targetPath, "package", "index.js")).Should().BeTrue();
        
        // Cleanup
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(targetPath))!, true);
    }

    [Fact]
    public async Task LinkPackageAsync_Should_Throw_When_Package_Not_Found()
    {
        // Arrange
        var packageName = "non-existent";
        var version = "1.0.0";
        var targetPath = Path.Combine(Path.GetTempPath(), "node_modules", packageName);

        // Act & Assert
        var act = async () => await _store.LinkPackageAsync(packageName, version, targetPath);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Package {packageName}@{version} not found in store");
    }

    [Fact]
    public async Task GetStoreSizeAsync_Should_Return_Total_Size()
    {
        // Arrange
        var packageContent = CreateTestPackage();
        
        using var stream1 = new MemoryStream(packageContent);
        await _store.AddPackageAsync("package1", "1.0.0", stream1);
        
        using var stream2 = new MemoryStream(packageContent);
        await _store.AddPackageAsync("package2", "2.0.0", stream2);

        // Act
        var size = await _store.GetStoreSizeAsync();

        // Assert
        size.Should().BeGreaterThan(0);
    }

    private byte[] CreateTestPackage()
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
        {
            var packageEntry = archive.CreateEntry("package/package.json");
            using (var entryStream = packageEntry.Open())
            {
                var content = Encoding.UTF8.GetBytes("""
                {
                  "name": "test-package",
                  "version": "1.0.0"
                }
                """);
                entryStream.Write(content, 0, content.Length);
            }

            var indexEntry = archive.CreateEntry("package/index.js");
            using (var entryStream = indexEntry.Open())
            {
                var content = Encoding.UTF8.GetBytes("console.log('Hello, World!');");
                entryStream.Write(content, 0, content.Length);
            }
        }
        return memoryStream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempStoreDirectory))
        {
            Directory.Delete(_tempStoreDirectory, true);
        }
    }
}
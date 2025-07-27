using System.Text;
using FluentAssertions;
using Jio.Core.Cache;
using Jio.Core.Configuration;
using Moq;

namespace Jio.Core.Tests.Cache;

public sealed class FileSystemPackageCacheTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly JioConfiguration _configuration;
    private readonly FileSystemPackageCache _cache;

    public FileSystemPackageCacheTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-cache-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        
        _configuration = new JioConfiguration
        {
            CacheDirectory = _tempDirectory
        };
        
        _cache = new FileSystemPackageCache(_configuration);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task ExistsAsync_WhenPackageDoesNotExist_ReturnsFalse()
    {
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WhenOnlyPackageFileExists_ReturnsFalse()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        // Delete metadata file
        var hash = ComputeHash("test-package", "1.0.0", "sha256-test");
        var metadataPath = Path.Combine(_tempDirectory, hash[..2], hash[2..4], $"{hash}.metadata.json");
        File.Delete(metadataPath);
        
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAsync_WhenBothFilesExist_ReturnsTrue()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_WhenPackageDoesNotExist_ReturnsNull()
    {
        var result = await _cache.GetAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenPackageExists_ReturnsValidStream()
    {
        var content = "test package content";
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        var result = await _cache.GetAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        
        result.Should().NotBeNull();
        using var reader = new StreamReader(result!);
        var readContent = await reader.ReadToEndAsync();
        readContent.Should().Be(content);
    }

    [Fact]
    public async Task GetAsync_UpdatesLastAccessTime()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        var beforeAccess = DateTime.UtcNow;
        await Task.Delay(10); // Ensure time difference
        
        var result = await _cache.GetAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        result?.Dispose();
        
        var cachedPackages = await _cache.ListAsync(CancellationToken.None);
        var package = cachedPackages.First();
        
        package.CachedAt.Should().BeBefore(beforeAccess);
    }

    [Fact]
    public async Task PutAsync_CreatesPackageFileAndMetadata()
    {
        var content = "test package content";
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        exists.Should().BeTrue();
        
        var retrievedStream = await _cache.GetAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        using var reader = new StreamReader(retrievedStream!);
        var retrievedContent = await reader.ReadToEndAsync();
        retrievedContent.Should().Be(content);
    }

    [Fact]
    public async Task PutAsync_CreatesNestedDirectoryStructure()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        var hash = ComputeHash("test-package", "1.0.0", "sha256-test");
        var expectedDir = Path.Combine(_tempDirectory, hash[..2], hash[2..4]);
        
        Directory.Exists(expectedDir).Should().BeTrue();
    }

    [Fact]
    public async Task PutAsync_WhenStreamThrowsException_CleansUpFiles()
    {
        var errorStream = new ErrorStream();
        
        var act = async () => await _cache.PutAsync("test-package", "1.0.0", "sha256-test", errorStream, CancellationToken.None);
        
        await act.Should().ThrowAsync<IOException>();
        
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        exists.Should().BeFalse();
    }

    private class ErrorStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 100;
        public override long Position { get; set; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new IOException("Test exception");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await Task.Delay(1, cancellationToken);
            throw new IOException("Test exception");
        }
    }

    [Fact]
    public async Task PutAsync_OverwritesExistingPackage()
    {
        var originalContent = "original content";
        var newContent = "new content";
        
        var originalStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", originalStream, CancellationToken.None);
        
        var newStream = new MemoryStream(Encoding.UTF8.GetBytes(newContent));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", newStream, CancellationToken.None);
        
        var retrievedStream = await _cache.GetAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        using var reader = new StreamReader(retrievedStream!);
        var retrievedContent = await reader.ReadToEndAsync();
        retrievedContent.Should().Be(newContent);
    }

    [Fact]
    public async Task GetCacheSizeAsync_WhenCacheIsEmpty_ReturnsZero()
    {
        var size = await _cache.GetCacheSizeAsync(CancellationToken.None);
        
        size.Should().Be(0);
    }

    [Fact]
    public async Task GetCacheSizeAsync_WhenCacheHasFiles_ReturnsCorrectSize()
    {
        var content1 = "test content 1";
        var content2 = "test content 2";
        
        var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(content1));
        var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(content2));
        
        await _cache.PutAsync("package1", "1.0.0", "sha256-test1", stream1, CancellationToken.None);
        await _cache.PutAsync("package2", "1.0.0", "sha256-test2", stream2, CancellationToken.None);
        
        var size = await _cache.GetCacheSizeAsync(CancellationToken.None);
        
        size.Should().BeGreaterThan(content1.Length + content2.Length);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllCachedFiles()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        await _cache.ClearAsync(CancellationToken.None);
        
        var exists = await _cache.ExistsAsync("test-package", "1.0.0", "sha256-test", CancellationToken.None);
        exists.Should().BeFalse();
        
        var size = await _cache.GetCacheSizeAsync(CancellationToken.None);
        size.Should().Be(0);
    }

    [Fact]
    public async Task ClearAsync_WhenCacheDirectoryDoesNotExist_DoesNotThrow()
    {
        Directory.Delete(_tempDirectory, true);
        
        var act = async () => await _cache.ClearAsync(CancellationToken.None);
        
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListAsync_WhenCacheIsEmpty_ReturnsEmptyList()
    {
        var packages = await _cache.ListAsync(CancellationToken.None);
        
        packages.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_WhenCacheHasPackages_ReturnsCorrectPackages()
    {
        var content1 = "test content 1";
        var content2 = "test content 2";
        
        var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(content1));
        var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(content2));
        
        await _cache.PutAsync("package1", "1.0.0", "sha256-test1", stream1, CancellationToken.None);
        await _cache.PutAsync("package2", "2.0.0", "sha256-test2", stream2, CancellationToken.None);
        
        var packages = await _cache.ListAsync(CancellationToken.None);
        
        packages.Should().HaveCount(2);
        packages.Should().Contain(p => p.Name == "package1" && p.Version == "1.0.0");
        packages.Should().Contain(p => p.Name == "package2" && p.Version == "2.0.0");
        packages.All(p => p.Size > 0).Should().BeTrue();
        packages.All(p => p.CachedAt > DateTime.MinValue).Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_WhenMetadataFileIsCorrupted_SkipsCorruptedEntries()
    {
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        await _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, CancellationToken.None);
        
        // Corrupt metadata file
        var hash = ComputeHash("test-package", "1.0.0", "sha256-test");
        var metadataPath = Path.Combine(_tempDirectory, hash[..2], hash[2..4], $"{hash}.metadata.json");
        await File.WriteAllTextAsync(metadataPath, "invalid json");
        
        var packages = await _cache.ListAsync(CancellationToken.None);
        
        packages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("package", "1.0.0", "sha256-test")]
    [InlineData("@scope/package", "1.0.0-beta.1", "sha512-longhash")]
    [InlineData("package-with-dashes", "1.0.0+build.1", "sha1-short")]
    public async Task PutAndGetAsync_WithVariousPackageNames_WorksCorrectly(string name, string version, string integrity)
    {
        var content = $"content for {name}@{version}";
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        
        await _cache.PutAsync(name, version, integrity, packageStream, CancellationToken.None);
        
        var exists = await _cache.ExistsAsync(name, version, integrity, CancellationToken.None);
        exists.Should().BeTrue();
        
        var retrievedStream = await _cache.GetAsync(name, version, integrity, CancellationToken.None);
        using var reader = new StreamReader(retrievedStream!);
        var retrievedContent = await reader.ReadToEndAsync();
        retrievedContent.Should().Be(content);
    }

    [Fact]
    public async Task Operations_WithCancellationToken_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        
        var putTask = _cache.PutAsync("test-package", "1.0.0", "sha256-test", packageStream, cts.Token);
        await putTask.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    private static string ComputeHash(string name, string version, string integrity)
    {
        var input = $"{name}@{version}#{integrity}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
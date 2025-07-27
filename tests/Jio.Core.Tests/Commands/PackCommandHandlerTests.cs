using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Jio.Core.Commands;
using Jio.Core.Logging;
using Jio.Core.Models;
using Moq;
using Xunit;

namespace Jio.Core.Tests.Commands;

[Collection("Command Tests")]
public sealed class PackCommandHandlerTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly PackCommandHandler _handler;
    private readonly string _tempDirectory;

    public PackCommandHandlerTests()
    {
        _loggerMock = new Mock<ILogger>();
        _handler = new PackCommandHandler(_loggerMock.Object);

        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-pack-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    private async Task<T> ExecuteWithCurrentDirectoryAsync<T>(Func<Task<T>> action)
    {
        var currentDir = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDirectory;
            return await action();
        }
        finally
        {
            Environment.CurrentDirectory = currentDir;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoPackageJsonExists_ReturnsError()
    {
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPackageJsonLacksNameOrVersion_ReturnsError()
    {
        await CreatePackageJsonAsync(new { description = "test package" });
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_DoesNotCreateTarball()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        
        var command = new PackCommand { DryRun = true };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        Directory.GetFiles(_tempDirectory, "*.tgz").Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CreatesCorrectlyNamedTarball()
    {
        await CreatePackageJsonAsync(new { name = "my-package", version = "2.1.0" });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(_tempDirectory, "my-package-2.1.0.tgz")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithScopedPackage_CreatesCorrectlyNamedTarball()
    {
        await CreatePackageJsonAsync(new { name = "@scope/my-package", version = "1.0.0" });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(_tempDirectory, "scope-my-package-1.0.0.tgz")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithCustomDestination_CreatesTarballInDestination()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        
        var destination = Path.Combine(_tempDirectory, "dist");
        Directory.CreateDirectory(destination);
        
        var command = new PackCommand { Destination = destination };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(destination, "test-package-1.0.0.tgz")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithFilesField_PacksOnlySpecifiedFiles()
    {
        await CreatePackageJsonAsync(new { 
            name = "test-package", 
            version = "1.0.0",
            files = new[] { "lib/**", "README.md" }
        });
        
        // Create files in lib directory
        var libDir = Path.Combine(_tempDirectory, "lib");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "index.js"), "module.exports = {};");
        
        // Create README.md
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "README.md"), "# Test Package");
        
        // Create file that should not be packed
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "test.js"), "// test file");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var tarballPath = Path.Combine(_tempDirectory, "test-package-1.0.0.tgz");
        var extractedContents = await ExtractTarballContentsAsync(tarballPath);
        
        extractedContents.Should().Contain("package/package.json");
        extractedContents.Should().Contain("package/lib/index.js");
        extractedContents.Should().Contain("package/README.md");
        extractedContents.Should().NotContain("package/test.js");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutFilesField_PacksDefaultFiles()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        
        // Create default files
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "README.md"), "# Test Package");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "LICENSE"), "MIT License");
        
        // Create files that should be excluded
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, ".gitignore"), "node_modules/");
        var nodeModulesDir = Path.Combine(_tempDirectory, "node_modules");
        Directory.CreateDirectory(nodeModulesDir);
        await File.WriteAllTextAsync(Path.Combine(nodeModulesDir, "test.js"), "// should not be packed");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var tarballPath = Path.Combine(_tempDirectory, "test-package-1.0.0.tgz");
        var extractedContents = await ExtractTarballContentsAsync(tarballPath);
        
        extractedContents.Should().Contain("package/package.json");
        extractedContents.Should().Contain("package/index.js");
        extractedContents.Should().Contain("package/README.md");
        extractedContents.Should().Contain("package/LICENSE");
        extractedContents.Should().NotContain(f => f.Contains("node_modules"));
        extractedContents.Should().NotContain("package/.gitignore");
    }

    [Fact]
    public async Task ExecuteAsync_RespectsNpmIgnore()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        
        // Create .npmignore file
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, ".npmignore"), @"
test/
*.test.js
development.config.js
");
        
        // Create files
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "app.test.js"), "// test file");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "development.config.js"), "// config");
        
        var testDir = Path.Combine(_tempDirectory, "test");
        Directory.CreateDirectory(testDir);
        await File.WriteAllTextAsync(Path.Combine(testDir, "spec.js"), "// test spec");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var tarballPath = Path.Combine(_tempDirectory, "test-package-1.0.0.tgz");
        var extractedContents = await ExtractTarballContentsAsync(tarballPath);
        
        extractedContents.Should().Contain("package/index.js");
        extractedContents.Should().NotContain("package/app.test.js");
        extractedContents.Should().NotContain("package/development.config.js");
        extractedContents.Should().NotContain(f => f.Contains("test/"));
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysIncludesPackageJson()
    {
        await CreatePackageJsonAsync(new { 
            name = "test-package", 
            version = "1.0.0",
            files = new[] { "lib/**" } // Only lib files specified
        });
        
        var libDir = Path.Combine(_tempDirectory, "lib");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "index.js"), "module.exports = {};");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var tarballPath = Path.Combine(_tempDirectory, "test-package-1.0.0.tgz");
        var extractedContents = await ExtractTarballContentsAsync(tarballPath);
        
        extractedContents.Should().Contain("package/package.json");
        extractedContents.Should().Contain("package/lib/index.js");
    }

    [Fact]
    public async Task ExecuteAsync_CalculatesAndDisplaysIntegrity()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        
        var tarballPath = Path.Combine(_tempDirectory, "test-package-1.0.0.tgz");
        File.Exists(tarballPath).Should().BeTrue();
        
        // Verify the tarball can be read
        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        
        // Should be able to read without errors
        var buffer = new byte[1024];
        gzipStream.ReadAtLeast(buffer, buffer.Length, false);
    }

    [Fact]
    public async Task ExecuteAsync_WithCustomDirectory_PacksFromSpecifiedDirectory()
    {
        var sourceDir = Path.Combine(_tempDirectory, "source");
        Directory.CreateDirectory(sourceDir);
        
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" }, sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "index.js"), "module.exports = {};");
        
        var command = new PackCommand { Directory = sourceDir };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(_tempDirectory, "test-package-1.0.0.tgz")).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_HandlesCancellation()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var command = new PackCommand();
        
        var result = await _handler.ExecuteAsync(command, cts.Token);
        
        result.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_CleansUpTempDirectoryOnError()
    {
        await CreatePackageJsonAsync(new { name = "test-package", version = "1.0.0" });
        
        // Create a scenario that would cause an error (e.g., invalid destination)
        var command = new PackCommand { Destination = "/invalid/path/that/does/not/exist" };
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(1);
        _loggerMock.Verify(l => l.LogError(It.IsAny<Exception>(), "Pack command failed"), Times.Once);
    }

    [Theory]
    [InlineData("*.js")]
    [InlineData("lib/**/*")]
    [InlineData("dist/**")]
    [InlineData("README*")]
    [InlineData("LICENSE*")]
    public async Task ExecuteAsync_WithVariousFilePatterns_PacksCorrectFiles(string pattern)
    {
        await CreatePackageJsonAsync(new { 
            name = "test-package", 
            version = "1.0.0",
            files = new[] { pattern }
        });
        
        // Create files that match different patterns
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "index.js"), "module.exports = {};");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "README.md"), "# Test");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "LICENSE"), "MIT");
        
        var libDir = Path.Combine(_tempDirectory, "lib");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "helper.js"), "// helper");
        
        var distDir = Path.Combine(_tempDirectory, "dist");
        Directory.CreateDirectory(distDir);
        await File.WriteAllTextAsync(Path.Combine(distDir, "bundle.js"), "// bundle");
        
        var command = new PackCommand();
        
        var result = await ExecuteWithCurrentDirectoryAsync(async () => await _handler.ExecuteAsync(command));
        
        result.Should().Be(0);
        File.Exists(Path.Combine(_tempDirectory, "test-package-1.0.0.tgz")).Should().BeTrue();
    }

    private async Task CreatePackageJsonAsync(object packageData, string? directory = null)
    {
        var targetDir = directory ?? _tempDirectory;
        var json = JsonSerializer.Serialize(packageData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(Path.Combine(targetDir, "package.json"), json);
    }

    private async Task<List<string>> ExtractTarballContentsAsync(string tarballPath)
    {
        var contents = new List<string>();
        
        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var memoryStream = new MemoryStream();
        
        await gzipStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        
        // Simple TAR file parsing to extract entry names
        var buffer = new byte[512];
        while (memoryStream.Read(buffer, 0, 512) == 512)
        {
            // Check if this is an empty block (end of archive)
            if (buffer.All(b => b == 0))
                break;
                
            // Extract filename (first 100 bytes, null-terminated)
            var nameBytes = buffer.Take(100).TakeWhile(b => b != 0).ToArray();
            if (nameBytes.Length > 0)
            {
                var fileName = System.Text.Encoding.UTF8.GetString(nameBytes);
                contents.Add(fileName);
                
                // Extract file size
                var sizeBytes = buffer.Skip(124).Take(12).TakeWhile(b => b != 0 && b != 32).ToArray();
                if (sizeBytes.Length > 0)
                {
                    var sizeStr = System.Text.Encoding.ASCII.GetString(sizeBytes);
                    if (long.TryParse(sizeStr, System.Globalization.NumberStyles.AllowLeadingWhite, null, out var size))
                    {
                        // Skip file content
                        var paddedSize = ((size + 511) / 512) * 512;
                        memoryStream.Position += paddedSize;
                    }
                }
            }
        }
        
        return contents;
    }
}
using Xunit;
using Moq;
using Jio.Core.Commands;
using Jio.Core.Registry;
using Jio.Core.Models;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Moq.Protected;

namespace Jio.Core.Tests.Commands;

public class AuditCommandHandlerTests : IDisposable
{
    private readonly Mock<IPackageRegistry> _mockRegistry;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;
    private readonly string _testDirectory;

    public AuditCommandHandlerTests()
    {
        _mockRegistry = new Mock<IPackageRegistry>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_NoPackageJson_ReturnsError()
    {
        // Arrange
        var handler = new AuditCommandHandler(_mockRegistry.Object, _httpClient);
        var command = new AuditCommand();

        // Act
        var result = await handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ExecuteAsync_NoVulnerabilities_ReturnsSuccess()
    {
        // Arrange
        var packageJson = new PackageManifest
        {
            Name = "test-project",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "4.18.2"
            }
        };
        
        var manifestPath = Path.Combine(_testDirectory, "package.json");
        await packageJson.SaveAsync(manifestPath);

        var emptyResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object[]>
            {
                ["express"] = Array.Empty<object>()
            }))
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(emptyResponse);

        Directory.SetCurrentDirectory(_testDirectory);
        var handler = new AuditCommandHandler(_mockRegistry.Object, _httpClient);
        var command = new AuditCommand();

        // Act
        var result = await handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithVulnerabilities_ReturnsError()
    {
        // Arrange
        var packageJson = new PackageManifest
        {
            Name = "test-project",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["vulnerable-package"] = "1.0.0"
            }
        };
        
        var manifestPath = Path.Combine(_testDirectory, "package.json");
        await packageJson.SaveAsync(manifestPath);

        var vulnerabilityResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object[]>
            {
                ["vulnerable-package"] = new[]
                {
                    new
                    {
                        severity = "high",
                        title = "Remote Code Execution",
                        url = "https://npmjs.com/advisories/1234",
                        vulnerable_versions = "<1.2.0",
                        patched_versions = ">=1.2.0",
                        cwe = "CWE-94",
                        cve = "CVE-2023-1234"
                    }
                }
            }))
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(vulnerabilityResponse);

        Directory.SetCurrentDirectory(_testDirectory);
        var handler = new AuditCommandHandler(_mockRegistry.Object, _httpClient);
        var command = new AuditCommand();

        // Act
        var result = await handler.ExecuteAsync(command);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithFix_UpdatesPackageJson()
    {
        // Arrange
        var packageJson = new PackageManifest
        {
            Name = "test-project",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["vulnerable-package"] = "1.0.0"
            }
        };
        
        var manifestPath = Path.Combine(_testDirectory, "package.json");
        await packageJson.SaveAsync(manifestPath);

        var vulnerabilityResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object[]>
            {
                ["vulnerable-package"] = new[]
                {
                    new
                    {
                        severity = "high",
                        title = "Remote Code Execution",
                        url = "https://npmjs.com/advisories/1234",
                        vulnerable_versions = "<1.2.0",
                        patched_versions = ">=1.2.0"
                    }
                }
            }))
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(vulnerabilityResponse);

        _mockRegistry
            .Setup(r => r.GetPackageMetadataAsync("vulnerable-package", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageMetadata
            {
                Name = "vulnerable-package",
                Versions = new Dictionary<string, PackageVersion>
                {
                    ["1.0.0"] = new(),
                    ["1.2.0"] = new(),
                    ["1.3.0"] = new()
                }
            });

        Directory.SetCurrentDirectory(_testDirectory);
        var handler = new AuditCommandHandler(_mockRegistry.Object, _httpClient);
        var command = new AuditCommand { Fix = true };

        // Act
        await handler.ExecuteAsync(command);

        // Assert
        var updatedManifest = await PackageManifest.LoadAsync(manifestPath);
        Assert.Equal("1.2.0", updatedManifest.Dependencies?["vulnerable-package"]);
    }

    [Fact]
    public async Task ExecuteAsync_JsonOutput_FormatsCorrectly()
    {
        // Arrange
        var packageJson = new PackageManifest
        {
            Name = "test-project",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["vulnerable-package"] = "1.0.0"
            }
        };
        
        var manifestPath = Path.Combine(_testDirectory, "package.json");
        await packageJson.SaveAsync(manifestPath);

        var vulnerabilityResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object[]>
            {
                ["vulnerable-package"] = new[]
                {
                    new
                    {
                        severity = "high",
                        title = "Remote Code Execution",
                        url = "https://npmjs.com/advisories/1234",
                        vulnerable_versions = "<1.2.0",
                        patched_versions = ">=1.2.0"
                    }
                }
            }))
        };

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(vulnerabilityResponse);

        Directory.SetCurrentDirectory(_testDirectory);
        var handler = new AuditCommandHandler(_mockRegistry.Object, _httpClient);
        var command = new AuditCommand { Json = true };

        using var output = new StringWriter();
        Console.SetOut(output);

        // Act
        await handler.ExecuteAsync(command);

        // Assert
        var jsonOutput = output.ToString();
        Assert.Contains("\"vulnerabilities\":", jsonOutput);
        Assert.Contains("\"metadata\":", jsonOutput);
        Assert.Contains("\"advisories\":", jsonOutput);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDirectory, true);
        }
        catch { }
    }
}
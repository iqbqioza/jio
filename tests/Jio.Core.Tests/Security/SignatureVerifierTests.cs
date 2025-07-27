using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jio.Core.Configuration;
using Jio.Core.Logging;
using Jio.Core.Security;
using Moq;
using Moq.Protected;

namespace Jio.Core.Tests.Security;

public sealed class SignatureVerifierTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly JioConfiguration _configuration;
    private readonly SignatureVerifier _verifier;
    private readonly string _tempDirectory;
    private readonly RSA _testRsa;
    private readonly string _testPublicKey;
    private readonly string _testPrivateKey;

    public SignatureVerifierTests()
    {
        _loggerMock = new Mock<ILogger>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-signature-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        
        _configuration = new JioConfiguration
        {
            CacheDirectory = _tempDirectory,
            Registry = "https://registry.npmjs.org",
            VerifySignatures = true
        };

        // Generate test RSA key pair
        _testRsa = RSA.Create(2048);
        _testPublicKey = Convert.ToBase64String(_testRsa.ExportRSAPublicKey());
        _testPrivateKey = Convert.ToBase64String(_testRsa.ExportRSAPrivateKey());

        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        
        _verifier = new SignatureVerifier(_loggerMock.Object, _httpClient, _configuration);
    }

    public void Dispose()
    {
        _testRsa?.Dispose();
        _httpClient?.Dispose();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WhenVerificationDisabled_ReturnsTrue()
    {
        _configuration.VerifySignatures = false;
        
        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test package"));
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WhenNoSignatureFound_ReturnsFalse()
    {
        var notFoundResponse = new HttpResponseMessage(HttpStatusCode.NotFound);
        
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(notFoundResponse);

        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test package"));
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeFalse();
        _loggerMock.Verify(l => l.LogWarning("No signature found for test-package@1.0.0"), Times.Once);
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WithValidSignature_ReturnsTrue()
    {
        var testData = "test package content";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var signature = _testRsa.SignData(testDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signature);

        var signatureInfo = new SignatureInfo
        {
            Signature = signatureBase64,
            PublicKey = _testPublicKey,
            Algorithm = "RSA-SHA256"
        };

        SetupSignatureResponse(signatureInfo);

        var packageStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogDebug("Signature verification passed for test-package@1.0.0"), Times.Once);
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WithInvalidSignature_ReturnsFalse()
    {
        var testData = "test package content";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var invalidSignature = Convert.ToBase64String(new byte[256]); // Invalid signature

        var signatureInfo = new SignatureInfo
        {
            Signature = invalidSignature,
            PublicKey = _testPublicKey,
            Algorithm = "RSA-SHA256"
        };

        SetupSignatureResponse(signatureInfo);

        var packageStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeFalse();
        _loggerMock.Verify(l => l.LogWarning("Signature verification failed for test-package@1.0.0"), Times.Once);
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WithDifferentData_ReturnsFalse()
    {
        var originalData = "original package content";
        var modifiedData = "modified package content";
        var originalDataBytes = Encoding.UTF8.GetBytes(originalData);
        var modifiedDataBytes = Encoding.UTF8.GetBytes(modifiedData);
        
        // Sign original data
        var signature = _testRsa.SignData(originalDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signature);

        var signatureInfo = new SignatureInfo
        {
            Signature = signatureBase64,
            PublicKey = _testPublicKey,
            Algorithm = "RSA-SHA256"
        };

        SetupSignatureResponse(signatureInfo);

        // Verify with modified data
        var packageStream = new MemoryStream(modifiedDataBytes);
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetPackageSignatureAsync_WithSuccessfulResponse_ReturnsSignatureInfo()
    {
        var signatureInfo = new SignatureInfo
        {
            Signature = "test-signature",
            PublicKey = _testPublicKey,
            Algorithm = "RSA-SHA256"
        };

        SetupSignatureResponse(signatureInfo);
        
        var result = await _verifier.GetPackageSignatureAsync("test-package", "1.0.0");
        
        result.Should().NotBeNull();
        result!.Signature.Should().Be("test-signature");
        result.PublicKey.Should().Be(_testPublicKey);
        result.Algorithm.Should().Be("RSA-SHA256");
    }

    [Fact]
    public async Task GetPackageSignatureAsync_WithAlternativeEndpoint_ReturnsSignatureInfo()
    {
        var signatureInfo = new SignatureInfo
        {
            Signature = "test-signature",
            PublicKey = _testPublicKey,
            Algorithm = "RSA-SHA256"
        };

        // First endpoint returns 404, second returns success
        _httpMessageHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(signatureInfo))
            });
        
        var result = await _verifier.GetPackageSignatureAsync("test-package", "1.0.0");
        
        result.Should().NotBeNull();
        result!.Signature.Should().Be("test-signature");
    }

    [Fact]
    public async Task GetPackageSignatureAsync_WithBothEndpointsFailing_ReturnsNull()
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        
        var result = await _verifier.GetPackageSignatureAsync("test-package", "1.0.0");
        
        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifySignatureAsync_WithValidData_ReturnsTrue()
    {
        var testData = "test data for verification";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var signature = _testRsa.SignData(testDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signature);

        var dataStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifySignatureAsync(dataStream, signatureBase64, _testPublicKey);
        
        result.Should().BeTrue();
        dataStream.Position.Should().Be(0); // Stream position should be restored
    }

    [Fact]
    public async Task VerifySignatureAsync_WithInvalidSignature_ReturnsFalse()
    {
        var testData = "test data for verification";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var invalidSignatureBase64 = Convert.ToBase64String(new byte[256]);

        var dataStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifySignatureAsync(dataStream, invalidSignatureBase64, _testPublicKey);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySignatureAsync_WithInvalidPublicKey_ReturnsFalse()
    {
        var testData = "test data for verification";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var signature = _testRsa.SignData(testDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signature);
        var invalidPublicKey = Convert.ToBase64String(new byte[256]);

        var dataStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifySignatureAsync(dataStream, signatureBase64, invalidPublicKey);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifySignatureAsync_WithMalformedBase64_ReturnsFalse()
    {
        var testData = "test data for verification";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var malformedSignature = "not-base64!@#$";

        var dataStream = new MemoryStream(testDataBytes);
        
        var result = await _verifier.VerifySignatureAsync(dataStream, malformedSignature, _testPublicKey);
        
        result.Should().BeFalse();
        _loggerMock.Verify(l => l.LogDebug(It.Is<string>(s => s.StartsWith("Signature verification failed:")), It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task GetTrustedPublishersAsync_WhenFileDoesNotExist_CreatesDefaultList()
    {
        var result = await _verifier.GetTrustedPublishersAsync();
        
        result.Should().NotBeEmpty();
        result.Should().ContainSingle(p => p.Name == "npm-registry");
        result.First().Packages.Should().Contain("*");
        
        // Should create the trusted keys file
        var trustedKeysPath = Path.Combine(_tempDirectory, "trusted-keys.json");
        File.Exists(trustedKeysPath).Should().BeTrue();
    }

    [Fact]
    public async Task GetTrustedPublishersAsync_WhenFileExists_LoadsFromFile()
    {
        var publishers = new List<TrustedPublisher>
        {
            new TrustedPublisher
            {
                Name = "custom-publisher",
                PublicKey = _testPublicKey,
                Packages = new List<string> { "my-package" },
                ValidFrom = DateTime.UtcNow.AddDays(-1),
                ValidUntil = DateTime.UtcNow.AddDays(365)
            }
        };

        var trustedKeysPath = Path.Combine(_tempDirectory, "trusted-keys.json");
        var json = JsonSerializer.Serialize(publishers, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(trustedKeysPath, json);
        
        var result = await _verifier.GetTrustedPublishersAsync();
        
        result.Should().ContainSingle();
        result.First().Name.Should().Be("custom-publisher");
        result.First().PublicKey.Should().Be(_testPublicKey);
        result.First().Packages.Should().Contain("my-package");
    }

    [Fact]
    public async Task GetTrustedPublishersAsync_WithCorruptedFile_ReturnsEmptyList()
    {
        var trustedKeysPath = Path.Combine(_tempDirectory, "trusted-keys.json");
        await File.WriteAllTextAsync(trustedKeysPath, "{ invalid json }");
        
        var result = await _verifier.GetTrustedPublishersAsync();
        
        result.Should().BeEmpty();
        _loggerMock.Verify(l => l.LogWarning(It.Is<string>(s => s.StartsWith("Failed to load trusted publishers:")), It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WithException_ReturnsFalse()
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test package"));
        
        var result = await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream);
        
        result.Should().BeFalse();
        _loggerMock.Verify(l => l.LogDebug(It.Is<string>(s => s.Contains("Failed to get signature")), It.IsAny<object[]>()), Times.Once);
        _loggerMock.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("No signature found")), It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task VerifyPackageSignatureAsync_WithCancellation_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var packageStream = new MemoryStream(Encoding.UTF8.GetBytes("test package"));
        
        var act = async () => await _verifier.VerifyPackageSignatureAsync("test-package", "1.0.0", packageStream, cts.Token);
        
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerifySignatureAsync_PreservesStreamPosition()
    {
        var testData = "test data for verification";
        var testDataBytes = Encoding.UTF8.GetBytes(testData);
        var signature = _testRsa.SignData(testDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signature);

        var dataStream = new MemoryStream(testDataBytes);
        dataStream.Position = 5; // Set to non-zero position
        var originalPosition = dataStream.Position;
        
        var result = await _verifier.VerifySignatureAsync(dataStream, signatureBase64, _testPublicKey);
        
        result.Should().BeTrue();
        dataStream.Position.Should().Be(originalPosition);
    }

    [Theory]
    [InlineData("test-package", "1.0.0")]
    [InlineData("@scope/package", "2.1.0-beta.1")]
    [InlineData("package-with-dashes", "1.0.0+build.1")]
    public async Task GetPackageSignatureAsync_WithVariousPackageNames_ConstructsCorrectUrls(string packageName, string version)
    {
        var capturedRequests = new List<string>();
        
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) => 
                capturedRequests.Add(req.RequestUri!.ToString()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        
        await _verifier.GetPackageSignatureAsync(packageName, version);
        
        capturedRequests.Should().HaveCount(2);
        capturedRequests[0].Should().Contain($"{packageName}/-/{packageName}-{version}.tgz.sig");
        capturedRequests[1].Should().Contain($"{packageName}/{version}/signature");
    }

    private void SetupSignatureResponse(SignatureInfo signatureInfo)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(signatureInfo))
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);
    }
}
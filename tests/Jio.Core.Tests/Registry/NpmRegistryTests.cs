using System.Net;
using System.Text;
using FluentAssertions;
using Jio.Core.Configuration;
using Jio.Core.Models;
using Jio.Core.Registry;
using Jio.Core.Cache;
using Moq;
using Moq.Protected;

namespace Jio.Core.Tests.Registry;

public class NpmRegistryTests
{
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;
    private readonly NpmRegistry _registry;

    public NpmRegistryTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);
        _configuration = new JioConfiguration
        {
            Registry = "https://registry.npmjs.org/"
        };
        var mockCache = new Mock<IPackageCache>();
        _registry = new NpmRegistry(_httpClient, _configuration, mockCache.Object);
    }

    [Fact]
    public async Task GetPackageManifestAsync_Should_Return_PackageManifest()
    {
        // Arrange
        var packageName = "express";
        var version = "4.18.2";
        var responseJson = """
        {
          "name": "express",
          "version": "4.18.2",
          "description": "Fast, unopinionated, minimalist web framework",
          "main": "index.js",
          "dependencies": {
            "accepts": "~1.3.8"
          },
          "devDependencies": {
            "eslint": "8.24.0"
          },
          "license": "MIT"
        }
        """;

        SetupHttpResponse($"{_configuration.Registry}{packageName}/{version}", responseJson);

        // Act
        var manifest = await _registry.GetPackageManifestAsync(packageName, version);

        // Assert
        manifest.Should().NotBeNull();
        manifest.Name.Should().Be("express");
        manifest.Version.Should().Be("4.18.2");
        manifest.Dependencies.Should().HaveCount(1);
        manifest.DevDependencies.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetPackageVersionsAsync_Should_Return_Version_List()
    {
        // Arrange
        var packageName = "express";
        var responseJson = """
        {
          "name": "express",
          "versions": {
            "4.18.0": { "name": "express", "version": "4.18.0" },
            "4.18.1": { "name": "express", "version": "4.18.1" },
            "4.18.2": { "name": "express", "version": "4.18.2" }
          }
        }
        """;

        SetupHttpResponse($"{_configuration.Registry}{packageName}", responseJson);

        // Act
        var versions = await _registry.GetPackageVersionsAsync(packageName);

        // Assert
        versions.Should().HaveCount(3);
        versions.Should().Contain("4.18.0");
        versions.Should().Contain("4.18.1");
        versions.Should().Contain("4.18.2");
    }

    [Fact]
    public async Task DownloadPackageAsync_Should_Return_Package_Stream()
    {
        // Arrange
        var packageName = "express";
        var version = "4.18.2";
        var tarballUrl = "https://registry.npmjs.org/express/-/express-4.18.2.tgz";
        var manifestJson = $$"""
        {
          "name": "express",
          "version": "4.18.2",
          "dist": {
            "tarball": "{{tarballUrl}}"
          }
        }
        """;
        var packageContent = Encoding.UTF8.GetBytes("fake tarball content");

        SetupHttpResponse($"{_configuration.Registry}{packageName}/{version}", manifestJson);
        SetupHttpResponse(tarballUrl, packageContent);

        // Act
        var stream = await _registry.DownloadPackageAsync(packageName, version);

        // Assert
        stream.Should().NotBeNull();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        memoryStream.ToArray().Should().Equal(packageContent);
    }

    [Fact]
    public async Task GetPackageIntegrityAsync_Should_Return_Integrity_Hash()
    {
        // Arrange
        var packageName = "express";
        var version = "4.18.2";
        var integrity = "sha512-FN50kBqLrvxfT2SPBETQjccfWJNGJBBJuJ3e9QyJ4u3PTJl1N4OGNSx9geI1qBIsB5OQjLN2epCiI1HjGNNziRQ==";
        var responseJson = $$"""
        {
          "name": "express",
          "version": "4.18.2",
          "dist": {
            "integrity": "{{integrity}}"
          }
        }
        """;

        SetupHttpResponse($"{_configuration.Registry}{packageName}/{version}", responseJson);

        // Act
        var result = await _registry.GetPackageIntegrityAsync(packageName, version);

        // Assert
        result.Should().Be(integrity);
    }

    [Fact]
    public async Task GetPackageManifestAsync_Should_Throw_On_Http_Error()
    {
        // Arrange
        var packageName = "non-existent";
        var version = "1.0.0";

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent("Not Found")
            });

        // Act & Assert
        var act = async () => await _registry.GetPackageManifestAsync(packageName, version);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DownloadPackageAsync_Should_Throw_When_No_Tarball_Url()
    {
        // Arrange
        var packageName = "express";
        var version = "4.18.2";
        var manifestJson = """
        {
          "name": "express",
          "version": "4.18.2",
          "dist": {}
        }
        """;

        SetupHttpResponse($"{_configuration.Registry}{packageName}/{version}", manifestJson);

        // Act & Assert
        var act = async () => await _registry.DownloadPackageAsync(packageName, version);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"No tarball URL found for {packageName}@{version}");
    }

    private void SetupHttpResponse(string url, string content)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private void SetupHttpResponse(string url, byte[] content)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString() == url),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(content)
            });
    }
}
using System.Text.Json;
using FluentAssertions;
using Jio.Core.Models;

namespace Jio.Core.Tests.Models;

public class PackageManifestTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Should_Serialize_PackageManifest_Correctly()
    {
        // Arrange
        var manifest = new PackageManifest
        {
            Name = "test-package",
            Version = "1.0.0",
            Description = "Test package",
            Main = "index.js",
            License = "MIT",
            Author = "Test Author",
            Dependencies = new Dictionary<string, string>
            {
                ["express"] = "^4.18.0",
                ["lodash"] = "^4.17.21"
            },
            DevDependencies = new Dictionary<string, string>
            {
                ["typescript"] = "^5.0.0"
            },
            Scripts = new Dictionary<string, string>
            {
                ["test"] = "jest",
                ["build"] = "tsc"
            },
            Keywords = new List<string> { "test", "package" }
        };

        // Act
        var json = JsonSerializer.Serialize(manifest, _jsonOptions);
        var result = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("test-package");
        result.Version.Should().Be("1.0.0");
        result.Description.Should().Be("Test package");
        result.Dependencies.Should().HaveCount(2);
        result.Dependencies["express"].Should().Be("^4.18.0");
        result.DevDependencies.Should().HaveCount(1);
        result.Scripts.Should().HaveCount(2);
        result.Keywords.Should().HaveCount(2);
    }

    [Fact]
    public void Should_Deserialize_Real_PackageJson()
    {
        // Arrange
        var json = """
        {
          "name": "express",
          "version": "4.18.2",
          "description": "Fast, unopinionated, minimalist web framework",
          "main": "index.js",
          "dependencies": {
            "accepts": "~1.3.8",
            "array-flatten": "1.1.1"
          },
          "devDependencies": {
            "eslint": "8.24.0"
          },
          "scripts": {
            "test": "mocha --require test/support/env --reporter spec --bail --check-leaks test/ test/acceptance/",
            "test-ci": "nyc --reporter=lcovonly --reporter=text npm test"
          },
          "engines": {
            "node": ">= 0.10.0"
          },
          "license": "MIT",
          "repository": {
            "type": "git",
            "url": "https://github.com/expressjs/express.git"
          }
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.Name.Should().Be("express");
        manifest.Version.Should().Be("4.18.2");
        manifest.Dependencies.Should().HaveCount(2);
        manifest.DevDependencies.Should().HaveCount(1);
        manifest.Scripts.Should().HaveCount(2);
        manifest.Engines.Should().NotBeNull();
        manifest.Engines!["node"].Should().Be(">= 0.10.0");
        manifest.Repository.Should().NotBeNull();
    }

    [Fact]
    public void Should_Handle_Missing_Optional_Properties()
    {
        // Arrange
        var json = """
        {
          "name": "minimal-package",
          "version": "1.0.0"
        }
        """;

        // Act
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);

        // Assert
        manifest.Should().NotBeNull();
        manifest!.Name.Should().Be("minimal-package");
        manifest.Version.Should().Be("1.0.0");
        manifest.Description.Should().BeNull();
        manifest.Dependencies.Should().BeEmpty();
        manifest.DevDependencies.Should().BeEmpty();
        manifest.Scripts.Should().BeEmpty();
        manifest.Keywords.Should().BeNull();
    }
}
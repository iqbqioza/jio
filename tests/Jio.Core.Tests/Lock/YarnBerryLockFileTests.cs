using Xunit;
using Jio.Core.Lock;

namespace Jio.Core.Tests.Lock;

[Collection("LockFileTests")]
public class YarnBerryLockFileTests : IDisposable
{
    private readonly LockFileImporter _importer;
    private readonly string _testDirectory;

    public YarnBerryLockFileTests()
    {
        _importer = new LockFileImporter();
        _testDirectory = Path.Combine(Path.GetTempPath(), $"jio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ImportAsync_YarnBerry_SimplePackage()
    {
        // Arrange
        var lockContent = @"# This file is generated by running ""yarn install"" inside your project.
# Manual changes might be lost - proceed with caution!

__metadata:
  version: 6

""lodash@npm:^4.17.21"":
  version: 4.17.21
  resolution: ""lodash@npm:4.17.21""
  checksum: eb835a2e51d381e561e508ce932ca50d8b713a5580e6a3b7827b7e1c08e6d5d38f11355869c8c3e5084d0cde525cd67d100ae3347e63e8a6fd3b9b7ce6b18c8a
  languageName: node
  linkType: hard
";
        var lockFilePath = Path.Combine(_testDirectory, "yarn.lock");
        await File.WriteAllTextAsync(lockFilePath, lockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Packages);
        Assert.True(result.Packages.ContainsKey("lodash@4.17.21"));
        
        var lodash = result.Packages["lodash@4.17.21"];
        Assert.Equal("4.17.21", lodash.Version);
        Assert.Equal("lodash", lodash.Name);
        Assert.StartsWith("sha512-", lodash.Integrity);
    }

    [Fact]
    public async Task ImportAsync_YarnBerry_WithDependencies()
    {
        // Arrange
        var lockContent = @"# This file is generated by running ""yarn install"" inside your project.
# Manual changes might be lost - proceed with caution!

__metadata:
  version: 6

""express@npm:^4.18.2"":
  version: 4.18.2
  resolution: ""express@npm:4.18.2""
  dependencies:
    accepts: ~1.3.8
    array-flatten: 1.1.1
    body-parser: 1.20.1
    content-disposition: 0.5.4
    content-type: ~1.0.4
    cookie: 0.5.0
    cookie-signature: 1.0.6
    debug: 2.6.9
    depd: 2.0.0
    encodeurl: ~1.0.2
    escape-html: ~1.0.3
    etag: ~1.8.1
    finalhandler: 1.2.0
    fresh: 0.5.2
    http-errors: 2.0.0
    merge-descriptors: 1.0.1
    methods: ~1.1.2
    on-finished: 2.4.1
    parseurl: ~1.3.3
    path-to-regexp: 0.1.7
    proxy-addr: ~2.0.7
    qs: 6.11.0
    range-parser: ~1.2.1
    safe-buffer: 5.2.1
    send: 0.18.0
    serve-static: 1.15.0
    setprototypeof: 1.2.0
    statuses: 2.0.1
    type-is: ~1.6.18
    utils-merge: 1.0.1
    vary: ~1.1.2
  checksum: 3c4b9b076879442f6b968fe53d85d9f1a29ca6930b0b6c5425f2e3cfd8e5a0c696747aa4bdec0068ee5e1fb2336e2569b4e88a4681f988f6f0e2dcd93a9e6c2a
  languageName: node
  linkType: hard

""body-parser@npm:1.20.1"":
  version: 1.20.1
  resolution: ""body-parser@npm:1.20.1""
  dependencies:
    bytes: 3.1.2
    content-type: ~1.0.4
    debug: 2.6.9
    depd: 2.0.0
    destroy: 1.2.0
    http-errors: 2.0.0
    iconv-lite: 0.4.24
    on-finished: 2.4.1
    qs: 6.11.0
    raw-body: 2.5.1
    type-is: ~1.6.18
    unpipe: 1.0.0
  checksum: f1050dbac3bede6a78f0b87947a8d548ce43f91ccc718a50dd774f3c81f2d8b04693e52acf62659fad23101827dd318da1fb1363444ff9a8482b886a3e4a5266
  languageName: node
  linkType: hard

""bytes@npm:3.1.2"":
  version: 3.1.2
  resolution: ""bytes@npm:3.1.2""
  checksum: e4bcd3948d289c5127591fbedf10c0b639ccbf00243504e4e127374a15c3bc8eed0d28d4aaab08ff6f1cf2abc0cce6ba3085ed32f4f90e82a5683ce0014e1b6e
  languageName: node
  linkType: hard
";
        var lockFilePath = Path.Combine(_testDirectory, "yarn.lock");
        await File.WriteAllTextAsync(lockFilePath, lockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Packages.Count);
        
        // Check express package
        Assert.True(result.Packages.ContainsKey("express@4.18.2"));
        var express = result.Packages["express@4.18.2"];
        Assert.Equal("4.18.2", express.Version);
        Assert.NotNull(express.Dependencies);
        Assert.Equal(31, express.Dependencies.Count);
        Assert.Equal("~1.3.8", express.Dependencies["accepts"]);
        Assert.Equal("1.20.1", express.Dependencies["body-parser"]);
        
        // Check body-parser package
        Assert.True(result.Packages.ContainsKey("body-parser@1.20.1"));
        var bodyParser = result.Packages["body-parser@1.20.1"];
        Assert.Equal("1.20.1", bodyParser.Version);
        Assert.NotNull(bodyParser.Dependencies);
        Assert.Equal(12, bodyParser.Dependencies.Count);
        Assert.Equal("3.1.2", bodyParser.Dependencies["bytes"]);
        
        // Check bytes package
        Assert.True(result.Packages.ContainsKey("bytes@3.1.2"));
        var bytes = result.Packages["bytes@3.1.2"];
        Assert.Equal("3.1.2", bytes.Version);
    }

    [Fact]
    public async Task ImportAsync_YarnBerry_WithScopedPackages()
    {
        // Arrange
        var lockContent = @"# This file is generated by running ""yarn install"" inside your project.
# Manual changes might be lost - proceed with caution!

__metadata:
  version: 6

""@types/node@npm:^20.0.0"":
  version: 20.10.5
  resolution: ""@types/node@npm:20.10.5""
  dependencies:
    undici-types: ~5.26.4
  checksum: e216b679f545a8356960ce985a0e53c3a58fff0eacd855e180b9e223b8db2b5bd3ec4b43046f3c16b9c2abae0df79816df8352528723e4fb2dd593aa4af4d4e8
  languageName: node
  linkType: hard

""@babel/core@npm:^7.0.0"":
  version: 7.23.6
  resolution: ""@babel/core@npm:7.23.6""
  dependencies:
    ""@ampproject/remapping"": ^2.2.0
    ""@babel/code-frame"": ^7.23.5
    ""@babel/generator"": ^7.23.6
    ""@babel/helper-compilation-targets"": ^7.23.6
    ""@babel/helper-module-transforms"": ^7.23.3
    ""@babel/helpers"": ^7.23.6
    ""@babel/parser"": ^7.23.6
    ""@babel/template"": ^7.22.15
    ""@babel/traverse"": ^7.23.6
    ""@babel/types"": ^7.23.6
    convert-source-map: ^2.0.0
    debug: ^4.1.0
    gensync: ^1.0.0-beta.2
    json5: ^2.2.3
    semver: ^6.3.1
  checksum: 49cd61b99222c50330b6fd2afeb00e62e907403efa93f8d1ef3e8d77a1f5c8f24c4dd85630cf232e4255d67c97c72e7e6dd3c37708c962ad91f7c425e2b06a5a
  languageName: node
  linkType: hard
";
        var lockFilePath = Path.Combine(_testDirectory, "yarn.lock");
        await File.WriteAllTextAsync(lockFilePath, lockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Packages.Count);
        
        // Check scoped packages
        Assert.True(result.Packages.ContainsKey("@types/node@20.10.5"));
        var typesNode = result.Packages["@types/node@20.10.5"];
        Assert.Equal("20.10.5", typesNode.Version);
        Assert.Equal("@types/node", typesNode.Name);
        
        Assert.True(result.Packages.ContainsKey("@babel/core@7.23.6"));
        var babelCore = result.Packages["@babel/core@7.23.6"];
        Assert.Equal("7.23.6", babelCore.Version);
        Assert.Equal("@babel/core", babelCore.Name);
        Assert.NotNull(babelCore.Dependencies);
        Assert.Equal(15, babelCore.Dependencies.Count);
    }

    [Fact]
    public async Task ImportAsync_YarnBerry_WithPeerDependencies()
    {
        // Arrange
        var lockContent = @"# This file is generated by running ""yarn install"" inside your project.
# Manual changes might be lost - proceed with caution!

__metadata:
  version: 6

""react-dom@npm:^18.2.0"":
  version: 18.2.0
  resolution: ""react-dom@npm:18.2.0""
  dependencies:
    loose-envify: ^1.1.0
    scheduler: ^0.23.0
  peerDependencies:
    react: ^18.2.0
  checksum: 7d323310be5871f798549f91f9c9cfeb6e7d15c179d59602bd9339a7fa1d4d475e9426c3da2e092b5965f77ed22357c5e3a398b86d4a1e3f17b74b2f8e3c3f79
  languageName: node
  linkType: hard

""react@npm:^18.2.0"":
  version: 18.2.0
  resolution: ""react@npm:18.2.0""
  dependencies:
    loose-envify: ^1.1.0
  checksum: 88e38092da8839b830cc3b294c8e65db2a9ab89c1974216d53ad9bb57cf8d5c45e2de887cc15ba337cf4d039735282b0f5234f5e77134a24b8a4cf8ac95a9e3c
  languageName: node
  linkType: hard
";
        var lockFilePath = Path.Combine(_testDirectory, "yarn.lock");
        await File.WriteAllTextAsync(lockFilePath, lockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Packages.Count);
        
        // Check react-dom with peer dependencies
        Assert.True(result.Packages.ContainsKey("react-dom@18.2.0"));
        var reactDom = result.Packages["react-dom@18.2.0"];
        Assert.Equal("18.2.0", reactDom.Version);
        Assert.NotNull(reactDom.Dependencies);
        Assert.Equal(2, reactDom.Dependencies.Count);
        // Note: Peer dependencies are not currently imported as regular dependencies
        
        // Check react
        Assert.True(result.Packages.ContainsKey("react@18.2.0"));
        var react = result.Packages["react@18.2.0"];
        Assert.Equal("18.2.0", react.Version);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDirectory, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
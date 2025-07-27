using Xunit;
using Jio.Core.Lock;

namespace Jio.Core.Tests.Lock;

[Collection("LockFileTests")]
public class RealWorldLockFileTests : IDisposable
{
    private readonly LockFileImporter _importer;
    private readonly string _testDirectory;

    public RealWorldLockFileTests()
    {
        _importer = new LockFileImporter();
        _testDirectory = Path.Combine(Path.GetTempPath(), $"jio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ImportAsync_ComplexNpmLockFile_HandlesNestedDependencies()
    {
        // Arrange - Real-world example with nested dependencies and peer dependencies
        var npmLockContent = @"{
  ""name"": ""real-project"",
  ""version"": ""1.0.0"",
  ""lockfileVersion"": 3,
  ""requires"": true,
  ""packages"": {
    """": {
      ""name"": ""real-project"",
      ""version"": ""1.0.0"",
      ""dependencies"": {
        ""react"": ""^18.2.0"",
        ""react-dom"": ""^18.2.0""
      },
      ""devDependencies"": {
        ""@types/react"": ""^18.2.0"",
        ""typescript"": ""^5.0.0""
      }
    },
    ""node_modules/react"": {
      ""version"": ""18.2.0"",
      ""resolved"": ""https://registry.npmjs.org/react/-/react-18.2.0.tgz"",
      ""integrity"": ""sha512-/3IjMdb2L9QbBdWiW5e3P2/npwMBaU9mHCSCUzNln0ZCYbcfTsGbTJrU/kG"",
      ""dependencies"": {
        ""loose-envify"": ""^1.1.0""
      },
      ""engines"": {
        ""node"": "">=0.10.0""
      }
    },
    ""node_modules/react-dom"": {
      ""version"": ""18.2.0"",
      ""resolved"": ""https://registry.npmjs.org/react-dom/-/react-dom-18.2.0.tgz"",
      ""integrity"": ""sha512-6IMTriUmvsjHUjNtEDudZfuDQUoWXVxKHhlEGSk81n4YFS+r/Kl99wXiwlVXtPBtJenozv2P+hxDsw9eA0XFTQ"",
      ""dependencies"": {
        ""loose-envify"": ""^1.1.0"",
        ""scheduler"": ""^0.23.0""
      },
      ""peerDependencies"": {
        ""react"": ""^18.2.0""
      }
    },
    ""node_modules/loose-envify"": {
      ""version"": ""1.4.0"",
      ""resolved"": ""https://registry.npmjs.org/loose-envify/-/loose-envify-1.4.0.tgz"",
      ""integrity"": ""sha512-lyuxPGr/Wfhrlem2CL/xFgrtzFKrMwpKbTvTxd4d7q"",
      ""dependencies"": {
        ""js-tokens"": ""^3.0.0 || ^4.0.0""
      },
      ""bin"": {
        ""loose-envify"": ""cli.js""
      }
    },
    ""node_modules/js-tokens"": {
      ""version"": ""4.0.0"",
      ""resolved"": ""https://registry.npmjs.org/js-tokens/-/js-tokens-4.0.0.tgz"",
      ""integrity"": ""sha512-RdJUflcE3cUzKiMqQgsCu06FPu9UdIJO0beYbPhHN4k6apgJxBT4"" 
    },
    ""node_modules/scheduler"": {
      ""version"": ""0.23.0"",
      ""resolved"": ""https://registry.npmjs.org/scheduler/-/scheduler-0.23.0.tgz"",
      ""integrity"": ""sha512-CtuThmgHNg7zIZWAXi3AsyIzA3n4xmN1mHq0Ft4"",
      ""dependencies"": {
        ""loose-envify"": ""^1.1.0""
      }
    },
    ""node_modules/@types/react"": {
      ""version"": ""18.2.45"",
      ""resolved"": ""https://registry.npmjs.org/@types/react/-/react-18.2.45.tgz"",
      ""integrity"": ""sha512-TtAxCNrlrBp8GoeEp1np5eFGx1h8sNR5SosOqSg5H6R"",
      ""dev"": true,
      ""dependencies"": {
        ""@types/prop-types"": ""*"",
        ""@types/scheduler"": ""*"",
        ""csstype"": ""^3.0.2""
      }
    },
    ""node_modules/@types/prop-types"": {
      ""version"": ""15.7.11"",
      ""resolved"": ""https://registry.npmjs.org/@types/prop-types/-/prop-types-15.7.11.tgz"",
      ""integrity"": ""sha512-ga8y9v9uyeiLdpKddhxYQkxNDrKqQStJhQdKjt"",
      ""dev"": true
    },
    ""node_modules/@types/scheduler"": {
      ""version"": ""0.16.8"",
      ""resolved"": ""https://registry.npmjs.org/@types/scheduler/-/scheduler-0.16.8.tgz"",
      ""integrity"": ""sha512-WZLiwShhwLRmeV6zH+GFEF1Hj7kz3h4Vs/8OwgZy"",
      ""dev"": true
    },
    ""node_modules/csstype"": {
      ""version"": ""3.1.3"",
      ""resolved"": ""https://registry.npmjs.org/csstype/-/csstype-3.1.3.tgz"",
      ""integrity"": ""sha512-M1uQkMl8rQK/szD0LNhtWhui0zxo5bKzLYRLezo"",
      ""dev"": true
    },
    ""node_modules/typescript"": {
      ""version"": ""5.3.3"",
      ""resolved"": ""https://registry.npmjs.org/typescript/-/typescript-5.3.3.tgz"",
      ""integrity"": ""sha512-pXWcraxM0uxAS+tGYdHBynBKMgxKhPJVucnvFwbNMzQP"",
      ""dev"": true,
      ""bin"": {
        ""tsc"": ""bin/tsc"",
        ""tsserver"": ""bin/tsserver""
      },
      ""engines"": {
        ""node"": "">=14.17""
      }
    }
  }
}";
        var lockFilePath = Path.Combine(_testDirectory, "package-lock.json");
        await File.WriteAllTextAsync(lockFilePath, npmLockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(10, result.Packages.Count);
        
        // Check production dependencies
        Assert.True(result.Packages.ContainsKey("react@18.2.0"));
        Assert.False(result.Packages["react@18.2.0"].Dev);
        
        Assert.True(result.Packages.ContainsKey("react-dom@18.2.0"));
        Assert.False(result.Packages["react-dom@18.2.0"].Dev);
        
        // Check shared dependencies
        Assert.True(result.Packages.ContainsKey("loose-envify@1.4.0"));
        Assert.False(result.Packages["loose-envify@1.4.0"].Dev);
        
        // Check dev dependencies
        Assert.True(result.Packages.ContainsKey("typescript@5.3.3"));
        Assert.True(result.Packages["typescript@5.3.3"].Dev);
        
        Assert.True(result.Packages.ContainsKey("@types/react@18.2.45"));
        Assert.True(result.Packages["@types/react@18.2.45"].Dev);
    }

    [Fact]
    public async Task ImportAsync_YarnBerryLockFile_HandlesCorrectly()
    {
        // Arrange - Yarn 2+ (Berry) format
        var yarnLockContent = @"# This file is generated by Yarn.

""react@npm:^18.2.0"":
  version: 18.2.0
  resolution: ""react@npm:18.2.0""
  dependencies:
    loose-envify: ^1.1.0
  checksum: 88e38092da8839b830cc3b294c8e65db2a9ab89c1974216d53ad9bb57cf8d5c45e2de887cc15ba337cf4d039735282b0f5234f5e77134a24b8a4cf8ac95a9e3c
  languageName: node
  linkType: hard

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

""loose-envify@npm:^1.1.0, loose-envify@npm:^1.4.0"":
  version: 1.4.0
  resolution: ""loose-envify@npm:1.4.0""
  dependencies:
    js-tokens: ^3.0.0 || ^4.0.0
  bin:
    loose-envify: cli.js
  checksum: 6517e24e52ca985b84f04e42beff1ef1fa9ad7e4e3d262976c783d0e9f6e01f3f3533b3ba06c7b89077edf0e3cc0e7cd3fb3c6f91f6e2b47bb3d58aa9e8e9a13
  languageName: node
  linkType: hard

""js-tokens@npm:^3.0.0 || ^4.0.0"":
  version: 4.0.0
  resolution: ""js-tokens@npm:4.0.0""
  checksum: 8a95213a5a77deb6cbe94d86340e8d9ace2b93bc367790b260101d2f36a5eaf4e4e22f9fa4de63617272dedd37a405b42ec8c7f88ad36322bb0a7df6bf3c4f7d
  languageName: node
  linkType: hard

""scheduler@npm:^0.23.0"":
  version: 0.23.0
  resolution: ""scheduler@npm:0.23.0""
  dependencies:
    loose-envify: ^1.1.0
  checksum: d79192eddaa8bebecab4eaa6d8e027e95a09e2c6c4227e76e7825ea6481ca10f4e7b96cdced8377cd8429f4b81c3c6ca81a382c28263d3410ee7e31a80ad8f89
  languageName: node
  linkType: hard
";
        var lockFilePath = Path.Combine(_testDirectory, "yarn.lock");
        await File.WriteAllTextAsync(lockFilePath, yarnLockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Packages.Count);
        
        // Check that all packages were imported with correct versions
        Assert.True(result.Packages.ContainsKey("react@18.2.0"));
        Assert.True(result.Packages.ContainsKey("react-dom@18.2.0"));
        Assert.True(result.Packages.ContainsKey("loose-envify@1.4.0"));
        Assert.True(result.Packages.ContainsKey("js-tokens@4.0.0"));
        Assert.True(result.Packages.ContainsKey("scheduler@0.23.0"));
        
        // Verify integrity conversion
        var reactPackage = result.Packages["react@18.2.0"];
        Assert.StartsWith("sha512-", reactPackage.Integrity);
    }

    [Fact]
    public async Task ImportAsync_PnpmWithWorkspaceProtocol_HandlesCorrectly()
    {
        // Arrange - PNPM with workspace: protocol
        var pnpmLockContent = @"lockfileVersion: '6.0'

settings:
  autoInstallPeers: true
  excludeLinksFromLockfile: false

importers:

  .:
    dependencies:
      '@myapp/core':
        specifier: workspace:*
        version: link:packages/core
      '@myapp/utils':
        specifier: workspace:^1.0.0
        version: link:packages/utils
      react:
        specifier: ^18.2.0
        version: 18.2.0

  packages/core:
    dependencies:
      '@myapp/utils':
        specifier: workspace:*
        version: link:../utils
      lodash:
        specifier: ^4.17.21
        version: 4.17.21

  packages/utils:
    dependencies:
      dayjs:
        specifier: ^1.11.10
        version: 1.11.10

packages:

  /react@18.2.0:
    resolution: {integrity: sha512-/3IjMdb2L9QbBdWiW5e3P2/npwMBaU9mHCSCUzNn0ZCYbcLTsGbTJrU/g, tarball: https://registry.npmjs.org/react/-/react-18.2.0.tgz}
    engines: {node: '>=0.10.0'}
    dependencies:
      loose-envify: 1.4.0
    dev: false

  /loose-envify@1.4.0:
    resolution: {integrity: sha512-lyuxPGr/Wfhrlem2CL/xFgrtzFKrMwpKbTvTxd4d7qQ, tarball: https://registry.npmjs.org/loose-envify/-/loose-envify-1.4.0.tgz}
    hasBin: true
    dependencies:
      js-tokens: 4.0.0
    dev: false

  /js-tokens@4.0.0:
    resolution: {integrity: sha512-RdJUflcE3cUzKiMqQgsCu06FPu9U, tarball: https://registry.npmjs.org/js-tokens/-/js-tokens-4.0.0.tgz}
    dev: false

  /lodash@4.17.21:
    resolution: {integrity: sha512-v2kDEe57lecTulaDIuNm5aDB, tarball: https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz}
    dev: false

  /dayjs@1.11.10:
    resolution: {integrity: sha512-vjAczensTQkGz7AdUFT5FqA, tarball: https://registry.npmjs.org/dayjs/-/dayjs-1.11.10.tgz}
    dev: false
";
        var lockFilePath = Path.Combine(_testDirectory, "pnpm-lock.yaml");
        await File.WriteAllTextAsync(lockFilePath, pnpmLockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(5, result.Packages.Count);
        
        // Check regular packages
        Assert.True(result.Packages.ContainsKey("react@18.2.0"));
        Assert.True(result.Packages.ContainsKey("lodash@4.17.21"));
        Assert.True(result.Packages.ContainsKey("dayjs@1.11.10"));
        
        // Verify integrity and resolved URLs
        var reactPackage = result.Packages["react@18.2.0"];
        Assert.Equal("sha512-/3IjMdb2L9QbBdWiW5e3P2/npwMBaU9mHCSCUzNn0ZCYbcLTsGbTJrU/g", reactPackage.Integrity);
        Assert.Equal("https://registry.npmjs.org/react/-/react-18.2.0.tgz", reactPackage.Resolved);
    }

    [Fact]
    public async Task ImportAsync_MixedDependencyTypes_CorrectlyCategorizesPackages()
    {
        // Arrange - Lock file with prod, dev, optional, and peer dependencies
        var npmLockContent = @"{
  ""name"": ""mixed-deps"",
  ""version"": ""1.0.0"",
  ""lockfileVersion"": 3,
  ""packages"": {
    ""node_modules/prod-package"": {
      ""version"": ""1.0.0"",
      ""resolved"": ""https://registry.npmjs.org/prod-package/-/prod-package-1.0.0.tgz"",
      ""integrity"": ""sha512-prod"",
      ""dependencies"": {}
    },
    ""node_modules/dev-package"": {
      ""version"": ""2.0.0"",
      ""resolved"": ""https://registry.npmjs.org/dev-package/-/dev-package-2.0.0.tgz"",
      ""integrity"": ""sha512-dev"",
      ""dev"": true,
      ""dependencies"": {}
    },
    ""node_modules/optional-package"": {
      ""version"": ""3.0.0"",
      ""resolved"": ""https://registry.npmjs.org/optional-package/-/optional-package-3.0.0.tgz"",
      ""integrity"": ""sha512-optional"",
      ""optional"": true,
      ""dependencies"": {}
    },
    ""node_modules/peer-dep-package"": {
      ""version"": ""4.0.0"",
      ""resolved"": ""https://registry.npmjs.org/peer-dep-package/-/peer-dep-package-4.0.0.tgz"",
      ""integrity"": ""sha512-peer"",
      ""peerDependencies"": {
        ""prod-package"": ""^1.0.0""
      },
      ""dependencies"": {}
    }
  }
}";
        var lockFilePath = Path.Combine(_testDirectory, "package-lock.json");
        await File.WriteAllTextAsync(lockFilePath, npmLockContent);

        // Act
        var result = await _importer.ImportAsync(lockFilePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Packages.Count);
        
        // Check categorization
        var prodPackage = result.Packages["prod-package@1.0.0"];
        Assert.False(prodPackage.Dev);
        Assert.False(prodPackage.Optional);
        
        var devPackage = result.Packages["dev-package@2.0.0"];
        Assert.True(devPackage.Dev);
        Assert.False(devPackage.Optional);
        
        var optionalPackage = result.Packages["optional-package@3.0.0"];
        Assert.False(optionalPackage.Dev);
        Assert.True(optionalPackage.Optional);
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
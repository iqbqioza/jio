using FluentAssertions;
using Jio.Core.Configuration;

namespace Jio.Core.Tests.Configuration;

public class NpmrcParserTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _npmrcPath;

    public NpmrcParserTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        _npmrcPath = Path.Combine(_testDirectory, ".npmrc");
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_Registry_Setting()
    {
        // Arrange
        var content = "registry=https://custom.registry.com/";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.Registry.Should().Be("https://custom.registry.com/");
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_Scoped_Registry()
    {
        // Arrange
        var content = @"
@mycompany:registry=https://npm.mycompany.com/
@another:registry=https://npm.another.com/
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.ScopedRegistries.Should().HaveCount(2);
        config.ScopedRegistries["@mycompany"].Should().Be("https://npm.mycompany.com/");
        config.ScopedRegistries["@another"].Should().Be("https://npm.another.com/");
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_Auth_Tokens()
    {
        // Arrange
        var content = @"
//registry.npmjs.org/:_authToken=npm_token123
//npm.mycompany.com/:_authToken=company_token456
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.AuthTokens.Should().HaveCount(2);
        config.AuthTokens["registry.npmjs.org"].Should().Be("npm_token123");
        config.AuthTokens["npm.mycompany.com"].Should().Be("company_token456");
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_Proxy_Settings()
    {
        // Arrange
        var content = @"
proxy=http://proxy.company.com:8080
https-proxy=https://secure-proxy.company.com:8443
no-proxy=localhost,127.0.0.1,.company.com
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.Proxy.Should().Be("http://proxy.company.com:8080");
        config.HttpsProxy.Should().Be("https://secure-proxy.company.com:8443");
        config.NoProxy.Should().Be("localhost,127.0.0.1,.company.com");
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_SSL_Settings()
    {
        // Arrange
        var content = @"
strict-ssl=false
ca=/path/to/ca.pem
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.StrictSsl.Should().BeFalse();
        config.CaFile.Should().Be("/path/to/ca.pem");
    }

    [Fact]
    public async Task ParseAsync_Should_Ignore_Comments()
    {
        // Arrange
        var content = @"
# This is a comment
registry=https://custom.registry.com/
; This is also a comment
@mycompany:registry=https://npm.mycompany.com/
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.Registry.Should().Be("https://custom.registry.com/");
        config.ScopedRegistries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ParseAsync_Should_Handle_Quoted_Values()
    {
        // Arrange
        var content = @"
user-agent=""jio client v1.0""
proxy='http://proxy with spaces.com:8080'
";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.UserAgent.Should().Be("jio client v1.0");
        config.Proxy.Should().Be("http://proxy with spaces.com:8080");
    }

    [Fact]
    public async Task ParseAsync_Should_Handle_Empty_File()
    {
        // Arrange
        await File.WriteAllTextAsync(_npmrcPath, "");

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.Registry.Should().BeNull();
        config.ScopedRegistries.Should().BeEmpty();
        config.AuthTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_Should_Handle_NonExistent_File()
    {
        // Act
        var config = await NpmrcParser.ParseAsync(Path.Combine(_testDirectory, "nonexistent.npmrc"));

        // Assert
        config.Should().NotBeNull();
        config.Registry.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_Should_Parse_MaxSockets()
    {
        // Arrange
        var content = "maxsockets=5";
        await File.WriteAllTextAsync(_npmrcPath, content);

        // Act
        var config = await NpmrcParser.ParseAsync(_npmrcPath);

        // Assert
        config.MaxSockets.Should().Be(5);
    }

    [Fact]
    public async Task LoadConfigurationAsync_Should_Merge_Configs()
    {
        // Arrange
        var originalDir = Directory.GetCurrentDirectory();
        var projectNpmrc = Path.Combine(_testDirectory, ".npmrc");
        await File.WriteAllTextAsync(projectNpmrc, @"
registry=https://project.registry.com/
@mycompany:registry=https://project.mycompany.com/
");

        // Create user-level .npmrc
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userNpmrc = Path.Combine(userHome, ".npmrc");
        var userNpmrcBackup = userNpmrc + ".backup";
        
        try
        {
            // Backup existing user .npmrc if it exists
            if (File.Exists(userNpmrc))
            {
                File.Move(userNpmrc, userNpmrcBackup);
            }

            await File.WriteAllTextAsync(userNpmrc, @"
registry=https://user.registry.com/
@another:registry=https://user.another.com/
proxy=http://user.proxy.com:8080
");

            // Change to test directory
            Directory.SetCurrentDirectory(_testDirectory);

            // Act
            var config = await NpmrcParser.LoadConfigurationAsync();

            // Assert
            // Project-level should override user-level
            config.Registry.Should().Be("https://project.registry.com/");
            config.ScopedRegistries["@mycompany"].Should().Be("https://project.mycompany.com/");
            // User-level settings that aren't overridden
            config.ScopedRegistries["@another"].Should().Be("https://user.another.com/");
            config.Proxy.Should().Be("http://user.proxy.com:8080");
        }
        finally
        {
            // Restore current directory
            Directory.SetCurrentDirectory(originalDir);
            
            // Restore user .npmrc
            if (File.Exists(userNpmrc))
            {
                File.Delete(userNpmrc);
            }
            if (File.Exists(userNpmrcBackup))
            {
                File.Move(userNpmrcBackup, userNpmrc);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
    
    [Fact]
    public async Task WriteAsync_CreatesNewFile_WhenFileDoesNotExist()
    {
        var filePath = Path.Combine(_testDirectory, "new.npmrc");
        
        await NpmrcParser.WriteAsync(filePath, "registry", "https://custom.registry.com/");
        
        File.Exists(filePath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("registry=https://custom.registry.com/");
    }
    
    [Fact]
    public async Task WriteAsync_UpdatesExistingKey_WhenKeyExists()
    {
        var filePath = Path.Combine(_testDirectory, "update.npmrc");
        await File.WriteAllTextAsync(filePath, "registry=https://old.registry.com/\nproxy=http://proxy.com:8080");
        
        await NpmrcParser.WriteAsync(filePath, "registry", "https://new.registry.com/");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("registry=https://new.registry.com/");
        content.Should().Contain("proxy=http://proxy.com:8080");
        content.Should().NotContain("https://old.registry.com/");
    }
    
    [Fact]
    public async Task WriteAsync_AddsNewKey_WhenKeyDoesNotExist()
    {
        var filePath = Path.Combine(_testDirectory, "add.npmrc");
        await File.WriteAllTextAsync(filePath, "registry=https://registry.npmjs.org/");
        
        await NpmrcParser.WriteAsync(filePath, "proxy", "http://proxy.com:8080");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("registry=https://registry.npmjs.org/");
        content.Should().Contain("proxy=http://proxy.com:8080");
    }
    
    [Fact]
    public async Task WriteAsync_PreservesComments_WhenUpdatingFile()
    {
        var filePath = Path.Combine(_testDirectory, "comments.npmrc");
        await File.WriteAllTextAsync(filePath, "# This is a comment\nregistry=https://registry.npmjs.org/\n# Another comment");
        
        await NpmrcParser.WriteAsync(filePath, "proxy", "http://proxy.com:8080");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("# This is a comment");
        content.Should().Contain("# Another comment");
        content.Should().Contain("proxy=http://proxy.com:8080");
    }
    
    [Fact]
    public async Task DeleteAsync_RemovesKey_WhenKeyExists()
    {
        var filePath = Path.Combine(_testDirectory, "delete.npmrc");
        await File.WriteAllTextAsync(filePath, "registry=https://registry.npmjs.org/\nproxy=http://proxy.com:8080");
        
        await NpmrcParser.DeleteAsync(filePath, "proxy");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("registry=https://registry.npmjs.org/");
        content.Should().NotContain("proxy");
    }
    
    [Fact]
    public async Task DeleteAsync_DoesNothing_WhenKeyDoesNotExist()
    {
        var filePath = Path.Combine(_testDirectory, "delete-missing.npmrc");
        await File.WriteAllTextAsync(filePath, "registry=https://registry.npmjs.org/");
        
        await NpmrcParser.DeleteAsync(filePath, "proxy");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("registry=https://registry.npmjs.org/");
    }
    
    [Fact]
    public async Task DeleteAsync_DoesNothing_WhenFileDoesNotExist()
    {
        var filePath = Path.Combine(_testDirectory, "nonexistent.npmrc");
        
        await NpmrcParser.DeleteAsync(filePath, "proxy");
        
        File.Exists(filePath).Should().BeFalse();
    }
    
    [Fact]
    public async Task WriteAsync_HandlesSpecialCharacters_InValue()
    {
        var filePath = Path.Combine(_testDirectory, "special.npmrc");
        
        await NpmrcParser.WriteAsync(filePath, "user-agent", "jio/1.0.0 (Linux x64) node/16.0.0");
        
        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("user-agent=jio/1.0.0 (Linux x64) node/16.0.0");
        
        // Verify it can be parsed back correctly
        var config = await NpmrcParser.ParseAsync(filePath);
        config.UserAgent.Should().Be("jio/1.0.0 (Linux x64) node/16.0.0");
    }
}
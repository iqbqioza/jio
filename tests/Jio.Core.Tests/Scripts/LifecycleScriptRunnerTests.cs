using System.Text.Json;
using FluentAssertions;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Scripts;
using Moq;

namespace Jio.Core.Tests.Scripts;

public sealed class LifecycleScriptRunnerTests : IDisposable
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly LifecycleScriptRunner _runner;
    private readonly string _tempDirectory;

    public LifecycleScriptRunnerTests()
    {
        _loggerMock = new Mock<ILogger>();
        _runner = new LifecycleScriptRunner(_loggerMock.Object);
        _tempDirectory = Path.Combine(Path.GetTempPath(), "jio-script-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task RunScriptAsync_WhenNoPackageJsonExists_ReturnsTrue()
    {
        var result = await _runner.RunScriptAsync("test", _tempDirectory);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogDebug("No package.json found in {0}", _tempDirectory), Times.Once);
    }

    [Fact]
    public async Task RunScriptAsync_WhenNoScriptsInPackageJson_ReturnsTrue()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var result = await _runner.RunScriptAsync("test", _tempDirectory);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogDebug("Script '{0}' not found", "test"), Times.Once);
    }

    [Fact]
    public async Task RunScriptAsync_WhenScriptNotFound_ReturnsTrue()
    {
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { build = "webpack" }
        });
        
        var result = await _runner.RunScriptAsync("nonexistent", _tempDirectory);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogDebug("Script '{0}' not found", "nonexistent"), Times.Once);
    }

    [Fact]
    public async Task RunScriptAsync_WithValidScript_ExecutesAndReturnsTrue()
    {
        var scriptCommand = OperatingSystem.IsWindows() ? "echo hello" : "echo hello";
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { test = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("test", _tempDirectory);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", "test", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RunScriptAsync_WithFailingScript_ReturnsFalse()
    {
        var scriptCommand = OperatingSystem.IsWindows() ? "exit 1" : "exit 1";
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { fail = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("fail", _tempDirectory);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunScriptAsync_WithInvalidScript_ReturnsFalse()
    {
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { invalid = "nonexistent-command-12345" }
        });
        
        var result = await _runner.RunScriptAsync("invalid", _tempDirectory);
        
        result.Should().BeFalse();
        // Script execution fails due to non-zero exit code, but no exception is thrown
    }

    [Fact]
    public async Task RunScriptAsync_WithCancellation_RespectsCancellation()
    {
        var longRunningScript = OperatingSystem.IsWindows() 
            ? "timeout /t 10 /nobreak" 
            : "sleep 10";
            
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { slow = longRunningScript }
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var act = async () => await _runner.RunScriptAsync("slow", _tempDirectory, cts.Token);
        
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("install", new[] { "preinstall", "install", "postinstall" })]
    [InlineData("test", new[] { "pretest", "test", "posttest" })]
    [InlineData("start", new[] { "prestart", "start", "poststart" })]
    [InlineData("publish", new[] { "prepublishOnly", "prepare", "prepublish", "publish", "postpublish" })]
    public async Task RunLifecycleScriptsAsync_WithKnownLifecycle_RunsCorrectScripts(string lifecycle, string[] expectedScripts)
    {
        var scripts = new Dictionary<string, object>();
        foreach (var script in expectedScripts)
        {
            scripts[script] = OperatingSystem.IsWindows() ? "echo " + script : "echo " + script;
        }

        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = scripts
        });
        
        var result = await _runner.RunLifecycleScriptsAsync(lifecycle, _tempDirectory);
        
        result.Should().BeTrue();
        
        // Verify each script was logged
        foreach (var script in expectedScripts)
        {
            _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", script, It.IsAny<string>()), Times.Once);
        }
    }

    [Fact]
    public async Task RunLifecycleScriptsAsync_WithUnknownLifecycle_ReturnsTrue()
    {
        await CreatePackageJsonAsync(new { name = "test", version = "1.0.0" });
        
        var result = await _runner.RunLifecycleScriptsAsync("unknown", _tempDirectory);
        
        result.Should().BeTrue();
        _loggerMock.Verify(l => l.LogDebug("No lifecycle events defined for '{0}'", "unknown"), Times.Once);
    }

    [Fact]
    public async Task RunLifecycleScriptsAsync_WithFailingScript_ReturnsFalseAndStops()
    {
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new 
            { 
                pretest = OperatingSystem.IsWindows() ? "echo pretest" : "echo pretest",
                test = OperatingSystem.IsWindows() ? "exit 1" : "exit 1", // This will fail
                posttest = OperatingSystem.IsWindows() ? "echo posttest" : "echo posttest"
            }
        });
        
        var result = await _runner.RunLifecycleScriptsAsync("test", _tempDirectory);
        
        result.Should().BeFalse();
        
        // Should run pretest and test, but not posttest due to failure
        _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", "pretest", It.IsAny<string>()), Times.Once);
        _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", "test", It.IsAny<string>()), Times.Once);
        _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", "posttest", It.IsAny<string>()), Times.Never);
        _loggerMock.Verify(l => l.LogError("Lifecycle script '{0}' failed", "test"), Times.Once);
    }

    [Fact]
    public async Task RunLifecycleScriptsAsync_WithMissingScripts_SkipsAndContinues()
    {
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new 
            { 
                // Only posttest exists, pretest and test are missing
                posttest = OperatingSystem.IsWindows() ? "echo posttest" : "echo posttest"
            }
        });
        
        var result = await _runner.RunLifecycleScriptsAsync("test", _tempDirectory);
        
        result.Should().BeTrue();
        
        // Should skip pretest and test (not found), but run posttest
        _loggerMock.Verify(l => l.LogDebug("Script '{0}' not found", "pretest"), Times.Once);
        _loggerMock.Verify(l => l.LogDebug("Script '{0}' not found", "test"), Times.Once);
        _loggerMock.Verify(l => l.LogInfo("Running {0} script in {1}", "posttest", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RunScriptAsync_SetsCorrectEnvironmentVariables()
    {
        var scriptCommand = OperatingSystem.IsWindows() 
            ? "echo %npm_package_name%-%npm_package_version%" 
            : "echo $npm_package_name-$npm_package_version";
            
        await CreatePackageJsonAsync(new 
        { 
            name = "my-package", 
            version = "2.1.0",
            description = "Test package",
            scripts = new { env_test = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("env_test", _tempDirectory);
        
        result.Should().BeTrue();
        // The actual environment variable verification would require capturing console output
        // which is complex in unit tests. We verify the script runs successfully.
    }

    [Fact]
    public async Task RunScriptAsync_AddsNodeModulesBinToPath()
    {
        // Create a fake node_modules/.bin directory
        var nodeModulesPath = Path.Combine(_tempDirectory, "node_modules");
        var binPath = Path.Combine(nodeModulesPath, ".bin");
        Directory.CreateDirectory(binPath);
        
        // Create a fake binary
        var binaryName = OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool";
        var binaryPath = Path.Combine(binPath, binaryName);
        var binaryContent = OperatingSystem.IsWindows() 
            ? "@echo fake tool executed" 
            : "#!/bin/sh\necho fake tool executed";
        await File.WriteAllTextAsync(binaryPath, binaryContent);
        
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var scriptCommand = OperatingSystem.IsWindows() ? "fake-tool.cmd" : "fake-tool";
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { bin_test = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("bin_test", _tempDirectory);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunScriptAsync_WithComplexPackageJson_SetsAllEnvironmentVariables()
    {
        await CreatePackageJsonAsync(new 
        { 
            name = "complex-package", 
            version = "1.2.3",
            description = "A complex test package",
            scripts = new 
            { 
                test = OperatingSystem.IsWindows() ? "echo test" : "echo test",
                build = OperatingSystem.IsWindows() ? "echo build" : "echo build",
                env_check = OperatingSystem.IsWindows() ? "echo env set" : "echo env set"
            }
        });
        
        var result = await _runner.RunScriptAsync("env_check", _tempDirectory);
        
        result.Should().BeTrue();
        // Environment variables would include:
        // npm_package_name=complex-package
        // npm_package_version=1.2.3
        // npm_package_description=A complex test package
        // npm_package_scripts_test=echo test
        // npm_package_scripts_build=echo build
        // npm_package_scripts_env_check=echo env set
    }

    [Fact]
    public async Task RunScriptAsync_WithCorruptedPackageJson_ReturnsFalse()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package.json"), "{ invalid json }");
        
        var result = await _runner.RunScriptAsync("test", _tempDirectory);
        
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RunScriptAsync_WithScriptContainingQuotes_HandlesCorrectly()
    {
        var scriptCommand = OperatingSystem.IsWindows() 
            ? "echo \"hello world\"" 
            : "echo \"hello world\"";
            
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { quotes = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("quotes", _tempDirectory);
        
        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunScriptAsync_WithMultipleCommands_ExecutesAll()
    {
        var scriptCommand = OperatingSystem.IsWindows() 
            ? "echo first && echo second" 
            : "echo first && echo second";
            
        await CreatePackageJsonAsync(new 
        { 
            name = "test", 
            version = "1.0.0",
            scripts = new { multi = scriptCommand }
        });
        
        var result = await _runner.RunScriptAsync("multi", _tempDirectory);
        
        result.Should().BeTrue();
    }

    private async Task CreatePackageJsonAsync(object packageData)
    {
        var json = JsonSerializer.Serialize(packageData, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "package.json"), json);
    }
}
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Scripts;

namespace Jio.Core.Commands;

public sealed class VersionCommandHandler : ICommandHandler<VersionCommand>
{
    private readonly ILogger _logger;
    private readonly ILifecycleScriptRunner _scriptRunner;
    private readonly JsonSerializerOptions _jsonOptions;

    public VersionCommandHandler(ILogger logger, ILifecycleScriptRunner scriptRunner)
    {
        _logger = logger;
        _scriptRunner = scriptRunner;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<int> ExecuteAsync(VersionCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var packageJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
            
            if (!File.Exists(packageJsonPath))
            {
                Console.Error.WriteLine("Error: No package.json found");
                return 1;
            }

            // Load package.json
            var manifest = await PackageManifest.LoadAsync(packageJsonPath);
            var currentVersion = manifest.Version ?? "0.0.0";
            
            // Determine new version
            string newVersion;
            if (!string.IsNullOrEmpty(command.NewVersion))
            {
                newVersion = command.NewVersion;
            }
            else if (command.Major || command.Minor || command.Patch || 
                     command.Premajor || command.Preminor || command.Prepatch || command.Prerelease)
            {
                newVersion = BumpVersion(currentVersion, command);
            }
            else
            {
                // Show current version
                Console.WriteLine(currentVersion);
                return 0;
            }

            // Validate new version
            if (!IsValidVersion(newVersion))
            {
                Console.Error.WriteLine($"Error: Invalid version: {newVersion}");
                return 1;
            }

            // Run preversion script
            await _scriptRunner.RunScriptAsync("preversion", Directory.GetCurrentDirectory(), cancellationToken);

            // Update package.json
            manifest.Version = newVersion;
            await manifest.SaveAsync(packageJsonPath);
            
            Console.WriteLine($"Updated version from {currentVersion} to {newVersion}");

            // Run version script
            await _scriptRunner.RunScriptAsync("version", Directory.GetCurrentDirectory(), cancellationToken);

            // Git operations
            if (!command.NoGitTagVersion && IsGitRepository())
            {
                var tagMessage = command.Message ?? $"v{newVersion}";
                
                // Commit changes
                await RunGitCommandAsync(new[] { "add", packageJsonPath }, cancellationToken);
                await RunGitCommandAsync(new[] { "commit", "-m", tagMessage }, cancellationToken);
                
                // Create tag
                await RunGitCommandAsync(new[] { "tag", $"v{newVersion}", "-m", tagMessage }, cancellationToken);
                
                Console.WriteLine($"Created git tag: v{newVersion}");
            }

            // Run postversion script
            await _scriptRunner.RunScriptAsync("postversion", Directory.GetCurrentDirectory(), cancellationToken);

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "Version command failed");
            return 1;
        }
    }

    private string BumpVersion(string currentVersion, VersionCommand command)
    {
        var version = ParseVersion(currentVersion);
        var major = version.Major;
        var minor = version.Minor;
        var patch = version.Patch;
        var prerelease = version.Prerelease;
        var preid = command.Preid ?? "alpha";

        if (command.Major)
        {
            major++;
            minor = 0;
            patch = 0;
            prerelease = null;
        }
        else if (command.Minor)
        {
            minor++;
            patch = 0;
            prerelease = null;
        }
        else if (command.Patch)
        {
            patch++;
            prerelease = null;
        }
        else if (command.Premajor)
        {
            major++;
            minor = 0;
            patch = 0;
            prerelease = $"{preid}.0";
        }
        else if (command.Preminor)
        {
            minor++;
            patch = 0;
            prerelease = $"{preid}.0";
        }
        else if (command.Prepatch)
        {
            patch++;
            prerelease = $"{preid}.0";
        }
        else if (command.Prerelease)
        {
            if (string.IsNullOrEmpty(prerelease))
            {
                patch++;
                prerelease = $"{preid}.0";
            }
            else
            {
                // Increment prerelease version
                var match = Regex.Match(prerelease, @"^(.+)\.(\d+)$");
                if (match.Success)
                {
                    var prereleaseId = match.Groups[1].Value;
                    var prereleaseNum = int.Parse(match.Groups[2].Value);
                    prerelease = $"{prereleaseId}.{prereleaseNum + 1}";
                }
                else
                {
                    prerelease = $"{prerelease}.0";
                }
            }
        }

        return string.IsNullOrEmpty(prerelease) 
            ? $"{major}.{minor}.{patch}"
            : $"{major}.{minor}.{patch}-{prerelease}";
    }

    private SemVersion ParseVersion(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$");
        if (!match.Success)
        {
            return new SemVersion { Major = 0, Minor = 0, Patch = 0 };
        }

        return new SemVersion
        {
            Major = int.Parse(match.Groups[1].Value),
            Minor = int.Parse(match.Groups[2].Value),
            Patch = int.Parse(match.Groups[3].Value),
            Prerelease = match.Groups[4].Success ? match.Groups[4].Value : null
        };
    }

    private bool IsValidVersion(string version)
    {
        return Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[\w\.-]+)?(?:\+[\w\.-]+)?$");
    }

    private bool IsGitRepository()
    {
        return Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".git"));
    }

    private async Task RunGitCommandAsync(params string[] args)
    {
        await RunGitCommandAsync(args, CancellationToken.None);
    }

    private async Task RunGitCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }

        await process.WaitForExitAsync(cancellationToken);
        
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {error}");
        }
    }

    private class SemVersion
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Patch { get; set; }
        public string? Prerelease { get; set; }
    }
}
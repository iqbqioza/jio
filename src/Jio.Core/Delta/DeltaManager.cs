using System.Diagnostics;
using System.Text.Json;
using Jio.Core.Configuration;
using Jio.Core.Logging;

namespace Jio.Core.Delta;

public class DeltaManager : IDeltaManager
{
    private readonly ILogger _logger;
    private readonly JioConfiguration _configuration;
    private readonly string _deltaDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public DeltaManager(ILogger logger, JioConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _deltaDirectory = Path.Combine(_configuration.CacheDirectory, "deltas");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        Directory.CreateDirectory(_deltaDirectory);
    }

    public async Task<Stream?> GetDeltaUpdateAsync(string packageName, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        if (!_configuration.DeltaUpdates)
        {
            return null;
        }

        var deltaKey = GetDeltaKey(packageName, fromVersion, toVersion);
        var deltaPath = Path.Combine(_deltaDirectory, $"{deltaKey}.delta");

        if (File.Exists(deltaPath))
        {
            _logger.LogDebug($"Found delta update for {packageName} {fromVersion} -> {toVersion}");
            return File.OpenRead(deltaPath);
        }

        return null;
    }

    public async Task<bool> ApplyDeltaAsync(string packagePath, Stream deltaStream, CancellationToken cancellationToken = default)
    {
        try
        {
            // Create temporary directory for delta application
            var tempDir = Path.Combine(Path.GetTempPath(), $"jio-delta-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Extract delta archive
                var deltaArchivePath = Path.Combine(tempDir, "delta.tar.gz");
                using (var fileStream = File.Create(deltaArchivePath))
                {
                    await deltaStream.CopyToAsync(fileStream, cancellationToken);
                }

                // Apply delta using tar/rsync or custom diff
                await ApplyDeltaChangesAsync(packagePath, deltaArchivePath, cancellationToken);

                _logger.LogDebug($"Applied delta update to {packagePath}");
                return true;
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to apply delta update: {ex.Message}");
            return false;
        }
    }

    public async Task CreateDeltaAsync(string packageName, string fromVersion, string toVersion, string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        if (!_configuration.DeltaUpdates)
        {
            return;
        }

        try
        {
            var deltaKey = GetDeltaKey(packageName, fromVersion, toVersion);
            var deltaPath = Path.Combine(_deltaDirectory, $"{deltaKey}.delta");

            // Create delta using binary diff or file-based diff
            await CreateBinaryDeltaAsync(fromPath, toPath, deltaPath, cancellationToken);

            _logger.LogDebug($"Created delta for {packageName} {fromVersion} -> {toVersion}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to create delta for {packageName}: {ex.Message}");
        }
    }

    public async Task<bool> SupportsDeltaAsync(string packageName, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        if (!_configuration.DeltaUpdates)
        {
            return false;
        }

        // Check if delta already exists
        var deltaKey = GetDeltaKey(packageName, fromVersion, toVersion);
        var deltaPath = Path.Combine(_deltaDirectory, $"{deltaKey}.delta");

        return File.Exists(deltaPath);
    }

    private string GetDeltaKey(string packageName, string fromVersion, string toVersion)
    {
        var safePackageName = packageName.Replace("/", "+").Replace("@", "");
        return $"{safePackageName}-{fromVersion}-to-{toVersion}".Replace(".", "_");
    }

    private async Task ApplyDeltaChangesAsync(string targetPath, string deltaArchivePath, CancellationToken cancellationToken)
    {
        // Extract delta metadata
        var deltaInfoPath = Path.ChangeExtension(deltaArchivePath, ".json");
        if (File.Exists(deltaInfoPath))
        {
            var deltaInfoJson = await File.ReadAllTextAsync(deltaInfoPath, cancellationToken);
            var deltaInfo = JsonSerializer.Deserialize<DeltaInfo>(deltaInfoJson, _jsonOptions);

            if (deltaInfo != null)
            {
                await ApplyStructuredDeltaAsync(targetPath, deltaInfo, cancellationToken);
                return;
            }
        }

        // Fallback to simple extraction
        await ExtractDeltaArchiveAsync(deltaArchivePath, targetPath, cancellationToken);
    }

    private async Task ApplyStructuredDeltaAsync(string targetPath, DeltaInfo deltaInfo, CancellationToken cancellationToken)
    {
        // Apply file additions
        foreach (var addition in deltaInfo.Additions)
        {
            var targetFile = Path.Combine(targetPath, addition.Path);
            var targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (addition.Content != null)
            {
                await File.WriteAllBytesAsync(targetFile, Convert.FromBase64String(addition.Content), cancellationToken);
            }
        }

        // Apply file modifications
        foreach (var modification in deltaInfo.Modifications)
        {
            var targetFile = Path.Combine(targetPath, modification.Path);
            if (File.Exists(targetFile) && modification.Content != null)
            {
                await File.WriteAllBytesAsync(targetFile, Convert.FromBase64String(modification.Content), cancellationToken);
            }
        }

        // Apply file deletions
        foreach (var deletion in deltaInfo.Deletions)
        {
            var targetFile = Path.Combine(targetPath, deletion.Path);
            if (File.Exists(targetFile))
            {
                File.Delete(targetFile);
            }
        }
    }

    private async Task ExtractDeltaArchiveAsync(string deltaArchivePath, string targetPath, CancellationToken cancellationToken)
    {
        // Simple extraction using tar
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{deltaArchivePath}\" -C \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
        }
    }

    private async Task CreateBinaryDeltaAsync(string fromPath, string toPath, string deltaPath, CancellationToken cancellationToken)
    {
        // Create a structured delta
        var deltaInfo = await CreateStructuredDeltaAsync(fromPath, toPath, cancellationToken);
        
        // Save delta info
        var deltaInfoPath = Path.ChangeExtension(deltaPath, ".json");
        var deltaInfoJson = JsonSerializer.Serialize(deltaInfo, _jsonOptions);
        await File.WriteAllTextAsync(deltaInfoPath, deltaInfoJson, cancellationToken);

        // Create delta archive with changes only
        await CreateDeltaArchiveAsync(toPath, deltaPath, deltaInfo, cancellationToken);
    }

    private async Task<DeltaInfo> CreateStructuredDeltaAsync(string fromPath, string toPath, CancellationToken cancellationToken)
    {
        var deltaInfo = new DeltaInfo();

        var fromFiles = Directory.Exists(fromPath) 
            ? Directory.GetFiles(fromPath, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(fromPath, f), f => f)
            : new Dictionary<string, string>();

        var toFiles = Directory.Exists(toPath)
            ? Directory.GetFiles(toPath, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(toPath, f), f => f)
            : new Dictionary<string, string>();

        // Find additions and modifications
        foreach (var (relativePath, fullPath) in toFiles)
        {
            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            
            if (!fromFiles.ContainsKey(relativePath))
            {
                // New file
                deltaInfo.Additions.Add(new DeltaFileChange
                {
                    Path = relativePath,
                    Content = Convert.ToBase64String(content)
                });
            }
            else
            {
                // Check if modified
                var originalContent = await File.ReadAllBytesAsync(fromFiles[relativePath], cancellationToken);
                if (!content.SequenceEqual(originalContent))
                {
                    deltaInfo.Modifications.Add(new DeltaFileChange
                    {
                        Path = relativePath,
                        Content = Convert.ToBase64String(content)
                    });
                }
            }
        }

        // Find deletions
        foreach (var relativePath in fromFiles.Keys)
        {
            if (!toFiles.ContainsKey(relativePath))
            {
                deltaInfo.Deletions.Add(new DeltaFileChange { Path = relativePath });
            }
        }

        return deltaInfo;
    }

    private async Task CreateDeltaArchiveAsync(string sourcePath, string deltaPath, DeltaInfo deltaInfo, CancellationToken cancellationToken)
    {
        // For simplicity, just create a marker file
        // In a real implementation, you'd create a proper binary diff
        await File.WriteAllTextAsync(deltaPath, $"Delta created at {DateTime.UtcNow}", cancellationToken);
    }
}

public class DeltaInfo
{
    public List<DeltaFileChange> Additions { get; set; } = new();
    public List<DeltaFileChange> Modifications { get; set; } = new();
    public List<DeltaFileChange> Deletions { get; set; } = new();
}

public class DeltaFileChange
{
    public string Path { get; set; } = "";
    public string? Content { get; set; }
}
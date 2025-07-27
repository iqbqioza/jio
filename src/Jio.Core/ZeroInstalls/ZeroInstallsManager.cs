using System.IO.Compression;
using System.Text.Json;
using Jio.Core.Configuration;
using Jio.Core.Logging;
using Jio.Core.Models;
using Jio.Core.Resolution;
using Jio.Core.Storage;

namespace Jio.Core.ZeroInstalls;

public class ZeroInstallsManager : IZeroInstallsManager
{
    private readonly ILogger _logger;
    private readonly IPackageStore _packageStore;
    private readonly JioConfiguration _configuration;
    private readonly string _archivePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public ZeroInstallsManager(ILogger logger, IPackageStore packageStore, JioConfiguration configuration)
    {
        _logger = logger;
        _packageStore = packageStore;
        _configuration = configuration;
        _archivePath = Path.Combine(Directory.GetCurrentDirectory(), ".yarn", "cache");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<bool> IsZeroInstallsEnabledAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return _configuration.ZeroInstalls && Directory.Exists(_archivePath);
    }

    public async Task CreateZeroInstallsArchiveAsync(DependencyGraph graph, CancellationToken cancellationToken = default)
    {
        if (!_configuration.ZeroInstalls)
        {
            return;
        }

        _logger.LogDebug("Creating zero-installs archive");
        
        // Create archive directory
        Directory.CreateDirectory(_archivePath);
        
        // Create manifest for archived packages
        var archiveManifest = new Dictionary<string, ArchivedPackageInfo>();
        
        foreach (var package in graph.Packages.Values)
        {
            try
            {
                var packagePath = await _packageStore.GetPackagePathAsync(package.Name, package.Version, cancellationToken);
                if (Directory.Exists(packagePath))
                {
                    var archiveFileName = $"{package.Name.Replace('/', '-')}-{package.Version}.zip";
                    var archiveFilePath = Path.Combine(_archivePath, archiveFileName);
                    
                    // Create zip archive of the package
                    await CreatePackageArchiveAsync(packagePath, archiveFilePath, cancellationToken);
                    
                    var fileInfo = new FileInfo(archiveFilePath);
                    archiveManifest[package.Name] = new ArchivedPackageInfo
                    {
                        Version = package.Version,
                        ArchiveFile = archiveFileName,
                        Size = fileInfo.Length,
                        Integrity = package.Integrity,
                        Dependencies = package.Dependencies?.ToDictionary(d => d.Key, d => d.Value) ?? new Dictionary<string, string>()
                    };
                    
                    _logger.LogDebug($"Archived {package.Name}@{package.Version} to {archiveFileName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to archive {package.Name}@{package.Version}: {ex.Message}");
            }
        }
        
        // Save archive manifest
        var manifestPath = Path.Combine(_archivePath, "archive-manifest.json");
        var manifestJson = JsonSerializer.Serialize(archiveManifest, _jsonOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        
        Console.WriteLine($"Created zero-installs archive with {archiveManifest.Count} packages");
    }

    public async Task ExtractZeroInstallsArchiveAsync(string extractPath, CancellationToken cancellationToken = default)
    {
        if (!await IsZeroInstallsEnabledAsync(cancellationToken))
        {
            return;
        }

        var manifestPath = Path.Combine(_archivePath, "archive-manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("No archive manifest found");
            return;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, ArchivedPackageInfo>>(manifestJson, _jsonOptions);

        if (manifest == null)
        {
            _logger.LogWarning("Failed to parse archive manifest");
            return;
        }

        Directory.CreateDirectory(extractPath);
        
        _logger.LogDebug("Extracting zero-installs archive");

        foreach (var (packageName, info) in manifest)
        {
            try
            {
                var archiveFilePath = Path.Combine(_archivePath, info.ArchiveFile);
                if (File.Exists(archiveFilePath))
                {
                    var packageExtractPath = Path.Combine(extractPath, packageName.Replace('/', Path.DirectorySeparatorChar));
                    await ExtractPackageArchiveAsync(archiveFilePath, packageExtractPath, cancellationToken);
                    
                    _logger.LogDebug($"Extracted {packageName}@{info.Version}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to extract {packageName}: {ex.Message}");
            }
        }

        Console.WriteLine($"Extracted {manifest.Count} packages from zero-installs archive");
    }

    public async Task<string?> GetArchivedPackageAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        if (!await IsZeroInstallsEnabledAsync(cancellationToken))
        {
            return null;
        }

        var manifestPath = Path.Combine(_archivePath, "archive-manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, ArchivedPackageInfo>>(manifestJson, _jsonOptions);

        if (manifest?.TryGetValue(name, out var info) == true && info.Version == version)
        {
            var archiveFilePath = Path.Combine(_archivePath, info.ArchiveFile);
            return File.Exists(archiveFilePath) ? archiveFilePath : null;
        }

        return null;
    }

    private async Task CreatePackageArchiveAsync(string packagePath, string archiveFilePath, CancellationToken cancellationToken)
    {
        using var fileStream = File.Create(archiveFilePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        await AddDirectoryToArchiveAsync(archive, packagePath, "", cancellationToken);
    }

    private async Task AddDirectoryToArchiveAsync(ZipArchive archive, string sourcePath, string entryBasePath, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.GetFiles(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryName = Path.Combine(entryBasePath, Path.GetFileName(file)).Replace('\\', '/');
            var entry = archive.CreateEntry(entryName);
            
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            await fileStream.CopyToAsync(entryStream, cancellationToken);
        }

        foreach (var directory in Directory.GetDirectories(sourcePath))
        {
            var dirName = Path.GetFileName(directory);
            var entryPath = Path.Combine(entryBasePath, dirName);
            await AddDirectoryToArchiveAsync(archive, directory, entryPath, cancellationToken);
        }
    }

    private async Task ExtractPackageArchiveAsync(string archiveFilePath, string extractPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extractPath);
        
        using var fileStream = File.OpenRead(archiveFilePath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.Combine(extractPath, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            if (!entry.FullName.EndsWith("/"))
            {
                using var entryStream = entry.Open();
                using var outputFileStream = File.Create(destinationPath);
                await entryStream.CopyToAsync(outputFileStream, cancellationToken);
            }
        }
    }
}

public class ArchivedPackageInfo
{
    public string Version { get; set; } = "";
    public string ArchiveFile { get; set; } = "";
    public long Size { get; set; }
    public string? Integrity { get; set; }
    public Dictionary<string, string> Dependencies { get; set; } = new();
}
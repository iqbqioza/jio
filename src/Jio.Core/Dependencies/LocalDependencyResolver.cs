using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Dependencies;

public interface ILocalDependencyResolver
{
    Task<string> ResolveFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<string> ResolveLinkAsync(string linkPath, CancellationToken cancellationToken = default);
    bool IsFileDependency(string spec);
    bool IsLinkDependency(string spec);
}

public class LocalDependencyResolver : ILocalDependencyResolver
{
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public LocalDependencyResolver(ILogger logger, string cacheDirectory)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(cacheDirectory, "local");
        Directory.CreateDirectory(_cacheDirectory);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public bool IsFileDependency(string spec)
    {
        return spec.StartsWith("file:") || 
               spec.StartsWith("./") || 
               spec.StartsWith("../") || 
               spec.StartsWith("/") ||
               (spec.Length > 1 && spec[1] == ':' && spec[2] == '\\'); // Windows path
    }
    
    public bool IsLinkDependency(string spec)
    {
        return spec.StartsWith("link:");
    }
    
    public async Task<string> ResolveFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Remove file: prefix if present
            if (filePath.StartsWith("file:"))
            {
                filePath = filePath.Substring(5);
            }
            
            // Resolve relative paths
            var resolvedPath = Path.GetFullPath(filePath);
            
            // Check if it's a directory or a tarball
            if (Directory.Exists(resolvedPath))
            {
                // Directory dependency
                var packageJsonPath = Path.Combine(resolvedPath, "package.json");
                if (!File.Exists(packageJsonPath))
                {
                    throw new InvalidOperationException($"No package.json found in directory: {resolvedPath}");
                }
                
                // Read package.json to get name and version
                var manifest = await LoadPackageManifestAsync(packageJsonPath, cancellationToken);
                var packageName = manifest.Name ?? throw new InvalidOperationException("Package name is required");
                var packageVersion = manifest.Version ?? "0.0.0";
                
                // Create a cached copy
                var cacheKey = $"{packageName.Replace("/", "-")}-{packageVersion}-{GetDirectoryHash(resolvedPath)}";
                var cachedPath = Path.Combine(_cacheDirectory, cacheKey);
                
                if (!Directory.Exists(cachedPath))
                {
                    _logger.LogDebug("Copying local directory dependency: {0}", resolvedPath);
                    await CopyDirectoryAsync(resolvedPath, cachedPath, cancellationToken);
                }
                
                return cachedPath;
            }
            else if (File.Exists(resolvedPath) && (resolvedPath.EndsWith(".tgz") || resolvedPath.EndsWith(".tar.gz")))
            {
                // Tarball dependency
                var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
                var cacheKey = $"{fileName}-{GetFileHash(resolvedPath)}";
                var cachedPath = Path.Combine(_cacheDirectory, cacheKey);
                
                if (!Directory.Exists(cachedPath))
                {
                    _logger.LogDebug("Extracting local tarball dependency: {0}", resolvedPath);
                    await ExtractTarballAsync(resolvedPath, cachedPath, cancellationToken);
                }
                
                return cachedPath;
            }
            else
            {
                throw new InvalidOperationException($"Local dependency not found or invalid: {resolvedPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve file dependency: {0}", filePath);
            throw;
        }
    }
    
    public async Task<string> ResolveLinkAsync(string linkPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Remove link: prefix
            if (linkPath.StartsWith("link:"))
            {
                linkPath = linkPath.Substring(5);
            }
            
            // Resolve relative paths
            var resolvedPath = Path.GetFullPath(linkPath);
            
            if (!Directory.Exists(resolvedPath))
            {
                throw new InvalidOperationException($"Link target directory not found: {resolvedPath}");
            }
            
            var packageJsonPath = Path.Combine(resolvedPath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                throw new InvalidOperationException($"No package.json found in link target: {resolvedPath}");
            }
            
            // For link dependencies, we return the actual path without caching
            // This allows live development with changes reflected immediately
            _logger.LogDebug("Resolved link dependency: {0}", resolvedPath);
            
            return resolvedPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve link dependency: {0}", linkPath);
            throw;
        }
    }
    
    private async Task<PackageManifest> LoadPackageManifestAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse package.json");
    }
    
    private string GetDirectoryHash(string directory)
    {
        // Simple hash based on directory modification time and file count
        var dirInfo = new DirectoryInfo(directory);
        var files = dirInfo.GetFiles("*", SearchOption.AllDirectories);
        var lastModified = files.Any() ? files.Max(f => f.LastWriteTimeUtc) : dirInfo.LastWriteTimeUtc;
        var hash = $"{lastModified.Ticks}-{files.Length}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(hash))
            .Replace("/", "-")
            .Replace("+", "_")
            .Replace("=", "");
    }
    
    private string GetFileHash(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToBase64String(hash)
            .Replace("/", "-")
            .Replace("+", "_")
            .Replace("=", "")
            .Substring(0, 16);
    }
    
    private async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        
        // Copy files
        foreach (var file in Directory.GetFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(target, fileName);
            File.Copy(file, destFile, overwrite: true);
        }
        
        // Copy subdirectories
        foreach (var dir in Directory.GetDirectories(source))
        {
            var dirName = Path.GetFileName(dir);
            
            // Skip node_modules and .git
            if (dirName == "node_modules" || dirName == ".git")
                continue;
                
            var destDir = Path.Combine(target, dirName);
            await CopyDirectoryAsync(dir, destDir, cancellationToken);
        }
    }
    
    private async Task ExtractTarballAsync(string tarballPath, string targetDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        
        using var fileStream = File.OpenRead(tarballPath);
        using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress);
        
        // Simple TAR extraction (should use a proper TAR library in production)
        var tempDir = Path.Combine(Path.GetTempPath(), $"jio-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        
        try
        {
            // Extract to temp directory first
            await ExtractTarStreamAsync(gzipStream, tempDir, cancellationToken);
            
            // Find the package directory (usually ./package/)
            var packageDir = Path.Combine(tempDir, "package");
            if (Directory.Exists(packageDir))
            {
                // Move contents from package/ to target
                foreach (var item in Directory.GetFileSystemEntries(packageDir))
                {
                    var itemName = Path.GetFileName(item);
                    var targetPath = Path.Combine(targetDirectory, itemName);
                    
                    if (Directory.Exists(item))
                    {
                        Directory.Move(item, targetPath);
                    }
                    else
                    {
                        File.Move(item, targetPath);
                    }
                }
            }
            else
            {
                // Move all contents to target
                foreach (var item in Directory.GetFileSystemEntries(tempDir))
                {
                    var itemName = Path.GetFileName(item);
                    var targetPath = Path.Combine(targetDirectory, itemName);
                    
                    if (Directory.Exists(item))
                    {
                        Directory.Move(item, targetPath);
                    }
                    else
                    {
                        File.Move(item, targetPath);
                    }
                }
            }
        }
        finally
        {
            // Cleanup temp directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
    
    private async Task ExtractTarStreamAsync(Stream tarStream, string targetDirectory, CancellationToken cancellationToken)
    {
        // Simplified TAR extraction
        // In production, use a proper TAR library
        var buffer = new byte[512];
        
        while (await tarStream.ReadAsync(buffer, 0, 512, cancellationToken) == 512)
        {
            // Check for end of archive
            if (IsEndOfArchive(buffer))
                break;
                
            // Parse header
            var fileName = GetFileName(buffer);
            if (string.IsNullOrEmpty(fileName))
                continue;
                
            var fileSize = GetFileSize(buffer);
            var fileType = buffer[156];
            
            var targetPath = Path.Combine(targetDirectory, fileName);
            
            if (fileType == '5' || fileName.EndsWith("/")) // Directory
            {
                Directory.CreateDirectory(targetPath);
            }
            else // Regular file
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                using var fileStream = File.Create(targetPath);
                await CopyBytesAsync(tarStream, fileStream, fileSize, cancellationToken);
            }
            
            // Skip to next 512-byte boundary
            var padding = 512 - (fileSize % 512);
            if (padding < 512)
            {
                await tarStream.ReadAsync(new byte[padding], 0, (int)padding, cancellationToken);
            }
        }
    }
    
    private bool IsEndOfArchive(byte[] buffer)
    {
        return buffer.All(b => b == 0);
    }
    
    private string GetFileName(byte[] buffer)
    {
        var nameBytes = new byte[100];
        Array.Copy(buffer, 0, nameBytes, 0, 100);
        var name = System.Text.Encoding.ASCII.GetString(nameBytes);
        var nullIndex = name.IndexOf('\0');
        return nullIndex >= 0 ? name.Substring(0, nullIndex) : name;
    }
    
    private long GetFileSize(byte[] buffer)
    {
        var sizeBytes = new byte[12];
        Array.Copy(buffer, 124, sizeBytes, 0, 12);
        var sizeStr = System.Text.Encoding.ASCII.GetString(sizeBytes).Trim('\0', ' ');
        return string.IsNullOrEmpty(sizeStr) ? 0 : Convert.ToInt64(sizeStr, 8);
    }
    
    private async Task CopyBytesAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var remaining = count;
        
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var bytesRead = await source.ReadAsync(buffer, 0, toRead, cancellationToken);
            if (bytesRead == 0)
                break;
                
            await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            remaining -= bytesRead;
        }
    }
}
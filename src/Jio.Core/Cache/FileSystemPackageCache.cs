using System.Security.Cryptography;
using System.Text.Json;
using Jio.Core.Configuration;

namespace Jio.Core.Cache;

public sealed class FileSystemPackageCache : IPackageCache
{
    private readonly JioConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public FileSystemPackageCache(JioConfiguration configuration)
    {
        _configuration = configuration;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        Directory.CreateDirectory(_configuration.CacheDirectory);
    }
    
    public Task<bool> ExistsAsync(string name, string version, string integrity, CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(name, version, integrity);
        var metadataPath = GetMetadataPath(name, version, integrity);
        return Task.FromResult(File.Exists(cachePath) && File.Exists(metadataPath));
    }
    
    public async Task<Stream?> GetAsync(string name, string version, string integrity, CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(name, version, integrity);
        var metadataPath = GetMetadataPath(name, version, integrity);
        
        if (!File.Exists(cachePath) || !File.Exists(metadataPath))
            return null;
        
        // Update last access time
        var metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
        if (metadata != null)
        {
            metadata.LastAccessedAt = DateTime.UtcNow;
            await WriteMetadataAsync(metadataPath, metadata, cancellationToken);
        }
        
        return new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    
    public async Task PutAsync(string name, string version, string integrity, Stream packageStream, CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath(name, version, integrity);
        var metadataPath = GetMetadataPath(name, version, integrity);
        var tempPath = cachePath + ".tmp";
        
        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        
        try
        {
            // Save package to temp file
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await packageStream.CopyToAsync(fileStream, cancellationToken);
            }
            
            // Move to final location
            File.Move(tempPath, cachePath, true);
            
            // Save metadata
            var fileInfo = new FileInfo(cachePath);
            var metadata = new CacheMetadata
            {
                Name = name,
                Version = version,
                Integrity = integrity,
                CachedAt = DateTime.UtcNow,
                LastAccessedAt = DateTime.UtcNow,
                Size = fileInfo.Length
            };
            
            await WriteMetadataAsync(metadataPath, metadata, cancellationToken);
        }
        catch
        {
            // Clean up on failure
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(cachePath))
                File.Delete(cachePath);
            throw;
        }
    }
    
    public async Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_configuration.CacheDirectory))
            return 0;
        
        var info = new DirectoryInfo(_configuration.CacheDirectory);
        return await Task.Run(() => 
            info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length), 
            cancellationToken);
    }
    
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_configuration.CacheDirectory))
        {
            Directory.Delete(_configuration.CacheDirectory, true);
            Directory.CreateDirectory(_configuration.CacheDirectory);
        }
        return Task.CompletedTask;
    }
    
    public async Task<IReadOnlyList<CachedPackage>> ListAsync(CancellationToken cancellationToken = default)
    {
        var packages = new List<CachedPackage>();
        
        if (!Directory.Exists(_configuration.CacheDirectory))
            return packages;
        
        var metadataFiles = Directory.GetFiles(_configuration.CacheDirectory, "*.metadata.json", SearchOption.AllDirectories);
        
        foreach (var metadataFile in metadataFiles)
        {
            var metadata = await ReadMetadataAsync(metadataFile, cancellationToken);
            if (metadata != null)
            {
                packages.Add(new CachedPackage
                {
                    Name = metadata.Name,
                    Version = metadata.Version,
                    Integrity = metadata.Integrity,
                    CachedAt = metadata.CachedAt,
                    Size = metadata.Size
                });
            }
        }
        
        return packages;
    }
    
    private string GetCachePath(string name, string version, string integrity)
    {
        var hash = ComputeHash(name, version, integrity);
        return Path.Combine(_configuration.CacheDirectory, hash[..2], hash[2..4], $"{hash}.tgz");
    }
    
    private string GetMetadataPath(string name, string version, string integrity)
    {
        var hash = ComputeHash(name, version, integrity);
        return Path.Combine(_configuration.CacheDirectory, hash[..2], hash[2..4], $"{hash}.metadata.json");
    }
    
    private static string ComputeHash(string name, string version, string integrity)
    {
        var input = $"{name}@{version}#{integrity}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    private async Task<CacheMetadata?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<CacheMetadata>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }
    
    private async Task WriteMetadataAsync(string path, CacheMetadata metadata, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(metadata, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }
    
    private sealed class CacheMetadata
    {
        public required string Name { get; set; }
        public required string Version { get; set; }
        public required string Integrity { get; set; }
        public required DateTime CachedAt { get; set; }
        public DateTime LastAccessedAt { get; set; }
        public required long Size { get; set; }
    }
}
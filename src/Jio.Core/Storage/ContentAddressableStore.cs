using System.Security.Cryptography;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Jio.Core.Configuration;
using Jio.Core.Security;

namespace Jio.Core.Storage;

public sealed class ContentAddressableStore : IPackageStore
{
    private readonly JioConfiguration _configuration;
    private readonly HttpClient _httpClient;
    
    public ContentAddressableStore(JioConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        Directory.CreateDirectory(_configuration.StoreDirectory);
    }
    
    public Task<string> GetPackagePathAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var hash = ComputePackageHash(name, version);
        var path = GetStorePath(hash);
        return Task.FromResult(path);
    }
    
    public Task<bool> ExistsAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var path = GetPackagePathAsync(name, version).Result;
        return Task.FromResult(Directory.Exists(path));
    }
    
    public Task AddPackageAsync(string name, string version, Stream packageStream, CancellationToken cancellationToken = default)
    {
        var hash = ComputePackageHash(name, version);
        var storePath = GetStorePath(hash);
        
        if (Directory.Exists(storePath))
            return Task.CompletedTask;
        
        var tempPath = Path.Combine(_configuration.StoreDirectory, $".tmp-{Guid.NewGuid()}");
        
        try
        {
            // Ensure parent directories exist
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            Directory.CreateDirectory(tempPath);
            
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempPath);
            }
            
            Directory.Move(tempPath, storePath);
        }
        catch
        {
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, true);
            throw;
        }
        
        return Task.CompletedTask;
    }
    
    public async Task LinkPackageAsync(string name, string version, string targetPath, CancellationToken cancellationToken = default)
    {
        var sourcePath = await GetPackagePathAsync(name, version, cancellationToken);
        
        if (!Directory.Exists(sourcePath))
            throw new InvalidOperationException($"Package {name}@{version} not found in store");
        
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        
        if (_configuration.UseSymlinks && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            try
            {
                CreateSymbolicLink(sourcePath, targetPath);
            }
            catch
            {
                // Fallback to hard links if symlinks fail
                await CreateHardLinksAsync(sourcePath, targetPath, cancellationToken);
            }
        }
        else if (_configuration.UseHardLinks && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            await CreateHardLinksAsync(sourcePath, targetPath, cancellationToken);
        }
        else
        {
            await CopyDirectoryAsync(sourcePath, targetPath, cancellationToken);
        }
    }
    
    public async Task<long> GetStoreSizeAsync(CancellationToken cancellationToken = default)
    {
        var info = new DirectoryInfo(_configuration.StoreDirectory);
        return await Task.Run(() => info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length), cancellationToken);
    }
    
    public Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Implement cleanup of unused packages
        return Task.CompletedTask;
    }
    
    private string ComputePackageHash(string name, string version)
    {
        var input = $"{name}@{version}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    
    private string GetStorePath(string hash)
    {
        return Path.Combine(_configuration.StoreDirectory, hash[..2], hash[2..4], hash);
    }
    
    private async Task CreateHardLinksAsync(string source, string target, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            foreach (var dirPath in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(source, target));
            }
            
            foreach (var filePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var targetFile = filePath.Replace(source, target);
                CreateHardLink(filePath, targetFile);
            }
        }, cancellationToken);
    }
    
    private async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            foreach (var dirPath in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(source, target));
            }
            
            foreach (var filePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var targetFile = filePath.Replace(source, target);
                File.Copy(filePath, targetFile, true);
            }
        }, cancellationToken);
    }
    
    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
    
    [DllImport("libc", SetLastError = true)]
    private static extern int symlink(string target, string linkpath);
    
    private static void CreateHardLink(string source, string target)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (link(source, target) != 0)
            {
                throw new IOException($"Failed to create hard link from {source} to {target}");
            }
        }
        else
        {
            // Fallback to copy on Windows
            File.Copy(source, target, true);
        }
    }
    
    private static void CreateSymbolicLink(string source, string target)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (symlink(source, target) != 0)
            {
                throw new IOException($"Failed to create symbolic link from {source} to {target}");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // On Windows, use Directory.CreateSymbolicLink (requires admin rights)
            try
            {
                Directory.CreateSymbolicLink(target, source);
            }
            catch
            {
                // Fallback to junction point or copy
                throw new NotSupportedException("Symbolic links not supported on this platform");
            }
        }
        else
        {
            throw new NotSupportedException("Symbolic links not supported on this platform");
        }
    }
    
    public async Task<string> GetIntegrityAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var packagePath = await GetPackagePathAsync(name, version, cancellationToken);
        var integrityFile = Path.Combine(packagePath, ".integrity");
        
        if (File.Exists(integrityFile))
        {
            return await File.ReadAllTextAsync(integrityFile, cancellationToken);
        }
        
        // Calculate integrity if not cached
        var tarPath = Path.Combine(packagePath, "package.tgz");
        if (File.Exists(tarPath))
        {
            using var stream = File.OpenRead(tarPath);
            using var sha512 = System.Security.Cryptography.SHA512.Create();
            var hash = await sha512.ComputeHashAsync(stream, cancellationToken);
            var integrity = $"sha512-{Convert.ToBase64String(hash)}";
            
            await File.WriteAllTextAsync(integrityFile, integrity, cancellationToken);
            return integrity;
        }
        
        throw new InvalidOperationException($"Package {name}@{version} not found in store");
    }
}
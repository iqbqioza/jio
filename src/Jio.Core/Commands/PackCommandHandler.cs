using System.IO.Compression;
using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public sealed class PackCommandHandler : ICommandHandler<PackCommand>
{
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public PackCommandHandler(ILogger logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<int> ExecuteAsync(PackCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = command.Directory ?? Directory.GetCurrentDirectory();
            var packageJsonPath = Path.Combine(directory, "package.json");
            
            if (!File.Exists(packageJsonPath))
            {
                Console.Error.WriteLine("Error: No package.json found");
                return 1;
            }

            // Load package.json
            var manifest = await PackageManifest.LoadAsync(packageJsonPath);
            
            if (string.IsNullOrEmpty(manifest.Name) || string.IsNullOrEmpty(manifest.Version))
            {
                Console.Error.WriteLine("Error: Package name and version are required");
                return 1;
            }

            var tarballName = $"{manifest.Name.Replace("@", "").Replace("/", "-")}-{manifest.Version}.tgz";
            var destination = command.Destination ?? Directory.GetCurrentDirectory();
            
            // Ensure destination directory exists
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(destination);
            }
            
            var tarballPath = Path.Combine(destination, tarballName);

            if (command.DryRun)
            {
                Console.WriteLine($"Would create tarball: {tarballPath}");
                return 0;
            }

            // Create temporary directory for package contents
            var tempDir = Path.Combine(Path.GetTempPath(), $"jio-pack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Copy files based on "files" field or default patterns
                var filesToPack = await GetFilesToPackAsync(directory, manifest, cancellationToken);
                
                Console.WriteLine($"Packing {filesToPack.Count} files...");
                
                // Create package directory
                var packageDir = Path.Combine(tempDir, "package");
                Directory.CreateDirectory(packageDir);
                
                foreach (var file in filesToPack)
                {
                    var relativePath = Path.GetRelativePath(directory, file);
                    var targetPath = Path.Combine(packageDir, relativePath);
                    var targetDir = Path.GetDirectoryName(targetPath)!;
                    
                    Directory.CreateDirectory(targetDir);
                    File.Copy(file, targetPath);
                }

                // Always include package.json
                File.Copy(packageJsonPath, Path.Combine(packageDir, "package.json"), overwrite: true);

                // Create tarball
                await CreateTarballAsync(tempDir, tarballPath, cancellationToken);
                
                var fileInfo = new FileInfo(tarballPath);
                Console.WriteLine($"Created {tarballName} ({FormatBytes(fileInfo.Length)})");
                
                // Calculate and display integrity
                var integrity = await CalculateIntegrityAsync(tarballPath, cancellationToken);
                Console.WriteLine($"Integrity: {integrity}");
                
                return 0;
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            _logger.LogError(ex, "Pack command failed");
            return 1;
        }
    }

    private async Task<List<string>> GetFilesToPackAsync(string directory, PackageManifest manifest, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var patterns = manifest.Files?.ToList() ?? GetDefaultPatterns();
        
        // Always exclude certain files/directories
        var excludePatterns = new[]
        {
            "node_modules",
            ".git",
            ".gitignore",
            ".npmignore",
            "*.tgz",
            ".jio",
            "jio-lock.json",
            "package-lock.json",
            "yarn.lock",
            "pnpm-lock.yaml"
        };

        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith("!"))
            {
                // Exclusion pattern
                continue;
            }

            var matchedFiles = GetMatchingFiles(directory, pattern);
            foreach (var file in matchedFiles)
            {
                var relativePath = Path.GetRelativePath(directory, file);
                if (!ShouldExclude(relativePath, excludePatterns))
                {
                    files.Add(file);
                }
            }
        }

        // Check for .npmignore
        var npmIgnorePath = Path.Combine(directory, ".npmignore");
        if (File.Exists(npmIgnorePath))
        {
            var ignorePatterns = await File.ReadAllLinesAsync(npmIgnorePath, cancellationToken);
            files = files.Where(f => !ShouldExclude(Path.GetRelativePath(directory, f), ignorePatterns)).ToList();
        }

        return files.Distinct().ToList();
    }

    private string[] GetMatchingFiles(string directory, string pattern)
    {
        try
        {
            // Handle glob patterns
            if (pattern.Contains("*"))
            {
                // Simple glob pattern support
                var searchPattern = "*";
                var searchOption = SearchOption.TopDirectoryOnly;
                
                if (pattern.Contains("**"))
                {
                    searchOption = SearchOption.AllDirectories;
                    searchPattern = "*";
                }
                else if (pattern.Contains("*"))
                {
                    var parts = pattern.Split('/');
                    searchPattern = parts.LastOrDefault() ?? "*";
                }
                
                var allFiles = Directory.GetFiles(directory, searchPattern, searchOption);
                return allFiles.Where(f => MatchesPattern(Path.GetRelativePath(directory, f), pattern)).ToArray();
            }
            else
            {
                // Direct file or simple pattern
                return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private bool MatchesPattern(string filePath, string pattern)
    {
        // Simple pattern matching - replace with proper glob library in production
        filePath = filePath.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');
        
        if (pattern.Contains("**"))
        {
            var parts = pattern.Split("**");
            if (parts.Length == 2)
            {
                var prefix = parts[0].TrimEnd('/');
                var suffix = parts[1].TrimStart('/');
                
                if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(suffix))
                    return true;
                    
                if (string.IsNullOrEmpty(prefix))
                    return filePath.EndsWith(suffix) || filePath.Contains("/" + suffix);
                    
                if (string.IsNullOrEmpty(suffix))
                    return filePath.StartsWith(prefix);
                    
                return filePath.StartsWith(prefix) && (filePath.EndsWith(suffix) || filePath.Contains("/" + suffix));
            }
        }
        
        // Simple wildcard matching
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern.TrimEnd('*');
            return Path.GetFileName(filePath).StartsWith(prefix);
        }
        
        if (pattern.StartsWith("*"))
        {
            var suffix = pattern.TrimStart('*');
            return Path.GetFileName(filePath).EndsWith(suffix);
        }
        
        return Path.GetFileName(filePath) == pattern || filePath == pattern;
    }

    private List<string> GetDefaultPatterns()
    {
        return new List<string>
        {
            "*.js",
            "*.mjs",
            "*.cjs",
            "*.json",
            "*.d.ts",
            "*.ts",
            "*.map",
            "lib/**/*",
            "dist/**/*",
            "src/**/*",
            "LICENSE*",
            "README*",
            "CHANGELOG*"
        };
    }

    private bool ShouldExclude(string path, string[] patterns)
    {
        var normalizedPath = path.Replace('\\', '/');
        
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.StartsWith("#"))
                continue;
                
            var normalizedPattern = pattern.Trim().Replace('\\', '/');
            
            // Simple pattern matching (can be enhanced)
            if (normalizedPattern.EndsWith("/"))
            {
                if (normalizedPath.StartsWith(normalizedPattern) || 
                    normalizedPath.Contains($"/{normalizedPattern}"))
                    return true;
            }
            else if (normalizedPattern.Contains("*"))
            {
                // Simple wildcard matching
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(normalizedPattern)
                    .Replace("\\*", ".*") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, regex))
                    return true;
            }
            else
            {
                if (normalizedPath == normalizedPattern || 
                    normalizedPath.EndsWith($"/{normalizedPattern}"))
                    return true;
            }
        }
        
        return false;
    }

    private async Task CreateTarballAsync(string sourceDirectory, string tarballPath, CancellationToken cancellationToken)
    {
        // Create tar.gz file
        using var fileStream = File.Create(tarballPath);
        using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
        using var tarWriter = new TarWriter(gzipStream);

        var packageDir = Path.Combine(sourceDirectory, "package");
        await AddDirectoryToTarAsync(tarWriter, packageDir, "package", cancellationToken);
    }

    private async Task AddDirectoryToTarAsync(TarWriter writer, string directory, string basePath, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var relativePath = Path.GetRelativePath(directory, file);
            var entryName = Path.Combine(basePath, relativePath).Replace('\\', '/');
            
            using var fileStream = File.OpenRead(file);
            var entry = new TarEntry
            {
                Name = entryName,
                Mode = 0644,
                Size = fileStream.Length,
                ModTime = File.GetLastWriteTimeUtc(file)
            };
            
            await writer.WriteEntryAsync(entry, fileStream, cancellationToken);
        }
    }

    private async Task<string> CalculateIntegrityAsync(string filePath, CancellationToken cancellationToken)
    {
        using var fileStream = File.OpenRead(filePath);
        using var sha512 = System.Security.Cryptography.SHA512.Create();
        var hash = await sha512.ComputeHashAsync(fileStream, cancellationToken);
        return $"sha512-{Convert.ToBase64String(hash)}";
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

// Simple TAR writer implementation
public class TarWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[512];

    public TarWriter(Stream stream)
    {
        _stream = stream;
    }

    public async Task WriteEntryAsync(TarEntry entry, Stream dataStream, CancellationToken cancellationToken)
    {
        // Write header
        Array.Clear(_buffer, 0, 512);
        
        // Name (100 bytes)
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(entry.Name);
        Array.Copy(nameBytes, 0, _buffer, 0, Math.Min(nameBytes.Length, 100));
        
        // Mode (8 bytes)
        WriteOctal(entry.Mode, _buffer, 100, 8);
        
        // UID (8 bytes)
        WriteOctal(0, _buffer, 108, 8);
        
        // GID (8 bytes)
        WriteOctal(0, _buffer, 116, 8);
        
        // Size (12 bytes)
        WriteOctal(entry.Size, _buffer, 124, 12);
        
        // Modification time (12 bytes)
        var unixTime = ((DateTimeOffset)entry.ModTime).ToUnixTimeSeconds();
        WriteOctal(unixTime, _buffer, 136, 12);
        
        // Type flag (1 byte) - '0' for regular file
        _buffer[156] = (byte)'0';
        
        // Magic (6 bytes)
        var magic = System.Text.Encoding.ASCII.GetBytes("ustar\0");
        Array.Copy(magic, 0, _buffer, 257, 6);
        
        // Version (2 bytes)
        _buffer[263] = (byte)'0';
        _buffer[264] = (byte)'0';
        
        // Calculate and write checksum
        WriteChecksum(_buffer);
        
        await _stream.WriteAsync(_buffer, 0, 512, cancellationToken);
        
        // Write file data
        await dataStream.CopyToAsync(_stream, cancellationToken);
        
        // Pad to 512-byte boundary
        var padding = 512 - (entry.Size % 512);
        if (padding < 512)
        {
            Array.Clear(_buffer, 0, (int)padding);
            await _stream.WriteAsync(_buffer, 0, (int)padding, cancellationToken);
        }
    }

    private void WriteOctal(long value, byte[] buffer, int offset, int length)
    {
        var octal = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        var bytes = System.Text.Encoding.ASCII.GetBytes(octal);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
        buffer[offset + length - 1] = 0; // Null terminator
    }

    private void WriteChecksum(byte[] buffer)
    {
        // Clear checksum field
        for (int i = 148; i < 156; i++)
            buffer[i] = (byte)' ';
        
        // Calculate checksum
        long checksum = 0;
        for (int i = 0; i < 512; i++)
            checksum += buffer[i];
        
        // Write checksum
        WriteOctal(checksum, buffer, 148, 8);
        buffer[155] = (byte)' '; // Space instead of null
    }

    public void Dispose()
    {
        // Write end-of-archive marker (two 512-byte blocks of zeros)
        Array.Clear(_buffer, 0, 512);
        _stream.Write(_buffer, 0, 512);
        _stream.Write(_buffer, 0, 512);
    }
}

public class TarEntry
{
    public string Name { get; set; } = "";
    public int Mode { get; set; }
    public long Size { get; set; }
    public DateTime ModTime { get; set; }
}
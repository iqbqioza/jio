using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Jio.Core.Configuration;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public class PublishCommand
{
    public string? Tag { get; set; }
    public string? Access { get; set; }
    public bool DryRun { get; set; }
    public string? Otp { get; set; }
    public string? Registry { get; set; }
}

public class PublishCommandHandler : ICommandHandler<PublishCommand>
{
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;
    private readonly string _projectRoot;

    public PublishCommandHandler(HttpClient httpClient, JioConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _projectRoot = Directory.GetCurrentDirectory();
    }

    public async Task<int> ExecuteAsync(PublishCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifestPath = Path.Combine(_projectRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine("No package.json found");
                return 1;
            }

            var manifest = await PackageManifest.LoadAsync(manifestPath);
            
            // Validate package
            var validationErrors = ValidatePackage(manifest);
            if (validationErrors.Any())
            {
                Console.Error.WriteLine("Package validation failed:");
                foreach (var error in validationErrors)
                {
                    Console.Error.WriteLine($"  - {error}");
                }
                return 1;
            }

            // Check if already published
            var registry = command.Registry ?? _configuration.Registry;
            if (!command.DryRun && await IsAlreadyPublished(manifest.Name!, manifest.Version!, registry))
            {
                Console.Error.WriteLine($"Package {manifest.Name}@{manifest.Version} already exists in registry");
                return 1;
            }

            // Create tarball
            var tarballPath = await CreateTarball(manifest);
            var tarballSize = new FileInfo(tarballPath).Length;
            
            Console.WriteLine($"Package: {manifest.Name}@{manifest.Version}");
            Console.WriteLine($"Size: {FormatSize(tarballSize)}");
            Console.WriteLine($"Tag: {command.Tag ?? "latest"}");
            Console.WriteLine($"Access: {command.Access ?? (manifest.Name!.StartsWith("@") ? "restricted" : "public")}");

            if (command.DryRun)
            {
                Console.WriteLine("\nDry run - package not published");
                File.Delete(tarballPath);
                return 0;
            }

            // Get auth token
            var authToken = GetAuthToken(registry);
            if (string.IsNullOrEmpty(authToken))
            {
                Console.Error.WriteLine("No authentication token found. Please run 'npm login' first.");
                return 1;
            }

            // Publish package
            Console.WriteLine("\nPublishing...");
            var success = await PublishPackage(manifest, tarballPath, command, authToken, registry);
            
            // Cleanup
            File.Delete(tarballPath);

            if (success)
            {
                Console.WriteLine($"+ {manifest.Name}@{manifest.Version}");
                return 0;
            }
            else
            {
                Console.Error.WriteLine("Failed to publish package");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error publishing package: {ex.Message}");
            return 1;
        }
    }

    private List<string> ValidatePackage(PackageManifest manifest)
    {
        var errors = new List<string>();

        if (string.IsNullOrEmpty(manifest.Name))
            errors.Add("Package name is required");
        
        if (string.IsNullOrEmpty(manifest.Version))
            errors.Add("Package version is required");
        
        if (manifest.Name?.StartsWith(".") == true || manifest.Name?.StartsWith("_") == true)
            errors.Add("Package name cannot start with . or _");
        
        if (manifest.Private == true)
            errors.Add("Cannot publish private packages");

        // Check for required files
        var readmePath = Path.Combine(_projectRoot, "README.md");
        if (!File.Exists(readmePath))
        {
            Console.WriteLine("Warning: No README.md found");
        }

        return errors;
    }

    private async Task<bool> IsAlreadyPublished(string name, string version, string registry)
    {
        try
        {
            var url = $"{registry}/{Uri.EscapeDataString(name)}/{version}";
            var response = await _httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> CreateTarball(PackageManifest manifest)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        var packageDir = Path.Combine(tempDir, "package");
        Directory.CreateDirectory(packageDir);

        try
        {
            // Copy files respecting .npmignore/.gitignore
            var filesToInclude = GetFilesToInclude();
            
            foreach (var file in filesToInclude)
            {
                var relativePath = Path.GetRelativePath(_projectRoot, file);
                var targetPath = Path.Combine(packageDir, relativePath);
                
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(file, targetPath);
            }

            // Create tarball
            var tarballName = $"{manifest.Name!.Replace("@", "").Replace("/", "-")}-{manifest.Version}.tgz";
            var tarballPath = Path.Combine(tempDir, tarballName);

            using (var fileStream = File.Create(tarballPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal))
            {
                await CreateTarArchive(packageDir, gzipStream);
            }

            return tarballPath;
        }
        finally
        {
            // Cleanup temp package directory
            if (Directory.Exists(packageDir))
                Directory.Delete(packageDir, true);
        }
    }

    private List<string> GetFilesToInclude()
    {
        var files = new List<string>();
        var npmignorePath = Path.Combine(_projectRoot, ".npmignore");
        var gitignorePath = Path.Combine(_projectRoot, ".gitignore");
        
        var ignorePatterns = new List<string> { "node_modules", ".git", "*.log", ".DS_Store" };
        
        if (File.Exists(npmignorePath))
        {
            ignorePatterns.AddRange(File.ReadAllLines(npmignorePath).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#")));
        }
        else if (File.Exists(gitignorePath))
        {
            ignorePatterns.AddRange(File.ReadAllLines(gitignorePath).Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#")));
        }

        // Always include these files
        var alwaysInclude = new[] { "package.json", "README.md", "LICENSE", "LICENCE" };

        foreach (var file in Directory.GetFiles(_projectRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_projectRoot, file);
            
            if (alwaysInclude.Any(f => relativePath.Equals(f, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(file);
                continue;
            }

            var shouldIgnore = ignorePatterns.Any(pattern => MatchesPattern(relativePath, pattern));
            if (!shouldIgnore)
            {
                files.Add(file);
            }
        }

        return files;
    }

    private bool MatchesPattern(string path, string pattern)
    {
        // Simple pattern matching - in reality would use proper glob matching
        if (pattern.Contains("*"))
        {
            var regex = "^" + pattern.Replace(".", "\\.").Replace("*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(path, regex);
        }
        
        return path.Contains(pattern);
    }

    private async Task CreateTarArchive(string sourceDirectory, Stream outputStream)
    {
        // Simplified TAR creation - in production would use a proper TAR library
        var entries = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        
        foreach (var entry in entries)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, entry);
            var fileInfo = new FileInfo(entry);
            
            // Write TAR header (simplified)
            var header = new byte[512];
            var nameBytes = Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/'));
            Array.Copy(nameBytes, header, Math.Min(nameBytes.Length, 100));
            
            // File size in octal
            var sizeOctal = Convert.ToString(fileInfo.Length, 8).PadLeft(11, '0');
            Array.Copy(Encoding.ASCII.GetBytes(sizeOctal), 0, header, 124, 11);
            
            await outputStream.WriteAsync(header);
            
            // Write file content
            using var fileStream = File.OpenRead(entry);
            await fileStream.CopyToAsync(outputStream);
            
            // Pad to 512 byte boundary
            var padding = 512 - (fileInfo.Length % 512);
            if (padding < 512)
            {
                await outputStream.WriteAsync(new byte[padding]);
            }
        }
        
        // Write end-of-archive marker
        await outputStream.WriteAsync(new byte[1024]);
    }

    private string GetAuthToken(string registry)
    {
        // Check environment variable
        var envToken = Environment.GetEnvironmentVariable("NPM_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
            return envToken;

        // Check .npmrc
        var npmrcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
        if (File.Exists(npmrcPath))
        {
            var lines = File.ReadAllLines(npmrcPath);
            var registryHost = new Uri(registry).Host;
            
            foreach (var line in lines)
            {
                if (line.Contains($"//{registryHost}/:_authToken="))
                {
                    return line.Split('=', 2)[1].Trim();
                }
            }
        }

        return "";
    }

    private async Task<bool> PublishPackage(PackageManifest manifest, string tarballPath, PublishCommand command, string authToken, string registry)
    {
        try
        {
            // Read tarball
            var tarballData = await File.ReadAllBytesAsync(tarballPath);
            var shasum = ComputeSha1(tarballData);

            // Create publish metadata
            var publishData = new
            {
                _id = manifest.Name,
                name = manifest.Name,
                description = manifest.Description,
                version = manifest.Version,
                readme = await ReadReadme(),
                _attachments = new Dictionary<string, object>
                {
                    [$"{manifest.Name}-{manifest.Version}.tgz"] = new
                    {
                        content_type = "application/octet-stream",
                        data = Convert.ToBase64String(tarballData),
                        length = tarballData.Length
                    }
                },
                dist = new
                {
                    shasum = shasum,
                    tarball = $"{registry}/{manifest.Name}/-/{manifest.Name}-{manifest.Version}.tgz"
                },
                access = command.Access ?? (manifest.Name!.StartsWith("@") ? "restricted" : "public"),
                tag = command.Tag ?? "latest"
            };

            // Send request
            var request = new HttpRequestMessage(HttpMethod.Put, $"{registry}/{Uri.EscapeDataString(manifest.Name!)}");
            request.Headers.Add("Authorization", $"Bearer {authToken}");
            if (!string.IsNullOrEmpty(command.Otp))
            {
                request.Headers.Add("npm-otp", command.Otp);
            }
            
            request.Content = new StringContent(
                JsonSerializer.Serialize(publishData),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"Registry returned {response.StatusCode}: {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to publish: {ex.Message}");
            return false;
        }
    }

    private async Task<string> ReadReadme()
    {
        var readmePath = Path.Combine(_projectRoot, "README.md");
        if (File.Exists(readmePath))
        {
            return await File.ReadAllTextAsync(readmePath);
        }
        return "";
    }

    private string ComputeSha1(byte[] data)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.#} {sizes[order]}";
    }
}
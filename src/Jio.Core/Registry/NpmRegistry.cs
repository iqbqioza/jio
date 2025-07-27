using System.Text.Json;
using System.Text.Json.Nodes;
using Jio.Core.Configuration;
using Jio.Core.Models;
using Jio.Core.Cache;

namespace Jio.Core.Registry;

public sealed class NpmRegistry : IPackageRegistry
{
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;
    private readonly IPackageCache _cache;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public NpmRegistry(HttpClient httpClient, JioConfiguration configuration, IPackageCache cache)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _cache = cache;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<PackageManifest> GetPackageManifestAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var registry = GetRegistryForPackage(name);
        var url = $"{registry}{name}/{version}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request, registry);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize package manifest for {name}@{version}");
        
        return manifest;
    }
    
    public async Task<IReadOnlyList<string>> GetPackageVersionsAsync(string name, CancellationToken cancellationToken = default)
    {
        var registry = GetRegistryForPackage(name);
        var url = $"{registry}{name}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request, registry);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonNode.Parse(json);
        var versions = doc?["versions"]?.AsObject();
        
        if (versions == null)
            return Array.Empty<string>();
        
        return versions.Select(kvp => kvp.Key).ToList();
    }
    
    public async Task<Stream> DownloadPackageAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        // First get the integrity hash
        var integrity = await GetPackageIntegrityAsync(name, version, cancellationToken);
        
        // Check cache first
        var cachedStream = await _cache.GetAsync(name, version, integrity, cancellationToken);
        if (cachedStream != null)
        {
            return cachedStream;
        }
        
        // Download from registry
        var registry = GetRegistryForPackage(name);
        var manifestUrl = $"{registry}{name}/{version}";
        
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        ConfigureRequest(manifestRequest, registry);
        
        var response = await _httpClient.SendAsync(manifestRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonNode.Parse(json);
        var tarballUrl = doc?["dist"]?["tarball"]?.GetValue<string>();
        
        if (string.IsNullOrEmpty(tarballUrl))
            throw new InvalidOperationException($"No tarball URL found for {name}@{version}");
        
        using var tarballRequest = new HttpRequestMessage(HttpMethod.Get, tarballUrl);
        ConfigureRequest(tarballRequest, new Uri(tarballUrl).Host);
        
        var tarballResponse = await _httpClient.SendAsync(tarballRequest, cancellationToken);
        tarballResponse.EnsureSuccessStatusCode();
        
        // Save to cache
        var packageStream = await tarballResponse.Content.ReadAsStreamAsync(cancellationToken);
        var memoryStream = new MemoryStream();
        await packageStream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Seek(0, SeekOrigin.Begin);
        
        // Save to cache in background
        _ = Task.Run(async () =>
        {
            try
            {
                var cacheStream = new MemoryStream(memoryStream.ToArray());
                await _cache.PutAsync(name, version, integrity, cacheStream, CancellationToken.None);
            }
            catch
            {
                // Ignore cache errors
            }
        });
        
        return memoryStream;
    }
    
    public async Task<string> GetPackageIntegrityAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var registry = GetRegistryForPackage(name);
        var url = $"{registry}{name}/{version}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ConfigureRequest(request, registry);
        
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonNode.Parse(json);
        var integrity = doc?["dist"]?["integrity"]?.GetValue<string>();
        
        return integrity ?? throw new InvalidOperationException($"No integrity hash found for {name}@{version}");
    }
    
    private string GetRegistryForPackage(string packageName)
    {
        // Check for scoped package registry
        if (packageName.StartsWith('@'))
        {
            var scopeEnd = packageName.IndexOf('/');
            if (scopeEnd > 0)
            {
                var scope = packageName[..scopeEnd];
                if (_configuration.ScopedRegistries.TryGetValue(scope, out var scopedRegistry))
                {
                    return EnsureTrailingSlash(scopedRegistry);
                }
            }
        }
        
        return EnsureTrailingSlash(_configuration.Registry);
    }
    
    private void ConfigureRequest(HttpRequestMessage request, string registryOrHost)
    {
        // Add user agent
        if (!string.IsNullOrEmpty(_configuration.UserAgent))
        {
            request.Headers.UserAgent.ParseAdd(_configuration.UserAgent);
        }
        
        // Add auth token if available
        var host = registryOrHost.Contains("://") 
            ? new Uri(registryOrHost).Host 
            : registryOrHost;
            
        if (_configuration.AuthTokens.TryGetValue(host, out var token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }
    
    private static string EnsureTrailingSlash(string url)
    {
        return url.EndsWith('/') ? url : url + "/";
    }
}
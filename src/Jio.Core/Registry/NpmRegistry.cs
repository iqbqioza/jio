using System.Text.Json;
using System.Text.Json.Nodes;
using Jio.Core.Configuration;
using Jio.Core.Models;

namespace Jio.Core.Registry;

public sealed class NpmRegistry : IPackageRegistry
{
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;
    
    public NpmRegistry(HttpClient httpClient, JioConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<PackageManifest> GetPackageManifestAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var url = $"{_configuration.Registry}{name}/{version}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize package manifest for {name}@{version}");
        
        return manifest;
    }
    
    public async Task<IReadOnlyList<string>> GetPackageVersionsAsync(string name, CancellationToken cancellationToken = default)
    {
        var url = $"{_configuration.Registry}{name}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
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
        var manifestUrl = $"{_configuration.Registry}{name}/{version}";
        var response = await _httpClient.GetAsync(manifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonNode.Parse(json);
        var tarballUrl = doc?["dist"]?["tarball"]?.GetValue<string>();
        
        if (string.IsNullOrEmpty(tarballUrl))
            throw new InvalidOperationException($"No tarball URL found for {name}@{version}");
        
        var tarballResponse = await _httpClient.GetAsync(tarballUrl, cancellationToken);
        tarballResponse.EnsureSuccessStatusCode();
        
        return await tarballResponse.Content.ReadAsStreamAsync(cancellationToken);
    }
    
    public async Task<string> GetPackageIntegrityAsync(string name, string version, CancellationToken cancellationToken = default)
    {
        var url = $"{_configuration.Registry}{name}/{version}";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonNode.Parse(json);
        var integrity = doc?["dist"]?["integrity"]?.GetValue<string>();
        
        return integrity ?? throw new InvalidOperationException($"No integrity hash found for {name}@{version}");
    }
}
using System.Text.Json.Serialization;

namespace Jio.Core.Models;

public sealed class PackageManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    
    [JsonPropertyName("version")]
    public required string Version { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = [];
    
    [JsonPropertyName("devDependencies")]
    public Dictionary<string, string> DevDependencies { get; init; } = [];
    
    [JsonPropertyName("scripts")]
    public Dictionary<string, string> Scripts { get; init; } = [];
    
    [JsonPropertyName("main")]
    public string? Main { get; set; }
    
    [JsonPropertyName("license")]
    public string? License { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }
    
    [JsonPropertyName("repository")]
    public object? Repository { get; init; }
    
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }
    
    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }
    
    [JsonPropertyName("engines")]
    public Dictionary<string, string>? Engines { get; init; }
    
    [JsonPropertyName("files")]
    public List<string>? Files { get; init; }
    
    [JsonPropertyName("workspaces")]
    public object? Workspaces { get; init; }
    
    [JsonPropertyName("private")]
    public bool? Private { get; init; }
    
    [JsonPropertyName("bin")]
    public object? Bin { get; init; }
    
    [JsonPropertyName("optionalDependencies")]
    public Dictionary<string, string>? OptionalDependencies { get; set; }
    
    [JsonPropertyName("peerDependencies")]
    public Dictionary<string, string>? PeerDependencies { get; set; }
    
    [JsonPropertyName("overrides")]
    public Dictionary<string, object>? Overrides { get; init; }
    
    [JsonPropertyName("resolutions")]
    public Dictionary<string, string>? Resolutions { get; init; }
    
    [JsonPropertyName("patchedDependencies")]
    public Dictionary<string, string>? PatchedDependencies { get; set; }
    
    public async Task SaveAsync(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        await File.WriteAllTextAsync(path, json);
    }
    
    public static async Task<PackageManifest> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse package.json");
    }
}
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
}
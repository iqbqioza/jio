using System.Text.Json.Serialization;

namespace Jio.Core.Models;

public sealed class LockFile
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";
    
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    
    [JsonPropertyName("lockfileVersion")]
    public int LockfileVersion { get; init; } = 3;
    
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = [];
    
    [JsonPropertyName("devDependencies")]
    public Dictionary<string, string> DevDependencies { get; init; } = [];
    
    [JsonPropertyName("optionalDependencies")]
    public Dictionary<string, string> OptionalDependencies { get; init; } = [];
    
    [JsonPropertyName("packages")]
    public Dictionary<string, LockFilePackage> Packages { get; init; } = [];
}

public sealed class LockFilePackage
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    
    [JsonPropertyName("version")]
    public required string Version { get; init; }
    
    [JsonPropertyName("resolved")]
    public required string Resolved { get; init; }
    
    [JsonPropertyName("integrity")]
    public required string Integrity { get; init; }
    
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; set; }
    
    [JsonPropertyName("optionalDependencies")]
    public Dictionary<string, string>? OptionalDependencies { get; set; }
    
    [JsonPropertyName("peerDependencies")]
    public Dictionary<string, string>? PeerDependencies { get; set; }
    
    [JsonPropertyName("dev")]
    public bool Dev { get; set; }
    
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
    
    [JsonPropertyName("engines")]
    public Dictionary<string, string>? Engines { get; init; }
}
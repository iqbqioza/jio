using System.Text.Json.Serialization;

namespace Jio.Core.Models;

public sealed class LockFile
{
    [JsonPropertyName("lockfileVersion")]
    public int LockfileVersion { get; init; } = 3;
    
    [JsonPropertyName("packages")]
    public Dictionary<string, LockFilePackage> Packages { get; init; } = [];
}

public sealed class LockFilePackage
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }
    
    [JsonPropertyName("resolved")]
    public required string Resolved { get; init; }
    
    [JsonPropertyName("integrity")]
    public required string Integrity { get; init; }
    
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; init; }
    
    [JsonPropertyName("dev")]
    public bool Dev { get; init; }
    
    [JsonPropertyName("optional")]
    public bool Optional { get; init; }
    
    [JsonPropertyName("engines")]
    public Dictionary<string, string>? Engines { get; init; }
}
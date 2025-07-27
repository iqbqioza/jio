using System.Text.Json.Serialization;

namespace Jio.Core.Models;

public class PackageMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("dist-tags")]
    public Dictionary<string, string>? DistTags { get; set; }
    
    [JsonPropertyName("versions")]
    public Dictionary<string, PackageVersion> Versions { get; set; } = new();
    
    [JsonPropertyName("time")]
    public Dictionary<string, DateTime>? Time { get; set; }
}

public class PackageVersion
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("main")]
    public string? Main { get; set; }
    
    [JsonPropertyName("scripts")]
    public Dictionary<string, string>? Scripts { get; set; }
    
    [JsonPropertyName("repository")]
    public object? Repository { get; set; }
    
    [JsonPropertyName("author")]
    public object? Author { get; set; }
    
    [JsonPropertyName("license")]
    public string? License { get; set; }
    
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; set; }
    
    [JsonPropertyName("devDependencies")]
    public Dictionary<string, string>? DevDependencies { get; set; }
    
    [JsonPropertyName("peerDependencies")]
    public Dictionary<string, string>? PeerDependencies { get; set; }
    
    [JsonPropertyName("optionalDependencies")]
    public Dictionary<string, string>? OptionalDependencies { get; set; }
    
    [JsonPropertyName("engines")]
    public Dictionary<string, string>? Engines { get; set; }
    
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }
    
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }
    
    [JsonPropertyName("bugs")]
    public object? Bugs { get; set; }
    
    [JsonPropertyName("maintainers")]
    public List<object>? Maintainers { get; set; }
    
    [JsonPropertyName("dist")]
    public PackageVersionDist? Dist { get; set; }
    
    [JsonPropertyName("time")]
    public DateTime? Time { get; set; }
}

public class PackageVersionDist
{
    [JsonPropertyName("tarball")]
    public string? Tarball { get; set; }
    
    [JsonPropertyName("shasum")]
    public string? Shasum { get; set; }
    
    [JsonPropertyName("integrity")]
    public string? Integrity { get; set; }
    
    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }
    
    [JsonPropertyName("unpackedSize")]
    public long UnpackedSize { get; set; }
}
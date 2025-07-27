namespace Jio.Core.Models;

public sealed class Package
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> Dependencies { get; init; } = [];
    public Dictionary<string, string> DevDependencies { get; init; } = [];
    public string? Main { get; init; }
    public List<string> Files { get; init; } = [];
    public Dictionary<string, string> Scripts { get; init; } = [];
    public string? Repository { get; init; }
    public string? License { get; init; }
    public List<string> Keywords { get; init; } = [];
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public Dictionary<string, object> AdditionalProperties { get; init; } = [];
}
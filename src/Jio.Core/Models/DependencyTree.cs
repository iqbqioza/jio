namespace Jio.Core.Models;

public class DependencyTree
{
    public Dictionary<string, DependencyNode> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class DependencyNode
{
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string RequestedVersion { get; set; } = "";
    public Dictionary<string, string> Dependencies { get; set; } = new();
    public bool IsDev { get; set; }
    public bool IsOptional { get; set; }
    public bool IsPeer { get; set; }
}
using System.Text;
using System.Text.Json;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public sealed class ListCommand
{
    public int Depth { get; init; } = 0;
    public bool Global { get; init; }
    public bool Json { get; init; }
    public bool Parseable { get; init; }
    public string? Pattern { get; init; }
}

public sealed class ListCommandHandler : ICommandHandler<ListCommand>
{
    private readonly JsonSerializerOptions _jsonOptions;
    
    public ListCommandHandler()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(ListCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Global)
        {
            Console.WriteLine("Global packages listing not yet implemented");
            return 1;
        }
        
        var nodeModules = Path.Combine(Directory.GetCurrentDirectory(), "node_modules");
        if (!Directory.Exists(nodeModules))
        {
            Console.WriteLine("No node_modules directory found");
            return 0;
        }
        
        var manifestPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine("No package.json found");
            return 1;
        }
        
        var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, _jsonOptions);
        if (manifest == null)
        {
            Console.WriteLine("Failed to parse package.json");
            return 1;
        }
        
        var tree = await BuildDependencyTreeAsync(nodeModules, manifest, command.Depth, cancellationToken);
        
        if (command.Json)
        {
            var jsonOutput = JsonSerializer.Serialize(tree, _jsonOptions);
            Console.WriteLine(jsonOutput);
        }
        else if (command.Parseable)
        {
            PrintParseable(tree, nodeModules);
        }
        else
        {
            PrintTree(tree, "", true, command.Pattern);
        }
        
        return 0;
    }
    
    private async Task<DependencyNode> BuildDependencyTreeAsync(
        string nodeModules, 
        PackageManifest rootManifest, 
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var root = new DependencyNode
        {
            Name = rootManifest.Name,
            Version = rootManifest.Version,
            Dependencies = new Dictionary<string, DependencyNode>()
        };
        
        if (maxDepth == 0)
        {
            // Only list direct dependencies
            foreach (var (name, versionRange) in rootManifest.Dependencies)
            {
                var depNode = await GetPackageNodeAsync(nodeModules, name, 0, maxDepth, cancellationToken);
                if (depNode != null)
                {
                    root.Dependencies[name] = depNode;
                }
            }
            
            foreach (var (name, versionRange) in rootManifest.DevDependencies)
            {
                var depNode = await GetPackageNodeAsync(nodeModules, name, 0, maxDepth, cancellationToken);
                if (depNode != null)
                {
                    depNode.Dev = true;
                    root.Dependencies[name] = depNode;
                }
            }
        }
        else
        {
            // Recursively build tree
            foreach (var (name, versionRange) in rootManifest.Dependencies)
            {
                var depNode = await GetPackageNodeAsync(nodeModules, name, 1, maxDepth, cancellationToken);
                if (depNode != null)
                {
                    root.Dependencies[name] = depNode;
                }
            }
            
            foreach (var (name, versionRange) in rootManifest.DevDependencies)
            {
                var depNode = await GetPackageNodeAsync(nodeModules, name, 1, maxDepth, cancellationToken);
                if (depNode != null)
                {
                    depNode.Dev = true;
                    root.Dependencies[name] = depNode;
                }
            }
        }
        
        return root;
    }
    
    private async Task<DependencyNode?> GetPackageNodeAsync(
        string nodeModules,
        string packageName,
        int currentDepth,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(nodeModules, packageName);
        var packageJsonPath = Path.Combine(packagePath, "package.json");
        
        if (!File.Exists(packageJsonPath))
            return null;
        
        try
        {
            var json = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);
            if (manifest == null)
                return null;
            
            var node = new DependencyNode
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Dependencies = new Dictionary<string, DependencyNode>()
            };
            
            // Recursively get dependencies if not at max depth
            if (maxDepth < 0 || currentDepth < maxDepth)
            {
                foreach (var (depName, _) in manifest.Dependencies)
                {
                    var depNode = await GetPackageNodeAsync(nodeModules, depName, currentDepth + 1, maxDepth, cancellationToken);
                    if (depNode != null)
                    {
                        node.Dependencies[depName] = depNode;
                    }
                }
            }
            
            return node;
        }
        catch
        {
            return null;
        }
    }
    
    private void PrintTree(DependencyNode node, string prefix, bool isLast, string? pattern)
    {
        if (!string.IsNullOrEmpty(pattern) && !node.Name.Contains(pattern))
            return;
        
        var connector = isLast ? "└── " : "├── ";
        var nodeText = $"{node.Name}@{node.Version}";
        if (node.Dev)
            nodeText += " (dev)";
        
        Console.WriteLine($"{prefix}{connector}{nodeText}");
        
        var childPrefix = prefix + (isLast ? "    " : "│   ");
        var children = node.Dependencies.Values.ToList();
        
        for (int i = 0; i < children.Count; i++)
        {
            PrintTree(children[i], childPrefix, i == children.Count - 1, pattern);
        }
    }
    
    private void PrintParseable(DependencyNode node, string basePath)
    {
        PrintParseableRecursive(node, basePath, "");
    }
    
    private void PrintParseableRecursive(DependencyNode node, string basePath, string parentPath)
    {
        var currentPath = string.IsNullOrEmpty(parentPath) 
            ? node.Name 
            : $"{parentPath}/node_modules/{node.Name}";
        
        var fullPath = Path.Combine(basePath, node.Name.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(fullPath))
        {
            Console.WriteLine($"{fullPath}:{node.Name}@{node.Version}");
        }
        
        foreach (var child in node.Dependencies.Values)
        {
            PrintParseableRecursive(child, basePath, currentPath);
        }
    }
    
    private sealed class DependencyNode
    {
        public required string Name { get; set; }
        public required string Version { get; set; }
        public Dictionary<string, DependencyNode> Dependencies { get; set; } = [];
        public bool Dev { get; set; }
    }
}
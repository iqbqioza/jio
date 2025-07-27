using System.Text.Json;
using Jio.Core.Logging;
using Jio.Core.Models;

namespace Jio.Core.Workspaces;

public class WorkspaceManager
{
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger _logger;

    public WorkspaceManager(string rootPath) : this(rootPath, new ConsoleLogger(LogLevel.Error))
    {
    }

    public WorkspaceManager(string rootPath, ILogger logger)
    {
        _rootPath = rootPath;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<List<WorkspaceInfo>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        var rootManifestPath = Path.Combine(_rootPath, "package.json");
        if (!File.Exists(rootManifestPath))
        {
            return new List<WorkspaceInfo>();
        }

        var json = await File.ReadAllTextAsync(rootManifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(json, _jsonOptions);

        if (manifest?.Workspaces == null)
        {
            return new List<WorkspaceInfo>();
        }

        var workspaces = new List<WorkspaceInfo>();
        var patterns = ParseWorkspacePatterns(manifest.Workspaces);

        foreach (var pattern in patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matchedPaths = await FindMatchingPathsAsync(pattern, cancellationToken);
            foreach (var path in matchedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workspaceManifestPath = Path.Combine(path, "package.json");
                if (File.Exists(workspaceManifestPath))
                {
                    try
                    {
                        var workspaceJson = await File.ReadAllTextAsync(workspaceManifestPath, cancellationToken);
                        var workspaceManifest = JsonSerializer.Deserialize<PackageManifest>(workspaceJson, _jsonOptions);
                        
                        if (workspaceManifest != null && !string.IsNullOrEmpty(workspaceManifest.Name))
                        {
                            workspaces.Add(new WorkspaceInfo
                            {
                                Name = workspaceManifest.Name,
                                Path = path,
                                RelativePath = Path.GetRelativePath(_rootPath, path),
                                Manifest = workspaceManifest
                            });
                        }
                    }
                    catch (JsonException ex)
                    {
                        // Skip corrupted or invalid package.json files
                        _logger.LogDebug("Skipping invalid package.json at {Path}: {Message}", workspaceManifestPath, ex.Message);
                    }
                }
            }
        }

        return workspaces;
    }

    public async Task<Dictionary<string, List<string>>> GetWorkspaceDependencyGraphAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        var workspaces = await GetWorkspacesAsync(cancellationToken);
        var graph = new Dictionary<string, List<string>>();

        foreach (var workspace in workspaces)
        {
            var dependencies = new List<string>();

            // Check dependencies
            if (workspace.Manifest.Dependencies != null)
            {
                foreach (var dep in workspace.Manifest.Dependencies.Keys)
                {
                    if (workspaces.Any(w => w.Name == dep))
                    {
                        dependencies.Add(dep);
                    }
                }
            }

            // Check devDependencies
            if (workspace.Manifest.DevDependencies != null)
            {
                foreach (var dep in workspace.Manifest.DevDependencies.Keys)
                {
                    if (workspaces.Any(w => w.Name == dep))
                    {
                        dependencies.Add(dep);
                    }
                }
            }

            graph[workspace.Name] = dependencies.Distinct().ToList();
        }

        return graph;
    }

    public async Task<List<WorkspaceInfo>> GetTopologicalOrderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        var workspaces = await GetWorkspacesAsync(cancellationToken);
        var graph = await GetWorkspaceDependencyGraphAsync(cancellationToken);
        
        var visited = new HashSet<string>();
        var result = new List<string>();

        void Visit(string name)
        {
            if (visited.Contains(name))
                return;

            visited.Add(name);

            if (graph.TryGetValue(name, out var deps))
            {
                foreach (var dep in deps)
                {
                    Visit(dep);
                }
            }

            result.Add(name);
        }

        foreach (var workspace in workspaces)
        {
            Visit(workspace.Name);
        }

        return result
            .Select(name => workspaces.First(w => w.Name == name))
            .ToList();
    }

    private List<string> ParseWorkspacePatterns(object workspaces)
    {
        var patterns = new List<string>();

        if (workspaces is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                patterns.Add(element.GetString()!);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        patterns.Add(item.GetString()!);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                // Yarn workspaces format with packages field
                if (element.TryGetProperty("packages", out var packages))
                {
                    if (packages.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in packages.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                patterns.Add(item.GetString()!);
                            }
                        }
                    }
                }
            }
        }
        else if (workspaces is string[] array)
        {
            patterns.AddRange(array);
        }
        else if (workspaces is List<string> list)
        {
            patterns.AddRange(list);
        }

        return patterns;
    }

    private Task<List<string>> FindMatchingPathsAsync(string pattern, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        
        // Simple glob pattern matching
        if (pattern.Contains("*"))
        {
            var basePath = Path.GetDirectoryName(pattern)?.Replace("*", "") ?? "";
            var searchPath = Path.Combine(_rootPath, basePath);
            
            if (Directory.Exists(searchPath))
            {
                if (pattern.EndsWith("**"))
                {
                    // Match all subdirectories
                    paths.AddRange(Directory.GetDirectories(searchPath, "*", SearchOption.AllDirectories));
                }
                else if (pattern.EndsWith("*"))
                {
                    // Match immediate subdirectories
                    paths.AddRange(Directory.GetDirectories(searchPath, "*", SearchOption.TopDirectoryOnly));
                }
            }
        }
        else
        {
            // Exact path
            var fullPath = Path.Combine(_rootPath, pattern);
            if (Directory.Exists(fullPath))
            {
                paths.Add(fullPath);
            }
        }

        return Task.FromResult(paths);
    }
}

public class WorkspaceInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public PackageManifest Manifest { get; set; } = new() { Name = "", Version = "0.0.0" };
}
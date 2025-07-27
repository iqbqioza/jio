using System.Text.Json;
using Jio.Core.Models;

namespace Jio.Core.Commands;

public sealed class InitCommand
{
    public string? Name { get; init; }
    public bool Yes { get; init; }
}

public sealed class InitCommandHandler : ICommandHandler<InitCommand>
{
    private readonly JsonSerializerOptions _jsonOptions;
    
    public InitCommandHandler()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
    
    public async Task<int> ExecuteAsync(InitCommand command, CancellationToken cancellationToken = default)
    {
        var packageJsonPath = Path.Combine(Directory.GetCurrentDirectory(), "package.json");
        
        if (File.Exists(packageJsonPath))
        {
            Console.WriteLine("package.json already exists");
            return 1;
        }
        
        var packageName = command.Name ?? Path.GetFileName(Directory.GetCurrentDirectory());
        
        var manifest = new PackageManifest
        {
            Name = packageName,
            Version = "1.0.0",
            Description = "",
            Main = "index.js",
            Scripts = new Dictionary<string, string>
            {
                ["test"] = "echo \"Error: no test specified\" && exit 1"
            },
            Keywords = new List<string>(),
            Author = "",
            License = "ISC",
            Dependencies = new Dictionary<string, string>(),
            DevDependencies = new Dictionary<string, string>()
        };
        
        if (!command.Yes)
        {
            Console.Write($"package name: ({packageName}) ");
            var input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
                manifest.Name = input;
            
            Console.Write("version: (1.0.0) ");
            input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
                manifest.Version = input;
            
            Console.Write("description: ");
            manifest.Description = Console.ReadLine() ?? "";
            
            Console.Write("entry point: (index.js) ");
            input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
                manifest.Main = input;
            
            Console.Write("keywords: ");
            input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
                manifest.Keywords = input.Split(',').Select(k => k.Trim()).ToList();
            
            Console.Write("author: ");
            manifest.Author = Console.ReadLine() ?? "";
            
            Console.Write("license: (ISC) ");
            input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
                manifest.License = input;
        }
        
        var json = JsonSerializer.Serialize(manifest, _jsonOptions);
        await File.WriteAllTextAsync(packageJsonPath, json, cancellationToken);
        
        Console.WriteLine($"Created package.json");
        return 0;
    }
}
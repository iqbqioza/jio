using System.Text.Json;
using Jio.Core.Configuration;

namespace Jio.Core.Commands;

public class SearchCommand
{
    public string Query { get; set; } = "";
    public bool Json { get; set; }
    public bool Long { get; set; }
    public bool ParseableOutput { get; set; }
    public string? Registry { get; set; }
}

public class SearchCommandHandler : ICommandHandler<SearchCommand>
{
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;

    public SearchCommandHandler(HttpClient httpClient, JioConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<int> ExecuteAsync(SearchCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command.Query))
            {
                Console.Error.WriteLine("Search query is required");
                return 1;
            }

            var registry = command.Registry ?? _configuration.Registry;
            var searchUrl = $"{registry}/-/v1/search?text={Uri.EscapeDataString(command.Query)}&size=20";

            var response = await _httpClient.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Search failed: {response.StatusCode}");
                return 1;
            }

            var content = await response.Content.ReadAsStringAsync();
            var searchResult = JsonSerializer.Deserialize<SearchResult>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (searchResult?.Objects == null || searchResult.Objects.Count == 0)
            {
                Console.WriteLine("No packages found");
                return 0;
            }

            // Output results
            if (command.Json)
            {
                OutputJson(searchResult);
            }
            else if (command.ParseableOutput)
            {
                OutputParseable(searchResult);
            }
            else if (command.Long)
            {
                OutputLong(searchResult);
            }
            else
            {
                OutputNormal(searchResult);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error searching packages: {ex.Message}");
            return 1;
        }
    }

    private void OutputNormal(SearchResult result)
    {
        foreach (var obj in result.Objects!)
        {
            var pkg = obj.Package;
            Console.WriteLine($"{pkg.Name,-40} {pkg.Version,-12} {TruncateDescription(pkg.Description, 40)}");
        }
    }

    private void OutputLong(SearchResult result)
    {
        foreach (var obj in result.Objects!)
        {
            var pkg = obj.Package;
            Console.WriteLine($"{pkg.Name}@{pkg.Version}");
            
            if (!string.IsNullOrEmpty(pkg.Description))
                Console.WriteLine($"  {pkg.Description}");
            
            if (pkg.Keywords?.Any() == true)
                Console.WriteLine($"  keywords: {string.Join(", ", pkg.Keywords)}");
            
            if (!string.IsNullOrEmpty(pkg.Author?.Name))
                Console.WriteLine($"  author: {pkg.Author.Name}");
            
            if (pkg.Date != null)
                Console.WriteLine($"  date: {pkg.Date:yyyy-MM-dd}");
            
            if (pkg.Links?.Npm != null)
                Console.WriteLine($"  npm: {pkg.Links.Npm}");
            
            Console.WriteLine();
        }
    }

    private void OutputParseable(SearchResult result)
    {
        foreach (var obj in result.Objects!)
        {
            var pkg = obj.Package;
            var author = pkg.Author?.Name ?? "";
            var date = pkg.Date?.ToString("yyyy-MM-dd") ?? "";
            var keywords = pkg.Keywords != null ? string.Join(",", pkg.Keywords) : "";
            
            Console.WriteLine($"{pkg.Name}\t{pkg.Description}\t{author}\t{date}\t{pkg.Version}\t{keywords}");
        }
    }

    private void OutputJson(SearchResult result)
    {
        var json = JsonSerializer.Serialize(result.Objects!.Select(o => o.Package), new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private string TruncateDescription(string? description, int maxLength)
    {
        if (string.IsNullOrEmpty(description))
            return "";
        
        if (description.Length <= maxLength)
            return description;
        
        return description.Substring(0, maxLength - 3) + "...";
    }
}

public class SearchResult
{
    public List<SearchObject>? Objects { get; set; }
    public int Total { get; set; }
    public string? Time { get; set; }
}

public class SearchObject
{
    public PackageSearchInfo Package { get; set; } = new();
    public SearchScore Score { get; set; } = new();
    public double SearchScore { get; set; }
}

public class PackageSearchInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? Keywords { get; set; }
    public DateTime? Date { get; set; }
    public PackageAuthor? Author { get; set; }
    public PackageLinks? Links { get; set; }
    public PackagePublisher? Publisher { get; set; }
    public List<PackageMaintainer>? Maintainers { get; set; }
}

public class PackageAuthor
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Url { get; set; }
}

public class PackageLinks
{
    public string? Npm { get; set; }
    public string? Homepage { get; set; }
    public string? Repository { get; set; }
    public string? Bugs { get; set; }
}

public class PackagePublisher
{
    public string? Username { get; set; }
    public string? Email { get; set; }
}

public class PackageMaintainer
{
    public string? Username { get; set; }
    public string? Email { get; set; }
}

public class SearchScore
{
    public double Final { get; set; }
    public SearchScoreDetail? Detail { get; set; }
}

public class SearchScoreDetail
{
    public double Quality { get; set; }
    public double Popularity { get; set; }
    public double Maintenance { get; set; }
}
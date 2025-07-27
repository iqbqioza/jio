using System.Net.Http;
using Jio.Core.Configuration;
using Jio.Core.Logging;

namespace Jio.Core.Monitoring;

public interface IHealthCheckService
{
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public class HealthCheckService : IHealthCheckService
{
    private readonly HttpClient _httpClient;
    private readonly JioConfiguration _configuration;
    private readonly ILogger _logger;
    
    public HealthCheckService(HttpClient httpClient, JioConfiguration configuration, ILogger logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }
    
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new HealthCheckResult();
        var checks = new List<Task>();
        
        // Check registry connectivity
        checks.Add(CheckRegistryAsync(result, cancellationToken));
        
        // Check disk space
        checks.Add(Task.Run(() => CheckDiskSpace(result), cancellationToken));
        
        // Check store directory
        checks.Add(Task.Run(() => CheckStoreDirectory(result), cancellationToken));
        
        await Task.WhenAll(checks);
        
        result.IsHealthy = result.Checks.All(c => c.Value.IsHealthy);
        return result;
    }
    
    private async Task CheckRegistryAsync(HealthCheckResult result, CancellationToken cancellationToken)
    {
        var check = new HealthCheck { Name = "registry" };
        
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _configuration.Registry);
            request.Headers.Add("Accept", "application/json");
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            
            check.IsHealthy = response.IsSuccessStatusCode;
            check.Message = check.IsHealthy 
                ? $"Registry accessible at {_configuration.Registry}"
                : $"Registry returned {response.StatusCode}";
            check.ResponseTimeMs = 0; // Could measure actual time
        }
        catch (Exception ex)
        {
            check.IsHealthy = false;
            check.Message = $"Registry check failed: {ex.Message}";
            _logger.LogError(ex, "Registry health check failed");
        }
        
        result.Checks["registry"] = check;
    }
    
    private void CheckDiskSpace(HealthCheckResult result)
    {
        var check = new HealthCheck { Name = "disk_space" };
        
        try
        {
            var storeDir = new DirectoryInfo(_configuration.StoreDirectory);
            var drive = new DriveInfo(storeDir.Root.FullName);
            
            var freeSpaceGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
            var totalSpaceGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0);
            var usedPercentage = ((totalSpaceGb - freeSpaceGb) / totalSpaceGb) * 100;
            
            check.IsHealthy = freeSpaceGb > 1.0; // At least 1GB free
            check.Message = $"Free space: {freeSpaceGb:F2}GB ({usedPercentage:F1}% used)";
            check.Metadata = new Dictionary<string, object>
            {
                ["free_space_gb"] = freeSpaceGb,
                ["total_space_gb"] = totalSpaceGb,
                ["used_percentage"] = usedPercentage
            };
        }
        catch (Exception ex)
        {
            check.IsHealthy = false;
            check.Message = $"Disk space check failed: {ex.Message}";
            _logger.LogError(ex, "Disk space health check failed");
        }
        
        result.Checks["disk_space"] = check;
    }
    
    private void CheckStoreDirectory(HealthCheckResult result)
    {
        var check = new HealthCheck { Name = "store_directory" };
        
        try
        {
            var storeDir = new DirectoryInfo(_configuration.StoreDirectory);
            
            if (!storeDir.Exists)
            {
                storeDir.Create();
            }
            
            // Test write access
            var testFile = Path.Combine(storeDir.FullName, $".health-check-{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            
            check.IsHealthy = true;
            check.Message = $"Store directory accessible at {storeDir.FullName}";
            
            // Count packages
            var packageCount = storeDir.GetDirectories("*", SearchOption.AllDirectories).Length;
            check.Metadata = new Dictionary<string, object>
            {
                ["package_count"] = packageCount,
                ["directory_size_mb"] = GetDirectorySize(storeDir) / (1024.0 * 1024.0)
            };
        }
        catch (Exception ex)
        {
            check.IsHealthy = false;
            check.Message = $"Store directory check failed: {ex.Message}";
            _logger.LogError(ex, "Store directory health check failed");
        }
        
        result.Checks["store_directory"] = check;
    }
    
    private long GetDirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}

public class HealthCheckResult
{
    public bool IsHealthy { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, HealthCheck> Checks { get; set; } = new();
}

public class HealthCheck
{
    public string Name { get; set; } = "";
    public bool IsHealthy { get; set; }
    public string Message { get; set; } = "";
    public long? ResponseTimeMs { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
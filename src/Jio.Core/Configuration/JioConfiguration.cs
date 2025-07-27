namespace Jio.Core.Configuration;

public sealed class JioConfiguration
{
    public string StoreDirectory { get; init; } = GetDefaultStoreDirectory();
    public string CacheDirectory { get; init; } = GetDefaultCacheDirectory();
    public string Registry { get; init; } = "https://registry.npmjs.org/";
    public int MaxConcurrentDownloads { get; init; } = 10;
    public bool UseHardLinks { get; init; } = true;
    public bool VerifyIntegrity { get; init; } = true;
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromMinutes(5);
    
    private static string GetDefaultStoreDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".jio", "store");
    }
    
    private static string GetDefaultCacheDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".jio", "cache");
    }
}
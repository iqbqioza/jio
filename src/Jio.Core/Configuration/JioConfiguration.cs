namespace Jio.Core.Configuration;

public sealed class JioConfiguration
{
    public string StoreDirectory { get; set; } = GetDefaultStoreDirectory();
    public string CacheDirectory { get; set; } = GetDefaultCacheDirectory();
    public string Registry { get; set; } = "https://registry.npmjs.org/";
    public Dictionary<string, string> ScopedRegistries { get; set; } = [];
    public Dictionary<string, string> AuthTokens { get; set; } = [];
    public string? Proxy { get; set; }
    public string? HttpsProxy { get; set; }
    public string? NoProxy { get; set; }
    public string? CaFile { get; set; }
    public bool StrictSsl { get; set; } = true;
    public string? UserAgent { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 10;
    public bool UseHardLinks { get; set; } = true;
    public bool UseSymlinks { get; set; } = false;
    public bool StrictNodeModules { get; set; } = false;
    public bool ZeroInstalls { get; set; } = false;
    public bool DeltaUpdates { get; set; } = false;
    public bool VerifySignatures { get; set; } = false;
    public bool VerifyIntegrity { get; set; } = true;
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public int? MaxRetries { get; set; } = 3;
    
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
    
    public static async Task<JioConfiguration> CreateWithNpmrcAsync(CancellationToken cancellationToken = default)
    {
        var config = new JioConfiguration();
        var npmrc = await NpmrcParser.LoadConfigurationAsync(cancellationToken);
        
        // Apply npmrc settings
        if (!string.IsNullOrEmpty(npmrc.Registry))
            config.Registry = npmrc.Registry;
        
        config.ScopedRegistries = npmrc.ScopedRegistries;
        config.AuthTokens = npmrc.AuthTokens;
        config.Proxy = npmrc.Proxy;
        config.HttpsProxy = npmrc.HttpsProxy;
        config.NoProxy = npmrc.NoProxy;
        config.CaFile = npmrc.CaFile;
        config.StrictSsl = npmrc.StrictSsl;
        config.UserAgent = npmrc.UserAgent ?? $"jio/{typeof(JioConfiguration).Assembly.GetName().Version}";
        
        if (npmrc.MaxSockets.HasValue)
            config.MaxConcurrentDownloads = npmrc.MaxSockets.Value;
        
        return config;
    }
}
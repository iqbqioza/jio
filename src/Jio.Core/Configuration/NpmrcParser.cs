using System.Text.RegularExpressions;

namespace Jio.Core.Configuration;

public sealed class NpmrcConfig
{
    public string? Registry { get; set; }
    public Dictionary<string, string> ScopedRegistries { get; set; } = [];
    public Dictionary<string, string> AuthTokens { get; set; } = [];
    public string? Proxy { get; set; }
    public string? HttpsProxy { get; set; }
    public string? NoProxy { get; set; }
    public string? CaFile { get; set; }
    public bool StrictSsl { get; set; } = true;
    public string? UserAgent { get; set; }
    public int? MaxSockets { get; set; }
    public Dictionary<string, string> AdditionalSettings { get; set; } = [];
}

public static class NpmrcParser
{
    private static readonly Regex CommentRegex = new(@"^\s*[#;]", RegexOptions.Compiled);
    private static readonly Regex KeyValueRegex = new(@"^\s*([^=]+?)\s*=\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ScopedRegistryRegex = new(@"^@(.+?):registry$", RegexOptions.Compiled);
    private static readonly Regex AuthTokenRegex = new(@"^//(.+?)/:_authToken$", RegexOptions.Compiled);
    
    public static async Task<NpmrcConfig> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var config = new NpmrcConfig();
        
        if (!File.Exists(filePath))
            return config;
        
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || CommentRegex.IsMatch(line))
                continue;
            
            var match = KeyValueRegex.Match(line);
            if (!match.Success)
                continue;
            
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();
            
            // Remove quotes if present
            if (value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];
            else if (value.StartsWith('\'') && value.EndsWith('\''))
                value = value[1..^1];
            
            ProcessKeyValue(config, key, value);
        }
        
        return config;
    }
    
    public static async Task<NpmrcConfig> LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var configs = new List<NpmrcConfig>();
        
        // Load from multiple locations in order of precedence (later overrides earlier)
        var locations = GetConfigLocations();
        
        foreach (var location in locations)
        {
            if (File.Exists(location))
            {
                var config = await ParseAsync(location, cancellationToken);
                configs.Add(config);
            }
        }
        
        // Merge configs
        return MergeConfigs(configs);
    }
    
    private static void ProcessKeyValue(NpmrcConfig config, string key, string value)
    {
        // Check for scoped registry
        var scopedMatch = ScopedRegistryRegex.Match(key);
        if (scopedMatch.Success)
        {
            config.ScopedRegistries[$"@{scopedMatch.Groups[1].Value}"] = value;
            return;
        }
        
        // Check for auth token
        var authMatch = AuthTokenRegex.Match(key);
        if (authMatch.Success)
        {
            config.AuthTokens[authMatch.Groups[1].Value] = value;
            return;
        }
        
        // Process standard settings
        switch (key.ToLowerInvariant())
        {
            case "registry":
                config.Registry = value;
                break;
            case "proxy":
            case "http-proxy":
                config.Proxy = value;
                break;
            case "https-proxy":
                config.HttpsProxy = value;
                break;
            case "no-proxy":
            case "noproxy":
                config.NoProxy = value;
                break;
            case "ca":
            case "cafile":
                config.CaFile = value;
                break;
            case "strict-ssl":
                config.StrictSsl = value.ToLowerInvariant() != "false";
                break;
            case "user-agent":
                config.UserAgent = value;
                break;
            case "maxsockets":
                if (int.TryParse(value, out var maxSockets))
                    config.MaxSockets = maxSockets;
                break;
            default:
                config.AdditionalSettings[key] = value;
                break;
        }
    }
    
    private static List<string> GetConfigLocations()
    {
        var locations = new List<string>();
        
        // Global config (loaded first, lowest priority)
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var globalConfig = Path.Combine(appData, "npm", "etc", "npmrc");
            locations.Add(globalConfig);
        }
        else
        {
            locations.Add("/etc/npmrc");
            locations.Add("/usr/local/etc/npmrc");
        }
        
        // User config
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userConfig = Path.Combine(userHome, ".npmrc");
        locations.Add(userConfig);
        
        // Per-project config (loaded last, highest priority)
        var projectConfig = Path.Combine(Directory.GetCurrentDirectory(), ".npmrc");
        locations.Add(projectConfig);
        
        return locations;
    }
    
    private static NpmrcConfig MergeConfigs(List<NpmrcConfig> configs)
    {
        var merged = new NpmrcConfig();
        
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Registry))
                merged.Registry = config.Registry;
            
            foreach (var (scope, registry) in config.ScopedRegistries)
                merged.ScopedRegistries[scope] = registry;
            
            foreach (var (host, token) in config.AuthTokens)
                merged.AuthTokens[host] = token;
            
            if (!string.IsNullOrEmpty(config.Proxy))
                merged.Proxy = config.Proxy;
            
            if (!string.IsNullOrEmpty(config.HttpsProxy))
                merged.HttpsProxy = config.HttpsProxy;
            
            if (!string.IsNullOrEmpty(config.NoProxy))
                merged.NoProxy = config.NoProxy;
            
            if (!string.IsNullOrEmpty(config.CaFile))
                merged.CaFile = config.CaFile;
            
            merged.StrictSsl = config.StrictSsl;
            
            if (!string.IsNullOrEmpty(config.UserAgent))
                merged.UserAgent = config.UserAgent;
            
            if (config.MaxSockets.HasValue)
                merged.MaxSockets = config.MaxSockets;
            
            foreach (var (key, value) in config.AdditionalSettings)
                merged.AdditionalSettings[key] = value;
        }
        
        return merged;
    }
    
    public static async Task WriteAsync(string filePath, string key, string value, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var keyFound = false;
        
        // Read existing file if it exists
        if (File.Exists(filePath))
        {
            lines = (await File.ReadAllLinesAsync(filePath, cancellationToken)).ToList();
            
            // Update existing key
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]) || CommentRegex.IsMatch(lines[i]))
                    continue;
                
                var match = KeyValueRegex.Match(lines[i]);
                if (match.Success && match.Groups[1].Value.Trim() == key)
                {
                    lines[i] = $"{key}={value}";
                    keyFound = true;
                    break;
                }
            }
        }
        
        // Add new key if not found
        if (!keyFound)
        {
            lines.Add($"{key}={value}");
        }
        
        // Write back to file
        await File.WriteAllLinesAsync(filePath, lines, cancellationToken);
    }
    
    public static async Task DeleteAsync(string filePath, string key, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return;
        
        var lines = (await File.ReadAllLinesAsync(filePath, cancellationToken)).ToList();
        var newLines = new List<string>();
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || CommentRegex.IsMatch(line))
            {
                newLines.Add(line);
                continue;
            }
            
            var match = KeyValueRegex.Match(line);
            if (!match.Success || match.Groups[1].Value.Trim() != key)
            {
                newLines.Add(line);
            }
        }
        
        await File.WriteAllLinesAsync(filePath, newLines, cancellationToken);
    }
}
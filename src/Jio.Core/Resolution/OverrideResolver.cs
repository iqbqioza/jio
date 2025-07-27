using System.Text.Json;
using Jio.Core.Models;

namespace Jio.Core.Resolution;

public interface IOverrideResolver
{
    string? GetOverride(string packageName, string requestedVersion, PackageManifest manifest);
    Dictionary<string, string> GetAllOverrides(PackageManifest manifest);
}

public class OverrideResolver : IOverrideResolver
{
    public string? GetOverride(string packageName, string requestedVersion, PackageManifest manifest)
    {
        // Check resolutions first (Yarn style)
        if (manifest.Resolutions != null && manifest.Resolutions.TryGetValue(packageName, out var resolution))
        {
            return resolution;
        }
        
        // Check overrides (npm style)
        if (manifest.Overrides != null)
        {
            return GetOverrideFromNpmStyle(packageName, manifest.Overrides);
        }
        
        return null;
    }
    
    public Dictionary<string, string> GetAllOverrides(PackageManifest manifest)
    {
        var allOverrides = new Dictionary<string, string>();
        
        // Add resolutions (Yarn style)
        if (manifest.Resolutions != null)
        {
            foreach (var (key, value) in manifest.Resolutions)
            {
                allOverrides[key] = value;
            }
        }
        
        // Add overrides (npm style)
        if (manifest.Overrides != null)
        {
            ProcessNpmOverrides(manifest.Overrides, "", allOverrides);
        }
        
        return allOverrides;
    }
    
    private string? GetOverrideFromNpmStyle(string packageName, Dictionary<string, object> overrides)
    {
        // Direct override
        if (overrides.TryGetValue(packageName, out var value))
        {
            if (value is string stringValue)
            {
                return stringValue;
            }
            else if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
            {
                return jsonElement.GetString();
            }
        }
        
        // Check nested overrides (e.g., "package>dependency")
        foreach (var (key, val) in overrides)
        {
            if (key.Contains(">"))
            {
                var parts = key.Split('>');
                var lastPart = parts[parts.Length - 1].Trim();
                
                if (lastPart == packageName)
                {
                    if (val is string stringValue)
                    {
                        return stringValue;
                    }
                    else if (val is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
                    {
                        return jsonElement.GetString();
                    }
                }
            }
        }
        
        return null;
    }
    
    private void ProcessNpmOverrides(Dictionary<string, object> overrides, string prefix, Dictionary<string, string> result)
    {
        foreach (var (key, value) in overrides)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}>{key}";
            
            if (value is string stringValue)
            {
                result[fullKey] = stringValue;
            }
            else if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    result[fullKey] = jsonElement.GetString()!;
                }
                else if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    // Nested overrides
                    var nestedOverrides = new Dictionary<string, object>();
                    foreach (var prop in jsonElement.EnumerateObject())
                    {
                        nestedOverrides[prop.Name] = prop.Value;
                    }
                    ProcessNpmOverrides(nestedOverrides, fullKey, result);
                }
            }
            else if (value is Dictionary<string, object> nestedDict)
            {
                ProcessNpmOverrides(nestedDict, fullKey, result);
            }
        }
    }
}
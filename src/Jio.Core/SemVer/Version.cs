using System.Text.RegularExpressions;

namespace SemVer;

public partial class Version : IComparable<Version>
{
    private static readonly Regex VersionRegex = GetVersionRegex();
    
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Prerelease { get; }
    public string Build { get; }
    
    public Version(string version)
    {
        var match = VersionRegex.Match(version);
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid semantic version: {version}");
        }
        
        Major = int.Parse(match.Groups["major"].Value);
        Minor = int.Parse(match.Groups["minor"].Value);
        Patch = int.Parse(match.Groups["patch"].Value);
        Prerelease = match.Groups["prerelease"].Value;
        Build = match.Groups["build"].Value;
    }
    
    public Version(int major, int minor, int patch, string prerelease = "", string build = "")
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? "";
        Build = build ?? "";
    }
    
    public int CompareTo(Version? other)
    {
        if (other == null) return 1;
        
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        
        // If one has prerelease and other doesn't, version without prerelease is greater
        if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease))
            return 1;
        if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease))
            return -1;
        
        // Compare prerelease versions
        return ComparePrerelease(Prerelease, other.Prerelease);
    }
    
    private static int ComparePrerelease(string pre1, string pre2)
    {
        if (pre1 == pre2) return 0;
        
        var parts1 = pre1.Split('.');
        var parts2 = pre2.Split('.');
        
        var minLength = Math.Min(parts1.Length, parts2.Length);
        
        for (int i = 0; i < minLength; i++)
        {
            var isNum1 = int.TryParse(parts1[i], out var num1);
            var isNum2 = int.TryParse(parts2[i], out var num2);
            
            if (isNum1 && isNum2)
            {
                var numCompare = num1.CompareTo(num2);
                if (numCompare != 0) return numCompare;
            }
            else if (isNum1)
            {
                return -1; // Numeric identifiers have lower precedence
            }
            else if (isNum2)
            {
                return 1;
            }
            else
            {
                var strCompare = string.Compare(parts1[i], parts2[i], StringComparison.Ordinal);
                if (strCompare != 0) return strCompare;
            }
        }
        
        return parts1.Length.CompareTo(parts2.Length);
    }
    
    public override string ToString()
    {
        var version = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease))
            version += $"-{Prerelease}";
        if (!string.IsNullOrEmpty(Build))
            version += $"+{Build}";
        return version;
    }
    
    public override bool Equals(object? obj)
    {
        return obj is Version other && CompareTo(other) == 0;
    }
    
    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor, Patch, Prerelease, Build);
    }
    
    public static bool operator ==(Version? left, Version? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.CompareTo(right) == 0;
    }
    
    public static bool operator !=(Version? left, Version? right)
    {
        return !(left == right);
    }
    
    public static bool operator <(Version? left, Version? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }
    
    public static bool operator >(Version? left, Version? right)
    {
        if (left is null) return false;
        return left.CompareTo(right) > 0;
    }
    
    public static bool operator <=(Version? left, Version? right)
    {
        return !(left > right);
    }
    
    public static bool operator >=(Version? left, Version? right)
    {
        return !(left < right);
    }
    
    [GeneratedRegex(@"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$")]
    private static partial Regex GetVersionRegex();
}
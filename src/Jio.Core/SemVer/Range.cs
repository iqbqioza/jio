using System.Text.RegularExpressions;

namespace SemVer;

public class Range
{
    private readonly List<Comparator> _comparators;
    
    public Range(string range)
    {
        _comparators = ParseRange(range);
    }
    
    public bool IsSatisfied(Version version)
    {
        return _comparators.All(c => c.IsSatisfied(version));
    }
    
    private List<Comparator> ParseRange(string range)
    {
        var comparators = new List<Comparator>();
        
        // Handle common npm range patterns
        range = range.Trim();
        
        // Exact version
        if (!ContainsOperator(range) && IsValidVersion(range))
        {
            comparators.Add(new Comparator("=", new Version(range)));
            return comparators;
        }
        
        // Caret ranges (^)
        if (range.StartsWith("^"))
        {
            var version = new Version(range[1..]);
            comparators.Add(new Comparator(">=", version));
            
            if (version.Major > 0)
            {
                comparators.Add(new Comparator("<", new Version(version.Major + 1, 0, 0)));
            }
            else if (version.Minor > 0)
            {
                comparators.Add(new Comparator("<", new Version(0, version.Minor + 1, 0)));
            }
            else
            {
                comparators.Add(new Comparator("<", new Version(0, 0, version.Patch + 1)));
            }
            return comparators;
        }
        
        // Tilde ranges (~)
        if (range.StartsWith("~"))
        {
            var version = new Version(range[1..]);
            comparators.Add(new Comparator(">=", version));
            comparators.Add(new Comparator("<", new Version(version.Major, version.Minor + 1, 0)));
            return comparators;
        }
        
        // Wildcard ranges (*, x, X)
        if (range == "*" || range == "x" || range == "X" || string.IsNullOrEmpty(range))
        {
            return comparators; // Matches any version
        }
        
        // Operator ranges (>, >=, <, <=, =)
        var operatorMatch = Regex.Match(range, @"^(>=?|<=?|=)\s*(.+)$");
        if (operatorMatch.Success)
        {
            var op = operatorMatch.Groups[1].Value;
            var ver = operatorMatch.Groups[2].Value;
            comparators.Add(new Comparator(op, new Version(ver)));
            return comparators;
        }
        
        // Hyphen ranges (1.2.3 - 2.3.4)
        var hyphenMatch = Regex.Match(range, @"^(.+?)\s*-\s*(.+?)$");
        if (hyphenMatch.Success)
        {
            comparators.Add(new Comparator(">=", new Version(hyphenMatch.Groups[1].Value)));
            comparators.Add(new Comparator("<=", new Version(hyphenMatch.Groups[2].Value)));
            return comparators;
        }
        
        // Space-separated ranges (>1.2.3 <2.0.0)
        var parts = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var subRange = new Range(part);
            comparators.AddRange(subRange._comparators);
        }
        
        return comparators;
    }
    
    private bool ContainsOperator(string range)
    {
        return range.Contains('>') || range.Contains('<') || range.Contains('=') || 
               range.Contains('^') || range.Contains('~') || range.Contains('-');
    }
    
    private bool IsValidVersion(string version)
    {
        try
        {
            _ = new Version(version);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private class Comparator
    {
        private readonly string _operator;
        private readonly Version _version;
        
        public Comparator(string op, Version version)
        {
            _operator = op;
            _version = version;
        }
        
        public bool IsSatisfied(Version version)
        {
            return _operator switch
            {
                "=" => version == _version,
                ">" => version > _version,
                ">=" => version >= _version,
                "<" => version < _version,
                "<=" => version <= _version,
                _ => false
            };
        }
    }
}
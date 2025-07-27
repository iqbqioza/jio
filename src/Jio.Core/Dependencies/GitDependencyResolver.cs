using System.Diagnostics;
using System.Text.RegularExpressions;
using Jio.Core.Logging;

namespace Jio.Core.Dependencies;


public class GitDependencyResolver : IGitDependencyResolver
{
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;
    
    // Git URL patterns
    private static readonly Regex GitUrlPattern = new(@"^(?:git\+)?(https?://|git://|git@|github:|gitlab:|bitbucket:)", RegexOptions.IgnoreCase);
    private static readonly Regex GitHubShorthandPattern = new(@"^([^/]+)/([^#]+)(?:#(.+))?$");
    
    public GitDependencyResolver(ILogger logger, string cacheDirectory)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(cacheDirectory, "git");
        Directory.CreateDirectory(_cacheDirectory);
    }
    
    public bool IsGitDependency(string spec)
    {
        // Exclude file: protocol as it's not a Git dependency
        if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        return GitUrlPattern.IsMatch(spec) || GitHubShorthandPattern.IsMatch(spec);
    }

    public Task<bool> IsGitDependencyAsync(string dependency, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(IsGitDependency(dependency));
    }

    public async Task<string> ResolveAsync(string gitUrl, string reference, CancellationToken cancellationToken = default)
    {
        var fullGitUrl = gitUrl + (string.IsNullOrEmpty(reference) ? "" : "#" + reference);
        return await ResolveAsync(fullGitUrl, cancellationToken);
    }
    
    public async Task<string> ResolveAsync(string gitUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse git URL
            var (repoUrl, commitish) = ParseGitUrl(gitUrl);
            
            // Generate cache key
            var cacheKey = GenerateCacheKey(repoUrl, commitish);
            var repoPath = Path.Combine(_cacheDirectory, cacheKey);
            
            // Clone or update repository
            if (Directory.Exists(repoPath))
            {
                _logger.LogDebug("Updating git repository: {0}", repoUrl);
                await UpdateRepositoryAsync(repoPath, commitish, cancellationToken);
            }
            else
            {
                _logger.LogDebug("Cloning git repository: {0}", repoUrl);
                await CloneRepositoryAsync(repoUrl, repoPath, commitish, cancellationToken);
            }
            
            // Verify package.json exists
            var packageJsonPath = Path.Combine(repoPath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                throw new InvalidOperationException($"No package.json found in git repository: {repoUrl}");
            }
            
            return repoPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve git dependency: {0}", gitUrl);
            throw;
        }
    }
    
    private (string repoUrl, string commitish) ParseGitUrl(string gitUrl)
    {
        // Handle GitHub shorthand (user/repo#ref)
        var shorthandMatch = GitHubShorthandPattern.Match(gitUrl);
        if (shorthandMatch.Success)
        {
            var user = shorthandMatch.Groups[1].Value;
            var repo = shorthandMatch.Groups[2].Value;
            var commitish = shorthandMatch.Groups[3].Success ? shorthandMatch.Groups[3].Value : "HEAD";
            return ($"https://github.com/{user}/{repo}.git", commitish);
        }
        
        // Handle full git URLs
        var urlParts = gitUrl.Split('#', 2);
        var repoUrl = urlParts[0];
        var refSpec = urlParts.Length > 1 ? urlParts[1] : "HEAD";
        
        // Remove git+ prefix if present
        if (repoUrl.StartsWith("git+"))
        {
            repoUrl = repoUrl.Substring(4);
        }
        
        // Convert git@ SSH URLs to HTTPS
        if (repoUrl.StartsWith("git@github.com:"))
        {
            repoUrl = repoUrl.Replace("git@github.com:", "https://github.com/");
        }
        else if (repoUrl.StartsWith("git@gitlab.com:"))
        {
            repoUrl = repoUrl.Replace("git@gitlab.com:", "https://gitlab.com/");
        }
        else if (repoUrl.StartsWith("git@bitbucket.org:"))
        {
            repoUrl = repoUrl.Replace("git@bitbucket.org:", "https://bitbucket.org/");
        }
        
        // Ensure .git extension
        if (!repoUrl.EndsWith(".git"))
        {
            repoUrl += ".git";
        }
        
        return (repoUrl, refSpec);
    }
    
    private string GenerateCacheKey(string repoUrl, string commitish)
    {
        var normalizedUrl = repoUrl.ToLowerInvariant()
            .Replace("https://", "")
            .Replace("http://", "")
            .Replace("git://", "")
            .Replace(".git", "")
            .Replace("/", "-");
            
        return $"{normalizedUrl}-{commitish}".Replace(":", "-");
    }
    
    private async Task CloneRepositoryAsync(string repoUrl, string targetPath, string commitish, CancellationToken cancellationToken)
    {
        // Clone repository
        await RunGitCommandAsync(null, new[] { "clone", "--depth", "1", repoUrl, targetPath }, cancellationToken);
        
        if (commitish != "HEAD" && commitish != "master" && commitish != "main")
        {
            // Fetch specific ref
            await RunGitCommandAsync(targetPath, new[] { "fetch", "origin", commitish }, cancellationToken);
            await RunGitCommandAsync(targetPath, new[] { "checkout", commitish }, cancellationToken);
        }
    }
    
    private async Task UpdateRepositoryAsync(string repoPath, string commitish, CancellationToken cancellationToken)
    {
        // Fetch latest changes
        await RunGitCommandAsync(repoPath, new[] { "fetch", "origin" }, cancellationToken);
        
        if (commitish != "HEAD")
        {
            await RunGitCommandAsync(repoPath, new[] { "checkout", commitish }, cancellationToken);
        }
        else
        {
            await RunGitCommandAsync(repoPath, new[] { "pull", "origin", "HEAD" }, cancellationToken);
        }
    }
    
    private async Task RunGitCommandAsync(string? workingDirectory, params string[] args)
    {
        await RunGitCommandAsync(workingDirectory, args, CancellationToken.None);
    }
    
    private async Task RunGitCommandAsync(string? workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }
        
        _logger.LogDebug("Running git command: git {0}", string.Join(" ", args));
        
        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start git process");
        }
        
        await process.WaitForExitAsync(cancellationToken);
        
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git command failed: {error}");
        }
    }
}
namespace Jio.Core.Commands;

public sealed class VersionCommand
{
    public string? NewVersion { get; init; }
    public bool Major { get; init; }
    public bool Minor { get; init; }
    public bool Patch { get; init; }
    public bool Premajor { get; init; }
    public bool Preminor { get; init; }
    public bool Prepatch { get; init; }
    public bool Prerelease { get; init; }
    public string? Preid { get; init; }
    public bool NoGitTagVersion { get; init; }
    public string? Message { get; init; }
}
namespace Jio.Core.Commands;

public sealed class DedupeCommand
{
    public bool DryRun { get; init; }
    public bool Json { get; init; }
    public string? Package { get; init; }
}
namespace Jio.Core.Commands;

public sealed class PruneCommand
{
    public bool Production { get; init; }
    public bool DryRun { get; init; }
    public bool Json { get; init; }
}
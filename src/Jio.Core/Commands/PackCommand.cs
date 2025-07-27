namespace Jio.Core.Commands;

public sealed class PackCommand
{
    public string? Directory { get; init; }
    public bool DryRun { get; init; }
    public string? Destination { get; init; }
}
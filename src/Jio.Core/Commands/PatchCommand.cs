namespace Jio.Core.Commands;

public sealed class PatchCommand
{
    public string Package { get; init; } = "";
    public string? EditDir { get; init; }
    public bool Create { get; init; }
}
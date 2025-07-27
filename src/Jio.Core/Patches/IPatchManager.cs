namespace Jio.Core.Patches;

public interface IPatchManager
{
    Task<string> CreatePatchAsync(string packageName, string originalPath, string modifiedPath, CancellationToken cancellationToken = default);
    Task ApplyPatchAsync(string patchFile, string targetPath, CancellationToken cancellationToken = default);
    Task<string?> GetExistingPatchAsync(string packageName, CancellationToken cancellationToken = default);
    Task ApplyAllPatchesAsync(string nodeModulesPath, CancellationToken cancellationToken = default);
}
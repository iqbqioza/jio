using Jio.Core.Models;

namespace Jio.Core.LockFiles;

public interface ILockFileManager
{
    Task<LockFile?> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task SaveAsync(LockFile lockFile, string workingDirectory, CancellationToken cancellationToken = default);
    Task<LockFile?> ImportNpmLockAsync(string filePath, CancellationToken cancellationToken = default);
    Task<LockFile?> ImportYarnLockAsync(string filePath, CancellationToken cancellationToken = default);
    Task<LockFile?> ImportPnpmLockAsync(string filePath, CancellationToken cancellationToken = default);
}
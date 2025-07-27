using Jio.Core.Models;

namespace Jio.Core.Registry;

public interface IPackageRegistry
{
    Task<PackageManifest> GetPackageManifestAsync(string name, string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetPackageVersionsAsync(string name, CancellationToken cancellationToken = default);
    Task<Stream> DownloadPackageAsync(string name, string version, CancellationToken cancellationToken = default);
    Task<string> GetPackageIntegrityAsync(string name, string version, CancellationToken cancellationToken = default);
}
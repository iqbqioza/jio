namespace Jio.Core.Security;

public interface ISignatureVerifier
{
    Task<bool> VerifyPackageSignatureAsync(string packageName, string version, Stream packageStream, CancellationToken cancellationToken = default);
    Task<SignatureInfo?> GetPackageSignatureAsync(string packageName, string version, CancellationToken cancellationToken = default);
    Task<bool> VerifySignatureAsync(Stream dataStream, string signature, string publicKey, CancellationToken cancellationToken = default);
    Task<List<TrustedPublisher>> GetTrustedPublishersAsync(CancellationToken cancellationToken = default);
}

public class SignatureInfo
{
    public string Signature { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Algorithm { get; set; } = "RS256";
    public DateTime SignedAt { get; set; }
    public string SignedBy { get; set; } = "";
}

public class TrustedPublisher
{
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public List<string> Packages { get; set; } = new();
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
}
using System.Security.Cryptography;
using System.Text;

namespace Jio.Core.Security;

public static class IntegrityVerifier
{
    public static bool VerifyIntegrity(Stream stream, string expectedIntegrity)
    {
        if (string.IsNullOrEmpty(expectedIntegrity))
            return false;
        
        var parts = expectedIntegrity.Split('-', 2);
        if (parts.Length != 2)
            return false;
        
        var algorithm = parts[0];
        var expectedHash = parts[1];
        
        try
        {
            byte[] computedHash = algorithm.ToLowerInvariant() switch
            {
                "sha1" => SHA1.HashData(ReadStream(stream)),
                "sha256" => SHA256.HashData(ReadStream(stream)),
                "sha384" => SHA384.HashData(ReadStream(stream)),
                "sha512" => SHA512.HashData(ReadStream(stream)),
                _ => throw new NotSupportedException($"Unsupported algorithm: {algorithm}")
            };
            
            var computedHashBase64 = Convert.ToBase64String(computedHash);
            return computedHashBase64 == expectedHash;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
    
    public static string ComputeIntegrity(Stream stream, string algorithm = "sha512")
    {
        byte[] hash = algorithm.ToLowerInvariant() switch
        {
            "sha1" => SHA1.HashData(ReadStream(stream)),
            "sha256" => SHA256.HashData(ReadStream(stream)),
            "sha384" => SHA384.HashData(ReadStream(stream)),
            "sha512" => SHA512.HashData(ReadStream(stream)),
            _ => throw new NotSupportedException($"Unsupported algorithm: {algorithm}")
        };
        
        return $"{algorithm}-{Convert.ToBase64String(hash)}";
    }
    
    private static byte[] ReadStream(Stream stream)
    {
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        
        if (stream.CanSeek)
            stream.Seek(0, SeekOrigin.Begin);
        
        return memoryStream.ToArray();
    }
}
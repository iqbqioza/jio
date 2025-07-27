using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jio.Core.Configuration;
using Jio.Core.Http;
using Jio.Core.Logging;

namespace Jio.Core.Security;

public class SignatureVerifier : ISignatureVerifier
{
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly JioConfiguration _configuration;
    private readonly string _trustedKeysPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SignatureVerifier(ILogger logger, IHttpClientFactory httpClientFactory, JioConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _trustedKeysPath = Path.Combine(_configuration.CacheDirectory, "trusted-keys.json");
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public async Task<bool> VerifyPackageSignatureAsync(string packageName, string version, Stream packageStream, CancellationToken cancellationToken = default)
    {
        if (!_configuration.VerifySignatures)
        {
            return true; // Skip verification if disabled
        }

        try
        {
            _logger.LogDebug($"Verifying signature for {packageName}@{version}");

            // Get signature info from registry
            var signatureInfo = await GetPackageSignatureAsync(packageName, version, cancellationToken);
            if (signatureInfo == null)
            {
                _logger.LogWarning($"No signature found for {packageName}@{version}");
                return false;
            }

            // Verify the signature
            var isValid = await VerifySignatureAsync(packageStream, signatureInfo.Signature, signatureInfo.PublicKey, cancellationToken);
            
            if (isValid)
            {
                _logger.LogDebug($"Signature verification passed for {packageName}@{version}");
            }
            else
            {
                _logger.LogWarning($"Signature verification failed for {packageName}@{version}");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error verifying signature for {packageName}@{version}");
            return false;
        }
    }

    public async Task<SignatureInfo?> GetPackageSignatureAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            
            // Construct signature URL (npm registry format)
            var signatureUrl = $"{_configuration.Registry.TrimEnd('/')}/{packageName}/-/{packageName}-{version}.tgz.sig";
            
            var response = await httpClient.GetAsync(signatureUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Try alternative signature endpoint
                var altSignatureUrl = $"{_configuration.Registry.TrimEnd('/')}/{packageName}/{version}/signature";
                response = await httpClient.GetAsync(altSignatureUrl, cancellationToken);
                
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
            }

            var signatureJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<SignatureInfo>(signatureJson, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to get signature for {packageName}@{version}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> VerifySignatureAsync(Stream dataStream, string signature, string publicKey, CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert base64 signature to bytes
            var signatureBytes = Convert.FromBase64String(signature);
            
            // Read data stream
            var originalPosition = dataStream.Position;
            dataStream.Position = 0;
            
            using var memoryStream = new MemoryStream();
            await dataStream.CopyToAsync(memoryStream, cancellationToken);
            var dataBytes = memoryStream.ToArray();
            
            // Restore original position
            dataStream.Position = originalPosition;

            // Import public key
            using var rsa = RSA.Create();
            var publicKeyBytes = Convert.FromBase64String(publicKey);
            rsa.ImportRSAPublicKey(publicKeyBytes, out _);

            // Verify signature
            var isValid = rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            
            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Signature verification failed: {ex.Message}");
            return false;
        }
    }

    public async Task<List<TrustedPublisher>> GetTrustedPublishersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_trustedKeysPath))
            {
                // Create default trusted publishers list
                var defaultPublishers = new List<TrustedPublisher>
                {
                    new TrustedPublisher
                    {
                        Name = "npm-registry",
                        PublicKey = GetNpmRegistryPublicKey(),
                        Packages = new List<string> { "*" }, // Trust all packages
                        ValidFrom = DateTime.UtcNow.AddYears(-1),
                        ValidUntil = null
                    }
                };

                await SaveTrustedPublishersAsync(defaultPublishers, cancellationToken);
                return defaultPublishers;
            }

            var json = await File.ReadAllTextAsync(_trustedKeysPath, cancellationToken);
            return JsonSerializer.Deserialize<List<TrustedPublisher>>(json, _jsonOptions) ?? new List<TrustedPublisher>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to load trusted publishers: {ex.Message}");
            return new List<TrustedPublisher>();
        }
    }

    private async Task SaveTrustedPublishersAsync(List<TrustedPublisher> publishers, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(publishers, _jsonOptions);
            var directory = Path.GetDirectoryName(_trustedKeysPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(_trustedKeysPath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to save trusted publishers: {ex.Message}");
        }
    }

    private string GetNpmRegistryPublicKey()
    {
        // This would be the actual npm registry public key in a real implementation
        // For demo purposes, returning a placeholder
        return Convert.ToBase64String(Encoding.UTF8.GetBytes("npm-registry-public-key-placeholder"));
    }
}
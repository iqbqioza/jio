using System.Net;
using Jio.Core.Configuration;

namespace Jio.Core.Http;

public static class ProxyAwareHttpClientFactory
{
    public static HttpClient CreateHttpClient(JioConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        
        // Configure proxy
        if (!string.IsNullOrEmpty(configuration.HttpsProxy) || !string.IsNullOrEmpty(configuration.Proxy))
        {
            var proxyUrl = configuration.HttpsProxy ?? configuration.Proxy;
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                handler.Proxy = new WebProxy(proxyUrl);
                handler.UseProxy = true;
                
                // Configure no-proxy list
                if (!string.IsNullOrEmpty(configuration.NoProxy))
                {
                    var bypassList = configuration.NoProxy
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToArray();
                    
                    handler.Proxy = new WebProxy(proxyUrl, true, bypassList);
                }
            }
        }
        
        // Configure SSL
        if (!configuration.StrictSsl)
        {
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        }
        
        // Configure CA certificate if provided
        if (!string.IsNullOrEmpty(configuration.CaFile) && File.Exists(configuration.CaFile))
        {
            // Note: In production, you would load and configure the CA certificate
            // This is a simplified implementation
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                    return true;
                
                // In a real implementation, validate against the CA file
                return !configuration.StrictSsl;
            };
        }
        
        var client = new HttpClient(handler)
        {
            Timeout = configuration.HttpTimeout
        };
        
        // Set user agent
        if (!string.IsNullOrEmpty(configuration.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(configuration.UserAgent);
        }
        
        return client;
    }
}
namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// HTTP/Proxy settings used by Authenticate and RequestData.
/// 1:1 the same shape as in the reference repo (Samedis.cs).
/// </summary>
public class HttpSettings
{
    public string? Proxy { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public bool ValidateCertificate { get; set; } = true;

    /// <summary>
    /// Hard timeout per HTTP request, in seconds. Default 30s.
    /// Prevents the UI/worker from hanging indefinitely when the auth/API endpoint is unreachable.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

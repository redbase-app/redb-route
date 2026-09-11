using Amazon.Runtime;
using Amazon.S3;

namespace redb.Route.S3;

/// <summary>
/// Shared wiring of connection options into <see cref="AmazonS3Config"/> — one place for the
/// endpoint-options path and the <see cref="S3ConnectionFactory"/> path (Ф11 волна S-Б, S-5:
/// SocketTimeout/RetryMode/TrustAllCertificates were declared but never reached the SDK).
/// </summary>
internal static class S3ClientConfigSupport
{
    /// <summary>
    /// Applies timeouts, connection limits, retry policy and certificate trust to the config.
    /// <c>connectionTimeoutMs</c> maps to <see cref="ClientConfig.ConnectTimeout"/>,
    /// <c>socketTimeoutMs</c> to the overall request <see cref="ClientConfig.Timeout"/>.
    /// </summary>
    internal static void Apply(AmazonS3Config config,
        int connectionTimeoutMs, int socketTimeoutMs, int maxConnections, int retryCount,
        string retryMode, bool trustAllCertificates)
    {
        config.ConnectTimeout = TimeSpan.FromMilliseconds(connectionTimeoutMs);
        config.Timeout = TimeSpan.FromMilliseconds(socketTimeoutMs);
        config.MaxConnectionsPerServer = maxConnections;
        config.MaxErrorRetry = retryCount;
        config.RetryMode = ParseRetryMode(retryMode);

        if (trustAllCertificates)
            config.HttpClientFactory = new TrustAllHttpClientFactory();
    }

    // SDK v4 knows only Standard and Adaptive (Legacy retired with v3).
    private static RequestRetryMode ParseRetryMode(string retryMode) =>
        string.Equals(retryMode, "adaptive", StringComparison.OrdinalIgnoreCase)
            ? RequestRetryMode.Adaptive
            : RequestRetryMode.Standard;
}

/// <summary>
/// HTTP client factory that disables server-certificate validation — for MinIO/Ceph and
/// friends behind self-signed TLS. Опасно по определению; включается только явной опцией
/// <c>trustAllCertificates=true</c>.
/// </summary>
internal sealed class TrustAllHttpClientFactory : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
}

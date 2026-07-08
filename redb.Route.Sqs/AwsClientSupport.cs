using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;

namespace redb.Route.Sqs;

/// <summary>
/// Shared AWS client plumbing for the SQS and SNS endpoints: credential resolution and the common
/// <see cref="ClientConfig"/> settings (region vs explicit service URL, timeouts, retries, proxy).
/// Mirrors <c>redb.Route.S3</c> so behaviour is identical across the AWS connectors.
/// </summary>
internal static class AwsClientSupport
{
    /// <summary>
    /// Resolves AWS credentials with the same precedence as the S3 connector:
    /// default provider chain → named profile → static session token → static access/secret keys.
    /// </summary>
    public static AWSCredentials ResolveCredentials(AwsEndpointOptions o)
    {
        if (o.UseDefaultCredentialsProvider)
#pragma warning disable CS0618 // FallbackCredentialsFactory is deprecated but still functional
            return FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618

        if (!string.IsNullOrEmpty(o.ProfileName))
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(o.ProfileName, out var profileCreds))
                return profileCreds;
            throw new InvalidOperationException($"AWS profile '{o.ProfileName}' not found.");
        }

        if (!string.IsNullOrEmpty(o.SessionToken))
            return new SessionAWSCredentials(o.AccessKey, o.SecretKey, o.SessionToken);

        return new BasicAWSCredentials(o.AccessKey, o.SecretKey);
    }

    /// <summary>
    /// Applies region / explicit service URL, timeout, retry and proxy settings onto an AWS service
    /// config. <c>ServiceUrl</c> and <c>RegionEndpoint</c> are mutually exclusive — an explicit URL
    /// (LocalStack / ElasticMQ) wins and the region is kept only for request signing.
    /// </summary>
    public static void ApplyCommonConfig(ClientConfig config, AwsEndpointOptions o)
    {
        config.Timeout = TimeSpan.FromMilliseconds(o.ConnectionTimeout);
        config.MaxErrorRetry = o.RetryCount;

        if (!string.IsNullOrEmpty(o.ServiceUrl))
        {
            config.ServiceURL = o.ServiceUrl;
            config.AuthenticationRegion = o.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
        }

        if (!string.IsNullOrEmpty(o.ProxyHost) && o.ProxyPort > 0)
        {
            config.ProxyHost = o.ProxyHost;
            config.ProxyPort = o.ProxyPort;
        }
    }

    /// <summary>
    /// Maps an exchange header to the message-attribute name a producer should send, or <c>null</c> to
    /// skip it. User headers pass through unchanged; a received SQS attribute (re-exposed under
    /// <c>redbSqs.attr.</c>) is forwarded with the prefix stripped (so sqs→sqs/sns bridges preserve
    /// attributes); framework headers (<c>redbSqs.*</c> / <c>redbSns.*</c>) are dropped.
    /// </summary>
    public static string? MapHeaderToAttributeName(string key)
    {
        if (key.StartsWith(SqsHeaders.MessageAttributePrefix, StringComparison.Ordinal))
            return key[SqsHeaders.MessageAttributePrefix.Length..];
        if (SqsHeaders.IsSqsHeader(key) || SnsHeaders.IsSnsHeader(key))
            return null;
        return key;
    }

    /// <summary>
    /// Coerces an exchange body to the text payload SQS/SNS carry: strings pass through, <c>byte[]</c>
    /// is decoded as UTF-8, everything else is JSON-serialized.
    /// </summary>
    public static string ResolveTextBody(object? body) => body switch
    {
        null => "",
        string s => s,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => JsonSerializer.Serialize(body),
    };
}

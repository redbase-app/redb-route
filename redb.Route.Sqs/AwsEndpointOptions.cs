using redb.Route.Core;

namespace redb.Route.Sqs;

/// <summary>
/// Base endpoint options shared by the SQS and SNS endpoints — AWS connection/credentials.
/// Bound from the URI query string by <see cref="EndpointOptions.BindFromUri"/> (reflection includes
/// these inherited properties). Credentials resolution mirrors <c>redb.Route.S3</c>.
/// </summary>
public abstract class AwsEndpointOptions : EndpointOptions
{
    /// <summary>
    /// Explicit service endpoint URL (e.g. <c>http://localhost:4566</c> for LocalStack / ElasticMQ).
    /// When set, it overrides the region endpoint. Empty = use the AWS region endpoint.
    /// </summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>AWS region (e.g. <c>us-east-1</c>, <c>eu-west-1</c>). Default <c>us-east-1</c>.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>AWS access key ID. Required unless <see cref="UseDefaultCredentialsProvider"/> or <see cref="ProfileName"/> is used.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>AWS secret access key.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>AWS session token for temporary (STS) credentials.</summary>
    public string SessionToken { get; set; } = "";

    /// <summary>Named AWS profile from the shared credentials file. Overrides access/secret keys when set.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>Use the default AWS credential provider chain (env vars / instance profile / SSO). Default false.</summary>
    public bool UseDefaultCredentialsProvider { get; set; }

    /// <summary>Name of a pre-registered connection factory in the route registry (overrides URI credentials when found).</summary>
    public string ConnectionFactory { get; set; } = "";

    /// <summary>Maximum SDK retry attempts on transient errors. Default 3.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>HTTP client timeout in milliseconds. Default 30000.</summary>
    public int ConnectionTimeout { get; set; } = 30000;

    /// <summary>Optional HTTP proxy host.</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>Optional HTTP proxy port.</summary>
    public int ProxyPort { get; set; }

    /// <summary>Validates the shared AWS options. Concrete option classes call this from <see cref="EndpointOptions.Validate"/>.</summary>
    protected void ValidateAws()
    {
        var haveStaticKeys = !string.IsNullOrEmpty(AccessKey) && !string.IsNullOrEmpty(SecretKey);
        if (!UseDefaultCredentialsProvider
            && string.IsNullOrEmpty(ProfileName)
            && string.IsNullOrEmpty(ConnectionFactory)
            && !haveStaticKeys)
        {
            throw new ArgumentException(
                "AWS credentials required: set accessKey+secretKey, or profileName, or useDefaultCredentialsProvider=true, "
                + "or a registered connectionFactory.");
        }

        if (RetryCount < 0)
            throw new ArgumentException($"retryCount cannot be negative. Got: {RetryCount}");
        if (ConnectionTimeout <= 0)
            throw new ArgumentException($"connectionTimeout must be positive. Got: {ConnectionTimeout}");
    }
}

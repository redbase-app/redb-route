using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace redb.Route.Sqs;

/// <summary>
/// Reusable AWS credentials/endpoint holder that can be pre-registered in the route registry and
/// referenced from a URI via <c>connectionFactory=name</c> (mirrors <c>redb.Route.S3</c>). One factory
/// builds both SQS and SNS clients since they share credentials, region and service URL.
/// </summary>
public sealed class AwsConnectionFactory : AwsEndpointOptions
{
    /// <inheritdoc />
    /// <remarks>No endpoint-shape validation for a bare factory — only credential resolution matters.</remarks>
    public override void Validate() => ValidateAws();

    /// <summary>Builds a configured <see cref="IAmazonSQS"/> client.</summary>
    public IAmazonSQS BuildSqs()
    {
        var config = new AmazonSQSConfig();
        AwsClientSupport.ApplyCommonConfig(config, this);
        return new AmazonSQSClient(AwsClientSupport.ResolveCredentials(this), config);
    }

    /// <summary>Builds a configured <see cref="IAmazonSimpleNotificationService"/> client.</summary>
    public IAmazonSimpleNotificationService BuildSns()
    {
        var config = new AmazonSimpleNotificationServiceConfig();
        AwsClientSupport.ApplyCommonConfig(config, this);
        return new AmazonSimpleNotificationServiceClient(AwsClientSupport.ResolveCredentials(this), config);
    }
}

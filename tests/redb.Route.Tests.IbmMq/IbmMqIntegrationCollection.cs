namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Disables parallel execution between the IBM MQ integration test classes so that
/// shared DEV.QUEUE.* queues are not contended across classes.
/// </summary>
[CollectionDefinition("IbmMqIntegration", DisableParallelization = true)]
public sealed class IbmMqIntegrationCollection
{
}

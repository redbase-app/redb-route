namespace redb.Route.TestKit;

/// <summary>
/// Maps an endpoint URI to the <c>mock://</c> URI that stands in for it under
/// <see cref="AdviceWithBuilder.MockEndpoints"/>, the same way Apache Camel names its mocks
/// (<c>mock:kafka:orders</c>): the query string is dropped and <c>scheme://path</c> collapses to
/// <c>scheme:path</c>, so <c>kafka://orders-vip?acks=all</c> becomes <c>mock://kafka:orders-vip</c>.
/// </summary>
public static class MockUri
{
    /// <summary>Returns the <c>mock://</c> URI standing in for <paramref name="endpointUri"/>; a <c>mock://</c> URI is returned unchanged.</summary>
    public static string For(string endpointUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUri);
        if (endpointUri.StartsWith("mock://", StringComparison.OrdinalIgnoreCase) ||
            endpointUri.StartsWith("mock:", StringComparison.OrdinalIgnoreCase) && !endpointUri.Contains("://", StringComparison.Ordinal))
            return endpointUri;

        var queryIndex = endpointUri.IndexOf('?');
        var withoutQuery = queryIndex >= 0 ? endpointUri[..queryIndex] : endpointUri;
        return "mock://" + withoutQuery.Replace("://", ":", StringComparison.Ordinal);
    }
}

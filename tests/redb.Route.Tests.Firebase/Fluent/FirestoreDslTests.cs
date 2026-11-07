using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirestoreDslTests
{
    [Fact]
    public void Collection_GeneratesBasicUri()
    {
        string uri = Firestore.Collection("users");
        uri.Should().Be("fstore://users");
    }

    [Fact]
    public void Collection_WithNestedPath()
    {
        string uri = Firestore.Collection("users/uid/orders");
        uri.Should().Be("fstore://users/uid/orders");
    }

    [Fact]
    public void Builder_WithAllOptions_GeneratesFullUri()
    {
        string uri = Firestore.Collection("orders")
            .Operation(FirestoreOperationType.Query)
            .DocumentId("doc-123")
            .Where("status==pending")
            .OrderBy("createdAt desc")
            .Limit(50)
            .Offset(10)
            .Merge()
            .Realtime()
            .Delay(3000)
            .RawJson()
            .CredentialPath("/secrets/sa.json")
            .ProjectId("proj")
            .ConnectionFactory("cf");

        uri.Should().Contain("fstore://orders?");
        uri.Should().Contain("operation=Query");
        uri.Should().Contain("documentId=doc-123");
        uri.Should().Contain("where=");
        uri.Should().Contain("orderBy=");
        uri.Should().Contain("limit=50");
        uri.Should().Contain("offset=10");
        uri.Should().Contain("merge=True");
        uri.Should().Contain("realtime=True");
        uri.Should().Contain("delay=3000");
        uri.Should().Contain("rawJson=True");
        uri.Should().Contain("credentialPath=");
        uri.Should().Contain("projectId=proj");
        uri.Should().Contain("connectionFactory=cf");
    }

    [Fact]
    public void Builder_ImplicitStringConversion()
    {
        FirestoreBuilder builder = Firestore.Collection("test");
        string uri = builder;
        uri.Should().Be("fstore://test");
    }

    [Fact]
    public void Builder_ToString_EqualsImplicit()
    {
        var builder = Firestore.Collection("test").Limit(10);
        builder.ToString().Should().Be((string)builder);
    }

    [Fact]
    public void Builder_NoParams_NoQueryString()
    {
        string uri = Firestore.Collection("data");
        uri.Should().NotContain("?");
    }
}

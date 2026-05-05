using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ldap;
using Xunit.Abstractions;

namespace redb.Route.Tests.Ldap;

/// <summary>
/// Integration tests for the LDAP connector against a real OpenLDAP instance.
/// Expects OpenLDAP at localhost:389 with seed data from docker-compose.
/// <para>
/// Admin DN:  cn=admin,dc=redb,dc=test
/// Password:  admin
/// Test data: 5 users in ou=users, 2 groups in ou=groups
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class LdapIntegrationTests
{
    private const string Server = "localhost";
    private const int Port = 389;
    private const string BindDn = "cn=admin,dc=redb,dc=test";
    private const string BindPassword = "admin";
    private const string BaseDn = "dc=redb,dc=test";
    private const string UsersDn = "ou=users,dc=redb,dc=test";
    private const string GroupsDn = "ou=groups,dc=redb,dc=test";

    private readonly ITestOutputHelper _output;
    private readonly LdapComponent _component = new();

    public LdapIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private LdapEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (LdapEndpoint)_component.CreateEndpoint(uri);
    }

    private string BuildSearchUri(string baseDn, string? filter = null, string? scope = null, string? attributes = null)
    {
        var uri = $"ldap:SEARCH:{baseDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        if (filter != null) uri += $"&filter={Uri.EscapeDataString(filter)}";
        if (scope != null) uri += $"&scope={scope}";
        if (attributes != null) uri += $"&attributes={attributes}";
        return uri;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Connection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Connect_ValidCredentials_Succeeds()
    {
        using var ep = CreateEndpoint(BuildSearchUri(BaseDn, "(objectClass=*)"));
        var producer = (LdapProducer)ep.CreateProducer();

        await producer.Start();
        await producer.Stop();

        _output.WriteLine("Successfully connected and disconnected from LDAP");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — Subtree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_Subtree_AllUsers_Returns5()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(objectClass=inetOrgPerson)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(5);

        var dns = results.Select(e => e.Dn).ToList();
        dns.Should().Contain(dn => dn.Contains("alice"));
        dns.Should().Contain(dn => dn.Contains("bob"));
        dns.Should().Contain(dn => dn.Contains("charlie"));
        dns.Should().Contain(dn => dn.Contains("diana"));
        dns.Should().Contain(dn => dn.Contains("eve"));

        _output.WriteLine($"Found {results.Count} users:");
        foreach (var entry in results)
            _output.WriteLine($"  {entry.Dn} — mail={entry.GetString("mail")}");
    }

    [Fact]
    public async Task Search_Subtree_Groups_Returns2()
    {
        using var ep = CreateEndpoint(BuildSearchUri(GroupsDn, "(objectClass=groupOfNames)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(2);

        var cns = results.Select(e => e.GetString("cn")).ToList();
        cns.Should().Contain("developers");
        cns.Should().Contain("admins");

        _output.WriteLine($"Found {results.Count} groups: {string.Join(", ", cns)}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — Scope: OneLevel
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_OneLevel_BaseDn_ReturnsOUs()
    {
        using var ep = CreateEndpoint(BuildSearchUri(BaseDn, "(objectClass=organizationalUnit)", "onelevel"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(2);

        _output.WriteLine($"Found {results.Count} OUs at one level: {string.Join(", ", results.Select(e => e.Dn))}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — Scope: Base
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_Base_SpecificUser_ReturnsSingleEntry()
    {
        var aliceDn = "cn=alice,ou=users,dc=redb,dc=test";
        using var ep = CreateEndpoint(BuildSearchUri(aliceDn, "(objectClass=*)", "base"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(1);
        results[0].Dn.Should().Contain("alice");
        results[0].GetString("mail").Should().Be("alice@redb.test");

        _output.WriteLine($"Base search returned: {results[0].Dn}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — Attribute filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_SpecificAttributes_ReturnsOnlyRequested()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=alice)", "subtree", "cn,mail"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(1);

        var alice = results[0];
        alice.GetString("cn").Should().Be("alice");
        alice.GetString("mail").Should().Be("alice@redb.test");
        // sn was not requested — should not be present
        alice.GetString("sn").Should().BeNull();

        _output.WriteLine($"Filtered attributes: cn={alice.GetString("cn")}, mail={alice.GetString("mail")}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — LDAP filter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_FilterByMail_ReturnsMatchingEntries()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(mail=bob@redb.test)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(1);
        results[0].GetString("cn").Should().Be("bob");

        _output.WriteLine($"Filter by mail found: {results[0].Dn}");
    }

    [Fact]
    public async Task Search_FilterByWildcard_ReturnsMultiple()
    {
        // Users with "a" in cn: alice, charlie, diana
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=*a*)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().BeGreaterThanOrEqualTo(2);

        _output.WriteLine($"Wildcard filter (cn=*a*) found {results.Count} entries");
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmptyList()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=nonexistent_user_12345)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(0);

        _output.WriteLine("Empty result set — correct");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Exchange headers
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_SetsExchangeHeaders()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(objectClass=inetOrgPerson)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers.Should().ContainKey(LdapHeaders.ResultCount);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.SearchTime);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.BaseDn);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.Filter);

        var resultCount = (int)exchange.In.Headers[LdapHeaders.ResultCount];
        resultCount.Should().Be(5);

        var searchTime = (long)exchange.In.Headers[LdapHeaders.SearchTime];
        searchTime.Should().BeGreaterThanOrEqualTo(0);

        _output.WriteLine($"ResultCount={resultCount}, SearchTime={searchTime}ms");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Connection pool
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectionPool_MultipleSearches_ReuseConnections()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(objectClass=inetOrgPerson)", "subtree") + "&maxConnections=2");
        var producer = (LdapProducer)ep.CreateProducer();
        await producer.Start();

        // Run 5 sequential searches — should reuse pooled connections
        for (var i = 0; i < 5; i++)
        {
            var exchange = new Exchange(new Message($"search-{i}"));
            await producer.Process(exchange);
            var results = exchange.In.Body as List<LdapEntry>;
            results.Should().NotBeNull();
            results!.Count.Should().Be(5);
        }

        await producer.Stop();
        _output.WriteLine("5 sequential searches completed successfully with connection pool");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Consumer (WATCH) — ModifyTimestamp
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Watch_InitialLoad_DeliversAllEntries()
    {
        var watchUri = $"ldap:WATCH:{UsersDn}?server={Server}&port={Port}" +
                       $"&bindDn={BindDn}&bindPassword={BindPassword}" +
                       $"&filter=(objectClass=inetOrgPerson)&initialLoad=true&pollInterval=1000";

        using var ep = CreateEndpoint(watchUri);
        var received = new List<LdapEntry>();
        var tcs = new TaskCompletionSource<bool>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                if (ex.In.Body is LdapEntry entry)
                {
                    lock (received) received.Add(entry);
                    _output.WriteLine($"  Received: {entry.Dn} ({entry.ChangeType})");
                    if (received.Count >= 5) tcs.TrySetResult(true);
                }
                return Task.CompletedTask;
            });

        var consumer = (LdapConsumer)ep.CreateConsumer(processor);
        await consumer.Start();

        // Wait for initial load to deliver all 5 users (or timeout)
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();

        completed.Should().Be(tcs.Task, "all 5 users should be delivered within 15s");
        received.Count.Should().BeGreaterThanOrEqualTo(5);

        _output.WriteLine($"Watch delivered {received.Count} entries on initial load");
    }

    // ═══════════════════════════════════════════════════════════════
    //  DSL → Producer round-trip
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DslSearch_EndToEnd_Works()
    {
        string uri = LdapDsl.Search(UsersDn)
            .Server(Server)
            .Port(Port)
            .BindDn(BindDn)
            .BindPassword(BindPassword)
            .Filter("(objectClass=inetOrgPerson)")
            .Scope(LdapSearchScope.Subtree)
            .Attributes("cn", "mail")
            .PageSize(100);

        using var ep = CreateEndpoint(uri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("dsl-search"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var results = exchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results!.Count.Should().Be(5);

        _output.WriteLine($"DSL round-trip search returned {results.Count} entries");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Search — Server/Port/Ssl/Scope headers
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Search_SetsServerPortSslScopeHeaders()
    {
        using var ep = CreateEndpoint(BuildSearchUri(UsersDn, "(objectClass=inetOrgPerson)", "subtree"));
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("headers-check"));

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers.Should().ContainKey(LdapHeaders.Server);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.Port);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.Ssl);
        exchange.In.Headers.Should().ContainKey(LdapHeaders.Scope);

        exchange.In.Headers[LdapHeaders.Server].Should().Be(Server);
        ((int)exchange.In.Headers[LdapHeaders.Port]).Should().Be(Port);
        ((bool)exchange.In.Headers[LdapHeaders.Ssl]).Should().BeFalse();

        _output.WriteLine($"Server={exchange.In.Headers[LdapHeaders.Server]}, " +
                          $"Port={exchange.In.Headers[LdapHeaders.Port]}, " +
                          $"Ssl={exchange.In.Headers[LdapHeaders.Ssl]}, " +
                          $"Scope={exchange.In.Headers[LdapHeaders.Scope]}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  BIND — User authentication
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bind_ValidCredentials_ReturnsTrue()
    {
        var bindUri = $"ldap:BIND:{BaseDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";

        using var ep = CreateEndpoint(bindUri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("bind"));
        exchange.In.Headers[LdapHeaders.AuthDn] = "cn=alice,ou=users,dc=redb,dc=test";
        exchange.In.Headers[LdapHeaders.AuthPassword] = "alice123";

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        ((bool)exchange.In.Body).Should().BeTrue();
        ((int)exchange.In.Headers[LdapHeaders.BindResult]).Should().Be(0);
        // Password must be cleared from headers
        exchange.In.Headers.Should().NotContainKey(LdapHeaders.AuthPassword);

        _output.WriteLine("BIND alice with correct password → success");
    }

    [Fact]
    public async Task Bind_WrongPassword_ReturnsFalse()
    {
        var bindUri = $"ldap:BIND:{BaseDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";

        using var ep = CreateEndpoint(bindUri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("bind"));
        exchange.In.Headers[LdapHeaders.AuthDn] = "cn=alice,ou=users,dc=redb,dc=test";
        exchange.In.Headers[LdapHeaders.AuthPassword] = "wrong_password";

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        ((bool)exchange.In.Body).Should().BeFalse();
        ((int)exchange.In.Headers[LdapHeaders.BindResult]).Should().NotBe(0);
        exchange.In.Headers.Should().NotContainKey(LdapHeaders.AuthPassword);

        _output.WriteLine($"BIND alice with wrong password → failed, code={exchange.In.Headers[LdapHeaders.BindResult]}");
    }

    [Fact]
    public async Task Bind_InvalidDn_ReturnsFalse()
    {
        var bindUri = $"ldap:BIND:{BaseDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";

        using var ep = CreateEndpoint(bindUri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("bind"));
        exchange.In.Headers[LdapHeaders.AuthDn] = "cn=nonexistent,dc=redb,dc=test";
        exchange.In.Headers[LdapHeaders.AuthPassword] = "whatever";

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        ((bool)exchange.In.Body).Should().BeFalse();
        exchange.In.Headers.Should().NotContainKey(LdapHeaders.AuthPassword);

        _output.WriteLine("BIND nonexistent user → failed as expected");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADD + DELETE — Create and remove entries
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Add_Delete_RoundTrip()
    {
        var testDn = "cn=testuser,ou=users,dc=redb,dc=test";

        // ADD
        var addUri = $"ldap:ADD:{UsersDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var addEp = CreateEndpoint(addUri);
        var addProducer = (LdapProducer)addEp.CreateProducer();
        var addExchange = new Exchange(new Message("add"));
        addExchange.In.Headers[LdapHeaders.Dn] = testDn;
        addExchange.In.Body = new Dictionary<string, object>
        {
            ["objectClass"] = new[] { "inetOrgPerson", "organizationalPerson", "person", "top" },
            ["cn"] = "testuser",
            ["sn"] = "User",
            ["uid"] = "testuser",
            ["mail"] = "testuser@redb.test",
            ["userPassword"] = "test123"
        };

        await addProducer.Start();
        await addProducer.Process(addExchange);
        await addProducer.Stop();

        ((string)addExchange.In.Body).Should().Be(testDn);
        _output.WriteLine($"ADD created: {testDn}");

        // Verify via SEARCH
        using var searchEp = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=testuser)", "subtree"));
        var searchProducer = (LdapProducer)searchEp.CreateProducer();
        var searchExchange = new Exchange(new Message("verify-add"));
        await searchProducer.Start();
        await searchProducer.Process(searchExchange);
        await searchProducer.Stop();

        var found = searchExchange.In.Body as List<LdapEntry>;
        found.Should().NotBeNull();
        found!.Count.Should().Be(1);
        found[0].GetString("mail").Should().Be("testuser@redb.test");
        _output.WriteLine($"SEARCH verified: {found[0].Dn}");

        // DELETE
        var deleteUri = $"ldap:DELETE:{testDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var deleteEp = CreateEndpoint(deleteUri);
        var deleteProducer = (LdapProducer)deleteEp.CreateProducer();
        var deleteExchange = new Exchange(new Message("delete"));
        deleteExchange.In.Headers[LdapHeaders.Dn] = testDn;

        await deleteProducer.Start();
        await deleteProducer.Process(deleteExchange);
        await deleteProducer.Stop();

        ((string)deleteExchange.In.Body).Should().Be(testDn);
        ((bool)deleteExchange.In.Headers[LdapHeaders.Deleted]).Should().BeTrue();
        _output.WriteLine($"DELETE removed: {testDn}");

        // Verify deletion via SEARCH
        using var verifyEp = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=testuser)", "subtree"));
        var verifyProducer = (LdapProducer)verifyEp.CreateProducer();
        var verifyExchange = new Exchange(new Message("verify-delete"));
        await verifyProducer.Start();
        await verifyProducer.Process(verifyExchange);
        await verifyProducer.Stop();

        var after = verifyExchange.In.Body as List<LdapEntry>;
        after.Should().NotBeNull();
        after!.Count.Should().Be(0);
        _output.WriteLine("Deletion verified — entry gone");
    }

    // ═══════════════════════════════════════════════════════════════
    //  MODIFY — Change attributes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Modify_ChangeAttribute_Succeeds()
    {
        var aliceDn = "cn=alice,ou=users,dc=redb,dc=test";

        // MODIFY: set telephoneNumber
        var modifyUri = $"ldap:MODIFY:{aliceDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var modEp = CreateEndpoint(modifyUri);
        var modProducer = (LdapProducer)modEp.CreateProducer();
        var modExchange = new Exchange(new Message("modify"));
        modExchange.In.Headers[LdapHeaders.Dn] = aliceDn;
        modExchange.In.Body = new Dictionary<string, object>
        {
            ["telephoneNumber"] = "+7-999-123-4567"
        };

        await modProducer.Start();
        await modProducer.Process(modExchange);
        await modProducer.Stop();

        ((string)modExchange.In.Body).Should().Be(aliceDn);
        ((int)modExchange.In.Headers[LdapHeaders.ModCount]).Should().Be(1);
        _output.WriteLine($"MODIFY set telephoneNumber on alice");

        // Verify via SEARCH
        using var searchEp = CreateEndpoint(BuildSearchUri(aliceDn, "(objectClass=*)", "base", "telephoneNumber"));
        var searchProducer = (LdapProducer)searchEp.CreateProducer();
        var searchExchange = new Exchange(new Message("verify-modify"));
        await searchProducer.Start();
        await searchProducer.Process(searchExchange);
        await searchProducer.Stop();

        var results = searchExchange.In.Body as List<LdapEntry>;
        results.Should().NotBeNull();
        results![0].GetString("telephoneNumber").Should().Be("+7-999-123-4567");
        _output.WriteLine($"Verified telephoneNumber={results[0].GetString("telephoneNumber")}");

        // Restore: set back to original value
        using var restoreEp = CreateEndpoint(modifyUri);
        var restoreProducer = (LdapProducer)restoreEp.CreateProducer();
        var restoreExchange = new Exchange(new Message("restore"));
        restoreExchange.In.Headers[LdapHeaders.Dn] = aliceDn;
        restoreExchange.In.Body = new Dictionary<string, object>
        {
            ["telephoneNumber"] = "+1-555-0101"
        };

        await restoreProducer.Start();
        await restoreProducer.Process(restoreExchange);
        await restoreProducer.Stop();
        _output.WriteLine("Restored alice telephoneNumber");
    }

    // ═══════════════════════════════════════════════════════════════
    //  COMPARE — Attribute value comparison
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Compare_MatchingValue_ReturnsTrue()
    {
        var aliceDn = "cn=alice,ou=users,dc=redb,dc=test";
        var compareUri = $"ldap:COMPARE:{aliceDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";

        using var ep = CreateEndpoint(compareUri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("compare-match"));
        exchange.In.Headers[LdapHeaders.Dn] = aliceDn;
        exchange.In.Headers[LdapHeaders.CompareAttribute] = "mail";
        exchange.In.Headers[LdapHeaders.CompareValue] = "alice@redb.test";

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        ((bool)exchange.In.Body).Should().BeTrue();
        ((bool)exchange.In.Headers[LdapHeaders.CompareResult]).Should().BeTrue();

        _output.WriteLine("COMPARE alice mail=alice@redb.test → true");
    }

    [Fact]
    public async Task Compare_NonMatchingValue_ReturnsFalse()
    {
        var aliceDn = "cn=alice,ou=users,dc=redb,dc=test";
        var compareUri = $"ldap:COMPARE:{aliceDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";

        using var ep = CreateEndpoint(compareUri);
        var producer = (LdapProducer)ep.CreateProducer();
        var exchange = new Exchange(new Message("compare-no-match"));
        exchange.In.Headers[LdapHeaders.Dn] = aliceDn;
        exchange.In.Headers[LdapHeaders.CompareAttribute] = "mail";
        exchange.In.Headers[LdapHeaders.CompareValue] = "wrong@redb.test";

        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        ((bool)exchange.In.Body).Should().BeFalse();
        ((bool)exchange.In.Headers[LdapHeaders.CompareResult]).Should().BeFalse();

        _output.WriteLine("COMPARE alice mail=wrong@redb.test → false");
    }

    // ═══════════════════════════════════════════════════════════════
    //  RENAME — Rename entry and move back
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Rename_ChangeRdn_Succeeds()
    {
        var originalDn = "cn=renametest,ou=users,dc=redb,dc=test";
        var renamedDn = "cn=renametest-renamed,ou=users,dc=redb,dc=test";

        // First create a temp entry
        var addUri = $"ldap:ADD:{UsersDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var addEp = CreateEndpoint(addUri);
        var addProducer = (LdapProducer)addEp.CreateProducer();
        var addExchange = new Exchange(new Message("add-for-rename"));
        addExchange.In.Headers[LdapHeaders.Dn] = originalDn;
        addExchange.In.Body = new Dictionary<string, object>
        {
            ["objectClass"] = new[] { "inetOrgPerson", "organizationalPerson", "person", "top" },
            ["cn"] = "renametest",
            ["sn"] = "Test",
            ["uid"] = "renametest"
        };

        await addProducer.Start();
        await addProducer.Process(addExchange);
        await addProducer.Stop();
        _output.WriteLine($"Created temp entry: {originalDn}");

        // RENAME
        var renameUri = $"ldap:RENAME:{originalDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var renameEp = CreateEndpoint(renameUri);
        var renameProducer = (LdapProducer)renameEp.CreateProducer();
        var renameExchange = new Exchange(new Message("rename"));
        renameExchange.In.Headers[LdapHeaders.Dn] = originalDn;
        renameExchange.In.Headers[LdapHeaders.NewRdn] = "cn=renametest-renamed";

        await renameProducer.Start();
        await renameProducer.Process(renameExchange);
        await renameProducer.Stop();

        var newDn = (string)renameExchange.In.Body;
        newDn.Should().Be(renamedDn);
        ((string)renameExchange.In.Headers[LdapHeaders.OldDn]).Should().Be(originalDn);
        ((string)renameExchange.In.Headers[LdapHeaders.NewDn]).Should().Be(renamedDn);
        _output.WriteLine($"RENAME {originalDn} → {newDn}");

        // Verify renamed entry exists
        using var searchEp = CreateEndpoint(BuildSearchUri(UsersDn, "(cn=renametest-renamed)", "subtree"));
        var searchProducer = (LdapProducer)searchEp.CreateProducer();
        var searchExchange = new Exchange(new Message("verify-rename"));
        await searchProducer.Start();
        await searchProducer.Process(searchExchange);
        await searchProducer.Stop();

        var found = searchExchange.In.Body as List<LdapEntry>;
        found.Should().NotBeNull();
        found!.Count.Should().Be(1);
        _output.WriteLine("Verified renamed entry exists");

        // Cleanup: delete the renamed entry
        var deleteUri = $"ldap:DELETE:{renamedDn}?server={Server}&port={Port}&bindDn={BindDn}&bindPassword={BindPassword}";
        using var deleteEp = CreateEndpoint(deleteUri);
        var deleteProducer = (LdapProducer)deleteEp.CreateProducer();
        var deleteExchange = new Exchange(new Message("cleanup"));
        deleteExchange.In.Headers[LdapHeaders.Dn] = renamedDn;
        await deleteProducer.Start();
        await deleteProducer.Process(deleteExchange);
        await deleteProducer.Stop();
        _output.WriteLine("Cleanup: deleted renamed entry");
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

/// <summary>
/// Security regression tests: no secret (password / token / key / userinfo password)
/// may survive the logging / telemetry / DTO boundary. Guards against the class of
/// leak where connector credentials in the endpoint URI reach logs and dashboards.
/// </summary>
public class UriSanitizationTests
{
    // --- EndpointUri.Sanitize — format-preserving redaction ----------------

    [Theory]
    // scheme separator (:// vs :) and non-secret params preserved byte-for-byte
    [InlineData("direct://props", "direct://props")]
    [InlineData("kafka://orders?brokers=localhost:9092&groupId=g", "kafka://orders?brokers=localhost:9092&groupId=g")]
    [InlineData("redis:GET:user:123?ttl=60", "redis:GET:user:123?ttl=60")]
    public void Sanitize_NoSecrets_ReturnsUnchanged(string uri, string expected)
    {
        EndpointUri.Sanitize(uri).Should().Be(expected);
    }

    [Theory]
    [InlineData("kafka://orders?password=s3cret", "kafka://orders?password=****")]
    [InlineData("ldap://host?bindPassword=p%40ss&host=x", "ldap://host?bindPassword=****&host=x")]
    [InlineData("sqs://q?accessKey=AKIA123&sessionToken=FQoGtemp&region=eu", "sqs://q?accessKey=****&sessionToken=****&region=eu")]
    [InlineData("kafka://t?saslPassword=x&sslKeyPassword=y&brokers=b", "kafka://t?saslPassword=****&sslKeyPassword=****&brokers=b")]
    [InlineData("http://api/x?authToken=bearerXYZ", "http://api/x?authToken=****")]
    [InlineData("sb://ns?connectionString=Endpoint%3Dsb%3B%3BSharedAccessKey%3Dabc", "sb://ns?connectionString=****")]
    public void Sanitize_QuerySecrets_AreFullyRedacted(string uri, string expected)
    {
        EndpointUri.Sanitize(uri).Should().Be(expected);
    }

    [Theory]
    [InlineData("ldap://cn=admin:s3cr3t@ldap.example.com:389/dc=x", "ldap://cn=admin:****@ldap.example.com:389/dc=x")]
    [InlineData("redis://:p4ss@cache:6379", "redis://:****@cache:6379")]
    [InlineData("amqp://user:pw@broker:5672/vhost", "amqp://user:****@broker:5672/vhost")]
    public void Sanitize_UserInfoPassword_IsRedacted(string uri, string expected)
    {
        EndpointUri.Sanitize(uri).Should().Be(expected);
    }

    [Theory]
    // host:port and an '@' inside the path (not userinfo) must NOT be touched
    [InlineData("http://host:8080/path")]
    [InlineData("http://host:8080/a@b")]
    [InlineData("rabbitmq://ex/q?routingKey=orders.new&partitionKey=7&clientId=abc&username=admin")]
    public void Sanitize_BenignParams_AreNotOverMasked(string uri)
    {
        EndpointUri.Sanitize(uri).Should().Be(uri);
        EndpointUri.Sanitize(uri).Should().NotContain("****");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Sanitize_EmptyOrNull_DoesNotThrow(string? uri)
    {
        EndpointUri.Sanitize(uri).Should().Be(string.Empty);
    }

    [Fact]
    public void Sanitize_NeverLeaksTheSecretValue()
    {
        const string secret = "SUPERSECRET_pw_9000";
        var sanitized = EndpointUri.Sanitize(
            $"amqp://svc:{secret}@broker:5672/v?password={secret}&sslKeyPassword={secret}");
        sanitized.Should().NotContain(secret);
        sanitized.Should().Contain("****");
    }

    // --- EndpointUri.ToMaskedUriString — used by connector {Uri} logs ------

    [Fact]
    public void ToMaskedUriString_FullyRedacts_NoPartialFirstTwoChars()
    {
        var uri = EndpointUriParser.Parse("rabbitmq://q?password=abcdef123");
        var masked = uri.ToMaskedUriString();

        masked.Should().Contain("password=****");
        masked.Should().NotContain("ab****");   // no first-2-char disclosure
        masked.Should().NotContain("abcdef123");
    }

    [Fact]
    public void ToMaskedUriString_MasksBindPassword_TheLdapLeak()
    {
        var uri = EndpointUriParser.Parse("ldap://host:389?bindDn=cn=admin&bindPassword=svcP4ss");
        var masked = uri.ToMaskedUriString();

        masked.Should().NotContain("svcP4ss");
        masked.Should().Contain("****");
    }

    [Fact]
    public void ToMaskedUriString_MasksUserInfoPassword()
    {
        var uri = EndpointUriParser.Parse("ldap://cn=admin:s3cr3t@host:389/dc=x");
        var masked = uri.ToMaskedUriString();

        masked.Should().NotContain("s3cr3t");
        masked.Should().Contain("****");
    }

    // --- RedactSecrets — free-text / connection-string / exception messages ---

    [Theory]
    [InlineData("Server=db;Password=p4ss;Database=x", "Server=db;Password=****;Database=x")]
    [InlineData("Server=db;Pwd=p4ss;", "Server=db;Pwd=****;")]
    [InlineData("Endpoint=sb://ns;SharedAccessKey=abc123", "Endpoint=sb://ns;SharedAccessKey=****")]
    [InlineData("Login failed for host=db user=admin", "Login failed for host=db user=admin")]
    [InlineData("no assignments here", "no assignments here")]
    public void RedactSecrets_MasksOnlySensitiveAssignments(string input, string expected)
    {
        EndpointUri.RedactSecrets(input).Should().Be(expected);
    }

    [Fact]
    public void RedactSecrets_KeepsNonSecretKeysAndSurroundingText()
    {
        var redacted = EndpointUri.RedactSecrets(
            "A network error occurred: Server=tcp:db,1433;User ID=svc;Password=Sup3rS3cret;Encrypt=True");

        redacted.Should().NotContain("Sup3rS3cret");
        redacted.Should().Contain("Server=tcp:db,1433");
        redacted.Should().Contain("User ID=svc");
        redacted.Should().Contain("Encrypt=True");
        redacted.Should().Contain("Password=****");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RedactSecrets_EmptyOrNull_DoesNotThrow(string? text)
    {
        EndpointUri.RedactSecrets(text).Should().Be(string.Empty);
    }

    // --- IsSensitiveKey ----------------------------------------------------

    [Theory]
    [InlineData("password")]
    [InlineData("bindPassword")]
    [InlineData("sslKeyPassword")]
    [InlineData("proxyPassword")]
    [InlineData("clientCertPassword")]
    [InlineData("passphrase")]
    [InlineData("privateKeyPassphrase")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("sessionToken")]
    [InlineData("authToken")]
    [InlineData("apiKey")]
    [InlineData("accessKey")]
    [InlineData("sharedAccessKey")]
    [InlineData("connectionString")]
    [InlineData("credentialPath")]
    [InlineData("saslJaasConfig")]
    public void IsSensitiveKey_KnownSecretNames_ReturnTrue(string key)
    {
        EndpointUri.IsSensitiveKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("username")]       // Camel keeps username visible
    [InlineData("routingKey")]     // must not trip bare "key"
    [InlineData("partitionKey")]
    [InlineData("clientId")]
    [InlineData("groupId")]
    [InlineData("brokers")]
    [InlineData("topic")]
    [InlineData("concurrentConsumers")]
    public void IsSensitiveKey_BenignNames_ReturnFalse(string key)
    {
        EndpointUri.IsSensitiveKey(key).Should().BeFalse();
    }

    [Fact]
    public void AddSensitiveKeys_RegistersCustomName()
    {
        EndpointUri.IsSensitiveKey("zzzConnectorPrivateHandle").Should().BeFalse();
        EndpointUri.AddSensitiveKeys("zzzConnectorPrivateHandle");
        EndpointUri.IsSensitiveKey("zzzConnectorPrivateHandle").Should().BeTrue();

        EndpointUri.Sanitize("x://y?zzzConnectorPrivateHandle=topsecret")
            .Should().Be("x://y?zzzConnectorPrivateHandle=****");
    }

    // --- [Sensitive] declaration drives masking ---------------------------

    /// <summary>
    /// Options type whose credential is named so that the name-keyword heuristic can NOT catch it —
    /// only the <see cref="SensitiveAttribute"/> declaration can. Mirrors the real-world case that
    /// caused the original leak (<c>bindPassword</c> was a secret nobody had put on the list).
    /// </summary>
    private sealed class DeclaredSecretOptions : EndpointOptions
    {
        public string? Wibble { get; set; }

        [Sensitive]
        public string? Wobble { get; set; }

        public override void Validate() { }
    }

    [Fact]
    public void SensitiveAttribute_IsHarvestedOnBind_AndDrivesMasking()
    {
        // "wobble" matches none of the built-in keywords — before binding it is not sensitive.
        EndpointUri.IsSensitiveKey("wobble").Should().BeFalse();

        var uri = EndpointUriParser.Parse("demo://x?wibble=visible&wobble=T0PSECRET");
        new DeclaredSecretOptions().BindFromUri(uri.RawParameters);

        // Binding the options type harvested its [Sensitive] declarations.
        EndpointUri.IsSensitiveKey("wobble").Should().BeTrue();

        var sanitized = EndpointUri.Sanitize("demo://x?wibble=visible&wobble=T0PSECRET");
        sanitized.Should().Be("demo://x?wibble=visible&wobble=****");
        sanitized.Should().NotContain("T0PSECRET");
    }

    // --- Safety invariant: routing keys stay RAW --------------------------

    [Fact]
    public void NormalizedKey_KeepsRawValues_ForRoutingIdentity()
    {
        // Masking is display-only. The dictionary/cache key that drives routing
        // must remain byte-identical to the raw URI, or endpoints stop matching.
        var parsed = EndpointUriParser.Parse("kafka://orders?password=raw_secret&groupId=g");
        parsed.NormalizedKey.Should().Contain("raw_secret");
        parsed.RawParameters["password"].Should().Be("raw_secret");
    }
}

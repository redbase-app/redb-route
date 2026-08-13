using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Amqp;

/// <summary>
/// Fluent API for AMQP 1.0 endpoints.
/// <example><code>
/// .From(Amqp.Address("orders").Host("broker1").User("admin").Password("secret"))
/// .To(Amqp.Address("notifications").Host("broker1").MessageDurable())
/// </code></example>
/// </summary>
public static class Amqp
{
    /// <summary>Creates an AMQP endpoint for the given address (terminus name).</summary>
    public static AmqpBuilder Address(string name) => new(name);
}

/// <summary>
/// Fluent builder for AMQP 1.0 endpoint URIs.
/// </summary>
public sealed class AmqpBuilder
{
    private readonly string _address;

    // Connection
    private string? _connectionFactory;
    private string? _host;
    private int? _port;
    private string? _user;
    private string? _password;
    private string? _containerId;
    private string? _virtualHost;
    private bool _ssl;

    // Link
    private uint? _durable;
    private string? _expiryPolicy;
    private uint? _terminusTimeout;
    private string? _distributionMode;
    private bool _dynamic;
    private string? _filterSelector;
    private string? _capabilities;
    private int? _senderSettleMode;
    private int? _receiverSettleMode;

    // Consumer
    private int? _credit;
    private bool? _autoAccept;
    private int? _concurrentConsumers;
    private int? _receiveTimeout;

    // Producer
    private bool? _messageDurable;
    private string? _messagePriority;
    private string? _messageTtl;
    private string? _contentType;
    private string? _subject;
    private string? _groupId;
    private bool _replyTo;
    private int? _timeout;
    private bool _transacted;
    private bool _declare;
    private string? _routingType;

    internal AmqpBuilder(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _address = address;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Use a named connection factory registered in DI.</summary>
    public AmqpBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Broker hostname. Default "localhost".</summary>
    public AmqpBuilder Host(string host) { _host = host; return this; }

    /// <summary>Broker port. Default 5672.</summary>
    public AmqpBuilder Port(int port) { _port = port; return this; }

    /// <summary>Username for authentication.</summary>
    public AmqpBuilder User(string user) { _user = user; return this; }

    /// <summary>Password for authentication.</summary>
    public AmqpBuilder Password(string password) { _password = password; return this; }

    /// <summary>AMQP container ID.</summary>
    public AmqpBuilder ContainerId(string id) { _containerId = id; return this; }

    /// <summary>Virtual host.</summary>
    public AmqpBuilder VirtualHost(string vhost) { _virtualHost = vhost; return this; }

    /// <summary>Enable SSL/TLS.</summary>
    public AmqpBuilder Ssl() { _ssl = true; return this; }

    // ── Link ──────────────────────────────────────────────────────────

    /// <summary>Terminus durability mode: 0=None, 1=Configuration, 2=UnsettledState.</summary>
    public AmqpBuilder Durable(uint mode) { _durable = mode; return this; }

    /// <summary>Expiry policy: "link-detach", "session-end", "connection-close", "never".</summary>
    public AmqpBuilder ExpiryPolicy(string policy) { _expiryPolicy = policy; return this; }

    /// <summary>Terminus timeout in seconds.</summary>
    public AmqpBuilder TerminusTimeout(uint seconds) { _terminusTimeout = seconds; return this; }

    /// <summary>Distribution mode: "move" (competing consumers) or "copy" (pub/sub).</summary>
    public AmqpBuilder DistributionMode(string mode) { _distributionMode = mode; return this; }

    /// <summary>Request a dynamically created terminus.</summary>
    public AmqpBuilder Dynamic() { _dynamic = true; return this; }

    /// <summary>JMS-style selector filter.</summary>
    public AmqpBuilder FilterSelector(string selector) { _filterSelector = selector; return this; }

    /// <summary>Terminus capabilities (comma-separated).</summary>
    public AmqpBuilder Capabilities(string caps) { _capabilities = caps; return this; }

    /// <summary>Sender settle mode: 0=Unsettled, 1=Settled, 2=Mixed.</summary>
    public AmqpBuilder SenderSettleMode(int mode) { _senderSettleMode = mode; return this; }

    /// <summary>Receiver settle mode: 0=First, 1=Second.</summary>
    public AmqpBuilder ReceiverSettleMode(int mode) { _receiverSettleMode = mode; return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Link credit (prefetch). Default 100.</summary>
    public AmqpBuilder Credit(int credit) { _credit = credit; return this; }

    /// <summary>Auto-accept received messages. Default true.</summary>
    public AmqpBuilder AutoAccept(bool accept = true) { _autoAccept = accept; return this; }

    /// <summary>Number of concurrent consumers. Default 1.</summary>
    public AmqpBuilder ConcurrentConsumers(int count) { _concurrentConsumers = count; return this; }

    /// <summary>Receive timeout in seconds. Default 60.</summary>
    public AmqpBuilder ReceiveTimeout(int seconds) { _receiveTimeout = seconds; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Mark sent messages as durable. Default true.</summary>
    public AmqpBuilder MessageDurable(bool durable = true) { _messageDurable = durable; return this; }

    /// <summary>Message priority. Default 4.</summary>
    public AmqpBuilder MessagePriority(byte priority) { _messagePriority = priority.ToString(); return this; }
    /// <summary>Message priority from an expression.</summary>
    public AmqpBuilder MessagePriority(IExpression priority) { _messagePriority = priority.ToTemplateString(); return this; }

    /// <summary>Message TTL in milliseconds. Default 0 (no expiry).</summary>
    public AmqpBuilder MessageTtl(uint ms) { _messageTtl = ms.ToString(); return this; }
    /// <summary>Message TTL from an expression.</summary>
    public AmqpBuilder MessageTtl(IExpression ms) { _messageTtl = ms.ToTemplateString(); return this; }

    /// <summary>Content type. Default "text/plain".</summary>
    public AmqpBuilder ContentType(string type) { _contentType = type; return this; }
    /// <summary>Content type from an expression.</summary>
    public AmqpBuilder ContentType(IExpression type) { _contentType = type.ToTemplateString(); return this; }

    /// <summary>Message subject.</summary>
    public AmqpBuilder Subject(string subject) { _subject = subject; return this; }
    /// <summary>Message subject from an expression.</summary>
    public AmqpBuilder Subject(IExpression subject) { _subject = subject.ToTemplateString(); return this; }

    /// <summary>Message group ID.</summary>
    public AmqpBuilder GroupId(string id) { _groupId = id; return this; }
    /// <summary>Message group ID from an expression.</summary>
    public AmqpBuilder GroupId(IExpression id) { _groupId = id.ToTemplateString(); return this; }

    /// <summary>Enable request-reply with dynamic reply-to address.</summary>
    public AmqpBuilder ReplyTo() { _replyTo = true; return this; }

    /// <summary>Operation timeout in seconds. Default 30.</summary>
    public AmqpBuilder Timeout(int seconds) { _timeout = seconds; return this; }

    /// <summary>Enable transacted sessions.</summary>
    public AmqpBuilder Transacted() { _transacted = true; return this; }

    /// <summary>Declare the address on the broker if it does not exist.</summary>
    public AmqpBuilder Declare() { _declare = true; return this; }

    /// <summary>Routing type for declaration: "ANYCAST" or "MULTICAST".</summary>
    public AmqpBuilder RoutingType(string type) { _routingType = type; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the AMQP endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("amqp:");
        sb.Append(_address);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }
        void AppendInt(string key, int? value) { if (value.HasValue) Append(key, value.Value.ToString()); }
        void AppendUint(string key, uint? value) { if (value.HasValue) Append(key, value.Value.ToString()); }

        // Route int-or-expression params to the correct property
        void AppendIntOrExpression(string key, string? value)
        {
            if (value == null) return;
            if (value.Contains("${"))
                Append(key + "Expression", value);
            else
                Append(key, value);
        }

        // Connection
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("host", _host);
        AppendInt("port", _port);
        AppendIf("user", _user);
        AppendIf("password", _password);
        AppendIf("containerId", _containerId);
        AppendIf("virtualHost", _virtualHost);
        AppendBool("ssl", _ssl);

        // Link
        AppendUint("durable", _durable);
        AppendIf("expiryPolicy", _expiryPolicy);
        AppendUint("terminusTimeout", _terminusTimeout);
        AppendIf("distributionMode", _distributionMode);
        AppendBool("dynamic", _dynamic);
        AppendIf("filterSelector", _filterSelector);
        AppendIf("capabilities", _capabilities);
        AppendInt("senderSettleMode", _senderSettleMode);
        AppendInt("receiverSettleMode", _receiverSettleMode);

        // Consumer
        AppendInt("credit", _credit);
        AppendBoolExplicit("autoAccept", _autoAccept);
        AppendInt("concurrentConsumers", _concurrentConsumers);
        AppendInt("receiveTimeout", _receiveTimeout);

        // Producer
        AppendBoolExplicit("messageDurable", _messageDurable);
        AppendIntOrExpression("messagePriority", _messagePriority);
        AppendIntOrExpression("messageTtl", _messageTtl);
        AppendIf("contentType", _contentType);
        AppendIf("subject", _subject);
        AppendIf("groupId", _groupId);
        AppendBool("replyTo", _replyTo);
        AppendInt("timeout", _timeout);
        AppendBool("transacted", _transacted);
        AppendBool("declare", _declare);
        AppendIf("routingType", _routingType);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(AmqpBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}

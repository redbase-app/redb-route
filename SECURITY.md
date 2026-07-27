# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.0.x (preview) | :white_check_mark: |

## Reporting a Vulnerability

If you discover a security vulnerability in redb.Route or any of its transport packages, please report it responsibly:

1. **Do NOT** open a public GitHub issue
2. Email: **security@redbase.app**
3. Include:
   - Affected package(s) and version
   - Description of the vulnerability
   - Steps to reproduce or proof-of-concept code
   - Potential impact assessment

We will acknowledge receipt within 48 hours and provide a timeline for a fix.

## Scope

This policy applies to all packages in the redb.Route family:

- `redb.Route` (core engine)
- `redb.Route.Kafka`, `redb.Route.RabbitMQ`, `redb.Route.Redis`
- `redb.Route.Http`, `redb.Route.Grpc`, `redb.Route.Tcp`, `redb.Route.WebSocket`
- `redb.Route.Sql`, `redb.Route.File`, `redb.Route.Sftp`, `redb.Route.Ftp`
- `redb.Route.MqttNet`, `redb.Route.Amqp`, `redb.Route.AzureServiceBus`
- `redb.Route.Kafka`, `redb.Route.IbmMq`, `redb.Route.Ldap`
- `redb.Route.Mail`, `redb.Route.S3`, `redb.Route.Elasticsearch`
- `redb.Route.Firebase`, `redb.Route.SignalR`
- `redb.Route.Controllers`, `redb.Route.Core`, `redb.Route.Validation.Adapters`

## Security Notes for Users

### Credentials in route definitions

Route endpoint URIs and fluent builder options frequently contain credentials (passwords, API keys, connection strings). **Never hardcode credentials** in source code. Use:

```csharp
// Good — read from configuration
var brokerPass = configuration["Kafka:Password"];

From(Kafka.Topic("orders")
    .Brokers(configuration["Kafka:Brokers"])
    .Sasl("PLAIN", configuration["Kafka:User"], configuration["Kafka:Password"]))
    .To("direct://process");
```

The same rule holds for every transport: keep credentials in configuration or a secret store and
pass them through the typed builder — never inline them into an endpoint URI. Since 3.4.0 a
credential that does reach a URI is redacted everywhere it would otherwise surface (logs,
telemetry tags, health metadata, the Tsak dashboard) — see the Security section of `CHANGELOG.md`.

### TLS / certificate validation

All transports that support TLS (HTTP, gRPC, TCP, SFTP, SMTP, MQTT, AMQP, IBM MQ, LDAPS) default to validating server certificates. Do not disable certificate validation in production:

```csharp
// DANGEROUS — never in production
Http.Post("api.example.com/data")
    .IgnoreSslErrors()   // only for local dev/testing
```

### Expression injection

The `Expr("${...}")` string expression engine evaluates header and property values at runtime. Avoid constructing expressions from untrusted external input, as this could expose internal message state.

### Input validation

Use `.Validate()`, `.ValidateJsonSchema()`, or `.ValidateFluent()` on consumer routes that accept data from external systems (webhooks, queues, TCP) to reject malformed or malicious payloads early.

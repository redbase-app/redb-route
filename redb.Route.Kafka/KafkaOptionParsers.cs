using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace redb.Route.Kafka;

/// <summary>
/// Strict parsers for enum-valued Kafka options (волна A1 плана
/// KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN). Every parse either succeeds or throws an
/// <see cref="ArgumentException"/> naming the option and the valid values — a typo must never
/// fall back to a default. The worst case was <c>securityProtocol</c>: an unparsed value used to
/// be silently skipped, yielding a plaintext connection with no credentials; and the canonical
/// Kafka spellings (<c>SASL_SSL</c>, <c>SCRAM-SHA-256</c>) fell into the same hole, because
/// Enum.TryParse knows only the C# names. Separators are normalized away, so both families of
/// spellings work.
/// </summary>
internal static class KafkaOptionParsers
{
    /// <summary>Case-insensitive parse ignoring '_' and '-', so C# names and Kafka canon both work.</summary>
    internal static TEnum ParseOrThrow<TEnum>(string value, string optionName) where TEnum : struct, Enum
    {
        var normalized = value.Trim().Replace("_", "").Replace("-", "");
        if (normalized.Length > 0 && Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
            return parsed;

        throw new ArgumentException(
            $"Unknown value '{value}' for '{optionName}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())} " +
            "(case-insensitive; '_' and '-' separators are accepted).");
    }

    /// <summary>Acks with the numeric synonyms Kafka users write (0 / 1 / -1).</summary>
    internal static Acks ParseAcks(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" or "0" => Acks.None,
        "leader" or "1" => Acks.Leader,
        "all" or "-1" => Acks.All,
        _ => throw new ArgumentException(
            $"Unknown value '{value}' for 'acks'. Valid values: None|0, Leader|1, All|-1."),
    };

    /// <summary>Consumer seek position; anything but beginning/end is a config error.</summary>
    internal static Offset ParseSeekTo(string value) => value.Trim().ToLowerInvariant() switch
    {
        "beginning" => Offset.Beginning,
        "end" => Offset.End,
        _ => throw new ArgumentException(
            $"Unknown value '{value}' for 'seekTo'. Valid values: beginning, end."),
    };

    /// <summary>
    /// SASL credential rule: the password-carrying mechanisms must arrive with credentials —
    /// a mechanism without them used to produce an anonymous connection. Gssapi and OAuthBearer
    /// authenticate through other channels and are exempt.
    /// </summary>
    /// <summary>
    /// <c>sslEndpointIdentificationAlgorithm</c>: <c>https</c> verifies the broker's hostname against its certificate,
    /// <c>none</c> (or empty) does not. Anything else used to mean <c>https</c>, so <c>none</c> itself turned the check
    /// on; now it is refused by name.
    /// </summary>
    internal static SslEndpointIdentificationAlgorithm ParseEndpointIdentification(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "https" => SslEndpointIdentificationAlgorithm.Https,
            "none" or "" => SslEndpointIdentificationAlgorithm.None,
            _ => throw new ArgumentException(
                $"sslEndpointIdentificationAlgorithm='{value}' is not a value: use 'https' (verify the broker's " +
                "hostname against its certificate) or 'none'."),
        };

    /// <summary>
    /// SASL PLAIN over <c>SASL_PLAINTEXT</c> sends the password as it is, unencrypted. Legal (a closed network, a test
    /// stand), so it is said, not refused.
    /// </summary>
    internal static void WarnIfPasswordInClear(ClientConfig config, ILogger? logger, string clientName)
    {
        if (config.SecurityProtocol == SecurityProtocol.SaslPlaintext && config.SaslMechanism == SaslMechanism.Plain)
            logger?.LogWarning(
                "{Client}: SASL PLAIN over SASL_PLAINTEXT sends the password unencrypted. Use securityProtocol=SaslSsl, " +
                "or a SCRAM mechanism.", clientName);
    }

    /// <summary>
    /// Refuses the librdkafka properties that would break a guarantee the connector gives, whichever of the two
    /// additionalProperties (the endpoint's or the connection factory's, both applied last) carries them:
    /// <c>transactional.id</c> (transactional mode with nobody opening transactions), <c>enable.auto.commit=true</c>
    /// (a background timer commits records read but not yet processed, under ackMode), and, for an idempotent or
    /// transactional producer (<c>transacted=true</c>, <c>transactionalIdPrefix</c>), <c>enable.idempotence</c> or
    /// <c>acks</c> contradicting it. A value that agrees with the connector is left alone.
    /// </summary>
    internal static void RefuseReservedProperties(
        IEnumerable<KeyValuePair<string, string>> properties, string source, bool idempotentProducer)
    {
        foreach (var (key, raw) in properties)
        {
            var name = key.Trim().ToLowerInvariant();
            var value = (raw ?? "").Trim().ToLowerInvariant();

            if (name == "transactional.id")
                throw new ArgumentException(
                    $"'transactional.id' in {source} puts librdkafka into transactional mode without the connector " +
                    "opening transactions, and every send fails with 'Erroneous state'. Use transactionalIdPrefix.");
            if (name == "enable.auto.commit" && value != "false")
                throw new ArgumentException(
                    $"'enable.auto.commit={raw}' in {source} lets librdkafka commit, on a timer, records read but not " +
                    "yet processed: a crash loses them, whatever ackMode says. The connector commits itself; choose " +
                    "when with ackMode (manual or auto).");
            if (idempotentProducer && name == "enable.idempotence" && value != "true")
                throw new ArgumentException(
                    $"'enable.idempotence={raw}' in {source} contradicts transacted=true / transactionalIdPrefix, " +
                    "which make the producer idempotent.");
            if (idempotentProducer && name == "acks" && value is not ("all" or "-1"))
                throw new ArgumentException(
                    $"'acks={raw}' in {source} contradicts transacted=true / transactionalIdPrefix, which require " +
                    "acks=all.");
        }
    }

    internal static void RequireSaslCredentials(SaslMechanism mechanism, string? username, string? password)
    {
        if (mechanism is SaslMechanism.Gssapi or SaslMechanism.OAuthBearer) return;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException(
                $"saslMechanism={mechanism} requires 'saslUsername' and 'saslPassword' " +
                "(inline or via a named connectionFactory).");
    }
}

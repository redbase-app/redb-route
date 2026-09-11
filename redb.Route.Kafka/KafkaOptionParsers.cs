using Confluent.Kafka;

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
    internal static void RequireSaslCredentials(SaslMechanism mechanism, string? username, string? password)
    {
        if (mechanism is SaslMechanism.Gssapi or SaslMechanism.OAuthBearer) return;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new ArgumentException(
                $"saslMechanism={mechanism} requires 'saslUsername' and 'saslPassword' " +
                "(inline or via a named connectionFactory).");
    }
}

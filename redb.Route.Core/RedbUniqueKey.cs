using System.Security.Cryptography;
using System.Text;

namespace redb.Route.RedbCore;

/// <summary>
/// Normalizes an application-composed business key so it fits the redb object key column
/// <c>_objects._value_unique</c> (440 characters, enforced in C# by the core on every provider)
/// and MSSQL's <c>_objects._name</c> (nvarchar(450)). A short key passes through untouched and
/// stays readable; an over-long one keeps a readable prefix and gets a SHA-256 tail computed
/// over the FULL original, so distinct long keys stay distinct and the mapping is deterministic.
/// Shared by <see cref="Repositories.RedbIdempotentRepository"/> and the redb.Route.Llm stores
/// (client-supplied keys — conversation ids, message keys — have no length contract).
/// </summary>
public static class RedbUniqueKey
{
    /// <summary>The hard cap of <c>_objects._value_unique</c>.</summary>
    public const int MaxLength = 440;

    /// <summary>Deterministic 440-safe form of <paramref name="raw"/>.</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length <= MaxLength) return raw;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        var cut = MaxLength - hash.Length - 1;
        // Never split a surrogate pair: a prefix ending in a lone high surrogate is not
        // encodable to UTF-8 and the provider would reject the save of the whole row.
        if (char.IsHighSurrogate(raw[cut - 1]))
            cut--;
        return $"{raw[..cut]}#{hash}";
    }
}
